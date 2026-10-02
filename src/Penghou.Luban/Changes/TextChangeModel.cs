using System.Collections.ObjectModel;

namespace Penghou.Luban.Changes;

/// <summary>Outcome of comparing two in-memory text values.</summary>
public enum TextDiffStatus { Succeeded, InvalidInput, LimitExceeded, Cancelled }

/// <summary>Outcome of a deterministic three-way in-memory text merge.</summary>
public enum TextMergeStatus { Clean, Conflicted, InvalidInput, LimitExceeded, Cancelled }

/// <summary>Frozen identity for the current bounded line-diff and merge semantics.</summary>
public static class TextChangeProfile
{
    public const string Identity = "penghou.luban.text-lcs.v1";
}

/// <summary>Hard-bounded work limits for the pure text diff and merge engines.</summary>
public sealed record TextChangeOptions
{
    public const int HardMaxInputBytes = 128 * 1024;
    public const int HardMaxLines = 4096;
    public const int HardMaxWorkCells = 4_000_000;
    public const int HardMaxMatrixBytes = 16 * 1024 * 1024;
    public const int HardMaxEdits = 4096;
    public const int HardMaxOutputBytes = 256 * 1024;
    public const int HardMaxConflicts = 4096;

    public static TextChangeOptions Default { get; } = new();

    /// <summary>Maximum UTF-8 bytes in each input, including a leading BOM when present.</summary>
    public int MaxInputBytes { get; init; } = HardMaxInputBytes;

    /// <summary>Maximum line tokens in each input. LF and CRLF terminators stay in their tokens.</summary>
    public int MaxLines { get; init; } = HardMaxLines;

    /// <summary>Maximum cumulative LCS cells and merge/grouping work units per call.</summary>
    public int MaxWorkCells { get; init; } = HardMaxWorkCells;

    /// <summary>Maximum bytes in one allocated LCS matrix.</summary>
    public int MaxMatrixBytes { get; init; } = HardMaxMatrixBytes;

    /// <summary>Maximum edits in one diff or clean merge result.</summary>
    public int MaxEdits { get; init; } = HardMaxEdits;

    /// <summary>Maximum total UTF-8 bytes emitted in replacements, merged value, or conflict regions.</summary>
    public int MaxOutputBytes { get; init; } = HardMaxOutputBytes;

    /// <summary>Maximum structured conflicts in a merge result.</summary>
    public int MaxConflicts { get; init; } = HardMaxConflicts;

    internal bool IsValid => MaxInputBytes is >= 1 and <= HardMaxInputBytes &&
        MaxLines is >= 1 and <= HardMaxLines &&
        MaxWorkCells is >= 1 and <= HardMaxWorkCells &&
        MaxMatrixBytes is >= sizeof(int) and <= HardMaxMatrixBytes &&
        MaxEdits is >= 1 and <= HardMaxEdits &&
        MaxOutputBytes is >= 1 and <= HardMaxOutputBytes &&
        MaxConflicts is >= 1 and <= HardMaxConflicts;
}

/// <summary>Identity of an exact strict-UTF-8 text input or output.</summary>
public sealed class TextContentSnapshot
{
    internal TextContentSnapshot(string sha256, int byteLength)
    {
        Sha256 = sha256;
        ByteLength = byteLength;
    }

    public string Sha256 { get; }
    public int ByteLength { get; }
}

/// <summary>
/// One scalar-safe edit against the exact original UTF-8 byte sequence.
/// StartOffset and DeleteLength are zero-based byte coordinates; replacement
/// text is strict UTF-8 and contains no implicit newline or Unicode normalization.
/// </summary>
public sealed record TextEdit(int StartOffset, int DeleteLength, string Replacement);

/// <summary>Immutable result of a bounded in-memory text diff.</summary>
public sealed class TextDiffResult
{
    internal TextDiffResult(TextDiffStatus status, TextContentSnapshot? before,
        TextContentSnapshot? after, IReadOnlyList<TextEdit>? edits = null,
        TextChangeOptions? options = null)
    {
        Status = status;
        Before = before;
        After = after;
        Edits = Freeze(edits);
        Options = FreezeOptions(options);
    }

