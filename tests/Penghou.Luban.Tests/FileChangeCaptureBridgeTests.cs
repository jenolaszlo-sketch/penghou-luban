using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class FileChangeCaptureBridgeTests
{
    private static readonly EffectInvocation Invocation = new("subject", "change", "attempt");

    [Fact]
    public async Task Candidate_identity_matches_independently_encoded_vector()
    {
        using var files = new Files(); files.Write("a.txt", "x\n"); files.Write("b.txt", "y\n");
        var result = await new FileChangeRuntime(files.Workspace, files.Provider, new ChangePolicy())
            .DiffAsync(Invocation, files.Id, new("a.txt", "b.txt"));
        Assert.Equal(FileChangeStatus.Succeeded, result.Status);
        Assert.Equal("DB70B99AB2CE6DCE1F36A9137CC0F2058B07A2071EC3C516290CF575C0A5D0FE",
            Assert.Single(result.Candidates).CandidateIdentity);
    }

    [Fact]
    public async Task Complete_candidate_set_uses_existing_batch_host_and_ordered_outcomes()
    {
        using var files = new Files();
        files.Write("a.txt", "a"); files.Write("b.txt", "b");
        var capture = await new FileChangeCaptureBridge(files.Workspace, files.Provider, new PreviewPolicy())
            .CaptureAsync(Invocation, new[] { Candidate(files.Id, "a.txt", "a", "A"), Candidate(files.Id, "b.txt", "b", "B") });
        Assert.Equal(FileChangeCaptureStatus.Succeeded, capture.Status);
        var host = new BatchHost();
        var result = await new BatchPatchExecutor(files.Workspace, files.Provider, host)
            .ExecuteAsync(capture.Document!, capture.Plan!, "candidate-batch");
        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(new[] { BatchRecoveryState.Completed, BatchRecoveryState.Completed }, result.Entries.Select(entry => entry.State));
        Assert.Equal(new[] { "admit", "start", "complete", "start", "complete" }, host.Events);
        Assert.Equal("A", files.Read("a.txt")); Assert.Equal("B", files.Read("b.txt"));
    }

    [Fact]
    public async Task Diff_candidate_uses_fresh_capture_and_separate_admitted_executor()
    {
        using var files = new Files();
        files.Write("target.txt", "α\r\nold\n");
        files.Write("proposed.txt", "α\r\nnew\n");
        var result = await new FileChangeRuntime(files.Workspace, files.Provider, new ChangePolicy())
            .DiffAsync(Invocation, files.Id, new("target.txt", "proposed.txt"));
        Assert.Equal(FileChangeStatus.Succeeded, result.Status);
        var capture = await new FileChangeCaptureBridge(files.Workspace, files.Provider, new PreviewPolicy())
            .CaptureAsync(Invocation, result.Candidates);
        Assert.Equal(FileChangeCaptureStatus.Succeeded, capture.Status);
        Assert.False(capture.Plan!.CanCommit);
        Assert.Equal("α\r\nold\n", files.Read("target.txt"));

        var host = new Host();
        var write = await new SinglePatchExecutor(files.Workspace, files.Provider, host)
            .ExecuteAsync(capture.Document!, capture.Plan, "diff-apply");
        Assert.True(write.Succeeded, write.Failure?.ToString());
        Assert.Equal("α\r\nnew\n", files.Read("target.txt"));
        Assert.Equal(new[] { "admit", "start", "complete" }, host.Events);
    }

    [Fact]
    public async Task Stale_later_candidate_drops_the_entire_capture_without_writes()
    {
        using var files = new Files();
        files.Write("a.txt", "a"); files.Write("b.txt", "b");
        var candidates = new[] { Candidate(files.Id, "a.txt", "a", "A"), Candidate(files.Id, "b.txt", "b", "B") };
        files.Write("b.txt", "changed");
        var capture = await new FileChangeCaptureBridge(files.Workspace, files.Provider, new PreviewPolicy())
            .CaptureAsync(Invocation, candidates);
        Assert.Equal(FileChangeCaptureStatus.Stale, capture.Status);
        Assert.Null(capture.Plan); Assert.Null(capture.Document);
        Assert.Equal("a", files.Read("a.txt"));
        Assert.Equal("changed", files.Read("b.txt"));
    }

    [Fact]
    public async Task Later_destination_denial_happens_before_any_content_read()
    {
        using var files = new Files();
        files.Write("a.txt", "a"); files.Write("b.txt", "b");
        var policy = new PreviewPolicy(request => request.Phase == PreviewAuthorizationPhase.TargetAdmission &&
            request.ResourcePath == "b.txt" ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var capture = await new FileChangeCaptureBridge(files.Workspace, files.Provider, policy)
            .CaptureAsync(Invocation, new[] { Candidate(files.Id, "a.txt", "a", "A"), Candidate(files.Id, "b.txt", "b", "B") });
        Assert.Equal(FileChangeCaptureStatus.CaptureFailed, capture.Status);
        Assert.Equal(PreviewRunStatus.AuthorityDenied, capture.CaptureStatus);
        Assert.DoesNotContain(policy.Requests, request => request.Action == ResourceAction.ReadFile);
        Assert.Null(capture.Plan);
    }

    [Fact]
    public async Task Opaque_version_agreement_does_not_replace_original_hash_validation()
    {
        using var files = new Files();
        files.Write("target.txt", "old");
        var forged = Candidate(files.Id, "target.txt", "bad", "new", Version("old"));
        var capture = await new FileChangeCaptureBridge(files.Workspace, files.Provider, new PreviewPolicy())
            .CaptureAsync(Invocation, new[] { forged });
        Assert.Equal(FileChangeCaptureStatus.Stale, capture.Status);
        Assert.Null(capture.Plan);
        Assert.Equal("old", files.Read("target.txt"));
    }

    [Fact]
    public async Task Release_denial_returns_neither_document_nor_protected_plan()
    {
        using var files = new Files(); files.Write("target.txt", "old");
        var policy = new PreviewPolicy(request => request.Phase == PreviewAuthorizationPhase.Release
            ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var result = await new FileChangeCaptureBridge(files.Workspace, files.Provider, policy)
            .CaptureAsync(Invocation, new[] { Candidate(files.Id, "target.txt", "old", "new") });
        Assert.Equal(FileChangeCaptureStatus.CaptureFailed, result.Status);
        Assert.Null(result.Document); Assert.Null(result.Plan);
        Assert.Equal("old", files.Read("target.txt"));
    }

    [Fact]
    public async Task Unchanged_candidate_has_no_plan_or_authorization_calls()
    {
        using var files = new Files();
        var policy = new PreviewPolicy();
        var result = await new FileChangeCaptureBridge(files.Workspace, files.Provider, policy)
            .CaptureAsync(Invocation, new[] { Candidate(files.Id, "absent.txt", "same", "same") });
        Assert.Equal(FileChangeCaptureStatus.NoChanges, result.Status);
        Assert.Null(result.Plan); Assert.Empty(policy.Requests);
    }

    [Fact]
    public async Task Retention_bounds_reject_before_authorization_and_content_reads()
    {
        using var files = new Files();
        var policy = new PreviewPolicy();
        var result = await new FileChangeCaptureBridge(files.Workspace, files.Provider, policy)
            .CaptureAsync(Invocation, new[] { Candidate(files.Id, "absent.txt", new string('a', 5000), new string('b', 5000)) },
                new PreviewLimits(MaxPlanBytes: 8192));
        Assert.Equal(FileChangeCaptureStatus.UnsupportedCandidate, result.Status);
        Assert.Empty(policy.Requests); Assert.Null(result.Plan);
    }

    private static FilePatchCandidate Candidate(WorkspaceId workspace, string path, string original,
        string proposed, ResourceVersion? version = null)
    {
        var diff = TextDiffEngine.Diff(original, proposed);
        Assert.Equal(TextDiffStatus.Succeeded, diff.Status);
        return new(workspace, path, version ?? Version(original), new(Encoding.UTF8.GetBytes(original)),
            new(Encoding.UTF8.GetBytes(proposed)), diff.Edits.Select(edit => new FrozenTextPatch(edit.StartOffset,
                edit.DeleteLength, new ImmutableBytes(Encoding.UTF8.GetBytes(edit.Replacement)))).ToArray(),
            [], TextChangeOptions.Default, FileChangeLimits.Default, FileChangeOperation.Diff, "local-windows-read-v1", "test-profile", "test-identity");
    }

    private static ResourceVersion Version(string content) => new("local-read-v1:sha256:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    private sealed class ChangePolicy : IFileChangeAuthorizer
    {
        public ValueTask<FileChangeAuthorizationDecision> AuthorizeAsync(FileChangeAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new FileChangeAuthorizationDecision(FileChangeAuthorizationStatus.Permit));
    }

    private sealed class PreviewPolicy(Func<PreviewAuthorizationRequest, LanguageAuthorityStatus>? decision = null) : IPreviewAuthorizer
    {
        public List<PreviewAuthorizationRequest> Requests { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        { Requests.Add(request); return ValueTask.FromResult(new LanguageAuthorityDecision(decision?.Invoke(request) ?? LanguageAuthorityStatus.Permit)); }
    }

    private sealed class Host : IPatchExecutionHost
    {
        public List<string> Events { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default)
        { Events.Add("admit"); return ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit)); }
        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        public ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default)
        { Events.Add("start"); return ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Started, "test-start")); }
        public ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default)
        { Events.Add("complete"); return ValueTask.FromResult(true); }
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-capture-" + Guid.NewGuid().ToString("N"));
        public WorkspaceId Id { get; } = new("workspace");
        public WorkspaceReference Workspace => new(Id.Value);
        public IWorkspaceProvider Provider { get; }
        public Files() { Directory.CreateDirectory(_root); Provider = TestLocalProvider.Create(Id.Value, _root); }
        public void Write(string path, string value) => File.WriteAllBytes(Path.Combine(_root, path), Encoding.UTF8.GetBytes(value));
        public string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_root, path)));
        public void Dispose()
        {
            var target = Path.GetFullPath(_root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(target, recursive: true);
        }
    }

    private sealed class BatchHost : IBatchPatchExecutionHost
    {
        public List<string> Events { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AdmitAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default)
        { Events.Add("admit"); return ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit)); }
        public ValueTask<BatchRecoverySnapshot?> InspectAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BatchRecoverySnapshot?>(new(request.SegmentId, request.Plan.Identity,
                Array.AsReadOnly(request.Operations.Select((operation, index) => new BatchRecoveryEntry(operation.OperationId,
                    request.Document.Nodes[index].Identity, BatchRecoveryState.NotStarted)).ToArray())));
        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(BatchAdmissionRequest request,
            PatchResourceCheck check, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        public ValueTask<MutationStartDecision> StartAsync(BatchAdmissionRequest request,
            PatchStartRequest start, CancellationToken cancellationToken = default)
        { Events.Add("start"); return ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Started, "test-batch-start")); }
        public ValueTask<bool> CompleteAsync(BatchAdmissionRequest request,
            PatchCompletion completion, CancellationToken cancellationToken = default)
        { Events.Add("complete"); return ValueTask.FromResult(true); }
    }
}
