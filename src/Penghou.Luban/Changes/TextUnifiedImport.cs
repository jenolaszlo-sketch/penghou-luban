using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Penghou.IO.Abstractions;

namespace Penghou.Luban.Changes;

/// <summary>Result of parsing the closed modified-existing-file unified dialect.</summary>
public enum TextUnifiedImportStatus { Succeeded, Unsupported, InvalidInput, LimitExceeded, Cancelled }

/// <summary>Kind of one exact line token in an imported unified hunk.</summary>
public enum TextUnifiedLineKind { Context, Delete, Add }

/// <summary>Result of checking and materializing every file in an imported candidate.</summary>
public enum TextUnifiedMaterializationStatus { Succeeded, Stale, InvalidInput, LimitExceeded, Cancelled }

/// <summary>Independent parser and materialization bounds for the hostile unified-text profile.</summary>
public sealed record TextUnifiedImportOptions
{
    public const int HardMaxInputBytes = 1_048_576;
    public const int HardMaxLines = 65_536;
    public const int HardMaxFiles = 128;
    public const int HardMaxHunks = 8_192;
    public const int HardMaxPayloadBytes = 524_288;
    public const int HardMaxWorkUnits = 4_000_000;

    public static TextUnifiedImportOptions Default { get; } = new();

    public int MaxInputBytes { get; init; } = HardMaxInputBytes;
    public int MaxLines { get; init; } = HardMaxLines;
    public int MaxFiles { get; init; } = HardMaxFiles;
    public int MaxHunks { get; init; } = HardMaxHunks;
    public int MaxPayloadBytes { get; init; } = HardMaxPayloadBytes;
    public int MaxWorkUnits { get; init; } = HardMaxWorkUnits;

    internal bool IsValid => MaxInputBytes is >= 1 and <= HardMaxInputBytes &&
        MaxLines is >= 1 and <= HardMaxLines && MaxFiles is >= 1 and <= HardMaxFiles &&
        MaxHunks is >= 1 and <= HardMaxHunks && MaxPayloadBytes is >= 1 and <= HardMaxPayloadBytes &&
        MaxWorkUnits is >= 1 and <= HardMaxWorkUnits;
}

/// <summary>One immutable exact line token from an untrusted unified candidate.</summary>
public sealed class TextUnifiedLine
{
    internal TextUnifiedLine(TextUnifiedLineKind kind, string text, bool markedNoNewline)
    { Kind = kind; Text = text; MarkedNoNewline = markedNoNewline; }

    public TextUnifiedLineKind Kind { get; }
    /// <summary>Exact UTF-16 text for this strict UTF-8 line token, including LF/CRLF when present.</summary>
    public string Text { get; }
    /// <summary>True when the unified no-final-newline marker removed the framing LF.</summary>
    public bool MarkedNoNewline { get; }
}

/// <summary>One immutable unified hunk using its parsed one-based/zero-count ranges.</summary>
public sealed class TextUnifiedHunk
{
    internal TextUnifiedHunk(int oldStart, int oldCount, int newStart, int newCount, IReadOnlyList<TextUnifiedLine> lines)
    {
        OldStart = oldStart; OldCount = oldCount; NewStart = newStart; NewCount = newCount;
        Lines = Array.AsReadOnly(lines.Select(line => new TextUnifiedLine(line.Kind, line.Text, line.MarkedNoNewline)).ToArray());
    }

    public int OldStart { get; }
    public int OldCount { get; }
    public int NewStart { get; }
    public int NewCount { get; }
    public IReadOnlyList<TextUnifiedLine> Lines { get; }
}

/// <summary>One exact workspace-relative existing-file target in an imported candidate.</summary>
public sealed class TextUnifiedFileCandidate
{
    internal TextUnifiedFileCandidate(string relativePath, IReadOnlyList<TextUnifiedHunk> hunks)
    { RelativePath = relativePath; Hunks = Array.AsReadOnly(hunks.Select(hunk => new TextUnifiedHunk(hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount, hunk.Lines)).ToArray()); }

    public string RelativePath { get; }
    public IReadOnlyList<TextUnifiedHunk> Hunks { get; }
}

