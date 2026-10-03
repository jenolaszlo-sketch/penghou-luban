using Penghou.Luban.Changes;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Penghou.Luban.Execution;

public sealed record BatchPredecessor(string SegmentId, string PlanIdentity);

public sealed record BatchAdmissionRequest(string SegmentId, CompiledPreviewDocument Document,
    ResolvedEffectPlan Plan, string WriterProfile,
    IReadOnlyList<PatchAdmissionRequest> Operations, BatchPredecessor? Predecessor);

public enum BatchRecoveryState { NotStarted, Completed, NoMutation, Uncertain }

/// <summary>Host-protected receipt summary for one stable batch operation.</summary>
public sealed record BatchRecoveryEntry(string OperationId, string NodeIdentity,
    BatchRecoveryState State, MutationCompletion? Completion = null);

/// <summary>
/// Inspect must reauthorize release before returning protected entries. A null
/// result means journal availability or release authority is unavailable; it never
/// substitutes for an explicit ordered NotStarted manifest.
/// </summary>
public sealed record BatchRecoverySnapshot(string SegmentId, string PlanIdentity,
    IReadOnlyList<BatchRecoveryEntry> Entries);

/// <summary>
/// Trusted host boundary. Admission binds the segment ID to the complete plan,
/// ordered operation IDs and predecessor, and rejects conflicting reuse. Inspect
/// returns current release-authorized journal state. Start serializes live
/// authority/fence checks with durable start evidence. No default host ships.
/// </summary>
public interface IBatchPatchExecutionHost
{
    ValueTask<LanguageAuthorityDecision> AdmitAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default);
    ValueTask<BatchRecoverySnapshot?> InspectAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default);
    ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(BatchAdmissionRequest request,
        PatchResourceCheck check, CancellationToken cancellationToken = default);
    ValueTask<MutationStartDecision> StartAsync(BatchAdmissionRequest request,
        PatchStartRequest start, CancellationToken cancellationToken = default);
    ValueTask<bool> CompleteAsync(BatchAdmissionRequest request,
        PatchCompletion completion, CancellationToken cancellationToken = default);
}

public sealed record BatchExecutionResult(ResourceFailureKind? Failure,
    IReadOnlyList<BatchRecoveryEntry> Entries)
{
    public bool Succeeded => Failure is null && Entries.Count > 0 && Entries.All(e => e.State == BatchRecoveryState.Completed);
}

/// <summary>
/// Executes a complete, ordered segment containing only exact existing-file
/// patches. It admits and inspects once, authorizes and version-checks all
/// remaining targets before the first write, then stops at the first failure.
/// This is not a multi-file transaction, rollback mechanism, or durable host.
/// </summary>
public sealed class BatchPatchExecutor
{
    private const string OperationDomain = "Penghou.Luban.BatchOperation.v1";
    private const int CompletionTimeoutMilliseconds = 5000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly WorkspaceReference _workspace;
    private readonly IWorkspaceProvider _provider;
    private readonly IBatchPatchExecutionHost _host;
    private readonly SinglePatchExecutor _validator;

