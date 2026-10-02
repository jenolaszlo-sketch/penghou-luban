using System.Text;
using Penghou.IO.Abstractions;

namespace Penghou.Luban.Changes;

/// <summary>Outcome of rendering a bounded unified text diff.</summary>
public enum TextUnifiedDiffStatus { Succeeded, InvalidInput, LimitExceeded, Cancelled, Unsupported }

/// <summary>Frozen identity of the supported plain unified serialization dialect.</summary>
public static class TextUnifiedDiffProfile
{
    public const string Identity = "penghou.luban.unified.windows.v1";
}

/// <summary>Options for the pure, byte-faithful unified text renderer.</summary>
public sealed record TextUnifiedDiffOptions
{
    public const int HardMaxContextLines = 100;
    public const int HardMaxOutputBytes = 256 * 1024;
    public const int HardMaxHunks = 4096;

    public static TextUnifiedDiffOptions Default { get; } = new();

    /// <summary>Context lines on each side of changes. Defaults to the conventional value of three.</summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>Underlying bounded text diff options.</summary>
    public TextChangeOptions ChangeOptions { get; init; } = TextChangeOptions.Default;

    /// <summary>Maximum UTF-8 bytes in the complete rendered diff.</summary>
    public int MaxOutputBytes { get; init; } = HardMaxOutputBytes;

    /// <summary>Maximum emitted display hunks.</summary>
    public int MaxHunks { get; init; } = HardMaxHunks;

    internal bool IsValid => ContextLines is >= 0 and <= HardMaxContextLines &&
        ChangeOptions is not null && ChangeOptions.IsValid && MaxOutputBytes is >= 1 and <= HardMaxOutputBytes &&
        MaxHunks is >= 1 and <= HardMaxHunks;
}

/// <summary>A zero-based start and count in display line coordinates.</summary>
public readonly record struct TextDisplayLineRange(int Start, int Count);

/// <summary>A deterministic unified-diff display hunk; ranges are not executable byte edits.</summary>
public sealed record TextDisplayHunk(TextDisplayLineRange OldRange, TextDisplayLineRange NewRange, string Body);

/// <summary>Immutable result of unified-diff rendering. A successful value is display data only.</summary>
public sealed class TextUnifiedDiffResult
{
    internal TextUnifiedDiffResult(TextUnifiedDiffStatus status, TextContentSnapshot? before,
        TextContentSnapshot? after, string? value = null, IReadOnlyList<TextDisplayHunk>? hunks = null)
    {
        Status = status;
        Before = before;
        After = after;
        UnifiedDiff = value;
        Hunks = Freeze(hunks);
    }

    public TextUnifiedDiffStatus Status { get; }
    public string ProfileIdentity => TextUnifiedDiffProfile.Identity;
    public string AlgorithmProfileIdentity => TextChangeProfile.Identity;
    public TextContentSnapshot? Before { get; }
    public TextContentSnapshot? After { get; }
    /// <summary>Present only when Status is Succeeded.</summary>
    public string? UnifiedDiff { get; }
    public IReadOnlyList<TextDisplayHunk> Hunks { get; }

    private static IReadOnlyList<TextDisplayHunk> Freeze(IReadOnlyList<TextDisplayHunk>? values)
    {
        if (values is null || values.Count == 0) return Array.AsReadOnly(Array.Empty<TextDisplayHunk>());
        var copy = new TextDisplayHunk[values.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = values[i] with { };
        return Array.AsReadOnly(copy);
    }
}

/// <summary>Renders deterministic, in-memory unified text diffs without I/O.</summary>
public static class TextUnifiedDiffRenderer
{
    /// <summary>
    /// Renders an existing-file modification using the narrow plain unified dialect.
    /// The path is a relative slash-separated display label and never selects a resource.
    /// </summary>
    public static TextUnifiedDiffResult Render(string? before, string? after, string? relativePath,
        TextUnifiedDiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        var selected = options ?? TextUnifiedDiffOptions.Default;
        if (!selected.IsValid) return new(TextUnifiedDiffStatus.InvalidInput, null, null);
        if (cancellationToken.IsCancellationRequested) return new(TextUnifiedDiffStatus.Cancelled, null, null);
        if (!IsSupportedPath(relativePath)) return new(TextUnifiedDiffStatus.Unsupported, null, null);

        try
        {
            var original = TextChangeCore.Validate(before, selected.ChangeOptions, cancellationToken);
            var updated = TextChangeCore.Validate(after, selected.ChangeOptions, cancellationToken);
            var budget = new TextWorkBudget(selected.ChangeOptions.MaxWorkCells, cancellationToken);
            var diff = TextChangeCore.Diff(original, updated, selected.ChangeOptions, budget);
            var bodyBudget = new TextOutputBudget(selected.MaxOutputBytes);
            var hunks = BuildHunks(original, updated, diff.LineEdits, selected, cancellationToken, budget, bodyBudget);
            var rendered = Write(relativePath!, hunks, selected, cancellationToken, budget);
            return new(TextUnifiedDiffStatus.Succeeded, original.Snapshot, updated.Snapshot, rendered, hunks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TextUnifiedDiffStatus.Cancelled, null, null);
        }
        catch (TextInputException)
        {
            return new(TextUnifiedDiffStatus.InvalidInput, null, null);
        }
        catch (TextLimitException)
        {
            return new(TextUnifiedDiffStatus.LimitExceeded, null, null);
        }
    }