/// <summary>Untrusted immutable transport data; it has no source snapshots or authority.</summary>
public sealed class TextUnifiedPatchCandidate
{
    internal TextUnifiedPatchCandidate(IReadOnlyList<TextUnifiedFileCandidate> files, int sourceBytes, int sourceLines, int payloadBytes, int hunkCount)
    {
        Files = Array.AsReadOnly(files.Select(file => new TextUnifiedFileCandidate(file.RelativePath, file.Hunks)).ToArray());
        SourceBytes = sourceBytes; SourceLines = sourceLines; PayloadBytes = payloadBytes; HunkCount = hunkCount;
    }

    public string ProfileIdentity => TextUnifiedDiffProfile.Identity;
    public IReadOnlyList<TextUnifiedFileCandidate> Files { get; }
    internal int SourceBytes { get; }
    internal int SourceLines { get; }
    internal int PayloadBytes { get; }
    internal int HunkCount { get; }
}

/// <summary>Whole-input result of closed-dialect parsing. Failed results never expose a partial candidate.</summary>
public sealed class TextUnifiedImportResult
{
    internal TextUnifiedImportResult(TextUnifiedImportStatus status, TextUnifiedPatchCandidate? candidate, string? errorCode)
    { Status = status; Candidate = candidate; ErrorCode = errorCode; }

    public TextUnifiedImportStatus Status { get; }
    public TextUnifiedPatchCandidate? Candidate { get; }
    public string? ErrorCode { get; }
}

/// <summary>Exact materialization of one imported target against caller-supplied complete source text.</summary>
public sealed class TextUnifiedMaterializedFile
{
    internal TextUnifiedMaterializedFile(string path, string proposedText, TextContentSnapshot before,
        TextContentSnapshot after, IReadOnlyList<TextEdit> edits)
    {
        RelativePath = path; ProposedText = proposedText; Before = before; After = after;
        Edits = Array.AsReadOnly(edits.Select(edit => edit with { }).ToArray());
    }

    public string RelativePath { get; }
    public string ProposedText { get; }
    public TextContentSnapshot Before { get; }
    public TextContentSnapshot After { get; }
    public IReadOnlyList<TextEdit> Edits { get; }
}

/// <summary>All-or-nothing pure materialization result; stale/error results expose no file prefix.</summary>
public sealed class TextUnifiedMaterializationResult
{
    internal TextUnifiedMaterializationResult(TextUnifiedMaterializationStatus status, IReadOnlyList<TextUnifiedMaterializedFile>? files, string? errorCode)
    {
        Status = status;
        Files = Array.AsReadOnly(files?.Select(file => new TextUnifiedMaterializedFile(
            file.RelativePath, file.ProposedText, file.Before, file.After, file.Edits)).ToArray() ?? []);
        ErrorCode = errorCode;
    }

    public TextUnifiedMaterializationStatus Status { get; }
    public IReadOnlyList<TextUnifiedMaterializedFile> Files { get; }
    public string? ErrorCode { get; }
}

