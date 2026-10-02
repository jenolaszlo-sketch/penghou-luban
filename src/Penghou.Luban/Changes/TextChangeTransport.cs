using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Penghou.Luban.Changes;

/// <summary>Outcome of encoding or decoding a canonical text-change document.</summary>
public enum TextChangeTransportStatus { Succeeded, InvalidInput, Unsupported, LimitExceeded, Cancelled }

/// <summary>An encoded canonical document. Payload access always returns an owned copy.</summary>
public sealed class EncodedTextChange
{
    private readonly byte[] _payload;

    internal EncodedTextChange(byte[] payload, string identity)
    {
        _payload = (byte[])payload.Clone();
        Identity = identity;
    }

    /// <summary>Domain-separated SHA-256 identity of the canonical document body.</summary>
    public string Identity { get; }

    /// <summary>Returns a copy of the complete canonical UTF-8 document.</summary>
    public byte[] ToArray() => (byte[])_payload.Clone();
}

/// <summary>Result of an encoding operation.</summary>
public sealed class TextChangeTransportEncodeResult
{
    internal TextChangeTransportEncodeResult(TextChangeTransportStatus status, EncodedTextChange? value)
    { Status = status; Value = value; }

    public TextChangeTransportStatus Status { get; }
    public EncodedTextChange? Value { get; }
}

/// <summary>A decoded diff and the exact limits recorded by its producer.</summary>
public sealed record DecodedTextDiff(TextDiffResult Result, TextChangeOptions Options, string Identity);

/// <summary>A decoded merge and the exact limits recorded by its producer.</summary>
public sealed record DecodedTextMerge(TextMergeResult Result, TextChangeOptions Options, string Identity);

/// <summary>Result of a decoding operation. Decoded records are untrusted transport data.</summary>
public sealed class TextChangeTransportDecodeResult<T>
{
    internal TextChangeTransportDecodeResult(TextChangeTransportStatus status, T? value)
    { Status = status; Value = value; }

    public TextChangeTransportStatus Status { get; }
    public T? Value { get; }
}