    public TextDiffStatus Status { get; }
    public string ProfileIdentity => TextChangeProfile.Identity;
    public TextContentSnapshot? Before { get; }
    public TextContentSnapshot? After { get; }
    public IReadOnlyList<TextEdit> Edits { get; }
    /// <summary>The frozen limits selected for the computation, or null when options were invalid.</summary>
    public TextChangeOptions? Options { get; }
    public bool Changed => Status == TextDiffStatus.Succeeded && Edits.Count != 0;

    private static IReadOnlyList<TextEdit> Freeze(IReadOnlyList<TextEdit>? values)
    {
        if (values is null || values.Count == 0) return Array.AsReadOnly(Array.Empty<TextEdit>());
        var copy = new TextEdit[values.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = values[i] with { };
        return new ReadOnlyCollection<TextEdit>(copy);
    }

    internal static TextChangeOptions? FreezeOptions(TextChangeOptions? value) => value is null ? null : value with { };
}

/// <summary>
/// A connected conflict region in base line coordinates. The line range is
/// zero-based and half-open. Text fields retain the exact UTF-8-decoded line
/// terminators from each input; insertion conflicts can have an empty base range.
/// </summary>
public sealed record TextMergeConflict(
    int BaseStartLine,
    int BaseEndLine,
    int BaseStartOffset,
    int BaseDeleteLength,
    string BaseText,
    string OursText,
    string TheirsText);

/// <summary>Immutable result of a bounded deterministic three-way merge.</summary>
public sealed class TextMergeResult
{
    internal TextMergeResult(TextMergeStatus status, TextContentSnapshot? @base,
        TextContentSnapshot? ours, TextContentSnapshot? theirs, TextContentSnapshot? result,
        string? value, IReadOnlyList<TextEdit>? edits = null,
        IReadOnlyList<TextMergeConflict>? conflicts = null, TextChangeOptions? options = null)
    {
        Status = status;
        Base = @base;
        Ours = ours;
        Theirs = theirs;
        Result = result;
        Value = value;
        Edits = FreezeEdits(edits);
        Conflicts = FreezeConflicts(conflicts);
        Options = TextDiffResult.FreezeOptions(options);
    }

    public TextMergeStatus Status { get; }
    public string ProfileIdentity => TextChangeProfile.Identity;
    public TextContentSnapshot? Base { get; }
    public TextContentSnapshot? Ours { get; }
    public TextContentSnapshot? Theirs { get; }
    public TextContentSnapshot? Result { get; }
    /// <summary>Present only when Status is Clean.</summary>
    public string? Value { get; }
    /// <summary>Exact UTF-8 edits against Base; empty unless Status is Clean.</summary>
    public IReadOnlyList<TextEdit> Edits { get; }
    /// <summary>Structured conflicts; no preferred side or conflict markers are generated.</summary>
    public IReadOnlyList<TextMergeConflict> Conflicts { get; }
    /// <summary>The frozen limits selected for the computation, or null when options were invalid.</summary>
    public TextChangeOptions? Options { get; }

    private static IReadOnlyList<TextEdit> FreezeEdits(IReadOnlyList<TextEdit>? values)
    {
        if (values is null || values.Count == 0) return Array.AsReadOnly(Array.Empty<TextEdit>());
        var copy = new TextEdit[values.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = values[i] with { };
        return new ReadOnlyCollection<TextEdit>(copy);
    }

    private static IReadOnlyList<TextMergeConflict> FreezeConflicts(IReadOnlyList<TextMergeConflict>? values)
    {
        if (values is null || values.Count == 0) return Array.AsReadOnly(Array.Empty<TextMergeConflict>());
        var copy = new TextMergeConflict[values.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = values[i] with { };
        return new ReadOnlyCollection<TextMergeConflict>(copy);
    }
}
