using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class TextUnifiedRoundTripTests
{
    [Fact]
    public void ExportImportPreservesExactBytesAcrossContextWindows()
    {
        string[] inputs = ["", "a", "a\n", "a\r", "a\r\n", "\uFEFF😀\r\nx\ny",
            "a\nb\nc\nd\ne\nf\ng\nh\n", "a\na\nb\n", "\n\r\n", "--- x\n+++ y\n@@ z\n\\ No newline at end of file"];
        foreach (var before in inputs)
        foreach (var after in inputs)
        foreach (var context in new[] { 0, 1, 3 })
        {
            var export = TextUnifiedDiffRenderer.Render(before, after, "dir/a file.txt",
                new TextUnifiedDiffOptions { ContextLines = context });
            Assert.Equal(TextUnifiedDiffStatus.Succeeded, export.Status);
            var parsed = TextUnifiedImport.Parse(export.UnifiedDiff);
            Assert.True(parsed.Status == TextUnifiedImportStatus.Succeeded, $"Parse {context}: {parsed.ErrorCode}: {export.UnifiedDiff}");
            if (before == after)
            {
                Assert.Empty(parsed.Candidate!.Files);
                continue;
            }
            var materialized = TextUnifiedImport.Materialize(parsed.Candidate!,
                new Dictionary<string, string> { ["dir/a file.txt"] = before });
            Assert.True(materialized.Status == TextUnifiedMaterializationStatus.Succeeded, $"Materialize {context}: {materialized.ErrorCode}: {export.UnifiedDiff}");
            var file = Assert.Single(materialized.Files);
            Assert.Equal(after, file.ProposedText);
            Assert.Equal(export.Before!.Sha256, file.Before.Sha256);
            Assert.Equal(export.After!.Sha256, file.After.Sha256);
            var expected = TextDiffEngine.Diff(before, after);
            Assert.Equal(expected.Edits, file.Edits);
        }
    }
}