/// <summary>
/// Canonical, versioned JSON transport for pure text diff and merge results.
/// The codec performs no I/O and grants no authority. Decoded edits remain
/// untrusted until checked against the exact source text by a separate validator.
/// </summary>
public static class TextChangeTransport
{
    /// <summary>The currently supported structured transport version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Maximum complete encoded document size accepted by this profile.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    private const string Schema = "penghou.luban.text-change";
    private const string Domain = "penghou.luban.text-change.transport.v1\0";
    private static readonly byte[] DomainBytes = Encoding.UTF8.GetBytes(Domain);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };

    /// <summary>
    /// Encodes a computed diff with its actual frozen computation options.
    /// A result without valid recorded options cannot be serialized.
    /// </summary>
    public static TextChangeTransportEncodeResult EncodeDiff(TextDiffResult? result,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Encoded(TextChangeTransportStatus.Cancelled);
        if (result is null || result.Options is null || !result.Options.IsValid || !IsValidDiff(result))
            return Encoded(TextChangeTransportStatus.InvalidInput);
        try
        {
            var body = WriteDiffBody(result, result.Options, cancellationToken);
            var full = AddIdentity(body, cancellationToken);
            if (full.Length > MaxDocumentBytes) return Encoded(TextChangeTransportStatus.LimitExceeded);
            return new(TextChangeTransportStatus.Succeeded, new(full, IdentityForBody(body)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Encoded(TextChangeTransportStatus.Cancelled); }
        catch (LimitException) { return Encoded(TextChangeTransportStatus.LimitExceeded); }
        catch (EncoderFallbackException) { return Encoded(TextChangeTransportStatus.InvalidInput); }
        catch (OverflowException) { return Encoded(TextChangeTransportStatus.LimitExceeded); }
    }

    /// <summary>
    /// Encodes a computed merge with its actual frozen computation options.
    /// A result without valid recorded options cannot be serialized.
    /// </summary>
    public static TextChangeTransportEncodeResult EncodeMerge(TextMergeResult? result,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Encoded(TextChangeTransportStatus.Cancelled);
        if (result is null || result.Options is null || !result.Options.IsValid || !IsValidMerge(result))
            return Encoded(TextChangeTransportStatus.InvalidInput);
        try
        {
            var body = WriteMergeBody(result, result.Options, cancellationToken);
            var full = AddIdentity(body, cancellationToken);
            if (full.Length > MaxDocumentBytes) return Encoded(TextChangeTransportStatus.LimitExceeded);
            return new(TextChangeTransportStatus.Succeeded, new(full, IdentityForBody(body)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Encoded(TextChangeTransportStatus.Cancelled); }
        catch (LimitException) { return Encoded(TextChangeTransportStatus.LimitExceeded); }
        catch (EncoderFallbackException) { return Encoded(TextChangeTransportStatus.InvalidInput); }
        catch (OverflowException) { return Encoded(TextChangeTransportStatus.LimitExceeded); }
    }

    /// <summary>
    /// Decodes a canonical diff document. Unknown schemas or versions return
    /// Unsupported; malformed, noncanonical, duplicate-field, or hash-mismatched
    /// documents return InvalidInput. Limits are checked before JSON parsing.
    /// </summary>
    public static TextChangeTransportDecodeResult<DecodedTextDiff> DecodeDiff(
        ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.Cancelled);
        if (document.Length > MaxDocumentBytes) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.LimitExceeded);
        try
        {
            var ownedDocument = document.ToArray();
            using var json = JsonDocument.Parse(ownedDocument, new JsonDocumentOptions { MaxDepth = 12, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            cancellationToken.ThrowIfCancellationRequested();
            var root = json.RootElement;
            if (IsUnsupportedEnvelope(root, "diff")) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.Unsupported);
            var names = Fields(root, "schema", "version", "kind", "profile", "options", "status", "before", "after", "edits", "identity");
            var profile = String(names["profile"]);
            if (!string.Equals(profile, TextChangeProfile.Identity, StringComparison.Ordinal)) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.Unsupported);
            var options = ParseOptions(names["options"]);
            var status = ParseDiffStatus(String(names["status"]));
            var before = ParseSnapshot(names["before"]);
            var after = ParseSnapshot(names["after"]);
            var edits = ParseEdits(names["edits"], options.MaxEdits);
            var identity = String(names["identity"]);
            var result = new TextDiffResult(status, before, after, edits, options);
            if (!IsValidDiff(result)) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput);
            var body = WriteDiffBody(result, options, cancellationToken);
            var expected = IdentityForBody(body);
            if (!IsHash(identity) || !FixedEquals(identity, expected)) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput);
            var canonical = AddIdentity(body, cancellationToken);
            if (!ownedDocument.AsSpan().SequenceEqual(canonical)) return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput);
            return new(TextChangeTransportStatus.Succeeded, new(result, options, identity));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.Cancelled); }
        catch (LimitException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.LimitExceeded); }
        catch (JsonException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput); }
        catch (InvalidDataException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput); }
        catch (InvalidOperationException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput); }
        catch (EncoderFallbackException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput); }
        catch (OverflowException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.LimitExceeded); }
        catch (ArgumentException) { return Decoded<DecodedTextDiff>(TextChangeTransportStatus.InvalidInput); }
    }

    /// <summary>Decodes a canonical merge document under the same strict bounds.</summary>
    public static TextChangeTransportDecodeResult<DecodedTextMerge> DecodeMerge(
        ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.Cancelled);
        if (document.Length > MaxDocumentBytes) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.LimitExceeded);
        try
        {
            var ownedDocument = document.ToArray();
            using var json = JsonDocument.Parse(ownedDocument, new JsonDocumentOptions { MaxDepth = 12, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            cancellationToken.ThrowIfCancellationRequested();
            var root = json.RootElement;
            if (IsUnsupportedEnvelope(root, "merge")) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.Unsupported);
            var names = Fields(root, "schema", "version", "kind", "profile", "options", "status", "base", "ours", "theirs", "result", "value", "edits", "conflicts", "identity");
            var profile = String(names["profile"]);
            if (!string.Equals(profile, TextChangeProfile.Identity, StringComparison.Ordinal)) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.Unsupported);
            var options = ParseOptions(names["options"]);
            var status = ParseMergeStatus(String(names["status"]));
            var baseSnapshot = ParseSnapshot(names["base"]);
            var ours = ParseSnapshot(names["ours"]);
            var theirs = ParseSnapshot(names["theirs"]);
            var resultSnapshot = ParseSnapshot(names["result"]);
            var value = NullableString(names["value"]);
            var edits = ParseEdits(names["edits"], options.MaxEdits);
            var conflicts = ParseConflicts(names["conflicts"], options.MaxConflicts);
            var identity = String(names["identity"]);
            var result = new TextMergeResult(status, baseSnapshot, ours, theirs, resultSnapshot, value, edits, conflicts, options);
            if (!IsValidMerge(result)) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput);
            var body = WriteMergeBody(result, options, cancellationToken);
            var expected = IdentityForBody(body);
            if (!IsHash(identity) || !FixedEquals(identity, expected)) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput);
            var canonical = AddIdentity(body, cancellationToken);
            if (!ownedDocument.AsSpan().SequenceEqual(canonical)) return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput);
            return new(TextChangeTransportStatus.Succeeded, new(result, options, identity));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.Cancelled); }
        catch (LimitException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.LimitExceeded); }
        catch (JsonException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput); }
        catch (InvalidDataException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput); }
        catch (InvalidOperationException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput); }
        catch (EncoderFallbackException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput); }
        catch (OverflowException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.LimitExceeded); }
        catch (ArgumentException) { return Decoded<DecodedTextMerge>(TextChangeTransportStatus.InvalidInput); }
    }

    private static TextChangeTransportEncodeResult Encoded(TextChangeTransportStatus status) => new(status, null);
    private static TextChangeTransportDecodeResult<T> Decoded<T>(TextChangeTransportStatus status) => new(status, default);

    private static byte[] WriteDiffBody(TextDiffResult result, TextChangeOptions options, CancellationToken ct) => Write(writer =>
    {
        writer.WriteStartObject();
        WriteHeader(writer, "diff", options);
        writer.WriteString("status", DiffStatusName(result.Status));
        WriteSnapshot(writer, "before", result.Before);
        WriteSnapshot(writer, "after", result.After);
        writer.WriteStartArray("edits");
        foreach (var edit in result.Edits)
        {
            ct.ThrowIfCancellationRequested();
            writer.WriteStartObject(); writer.WriteNumber("start", edit.StartOffset);
            writer.WriteNumber("deleteLength", edit.DeleteLength); writer.WriteString("replacement", edit.Replacement); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    private static byte[] WriteMergeBody(TextMergeResult result, TextChangeOptions options, CancellationToken ct) => Write(writer =>
    {
        writer.WriteStartObject();
        WriteHeader(writer, "merge", options);
        writer.WriteString("status", MergeStatusName(result.Status));
        WriteSnapshot(writer, "base", result.Base);
        WriteSnapshot(writer, "ours", result.Ours);
        WriteSnapshot(writer, "theirs", result.Theirs);
        WriteSnapshot(writer, "result", result.Result);
        if (result.Value is null) writer.WriteNull("value"); else writer.WriteString("value", result.Value);
        WriteEdits(writer, result.Edits, ct);
        writer.WriteStartArray("conflicts");
        foreach (var conflict in result.Conflicts)
        {
            ct.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("baseStartLine", conflict.BaseStartLine); writer.WriteNumber("baseEndLine", conflict.BaseEndLine);
            writer.WriteNumber("baseStartOffset", conflict.BaseStartOffset); writer.WriteNumber("baseDeleteLength", conflict.BaseDeleteLength);
            writer.WriteString("baseText", conflict.BaseText); writer.WriteString("oursText", conflict.OursText);
            writer.WriteString("theirsText", conflict.TheirsText); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    private static void WriteHeader(Utf8JsonWriter writer, string kind, TextChangeOptions options)
    {
        writer.WriteString("schema", Schema); writer.WriteNumber("version", CurrentVersion);
        writer.WriteString("kind", kind); writer.WriteString("profile", TextChangeProfile.Identity);
        writer.WritePropertyName("options"); writer.WriteStartObject();
        writer.WriteNumber("maxInputBytes", options.MaxInputBytes); writer.WriteNumber("maxLines", options.MaxLines);
        writer.WriteNumber("maxWorkCells", options.MaxWorkCells); writer.WriteNumber("maxMatrixBytes", options.MaxMatrixBytes);
        writer.WriteNumber("maxEdits", options.MaxEdits); writer.WriteNumber("maxOutputBytes", options.MaxOutputBytes);
        writer.WriteNumber("maxConflicts", options.MaxConflicts); writer.WriteEndObject();
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, string name, TextContentSnapshot? snapshot)
    {
        if (snapshot is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name); writer.WriteString("sha256", snapshot.Sha256);
        writer.WriteNumber("byteLength", snapshot.ByteLength); writer.WriteEndObject();
    }

    private static void WriteEdits(Utf8JsonWriter writer, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        writer.WriteStartArray("edits");
        foreach (var edit in edits)
        {
            ct.ThrowIfCancellationRequested();
            writer.WriteStartObject(); writer.WriteNumber("start", edit.StartOffset);
            writer.WriteNumber("deleteLength", edit.DeleteLength); writer.WriteString("replacement", edit.Replacement); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static byte[] AddIdentity(byte[] body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(body);
        var buffer = new ArrayBufferWriter<byte>(Math.Min(MaxDocumentBytes, body.Length + 90));
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            var root = document.RootElement;
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                property.WriteTo(writer);
            }
            writer.WriteString("identity", IdentityForBody(body));
            writer.WriteEndObject();
        }
        ct.ThrowIfCancellationRequested();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Write(Action<Utf8JsonWriter> emit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) emit(writer);
        if (buffer.WrittenCount > MaxDocumentBytes) throw new LimitException();
        return buffer.WrittenSpan.ToArray();
    }

    private static string IdentityForBody(ReadOnlySpan<byte> body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(DomainBytes); hash.AppendData(body);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var set = new HashSet<string>(expected, StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!set.Contains(property.Name) || !values.TryAdd(property.Name, property.Value)) throw new InvalidDataException();
        }
        if (values.Count != expected.Length) throw new InvalidDataException();
        return values;
    }

    private static bool IsUnsupportedEnvelope(JsonElement root, string expectedKind)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        JsonElement schema = default, version = default, kind = default;
        var hasSchema = false; var hasVersion = false; var hasKind = false;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "schema": if (hasSchema) throw new InvalidDataException(); schema = property.Value; hasSchema = true; break;
                case "version": if (hasVersion) throw new InvalidDataException(); version = property.Value; hasVersion = true; break;
                case "kind": if (hasKind) throw new InvalidDataException(); kind = property.Value; hasKind = true; break;
            }
        }
        if (!hasSchema || !hasVersion || !hasKind) throw new InvalidDataException();
        return String(schema) != Schema || String(kind) != expectedKind || Integer(version) != CurrentVersion;
    }

    private static TextChangeOptions ParseOptions(JsonElement element)
    {
        var fields = Fields(element, "maxInputBytes", "maxLines", "maxWorkCells", "maxMatrixBytes", "maxEdits", "maxOutputBytes", "maxConflicts");
        var options = new TextChangeOptions
        {
            MaxInputBytes = Integer(fields["maxInputBytes"]), MaxLines = Integer(fields["maxLines"]),
            MaxWorkCells = Integer(fields["maxWorkCells"]), MaxMatrixBytes = Integer(fields["maxMatrixBytes"]),
            MaxEdits = Integer(fields["maxEdits"]), MaxOutputBytes = Integer(fields["maxOutputBytes"]),
            MaxConflicts = Integer(fields["maxConflicts"])
        };
        if (!options.IsValid) throw new InvalidDataException();
        return options;
    }

    private static TextContentSnapshot? ParseSnapshot(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        var fields = Fields(element, "sha256", "byteLength");
        var hash = String(fields["sha256"]); var length = Integer(fields["byteLength"]);
        if (!IsHash(hash) || length < 0 || length > TextChangeOptions.HardMaxInputBytes + TextChangeOptions.HardMaxOutputBytes)
            throw new InvalidDataException();
        return new(hash, length);
    }

    private static List<TextEdit> ParseEdits(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var edits = new List<TextEdit>();
        foreach (var item in element.EnumerateArray())
        {
            if (edits.Count >= maximum) throw new LimitException();
            var fields = Fields(item, "start", "deleteLength", "replacement");
            edits.Add(new(Integer(fields["start"]), Integer(fields["deleteLength"]), String(fields["replacement"])));
        }
        return edits;
    }

    private static List<TextMergeConflict> ParseConflicts(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var conflicts = new List<TextMergeConflict>();
        foreach (var item in element.EnumerateArray())
        {
            if (conflicts.Count >= maximum) throw new LimitException();
            var fields = Fields(item, "baseStartLine", "baseEndLine", "baseStartOffset", "baseDeleteLength", "baseText", "oursText", "theirsText");
            conflicts.Add(new(Integer(fields["baseStartLine"]), Integer(fields["baseEndLine"]),
                Integer(fields["baseStartOffset"]), Integer(fields["baseDeleteLength"]), String(fields["baseText"]),
                String(fields["oursText"]), String(fields["theirsText"])));
        }
        return conflicts;
    }

    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? throw new InvalidDataException() : throw new InvalidDataException();
    private static string? NullableString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString() ?? throw new InvalidDataException(),
        _ => throw new InvalidDataException()
    };
    private static int Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number : throw new InvalidDataException();

    private static TextDiffStatus ParseDiffStatus(string status) => status switch
    {
        "succeeded" => TextDiffStatus.Succeeded, "invalidInput" => TextDiffStatus.InvalidInput,
        "limitExceeded" => TextDiffStatus.LimitExceeded, "cancelled" => TextDiffStatus.Cancelled,
        _ => throw new InvalidDataException()
    };
    private static TextMergeStatus ParseMergeStatus(string status) => status switch
    {
        "clean" => TextMergeStatus.Clean, "conflicted" => TextMergeStatus.Conflicted,
        "invalidInput" => TextMergeStatus.InvalidInput, "limitExceeded" => TextMergeStatus.LimitExceeded,
        "cancelled" => TextMergeStatus.Cancelled, _ => throw new InvalidDataException()
    };
    private static string DiffStatusName(TextDiffStatus status) => status switch
    {
        TextDiffStatus.Succeeded => "succeeded", TextDiffStatus.InvalidInput => "invalidInput",
        TextDiffStatus.LimitExceeded => "limitExceeded", TextDiffStatus.Cancelled => "cancelled",
        _ => throw new InvalidDataException()
    };
    private static string MergeStatusName(TextMergeStatus status) => status switch
    {
        TextMergeStatus.Clean => "clean", TextMergeStatus.Conflicted => "conflicted",
        TextMergeStatus.InvalidInput => "invalidInput", TextMergeStatus.LimitExceeded => "limitExceeded",
        TextMergeStatus.Cancelled => "cancelled", _ => throw new InvalidDataException()
    };

    private static bool IsValidDiff(TextDiffResult result)
    {
        if (result.Options is null || !result.Options.IsValid) return false;
        if (result.Status != TextDiffStatus.Succeeded)
            return result.Before is null && result.After is null && result.Edits.Count == 0;
        if (!ValidSnapshot(result.Before, result.Options.MaxInputBytes) || !ValidSnapshot(result.After, result.Options.MaxInputBytes) ||
            result.Edits.Count > result.Options.MaxEdits) return false;
        if (!ValidEdits(result.Edits, result.Before!.ByteLength, result.Options.MaxOutputBytes)) return false;
        long expectedAfterLength = result.Before.ByteLength;
        foreach (var edit in result.Edits)
        {
            if (edit.DeleteLength == 0 && edit.Replacement.Length == 0) return false;
            expectedAfterLength = checked(expectedAfterLength - edit.DeleteLength + StrictUtf8.GetByteCount(edit.Replacement));
        }
        if (expectedAfterLength != result.After!.ByteLength) return false;
        return result.Edits.Count != 0 ||
            (result.Before.ByteLength == result.After.ByteLength && FixedEquals(result.Before.Sha256, result.After.Sha256));
    }

    private static bool IsValidMerge(TextMergeResult result)
    {
        if (result.Options is null || !result.Options.IsValid) return false;
        if (result.Status is TextMergeStatus.InvalidInput or TextMergeStatus.LimitExceeded or TextMergeStatus.Cancelled)
            return result.Base is null && result.Ours is null && result.Theirs is null && result.Result is null &&
                result.Value is null && result.Edits.Count == 0 && result.Conflicts.Count == 0;
        if (!ValidSnapshot(result.Base, result.Options.MaxInputBytes) || !ValidSnapshot(result.Ours, result.Options.MaxInputBytes) ||
            !ValidSnapshot(result.Theirs, result.Options.MaxInputBytes)) return false;
        if (result.Status == TextMergeStatus.Clean)
        {
            if (result.Value is null || result.Result is null || result.Conflicts.Count != 0 || result.Edits.Count > result.Options.MaxEdits ||
                !ValidSnapshot(result.Result, result.Options.MaxOutputBytes)) return false;
            try
            {
                var bytes = StrictUtf8.GetBytes(result.Value);
                if (bytes.Length != result.Result.ByteLength || bytes.Length > result.Options.MaxOutputBytes || !HashMatches(bytes, result.Result.Sha256)) return false;
            }
            catch (EncoderFallbackException) { return false; }
            if (!ValidEdits(result.Edits, result.Base!.ByteLength, result.Options.MaxOutputBytes)) return false;
            long resultingBytes = result.Base.ByteLength;
            long replacementBytes = 0;
            foreach (var edit in result.Edits)
            {
                if (edit.DeleteLength == 0 && edit.Replacement.Length == 0) return false;
                try { replacementBytes = checked(replacementBytes + StrictUtf8.GetByteCount(edit.Replacement)); }
                catch (EncoderFallbackException) { return false; }
                resultingBytes = checked(resultingBytes - edit.DeleteLength + StrictUtf8.GetByteCount(edit.Replacement));
            }
            if (resultingBytes != result.Result.ByteLength ||
                checked(resultingBytes + replacementBytes) > result.Options.MaxOutputBytes) return false;
            return result.Edits.Count != 0 ||
                (result.Base.ByteLength == result.Result.ByteLength && FixedEquals(result.Base.Sha256, result.Result.Sha256));
        }
        if (result.Status != TextMergeStatus.Conflicted || result.Result is not null || result.Value is not null ||
            result.Edits.Count != 0 || result.Conflicts.Count == 0 || result.Conflicts.Count > result.Options.MaxConflicts) return false;
        long emitted = 0; var previousLine = -1; var previousOffset = -1; long previousEndOffset = -1;
        foreach (var conflict in result.Conflicts)
        {
            if (conflict.BaseStartLine < 0 || conflict.BaseEndLine < conflict.BaseStartLine ||
                conflict.BaseEndLine > result.Options.MaxLines ||
                conflict.BaseStartOffset < 0 || conflict.BaseDeleteLength < 0 ||
                (long)conflict.BaseStartOffset + conflict.BaseDeleteLength > result.Base!.ByteLength ||
                conflict.BaseStartLine < previousLine || conflict.BaseStartOffset <= previousOffset ||
                conflict.BaseStartOffset < previousEndOffset) return false;
            try
            {
                var baseBytes = StrictUtf8.GetByteCount(conflict.BaseText);
                if (baseBytes != conflict.BaseDeleteLength || CountTextLines(conflict.BaseText) != conflict.BaseEndLine - conflict.BaseStartLine) return false;
                emitted = checked(emitted + baseBytes + StrictUtf8.GetByteCount(conflict.OursText) + StrictUtf8.GetByteCount(conflict.TheirsText));
            }
            catch (EncoderFallbackException) { return false; }
            if (emitted > result.Options.MaxOutputBytes) return false;
            previousLine = conflict.BaseEndLine; previousOffset = conflict.BaseStartOffset;
            previousEndOffset = (long)conflict.BaseStartOffset + conflict.BaseDeleteLength;
        }
        return true;
    }

    private static bool ValidSnapshot(TextContentSnapshot? snapshot, int maximumLength) => snapshot is not null &&
        IsHash(snapshot.Sha256) && snapshot.ByteLength >= 0 && snapshot.ByteLength <= maximumLength;

    private static bool ValidEdits(IReadOnlyList<TextEdit> edits, int baseLength, int maxReplacementBytes)
    {
        long replacementBytes = 0; long previousEnd = -1; var previousStart = -1;
        foreach (var edit in edits)
        {
            if (edit is null || edit.StartOffset < 0 || edit.DeleteLength < 0 || edit.StartOffset <= previousStart ||
                (long)edit.StartOffset + edit.DeleteLength > baseLength || edit.StartOffset < previousEnd || edit.Replacement is null) return false;
            try { replacementBytes = checked(replacementBytes + StrictUtf8.GetByteCount(edit.Replacement)); }
            catch (EncoderFallbackException) { return false; }
            if (replacementBytes > maxReplacementBytes) return false;
            previousStart = edit.StartOffset; previousEnd = (long)edit.StartOffset + edit.DeleteLength;
        }
        return true;
    }

    private static int CountTextLines(string value)
    {
        if (value.Length == 0) return 0;
        var lines = 0; var hasTail = true;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\n') continue;
            lines++;
            hasTail = i + 1 < value.Length;
        }
        return lines + (hasTail ? 1 : 0);
    }

    private static bool IsHash(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var ch in value) if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static bool HashMatches(ReadOnlySpan<byte> bytes, string expected) =>
        FixedEquals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), expected);

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private sealed class LimitException : Exception;
}
