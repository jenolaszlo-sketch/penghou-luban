using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class FileChangeRuntimeTests
{
    private static readonly EffectInvocation Invocation = new("subject", "files.change", "attempt");

    [Fact]
    public async Task Unchanged_diff_returns_zero_edit_candidate_and_never_writes()
    {
        using var files = new Files();
        files.Write("target.txt", "same\r\n");
        files.Write("source.txt", "same\r\n");
        var result = await files.Runtime(new Policy()).DiffAsync(Invocation, files.Id, new("target.txt", "source.txt"));
        Assert.Equal(FileChangeStatus.Succeeded, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(candidate.Edits);
        Assert.Equal("same\r\n", Encoding.UTF8.GetString(candidate.ProposedContent.ToArray()));
        Assert.Equal("same\r\n", files.Read("target.txt"));
    }

    [Fact]
    public async Task Clean_merge_targets_ours_and_emits_exact_candidate()
    {
        using var files = new Files();
        files.Write("base.txt", "a\nb\n");
        files.Write("ours.txt", "A\nb\n");
        files.Write("theirs.txt", "a\nB\n");
        var result = await files.Runtime(new Policy()).MergeAsync(Invocation, files.Id,
            new("base.txt", "ours.txt", "theirs.txt"));
        Assert.Equal(FileChangeStatus.Succeeded, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ours.txt", candidate.TargetPath);
        Assert.Equal("A\nB\n", Encoding.UTF8.GetString(candidate.ProposedContent.ToArray()));
        Assert.Equal(FileChangeStatus.Valid,
            (await files.Runtime(new Policy()).ValidateCandidateAsync(Invocation, files.Id, candidate)).Status);
        Assert.Equal("A\nb\n", files.Read("ours.txt"));
    }

    [Fact]
    public async Task Unified_materialization_reads_target_and_returns_exact_candidate()
    {
        using var files = new Files();
        files.Write("a.txt", "old\n");
        var parsed = TextUnifiedImport.Parse("--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new\n");
        Assert.Equal(TextUnifiedImportStatus.Succeeded, parsed.Status);
        var result = await files.Runtime(new Policy()).MaterializeUnifiedAsync(Invocation, files.Id, parsed.Candidate!);
        Assert.Equal(FileChangeStatus.Succeeded, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("new\n", Encoding.UTF8.GetString(candidate.ProposedContent.ToArray()));
        Assert.Equal(FileChangeStatus.Valid,
            (await files.Runtime(new Policy()).ValidateCandidateAsync(Invocation, files.Id, candidate)).Status);
        Assert.Equal("old\n", files.Read("a.txt"));
    }

    [Fact]
    public async Task Metadata_denial_fails_closed_and_releases_no_candidate()
    {
        using var files = new Files();
        files.Write("target.txt", "old\n"); files.Write("source.txt", "new\n");
        var policy = new Policy(request => request.Phase == FileChangePhase.ResourceAccess &&
            request.Action == ResourceAction.ReadMetadata ? FileChangeAuthorizationStatus.Deny : FileChangeAuthorizationStatus.Permit);
        var result = await files.Runtime(policy).DiffAsync(Invocation, files.Id, new("target.txt", "source.txt"));
        Assert.Equal(FileChangeStatus.AuthorizationDenied, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Contains(policy.Requests, request => request.Action == ResourceAction.ReadMetadata);
        Assert.Contains(policy.Requests, request => request.Phase == FileChangePhase.ResultRelease &&
            request.Outcome == FileChangeStatus.AuthorizationDenied);
        Assert.Equal("old\n", files.Read("target.txt"));
    }

    [Fact]
    public async Task Work_limit_and_pre_cancel_are_reported_without_candidates()
    {
        using var files = new Files();
        files.Write("target.txt", "a\nb\nc\n"); files.Write("source.txt", "x\ny\nz\n");
        var bounded = await files.Runtime(new Policy()).DiffAsync(Invocation, files.Id,
            new("target.txt", "source.txt", new TextChangeOptions { MaxWorkCells = 1 }));
        Assert.Equal(FileChangeStatus.LimitExceeded, bounded.Status);
        Assert.Empty(bounded.Candidates);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = await files.Runtime(new Policy()).DiffAsync(Invocation, files.Id,
            new("target.txt", "source.txt"), cancellationToken: cancelled.Token);
        Assert.Equal(FileChangeStatus.Cancelled, stopped.Status);
        Assert.Empty(stopped.Candidates);
    }

    [Fact]
    public async Task Injected_provider_uses_opaque_versions_and_declared_profile_without_local_composition()
    {
        var workspace = new WorkspaceId("memory-workspace");
        var provider = new MemoryProvider(workspace, new Dictionary<string, byte[]>
        {
            ["target.txt"] = Encoding.UTF8.GetBytes("before\n"),
            ["source.txt"] = Encoding.UTF8.GetBytes("after\n")
        });
        var policy = new Policy();
        var runtime = new FileChangeRuntime(new WorkspaceReference(workspace.Value), provider, policy);
        var result = await runtime.DiffAsync(Invocation, workspace, new("target.txt", "source.txt"));
        Assert.Equal(FileChangeStatus.Succeeded, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.StartsWith("opaque-memory-v1:", candidate.OriginalVersion.Value, StringComparison.Ordinal);
        Assert.Contains(policy.Requests, request => request.Phase == FileChangePhase.ResourceAccess &&
            request.Action == ResourceAction.ReadFile && request.Resource?.Path.Value == "target.txt");
        Assert.Equal("before\n", Encoding.UTF8.GetString(provider.Content("target.txt")));
    }

    [Fact]
    public async Task Candidate_validation_honors_stricter_current_limits_without_widening_capture_bounds()
    {
        using var files = new Files();
        files.Write("target.txt", "original\n"); files.Write("source.txt", "proposed\n");
        var candidate = Assert.Single((await files.Runtime(new Policy())
            .DiffAsync(Invocation, files.Id, new("target.txt", "source.txt"))).Candidates);
        var policy = new Policy();
        var limited = await files.Runtime(policy).ValidateCandidateAsync(Invocation, files.Id,
            candidate, new FileChangeLimits(MaxTotalReadBytes: 1));
        Assert.Equal(FileChangeStatus.LimitExceeded, limited.Status);
        Assert.Null(limited.ValidatedCandidate);
        Assert.All(policy.Requests, request => Assert.Equal(1, request.Limits!.MaxTotalReadBytes));

        policy.Requests.Clear();
        var retained = await files.Runtime(policy).ValidateCandidateAsync(Invocation, files.Id,
            candidate, new FileChangeLimits(MaxTotalRetainedBytes: 1));
        Assert.Equal(FileChangeStatus.LimitExceeded, retained.Status);
        Assert.Null(retained.ValidatedCandidate);
        Assert.Empty(policy.Requests);
        Assert.Equal(FileChangeStatus.InvalidInput, (await files.Runtime(policy)
            .ValidateCandidateAsync(Invocation, files.Id, candidate, new FileChangeLimits(MaxFiles: 33))).Status);
    }

    [Fact]
    public async Task Conflicted_merge_payload_also_respects_retained_data_limit()
    {
        using var files = new Files();
        files.Write("base.txt", "base\n"); files.Write("ours.txt", "ours\n"); files.Write("theirs.txt", "theirs\n");
        var policy = new Policy();
        var result = await files.Runtime(policy).MergeAsync(Invocation, files.Id,
            new("base.txt", "ours.txt", "theirs.txt"), new FileChangeLimits(MaxTotalRetainedBytes: 1));
        Assert.Equal(FileChangeStatus.LimitExceeded, result.Status);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Merge);
        Assert.Contains(policy.Requests, request => request.Phase == FileChangePhase.ResultRelease &&
            request.Outcome == FileChangeStatus.LimitExceeded);
        Assert.Equal("ours\n", files.Read("ours.txt"));
    }

    private sealed class Policy(Func<FileChangeAuthorizationRequest, FileChangeAuthorizationStatus>? decide = null) : IFileChangeAuthorizer
    {
        public List<FileChangeAuthorizationRequest> Requests { get; } = [];
        public ValueTask<FileChangeAuthorizationDecision> AuthorizeAsync(FileChangeAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new FileChangeAuthorizationDecision(decide?.Invoke(request) ?? FileChangeAuthorizationStatus.Permit));
        }
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-file-change-" + Guid.NewGuid().ToString("N"));
        internal WorkspaceId Id { get; } = new("workspace");
        internal WorkspaceReference Workspace => new(Id.Value);
        internal IWorkspaceProvider Provider { get; }
        internal Files() { Directory.CreateDirectory(_root); Provider = TestLocalProvider.Create(Id.Value, _root); }
        internal FileChangeRuntime Runtime(IFileChangeAuthorizer authorizer) => new(Workspace, Provider, authorizer);
        internal void Write(string path, string value) => File.WriteAllBytes(Path.Combine(_root, path), Encoding.UTF8.GetBytes(value));
        internal string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_root, path)));
        public void Dispose()
        {
            var target = Path.GetFullPath(_root);
            if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) Directory.Delete(target, recursive: true);
        }
    }

    private sealed class MemoryProvider(WorkspaceId workspace, IReadOnlyDictionary<string, byte[]> initial) : IWorkspaceProvider
    {
        private readonly Dictionary<string, byte[]> _files = initial.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        public WorkspaceId Workspace { get; } = workspace;
        public WorkspaceProviderCapabilities Capabilities { get; } = new("memory-read-v1", null, true, false);
        internal byte[] Content(string path) => _files[path].ToArray();
        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null) => new Reader(this, authorizer);
        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal,
            WorkspaceWriterOptions? options = null) => throw new NotSupportedException();

        private sealed class Reader(MemoryProvider provider, IResourceAuthorizer authorizer) : IWorkspaceReaderSession
        {
            public async ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request,
                CancellationToken cancellationToken = default)
            {
                if (request.Workspace != provider.Workspace || !provider._files.TryGetValue(request.Path.Value, out var bytes))
                    return ResourceResult<FileReadResult>.Failed(ResourceFailureKind.NotFound);
                var identity = ResourceRequestIdentity.Compute(request);
                var decision = await authorizer.AuthorizeAsync(new(request.Invocation, identity, ResourceAction.ReadFile,
                    new ResourceBinding.WorkspaceFile(request.Workspace, request.Path)), cancellationToken);
                if (decision.Status != AuthorizationStatus.Permit)
                    return ResourceResult<FileReadResult>.Failed(ResourceFailureKind.AuthorizationDenied);
                if (bytes.Length > request.Limits.MaxBytes) return ResourceResult<FileReadResult>.Failed(ResourceFailureKind.TooLarge);
                return ResourceResult<FileReadResult>.Success(new(bytes.ToArray(), new ResourceVersion(
                    "opaque-memory-v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))));
            }

            public ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(ResourceResult<FileMetadata>.Failed(ResourceFailureKind.Unsupported));
            public ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(ResourceResult<DirectoryPage>.Failed(ResourceFailureKind.Unsupported));
            public void Dispose() { }
        }
    }
}
