using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Execution.Tests;

public sealed class SinglePatchExecutorTests
{
    [Theory]
    [InlineData(LocalPatchNamespace.Unspecified)]
    [InlineData((LocalPatchNamespace)999)]
    public async Task UnsupportedNamespaceIsRejectedBeforeHostAdmission(LocalPatchNamespace namespaceProfile)
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var host = new RecordingHost();
        var executor = new SinglePatchExecutor(new WorkspaceReference(workspace.Id.Value, workspace.Root), host, namespaceProfile);

        var result = await executor.ExecuteAsync(capture.Document, capture.Plan, "op-unsupported-namespace");

        Assert.Equal(ResourceFailureKind.Unsupported, result.Failure);
        Assert.Empty(host.Events);
        Assert.Equal(old, workspace.ReadBytes("note.txt"));
    }

    [Fact]
    public async Task WholePlanAdmissionDenialPreventsProviderAccessAndStart()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var host = new RecordingHost { AdmissionStatus = LanguageAuthorityStatus.Deny };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-denied");

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Equal(old, workspace.ReadBytes("note.txt"));
        Assert.Equal(new[] { "admit" }, host.Events);
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.CompletionCalls);
    }

    [Fact]
    public async Task RevokedFinalPatchResourceCheckPreventsStart()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var patchFileChecks = 0;
        var host = new RecordingHost
        {
            ResourceDecision = check => check.Resource.Action == ResourceAction.PatchFile && ++patchFileChecks == 2
                ? AuthorizationStatus.Deny : AuthorizationStatus.Permit
        };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-revoked");

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Equal(old, workspace.ReadBytes("note.txt"));
        Assert.Equal(2, host.ResourceChecks.Count(c => c.Resource.Action == ResourceAction.PatchFile));
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.CompletionCalls);
    }

    [Fact]
    public async Task ChangedOriginalVersionFailsPreconditionBeforeMutationStart()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var changed = Encoding.UTF8.GetBytes("OLD");
        workspace.WriteBytes("note.txt", changed);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-stale");

        Assert.Equal(ResourceFailureKind.PreconditionFailed, result.Failure);
        Assert.Equal(changed, workspace.ReadBytes("note.txt"));
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.CompletionCalls);
    }

    [Fact]
    public async Task CombinedOriginalAndProposedReadBudgetIsCheckedBeforeAdmission()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var limits = new PreviewLimits(MaxReadBytes: 3, MaxFileBytes: 3);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")], limits);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-byte-budget");

        Assert.Equal(ResourceFailureKind.TooLarge, result.Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(old, workspace.ReadBytes("note.txt"));
    }

    [Fact]
    public async Task SuccessfulPatchOrdersAdmissionResourceChecksStartAndCompletion()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-success");

        Assert.True(result.Succeeded);
        Assert.Equal(Encoding.UTF8.GetBytes("new"), workspace.ReadBytes("note.txt"));
        Assert.Equal("admit", host.Events[0]);
        var start = host.Events.IndexOf("start");
        var complete = host.Events.IndexOf("complete");
        Assert.True(start > 1);
        Assert.True(complete > start);
        Assert.All(host.Events.Skip(1).Take(start - 1), e => Assert.StartsWith("resource:", e));
        Assert.Equal("complete", host.Events[^1]);
        Assert.Equal("local-read-v1:sha256:" + Sha256(Encoding.UTF8.GetBytes("new")), host.LastStart!.Mutation.ProposedVersion.Value);
        Assert.Equal("local-read-v1:sha256:" + Sha256(Encoding.UTF8.GetBytes("new")), result.Version!.Value.Value);
        Assert.Equal(MutationOutcome.Completed, host.LastCompletion!.Mutation.Outcome);
        Assert.Equal(host.LastStart.Mutation, host.LastCompletion.Mutation.Start);
        Assert.True(host.AdmissionCalls + host.ResourceChecks.Count + host.StartCalls + host.CompletionCalls <= capture.Plan.Limits.MaxResourceCalls);
    }

    [Theory]
    [InlineData(MutationStartStatus.Deny)]
    [InlineData(MutationStartStatus.Unavailable)]
    [InlineData(MutationStartStatus.AlreadyStarted)]
    public async Task NonStartedHostDecisionNeverMutatesOrCompletes(MutationStartStatus status)
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var host = new RecordingHost { StartDecision = _ => new(status) };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-start-state");

        Assert.False(result.Succeeded);
        Assert.Equal(old, workspace.ReadBytes("note.txt"));
        Assert.Equal(1, host.StartCalls);
        Assert.Equal(0, host.CompletionCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task StartedWithoutValidEvidenceNeverMutates(string evidence)
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        var host = new RecordingHost { StartDecision = _ => new(MutationStartStatus.Started, evidence) };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-invalid-evidence");

        Assert.False(result.Succeeded);
        Assert.Equal(old, workspace.ReadBytes("note.txt"));
        Assert.Equal(0, host.CompletionCalls);
    }

    [Fact]
    public async Task CompletionFailureIsAmbiguousAndRepeatedOperationIsRejectedBeforeRepatch()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var host = new RecordingHost { CompleteDecision = _ => false };
        var first = await Capture(workspace, [Patch("note.txt", old, 3, 0, "!")]);

        var result = await Executor(workspace, host).ExecuteAsync(first.Document, first.Plan, "op-duplicate");

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Equal(Encoding.UTF8.GetBytes("old!"), workspace.ReadBytes("note.txt"));
        Assert.Equal(1, host.CompletionCalls);

        var current = workspace.ReadBytes("note.txt");
        var retry = await Capture(workspace, [Patch("note.txt", current, current.Length, 0, "!")]);
        var retryResult = await Executor(workspace, host).ExecuteAsync(retry.Document, retry.Plan, "op-duplicate");

        Assert.False(retryResult.Succeeded);
        Assert.Equal(current, workspace.ReadBytes("note.txt"));
        Assert.Equal(2, host.StartCalls);
        Assert.Equal(1, host.CompletionCalls);
    }

    [Fact]
    public async Task CallerCancellationAfterStartedRecordsNoMutationBeforePropagating()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", old);
        var capture = await Capture(workspace, [Patch("note.txt", old, 0, 3, "new")]);
        using var cancellation = new CancellationTokenSource();
        var host = new RecordingHost { OnStart = _ => cancellation.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "op-cancel-after-start", cancellation.Token));

        Assert.Equal(old, workspace.ReadBytes("note.txt"));
        Assert.Equal(1, host.StartCalls);
        Assert.Equal(1, host.CompletionCalls);
        Assert.Equal(MutationOutcome.NoMutation, host.LastCompletion!.Mutation.Outcome);
    }

    [Fact]
    public async Task WorkspaceAndDocumentMismatchAreRejectedBeforeHostAdmission()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", bytes);
        workspace.WriteBytes("other.txt", bytes);
        var capture = await Capture(workspace, [Patch("note.txt", bytes, 0, 3, "new")]);
        var otherDocCapture = await Capture(workspace, [Patch("other.txt", bytes, 0, 3, "new")]);
        var host = new RecordingHost();
        var executor = Executor(workspace, host);

        var wrongDoc = await executor.ExecuteAsync(otherDocCapture.Document, capture.Plan, "op-wrong-doc");
        var wrongWorkspace = await new SinglePatchExecutor(new WorkspaceReference("different-id", workspace.Root), host, LocalPatchNamespace.HostControlled)
            .ExecuteAsync(capture.Document, capture.Plan, "op-wrong-workspace");

        Assert.Equal(ResourceFailureKind.InvalidRequest, wrongDoc.Failure);
        Assert.Equal(ResourceFailureKind.InvalidRequest, wrongWorkspace.Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Equal(bytes, workspace.ReadBytes("note.txt"));
        Assert.Equal(bytes, workspace.ReadBytes("other.txt"));
    }

    [Fact]
    public async Task SelfConsistentPlanCannotRetargetProposalAwayFromCompiledOperation()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", bytes);
        workspace.WriteBytes("other.txt", bytes);
        var capture = await Capture(workspace, [Patch("note.txt", bytes, 0, 3, "new")]);
        var resolved = Assert.Single(capture.Plan.Nodes);
        var forgedProposal = Assert.Single(resolved.Proposals) with { RelativePath = "other.txt" };
        var forgedNode = resolved with { Proposals = Array.AsReadOnly(new[] { forgedProposal }) };
        var forgedNodes = Array.AsReadOnly(new[] { forgedNode });
        var forgedIdentity = PreviewIdentity.Plan(capture.Invocation, capture.Document, capture.Plan.Observations, forgedNodes);
        var forgedPlan = new ResolvedEffectPlan(capture.Invocation, capture.Document, forgedIdentity, capture.Plan.Observations, forgedNodes);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, forgedPlan, "op-forged-target");

        Assert.Equal(ResourceFailureKind.InvalidRequest, result.Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Equal(bytes, workspace.ReadBytes("note.txt"));
        Assert.Equal(bytes, workspace.ReadBytes("other.txt"));
    }

    [Fact]
    public async Task ProposedHashSubstitutionIsRejectedAtStartBridgeBeforeMutation()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("note.txt", bytes);
        var capture = await Capture(workspace, [Patch("note.txt", bytes, 0, 3, "new")]);
        var resolved = Assert.Single(capture.Plan.Nodes);
        var originalProposal = Assert.Single(resolved.Proposals);
        var forgedProposal = originalProposal with { ProposedSha256 = Sha256(Encoding.UTF8.GetBytes("BAD")) };
        var forgedNode = resolved with { Proposals = Array.AsReadOnly(new[] { forgedProposal }) };
        var forgedNodes = Array.AsReadOnly(new[] { forgedNode });
        var forgedIdentity = PreviewIdentity.Plan(capture.Invocation, capture.Document, capture.Plan.Observations, forgedNodes);
        var forgedPlan = new ResolvedEffectPlan(capture.Invocation, capture.Document, forgedIdentity, capture.Plan.Observations, forgedNodes);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, forgedPlan, "op-forged-hash");

        Assert.False(result.Succeeded);
        Assert.Equal(bytes, workspace.ReadBytes("note.txt"));
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.CompletionCalls);
    }

    [Fact]
    public void CompilerRejectsExactTargetWhenMinimumAuthorizationBudgetCannotFit()
    {
        var workspace = new WorkspaceId("patch-executor-test-workspace");
        var result = PreviewCompiler.Compile(
            [new FilePatchStage("note.txt", [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))])],
            workspace, new PreviewLimits(MaxResourceCalls: 8));

        Assert.False(result.Succeeded);
        Assert.Null(result.Document);
        Assert.Contains(result.Diagnostics, d => d.Code == "PREVIEW_RESOURCE_CALLS");
    }

    [Fact]
    public async Task IncompleteMultiAndGlobPlansAreUnsupportedBeforeAdmission()
    {
        using var workspace = new TestWorkspace();
        var bytes = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", bytes);
        workspace.WriteBytes("b.txt", bytes);
        var host = new RecordingHost();
        var executor = Executor(workspace, host);

        var multi = await Capture(workspace,
            [Patch("a.txt", bytes, 0, 3, "new"), Patch("b.txt", bytes, 0, 3, "new")]);
        var glob = await Capture(workspace,
            [new GlobPatchStage("", "**/*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new TraversalLimits())]);
        var incomplete = await Capture(workspace,
            [new GlobPatchStage("", "**/*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new TraversalLimits(MaxEntries: 10, MaxMatches: 1))],
            new PreviewLimits(MaxTargets: 1));

        Assert.Equal(PreviewRunStatus.Incomplete, incomplete.Status);
        Assert.Equal(ResourceFailureKind.Unsupported,
            (await executor.ExecuteAsync(multi.Document, multi.Plan, "op-multi")).Failure);
        Assert.Equal(ResourceFailureKind.Unsupported,
            (await executor.ExecuteAsync(glob.Document, glob.Plan, "op-glob")).Failure);
        Assert.Equal(ResourceFailureKind.Unsupported,
            (await executor.ExecuteAsync(incomplete.Document, incomplete.Plan, "op-incomplete")).Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Equal(bytes, workspace.ReadBytes("a.txt"));
        Assert.Equal(bytes, workspace.ReadBytes("b.txt"));
    }

    private static FilePatchStage Patch(string path, byte[] original, int start, int deleteLength, string replacement) =>
        new(path, [new TextPatch(start, deleteLength, Encoding.UTF8.GetBytes(replacement))], Version(original));

    private static async Task<(CompiledPreviewDocument Document, ResolvedEffectPlan Plan, EffectInvocation Invocation, PreviewRunStatus Status)> Capture(
        TestWorkspace workspace, IReadOnlyList<PreviewStage> stages, PreviewLimits? limits = null)
    {
        var compilation = PreviewCompiler.Compile(stages, workspace.Id, limits);
        var document = compilation.Document ?? throw new Xunit.Sdk.XunitException("Expected preview document to compile.");
        var invocation = new EffectInvocation("subject", "effect", Guid.NewGuid().ToString("N"));
        var preview = await new PreviewRuntime(new WorkspaceReference(workspace.Id.Value, workspace.Root), new PermitPreviewAuthority())
            .WhatIfAsync(invocation, document);
        return (document, preview.Plan ?? throw new Xunit.Sdk.XunitException($"Expected captured plan, got {preview.Status}."), invocation, preview.Status);
    }

    private static SinglePatchExecutor Executor(TestWorkspace workspace, RecordingHost host) =>
        new(new WorkspaceReference(workspace.Id.Value, workspace.Root), host, LocalPatchNamespace.HostControlled);

    private static ResourceVersion Version(byte[] bytes) => new("local-read-v1:sha256:" + Sha256(bytes));
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class PermitPreviewAuthority : IPreviewAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
    }

    private sealed class RecordingHost : IPatchExecutionHost
    {
        private readonly HashSet<string> _startedOperationIds = new(StringComparer.Ordinal);
        public List<string> Events { get; } = [];
        public List<PatchResourceCheck> ResourceChecks { get; } = [];
        public int AdmissionCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public LanguageAuthorityStatus AdmissionStatus { get; init; } = LanguageAuthorityStatus.Permit;
        public Func<PatchResourceCheck, AuthorizationStatus> ResourceDecision { get; init; } = _ => AuthorizationStatus.Permit;
        public Func<PatchStartRequest, MutationStartDecision> StartDecision { get; init; } = _ => new(MutationStartStatus.Started, "durable-start-1");
        public Func<PatchCompletion, bool> CompleteDecision { get; init; } = _ => true;
        public Action<PatchStartRequest>? OnStart { get; init; }
        public PatchStartRequest? LastStart { get; private set; }
        public PatchCompletion? LastCompletion { get; private set; }

        public ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default)
        {
            AdmissionCalls++; Events.Add("admit");
            return ValueTask.FromResult(new LanguageAuthorityDecision(AdmissionStatus));
        }

        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default)
        {
            ResourceChecks.Add(request);
            var path = request.Resource.Resource switch
            {
                ResourceBinding.WorkspaceFile file => file.Path.Value,
                ResourceBinding.WorkspaceDirectory directory => directory.Path.Value,
                ResourceBinding.WorkspaceEntry entry => entry.Path.Value,
                _ => "?"
            };
            Events.Add($"resource:{request.Resource.Action}:{path}");
            return ValueTask.FromResult(new ResourceAuthorizationDecision(ResourceDecision(request)));
        }

        public ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default)
        {
            StartCalls++; Events.Add("start"); LastStart = request;
            if (!_startedOperationIds.Add(request.Admission.OperationId))
                return ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.AlreadyStarted));
            OnStart?.Invoke(request);
            return ValueTask.FromResult(StartDecision(request));
        }

        public ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default)
        {
            CompletionCalls++; Events.Add("complete"); LastCompletion = completion;
            return ValueTask.FromResult(CompleteDecision(completion));
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-patch-executor-" + Guid.NewGuid().ToString("N"));
        public WorkspaceId Id { get; } = new("patch-executor-test-workspace");
        public string Root => _root;
        public TestWorkspace() => Directory.CreateDirectory(_root);
        public void WriteBytes(string relative, byte[] content)
        {
            var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test path escaped its temporary workspace.");
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