/// <summary>
/// Parses the closed LF-framed modified-existing-file dialect and materializes it
/// only against caller-owned complete source strings. This type performs no I/O.
/// </summary>
public static class TextUnifiedImport
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string NoNewlineMarker = "\\ No newline at end of file";

    /// <summary>Parses an untrusted transport string without resolving targets or reading resources.</summary>
    public static TextUnifiedImportResult Parse(string? unifiedDiff, TextUnifiedImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= TextUnifiedImportOptions.Default;
        if (unifiedDiff is null || !options.IsValid) return Failure(TextUnifiedImportStatus.InvalidInput, "InvalidInput");
        if (cancellationToken.IsCancellationRequested) return Failure(TextUnifiedImportStatus.Cancelled, "Cancelled");
        var budget = new WorkBudget(options.MaxWorkUnits, cancellationToken);
        try
        {
            var inputBytes = CountUtf8(unifiedDiff, options.MaxInputBytes, budget);
            if (inputBytes > options.MaxInputBytes) return Failure(TextUnifiedImportStatus.LimitExceeded, "InputBytes");
            var lines = SplitPhysicalLines(unifiedDiff, options, budget);
            if (lines is null) return Failure(TextUnifiedImportStatus.LimitExceeded, "Lines");
            if (lines.Count == 0) return new(TextUnifiedImportStatus.Succeeded, new TextUnifiedPatchCandidate(Array.Empty<TextUnifiedFileCandidate>(), 0, 0, 0, 0), null);
            foreach (var line in lines)
            {
                budget.Charge();
                if (!line.HasLf) return Failure(TextUnifiedImportStatus.InvalidInput, "UnterminatedTransportLine");
            }

            var files = new List<TextUnifiedFileCandidate>();
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cursor = 0;
            var hunkTotal = 0;
            var payloadBytes = 0;
            while (cursor < lines.Count)
            {
                budget.Charge();
                var header = lines[cursor++].Content;
                if (IsUnsupportedForm(header)) return Failure(TextUnifiedImportStatus.Unsupported, "UnsupportedForm");
                if (header == "--- /dev/null")
                    return Failure(TextUnifiedImportStatus.Unsupported, "CreateDeleteUnsupported");
                if (!header.StartsWith("--- a/", StringComparison.Ordinal))
                    return Failure(TextUnifiedImportStatus.InvalidInput, "ExpectedOldFileHeader");
                var path = ValidatePath(header[6..]);
                if (path is null) return Failure(TextUnifiedImportStatus.InvalidInput, "InvalidPath");
                if (cursor >= lines.Count) return Failure(TextUnifiedImportStatus.InvalidInput, "MissingNewFileHeader");
                var newHeader = lines[cursor++].Content;
                if (IsUnsupportedForm(newHeader)) return Failure(TextUnifiedImportStatus.Unsupported, "UnsupportedForm");
                if (newHeader == "+++ /dev/null")
                    return Failure(TextUnifiedImportStatus.Unsupported, "CreateDeleteUnsupported");
                if (!newHeader.StartsWith("+++ b/", StringComparison.Ordinal))
                    return Failure(TextUnifiedImportStatus.InvalidInput, "ExpectedNewFileHeader");
                var newPath = ValidatePath(newHeader[6..]);
                if (newPath is null) return Failure(TextUnifiedImportStatus.InvalidInput, "InvalidPath");
                if (!string.Equals(path, newPath, StringComparison.Ordinal)) return Failure(TextUnifiedImportStatus.Unsupported, "RenameUnsupported");
                if (!knownPaths.Add(path)) return Failure(TextUnifiedImportStatus.InvalidInput, "DuplicateTarget");
                if (files.Count >= options.MaxFiles) return Failure(TextUnifiedImportStatus.LimitExceeded, "Files");

                var hunks = new List<TextUnifiedHunk>();
                var previousOldEnd = 0;
                var previousNewEnd = 0;
                var previousOldStart = -1;
                var previousNewStart = -1;
                var previousOldCount = -1;
                var previousNewCount = -1;
                while (cursor < lines.Count && lines[cursor].Content.StartsWith("@@", StringComparison.Ordinal))
                {
                    budget.Charge();
                    if (IsUnsupportedForm(lines[cursor].Content)) return Failure(TextUnifiedImportStatus.Unsupported, "UnsupportedForm");
                    if (++hunkTotal > options.MaxHunks) return Failure(TextUnifiedImportStatus.LimitExceeded, "Hunks");
                    if (!TryParseHunkHeader(lines[cursor++].Content, options.MaxLines, out var oldStart, out var oldCount, out var newStart, out var newCount))
                        return Failure(TextUnifiedImportStatus.InvalidInput, "InvalidHunkRange");
                    var oldStart0 = StartZero(oldStart, oldCount);
                    var newStart0 = StartZero(newStart, newCount);
                    var oldEnd = checked(oldStart0 + oldCount);
                    var newEnd = checked(newStart0 + newCount);
                    if (oldCount == 0 && newCount == 0) return Failure(TextUnifiedImportStatus.InvalidInput, "EmptyHunk");
                    if (oldStart0 < previousOldEnd || newStart0 < previousNewEnd ||
                        (oldStart0 == previousOldStart && (oldCount == 0 || previousOldCount == 0)) ||
                        (newStart0 == previousNewStart && (newCount == 0 || previousNewCount == 0)))
                        return Failure(TextUnifiedImportStatus.InvalidInput, "OverlappingHunks");
                    if (newStart0 - previousNewEnd != oldStart0 - previousOldEnd)
                        return Failure(TextUnifiedImportStatus.InvalidInput, "InconsistentHunkRanges");

                    var hunkLines = new List<TextUnifiedLine>();
                    var oldUsed = 0;
                    var newUsed = 0;
                    while (oldUsed < oldCount || newUsed < newCount)
                    {
                        budget.Charge();
                        if (cursor >= lines.Count) return Failure(TextUnifiedImportStatus.InvalidInput, "TruncatedHunk");
                        var physical = lines[cursor++];
                        var record = physical.Content;
                        if (record == NoNewlineMarker) return Failure(TextUnifiedImportStatus.InvalidInput, "MisplacedNoNewlineMarker");
                        if (record.Length == 0 || record[0] is not (' ' or '+' or '-'))
                            return IsUnsupportedForm(record) ? Failure(TextUnifiedImportStatus.Unsupported, "UnsupportedForm") : Failure(TextUnifiedImportStatus.InvalidInput, "InvalidHunkLine");
                        var kind = record[0] switch { ' ' => TextUnifiedLineKind.Context, '+' => TextUnifiedLineKind.Add, _ => TextUnifiedLineKind.Delete };
                        if (kind is TextUnifiedLineKind.Context or TextUnifiedLineKind.Delete) oldUsed++;
                        if (kind is TextUnifiedLineKind.Context or TextUnifiedLineKind.Add) newUsed++;
                        if (oldUsed > oldCount || newUsed > newCount) return Failure(TextUnifiedImportStatus.InvalidInput, "HunkCountExceeded");
                        var token = record[1..] + "\n";
                        var tokenBytes = StrictUtf8.GetByteCount(token);
                        payloadBytes = checked(payloadBytes + tokenBytes);
                        if (payloadBytes > options.MaxPayloadBytes) return Failure(TextUnifiedImportStatus.LimitExceeded, "PayloadBytes");
                        budget.Charge(Math.Max(1, token.Length));
                        hunkLines.Add(new TextUnifiedLine(kind, token, markedNoNewline: false));
                        if (cursor < lines.Count && lines[cursor].Content == NoNewlineMarker)
                        {
                            var previous = hunkLines[^1];
                            if (!previous.Text.EndsWith('\n')) return Failure(TextUnifiedImportStatus.InvalidInput, "MisplacedNoNewlineMarker");
                            if (previous.Text.Length == 1) return Failure(TextUnifiedImportStatus.InvalidInput, "EmptyNoNewlineToken");
                            payloadBytes -= 1; // The LF is framing; any preceding CR remains content.
                            hunkLines[^1] = new TextUnifiedLine(previous.Kind, previous.Text[..^1], markedNoNewline: true);
                            cursor++;
                        }
                    }
                    hunks.Add(new TextUnifiedHunk(oldStart, oldCount, newStart, newCount, hunkLines));
                    previousOldEnd = oldEnd;
                    previousNewEnd = newEnd;
                    previousOldStart = oldStart0;
                    previousNewStart = newStart0;
                    previousOldCount = oldCount;
                    previousNewCount = newCount;
                }
                if (hunks.Count == 0)
                {
                    if (cursor < lines.Count && IsUnsupportedForm(lines[cursor].Content)) return Failure(TextUnifiedImportStatus.Unsupported, "UnsupportedForm");
                    return Failure(TextUnifiedImportStatus.InvalidInput, "MissingHunks");
                }
                files.Add(new TextUnifiedFileCandidate(path, hunks));
                if (cursor < lines.Count && !lines[cursor].Content.StartsWith("--- ", StringComparison.Ordinal))
                    return IsUnsupportedForm(lines[cursor].Content) ? Failure(TextUnifiedImportStatus.Unsupported, "UnsupportedForm") : Failure(TextUnifiedImportStatus.InvalidInput, "TrailingData");
            }

            return new(TextUnifiedImportStatus.Succeeded, new TextUnifiedPatchCandidate(files, inputBytes, lines.Count, payloadBytes, hunkTotal), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(TextUnifiedImportStatus.Cancelled, "Cancelled"); }
        catch (OverflowException) { return Failure(TextUnifiedImportStatus.InvalidInput, "NumericOverflow"); }
        catch (EncoderFallbackException) { return Failure(TextUnifiedImportStatus.InvalidInput, "InvalidUnicode"); }
        catch (ArgumentException) { return Failure(TextUnifiedImportStatus.InvalidInput, "InvalidInput"); }
        catch (LimitException) { return Failure(TextUnifiedImportStatus.LimitExceeded, "Work"); }
    }

    /// <summary>
    /// Checks every imported context/deletion against complete caller-owned source text,
    /// then derives exact snapshots and byte edits. It never reads or writes a resource.
    /// </summary>
    public static TextUnifiedMaterializationResult Materialize(TextUnifiedPatchCandidate? candidate,
        IReadOnlyDictionary<string, string>? originals, TextUnifiedImportOptions? importOptions = null,
        TextChangeOptions? textOptions = null, CancellationToken cancellationToken = default)
    {
        importOptions ??= TextUnifiedImportOptions.Default;
        textOptions ??= TextChangeOptions.Default;
        if (candidate is null || originals is null || !importOptions.IsValid || !textOptions.IsValid)
            return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidInput");
        if (cancellationToken.IsCancellationRequested)
            return MaterializationFailure(TextUnifiedMaterializationStatus.Cancelled, "Cancelled");
        if (candidate.SourceBytes > importOptions.MaxInputBytes || candidate.SourceLines > importOptions.MaxLines ||
            candidate.Files.Count > importOptions.MaxFiles || candidate.HunkCount > importOptions.MaxHunks || candidate.PayloadBytes > importOptions.MaxPayloadBytes)
            return MaterializationFailure(TextUnifiedMaterializationStatus.LimitExceeded, "CandidateLimits");
        if (originals.Count != candidate.Files.Count)
            return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "SourceSetMismatch");

        var budget = new WorkBudget(importOptions.MaxWorkUnits, cancellationToken);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var foldedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceCount = 0;
        try
        {
            foreach (var pair in originals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.Charge();
                if (++sourceCount > importOptions.MaxFiles)
                    return MaterializationFailure(TextUnifiedMaterializationStatus.LimitExceeded, "Files");
                if (pair.Key is null || pair.Value is null) return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidSourceMap");
                var validatedPath = WindowsWorkspacePath.Normalize(new WorkspacePath(pair.Key), allowRoot: false).Value;
                if (!string.Equals(pair.Key, validatedPath, StringComparison.Ordinal) || !foldedSources.Add(validatedPath) || !sources.TryAdd(validatedPath, pair.Value))
                    return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidSourceMap");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return MaterializationFailure(TextUnifiedMaterializationStatus.Cancelled, "Cancelled"); }
        catch (ArgumentException) { return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidSourceMap"); }

        var materialized = new List<TextUnifiedMaterializedFile>(candidate.Files.Count);
        var textWork = new TextWorkBudget(textOptions.MaxWorkCells, cancellationToken);
        try
        {
            foreach (var file in candidate.Files)
            {
                budget.Charge();
                if (!sources.TryGetValue(file.RelativePath, out var original))
                    return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "MissingSource");
                var originalText = TextChangeCore.Validate(original, textOptions, cancellationToken);
                var originalLines = SplitTextTokens(original);
                var output = new StringBuilder(original.Length);
                var proposedBytes = 0;
                var sourceLine = 0;
                var outputLine = 0;
                var outputClosed = false;
                foreach (var hunk in file.Hunks)
                {
                    budget.Charge();
                    var oldStart0 = StartZero(hunk.OldStart, hunk.OldCount);
                    var newStart0 = StartZero(hunk.NewStart, hunk.NewCount);
                    if (oldStart0 < sourceLine || oldStart0 > originalLines.Count || oldStart0 + hunk.OldCount > originalLines.Count)
                        return MaterializationFailure(TextUnifiedMaterializationStatus.Stale, "SourceRangeMismatch");
                    while (sourceLine < oldStart0)
                    {
                        budget.Charge();
                        if (outputClosed) return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NonFinalOutputMarker");
                        AppendToken(originalLines[sourceLine++]);
                        outputLine++;
                    }
                    if (newStart0 != outputLine)
                        return MaterializationFailure(TextUnifiedMaterializationStatus.Stale, "TargetRangeMismatch");
                    var oldUsed = 0;
                    var newUsed = 0;
                    foreach (var line in hunk.Lines)
                    {
                        budget.Charge();
                        if (line.MarkedNoNewline != !line.Text.EndsWith('\n'))
                            return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidNoNewlineMarker");
                        switch (line.Kind)
                        {
                            case TextUnifiedLineKind.Context:
                                if (sourceLine >= originalLines.Count || !string.Equals(originalLines[sourceLine], line.Text, StringComparison.Ordinal))
                                    return MaterializationFailure(TextUnifiedMaterializationStatus.Stale, "ContextMismatch");
                                if (line.MarkedNoNewline && sourceLine != originalLines.Count - 1)
                                    return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NonFinalSourceMarker");
                                if (outputClosed) return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NonFinalOutputMarker");
                                AppendToken(line.Text); sourceLine++; outputLine++; oldUsed++; newUsed++;
                                break;
                            case TextUnifiedLineKind.Delete:
                                if (sourceLine >= originalLines.Count || !string.Equals(originalLines[sourceLine], line.Text, StringComparison.Ordinal))
                                    return MaterializationFailure(TextUnifiedMaterializationStatus.Stale, "DeletionMismatch");
                                if (line.MarkedNoNewline && sourceLine != originalLines.Count - 1)
                                    return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NonFinalSourceMarker");
                                sourceLine++; oldUsed++; break;
                            case TextUnifiedLineKind.Add:
                                if (outputClosed) return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NonFinalOutputMarker");
                                AppendToken(line.Text); outputLine++; newUsed++;
                                break;
                            default: return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidLineKind");
                        }
                    }
                    if (oldUsed != hunk.OldCount || newUsed != hunk.NewCount)
                        return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "HunkCountMismatch");
                }
                while (sourceLine < originalLines.Count)
                {
                    budget.Charge();
                    if (outputClosed) return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NonFinalOutputMarker");
                    AppendToken(originalLines[sourceLine++]);
                    outputLine++;
                }
                var proposed = output.ToString();
                var proposedText = TextChangeCore.Validate(proposed, textOptions, cancellationToken);
                var diff = TextChangeCore.Diff(originalText, proposedText, textOptions, textWork);
                materialized.Add(new TextUnifiedMaterializedFile(file.RelativePath, proposed,
                    originalText.Snapshot, proposedText.Snapshot, diff.Edits));

                void AppendToken(string token)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = TextChangeCore.StrictUtf8.GetByteCount(token);
                    if (bytes > textOptions.MaxInputBytes - proposedBytes) throw new TextLimitException();
                    proposedBytes += bytes;
                    output.Append(token);
                    if (!token.EndsWith('\n')) outputClosed = true;
                }
            }
            return new(TextUnifiedMaterializationStatus.Succeeded, materialized, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return MaterializationFailure(TextUnifiedMaterializationStatus.Cancelled, "Cancelled"); }
        catch (LimitException) { return MaterializationFailure(TextUnifiedMaterializationStatus.LimitExceeded, "Work"); }
        catch (TextLimitException) { return MaterializationFailure(TextUnifiedMaterializationStatus.LimitExceeded, "TextLimits"); }
        catch (TextInputException) { return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "InvalidText"); }
        catch (OverflowException) { return MaterializationFailure(TextUnifiedMaterializationStatus.InvalidInput, "NumericOverflow"); }
    }

    private static string? ValidatePath(string value)
    {
        try
        {
            var normalized = WindowsWorkspacePath.Normalize(new WorkspacePath(value), allowRoot: false).Value;
            return string.Equals(normalized, value, StringComparison.Ordinal) ? value : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static bool IsUnsupportedForm(string line) => line.StartsWith("diff ", StringComparison.Ordinal) ||
        line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith("old mode ", StringComparison.Ordinal) ||
        line.StartsWith("new mode ", StringComparison.Ordinal) || line.StartsWith("new file mode ", StringComparison.Ordinal) ||
        line.StartsWith("deleted file mode ", StringComparison.Ordinal) || line.StartsWith("similarity index ", StringComparison.Ordinal) ||
        line.StartsWith("rename from ", StringComparison.Ordinal) || line.StartsWith("rename to ", StringComparison.Ordinal) ||
        line.StartsWith("copy from ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal) ||
        line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch" ||
        line.StartsWith("@@@", StringComparison.Ordinal) || line.StartsWith("Submodule ", StringComparison.Ordinal) ||
        line.StartsWith("--- /dev/null", StringComparison.Ordinal) || line.StartsWith("+++ /dev/null", StringComparison.Ordinal);

    private static bool TryParseHunkHeader(string text, int maxLines, out int oldStart, out int oldCount, out int newStart, out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;
        if (!text.StartsWith("@@ -", StringComparison.Ordinal)) return false;
        var separator = text.IndexOf(" +", 4, StringComparison.Ordinal);
        if (separator < 0 || !text.EndsWith(" @@", StringComparison.Ordinal)) return false;
        var oldRange = text.AsSpan(4, separator - 4);
        var newRange = text.AsSpan(separator + 2, text.Length - (separator + 2) - 3);
        if (!TryParseRange(oldRange, out oldStart, out oldCount) || !TryParseRange(newRange, out newStart, out newCount)) return false;
        if (oldStart > maxLines || newStart > maxLines) return false;
        var old0 = StartZero(oldStart, oldCount);
        var new0 = StartZero(newStart, newCount);
        return (long)old0 + oldCount <= maxLines && (long)new0 + newCount <= maxLines;
    }

    private static bool TryParseRange(ReadOnlySpan<char> value, out int start, out int count)
    {
        start = count = 0;
        var comma = value.IndexOf(',');
        var startSpan = comma < 0 ? value : value[..comma];
        if (startSpan.Length == 0 || !int.TryParse(startSpan, NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
        if (comma < 0) count = 1;
        else if (comma == value.Length - 1 || !int.TryParse(value[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count)) return false;
        return count >= 0 && (count == 0 ? start >= 0 : start >= 1);
    }

    private static int StartZero(int start, int count) => count == 0 ? start : checked(start - 1);

    private static List<PhysicalLine>? SplitPhysicalLines(string text, TextUnifiedImportOptions options, WorkBudget budget)
    {
        var result = new List<PhysicalLine>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            budget.Charge();
            if (text[i] != '\n') continue;
            if (result.Count >= options.MaxLines) return null;
            result.Add(new PhysicalLine(text[start..i], true));
            start = i + 1;
        }
        if (start < text.Length)
        {
            if (result.Count >= options.MaxLines) return null;
            result.Add(new PhysicalLine(text[start..], false));
        }
        return result;
    }

    private static int CountUtf8(string text, int maxBytes, WorkBudget budget)
    {
        if (text.Length > maxBytes) return maxBytes + 1;
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            budget.Charge();
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new EncoderFallbackException();
                i++; count += 4;
            }
            else if (char.IsLowSurrogate(c)) throw new EncoderFallbackException();
            else count += c <= 0x7f ? 1 : c <= 0x7ff ? 2 : 3;
            if (count > maxBytes) return maxBytes + 1;
        }
        return count;
    }

    private static List<string> SplitTextTokens(string value)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\n') continue;
            lines.Add(value[start..(i + 1)]);
            start = i + 1;
        }
        if (start < value.Length) lines.Add(value[start..]);
        return lines;
    }

    private static TextUnifiedImportResult Failure(TextUnifiedImportStatus status, string code) => new(status, null, code);
    private static TextUnifiedMaterializationResult MaterializationFailure(TextUnifiedMaterializationStatus status, string code) => new(status, null, code);
    private readonly record struct PhysicalLine(string Content, bool HasLf);

    private sealed class WorkBudget(int maximum, CancellationToken cancellationToken)
    {
        private int _used;
        internal void Charge(int amount = 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (amount < 0 || amount > maximum - _used) throw new LimitException();
            _used += amount;
        }
    }

    private sealed class LimitException : Exception;
}
