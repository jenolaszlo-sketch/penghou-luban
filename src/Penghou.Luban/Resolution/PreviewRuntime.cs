using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban.Language;

namespace Penghou.Luban.Resolution;

/// <summary>Authorized observations and immutable patch capture. No mutation dispatch exists.</summary>
public sealed class PreviewRuntime
{
    private readonly WorkspaceReference _workspace;
    private readonly IPreviewAuthorizer _authorizer;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public PreviewRuntime(WorkspaceReference workspace, IPreviewAuthorizer authorizer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        if (!ValidId(workspace.Id)) throw new ArgumentException("A bounded workspace ID is required.", nameof(workspace));
    }

    public async ValueTask<PreviewReadiness> PreflightAsync(EffectInvocation invocation, CompiledPreviewDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(invocation, document)) return new(PreviewRunStatus.InvalidDocument, false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(document.Limits.TimeoutMilliseconds);
        try
        {
            await Preflight(invocation, document, new(document.Limits), deadline.Token).ConfigureAwait(false);
            return new(PreviewRunStatus.Succeeded, Dynamic(document));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(PreviewRunStatus.LimitExceeded, Dynamic(document)); }
        catch (Failure ex) { return new(ex.Status, Dynamic(document)); }
    }

    public async ValueTask<PreviewRunResult> WhatIfAsync(EffectInvocation invocation, CompiledPreviewDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(invocation, document)) return new(PreviewRunStatus.InvalidDocument);
        if (!OperatingSystem.IsWindows()) return new(PreviewRunStatus.UnsupportedProfile);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(document.Limits.TimeoutMilliseconds);
        var ct = deadline.Token;
        try
        {
            var budget = new Budget(document.Limits);
            await Preflight(invocation, document, budget, ct).ConfigureAwait(false);
            var observations = new List<PreviewObservation>();
            var resourceReleases = new HashSet<ResourceRelease>();
            void RetainMetadata(CompiledPreviewNode node, string path, ResourceAction action, RequestIdentity identity)
            {
                if (action == ResourceAction.ReadMetadata && resourceReleases.Add(new(node, path, action, identity)))
                    budget.Retain(160 + Encoding.UTF8.GetByteCount(path));
            }
            var manifests = new List<Manifest>();
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var opaque = false;
            var unresolvedDependency = false;
            foreach (var node in document.Nodes)
            {
                ct.ThrowIfCancellationRequested();
                budget.Retain(192);
                if (opaque) { manifests.Add(new(node, [], PreviewUnresolvedReason.DependsOnOpaqueEffect)); continue; }
                if (unresolvedDependency) { manifests.Add(new(node, [], PreviewUnresolvedReason.DependsOnUnresolvedEffect)); continue; }
                if (node.Operation is DeferredToolOperation)
                { opaque = true; manifests.Add(new(node, [], PreviewUnresolvedReason.OpaqueEffect)); continue; }
                List<string> paths;
                if (node.Operation is ExactPatchOperation exact) paths = [exact.Path];
                else if (node.Operation is SelectedPatchOperation glob)
                {
                    if (glob.Selection == PreviewSelection.AllMatches)
                    { unresolvedDependency = true; manifests.Add(new(node, [], PreviewUnresolvedReason.AllMatchCoverageUnavailable)); continue; }
                    using var access = new Access(this, invocation, document, node, budget, glob.Limits.MaxEntries, RetainMetadata);
                    var found = await Discover(access, glob, observations, ct).ConfigureAwait(false);
                    if (!found.Complete) { unresolvedDependency = true; manifests.Add(new(node, [], PreviewUnresolvedReason.IncompleteSelection)); continue; }
                    paths = found.Paths;
                }
                else throw new Failure(PreviewRunStatus.InvalidDocument);
                if (paths.Any(p => selected.Contains(p)))
                { unresolvedDependency = true; manifests.Add(new(node, [], PreviewUnresolvedReason.RepeatedTarget)); continue; }
                foreach (var path in paths) { selected.Add(path); budget.Target(path); }
                manifests.Add(new(node, paths, null));
            }

            // Freeze the entire known manifest and admit every target before reading
            // any file content. A denied dynamic target cannot yield an allowed prefix.
            foreach (var manifest in manifests.Where(m => m.Reason is null))
                foreach (var path in manifest.Paths)
                    await Demand(invocation, document, manifest.Node, PreviewAuthorizationPhase.TargetAdmission,
                        budget, ct, path, ResourceAction.PatchFile).ConfigureAwait(false);

            var nodes = new List<ResolvedPreviewNode>();
            foreach (var manifest in manifests)
            {
                var node = manifest.Node;
                var dependencies = node.Index == 0 ? Array.Empty<string>() : new[] { document.Nodes[node.Index - 1].Identity };
                var selection = node.Operation is SelectedPatchOperation glob ? glob.Selection : PreviewSelection.Unspecified;
                if (manifest.Reason is { } reason)
                {
                    nodes.Add(new(node.Identity, node.Descriptor, PreviewNodeState.Unresolved, selection,
                        Array.Empty<CapturedFilePatch>(), Array.AsReadOnly(dependencies), reason));
                    continue;
                }
                var proposals = new List<CapturedFilePatch>();
                using var access = new Access(this, invocation, document, node, budget, 1, RetainMetadata);
                foreach (var path in manifest.Paths)
                {
                    var data = await Read(access, path, ct).ConfigureAwait(false);
                    var patches = Patches(node.Operation);
                    if (node.Operation is ExactPatchOperation { ExpectedVersion: { } expected } && expected != data.Version)
                        throw new Failure(PreviewRunStatus.StaleObservation);
                    var proposedBytes = Apply(data.Bytes, patches, document.Limits.MaxFileBytes, ct);
                    var originalHash = Convert.ToHexString(SHA256.HashData(data.Bytes));
                    var proposal = new CapturedFilePatch(path, data.Version, originalHash, data.Bytes.Length,
                        Convert.ToHexString(SHA256.HashData(proposedBytes)), proposedBytes.Length, patches);
                    budget.Retain(512 + Encoding.UTF8.GetByteCount(path) + patches.Sum(p => 16 + p.ReplacementUtf8.Length));
                    observations.Add(new(node.Identity, path, ResourceAction.ReadFile, data.Identity,
                        data.Version, originalHash, data.Bytes.Length, true));
                    budget.Retain(256 + Encoding.UTF8.GetByteCount(path));
                    await Demand(invocation, document, node, PreviewAuthorizationPhase.ProposalAdmission,
                        budget, ct, path, ResourceAction.PatchFile, proposal: proposal).ConfigureAwait(false);
                    proposals.Add(proposal);
                }
                nodes.Add(new(node.Identity, node.Descriptor, PreviewNodeState.Proposed, selection,
                    Array.AsReadOnly(proposals.ToArray()), Array.AsReadOnly(dependencies), null));
            }
            var identity = PreviewIdentity.Plan(invocation, document, observations, nodes);
            // The exact encoding is independently bounded, not merely the memory estimate.
            if (PreviewIdentity.PlanSize(invocation, document, observations, nodes) > document.Limits.MaxPlanBytes)
                throw new Failure(PreviewRunStatus.LimitExceeded);
            var plan = new ResolvedEffectPlan(invocation, document, identity, observations, nodes);
            foreach (var node in document.Nodes)
                await Demand(invocation, document, node, PreviewAuthorizationPhase.Release, budget, ct, planIdentity: identity).ConfigureAwait(false);
            // Page digests also depend on authorized nonmatching candidate metadata.
            // Retain private release dependencies without publishing those names.
            foreach (var binding in resourceReleases)
                await Demand(invocation, document, binding.Node, PreviewAuthorizationPhase.Release, budget, ct,
                    binding.Path, binding.Action, binding.Identity, planIdentity: identity).ConfigureAwait(false);
            foreach (var observation in observations)
                await Demand(invocation, document, document.Nodes.First(n => n.Identity == observation.NodeIdentity),
                    PreviewAuthorizationPhase.Release, budget, ct, observation.RelativePath, observation.Action,
                    observation.RequestIdentity, planIdentity: identity).ConfigureAwait(false);
            foreach (var resolved in nodes)
                foreach (var proposal in resolved.Proposals)
                    await Demand(invocation, document, document.Nodes.First(n => n.Identity == resolved.NodeIdentity),
                        PreviewAuthorizationPhase.Release, budget, ct, proposal.RelativePath, ResourceAction.PatchFile,
                        proposal: proposal, planIdentity: identity).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(plan.CaptureComplete ? PreviewRunStatus.Succeeded : PreviewRunStatus.Incomplete, plan);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(PreviewRunStatus.LimitExceeded); }
        catch (OperationCanceledException) { throw; }
        catch (Failure ex) { return new(ex.Status); }
        catch (InvalidDataException) { return new(PreviewRunStatus.LimitExceeded); }
        catch { return new(PreviewRunStatus.ProviderFailure); }
    }

    private async ValueTask Preflight(EffectInvocation invocation, CompiledPreviewDocument document, Budget budget, CancellationToken ct)
    {
        foreach (var node in document.Nodes)
            await Demand(invocation, document, node, PreviewAuthorizationPhase.Preflight, budget, ct).ConfigureAwait(false);
    }

    private bool Validate(EffectInvocation? invocation, CompiledPreviewDocument? doc)
    {
        if (invocation is null || !ValidId(invocation.SubjectId) || !ValidId(invocation.EffectId) || !ValidId(invocation.AttemptId) || doc is null || doc.Workspace.Value != _workspace.Id)
            return false;
        try
        {
            if (doc.Nodes.Count is < 1 or > 64 || PreviewIdentity.Document(doc.Workspace, doc.Limits, doc.Nodes.Select(n => n.Operation).ToArray()) != doc.Identity) return false;
            return doc.Nodes.Select((node, index) => node.Index == index && node.Identity == PreviewIdentity.Node(doc.Identity, index)).All(v => v);
        }
        catch { return false; }
    }
    private static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return Utf8.GetByteCount(value) <= 1024; }
        catch (EncoderFallbackException) { return false; }
    }
    private static bool Dynamic(CompiledPreviewDocument document) => document.Nodes.Any(n => n.Operation is SelectedPatchOperation or DeferredToolOperation);
    private static IReadOnlyList<FrozenTextPatch> Patches(PreviewOperation operation) => operation switch
    { ExactPatchOperation e => e.Patches, SelectedPatchOperation s => s.Patches, _ => throw new Failure(PreviewRunStatus.InvalidDocument) };
    private static string Selector(PreviewOperation operation) => operation switch
    { ExactPatchOperation e => e.Path, SelectedPatchOperation s => s.Root, DeferredToolOperation t => t.Root, _ => throw new Failure(PreviewRunStatus.InvalidDocument) };

    private async ValueTask<LanguageAuthorityStatus> Check(PreviewAuthorizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var decision = await _authorizer.AuthorizeAsync(request, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return decision?.Status switch { LanguageAuthorityStatus.Permit => LanguageAuthorityStatus.Permit, LanguageAuthorityStatus.Deny => LanguageAuthorityStatus.Deny, _ => LanguageAuthorityStatus.Unavailable };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return LanguageAuthorityStatus.Unavailable; }
    }
    private static PreviewAuthorizationRequest Request(EffectInvocation invocation, CompiledPreviewDocument doc, CompiledPreviewNode node,
        PreviewAuthorizationPhase phase, string? path, ResourceAction? action, RequestIdentity? identity, CapturedFilePatch? proposal, string? planIdentity) =>
        new(invocation, doc.Identity, node.Identity, node.Descriptor, PreviewProfile.SchemaVersion, PreviewProfile.CatalogueVersion,
            PreviewProfile.ProviderProfile, doc.Workspace, node.Operation, phase, path ?? Selector(node.Operation), action, identity, proposal, planIdentity);
    private async ValueTask Demand(EffectInvocation invocation, CompiledPreviewDocument doc, CompiledPreviewNode node,
        PreviewAuthorizationPhase phase, Budget budget, CancellationToken ct, string? path = null, ResourceAction? action = null,
        RequestIdentity? identity = null, CapturedFilePatch? proposal = null, string? planIdentity = null)
    {
        budget.Call();
        var status = await Check(Request(invocation, doc, node, phase, path, action, identity, proposal, planIdentity), ct).ConfigureAwait(false);
        if (status != LanguageAuthorityStatus.Permit)
            throw new Failure(status == LanguageAuthorityStatus.Deny ? PreviewRunStatus.AuthorityDenied : PreviewRunStatus.AuthorizationUnavailable);
    }

    private static async ValueTask<(byte[] Bytes, ResourceVersion Version, RequestIdentity Identity)> Read(Access access, string path, CancellationToken ct)
    {
        var bound = Math.Min(access.Document.Limits.MaxFileBytes, access.Budget.RemainingBytes);
        if (bound <= 0) throw new Failure(PreviewRunStatus.LimitExceeded);
        var request = new FileReadRequest(access.Invocation, access.Document.Workspace, new(path), new(bound));
        var identity = ResourceRequestIdentity.Compute(request);
        access.Register(identity, ResourceAction.ReadFile, path);
        var result = await access.Reader.ReadFileAsync(request with { Invocation = request.Invocation with { RequestIdentity = identity } }, ct).ConfigureAwait(false);
        if (access.Budget.Exhausted) throw new Failure(PreviewRunStatus.LimitExceeded);
        if (!result.Succeeded) throw new Failure(Map(result.Failure));
        var bytes = result.Value!.Content.ToArray();
        access.Budget.Read(bytes.Length);
        return (bytes, result.Value.Version, identity);
    }

    private static async ValueTask<(List<string> Paths, bool Complete)> Discover(Access access, SelectedPatchOperation selection,
        List<PreviewObservation> observations, CancellationToken ct)
    {
        var paths = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        var stackBytes = 0;
        var outputBytes = 0;
        void Push(string path, int depth)
        {
            stackBytes = checked(stackBytes + Encoding.UTF8.GetByteCount(path) + 32);
            if (stack.Count >= access.Document.Limits.MaxTargets || stackBytes > access.Document.Limits.MaxPlanBytes)
                throw new Failure(PreviewRunStatus.LimitExceeded);
            stack.Push((path, depth));
        }
        Push(selection.Root, 0);
        var complete = true;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = stack.Pop();
            stackBytes -= Encoding.UTF8.GetByteCount(directory) + 32;
            var request = new DirectoryListRequest(access.Invocation, access.Document.Workspace, new(directory), 100_000, 100_000, access.Document.Limits.MaxPlanBytes);
            var identity = ResourceRequestIdentity.Compute(request);
            access.Register(identity, ResourceAction.ListDirectory, directory);
            var result = await access.Reader.ListDirectoryAsync(request with { Invocation = request.Invocation with { RequestIdentity = identity } }, ct).ConfigureAwait(false);
            if (access.Budget.Exhausted) throw new Failure(PreviewRunStatus.LimitExceeded);
            if (!result.Succeeded)
            {
                if (result.Failure == ResourceFailureKind.TooLarge) return (paths, false);
                if (directory != selection.Root && result.Failure is ResourceFailureKind.AuthorizationDenied or ResourceFailureKind.NotFound) continue;
                throw new Failure(Map(result.Failure));
            }
            var page = result.Value!;
            access.Budget.Retain(256 + Encoding.UTF8.GetByteCount(directory));
            observations.Add(new(access.Node.Identity, directory, ResourceAction.ListDirectory, identity, null,
                PreviewIdentity.Directory(page), 0, page.IsComplete));
            complete &= page.IsComplete;
            foreach (var entry in page.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = directory.Length == 0 ? entry.Name : directory + "/" + entry.Name;
                var relative = selection.Root.Length == 0 ? path : path[(selection.Root.Length + 1)..];
                if (entry.IsDirectory)
                {
                    if (LanguageGlob.CanDescend(selection.Pattern, relative, access.Budget.Glob, ct))
                    {
                        if (depth < selection.Limits.MaxDepth) Push(path, depth + 1);
                        else complete = false;
                    }
                    continue;
                }
                if (!LanguageGlob.IsMatch(selection.Pattern, relative, access.Budget.Glob, ct)) continue;
                var cost = Encoding.UTF8.GetByteCount(path) + 32;
                if (paths.Count >= selection.Limits.MaxMatches || cost > selection.Limits.MaxOutputBytes - outputBytes) return (paths, false);
                paths.Add(path); outputBytes += cost;
            }
            if (!complete) break;
        }
        return (paths, complete);
    }

    internal static byte[] Apply(byte[] original, IReadOnlyList<FrozenTextPatch> patches, int maximum, CancellationToken ct)
    {
        try { _ = Utf8.GetCharCount(original); }
        catch (DecoderFallbackException) { throw new Failure(PreviewRunStatus.InvalidPatch); }
        long size = original.Length;
        foreach (var patch in patches)
        {
            ct.ThrowIfCancellationRequested();
            var end = (long)patch.StartOffset + patch.DeleteLength;
            if (patch.StartOffset < 0 || end > original.Length || !Boundary(original, patch.StartOffset) || !Boundary(original, (int)end))
                throw new Failure(PreviewRunStatus.InvalidPatch);
            size += patch.ReplacementUtf8.Length - (long)patch.DeleteLength;
        }
        if (size < 0 || size > maximum) throw new Failure(PreviewRunStatus.LimitExceeded);
        var output = new byte[(int)size];
        var from = 0; var to = 0;
        foreach (var patch in patches)
        {
            ct.ThrowIfCancellationRequested();
            original.AsSpan(from, patch.StartOffset - from).CopyTo(output.AsSpan(to));
            to += patch.StartOffset - from;
            patch.ReplacementUtf8.Span.CopyTo(output.AsSpan(to));
            to += patch.ReplacementUtf8.Length;
            from = patch.StartOffset + patch.DeleteLength;
        }
        original.AsSpan(from).CopyTo(output.AsSpan(to));
        return output;
    }
    private static bool Boundary(byte[] bytes, int offset) => offset == bytes.Length || offset >= 0 && (bytes[offset] & 0xC0) != 0x80;
    private static PreviewRunStatus Map(ResourceFailureKind? failure) => failure switch
    {
        ResourceFailureKind.AuthorizationDenied => PreviewRunStatus.AuthorityDenied,
        ResourceFailureKind.AuthorizationUnavailable => PreviewRunStatus.AuthorizationUnavailable,
        ResourceFailureKind.NotFound => PreviewRunStatus.NotFound,
        ResourceFailureKind.TooLarge => PreviewRunStatus.LimitExceeded,
        ResourceFailureKind.AccessDenied => PreviewRunStatus.AccessDenied,
        ResourceFailureKind.Unsupported => PreviewRunStatus.UnsupportedProfile,
        ResourceFailureKind.InvalidPath or ResourceFailureKind.InvalidRequest => PreviewRunStatus.InvalidDocument,
        _ => PreviewRunStatus.ProviderFailure
    };
    private sealed record Manifest(CompiledPreviewNode Node, List<string> Paths, PreviewUnresolvedReason? Reason);
    private sealed record ResourceRelease(CompiledPreviewNode Node, string Path, ResourceAction Action, RequestIdentity Identity);
    private sealed class Failure(PreviewRunStatus status) : Exception { internal PreviewRunStatus Status { get; } = status; }
    private sealed class Budget(PreviewLimits limits)
    {
        private int _calls, _bytes, _retained, _targets, _glob;
        internal bool Exhausted { get; private set; }
        internal int RemainingBytes => limits.MaxReadBytes - _bytes;
        internal void Call() { if (++_calls > limits.MaxResourceCalls) { Exhausted = true; throw new Failure(PreviewRunStatus.LimitExceeded); } }
        internal void Read(int bytes) { _bytes = checked(_bytes + bytes); if (_bytes > limits.MaxReadBytes) throw new Failure(PreviewRunStatus.LimitExceeded); }
        internal void Retain(int bytes) { _retained = checked(_retained + bytes); if (_retained > limits.MaxPlanBytes) { Exhausted = true; throw new Failure(PreviewRunStatus.LimitExceeded); } }
        internal void Target(string path) { if (++_targets > limits.MaxTargets) throw new Failure(PreviewRunStatus.LimitExceeded); Retain(64 + Encoding.UTF8.GetByteCount(path)); }
        internal void Glob(int work) { if (work > LanguageProfile.MaxGlobWork - _glob) throw new Failure(PreviewRunStatus.LimitExceeded); _glob += work; }
    }

    private sealed class Access : IResourceAuthorizer, IDisposable
    {
        private readonly PreviewRuntime _runtime;
        private readonly EffectInvocation _parent;
        private readonly Action<CompiledPreviewNode, string, ResourceAction, RequestIdentity> _retain;
        private RequestIdentity _identity;
        private ResourceAction _action;
        private string _path = "";
        internal Access(PreviewRuntime runtime, EffectInvocation parent, CompiledPreviewDocument document, CompiledPreviewNode node, Budget budget,
            int candidates, Action<CompiledPreviewNode, string, ResourceAction, RequestIdentity> retain)
        {
            _runtime = runtime; _parent = parent; Document = document; Node = node; Budget = budget;
            _retain = retain;
            Invocation = new(Guid.NewGuid().ToString("N"), parent.SubjectId, parent.EffectId, parent.AttemptId, Guid.NewGuid().ToString("N"), null, default);
            Reader = new(document.Workspace, runtime._workspace.FullRootPath, this, new()
            { MaxEntries = 100_000, MaxCandidatesScanned = 100_000, MaxTotalCandidatesScanned = candidates, OperationTimeout = TimeSpan.FromMilliseconds(document.Limits.TimeoutMilliseconds) });
        }
        internal CompiledPreviewDocument Document { get; }
        internal CompiledPreviewNode Node { get; }
        internal Budget Budget { get; }
        internal HostInvocation Invocation { get; }
        internal LocalWorkspaceReader Reader { get; }
        internal void Register(RequestIdentity identity, ResourceAction action, string path) { _identity = identity; _action = action; _path = path; }
        public async ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request, CancellationToken ct = default)
        {
            if (request.Invocation with { RequestIdentity = default } != Invocation || request.SnapshotRequestIdentity != _identity || request.Invocation.RequestIdentity != _identity)
                return new(AuthorizationStatus.Deny);
            var (workspace, path) = request.Resource switch
            {
                ResourceBinding.WorkspaceFile f => (f.Workspace, f.Path.Value),
                ResourceBinding.WorkspaceDirectory d => (d.Workspace, d.Path.Value),
                ResourceBinding.WorkspaceEntry e => (e.Workspace, e.Path.Value),
                _ => (default, null)
            };
            if (workspace != Document.Workspace || path is null) return new(AuthorizationStatus.Deny);
            var exact = string.Equals(path, _path, StringComparison.Ordinal);
            var ancestor = path.Length == 0 || _path.StartsWith(path + "/", StringComparison.Ordinal);
            var child = _action == ResourceAction.ListDirectory && (_path.Length == 0 ? path.Length != 0 && !path.Contains('/') : path.StartsWith(_path + "/", StringComparison.Ordinal) && !path[(_path.Length + 1)..].Contains('/'));
            var role = request.Action switch
            {
                ResourceAction.ReadFile => _action == request.Action && Node.Operation is ExactPatchOperation or SelectedPatchOperation && exact && request.Resource is ResourceBinding.WorkspaceFile,
                ResourceAction.ListDirectory => _action == request.Action && Node.Operation is SelectedPatchOperation && exact && request.Resource is ResourceBinding.WorkspaceDirectory,
                ResourceAction.ReadMetadata => request.Resource is ResourceBinding.WorkspaceEntry && (exact || ancestor || child),
                _ => false
            };
            if (!role) return new(AuthorizationStatus.Deny);
            Budget.Call();
            var decision = await _runtime.Check(Request(_parent, Document, Node, PreviewAuthorizationPhase.ResourceAccess, path, request.Action, _identity, null, null), ct).ConfigureAwait(false);
            if (decision == LanguageAuthorityStatus.Permit) _retain(Node, path, request.Action, _identity);
            return new(decision switch { LanguageAuthorityStatus.Permit => AuthorizationStatus.Permit, LanguageAuthorityStatus.Deny => AuthorizationStatus.Deny, _ => AuthorizationStatus.Unavailable });
        }
        public void Dispose() => Reader.Dispose();
    }
}
