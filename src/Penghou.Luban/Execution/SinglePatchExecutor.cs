using System.Text;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Penghou.Luban.Execution;

/// <summary>One complete exact-target plan and its concrete mutation identity.</summary>
public sealed record PatchAdmissionRequest(string OperationId, CompiledPreviewDocument Document,
    ResolvedEffectPlan Plan, string WriterProfile, LocalPatchNamespace Namespace,
    HostInvocation Invocation, RequestIdentity ResourceRequestIdentity);
public sealed record PatchResourceCheck(PatchAdmissionRequest Admission, ResourceAuthorizationRequest Resource);
public sealed record PatchStartRequest(PatchAdmissionRequest Admission, MutationStartRequest Mutation);
public sealed record PatchCompletion(PatchAdmissionRequest Admission, MutationCompletion Mutation);

/// <summary>
/// Trusted composition boundary. Admission must validate the whole exact plan;
/// Start must serialize current authority/revision/fence and durable start
/// evidence. Implementations authenticate context, reject duplicate/conflicting
/// operation IDs and reconcile uncertain starts. No permissive host ships.
/// </summary>
public interface IPatchExecutionHost
{
    ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default);
    ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default);
    ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default);
    ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default);
}

public sealed record PatchExecutionResult(ResourceFailureKind? Failure, ResourceVersion? Version = null)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Separate single-target execution profile. Capture-only PreviewRuntime never
/// dispatches this executor and its plans still confer no commit permission.
/// </summary>
public sealed class SinglePatchExecutor
{
    private readonly WorkspaceReference _workspace;
    private readonly IPatchExecutionHost _host;
    private readonly LocalPatchNamespace _namespace;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public SinglePatchExecutor(WorkspaceReference workspace, IPatchExecutionHost host, LocalPatchNamespace namespaceProfile)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _namespace = namespaceProfile;
    }

    public ValueTask<PatchExecutionResult> ExecuteAsync(CompiledPreviewDocument document,
        ResolvedEffectPlan plan, string operationId, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(document, plan, operationId, null, cancellationToken);

    internal ValueTask<PatchExecutionResult> ExecuteNodeAsync(CompiledPreviewDocument document,
        ResolvedEffectPlan plan, string operationId, int nodeIndex, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(document, plan, operationId, nodeIndex, cancellationToken);

    private async ValueTask<PatchExecutionResult> ExecuteCoreAsync(CompiledPreviewDocument document,
        ResolvedEffectPlan plan, string operationId, int? selectedNode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(document, plan, operationId)) return new(ResourceFailureKind.InvalidRequest);
        if (_namespace != LocalPatchNamespace.HostControlled) return new(ResourceFailureKind.Unsupported);
        if (!OperatingSystem.IsWindows()) return new(ResourceFailureKind.Unsupported);
        var index = selectedNode ?? 0;
        if (index < 0 || index >= document.Nodes.Count || (!selectedNode.HasValue && document.Nodes.Count != 1) ||
            document.Nodes[index].Operation is not ExactPatchOperation || plan.Nodes[index].Proposals.Count != 1 || !plan.CaptureComplete)
            return new(ResourceFailureKind.Unsupported);
        var proposal = plan.Nodes[index].Proposals[0];
        if (proposal.OriginalByteLength < 0 || proposal.ProposedByteLength < 0 ||
            proposal.OriginalByteLength > document.Limits.MaxFileBytes || proposal.ProposedByteLength > document.Limits.MaxFileBytes ||
            (long)proposal.OriginalByteLength + proposal.ProposedByteLength > document.Limits.MaxReadBytes)
            return new(ResourceFailureKind.TooLarge);
        var admission = CreateAdmission(document, plan, operationId, index, _namespace);
        var request = CreateRequest(admission, proposal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(document.Limits.TimeoutMilliseconds);
        try
        {
            LanguageAuthorityDecision? decision;
            try { decision = await _host.AdmitAsync(admission, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw; }
            catch { return new(ResourceFailureKind.AuthorizationUnavailable); }
            deadline.Token.ThrowIfCancellationRequested();
            if (decision?.Status != LanguageAuthorityStatus.Permit)
                return new(decision?.Status == LanguageAuthorityStatus.Deny ? ResourceFailureKind.AuthorizationDenied : ResourceFailureKind.AuthorizationUnavailable);
            var boundary = new Boundary(_host, admission, proposal);
            var writer = new LocalWorkspacePatcher(document.Workspace, _workspace.FullRootPath, boundary, boundary,
                new LocalPatchOptions(document.Limits.MaxFileBytes, document.Limits.MaxReadBytes, document.Limits.TimeoutMilliseconds, _namespace));
            var result = await writer.PatchFileAsync(request, deadline.Token).ConfigureAwait(false);
            return new(result.Failure, result.Succeeded ? result.Value : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(ResourceFailureKind.ProviderFailure); }
    }

    internal static PatchAdmissionRequest CreateAdmission(CompiledPreviewDocument document, ResolvedEffectPlan plan,
        string operationId, int nodeIndex, LocalPatchNamespace namespaceProfile)
    {
        var invocation = new HostInvocation(operationId, plan.Invocation.SubjectId, plan.Invocation.EffectId,
            plan.Invocation.AttemptId, plan.Identity, document.Identity, default);
        var draft = new PatchAdmissionRequest(operationId, document, plan, LocalWorkspacePatcher.ProviderProfile,
            namespaceProfile, invocation, default);
        var identity = ResourceRequestIdentity.Compute(CreateRequest(draft, plan.Nodes[nodeIndex].Proposals[0]));
        return draft with { Invocation = invocation with { RequestIdentity = identity }, ResourceRequestIdentity = identity };
    }

    private static FilePatchRequest CreateRequest(PatchAdmissionRequest admission, CapturedFilePatch proposal) =>
        new(admission.Invocation, admission.Document.Workspace, new(proposal.RelativePath), proposal.OriginalVersion,
            Array.AsReadOnly(proposal.Patches.Select(p => new TextPatch(p.StartOffset, p.DeleteLength, p.ReplacementUtf8.ToArray())).ToArray()),
            new(admission.Document.Limits.MaxPatchCount, admission.Document.Limits.MaxReplacementBytes, admission.Document.Limits.MaxFileBytes));

    internal bool Validate(CompiledPreviewDocument? doc, ResolvedEffectPlan? plan, string? operationId)
    {
        if (doc is null || plan is null || !Id(operationId) || !Id(plan.Invocation.SubjectId) || !Id(plan.Invocation.EffectId) ||
            !Id(plan.Invocation.AttemptId) || doc.Workspace.Value != _workspace.Id || plan.Workspace != doc.Workspace ||
            plan.DocumentIdentity != doc.Identity || plan.Limits != doc.Limits) return false;
        try
        {
            if (plan.Nodes.Count != doc.Nodes.Count) return false;
            for (var index = 0; index < doc.Nodes.Count; index++)
            {
                var node = doc.Nodes[index];
                var resolved = plan.Nodes[index];
                var dependencies = index == 0 ? Array.Empty<string>() : new[] { doc.Nodes[index - 1].Identity };
                if (resolved.NodeIdentity != node.Identity || resolved.Descriptor != node.Descriptor ||
                    !resolved.Dependencies.SequenceEqual(dependencies)) return false;
                if (resolved.State == PreviewNodeState.Proposed && node.Operation is ExactPatchOperation exact)
                {
                    if (resolved.Selection != PreviewSelection.Unspecified || resolved.UnresolvedReason is not null || resolved.Proposals.Count != 1) return false;
                    var proposal = resolved.Proposals[0];
                    if (proposal.RelativePath != exact.Path || exact.ExpectedVersion is { } expected && expected != proposal.OriginalVersion ||
                        proposal.OriginalVersion.Value != "local-read-v1:sha256:" + proposal.OriginalSha256 ||
                        proposal.Patches.Count != exact.Patches.Count) return false;
                    for (var p = 0; p < exact.Patches.Count; p++)
                        if (proposal.Patches[p].StartOffset != exact.Patches[p].StartOffset || proposal.Patches[p].DeleteLength != exact.Patches[p].DeleteLength ||
                            !proposal.Patches[p].ReplacementUtf8.Span.SequenceEqual(exact.Patches[p].ReplacementUtf8.Span)) return false;
                    var reads = plan.Observations.Where(o => o.NodeIdentity == node.Identity).ToArray();
                    if (reads.Length != 1 || reads[0].Action != ResourceAction.ReadFile || reads[0].RelativePath != exact.Path ||
                        !reads[0].IsComplete || reads[0].Version != proposal.OriginalVersion || reads[0].Digest != proposal.OriginalSha256 ||
                        reads[0].ByteLength != proposal.OriginalByteLength) return false;
                }
            }
            return PreviewIdentity.Document(doc.Workspace, doc.Limits, doc.Nodes.Select(n => n.Operation).ToArray()) == doc.Identity &&
                doc.Nodes.Select((n, i) => n.Index == i && n.Identity == PreviewIdentity.Node(doc.Identity, i)).All(v => v) &&
                PreviewIdentity.Plan(plan.Invocation, doc, plan.Observations, plan.Nodes) == plan.Identity;
        }
        catch { return false; }
    }
    private static bool Id(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return Utf8.GetByteCount(value) <= 1024; } catch (EncoderFallbackException) { return false; }
    }

    private sealed class Boundary(IPatchExecutionHost host, PatchAdmissionRequest admission, CapturedFilePatch proposal)
        : IResourceAuthorizer, IResourceMutationJournal
    {
        private MutationStartRequest? _start;
        private string? _evidence;
        private bool _startCalled;
        private bool _completed;
        private int _calls = 1; // Whole-plan admission; reserve start and completion.
        public ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request, CancellationToken ct = default)
        {
            if (request.Invocation != admission.Invocation || request.SnapshotRequestIdentity != admission.ResourceRequestIdentity)
                return ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Deny));
            if (++_calls > admission.Plan.Limits.MaxResourceCalls - 2)
                return ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Unavailable));
            var role = request.Resource switch
            {
                ResourceBinding.WorkspaceFile f => f.Workspace == admission.Plan.Workspace && f.Path.Value == proposal.RelativePath &&
                    request.Action is ResourceAction.ReadFile or ResourceAction.PatchFile,
                ResourceBinding.WorkspaceEntry e => e.Workspace == admission.Plan.Workspace && request.Action == ResourceAction.ReadMetadata &&
                    (e.Path.Value.Length == 0 || e.Path.Value == proposal.RelativePath || proposal.RelativePath.StartsWith(e.Path.Value + "/", StringComparison.Ordinal)),
                _ => false
            };
            return role ? host.AuthorizeResourceAsync(new(admission, request), ct) :
                ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Deny));
        }
        public async ValueTask<MutationStartDecision> StartAsync(MutationStartRequest request, CancellationToken ct = default)
        {
            if (_startCalled || ++_calls > admission.Plan.Limits.MaxResourceCalls - 1 || request.Invocation != admission.Invocation || request.RequestIdentity != admission.ResourceRequestIdentity ||
                request.Workspace != admission.Plan.Workspace || request.Path.Value != proposal.RelativePath ||
                request.ProviderProfile != admission.WriterProfile || string.IsNullOrWhiteSpace(request.ObjectIdentity) ||
                request.OriginalVersion != proposal.OriginalVersion || request.ProposedVersion.Value != "local-read-v1:sha256:" + proposal.ProposedSha256 ||
                request.OriginalByteLength != proposal.OriginalByteLength || request.ProposedByteLength != proposal.ProposedByteLength)
                return new(MutationStartStatus.Deny);
            _startCalled = true;
            var decision = await host.StartAsync(new(admission, request), ct).ConfigureAwait(false);
            if (decision?.Status == MutationStartStatus.Started && Id(decision.EvidenceId))
            { _start = request; _evidence = decision.EvidenceId; }
            return decision?.Status == MutationStartStatus.Started && _start is null
                ? new(MutationStartStatus.Unavailable) : decision ?? new(MutationStartStatus.Unavailable);
        }
        public ValueTask<bool> CompleteAsync(MutationCompletion completion, CancellationToken ct = default)
        {
            if (_completed || ++_calls > admission.Plan.Limits.MaxResourceCalls || _start != completion.Start || _evidence != completion.EvidenceId) return ValueTask.FromResult(false);
            _completed = true;
            return host.CompleteAsync(new(admission, completion), ct);
        }
    }
}
