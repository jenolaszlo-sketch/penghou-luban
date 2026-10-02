using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Resolution.Tests;

public sealed class PreviewRuntimeTests
{
    [Fact]
    public async Task ExactPatchCapturesByteAccurateProposalWithoutChangingWorkspace()
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes("AéB");
        workspace.WriteBytes("note.txt", original);
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var patch = new TextPatch(3, 1, Encoding.UTF8.GetBytes("!"));
        var (runtime, document, invocation) = Create(workspace, authority,
            [new FilePatchStage("note.txt", [patch], Version(original))]);

        var result = await runtime.WhatIfAsync(invocation, document);

        Assert.Equal(PreviewRunStatus.Succeeded, result.Status);
        var plan = Assert.IsType<ResolvedEffectPlan>(result.Plan);
        Assert.False(plan.CanCommit);
        var proposal = Assert.Single(Assert.Single(plan.Nodes).Proposals);
        Assert.Equal("note.txt", proposal.RelativePath);
        Assert.Equal("AéB", Encoding.UTF8.GetString(original));
        Assert.Equal(Sha256(Encoding.UTF8.GetBytes("Aé!")), proposal.ProposedSha256);
        Assert.Equal(4, proposal.ProposedByteLength);
        Assert.Equal(original, workspace.ReadBytes("note.txt"));
    }

    [Fact]
    public async Task DeniedLaterKnownTargetBlocksBeforeAnyProtectedRead()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("abc");
        workspace.WriteBytes("first.txt", bytes);
        workspace.WriteBytes("second.txt", bytes);
        var authority = new RecordingAuthority(r =>
            r.Phase == PreviewAuthorizationPhase.Preflight && r.ResourcePath == "second.txt"
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new FilePatchStage("first.txt", [new TextPatch(0, 1, Encoding.UTF8.GetBytes("A"))], Version(bytes)),
             new FilePatchStage("second.txt", [new TextPatch(0, 1, Encoding.UTF8.GetBytes("B"))], Version(bytes))]);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile);
    }

    [Fact]
    public async Task StaticPreflightDoesNotEnumerateOrReadDynamicRoot()
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new GlobPatchStage("does-not-exist", "*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 0, Encoding.UTF8.GetBytes("x"))], new TraversalLimits())]);

        var readiness = await runtime.PreflightAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.Succeeded, readiness.Status);
        Assert.True(readiness.HasDynamicTargets);
        Assert.All(authority.Requests, r => Assert.Equal(PreviewAuthorizationPhase.Preflight, r.Phase));
    }

    [Fact]
    public async Task DeniedDynamicTargetAdmissionBlocksContentReadsAcrossManifest()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("src/a.txt", bytes);
        workspace.WriteBytes("src/b.txt", bytes);
        var authority = new RecordingAuthority(r =>
            r.Phase == PreviewAuthorizationPhase.TargetAdmission && r.ResourcePath == "src/b.txt"
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new GlobPatchStage("src", "*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new TraversalLimits())]);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile);
    }

    [Fact]
    public async Task AllMatchesSelectionRemainsUnresolvedWithoutReadingFiles()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("src/a.txt", Encoding.UTF8.GetBytes("old"));
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new GlobPatchStage("src", "*.txt", PreviewSelection.AllMatches,
                [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new TraversalLimits(MaxEntries: 10, MaxMatches: 2)),
             new FilePatchStage("src/a.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))])],
            new PreviewLimits(MaxResourceCalls: 100));

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.Incomplete, result.Status);
        var nodes = Assert.IsType<ResolvedEffectPlan>(result.Plan).Nodes;
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, node =>
        {
            Assert.Equal(PreviewNodeState.Unresolved, node.State);
            Assert.Empty(node.Proposals);
        });
        Assert.Equal(PreviewUnresolvedReason.AllMatchCoverageUnavailable, nodes[0].UnresolvedReason);
        Assert.Equal(PreviewUnresolvedReason.DependsOnUnresolvedEffect, nodes[1].UnresolvedReason);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess);
    }

    [Fact]
    public async Task DeferredToolAndDependentNodesStayUnresolvedWithoutReads()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("unchanged");
        workspace.WriteBytes("src/a.txt", bytes);
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new DeferredToolStage(DeferredTool.Test, "."),
             new FilePatchStage("src/a.txt", [new TextPatch(0, 1, Encoding.UTF8.GetBytes("U"))], Version(bytes))]);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.Incomplete, result.Status);
        var nodes = Assert.IsType<ResolvedEffectPlan>(result.Plan).Nodes;
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => Assert.Equal(PreviewNodeState.Unresolved, n.State));
        Assert.Equal(PreviewUnresolvedReason.OpaqueEffect, nodes[0].UnresolvedReason);
        Assert.Equal(PreviewUnresolvedReason.DependsOnOpaqueEffect, nodes[1].UnresolvedReason);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile);
    }

    [Fact]
    public async Task IncompleteGlobSelectionHasNoProposedPrefix()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteBytes("src/a.txt", Encoding.UTF8.GetBytes("old"));
        workspace.WriteBytes("src/b.txt", Encoding.UTF8.GetBytes("old"));
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var limits = new PreviewLimits(MaxTargets: 1);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new GlobPatchStage("src", "*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new TraversalLimits(MaxMatches: 1))], limits);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.Incomplete, result.Status);
        var node = Assert.Single(Assert.IsType<ResolvedEffectPlan>(result.Plan).Nodes);
        Assert.Equal(PreviewNodeState.Unresolved, node.State);
        Assert.Equal(PreviewUnresolvedReason.IncompleteSelection, node.UnresolvedReason);
        Assert.Empty(node.Proposals);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile);
    }

    [Fact]
    public async Task StaleExpectedVersionReturnsNoPlanAndDoesNotChangeBytes()
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", original);
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new FilePatchStage("note.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new ResourceVersion("stale-version"))]);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.StaleObservation, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(original, workspace.ReadBytes("note.txt"));
    }

    [Theory]
    [InlineData(1, 1)] // Splits the UTF-8 encoding of é.
    [InlineData(int.MaxValue, 0)] // Overflows the actual byte range.
    public async Task InvalidUtf8BoundaryOrOverflowReturnsInvalidPatch(int start, int deleteLength)
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes("AéB");
        workspace.WriteBytes("note.txt", original);
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new FilePatchStage("note.txt", [new TextPatch(start, deleteLength, Encoding.UTF8.GetBytes("x"))], Version(original))]);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.InvalidPatch, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal(original, workspace.ReadBytes("note.txt"));
    }

    [Fact]
    public async Task DeniedFinalReleaseReturnsNoPlan()
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", original);
        var authority = new RecordingAuthority(r => r.Phase == PreviewAuthorizationPhase.Release
            ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new FilePatchStage("note.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(original))]);

        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.Release);
        Assert.Equal(original, workspace.ReadBytes("note.txt"));
    }

    [Theory]
    [InlineData(LanguageAuthorityStatus.Unavailable, false)]
    [InlineData(null, true)]
    public async Task PreflightUnavailableOrThrowingFailsClosed( LanguageAuthorityStatus? status, bool throws)
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => throws ? throw new InvalidOperationException("authority unavailable") : status!.Value);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new FilePatchStage("missing.txt", [new TextPatch(0, 0, Encoding.UTF8.GetBytes("x"))], new ResourceVersion("v"))]);

        var result = await runtime.PreflightAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.AuthorizationUnavailable, result.Status);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess);
    }

    [Fact]
    public async Task CompiledDocumentAndPlanOwnDefensiveCopiesOfPatchBytes()
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", original);
        var replacement = Encoding.UTF8.GetBytes("new");
        var patchList = new List<TextPatch> { new(0, 3, replacement) };
        var stage = new FilePatchStage("note.txt", patchList, Version(original));
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, [stage]);

        Array.Fill(replacement, (byte)'x');
        patchList.Clear();
        var result = await runtime.WhatIfAsync(invocation, doc);

        Assert.Equal(PreviewRunStatus.Succeeded, result.Status);
        var plan = Assert.IsType<ResolvedEffectPlan>(result.Plan);
        var captured = Assert.Single(Assert.Single(plan.Nodes).Proposals);
        var frozen = Assert.Single(captured.Patches).ReplacementUtf8;
        var copy = frozen.ToArray();
        Array.Fill(copy, (byte)'z');
        Assert.Equal(Encoding.UTF8.GetBytes("new"), frozen.ToArray());
        Assert.Equal(Sha256(Encoding.UTF8.GetBytes("new")), captured.ProposedSha256);
    }

    [Fact]
    public async Task PlanIdentityBindsInvocationTargetPayloadAndVersion()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", bytes);
        workspace.WriteBytes("b.txt", bytes);
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var a = Compile(workspace, [new FilePatchStage("a.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes))]).Document!;
        var b = Compile(workspace, [new FilePatchStage("b.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes))]).Document!;
        var changedPayload = Compile(workspace, [new FilePatchStage("a.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("NEW"))], Version(bytes))]).Document!;
        var noExpectedVersion = Compile(workspace, [new FilePatchStage("a.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))])]).Document!;
        var runtime = Runtime(workspace, authority);

        var p1 = (await runtime.WhatIfAsync(Invocation("attempt-one"), a)).Plan!;
        var p2 = (await runtime.WhatIfAsync(Invocation("attempt-two"), a)).Plan!;
        var p3 = (await runtime.WhatIfAsync(Invocation("attempt-one"), b)).Plan!;
        var p4 = (await runtime.WhatIfAsync(Invocation("attempt-one"), changedPayload)).Plan!;
        var forward = Compile(workspace,
            [new FilePatchStage("a.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes)),
             new FilePatchStage("b.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes))]).Document!;
        var reverse = Compile(workspace,
            [new FilePatchStage("b.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes)),
             new FilePatchStage("a.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes))]).Document!;
        var p6 = (await runtime.WhatIfAsync(Invocation("attempt-one"), forward)).Plan!;
        var p7 = (await runtime.WhatIfAsync(Invocation("attempt-one"), reverse)).Plan!;
        var observed1 = (await runtime.WhatIfAsync(Invocation("attempt-one"), noExpectedVersion)).Plan!;
        workspace.WriteBytes("a.txt", Encoding.UTF8.GetBytes("OLD"));
        var observed2 = (await runtime.WhatIfAsync(Invocation("attempt-one"), noExpectedVersion)).Plan!;

        Assert.NotEqual(p1.Identity, p2.Identity);
        Assert.NotEqual(p1.Identity, p3.Identity);
        Assert.NotEqual(p1.Identity, p4.Identity);
        Assert.NotEqual(p6.Identity, p7.Identity);
        Assert.NotEqual(observed1.Identity, observed2.Identity);
    }

    [Fact]
    public async Task ResourceBudgetAndByteLimitFailureReturnNoPlan()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("a longer original");
        workspace.WriteBytes("note.txt", bytes);
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var callLimited = Create(workspace, authority,
            [new GlobPatchStage("", "*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 1, Encoding.UTF8.GetBytes("A"))], new TraversalLimits(MaxEntries: 4, MaxMatches: 1))],
            new PreviewLimits(MaxResourceCalls: 4));
        var callResult = await callLimited.Runtime.WhatIfAsync(callLimited.Invocation, callLimited.Document);
        Assert.Equal(PreviewRunStatus.LimitExceeded, callResult.Status);
        Assert.Null(callResult.Plan);

        authority.Requests.Clear();
        var readLimited = Create(workspace, authority,
            [new FilePatchStage("note.txt", [new TextPatch(0, 1, Encoding.UTF8.GetBytes("A"))], Version(bytes))],
            new PreviewLimits(MaxReadBytes: 2, MaxFileBytes: 2));
        var readResult = await readLimited.Runtime.WhatIfAsync(readLimited.Invocation, readLimited.Document);
        Assert.Equal(PreviewRunStatus.LimitExceeded, readResult.Status);
        Assert.Null(readResult.Plan);

        authority.Requests.Clear();
        var planLimited = Create(workspace, authority,
            [new FilePatchStage("note.txt", [new TextPatch(0, 1, Encoding.UTF8.GetBytes("A"))], Version(bytes))],
            new PreviewLimits(MaxPlanBytes: 1024));
        var planResult = await planLimited.Runtime.WhatIfAsync(planLimited.Invocation, planLimited.Document);
        Assert.Equal(PreviewRunStatus.LimitExceeded, planResult.Status);
        Assert.Null(planResult.Plan);
        Assert.Equal(bytes, workspace.ReadBytes("note.txt"));
    }

    [Fact]
    public async Task CancellationDoesNotReturnPlanOrChangeWorkspace()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority,
            [new FilePatchStage("note.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], Version(bytes))]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runtime.WhatIfAsync(invocation, doc, cancellation.Token));
        Assert.Equal(bytes, workspace.ReadBytes("note.txt"));
    }

    private static (PreviewRuntime Runtime, CompiledPreviewDocument Document, EffectInvocation Invocation) Create(
        TestWorkspace workspace, RecordingAuthority authority, IReadOnlyList<PreviewStage> stages, PreviewLimits? limits = null)
    {
        var compilation = Compile(workspace, stages, limits);
        var document = compilation.Document ?? throw new Xunit.Sdk.XunitException("Expected preview to compile.");
        return (Runtime(workspace, authority), document, Invocation());
    }

    private static PreviewCompilation Compile(TestWorkspace workspace, IReadOnlyList<PreviewStage> stages, PreviewLimits? limits = null) =>
        PreviewCompiler.Compile(stages, workspace.Id, limits);

    private static PreviewRuntime Runtime(TestWorkspace workspace, RecordingAuthority authority) =>
        new(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), authority);

    private static EffectInvocation Invocation(string attempt = "attempt") => new("subject", "effect", attempt);

    private static ResourceVersion Version(byte[] bytes) => new("local-read-v1:sha256:" + Sha256(bytes));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class RecordingAuthority(Func<PreviewAuthorizationRequest, LanguageAuthorityStatus> decide) : IPreviewAuthorizer
    {
        public List<PreviewAuthorizationRequest> Requests { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new LanguageAuthorityDecision(decide(request)));
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-preview-" + Guid.NewGuid().ToString("N"));
        public WorkspaceId Id { get; } = new("preview-test-workspace");
        public string Root => _root;
        public TestWorkspace() => Directory.CreateDirectory(_root);
        public void WriteBytes(string relative, byte[] content)
        {
            var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test path escaped its temporary workspace.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        public byte[] ReadBytes(string relative) => File.ReadAllBytes(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
