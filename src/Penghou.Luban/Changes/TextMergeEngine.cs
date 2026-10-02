using System.Text;

namespace Penghou.Luban.Changes;

/// <summary>Computes bounded, deterministic, in-memory three-way line merges.</summary>
public static class TextMergeEngine
{
    /// <summary>
    /// Merges exact text snapshots by line. Conflicts are explicit structured regions;
    /// this method performs no I/O and never selects a preferred side.
    /// </summary>
    public static TextMergeResult Merge(string? @base, string? ours, string? theirs,
        TextChangeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var selected = options ?? TextChangeOptions.Default;
        if (!selected.IsValid) return Failure(TextMergeStatus.InvalidInput);
        if (cancellationToken.IsCancellationRequested) return Failure(TextMergeStatus.Cancelled, selected);
        try
        {
            // Validate every input before any equality or one-sided fast path.
            var baseText = TextChangeCore.Validate(@base, selected, cancellationToken);
            var oursText = TextChangeCore.Validate(ours, selected, cancellationToken);
            var theirsText = TextChangeCore.Validate(theirs, selected, cancellationToken);
            var budget = new TextWorkBudget(selected.MaxWorkCells, cancellationToken);

            if (string.Equals(oursText.Value, theirsText.Value, StringComparison.Ordinal))
            {
                var sameSideDiff = string.Equals(baseText.Value, oursText.Value, StringComparison.Ordinal)
                    ? null : TextChangeCore.Diff(baseText, oursText, selected, budget);
                return Clean(baseText, oursText, theirsText, oursText, sameSideDiff, selected, budget);
            }

            TextDiffData? oursDiff = null;
            TextDiffData? theirsDiff = null;
            if (!string.Equals(baseText.Value, oursText.Value, StringComparison.Ordinal))
                oursDiff = TextChangeCore.Diff(baseText, oursText, selected, budget);
            if (!string.Equals(baseText.Value, theirsText.Value, StringComparison.Ordinal))
                theirsDiff = TextChangeCore.Diff(baseText, theirsText, selected, budget);

            if (oursDiff is null)
                return Clean(baseText, oursText, theirsText, theirsText, theirsDiff, selected, budget);
            if (theirsDiff is null)
                return Clean(baseText, oursText, theirsText, oursText, oursDiff, selected, budget);

            var changes = new List<MergeChange>(oursDiff.LineEdits.Length + theirsDiff.LineEdits.Length);
            foreach (var edit in oursDiff.LineEdits)
            {
                budget.Consume(1);
                changes.Add(new(true, edit, TextChangeCore.TextForLines(oursText, edit.TargetStart, edit.TargetEnd)));
            }
            foreach (var edit in theirsDiff.LineEdits)
            {
                budget.Consume(1);
                changes.Add(new(false, edit, TextChangeCore.TextForLines(theirsText, edit.TargetStart, edit.TargetEnd)));
            }

            var duplicateTheirs = new bool[changes.Count];
            var parent = new int[changes.Count];
            var hasConflict = new bool[changes.Count];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;
            for (var oi = 0; oi < oursDiff.LineEdits.Length; oi++)
            {
                var oursIndex = oi;
                for (var ti = 0; ti < theirsDiff.LineEdits.Length; ti++)
                {
                    budget.Consume(1);
                    var theirsIndex = oursDiff.LineEdits.Length + ti;
                    var left = changes[oursIndex];
                    var right = changes[theirsIndex];
                    if (SameEdit(left, right))
                    {
                        duplicateTheirs[theirsIndex] = true;
                        continue;
                    }
                    if (!Conflicts(left.Edit, right.Edit)) continue;
                    Union(parent, oursIndex, theirsIndex);
                    hasConflict[oursIndex] = true;
                    hasConflict[theirsIndex] = true;
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (var i = 0; i < changes.Count; i++)
            {
                budget.Consume(1);
                if (!hasConflict[i]) continue;
                var root = Find(parent, i);
                if (!groups.TryGetValue(root, out var group)) groups.Add(root, group = []);
                group.Add(i);
            }
            if (groups.Count > selected.MaxConflicts) throw new TextLimitException();
            if (groups.Count != 0)
                return Conflicted(baseText, oursText, theirsText, changes, groups, selected, budget);

            var cleanChanges = new List<MergeChange>(changes.Count);
            for (var i = 0; i < changes.Count; i++)
            {
                budget.Consume(1);
                if (!duplicateTheirs[i]) cleanChanges.Add(changes[i]);
            }
            ChargeSort(cleanChanges.Count, budget);
            cleanChanges.Sort(static (left, right) =>
            {
                var start = left.Edit.BaseStart.CompareTo(right.Edit.BaseStart);
                return start != 0 ? start : left.Edit.BaseEnd.CompareTo(right.Edit.BaseEnd);
            });
            return BuildClean(baseText, oursText, theirsText, cleanChanges.ToArray(), selected, budget);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(TextMergeStatus.Cancelled, selected);
        }
        catch (TextInputException)
        {
            return Failure(TextMergeStatus.InvalidInput, selected);
        }
        catch (TextLimitException)
        {
            return Failure(TextMergeStatus.LimitExceeded, selected);
        }
    }

    private static TextMergeResult Clean(ValidatedText baseText, ValidatedText oursText,
        ValidatedText theirsText, ValidatedText resultText, TextDiffData? diff,
        TextChangeOptions options, TextWorkBudget budget)
    {
        var edits = diff?.Edits ?? [];
        long bytes = resultText.Bytes.Length;
        if (bytes > options.MaxOutputBytes) throw new TextLimitException();
        foreach (var edit in edits)
        {
            budget.Consume(1);
            bytes = checked(bytes + TextChangeCore.StrictUtf8.GetByteCount(edit.Replacement));
            if (bytes > options.MaxOutputBytes) throw new TextLimitException();
        }
        if (edits.Length > options.MaxEdits) throw new TextLimitException();
        return new(TextMergeStatus.Clean, baseText.Snapshot, oursText.Snapshot,
            theirsText.Snapshot, resultText.Snapshot, resultText.Value, edits, options: options);
    }

    private static TextMergeResult BuildClean(ValidatedText baseText, ValidatedText oursText,
        ValidatedText theirsText, MergeChange[] changes, TextChangeOptions options, TextWorkBudget budget)
    {
        if (changes.Length > options.MaxEdits) throw new TextLimitException();
        long valueBytes = baseText.Bytes.Length;
        long replacementBytes = 0;
        foreach (var change in changes)
        {
            budget.Consume(1);
            var oldLength = baseText.ByteLengthForLines(change.Edit.BaseStart, change.Edit.BaseEnd);
            var replacementLength = TextChangeCore.StrictUtf8.GetByteCount(change.Replacement);
            valueBytes = checked(valueBytes - oldLength + replacementLength);
            replacementBytes = checked(replacementBytes + replacementLength);
        }
        if (valueBytes < 0 || valueBytes > options.MaxOutputBytes ||
            checked(valueBytes + replacementBytes) > options.MaxOutputBytes) throw new TextLimitException();

        var builder = new StringBuilder();
        var edits = new TextEdit[changes.Length];
        var cursor = 0;
        for (var i = 0; i < changes.Length; i++)
        {
            budget.Consume(1);
            var change = changes[i];
            if (change.Edit.BaseStart < cursor) throw new TextInputException();
            builder.Append(TextChangeCore.TextForLines(baseText, cursor, change.Edit.BaseStart));
            var startOffset = baseText.ByteOffsetAtLine(change.Edit.BaseStart);
            var deleteLength = baseText.ByteOffsetAtLine(change.Edit.BaseEnd) - startOffset;
            builder.Append(change.Replacement);
            edits[i] = new(startOffset, deleteLength, change.Replacement);
            cursor = change.Edit.BaseEnd;
        }
        builder.Append(TextChangeCore.TextForLines(baseText, cursor, baseText.Lines.Length));
        var value = builder.ToString();
        var snapshot = TextChangeCore.SnapshotOutput(value, (int)valueBytes, budget.CancellationToken);
        return new(TextMergeStatus.Clean, baseText.Snapshot, oursText.Snapshot,
            theirsText.Snapshot, snapshot, value, edits, options: options);
    }

    private static TextMergeResult Conflicted(ValidatedText baseText, ValidatedText oursText,
        ValidatedText theirsText, List<MergeChange> changes, Dictionary<int, List<int>> groups,
        TextChangeOptions options, TextWorkBudget budget)
    {
        var ordered = new List<ConflictGroup>(groups.Count);
        foreach (var indices in groups.Values)
        {
            budget.Consume(1);
            var start = int.MaxValue;
            var end = 0;
            foreach (var i in indices)
            {
                budget.Consume(1);
                start = Math.Min(start, changes[i].Edit.BaseStart);
                end = Math.Max(end, changes[i].Edit.BaseEnd);
            }
            ordered.Add(new(start, end, indices));
        }
        ChargeSort(ordered.Count, budget);
        ordered.Sort(static (left, right) =>
        {
            var start = left.Start.CompareTo(right.Start);
            return start != 0 ? start : left.End.CompareTo(right.End);
        });
        var conflicts = new TextMergeConflict[ordered.Count];
        long emittedBytes = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            budget.Consume(1);
            var group = ordered[index];
            var baseRegion = TextChangeCore.TextForLines(baseText, group.Start, group.End);
            var oursList = new List<MergeChange>();
            var theirsList = new List<MergeChange>();
            foreach (var i in group.Indices)
            {
                budget.Consume(1);
                (changes[i].Ours ? oursList : theirsList).Add(changes[i]);
            }
            var oursEdits = oursList.ToArray();
            var theirsEdits = theirsList.ToArray();
            var oursRegion = RenderRegion(baseText, group.Start, group.End, oursEdits, budget);
            var theirsRegion = RenderRegion(baseText, group.Start, group.End, theirsEdits, budget);
            emittedBytes = checked(emittedBytes + TextChangeCore.StrictUtf8.GetByteCount(baseRegion) +
                TextChangeCore.StrictUtf8.GetByteCount(oursRegion) + TextChangeCore.StrictUtf8.GetByteCount(theirsRegion));
            if (emittedBytes > options.MaxOutputBytes) throw new TextLimitException();
            var startOffset = baseText.ByteOffsetAtLine(group.Start);
            var deleteLength = baseText.ByteOffsetAtLine(group.End) - startOffset;
            conflicts[index] = new(group.Start, group.End, startOffset, deleteLength,
                baseRegion, oursRegion, theirsRegion);
        }
        return new(TextMergeStatus.Conflicted, baseText.Snapshot, oursText.Snapshot,
            theirsText.Snapshot, null, null, conflicts: conflicts, options: options);
    }

    private static string RenderRegion(ValidatedText baseText, int start, int end,
        MergeChange[] changes, TextWorkBudget budget)
    {
        if (changes.Length == 0) return TextChangeCore.TextForLines(baseText, start, end);
        ChargeSort(changes.Length, budget);
        Array.Sort(changes, static (a, b) =>
        {
            var byStart = a.Edit.BaseStart.CompareTo(b.Edit.BaseStart);
            return byStart != 0 ? byStart : a.Edit.BaseEnd.CompareTo(b.Edit.BaseEnd);
        });
        var builder = new StringBuilder();
        var cursor = start;
        foreach (var change in changes)
        {
            budget.Consume(1);
            if (change.Edit.BaseStart < cursor || change.Edit.BaseEnd > end) throw new TextInputException();
            builder.Append(TextChangeCore.TextForLines(baseText, cursor, change.Edit.BaseStart));
            builder.Append(change.Replacement);
            cursor = change.Edit.BaseEnd;
        }
        builder.Append(TextChangeCore.TextForLines(baseText, cursor, end));
        return builder.ToString();
    }

    private static bool SameEdit(MergeChange left, MergeChange right) =>
        left.Edit.BaseStart == right.Edit.BaseStart && left.Edit.BaseEnd == right.Edit.BaseEnd &&
        string.Equals(left.Replacement, right.Replacement, StringComparison.Ordinal);

    private static bool Conflicts(LineEdit left, LineEdit right)
    {
        var leftInsert = left.BaseStart == left.BaseEnd;
        var rightInsert = right.BaseStart == right.BaseEnd;
        if (leftInsert && rightInsert) return left.BaseStart == right.BaseStart;
        if (leftInsert) return left.BaseStart >= right.BaseStart && left.BaseStart <= right.BaseEnd;
        if (rightInsert) return right.BaseStart >= left.BaseStart && right.BaseStart <= left.BaseEnd;
        return left.BaseStart < right.BaseEnd && right.BaseStart < left.BaseEnd;
    }

    private static int Find(int[] parent, int value)
    {
        while (parent[value] != value)
        {
            parent[value] = parent[parent[value]];
            value = parent[value];
        }
        return value;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var a = Find(parent, left);
        var b = Find(parent, right);
        if (a != b) parent[b] = a;
    }

    private static void ChargeSort(int count, TextWorkBudget budget)
    {
        if (count < 2) return;
        var log = 1;
        while ((1L << log) < count) log++;
        budget.Consume(checked(2 * count * (log + 1)));
    }

    private static TextMergeResult Failure(TextMergeStatus status, TextChangeOptions? options = null) =>
        new(status, null, null, null, null, null, options: options);
    private sealed record MergeChange(bool Ours, LineEdit Edit, string Replacement);
    private sealed record ConflictGroup(int Start, int End, List<int> Indices);
}
