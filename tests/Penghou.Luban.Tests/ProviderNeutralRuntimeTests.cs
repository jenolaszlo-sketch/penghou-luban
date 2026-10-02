using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class ProviderNeutralRuntimeTests
{
    [Fact]
    public async Task Opaque_provider_versions_work_for_read_preview_single_and_batch_writes()
    {
        var workspaceId = new WorkspaceId("memory-workspace");
        var provider = new MemoryProvider(workspaceId, "note.txt", Encoding.UTF8.GetBytes("old"));
        var workspace = new WorkspaceReference(workspaceId.Value);

        var language = new LanguageRuntime(workspace, provider, new PermitLanguageAuthority());
        var read = await language.ExecuteAsync(new("subject", "effect", "attempt"),
            LanguageCompiler.Compile([new LanguageStage[] { new ReadStage("note.txt") }], workspaceId).Document!);
        Assert.Equal(LanguageRunStatus.Succeeded, read.Status);

        var single = await Capture(workspaceId, provider, "note.txt", "one");
        var singleHost = new PermitPatchHost();
        var singleResult = await new SinglePatchExecutor(workspace, provider, singleHost)
            .ExecuteAsync(single.Document, single.Plan, "single-memory-write");
        Assert.True(singleResult.Succeeded, singleResult.Failure?.ToString());
        Assert.StartsWith("opaque-memory-v1:", singleResult.Version!.Value.Value, StringComparison.Ordinal);
        Assert.Equal("one", Encoding.UTF8.GetString(provider.Read("note.txt")));

        var batch = await Capture(workspaceId, provider, "note.txt", "two");
        var batchHost = new PermitBatchHost();
        var batchResult = await new BatchPatchExecutor(workspace, provider, batchHost)
            .ExecuteAsync(batch.Document, batch.Plan, "batch-memory-write");
        Assert.True(batchResult.Succeeded, batchResult.Failure?.ToString());
        Assert.Equal("two", Encoding.UTF8.GetString(provider.Read("note.txt")));
        Assert.Equal("memory-read-v1", provider.Capabilities.ReadProfile);
        Assert.Equal("memory-conditional-write-v1", provider.Capabilities.WriteProfile);
    }

    private static async Task<(CompiledPreviewDocument Document, ResolvedEffectPlan Plan)> Capture(
        WorkspaceId workspace, IWorkspaceProvider provider, string path, string replacement)
    {
        var current = provider is MemoryProvider memory ? memory.Read(path) : throw new InvalidOperationException();
        var version = MemoryProvider.VersionOf(current);
        var compilation = PreviewCompiler.Compile([new FilePatchStage(path,
            [new TextPatch(0, current.Length, Encoding.UTF8.GetBytes(replacement))], version)], workspace);
        var document = compilation.Document ?? throw new Xunit.Sdk.XunitException("Preview compilation failed.");
        var capture = await new PreviewRuntime(new WorkspaceReference(workspace.Value), provider,
            new PermitPreviewAuthority()).WhatIfAsync(new("subject", "effect", Guid.NewGuid().ToString("N")), document);
        return (document, capture.Plan ?? throw new Xunit.Sdk.XunitException($"Capture failed: {capture.Status}."));
    }

    private sealed class MemoryProvider(WorkspaceId workspace, string path, byte[] initial) : IWorkspaceProvider
    {
        private readonly object _gate = new();
        private readonly string _path = path;
        private byte[] _content = initial.ToArray();
        public WorkspaceId Workspace { get; } = workspace;
        public WorkspaceProviderCapabilities Capabilities { get; } = new("memory-read-v1", "memory-conditional-write-v1", true, true);
        internal byte[] Read(string target) { lock (_gate) return target == _path ? _content.ToArray() : throw new FileNotFoundException(); }
        internal static ResourceVersion VersionOf(byte[] bytes) => new("opaque-memory-v1:" + Convert.ToHexString(SHA256.HashData(bytes)));
        public IWorkspaceReaderSession OpenReader(IResourceAuthorizer authorizer, WorkspaceReaderOptions? options = null) => new Reader(this, authorizer);
        public IWorkspaceConditionalWriter OpenWriter(IResourceAuthorizer authorizer, IResourceMutationJournal journal,
            WorkspaceWriterOptions? options = null) => new Writer(this, authorizer, journal);

        private sealed class Reader(MemoryProvider provider, IResourceAuthorizer authorizer) : IWorkspaceReaderSession
        {
            public async ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request, CancellationToken cancellationToken = default)
            {
                if (request.Workspace != provider.Workspace || request.Path.Value != provider._path)
                    return ResourceResult<FileReadResult>.Failed(ResourceFailureKind.NotFound);
                var identity = ResourceRequestIdentity.Compute(request);
                var decision = await authorizer.AuthorizeAsync(new(request.Invocation, identity, ResourceAction.ReadFile,
                    new ResourceBinding.WorkspaceFile(request.Workspace, request.Path)), cancellationToken);
                if (decision.Status != AuthorizationStatus.Permit) return ResourceResult<FileReadResult>.Failed(ResourceFailureKind.AuthorizationDenied);
                var bytes = provider.Read(request.Path.Value);
                if (bytes.Length > request.Limits.MaxBytes) return ResourceResult<FileReadResult>.Failed(ResourceFailureKind.TooLarge);
                return ResourceResult<FileReadResult>.Success(new(bytes, VersionOf(bytes)));
            }

            public async ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request,
                CancellationToken cancellationToken = default)
            {
                var identity = ResourceRequestIdentity.Compute(request);
                var decision = await authorizer.AuthorizeAsync(new(request.Invocation, identity, ResourceAction.ReadMetadata,
                    new ResourceBinding.WorkspaceFile(request.Workspace, request.Path)), cancellationToken);
                if (decision.Status != AuthorizationStatus.Permit) return ResourceResult<FileMetadata>.Failed(ResourceFailureKind.AuthorizationDenied);
                var bytes = provider.Read(request.Path.Value);
                return ResourceResult<FileMetadata>.Success(new(true, bytes.Length, VersionOf(bytes)));
            }

            public ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(ResourceResult<DirectoryPage>.Failed(ResourceFailureKind.Unsupported));
            public void Dispose() { }
        }

        private sealed class Writer(MemoryProvider provider, IResourceAuthorizer authorizer, IResourceMutationJournal journal)
            : IWorkspaceConditionalWriter
        {
            public async ValueTask<ResourceResult<ResourceVersion>> WriteFileAsync(FileWriteRequest request,
                CancellationToken cancellationToken = default)
            {
                if (request.Workspace != provider.Workspace || request.Path.Value != provider._path)
                    return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.NotFound);
                var identity = ResourceRequestIdentity.Compute(request);
                var frozenRequest = request with { Content = request.Content.ToArray(),
                    Invocation = request.Invocation with { RequestIdentity = identity } };
                if (request.Invocation.RequestIdentity != identity)
                    return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.AuthorizationDenied);
                var binding = new ResourceBinding.WorkspaceFile(request.Workspace, request.Path);
                async ValueTask<bool> Demand(ResourceAction action) =>
                    (await authorizer.AuthorizeAsync(new(request.Invocation, identity, action, binding), cancellationToken)).Status == AuthorizationStatus.Permit;
                if (!await Demand(ResourceAction.WriteFile) || !await Demand(ResourceAction.ReadFile))
                    return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.AuthorizationDenied);
                lock (provider._gate)
                {
                    if (request.Precondition.Version != VersionOf(provider._content))
                        return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.PreconditionFailed);
                }
                if (!await Demand(ResourceAction.WriteFile)) return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.AuthorizationDenied);
                ResourceVersion original;
                lock (provider._gate) original = VersionOf(provider._content);
                var proposed = VersionOf(frozenRequest.Content.ToArray());
                var start = new MutationStartRequest(request.Invocation, identity, request.Workspace, request.Path,
                    provider.Capabilities.WriteProfile!, "memory:" + request.Path.Value, original, proposed,
                    provider.Read(request.Path.Value).Length, request.Content.Length);
                var startDecision = await journal.StartAsync(start, cancellationToken);
                if (startDecision.Status != MutationStartStatus.Started || string.IsNullOrWhiteSpace(startDecision.EvidenceId))
                    return ResourceResult<ResourceVersion>.Failed(startDecision.Status == MutationStartStatus.Deny
                        ? ResourceFailureKind.AuthorizationDenied : ResourceFailureKind.AuthorizationUnavailable);
                var noLongerMatches = false;
                lock (provider._gate)
                {
                    if (VersionOf(provider._content) != original)
                    {
                        noLongerMatches = true;
                    }
                    else provider._content = frozenRequest.Content.ToArray();
                }
                if (noLongerMatches)
                {
                    await journal.CompleteAsync(new(start, startDecision.EvidenceId, MutationOutcome.NoMutation, original), cancellationToken);
                    return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.PreconditionFailed);
                }
                var completionAccepted = await journal.CompleteAsync(new(start, startDecision.EvidenceId,
                    MutationOutcome.Completed, proposed), cancellationToken);
                return completionAccepted ? ResourceResult<ResourceVersion>.Success(proposed)
                    : ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.AmbiguousOutcome);
            }
        }
    }

    private sealed class PermitLanguageAuthority : ILanguageAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
    }

    private sealed class PermitPreviewAuthority : IPreviewAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
    }

    private sealed class PermitPatchHost : IPatchExecutionHost
    {
        public ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        public ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Started, "memory-start"));
        public ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class PermitBatchHost : IBatchPatchExecutionHost
    {
        public ValueTask<LanguageAuthorityDecision> AdmitAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
        public ValueTask<BatchRecoverySnapshot?> InspectAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BatchRecoverySnapshot?>(new(request.SegmentId, request.Plan.Identity,
                Array.AsReadOnly(request.Operations.Select((operation, index) => new BatchRecoveryEntry(operation.OperationId,
                    request.Document.Nodes[index].Identity, BatchRecoveryState.NotStarted)).ToArray())));
        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(BatchAdmissionRequest request, PatchResourceCheck check,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        public ValueTask<MutationStartDecision> StartAsync(BatchAdmissionRequest request, PatchStartRequest start,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Started, "memory-batch-start"));
        public ValueTask<bool> CompleteAsync(BatchAdmissionRequest request, PatchCompletion completion,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