    private static bool IsSupportedPath(string? path)
    {
        if (path is null) return false;
        try
        {
            _ = WindowsWorkspacePath.Normalize(new WorkspacePath(path), allowRoot: false);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static TextDisplayHunk[] BuildHunks(ValidatedText before, ValidatedText after,
        LineEdit[] changes, TextUnifiedDiffOptions options, CancellationToken ct, TextWorkBudget budget,
        TextOutputBudget bodyBudget)
    {
        if (changes.Length == 0) return [];
        var groups = new List<(int First, int Last)>();
        var groupStart = 0;
        for (var i = 1; i < changes.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var gap = changes[i].BaseStart - changes[i - 1].BaseEnd;
            budget.Consume(1);
            if (gap <= options.ContextLines * 2) continue;
            groups.Add((groupStart, i - 1));
            groupStart = i;
        }
        groups.Add((groupStart, changes.Length - 1));
        if (groups.Count > options.MaxHunks) throw new TextLimitException();

        var result = new TextDisplayHunk[groups.Count];
        for (var h = 0; h < groups.Count; h++)
        {
            ct.ThrowIfCancellationRequested();
            var (firstIndex, lastIndex) = groups[h];
            var first = changes[firstIndex];
            var last = changes[lastIndex];
            var precedingGap = firstIndex == 0 ? first.BaseStart : first.BaseStart - changes[firstIndex - 1].BaseEnd;
            var followingGap = lastIndex == changes.Length - 1 ? before.Lines.Length - last.BaseEnd : changes[lastIndex + 1].BaseStart - last.BaseEnd;
            var beforeCount = Math.Min(options.ContextLines, precedingGap);
            var afterCount = Math.Min(options.ContextLines, followingGap);
            var oldStart = first.BaseStart - beforeCount;
            var newStart = first.TargetStart - beforeCount;
            var oldEnd = last.BaseEnd + afterCount;
            var newEnd = last.TargetEnd + afterCount;
            if (newStart < 0 || newEnd > after.Lines.Length) throw new TextInputException();
            result[h] = new(new(oldStart, oldEnd - oldStart), new(newStart, newEnd - newStart),
                BuildBody(before, after, changes, firstIndex, lastIndex, oldStart, newStart, oldEnd, newEnd, ct, budget, bodyBudget));
        }
        return result;
    }

    private static string BuildBody(ValidatedText before, ValidatedText after, LineEdit[] changes,
        int firstIndex, int lastIndex, int oldStart, int newStart, int oldEnd, int newEnd,
        CancellationToken ct, TextWorkBudget budget, TextOutputBudget bodyBudget)
    {
        var body = new StringBuilder();
        var oldLine = oldStart;
        var newLine = newStart;
        for (var i = firstIndex; i <= lastIndex; i++)
        {
            ct.ThrowIfCancellationRequested();
            var change = changes[i];
            AppendEqual(change.BaseStart - oldLine);
            for (var line = change.BaseStart; line < change.BaseEnd; line++) AppendLine('-', before, line);
            for (var line = change.TargetStart; line < change.TargetEnd; line++) AppendLine('+', after, line);
            oldLine = change.BaseEnd;
            newLine = change.TargetEnd;
        }
        AppendEqual(oldEnd - oldLine);
        return body.ToString();

        void AppendEqual(int count)
        {
            if (count < 0 || oldLine + count > before.Lines.Length || newLine + count > after.Lines.Length)
                throw new TextInputException();
            for (var line = 0; line < count; line++)
            {
                ct.ThrowIfCancellationRequested();
                if (!before.TokenEquals(oldLine, after, newLine)) throw new TextInputException();
                AppendLine(' ', before, oldLine++);
                newLine++;
            }
        }

        void AppendLine(char prefix, ValidatedText source, int line)
        {
            budget.Consume(1);
            var sourceLine = source.Lines[line];
            var unterminated = source.Value[sourceLine.CharacterStart + sourceLine.CharacterLength - 1] != '\n';
            bodyBudget.ConsumeLine(sourceLine.ByteLength, unterminated);
            body.Append(prefix);
            body.Append(source.TextForLines(line, line + 1));
            if (unterminated) body.Append("\n\\ No newline at end of file\n");
        }
    }

    private static string Write(string path, IReadOnlyList<TextDisplayHunk> hunks,
        TextUnifiedDiffOptions options, CancellationToken ct, TextWorkBudget budget)
    {
        if (hunks.Count == 0) return string.Empty;
        var output = new StringBuilder();
        var outputBytes = 0;
        Append($"--- a/{path}\n");
        Append($"+++ b/{path}\n");
        foreach (var hunk in hunks)
        {
            ct.ThrowIfCancellationRequested();
            var old = FormatRange(hunk.OldRange);
            var updated = FormatRange(hunk.NewRange);
            Append($"@@ -{old} +{updated} @@\n");
            Append(hunk.Body);
        }
        return output.ToString();

        void Append(string text)
        {
            ct.ThrowIfCancellationRequested();
            budget.Consume(1);
            var appendBytes = TextChangeCore.StrictUtf8.GetByteCount(text);
            if ((long)outputBytes + appendBytes > options.MaxOutputBytes)
                throw new TextLimitException();
            output.Append(text);
            outputBytes += appendBytes;
        }
    }

    private static string FormatRange(TextDisplayLineRange range)
    {
        var start = range.Count == 0 ? range.Start : checked(range.Start + 1);
        return range.Count == 1 ? start.ToString(System.Globalization.CultureInfo.InvariantCulture) :
            $"{start.ToString(System.Globalization.CultureInfo.InvariantCulture)},{range.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private sealed class TextOutputBudget(int maximum)
    {
        private int _used;

        internal void ConsumeLine(int tokenByteLength, bool unterminated)
        {
            var bytes = tokenByteLength + 1L;
            if (unterminated) bytes += TextChangeCore.StrictUtf8.GetByteCount("\n\\ No newline at end of file\n");
            if (bytes > maximum - _used) throw new TextLimitException();
            _used += (int)bytes;
        }
    }
}