    public BatchPatchExecutor(WorkspaceReference workspace, IWorkspaceProvider provider, IBatchPatchExecutionHost host)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (_provider.Workspace.Value != workspace.Id) throw new ArgumentException("Provider workspace does not match the logical workspace.", nameof(provider));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _validator = new SinglePatchExecutor(workspace, provider, new DenyAllPatchHost());
    }

    public async ValueTask<BatchExecutionResult> ExecuteAsync(CompiledPreviewDocument document,
        ResolvedEffectPlan plan, string segmentId, BatchPredecessor? predecessor = null,
        CancellationToken cancellationToken = default)
    {
        var empty = Array.AsReadOnly(Array.Empty<BatchRecoveryEntry>());
        if (cancellationToken.IsCancellationRequested) return new(ResourceFailureKind.ProviderFailure, empty);
        if (!_provider.Capabilities.SupportsReads || !_provider.Capabilities.SupportsConditionalWrites || string.IsNullOrWhiteSpace(_provider.Capabilities.WriteProfile))
            return new(ResourceFailureKind.Unsupported, empty);
        if (!ValidId(segmentId) ||
            (predecessor is not null && (!ValidId(predecessor.SegmentId) || predecessor.SegmentId == segmentId ||
                !ValidPlanIdentity(predecessor.PlanIdentity))))
            return new(ResourceFailureKind.InvalidRequest, empty);

        var frozen = FreezeAndValidate(document, plan, segmentId);
        if (frozen.Failure is { } validationFailure) return new(validationFailure, empty);
        document = frozen.Document!;
        plan = frozen.Plan!;
        var count = document.Nodes.Count;
        var operationIds = new string[count];
        var admissions = new PatchAdmissionRequest[count];
        for (var i = 0; i < count; i++)
        {
            operationIds[i] = MakeOperationId(segmentId, document.Nodes[i].Identity);
            admissions[i] = SinglePatchExecutor.CreateAdmission(document, plan, operationIds[i], i, _provider.Capabilities.WriteProfile!);
        }
        var batch = new BatchAdmissionRequest(segmentId, document, plan, _provider.Capabilities.WriteProfile!,
            Array.AsReadOnly((PatchAdmissionRequest[])admissions.Clone()), predecessor);

        // Worst-case host calls include semantic PatchFile rechecks paired with
        // both concrete WriteFile authorization boundaries.
        long minimumCalls = 2;
        foreach (var node in document.Nodes)
            minimumCalls = checked(minimumCalls + 3L * ((ExactPatchOperation)node.Operation).Path.Split('/').Length + 14);
        if (minimumCalls > document.Limits.MaxResourceCalls) return new(ResourceFailureKind.TooLarge, empty);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(document.Limits.TimeoutMilliseconds);
        var callBudget = new CallBudget(document.Limits.MaxResourceCalls);
        if (!callBudget.TryUse()) return new(ResourceFailureKind.TooLarge, empty);
        LanguageAuthorityDecision? decision;
        try { decision = await _host.AdmitAsync(batch, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(ResourceFailureKind.ProviderFailure, empty); }
        catch { return new(ResourceFailureKind.AuthorizationUnavailable, empty); }
        if (decision?.Status != LanguageAuthorityStatus.Permit)
            return new(decision?.Status == LanguageAuthorityStatus.Deny ? ResourceFailureKind.AuthorizationDenied : ResourceFailureKind.AuthorizationUnavailable, empty);

        if (!callBudget.TryUse()) return new(ResourceFailureKind.TooLarge, empty);
        BatchRecoverySnapshot? snapshot;
        try { snapshot = await _host.InspectAsync(batch, deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(ResourceFailureKind.ProviderFailure, empty); }
        catch { return new(ResourceFailureKind.AuthorizationUnavailable, empty); }
        if (snapshot is null) return new(ResourceFailureKind.AuthorizationUnavailable, empty);
        Recovery inspected;
        try { inspected = ValidateSnapshot(snapshot, batch); }
        catch { return new(ResourceFailureKind.AmbiguousOutcome, empty); }
        if (inspected.Failure is { } inspectionFailure) return new(inspectionFailure, inspected.Entries);
        var entries = inspected.Entries.ToArray();
        var completedPrefix = 0;
        while (completedPrefix < count && entries[completedPrefix].State == BatchRecoveryState.Completed) completedPrefix++;
        if (entries.Skip(completedPrefix).Any(e => e.State is BatchRecoveryState.NoMutation or BatchRecoveryState.Uncertain) ||
            entries.Skip(completedPrefix).Any(e => e.State != BatchRecoveryState.NotStarted))
            return new(ResourceFailureKind.AmbiguousOutcome, Array.AsReadOnly(entries));
        if (completedPrefix == count) return new(null, Array.AsReadOnly(entries));

        long aggregateIo = 0;
        for (var i = completedPrefix; i < count; i++)
        {
            var p = plan.Nodes[i].Proposals[0];
            aggregateIo = checked(aggregateIo + 2L * p.OriginalByteLength + p.ProposedByteLength);
        }
        if (aggregateIo > document.Limits.MaxReadBytes)
            return new(ResourceFailureKind.TooLarge, Array.AsReadOnly(entries));

        // Whole remaining-set rights before any file-content read.
        try
        {
            for (var i = completedPrefix; i < count; i++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var proposal = plan.Nodes[i].Proposals[0];
                foreach (var check in PreflightChecks(document.Workspace, proposal.RelativePath, admissions[i]))
                {
                    if (!callBudget.TryUse()) return new(ResourceFailureKind.TooLarge, Array.AsReadOnly(entries));
                    var decisionForResource = await _host.AuthorizeResourceAsync(batch, check, deadline.Token)
                        .AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (decisionForResource?.Status != AuthorizationStatus.Permit)
                        return new(decisionForResource?.Status == AuthorizationStatus.Deny ? ResourceFailureKind.AuthorizationDenied : ResourceFailureKind.AuthorizationUnavailable,
                            Array.AsReadOnly(entries));
                }
            }
        }
        catch (OperationCanceledException) { return new(ResourceFailureKind.ProviderFailure, Array.AsReadOnly(entries)); }
        catch { return new(ResourceFailureKind.AuthorizationUnavailable, Array.AsReadOnly(entries)); }

        // Bounded read-only readiness validation of every remaining original before writes.
        try
        {
            for (var i = completedPrefix; i < count; i++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var proposal = plan.Nodes[i].Proposals[0];
                var readLimit = Math.Max(1, Math.Min(document.Limits.MaxFileBytes, checked(proposal.OriginalByteLength + 1)));
                var draft = new FileReadRequest(admissions[i].Invocation, document.Workspace,
                    new WorkspacePath(proposal.RelativePath), new IoLimits(readLimit));
                var readIdentity = ResourceRequestIdentity.Compute(draft);
                var request = draft with { Invocation = draft.Invocation with { RequestIdentity = readIdentity } };
                var reader = _provider.OpenReader(new ReadBoundary(_host, batch, admissions[i], proposal.RelativePath, readIdentity, callBudget));
                using (reader)
                {
                    var result = await reader.ReadFileAsync(request, deadline.Token).ConfigureAwait(false);
                    if (result.Failure is { } failure) return new(failure, Array.AsReadOnly(entries));
                    var original = result.Value!.Content.ToArray();
                    try
                    {
                        if (original.Length != proposal.OriginalByteLength || result.Value.Version != proposal.OriginalVersion ||
                            Convert.ToHexString(SHA256.HashData(original)) != proposal.OriginalSha256)
                            return new(ResourceFailureKind.PreconditionFailed, Array.AsReadOnly(entries));
                    }
                    finally { CryptographicOperations.ZeroMemory(original); }
                }
            }
        }
        catch (OperationCanceledException) { return new(ResourceFailureKind.ProviderFailure, Array.AsReadOnly(entries)); }
        catch { return new(ResourceFailureKind.ProviderFailure, Array.AsReadOnly(entries)); }

        // A writer-side Unsupported can still occur after earlier nodes have
        // completed (for example a late hard-link alias); preserve receipts and
        // never claim all native constraints were preflighted.
        var bridge = new HostBridge(_host, batch, admissions, callBudget);
        var executor = new SinglePatchExecutor(_workspace, _provider, bridge);
        for (var i = completedPrefix; i < count; i++)
        {
            var state = new OperationState(operationIds[i], document.Nodes[i].Identity);
            bridge.SetCurrent(i, state);
            PatchExecutionResult result;
            try { result = await executor.ExecuteNodeAsync(document, plan, operationIds[i], i, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { state.OnCancellation(); result = new(ResourceFailureKind.ProviderFailure); }
            catch { state.OnException(); result = new(ResourceFailureKind.ProviderFailure); }
            entries[i] = state.ToEntry();
            if (result.Failure is { } failure)
                return new(entries[i].State == BatchRecoveryState.Uncertain
                    ? ResourceFailureKind.AmbiguousOutcome : failure, Array.AsReadOnly(entries));
            if (entries[i].State != BatchRecoveryState.Completed)
                return new(entries[i].State == BatchRecoveryState.NoMutation ? ResourceFailureKind.ProviderFailure :
                    ResourceFailureKind.AmbiguousOutcome, Array.AsReadOnly(entries));
        }
        return new(null, Array.AsReadOnly(entries));
    }

    private Validation FreezeAndValidate(CompiledPreviewDocument? document, ResolvedEffectPlan? plan, string segmentId)
    {
        var replacementArrays = new List<byte[]>();
        try
        {
            if (document is null || plan is null || document.Limits is null || document.Nodes.Count is < 1 or > 64 ||
                plan.Nodes.Count is < 1 or > 64 || plan.Observations.Count > 64 ||
                plan.Observations.Count > document.Limits.MaxResourceCalls || document.Nodes.Count != plan.Nodes.Count ||
                !PreviewCompiler.ValidLimits(document.Limits) || document.Workspace.Value != _workspace.Id || !ValidId(plan.Invocation.SubjectId) ||
                !ValidId(plan.Invocation.EffectId) || !ValidId(plan.Invocation.AttemptId) || plan.DocumentIdentity != document.Identity ||
                plan.Workspace != document.Workspace || plan.Limits != document.Limits)
                return new(ResourceFailureKind.InvalidRequest);
            if (!plan.CaptureComplete) return new(ResourceFailureKind.Unsupported);
            if (PreviewIdentity.RetainedPlanSize(plan.Invocation, document, plan.Observations, plan.Nodes) > document.Limits.MaxPlanBytes)
                return new(ResourceFailureKind.InvalidRequest);
            var stages = new List<PreviewStage>(document.Nodes.Count);
            var clonedNodes = new List<ResolvedPreviewNode>(plan.Nodes.Count);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalPatches = 0, totalReplacementBytes = 0;
            for (var i = 0; i < document.Nodes.Count; i++)
            {
                var node = document.Nodes[i];
                var resolved = plan.Nodes[i];
                if (node.Index != i || node.Operation is not ExactPatchOperation exact ||
                    resolved.State != PreviewNodeState.Proposed || resolved.Selection != PreviewSelection.Unspecified ||
                    resolved.UnresolvedReason is not null || resolved.NodeIdentity != node.Identity ||
                    resolved.Descriptor != node.Descriptor || resolved.Proposals.Count != 1 ||
                    resolved.Dependencies.Count != (i == 0 ? 0 : 1) ||
                    (i > 0 && resolved.Dependencies[0] != document.Nodes[i - 1].Identity))
                    return new(ResourceFailureKind.Unsupported);
                if (exact.Path.Length > WindowsWorkspacePath.MaximumLength) return new(ResourceFailureKind.InvalidRequest);
                var path = WindowsWorkspacePath.Normalize(new WorkspacePath(exact.Path), false).Value;
                if (!targets.Add(path)) return new(ResourceFailureKind.InvalidRequest);
                var proposal = resolved.Proposals[0];
                if (ProposalInvalid(proposal, exact, document.Limits.MaxFileBytes, document.Limits.MaxReplacementBytes)) return new(ResourceFailureKind.InvalidRequest);
                if (proposal.OriginalByteLength > document.Limits.MaxFileBytes || proposal.ProposedByteLength > document.Limits.MaxFileBytes)
                    return new(ResourceFailureKind.TooLarge);
                totalPatches += proposal.Patches.Count;
                if (proposal.Patches.Count is < 1 or > 128 || totalPatches > document.Limits.MaxPatchCount)
                    return new(ResourceFailureKind.TooLarge);
                foreach (var patch in proposal.Patches)
                {
                    totalReplacementBytes = checked(totalReplacementBytes + patch.ReplacementUtf8.Length);
                    if (patch.ReplacementUtf8.Length > document.Limits.MaxReplacementBytes ||
                        totalReplacementBytes > document.Limits.MaxReplacementBytes)
                        return new(ResourceFailureKind.TooLarge);
                }
                for (var patchIndex = 0; patchIndex < proposal.Patches.Count; patchIndex++)
                    if (!proposal.Patches[patchIndex].ReplacementUtf8.Span.SequenceEqual(exact.Patches[patchIndex].ReplacementUtf8.Span))
                        return new(ResourceFailureKind.InvalidRequest);
                var stagePatches = new List<TextPatch>(proposal.Patches.Count);
                var clonedPatches = new List<FrozenTextPatch>(proposal.Patches.Count);
                foreach (var patch in proposal.Patches)
                {
                    var bytes = patch.ReplacementUtf8.ToArray();
                    replacementArrays.Add(bytes);
                    stagePatches.Add(new TextPatch(patch.StartOffset, patch.DeleteLength, bytes));
                    clonedPatches.Add(new FrozenTextPatch(patch.StartOffset, patch.DeleteLength, new ImmutableBytes(bytes)));
                }
                stages.Add(new FilePatchStage(path, new ReadOnlyCollection<TextPatch>(stagePatches), exact.ExpectedVersion));
                clonedNodes.Add(new ResolvedPreviewNode(resolved.NodeIdentity, resolved.Descriptor, resolved.State,
                    resolved.Selection,
                    Array.AsReadOnly(new[] { proposal with { RelativePath = path, Patches = Array.AsReadOnly(clonedPatches.ToArray()) } }),
                    Array.AsReadOnly(resolved.Dependencies.ToArray()), resolved.UnresolvedReason));
            }
            var compilation = PreviewCompiler.Compile(stages, document.Workspace, document.Limits);
            if (!compilation.Succeeded || compilation.Document!.Identity != document.Identity)
                return new(ResourceFailureKind.InvalidRequest);
            var observations = new List<PreviewObservation>(plan.Observations.Count);
            foreach (var observation in plan.Observations)
            {
                if (observation is null || observation.NodeIdentity.Length > 64 || observation.RelativePath.Length > WindowsWorkspacePath.MaximumLength ||
                    observation.Digest.Length > 128 || observation.RequestIdentity.Value.Length > 256 || observation.ByteLength < 0)
                    return new(ResourceFailureKind.InvalidRequest);
                observations.Add(observation with { });
            }
            if (PreviewIdentity.RetainedPlanSize(plan.Invocation, document, observations, clonedNodes) > document.Limits.MaxPlanBytes)
                return new(ResourceFailureKind.InvalidRequest);
            var frozenDocument = compilation.Document;
            var frozenPlan = new ResolvedEffectPlan(plan.Invocation, frozenDocument, plan.Identity,
                new ReadOnlyCollection<PreviewObservation>(observations), new ReadOnlyCollection<ResolvedPreviewNode>(clonedNodes));
            if (PreviewIdentity.Plan(plan.Invocation, frozenDocument, frozenPlan.Observations, frozenPlan.Nodes) != plan.Identity ||
                !_validator.Validate(frozenDocument, frozenPlan, MakeOperationId(segmentId, frozenDocument.Nodes[0].Identity)))
                return new(ResourceFailureKind.InvalidRequest);
            return new(frozenDocument, frozenPlan);
        }
        catch { return new(ResourceFailureKind.InvalidRequest); }
        finally { foreach (var bytes in replacementArrays) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool ProposalInvalid(CapturedFilePatch proposal, ExactPatchOperation exact,
        int maxFileBytes, int maxReplacementBytes)
    {
        if (proposal is null || proposal.RelativePath != exact.Path || proposal.OriginalByteLength < 0 ||
            proposal.ProposedByteLength < 0 || proposal.OriginalByteLength > maxFileBytes || proposal.ProposedByteLength > maxFileBytes ||
            proposal.OriginalVersion.Value is null || proposal.OriginalVersion.Value.Length > 256 || !ValidHash(proposal.OriginalSha256) || !ValidHash(proposal.ProposedSha256) ||
            proposal.OriginalContent is null || proposal.OriginalContent.Length != proposal.OriginalByteLength ||
            proposal.OriginalContent.Sha256 != proposal.OriginalSha256 ||
            proposal.ProposedContent is null || proposal.ProposedContent.Length != proposal.ProposedByteLength ||
            proposal.ProposedContent.Sha256 != proposal.ProposedSha256 ||
            proposal.Patches is null || exact.Patches is null || proposal.Patches.Count is < 1 or > 128 ||
            exact.Patches.Count != proposal.Patches.Count) return true;
        long replacementBytes = 0;
        for (var i = 0; i < proposal.Patches.Count; i++)
        {
            var a = proposal.Patches[i]; var b = exact.Patches[i];
            if (a is null || b is null || a.StartOffset != b.StartOffset || a.DeleteLength != b.DeleteLength ||
                a.ReplacementUtf8.Length != b.ReplacementUtf8.Length) return true;
            replacementBytes = checked(replacementBytes + a.ReplacementUtf8.Length);
            if (a.ReplacementUtf8.Length > maxReplacementBytes || replacementBytes > maxReplacementBytes) return true;
        }
        return false;
    }

    private static Recovery ValidateSnapshot(BatchRecoverySnapshot? snapshot, BatchAdmissionRequest batch)
    {
        if (snapshot is null) return new(ResourceFailureKind.AuthorizationUnavailable);
        var count = batch.Operations.Count;
        if (snapshot.SegmentId != batch.SegmentId || snapshot.PlanIdentity != batch.Plan.Identity ||
            snapshot.Entries is null || snapshot.Entries.Count != count || snapshot.Entries.Count > 64)
            return new(ResourceFailureKind.AmbiguousOutcome);
        var copy = new BatchRecoveryEntry[count];
        var prefixClosed = false;
        for (var i = 0; i < count; i++)
        {
            var entry = snapshot.Entries[i];
            var operation = batch.Operations[i];
            if (entry is null || entry.OperationId != operation.OperationId || entry.NodeIdentity != batch.Document.Nodes[i].Identity ||
                !Enum.IsDefined(entry.State)) return new(ResourceFailureKind.AmbiguousOutcome);
            if (entry.State != BatchRecoveryState.Completed) prefixClosed = true;
            else if (prefixClosed) return new(ResourceFailureKind.AmbiguousOutcome);
            if (!ValidReceipt(entry, operation, batch.Plan.Nodes[i].Proposals[0]))
                return new(ResourceFailureKind.AmbiguousOutcome);
            copy[i] = entry with { };
        }
        return new(Array.AsReadOnly(copy));
    }

    private static bool ValidReceipt(BatchRecoveryEntry entry, PatchAdmissionRequest admission, CapturedFilePatch proposal)
    {
        if (entry.State == BatchRecoveryState.NotStarted) return entry.Completion is null;
        if (entry.State == BatchRecoveryState.Uncertain)
            return entry.Completion is null || (entry.Completion.Outcome == MutationOutcome.Ambiguous &&
                ValidCompletion(entry.Completion, admission, proposal, true));
        if (entry.Completion is null || !ValidCompletion(entry.Completion, admission, proposal, false)) return false;
        var mutation = entry.Completion;
        return entry.State switch
        {
            BatchRecoveryState.Completed => mutation.Outcome == MutationOutcome.Completed &&
                mutation.ObservedVersion == mutation.Start.ProposedVersion,
            BatchRecoveryState.NoMutation => mutation.Outcome == MutationOutcome.NoMutation &&
                mutation.ObservedVersion == proposal.OriginalVersion,
            _ => false
        };
    }

    private static bool ValidCompletion(MutationCompletion mutation, PatchAdmissionRequest admission,
        CapturedFilePatch proposal, bool allowAmbiguous)
    {
        var start = mutation.Start;
        if (start is null || !ValidId(mutation.EvidenceId) ||
            start.Invocation != admission.Invocation || start.RequestIdentity != admission.ResourceRequestIdentity ||
            start.Workspace != admission.Plan.Workspace || start.Path.Value != proposal.RelativePath ||
            start.ProviderProfile != admission.WriterProfile || !ValidId(start.ObjectIdentity) ||
            start.OriginalVersion != proposal.OriginalVersion ||
            string.IsNullOrWhiteSpace(start.ProposedVersion.Value) || start.ProposedVersion.Value.Length > 256 ||
            start.OriginalByteLength != proposal.OriginalByteLength || start.ProposedByteLength != proposal.ProposedByteLength) return false;
        return mutation.Outcome is MutationOutcome.Completed or MutationOutcome.NoMutation ||
            (allowAmbiguous && mutation.Outcome == MutationOutcome.Ambiguous);
    }

    private static IEnumerable<PatchResourceCheck> PreflightChecks(WorkspaceId workspace, string path,
        PatchAdmissionRequest admission)
    {
        var file = new ResourceBinding.WorkspaceFile(workspace, new WorkspacePath(path));
        yield return Check(ResourceAction.PatchFile, file);
        yield return Check(ResourceAction.ReadFile, file);
        yield return Check(ResourceAction.ReadMetadata, new ResourceBinding.WorkspaceEntry(workspace, WorkspacePath.Root));
        var parts = path.Split('/'); var current = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
            yield return Check(ResourceAction.ReadMetadata, new ResourceBinding.WorkspaceEntry(workspace, new WorkspacePath(current)));
        }
        yield return Check(ResourceAction.ReadMetadata, new ResourceBinding.WorkspaceEntry(workspace, new WorkspacePath(path)));

        PatchResourceCheck Check(ResourceAction action, ResourceBinding resource) => new(admission,
            new ResourceAuthorizationRequest(admission.Invocation, admission.ResourceRequestIdentity, action, resource));
    }

    private static string MakeOperationId(string segmentId, string nodeIdentity)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            // Canonical frames are Int32 little-endian UTF-8 byte lengths followed by strict UTF-8 bytes.
            Frame(writer, OperationDomain); Frame(writer, segmentId); Frame(writer, nodeIdentity); writer.Flush();
        }
        return "batch-" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Frame(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; }
        catch (EncoderFallbackException) { return false; }
    }
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool ValidPlanIdentity(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record Validation(CompiledPreviewDocument? Document, ResolvedEffectPlan? Plan, ResourceFailureKind? Failure = null)
    {
        internal Validation(ResourceFailureKind failure) : this(null, null, failure) { }
        internal Validation(CompiledPreviewDocument document, ResolvedEffectPlan plan) : this(document, plan, null) { }
    }
    private sealed record Recovery(IReadOnlyList<BatchRecoveryEntry> Entries, ResourceFailureKind? Failure = null)
    {
        internal Recovery(ResourceFailureKind failure) : this(Array.AsReadOnly(Array.Empty<BatchRecoveryEntry>()), failure) { }
        internal Recovery(IReadOnlyList<BatchRecoveryEntry> entries) : this(entries, null) { }
    }

    private sealed class CallBudget(int maximum)
    {
        private int _used;
        internal bool TryUse() => Interlocked.Increment(ref _used) <= maximum;
    }

    private sealed class ReadBoundary(IBatchPatchExecutionHost host, BatchAdmissionRequest batch,
        PatchAdmissionRequest admission, string exactPath, RequestIdentity expectedIdentity, CallBudget calls) : IResourceAuthorizer
    {
        public async ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null || request.SnapshotRequestIdentity != expectedIdentity || !SameContext(request.Invocation, admission.Invocation) ||
                request.Invocation.RequestIdentity != expectedIdentity ||
                !Allowed(batch.Document.Workspace, exactPath, request)) return new(AuthorizationStatus.Deny);
            if (!calls.TryUse()) return new(AuthorizationStatus.Unavailable);
            return await host.AuthorizeResourceAsync(batch, new PatchResourceCheck(admission, request), cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class HostBridge(IBatchPatchExecutionHost host, BatchAdmissionRequest batch,
        PatchAdmissionRequest[] admissions, CallBudget calls) : IPatchExecutionHost
    {
        private int _index = -1;
        private OperationState? _state;
        internal void SetCurrent(int index, OperationState state) { _index = index; _state = state; }

        public ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Matches(request) ? new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit) :
                new LanguageAuthorityDecision(LanguageAuthorityStatus.Deny)); // whole batch admission already succeeded

        public async ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck check,
            CancellationToken cancellationToken = default)
        {
            if (!Matches(check.Admission) || _index < 0 || check.Admission != admissions[_index] ||
                !Allowed(batch.Document.Workspace, admissions[_index].Plan.Nodes[_index].Proposals[0].RelativePath, check.Resource)) return new(AuthorizationStatus.Deny);
            if (check.Resource.Action == ResourceAction.WriteFile)
            {
                if (!calls.TryUse()) return new(AuthorizationStatus.Unavailable);
                var patchDecision = await host.AuthorizeResourceAsync(batch,
                    check with { Resource = check.Resource with { Action = ResourceAction.PatchFile } }, cancellationToken)
                    .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (patchDecision?.Status != AuthorizationStatus.Permit) return patchDecision ?? new(AuthorizationStatus.Unavailable);
            }
            if (!calls.TryUse()) return new(AuthorizationStatus.Unavailable);
            return await host.AuthorizeResourceAsync(batch, check, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<MutationStartDecision> StartAsync(PatchStartRequest start, CancellationToken cancellationToken = default)
        {
            if (!Matches(start.Admission) || _state is null || start.Admission != admissions[_index]) return new(MutationStartStatus.Deny);
            if (!calls.TryUse()) { _state.StartUnknown(); return new(MutationStartStatus.Unavailable); }
            _state.StartUnknown(); // From the call boundary onward, lost/unknown replies are uncertain.
            try
            {
                var result = await host.StartAsync(batch, start, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result?.Status == MutationStartStatus.Deny) _state.NotStarted();
                return result ?? new(MutationStartStatus.Unavailable);
            }
            catch { return new(MutationStartStatus.Unavailable); }
        }

        public async ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default)
        {
            if (!Matches(completion.Admission) || _state is null || completion.Admission != admissions[_index])
            { _state?.StartUnknown(); return false; }
            if (!calls.TryUse()) { _state.StartUnknown(); return false; }
            using var independent = new CancellationTokenSource(CompletionTimeoutMilliseconds);
            try
            {
                var accepted = await host.CompleteAsync(batch, completion, independent.Token).AsTask()
                    .WaitAsync(independent.Token).ConfigureAwait(false);
                if (!accepted) { _state.StartUnknown(); return false; }
                _state.Record(completion.Mutation);
                return true;
            }
            catch { _state.StartUnknown(); return false; }
        }

        private bool Matches(PatchAdmissionRequest admission) => _index >= 0 && _index < admissions.Length &&
            admission == admissions[_index] && admission.Plan.Identity == batch.Plan.Identity &&
            admission.Document.Identity == batch.Document.Identity;
    }

    private static bool SameContext(HostInvocation a, HostInvocation b) => a.InvocationId == b.InvocationId &&
        a.SubjectId == b.SubjectId && a.EffectId == b.EffectId && a.AttemptId == b.AttemptId &&
        a.EffectScopeId == b.EffectScopeId && a.ParentEffectScopeId == b.ParentEffectScopeId;

    private static bool Allowed(WorkspaceId workspace, string path, ResourceAuthorizationRequest request) => request.Action switch
    {
        ResourceAction.ReadFile or ResourceAction.PatchFile or ResourceAction.WriteFile => request.Resource is ResourceBinding.WorkspaceFile f &&
            f.Workspace == workspace && f.Path.Value == path,
        ResourceAction.ReadMetadata => request.Resource is ResourceBinding.WorkspaceEntry e &&
            e.Workspace == workspace && IsAncestorOrSelf(e.Path.Value, path),
        _ => false
    };

    private static bool IsAncestorOrSelf(string candidate, string path) => candidate.Length == 0 ||
        candidate == path || path.StartsWith(candidate + "/", StringComparison.Ordinal);

    private sealed class OperationState(string operationId, string nodeIdentity)
    {
        private BatchRecoveryState _state = BatchRecoveryState.NotStarted;
        private MutationCompletion? _completion;
        internal BatchRecoveryEntry ToEntry() => new(operationId, nodeIdentity, _state, _completion);
        internal void StartUnknown() => _state = BatchRecoveryState.Uncertain;
        internal void NotStarted() => _state = BatchRecoveryState.NotStarted;
        internal void Record(MutationCompletion completion)
        {
            _completion = completion;
            _state = completion.Outcome switch
            {
                MutationOutcome.Completed => BatchRecoveryState.Completed,
                MutationOutcome.NoMutation => BatchRecoveryState.NoMutation,
                _ => BatchRecoveryState.Uncertain
            };
        }
        internal void OnCancellation() { if (_state is not (BatchRecoveryState.NotStarted or BatchRecoveryState.Completed or BatchRecoveryState.NoMutation)) _state = BatchRecoveryState.Uncertain; }
        internal void OnException() { if (_state is not (BatchRecoveryState.NotStarted or BatchRecoveryState.Completed or BatchRecoveryState.NoMutation)) _state = BatchRecoveryState.Uncertain; }
    }
    private sealed class DenyAllPatchHost : IPatchExecutionHost
    {
        public ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Deny));
        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ResourceAuthorizationDecision(AuthorizationStatus.Deny));
        public ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.Deny));
        public ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }
}
