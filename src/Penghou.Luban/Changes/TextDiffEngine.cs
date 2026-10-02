using System.Security.Cryptography;
using System.Text;

namespace Penghou.Luban.Changes;

/// <summary>Computes bounded, deterministic, in-memory text changes.</summary>
public static class TextDiffEngine
{
    /// <summary>Diffs exact Unicode text using UTF-8 line tokens; this method performs no I/O.</summary>
    public static TextDiffResult Diff(string? before, string? after,
        TextChangeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var selected = options ?? TextChangeOptions.Default;
        if (!selected.IsValid) return new(TextDiffStatus.InvalidInput, null, null);
        if (cancellationToken.IsCancellationRequested) return new(TextDiffStatus.Cancelled, null, null, options: selected);
        try
        {
            var original = TextChangeCore.Validate(before, selected, cancellationToken);
            var updated = TextChangeCore.Validate(after, selected, cancellationToken);
            var budget = new TextWorkBudget(selected.MaxWorkCells, cancellationToken);
            var data = TextChangeCore.Diff(original, updated, selected, budget);
            return new(TextDiffStatus.Succeeded, original.Snapshot, updated.Snapshot, data.Edits, selected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TextDiffStatus.Cancelled, null, null, options: selected);
        }
        catch (TextInputException)
        {
            return new(TextDiffStatus.InvalidInput, null, null, options: selected);
        }
        catch (TextLimitException)
        {
            return new(TextDiffStatus.LimitExceeded, null, null, options: selected);
        }
    }
}

internal static class TextChangeCore
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ValidatedText Validate(string? value, TextChangeOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value is null) throw new TextInputException();
        // UTF-8 byte count can never be less than the UTF-16 code-unit count.
        // Reject before asking the encoder to scan potentially oversized input.
        if (value.Length > options.MaxInputBytes) throw new TextLimitException();
        int byteCount;
        try { byteCount = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new TextInputException(); }
        if (byteCount > options.MaxInputBytes) throw new TextLimitException();
        ct.ThrowIfCancellationRequested();

        var bytes = StrictUtf8.GetBytes(value);
        var lineCount = CountLines(value, options.MaxLines, ct);
        var lines = new TextLine[lineCount];
        var lineIndex = 0;
        var charStart = 0;
        var byteStart = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (value[i] != '\n') continue;
            var length = i + 1 - charStart;
            var bytesInLine = StrictUtf8.GetByteCount(value.AsSpan(charStart, length));
            lines[lineIndex++] = new(charStart, length, byteStart, bytesInLine);
            charStart = i + 1;
            byteStart += bytesInLine;
        }
        if (charStart < value.Length)
        {
            var length = value.Length - charStart;
            var bytesInLine = StrictUtf8.GetByteCount(value.AsSpan(charStart, length));
            lines[lineIndex] = new(charStart, length, byteStart, bytesInLine);
        }
        if (lineIndex + (charStart < value.Length ? 1 : 0) != lineCount || byteStart +
            (charStart < value.Length ? lines[^1].ByteLength : 0) != bytes.Length) throw new TextInputException();

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(value, bytes, lines, new TextContentSnapshot(hash, byteCount));
    }

    private static int CountLines(string value, int maximum, CancellationToken ct)
    {
        if (value.Length == 0) return 0;
        var count = 0;
        var hasTail = true;
        for (var i = 0; i < value.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (value[i] != '\n') continue;
            if (++count > maximum) throw new TextLimitException();
            hasTail = i + 1 < value.Length;
        }
        if (hasTail && ++count > maximum) throw new TextLimitException();
        return count;
    }

    internal static TextDiffData Diff(ValidatedText before, ValidatedText after,
        TextChangeOptions options, TextWorkBudget budget)
    {
        budget.CancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(before.Value, after.Value, StringComparison.Ordinal)) return new([], []);

        var n = before.Lines.Length;
        var m = after.Lines.Length;
        var prefix = 0;
        while (prefix < n && prefix < m)
        {
            if ((prefix & 255) == 0) budget.CancellationToken.ThrowIfCancellationRequested();
            if (!before.TokenEquals(prefix, after, prefix)) break;
            budget.Consume(1);
            prefix++;
        }

        // The traceback always consumes equal leading tokens immediately, so the
        // unmatched suffixes have the same LCS choices and deletion-first ties as
        // the full matrix while needing only a matrix for the changed tail.
        var remainingBefore = n - prefix;
        var remainingAfter = m - prefix;
        var rows = checked((long)remainingBefore + 1);
        var columns = checked((long)remainingAfter + 1);
        var cells = checked(rows * columns);
        var matrixBytes = checked(cells * sizeof(int));
        if (cells > options.MaxWorkCells || cells > int.MaxValue || matrixBytes > options.MaxMatrixBytes)
            throw new TextLimitException();
        budget.Consume((int)cells);
        budget.CancellationToken.ThrowIfCancellationRequested();
        var columnCount = (int)columns;
        var lcs = new int[(int)cells];

        for (var i = remainingBefore - 1; i >= 0; i--)
        {
            if ((i & 31) == 0) budget.CancellationToken.ThrowIfCancellationRequested();
            var row = i * columnCount;
            var nextRow = (i + 1) * columnCount;
            for (var j = remainingAfter - 1; j >= 0; j--)
            {
                if ((j & 255) == 0) budget.CancellationToken.ThrowIfCancellationRequested();
                lcs[row + j] = before.TokenEquals(prefix + i, after, prefix + j)
                    ? checked(lcs[nextRow + j + 1] + 1)
                    : Math.Max(lcs[nextRow + j], lcs[row + j + 1]);
            }
        }

        var lineEdits = new List<LineEdit>();
        var oldLine = prefix;
        var newLine = prefix;
        var editOldStart = -1;
        var editNewStart = -1;
        while (oldLine < n || newLine < m)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (oldLine < n && newLine < m && before.TokenEquals(oldLine, after, newLine))
            {
                FlushEdit();
                oldLine++;
                newLine++;
                continue;
            }

            var relativeOldLine = oldLine - prefix;
            var relativeNewLine = newLine - prefix;
            var deleteScore = oldLine < n ? lcs[(relativeOldLine + 1) * columnCount + relativeNewLine] : -1;
            var insertScore = newLine < m ? lcs[relativeOldLine * columnCount + relativeNewLine + 1] : -1;
            if (oldLine < n && (newLine == m || deleteScore >= insertScore))
            {
                if (editOldStart < 0) { editOldStart = oldLine; editNewStart = newLine; }
                oldLine++; // Equal LCS scores choose deletion first (frozen deterministic tie break).
            }
            else
            {
                if (editOldStart < 0) { editOldStart = oldLine; editNewStart = newLine; }
                newLine++;
            }
        }
        FlushEdit();

        if (lineEdits.Count > options.MaxEdits) throw new TextLimitException();
        var edits = new TextEdit[lineEdits.Count];
        long outputBytes = 0;
        for (var i = 0; i < lineEdits.Count; i++)
        {
            if ((i & 127) == 0) budget.CancellationToken.ThrowIfCancellationRequested();
            var lineEdit = lineEdits[i];
            var byteStart = before.ByteOffsetAtLine(lineEdit.BaseStart);
            var deleteLength = before.ByteOffsetAtLine(lineEdit.BaseEnd) - byteStart;
            var replacement = after.TextForLines(lineEdit.TargetStart, lineEdit.TargetEnd);
            var replacementBytes = StrictUtf8.GetByteCount(replacement);
            outputBytes = checked(outputBytes + replacementBytes);
            if (outputBytes > options.MaxOutputBytes) throw new TextLimitException();
            edits[i] = new(byteStart, deleteLength, replacement);
        }
        return new(lineEdits.ToArray(), edits);

        void FlushEdit()
        {
            if (editOldStart < 0) return;
            lineEdits.Add(new(editOldStart, oldLine, editNewStart, newLine));
            editOldStart = -1;
            editNewStart = -1;
        }
    }

    internal static string TextForLines(ValidatedText text, int start, int end) => text.TextForLines(start, end);

    internal static TextContentSnapshot SnapshotOutput(string value, int byteCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException) { throw new TextInputException(); }
        if (bytes.Length != byteCount) throw new TextInputException();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        ct.ThrowIfCancellationRequested();
        return new(hash, byteCount);
    }
}

