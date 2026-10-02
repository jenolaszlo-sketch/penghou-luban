using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class PreviewCompilerTests
{
    private static readonly WorkspaceId Workspace = new("ws");
    private static TextPatch Patch(int start = 0, int delete = 1, params byte[] bytes) => new(start, delete, bytes);

    [Fact]
    public void Exact_patch_is_normalized_and_frozen_with_provider_precondition()
    {
        var payload = new byte[] { (byte)'B' };
        var callerList = new List<TextPatch> { Patch(0, 1, payload) };
        var result = PreviewCompiler.Compile(new PreviewStage[]
            { new FilePatchStage("./Docs\\A.txt", callerList, new ResourceVersion("version-1")) }, Workspace);
        payload[0] = (byte)'X'; callerList.Clear();

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
        var operation = Assert.IsType<ExactPatchOperation>(result.Document!.Nodes[0].Operation);
        Assert.Equal("Docs/A.txt", operation.Path);
        Assert.Equal("version-1", operation.ExpectedVersion!.Value.Value);
        Assert.Equal(new byte[] { (byte)'B' }, operation.Patches[0].ReplacementUtf8.ToArray());
        Assert.Equal("files.patch", result.Document.Nodes[0].Descriptor);
    }

    [Fact]
    public void Alias_free_equivalent_paths_share_document_and_node_identity()
    {
        var upper = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("./Folder\\A.txt", [Patch(0, 1, 0x62)]) }, Workspace);
        var lower = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("folder/a.txt", [Patch(0, 1, 0x62)]) }, Workspace);
        Assert.True(upper.Succeeded);
        Assert.True(lower.Succeeded);
        Assert.Equal(upper.Document!.Identity, lower.Document!.Identity);
        Assert.Equal(upper.Document.Nodes[0].Identity, lower.Document.Nodes[0].Identity);
    }

    [Fact]
    public void Golden_document_and_node_vector_matches_independent_binary_vector()
    {
        var result = PreviewCompiler.Compile(new PreviewStage[]
            { new FilePatchStage("A.txt", [Patch(0, 1, 0x42)]) }, Workspace);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
        Assert.Equal("f9863b107047f7925f852b951f2ed3a7f391e0f1dd0d983294eaffd79a417d0e", result.Document!.Identity);
        Assert.Equal("06cf34965d6021555136135e9459d37618b3a40d0b448edc2008f56e4f9873fd", result.Document.Nodes[0].Identity);
    }

    [Fact]
    public void Workspace_limits_target_version_and_payload_are_identity_inputs()
    {
        var original = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 1, 0x61)]) }, Workspace);
        var payload = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 1, 0x62)]) }, Workspace);
        var version = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 1, 0x61)], new ResourceVersion("v2")) }, Workspace);
        var workspace = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 1, 0x61)]) }, new WorkspaceId("other"));
        var limits = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 1, 0x61)]) }, Workspace,
            new PreviewLimits(MaxReadBytes: 8 * 1024 * 1024));
        Assert.True(original.Succeeded);
        Assert.NotEqual(original.Document!.Identity, payload.Document!.Identity);
        Assert.NotEqual(original.Document.Identity, version.Document!.Identity);
        Assert.NotEqual(original.Document.Identity, workspace.Document!.Identity);
        Assert.NotEqual(original.Document.Identity, limits.Document!.Identity);
    }

    [Fact]
    public void Directory_identity_excludes_random_continuation_but_binds_versions_and_completeness()
    {
        var entries = new[] { new DirectoryEntry("A.txt", false, 3, new ResourceVersion("v1")) };
        var a = PreviewIdentity.Directory(new DirectoryPage(entries, true, new DirectoryContinuation("opaque-a"), null));
        var same = PreviewIdentity.Directory(new DirectoryPage(entries, true, new DirectoryContinuation("opaque-b"), null));
        var changed = PreviewIdentity.Directory(new DirectoryPage(
            [new DirectoryEntry("A.txt", false, 3, new ResourceVersion("v2"))], true, null, null));
        var incomplete = PreviewIdentity.Directory(new DirectoryPage(entries, false, null, DirectoryTruncationReason.EntryLimit));
        Assert.Equal(a, same);
        Assert.NotEqual(a, changed);
        Assert.NotEqual(a, incomplete);
    }

    [Fact]
    public void Plan_identity_binds_invocation_observations_coverage_and_proposals()
    {
        var compilation = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 1, 0x42)]) }, Workspace);
        Assert.True(compilation.Succeeded);
        var node = compilation.Document!.Nodes[0];
        var frozenPatch = Assert.IsType<ExactPatchOperation>(node.Operation).Patches;
        var observation = new PreviewObservation(node.Identity, "a", ResourceAction.ReadFile, new RequestIdentity("request-1"),
            new ResourceVersion("provider-v1"), "original-digest", 1, true);
        var proposal = new CapturedFilePatch("a", new ResourceVersion("provider-v1"), "original-digest", 1,
            "proposed-digest", 1, frozenPatch);
        var resolved = new ResolvedPreviewNode(node.Identity, node.Descriptor, PreviewNodeState.Proposed, PreviewSelection.AuthorizedView,
            [proposal], [], null);
        var invocation = new EffectInvocation("subject", "effect", "attempt");
        var plan = PreviewIdentity.Plan(invocation, compilation.Document, [observation], [resolved]);
        var changed = PreviewIdentity.Plan(invocation with { AttemptId = "attempt-2" }, compilation.Document, [observation], [resolved]);
        var incompleteObservation = observation with { IsComplete = false };
        var incomplete = PreviewIdentity.Plan(invocation, compilation.Document, [incompleteObservation], [resolved]);
        Assert.NotEqual(plan, changed);
        Assert.NotEqual(plan, incomplete);
    }

    [Fact]
    public void Invalid_patches_selections_versions_tools_and_limits_are_rejected()
    {
        AssertRejected(new FilePatchStage("a", []));
        AssertRejected(new FilePatchStage("a", [Patch(2, 1, 0x61), Patch(1, 0, 0x62)]));
        AssertRejected(new FilePatchStage("a", [Patch(-1, 0, 0x61)]));
        AssertRejected(new FilePatchStage("a", [Patch(int.MaxValue, 1, 0x61)]));
        AssertRejected(new FilePatchStage("a", [Patch(0, 0, 0xff)]));
        AssertRejected(new GlobPatchStage("src", "**/*.cs", PreviewSelection.Unspecified, [Patch()], new TraversalLimits()));
        AssertRejected(new GlobPatchStage("src", "**/*.cs", (PreviewSelection)44, [Patch()], new TraversalLimits()));
        AssertRejected(new DeferredToolStage((DeferredTool)99, "src"));
        AssertRejected(new FilePatchStage("a", [Patch()], new ResourceVersion("bad\nversion")));
        AssertRejected(new FilePatchStage("a", [Patch()]), new PreviewLimits(MaxReadBytes: 20 * 1024 * 1024));
        AssertRejected(new FilePatchStage("a", [Patch()]), new PreviewLimits(MaxFileBytes: 2 * 1024 * 1024, MaxReadBytes: 1 * 1024 * 1024));
        AssertRejected(new GlobPatchStage("src", "**", PreviewSelection.AuthorizedView, [Patch()], new TraversalLimits(MaxDepth: 17)));
    }

    [Fact]
    public void Duplicate_exact_targets_are_rejected_case_insensitively()
    {
        var result = PreviewCompiler.Compile(new PreviewStage[]
        {
            new FilePatchStage("Src/A.txt", [Patch()]),
            new FilePatchStage("src\\a.txt", [Patch()])
        }, Workspace);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "PREVIEW_DUPLICATE_TARGET");
    }

    [Fact]
    public void Target_count_and_declared_byte_budgets_are_enforced()
    {
        var tooManyTargets = PreviewCompiler.Compile(new PreviewStage[]
        {
            new GlobPatchStage("src", "**", PreviewSelection.AuthorizedView, [Patch()], new TraversalLimits(MaxMatches: 1000)),
            new FilePatchStage("a", [Patch()])
        }, Workspace);
        var replacementBudget = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch(0, 0, new byte[32])]) }, Workspace,
            new PreviewLimits(MaxReplacementBytes: 16));
        var patchBudget = PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", Enumerable.Range(0, 3).Select(i => Patch(i, 0, 0x61)).ToArray()) }, Workspace,
            new PreviewLimits(MaxPatchCount: 2));
        Assert.Contains(tooManyTargets.Diagnostics, d => d.Code == "PREVIEW_TARGET_LIMIT");
        Assert.Contains(replacementBudget.Diagnostics, d => d.Code == "PREVIEW_REPLACEMENT_LIMIT");
        Assert.Contains(patchBudget.Diagnostics, d => d.Code == "PREVIEW_PATCH_COUNT");
    }

    [Fact]
    public void Snapshot_uses_bounded_indexed_lists_and_unknown_stages_fail_closed()
    {
        var indexed = new IndexedStages(new PreviewStage[] { new FilePatchStage("a", new IndexedPatches([Patch()])) });
        var result = PreviewCompiler.Compile(indexed, Workspace);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(d => d.Code)));
        var unknown = PreviewCompiler.Compile(new PreviewStage[] { new UnknownStage() }, Workspace);
        Assert.False(unknown.Succeeded);
        Assert.Contains(unknown.Diagnostics, d => d.Code == "PREVIEW_UNKNOWN_STAGE");
    }

    [Fact]
    public void Empty_documents_and_cancellation_are_handled()
    {
        var empty = PreviewCompiler.Compile(Array.Empty<PreviewStage>(), Workspace);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Contains(empty.Diagnostics, d => d.Code == "PREVIEW_EMPTY");
        Assert.Throws<OperationCanceledException>(() => PreviewCompiler.Compile(new PreviewStage[] { new FilePatchStage("a", [Patch()]) }, Workspace, cancellationToken: cts.Token));
    }

    private static void AssertRejected(PreviewStage stage, PreviewLimits? limits = null)
    {
        var result = PreviewCompiler.Compile(new[] { stage }, Workspace, limits);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Diagnostics);
    }

    private sealed record UnknownStage : PreviewStage;
    private sealed class IndexedStages(PreviewStage[] items) : IReadOnlyList<PreviewStage>
    {
        public int Count => items.Length;
        public PreviewStage this[int index] => items[index];
        public IEnumerator<PreviewStage> GetEnumerator() => throw new InvalidOperationException("The compiler must use indexed access.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IndexedPatches(TextPatch[] items) : IReadOnlyList<TextPatch>
    {
        public int Count => items.Length;
        public TextPatch this[int index] => items[index];
        public IEnumerator<TextPatch> GetEnumerator() => throw new InvalidOperationException("The compiler must use indexed access.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
