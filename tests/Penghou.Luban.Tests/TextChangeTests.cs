using System.Text;
using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class TextChangeTests
{
    [Theory]
    [InlineData("a\r\nb\n", "A\r\nb\n")]
    [InlineData("\uFEFFa\nb", "\uFEFFa\nB")]
    [InlineData("", "x")]
    [InlineData("x", "")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("hello 😀\n", "hello 🌍\n")]
    public void DiffEditsReconstructExactUtf8(string before, string after)
    {
        var result = TextDiffEngine.Diff(before, after);
        Assert.Equal(TextDiffStatus.Succeeded, result.Status);
        Assert.Equal(TextChangeProfile.Identity, result.ProfileIdentity);
        Assert.Equal(after, Apply(before, result.Edits));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(before))).ToLowerInvariant(), result.Before!.Sha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(after), result.After!.ByteLength);
    }

    [Fact]
    public void RepeatedLinesUseDeletionFirstTieBreak()
    {
        var result = TextDiffEngine.Diff("a\nb\n", "b\na\n");
        Assert.Equal(TextDiffStatus.Succeeded, result.Status);
        Assert.Equal(new TextEdit(0, 2, ""), result.Edits[0]);
        Assert.Equal(new TextEdit(4, 0, "a\n"), result.Edits[1]);
        Assert.Equal("b\na\n", Apply("a\nb\n", result.Edits));
    }

    [Fact]
    public void PublishedRepeatedLineVectorIsStable()
    {
        var result = TextDiffEngine.Diff("A\nA\nB\n", "A\nB\nA\n");
        Assert.Equal(TextDiffStatus.Succeeded, result.Status);
        Assert.Equal(new TextEdit(2, 2, ""), result.Edits[0]);
        Assert.Equal(new TextEdit(6, 0, "A\n"), result.Edits[1]);
        Assert.Equal("a83c683382655e93a95d5e146c55635e1966c8dbd9a292fe4deaaad612625577", result.Before!.Sha256);
        Assert.Equal("42dae60f8089418e5b92fee52559198aacb62f9291989e137feef34e6e05c637", result.After!.Sha256);
    }

    [Fact]
    public void LongCommonPrefixAllowsSmallEditWithinMatrixBudget()
    {
        var lines = Enumerable.Range(0, 2200).Select(i => $"line-{i:D4}\n").ToArray();
        const int changedLine = 1100;
        var before = string.Concat(lines);
        lines[changedLine] = "changed\n";
        var after = string.Concat(lines);

        var result = TextDiffEngine.Diff(before, after, new TextChangeOptions { MaxMatrixBytes = 6 * 1024 * 1024 });

        Assert.Equal(TextDiffStatus.Succeeded, result.Status);
        Assert.Equal(after, Apply(before, result.Edits));
        var edit = Assert.Single(result.Edits);
        Assert.Equal(Encoding.UTF8.GetByteCount(string.Concat(Enumerable.Range(0, changedLine).Select(i => $"line-{i:D4}\n"))), edit.StartOffset);
        Assert.Equal(Encoding.UTF8.GetByteCount($"line-{changedLine:D4}\n"), edit.DeleteLength);
        Assert.Equal("changed\n", edit.Replacement);
    }

    [Fact]
    public void PrefixTrimMatchesFullMatrixReferenceForRepeatedLines()
    {
        var inputs = new[]
        {
            ("A\nA\nB\n", "A\nB\nA\n"),
            ("x\nx\ny\nx\n", "x\ny\nx\nx\n"),
            ("head\na\na\nb\ntail\n", "head\na\nb\na\ntail\n"),
            ("p\na\nb\na\n", "p\nb\na\na\n"),
            ("repeat\nrepeat\nleft\n", "repeat\nright\nrepeat\n")
        };

        foreach (var (before, after) in inputs)
        {
            var actual = TextDiffEngine.Diff(before, after);
            Assert.Equal(TextDiffStatus.Succeeded, actual.Status);
            Assert.Equal(FullMatrixReference(before, after), actual.Edits);
        }
    }

    [Fact]
    public void DiffCollectionsCannotMutateResult()
    {
        var result = TextDiffEngine.Diff("a", "b");
        var list = Assert.IsAssignableFrom<IList<TextEdit>>(result.Edits);
        Assert.Throws<NotSupportedException>(() => list[0] = new TextEdit(0, 1, "x"));
    }

    [Fact]
    public void MergeCombinesDisjointLineEditsAndKeepsExactTerminators()
    {
        var result = TextMergeEngine.Merge("a\r\nb\nc\n", "A\r\nb\nc\n", "a\r\nb\nC\n");
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal(TextChangeProfile.Identity, result.ProfileIdentity);
        Assert.Equal("A\r\nb\nC\n", result.Value);
        Assert.Equal(result.Value, Apply("a\r\nb\nc\n", result.Edits));
        Assert.Equal(2, result.Edits.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(result.Value!), result.Result!.ByteLength);
    }

    [Fact]
    public void MergeIdenticalEditsOnceAndFastPathUsesOneMatrix()
    {
        var options = new TextChangeOptions { MaxWorkCells = 10 };
        var result = TextMergeEngine.Merge("a\nb\n", "A\nb\n", "A\nb\n", options);
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal("A\nb\n", result.Value);
        Assert.Single(result.Edits);
    }

    [Fact]
    public void MergeOutputMayExceedEachInputLineLimitWhenOutputBudgetAllows()
    {
        var options = new TextChangeOptions { MaxInputBytes = 4, MaxLines = 2, MaxOutputBytes = 32 };
        var result = TextMergeEngine.Merge("x\n", "a\nx\n", "x\nb\n", options);
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal("a\nx\nb\n", result.Value);
        Assert.Equal(3, result.Value!.Count(c => c == '\n'));
    }

    [Fact]
    public void IdenticalInsertionAtSameBoundaryDeduplicates()
    {
        var result = TextMergeEngine.Merge("a\n", "x\na\n", "x\na\n");
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal("x\na\n", result.Value);
        Assert.Single(result.Edits);
    }

    [Theory]
    [InlineData("a\nb\n", "A\nb\n", "B\nb\n", 0, 1)]
    [InlineData("a\nb\n", "a\nb\nX\n", "a\nb\nY\n", 2, 2)]
    [InlineData("a\nb\nc\n", "A\nb\nc\n", "X\nc\n", 0, 2)]
    [InlineData("a\nb\nc\n", "A\nb\nc\n", "X\nb\nc\n", 0, 1)]
    [InlineData("a\nb\n", "A\nb\n", "x\nA\nb\n", 0, 1)]
    public void OverlappingOrBoundaryEditsAreStructuredConflicts(
        string baseText, string ours, string theirs, int start, int end)
    {
        var result = TextMergeEngine.Merge(baseText, ours, theirs);
        Assert.Equal(TextMergeStatus.Conflicted, result.Status);
        Assert.Null(result.Value);
        Assert.Null(result.Result);
        Assert.Empty(result.Edits);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(start, conflict.BaseStartLine);
        Assert.Equal(end, conflict.BaseEndLine);
    }

    [Theory]
    [InlineData("A\nb\n", "x\na\nb\n")]
    [InlineData("a\nB\n", "a\nb\nx\n")]
    public void InsertionsAtReplacementBoundariesConflict(string ours, string theirs)
    {
        var result = TextMergeEngine.Merge("a\nb\n", ours, theirs);
        Assert.Equal(TextMergeStatus.Conflicted, result.Status);
        Assert.Single(result.Conflicts);
    }

    [Fact]
    public void DeleteVersusModifyConflictsEvenWithEmptyBaseSideResult()
    {
        var result = TextMergeEngine.Merge("a\n", "", "A\n");
        Assert.Equal(TextMergeStatus.Conflicted, result.Status);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("a\n", conflict.BaseText);
        Assert.Equal("", conflict.OursText);
        Assert.Equal("A\n", conflict.TheirsText);
    }

    [Fact]
    public void AdjacentReplacementsAreIndependent()
    {
        var result = TextMergeEngine.Merge("a\nb\nc\n", "A\nb\nc\n", "a\nB\nc\n");
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal("A\nB\nc\n", result.Value);
    }

    [Fact]
    public void ConflictComponentsAreTransitiveAndStableUnderSideSwap()
    {
        const string @base = "0\n1\n2\n3\n4\n5\n6\n";
        const string ours = "0\nO1\n2\n3\n4\nO5\n6\n";
        const string theirs = "0\nT1\nT2\nT3\nT4\nT5\n6\n";
        var first = TextMergeEngine.Merge(@base, ours, theirs);
        var swapped = TextMergeEngine.Merge(@base, theirs, ours);
        Assert.Equal(TextMergeStatus.Conflicted, first.Status);
        var conflict = Assert.Single(first.Conflicts);
        Assert.Equal((1, 6), (conflict.BaseStartLine, conflict.BaseEndLine));
        var reverse = Assert.Single(swapped.Conflicts);
        Assert.Equal((conflict.BaseStartLine, conflict.BaseEndLine), (reverse.BaseStartLine, reverse.BaseEndLine));
        Assert.Equal(conflict.BaseText, reverse.BaseText);
        Assert.Equal(conflict.OursText, reverse.TheirsText);
        Assert.Equal(conflict.TheirsText, reverse.OursText);
    }

    [Fact]
    public void InvalidUnicodeAndOversizedOutputNeverReturnCleanValue()
    {
        var malformed = TextDiffEngine.Diff("\uD800", "x");
        Assert.Equal(TextDiffStatus.InvalidInput, malformed.Status);
        var fastPath = TextMergeEngine.Merge("long", "long", "long", new TextChangeOptions { MaxOutputBytes = 3 });
        Assert.Equal(TextMergeStatus.LimitExceeded, fastPath.Status);
        Assert.Null(fastPath.Value);
        var tooManyLines = TextMergeEngine.Merge("x\n", "a\nx\n", "x\nb\n",
            new TextChangeOptions { MaxInputBytes = 4, MaxLines = 2, MaxOutputBytes = 5 });
        Assert.Equal(TextMergeStatus.LimitExceeded, tooManyLines.Status);
        Assert.Null(tooManyLines.Value);
    }

    [Fact]
    public void DiffLimitsCoverInputLinesMatrixEditsAndReplacementOutput()
    {
        Assert.Equal(TextDiffStatus.LimitExceeded,
            TextDiffEngine.Diff("abcd", "x", new TextChangeOptions { MaxInputBytes = 3 }).Status);
        Assert.Equal(TextDiffStatus.LimitExceeded,
            TextDiffEngine.Diff("a\nb\n", "a\nb\n", new TextChangeOptions { MaxLines = 1 }).Status);
        Assert.Equal(TextDiffStatus.LimitExceeded,
            TextDiffEngine.Diff("a\nb\n", "A\nb\n", new TextChangeOptions { MaxMatrixBytes = 16 }).Status);
        Assert.Equal(TextDiffStatus.LimitExceeded,
            TextDiffEngine.Diff("a\nb\nc\n", "A\nb\nC\n", new TextChangeOptions { MaxEdits = 1 }).Status);
        Assert.Equal(TextDiffStatus.LimitExceeded,
            TextDiffEngine.Diff("a\n", "long\n", new TextChangeOptions { MaxOutputBytes = 2 }).Status);
    }

    [Fact]
    public void ConflictCountAndAggregateRegionBytesAreBounded()
    {
        const string @base = "a\nb\nc\nd\n";
        const string ours = "A\nb\nC\nd\n";
        const string theirs = "X\nb\nY\nd\n";
        Assert.Equal(TextMergeStatus.LimitExceeded,
            TextMergeEngine.Merge(@base, ours, theirs, new TextChangeOptions { MaxConflicts = 1 }).Status);
        Assert.Equal(TextMergeStatus.LimitExceeded,
            TextMergeEngine.Merge("a\n", "A\n", "B\n", new TextChangeOptions { MaxOutputBytes = 5 }).Status);
    }

    [Fact]
    public void OneSidedFastPathPreservesSnapshotProvenanceAndConflictListIsReadOnly()
    {
        var result = TextMergeEngine.Merge("a\n", "a\n", "b\n");
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal(result.Base!.Sha256, result.Ours!.Sha256);
        Assert.NotEqual(result.Base.Sha256, result.Theirs!.Sha256);
        Assert.Equal(result.Theirs.Sha256, result.Result!.Sha256);

        var conflict = TextMergeEngine.Merge("a\n", "A\n", "B\n");
        var list = Assert.IsAssignableFrom<IList<TextMergeConflict>>(conflict.Conflicts);
        Assert.Throws<NotSupportedException>(() => list[0] = conflict.Conflicts[0]);
    }

    [Fact]
    public void MergeWorkBudgetIsSharedAcrossBothDiffsAndGrouping()
    {
        var result = TextMergeEngine.Merge("a\nb\nc\nd\n", "A\nb\nc\nd\n", "a\nb\nc\nD\n",
            new TextChangeOptions { MaxWorkCells = 40 });
        Assert.Equal(TextMergeStatus.LimitExceeded, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void CancellationAndInvalidOptionsAreExplicit()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(TextMergeStatus.Cancelled, TextMergeEngine.Merge("a", "b", "c", cancellationToken: cts.Token).Status);
        Assert.Equal(TextMergeStatus.InvalidInput, TextMergeEngine.Merge("a", "b", "c", new TextChangeOptions { MaxLines = 0 }).Status);
    }

    private static string Apply(string original, IReadOnlyList<TextEdit> edits)
    {
        var bytes = Encoding.UTF8.GetBytes(original).ToList();
        foreach (var edit in edits.OrderByDescending(e => e.StartOffset))
        {
            var replacement = Encoding.UTF8.GetBytes(edit.Replacement);
            bytes.RemoveRange(edit.StartOffset, edit.DeleteLength);
            bytes.InsertRange(edit.StartOffset, replacement);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    // Independent test oracle retaining the original full-matrix profile and its
    // deletion-first traceback, used to guard exact repeated-line output parity.
    private static TextEdit[] FullMatrixReference(string before, string after)
    {
        var oldLines = SplitLines(before);
        var newLines = SplitLines(after);
        var n = oldLines.Length;
        var m = newLines.Length;
        var width = m + 1;
        var lcs = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            lcs[i * width + j] = oldLines[i] == newLines[j]
                ? lcs[(i + 1) * width + j + 1] + 1
                : Math.Max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);

        var lineEdits = new List<(int OldStart, int OldEnd, int NewStart, int NewEnd)>();
        var oldLine = 0;
        var newLine = 0;
        var editOldStart = -1;
        var editNewStart = -1;
        while (oldLine < n || newLine < m)
        {
            if (oldLine < n && newLine < m && oldLines[oldLine] == newLines[newLine])
            {
                Flush();
                oldLine++;
                newLine++;
                continue;
            }

            var deleteScore = oldLine < n ? lcs[(oldLine + 1) * width + newLine] : -1;
            var insertScore = newLine < m ? lcs[oldLine * width + newLine + 1] : -1;
            if (oldLine < n && (newLine == m || deleteScore >= insertScore))
            {
                if (editOldStart < 0) { editOldStart = oldLine; editNewStart = newLine; }
                oldLine++;
            }
            else
            {
                if (editOldStart < 0) { editOldStart = oldLine; editNewStart = newLine; }
                newLine++;
            }
        }
        Flush();

        var offsets = new int[n + 1];
        for (var i = 0; i < n; i++) offsets[i + 1] = offsets[i] + Encoding.UTF8.GetByteCount(oldLines[i]);
        return lineEdits.Select(e => new TextEdit(offsets[e.OldStart], offsets[e.OldEnd] - offsets[e.OldStart],
            string.Concat(newLines.Skip(e.NewStart).Take(e.NewEnd - e.NewStart)))).ToArray();

        void Flush()
        {
            if (editOldStart < 0) return;
            lineEdits.Add((editOldStart, oldLine, editNewStart, newLine));
            editOldStart = -1;
            editNewStart = -1;
        }
    }

    private static string[] SplitLines(string value)
    {
        if (value.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\n') continue;
            lines.Add(value.Substring(start, i + 1 - start));
            start = i + 1;
        }
        if (start < value.Length) lines.Add(value[start..]);
        return lines.ToArray();
    }
}