internal sealed class ValidatedText(string value, byte[] bytes, TextLine[] lines, TextContentSnapshot snapshot)
{
    internal string Value { get; } = value;
    internal byte[] Bytes { get; } = bytes;
    internal TextLine[] Lines { get; } = lines;
    internal TextContentSnapshot Snapshot { get; } = snapshot;

    internal bool TokenEquals(int index, ValidatedText other, int otherIndex)
    {
        var left = Lines[index];
        var right = other.Lines[otherIndex];
        return left.CharacterLength == right.CharacterLength && string.CompareOrdinal(
            Value, left.CharacterStart, other.Value, right.CharacterStart, left.CharacterLength) == 0;
    }

    internal int ByteOffsetAtLine(int lineIndex) => lineIndex == Lines.Length
        ? Bytes.Length
        : Lines[lineIndex].ByteStart;

    internal string TextForLines(int start, int end)
    {
        if (start == end) return string.Empty;
        var first = Lines[start];
        var last = Lines[end - 1];
        return Value.Substring(first.CharacterStart, last.CharacterStart + last.CharacterLength - first.CharacterStart);
    }

    internal int ByteLengthForLines(int start, int end) => ByteOffsetAtLine(end) - ByteOffsetAtLine(start);
}

internal readonly record struct TextLine(int CharacterStart, int CharacterLength, int ByteStart, int ByteLength);
internal readonly record struct LineEdit(int BaseStart, int BaseEnd, int TargetStart, int TargetEnd);
internal sealed record TextDiffData(LineEdit[] LineEdits, TextEdit[] Edits);

internal sealed class TextWorkBudget(int maximum, CancellationToken cancellationToken)
{
    private int _used;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal void Consume(int amount)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (amount < 0 || amount > maximum - _used) throw new TextLimitException();
        _used += amount;
    }
}

internal sealed class TextInputException : Exception;
internal sealed class TextLimitException : Exception;
