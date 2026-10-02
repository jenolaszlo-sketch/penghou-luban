using System.Text;
using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class TextUnifiedImportTests
{
    [Fact]
    public void Parses_and_materializes_exact_existing_file_modification()
    {
        const string patch = "--- a/src/File.cs\n+++ b/src/File.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var parsed = TextUnifiedImport.Parse(patch);
        Assert.Equal(TextUnifiedImportStatus.Succeeded, parsed.Status);
        var file = Assert.Single(parsed.Candidate!.Files);
        Assert.Equal("src/File.cs", file.RelativePath);
        Assert.Equal((1, 1, 1, 1), (Assert.Single(file.Hunks).OldStart, file.Hunks[0].OldCount, file.Hunks[0].NewStart, file.Hunks[0].NewCount));

        var materialized = TextUnifiedImport.Materialize(parsed.Candidate,
            new Dictionary<string, string> { ["src/File.cs"] = "old\n" });
        Assert.Equal(TextUnifiedMaterializationStatus.Succeeded, materialized.Status);
        var result = Assert.Single(materialized.Files);
        Assert.Equal("new\n", result.ProposedText);
        Assert.Equal(Encoding.UTF8.GetByteCount("old\n"), result.Before.ByteLength);
        Assert.Equal(Encoding.UTF8.GetByteCount("new\n"), result.After.ByteLength);
        Assert.Equal("new\n", Apply("old\n", result.Edits));
    }

    [Fact]
    public void Exact_materialization_preserves_mixed_terminators_lone_cr_bom_and_marker_lines()
    {
        var before = "\uFEFFA\r\nold\nlone\r";
        var after = "\uFEFFA\r\nnew\nlone\r";
        var rendered = TextUnifiedDiffRenderer.Render(before, after, "dir/file name.txt");
        Assert.Equal(TextUnifiedDiffStatus.Succeeded, rendered.Status);
        var parsed = TextUnifiedImport.Parse(rendered.UnifiedDiff);
        Assert.Equal(TextUnifiedImportStatus.Succeeded, parsed.Status);
        var result = TextUnifiedImport.Materialize(parsed.Candidate,
            new Dictionary<string, string> { ["dir/file name.txt"] = before });
        Assert.Equal(TextUnifiedMaterializationStatus.Succeeded, result.Status);
        Assert.Equal(after, Assert.Single(result.Files).ProposedText);

        const string noFinalNewlinePatch = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n";
        var noFinalNewline = TextUnifiedImport.Parse(noFinalNewlinePatch);
        Assert.Equal(TextUnifiedImportStatus.Succeeded, noFinalNewline.Status);
        var exact = TextUnifiedImport.Materialize(noFinalNewline.Candidate,
            new Dictionary<string, string> { ["f"] = "old" });
        Assert.Equal(TextUnifiedMaterializationStatus.Succeeded, exact.Status);
        Assert.Equal("new", Assert.Single(exact.Files).ProposedText);
    }

    [Fact]
    public void No_final_newline_marker_is_consumed_after_each_body_line()
    {
        const string patch = "--- a/f\n+++ b/f\n@@ -1 +1,2 @@\n-old\n\\ No newline at end of file\n+new\n+tail\n";
        var result = TextUnifiedImport.Parse(patch);
        Assert.Equal(TextUnifiedImportStatus.Succeeded, result.Status);
        var lines = Assert.Single(Assert.Single(result.Candidate!.Files).Hunks).Lines;
        Assert.True(lines[0].MarkedNoNewline);
        Assert.False(lines[1].MarkedNoNewline);
    }

    [Fact]
    public void Renderer_round_trip_preserves_lines_that_look_like_headers_and_hunks()
    {
        const string before = "--- a/strange\n+++ b/strange\n@@ -1 +1 @@\n\\ No newline at end of file\n";
        const string after = "--- a/strange\n+++ b/strange\n@@ -1 +1 @@\n\\ No newline at end of file\nchanged\n";
        var rendered = TextUnifiedDiffRenderer.Render(before, after, "space path/file.txt");
        Assert.Equal(TextUnifiedDiffStatus.Succeeded, rendered.Status);
        var parsed = TextUnifiedImport.Parse(rendered.UnifiedDiff);
        Assert.Equal(TextUnifiedImportStatus.Succeeded, parsed.Status);
        var materialized = TextUnifiedImport.Materialize(parsed.Candidate,
            new Dictionary<string, string> { ["space path/file.txt"] = before });
        Assert.Equal(TextUnifiedMaterializationStatus.Succeeded, materialized.Status);
        Assert.Equal(after, Assert.Single(materialized.Files).ProposedText);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/drive")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a/file:stream")]
    [InlineData("a/CON.txt")]
    [InlineData("a/COM¹.log")]
    [InlineData("a/trailing.")]
    [InlineData("a/name\tfile")]
    public void Rooted_traversal_ads_and_windows_device_paths_are_rejected(string path)
    {
        var result = TextUnifiedImport.Parse($"--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-a\n+b\n");
        Assert.Equal(TextUnifiedImportStatus.InvalidInput, result.Status);
        Assert.Null(result.Candidate);
    }

    [Theory]
    [InlineData("--- /dev/null\n+++ b/new\n@@ -0,0 +1 @@\n+x\n")]
    [InlineData("--- a/old\n+++ /dev/null\n@@ -1 +0,0 @@\n-x\n")]
    [InlineData("diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n")]
    [InlineData("--- a/f\n+++ b/f\nold mode 100644\n@@ -1 +1 @@\n-a\n+b\n")]
    [InlineData("--- a/f\n+++ b/f\nGIT binary patch\n")]
    [InlineData("--- a/f\n+++ b/f\n@@@ -1,1 -1,1 +1,1 @@@\n")]
    public void Creation_deletion_git_mode_binary_and_combined_forms_are_unsupported(string patch)
    {
        var result = TextUnifiedImport.Parse(patch);
        Assert.Equal(TextUnifiedImportStatus.Unsupported, result.Status);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void A_different_old_and_new_target_is_an_unsupported_rename()
    {
        const string patch = "--- a/old\n+++ b/new\n@@ -1 +1 @@\n-a\n+b\n";
        var result = TextUnifiedImport.Parse(patch);
        Assert.Equal(TextUnifiedImportStatus.Unsupported, result.Status);
        Assert.Equal("RenameUnsupported", result.ErrorCode);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void Malformed_counts_and_ranges_fail_as_a_whole()
    {
        var zeroStart = TextUnifiedImport.Parse("--- a/f\n+++ b/f\n@@ -0 +1 @@\n-a\n+b\n");
        Assert.Equal("InvalidHunkRange", zeroStart.ErrorCode);
        var truncated = TextUnifiedImport.Parse("--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n");
        Assert.Equal("TruncatedHunk", truncated.ErrorCode);
        var extraOld = TextUnifiedImport.Parse("--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n-b\n+c\n");
        Assert.Equal("HunkCountExceeded", extraOld.ErrorCode);
        Assert.All(new[] { zeroStart, truncated, extraOld }, result =>
        {
            Assert.Equal(TextUnifiedImportStatus.InvalidInput, result.Status);
            Assert.Null(result.Candidate);
        });
    }

    [Fact]
    public void Duplicate_casefolded_targets_and_overlapping_hunks_are_rejected()
    {
        const string duplicate = "--- a/F\n+++ b/F\n@@ -1 +1 @@\n-a\n+b\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.Equal("DuplicateTarget", TextUnifiedImport.Parse(duplicate).ErrorCode);

        const string overlap = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n@@ -1 +1 @@\n-a\n+c\n";
        Assert.Equal("OverlappingHunks", TextUnifiedImport.Parse(overlap).ErrorCode);
    }

    [Theory]
    [InlineData("bad marker", "InvalidHunkLine")]
    [InlineData("\\ No newline at end of file", "MisplacedNoNewlineMarker")]
    public void Misplaced_markers_and_unconsumed_lines_are_rejected(string extra, string expectedCode)
    {
        var patch = $"--- a/f\n+++ b/f\n@@ -1 +1 @@\n{extra}\n";
        var result = TextUnifiedImport.Parse(patch);
        Assert.Equal(TextUnifiedImportStatus.InvalidInput, result.Status);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public void Stale_context_and_invalid_final_marker_return_no_partial_materialization()
    {
        const string patch = "--- a/a\n+++ b/a\n@@ -1 +1 @@\n-a\n+A\n--- a/b\n+++ b/b\n@@ -1 +1 @@\n-b\n+B\n";
        var candidate = TextUnifiedImport.Parse(patch).Candidate!;
        var stale = TextUnifiedImport.Materialize(candidate, new Dictionary<string, string> { ["a"] = "wrong\n", ["b"] = "b\n" });
        Assert.Equal(TextUnifiedMaterializationStatus.Stale, stale.Status);
        Assert.Empty(stale.Files);

        const string markedNonfinal = "--- a/f\n+++ b/f\n@@ -1 +1,2 @@\n-a\n+A\n\\ No newline at end of file\n+tail\n";
        var malformed = TextUnifiedImport.Materialize(TextUnifiedImport.Parse(markedNonfinal).Candidate,
            new Dictionary<string, string> { ["f"] = "a\n" });
        Assert.Equal(TextUnifiedMaterializationStatus.InvalidInput, malformed.Status);
        Assert.Empty(malformed.Files);
    }

    [Fact]
    public void Appending_after_an_unchanged_unterminated_source_token_is_rejected()
    {
        const string patch = "--- a/f\n+++ b/f\n@@ -1,0 +2 @@\n+b\n";
        var result = TextUnifiedImport.Materialize(TextUnifiedImport.Parse(patch).Candidate,
            new Dictionary<string, string> { ["f"] = "a" });
        Assert.Equal(TextUnifiedMaterializationStatus.InvalidInput, result.Status);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Materialization_rechecks_lowered_parse_caps_and_text_options()
    {
        const string patch = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n";
        var candidate = TextUnifiedImport.Parse(patch).Candidate!;
        Assert.Equal(TextUnifiedMaterializationStatus.LimitExceeded,
            TextUnifiedImport.Materialize(candidate, new Dictionary<string, string> { ["f"] = "a\n" },
                new TextUnifiedImportOptions { MaxPayloadBytes = 1 }).Status);
        Assert.Equal(TextUnifiedMaterializationStatus.InvalidInput,
            TextUnifiedImport.Materialize(candidate, new Dictionary<string, string> { ["f"] = "a\n" },
                textOptions: new TextChangeOptions { MaxLines = 0 }).Status);
    }

    [Fact]
    public void Materialization_shares_text_diff_work_and_bounds_proposed_bytes_before_building()
    {
        const string twoFiles = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n--- a/g\n+++ b/g\n@@ -1 +1 @@\n-c\n+d\n";
        var sharedBudget = TextUnifiedImport.Materialize(TextUnifiedImport.Parse(twoFiles).Candidate,
            new Dictionary<string, string> { ["f"] = "a\n", ["g"] = "c\n" },
            textOptions: new TextChangeOptions { MaxWorkCells = 4 });
        Assert.Equal(TextUnifiedMaterializationStatus.LimitExceeded, sharedBudget.Status);
        Assert.Empty(sharedBudget.Files);

        const string expanded = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+bc\n";
        var outputLimit = TextUnifiedImport.Materialize(TextUnifiedImport.Parse(expanded).Candidate,
            new Dictionary<string, string> { ["f"] = "a\n" },
            textOptions: new TextChangeOptions { MaxInputBytes = 2 });
        Assert.Equal(TextUnifiedMaterializationStatus.LimitExceeded, outputLimit.Status);
        Assert.Empty(outputLimit.Files);
    }

    [Fact]
    public void Independent_limits_and_cancellation_are_explicit()
    {
        const string patch = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.Equal(TextUnifiedImportStatus.LimitExceeded,
            TextUnifiedImport.Parse(patch, new TextUnifiedImportOptions { MaxInputBytes = 10 }).Status);
        Assert.Equal(TextUnifiedImportStatus.LimitExceeded,
            TextUnifiedImport.Parse(patch, new TextUnifiedImportOptions { MaxLines = 2 }).Status);
        const string twoFiles = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+b\n--- a/g\n+++ b/g\n@@ -1 +1 @@\n-a\n+b\n";
        Assert.Equal(TextUnifiedImportStatus.LimitExceeded,
            TextUnifiedImport.Parse(twoFiles, new TextUnifiedImportOptions { MaxFiles = 1 }).Status);
        Assert.Equal(TextUnifiedImportStatus.LimitExceeded,
            TextUnifiedImport.Parse(patch, new TextUnifiedImportOptions { MaxPayloadBytes = 1 }).Status);
        const string twoHunks = "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\n+A\n@@ -3 +3 @@\n-c\n+C\n";
        Assert.Equal(TextUnifiedImportStatus.LimitExceeded,
            TextUnifiedImport.Parse(twoHunks, new TextUnifiedImportOptions { MaxHunks = 1 }).Status);
        Assert.Equal(TextUnifiedImportStatus.LimitExceeded,
            TextUnifiedImport.Parse(patch, new TextUnifiedImportOptions { MaxWorkUnits = 1 }).Status);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(TextUnifiedImportStatus.Cancelled, TextUnifiedImport.Parse(patch, cancellationToken: cts.Token).Status);
        Assert.Equal(TextUnifiedMaterializationStatus.Cancelled,
            TextUnifiedImport.Materialize(TextUnifiedImport.Parse(patch).Candidate,
                new Dictionary<string, string> { ["f"] = "a\n" }, cancellationToken: cts.Token).Status);
    }

    [Fact]
    public void Empty_and_renderer_equal_patch_round_trip_without_inventing_targets()
    {
        var equal = TextUnifiedDiffRenderer.Render("same\r\n", "same\r\n", "f");
        var parsed = TextUnifiedImport.Parse(equal.UnifiedDiff);
        Assert.Equal(TextUnifiedImportStatus.Succeeded, parsed.Status);
        Assert.Empty(parsed.Candidate!.Files);
        Assert.Equal(TextUnifiedMaterializationStatus.Succeeded, TextUnifiedImport.Materialize(parsed.Candidate,
            new Dictionary<string, string>()).Status);
    }

    private static string Apply(string original, IReadOnlyList<TextEdit> edits)
    {
        var bytes = Encoding.UTF8.GetBytes(original).ToList();
        foreach (var edit in edits.OrderByDescending(edit => edit.StartOffset))
        {
            bytes.RemoveRange(edit.StartOffset, edit.DeleteLength);
            bytes.InsertRange(edit.StartOffset, Encoding.UTF8.GetBytes(edit.Replacement));
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }
}
