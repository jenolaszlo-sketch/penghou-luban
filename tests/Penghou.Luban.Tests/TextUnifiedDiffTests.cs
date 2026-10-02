using System.Text;
using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class TextUnifiedDiffTests
{
    [Fact]
    public void RendersDeterministicHeadersHunkAndExactMixedTerminators()
    {
        const string before = "A\r\nB\nC";
        const string after = "A\r\nB2\nC";
        var result = TextUnifiedDiffRenderer.Render(before, after, "dir/a file.txt");

        Assert.Equal(TextUnifiedDiffStatus.Succeeded, result.Status);
        Assert.Equal(TextUnifiedDiffProfile.Identity, result.ProfileIdentity);
        Assert.Equal(TextChangeProfile.Identity, result.AlgorithmProfileIdentity);
        Assert.Equal("--- a/dir/a file.txt\n+++ b/dir/a file.txt\n@@ -1,3 +1,3 @@\n A\r\n-B\n+B2\n C\n\\ No newline at end of file\n", result.UnifiedDiff);
        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(new TextDisplayLineRange(0, 3), hunk.OldRange);
        Assert.Equal(new TextDisplayLineRange(0, 3), hunk.NewRange);
        Assert.Equal(" A\r\n-B\n+B2\n C\n\\ No newline at end of file\n", hunk.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(before), result.Before!.ByteLength);
        Assert.Equal(Encoding.UTF8.GetByteCount(after), result.After!.ByteLength);
    }

    [Fact]
    public void PreservesBomAndUnicodePayloadBytes()
    {
        const string before = "\uFEFFcafé 😀\r\nold\n";
        const string after = "\uFEFFcafé 😀\r\n新😀\n";
        var result = TextUnifiedDiffRenderer.Render(before, after, "unicode.txt");
        Assert.Equal(TextUnifiedDiffStatus.Succeeded, result.Status);
        Assert.Equal("--- a/unicode.txt\n+++ b/unicode.txt\n@@ -1,2 +1,2 @@\n \uFEFFcafé 😀\r\n-old\n+新😀\n", result.UnifiedDiff);
        Assert.Equal(Encoding.UTF8.GetBytes(result.UnifiedDiff!), new UTF8Encoding(false, true).GetBytes(result.UnifiedDiff!));
    }

    [Fact]
    public void ZeroContextUsesUnifiedZeroCountConventionsAndSupportsMultipleHunks()
    {
        var inserted = TextUnifiedDiffRenderer.Render("a\nb\n", "x\na\nb\n", "f.txt",
            new TextUnifiedDiffOptions { ContextLines = 0 });
        Assert.Equal(TextUnifiedDiffStatus.Succeeded, inserted.Status);
        Assert.Equal("--- a/f.txt\n+++ b/f.txt\n@@ -0,0 +1 @@\n+x\n", inserted.UnifiedDiff);
        Assert.Equal(new TextDisplayLineRange(0, 0), Assert.Single(inserted.Hunks).OldRange);

        var deleted = TextUnifiedDiffRenderer.Render("a\n", "", "f.txt",
            new TextUnifiedDiffOptions { ContextLines = 0 });
        Assert.Equal("--- a/f.txt\n+++ b/f.txt\n@@ -1 +0,0 @@\n-a\n", deleted.UnifiedDiff);
        Assert.Equal(new TextDisplayLineRange(0, 0), Assert.Single(deleted.Hunks).NewRange);

        var multiple = TextUnifiedDiffRenderer.Render("a\nb\nc\nd\ne\n", "A\nb\nc\nd\nE\n", "f.txt",
            new TextUnifiedDiffOptions { ContextLines = 0 });
        Assert.Equal(2, multiple.Hunks.Count);
        Assert.Contains("@@ -1 +1 @@\n-a\n+A\n", multiple.UnifiedDiff);
        Assert.Contains("@@ -5 +5 @@\n-e\n+E\n", multiple.UnifiedDiff);
    }

    [Fact]
    public void EqualInputsProduceNoFileSectionOrHunks()
    {
        var result = TextUnifiedDiffRenderer.Render("same\r\n", "same\r\n", "f.txt");
        Assert.Equal(TextUnifiedDiffStatus.Succeeded, result.Status);
        Assert.Equal(string.Empty, result.UnifiedDiff);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void LoneCarriageReturnRemainsPayloadBeforeMarkerFraming()
    {
        var result = TextUnifiedDiffRenderer.Render("old\r", "new\r", "f.txt",
            new TextUnifiedDiffOptions { ContextLines = 0 });
        Assert.Equal(TextUnifiedDiffStatus.Succeeded, result.Status);
        Assert.Equal("--- a/f.txt\n+++ b/f.txt\n@@ -1 +1 @@\n-old\r\n\\ No newline at end of file\n+new\r\n\\ No newline at end of file\n", result.UnifiedDiff);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/rooted.txt")]
    [InlineData("../outside.txt")]
    [InlineData("dir//file.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("dir/file\tname.txt")]
    [InlineData("dir/CON.txt")]
    [InlineData("dir/COM¹.txt")]
    [InlineData("dir/name?.txt")]
    public void UnsupportedPathNeverProducesPatch(string path)
    {
        var result = TextUnifiedDiffRenderer.Render("a", "b", path);
        Assert.Equal(TextUnifiedDiffStatus.Unsupported, result.Status);
        Assert.Null(result.UnifiedDiff);
        Assert.Empty(result.Hunks);
    }

    [Fact]
    public void WorkOutputAndHunkCapsReturnLimitExceededWithoutPartialPatch()
    {
        var workLimited = TextUnifiedDiffRenderer.Render("a\nb\n", "A\nB\n", "f.txt",
            new TextUnifiedDiffOptions { ChangeOptions = new TextChangeOptions { MaxWorkCells = 1 } });
        Assert.Equal(TextUnifiedDiffStatus.LimitExceeded, workLimited.Status);
        Assert.Null(workLimited.UnifiedDiff);

        var outputLimited = TextUnifiedDiffRenderer.Render(new string('a', 64 * 1024), new string('b', 64 * 1024), "f.txt",
            new TextUnifiedDiffOptions { MaxOutputBytes = 8 });
        Assert.Equal(TextUnifiedDiffStatus.LimitExceeded, outputLimited.Status);
        Assert.Null(outputLimited.UnifiedDiff);

        var hunkLimited = TextUnifiedDiffRenderer.Render("a\nb\nc\nd\ne\n", "A\nb\nc\nd\nE\n", "f.txt",
            new TextUnifiedDiffOptions { ContextLines = 0, MaxHunks = 1 });
        Assert.Equal(TextUnifiedDiffStatus.LimitExceeded, hunkLimited.Status);
        Assert.Null(hunkLimited.UnifiedDiff);
    }

    [Fact]
    public void InputAndCancellationStatusesStayExplicit()
    {
        Assert.Equal(TextUnifiedDiffStatus.InvalidInput,
            TextUnifiedDiffRenderer.Render("\uD800", "x", "f.txt").Status);
        Assert.Equal(TextUnifiedDiffStatus.InvalidInput,
            TextUnifiedDiffRenderer.Render("a", "b", "f.txt", new TextUnifiedDiffOptions { ContextLines = 101 }).Status);
        Assert.Equal(TextUnifiedDiffStatus.InvalidInput,
            TextUnifiedDiffRenderer.Render("a", "b", "f.txt", new TextUnifiedDiffOptions
            {
                ChangeOptions = new TextChangeOptions { MaxLines = 0 }
            }).Status);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(TextUnifiedDiffStatus.Cancelled,
            TextUnifiedDiffRenderer.Render("a", "b", "f.txt", cancellationToken: cts.Token).Status);
    }
}
