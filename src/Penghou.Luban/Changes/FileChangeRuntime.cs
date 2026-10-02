using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Penghou.Luban.Changes;

/// <summary>
/// Authorized, same-workspace file observation and patch-candidate construction.
/// This runtime never writes and never turns a clean result into permission.
/// </summary>
public sealed class FileChangeRuntime
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly WorkspaceReference _workspace;
    private readonly IWorkspaceProvider _provider;
    private readonly IFileChangeAuthorizer _authorizer;

    public FileChangeRuntime(WorkspaceReference workspace, IWorkspaceProvider provider, IFileChangeAuthorizer authorizer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        if (_provider.Workspace.Value != workspace.Id)
            throw new ArgumentException("Provider workspace does not match the logical workspace.", nameof(provider));
        if (!ValidId(workspace.Id)) throw new ArgumentException("A bounded workspace ID is required.", nameof(workspace));
    }

    public ValueTask<FileChangeResult> DiffAsync(EffectInvocation invocation, WorkspaceId workspace,
        FileDiffRequest request, FileChangeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Done(FileChangeStatus.Cancelled);
        limits ??= FileChangeLimits.Default;
        if (request is null || request.Options is { IsValid: false }) return Done(FileChangeStatus.InvalidInput);
        limits = limits with { };
        var options = (request.Options ?? TextChangeOptions.Default) with { };
        if (!TryInputs(workspace, limits, [new(request.TargetPath, FileChangeInputRole.Target),
            new(request.ProposedSourcePath, FileChangeInputRole.ProposedSource)], out var inputs))
            return Done(FileChangeStatus.InvalidInput);
        return RunAsync(invocation, workspace, FileChangeOperation.Diff, inputs, options.MaxInputBytes, limits,
            options, null, (snapshots, ct, profile) => ComputeDiff(workspace, inputs, snapshots, options, limits, profile, ct), cancellationToken);
    }

    public ValueTask<FileChangeResult> MergeAsync(EffectInvocation invocation, WorkspaceId workspace,
        FileMergeRequest request, FileChangeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Done(FileChangeStatus.Cancelled);
        limits ??= FileChangeLimits.Default;
        if (request is null || request.Options is { IsValid: false }) return Done(FileChangeStatus.InvalidInput);
        limits = limits with { };
        var options = (request.Options ?? TextChangeOptions.Default) with { };
        if (!TryInputs(workspace, limits, [new(request.BasePath, FileChangeInputRole.Base),
            new(request.OursPath, FileChangeInputRole.Ours), new(request.TheirsPath, FileChangeInputRole.Theirs)], out var inputs))
            return Done(FileChangeStatus.InvalidInput);
        return RunAsync(invocation, workspace, FileChangeOperation.Merge, inputs, options.MaxInputBytes, limits,
            options, null, (snapshots, ct, profile) => ComputeMerge(workspace, inputs, snapshots, options, limits, profile, ct), cancellationToken);
    }

    public ValueTask<FileChangeResult> ValidateCandidateAsync(EffectInvocation invocation, WorkspaceId workspace,
        FilePatchCandidate candidate, FileChangeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Done(FileChangeStatus.Cancelled);
        var requestedLimits = limits;
        if (candidate is null || candidate.Workspace != workspace)
            return Done(FileChangeStatus.InvalidInput);
        if (!candidate.Limits.IsValid || requestedLimits is { IsValid: false }) return Done(FileChangeStatus.InvalidInput);
        limits = requestedLimits is null ? candidate.Limits : Intersect(candidate.Limits, requestedLimits);
        limits = limits with { };
        if (string.IsNullOrWhiteSpace(_provider.Capabilities.ReadProfile) ||
            candidate.ProviderProfile != _provider.Capabilities.ReadProfile) return Done(FileChangeStatus.UnsupportedProfile);
        if (RetainedBytes([candidate]) > limits.MaxTotalRetainedBytes) return Done(FileChangeStatus.LimitExceeded);
        if (!ValidateCandidate(candidate)) return Done(FileChangeStatus.InvalidInput);
        if (!TryInputs(workspace, limits, [new(candidate.TargetPath, FileChangeInputRole.Target)], out var inputs))
            return Done(FileChangeStatus.InvalidInput);
        return RunAsync(invocation, workspace, FileChangeOperation.CandidateValidation, inputs,
            candidate.Options.MaxInputBytes, limits, candidate.Options, null, (snapshots, ct, _) =>
            {
                var current = snapshots[0];
                var valid = current.Version == candidate.OriginalVersion &&
                    current.ByteLength == candidate.OriginalByteLength && current.Sha256 == candidate.OriginalSha256 &&
                    current.Content.ToArray().AsSpan().SequenceEqual(candidate.OriginalContent.ToArray());
                var status = valid ? FileChangeStatus.Valid : FileChangeStatus.Stale;
                ct.ThrowIfCancellationRequested();
                return new FileChangeResult(status, validatedCandidate: valid ? candidate : null,
                    resultIdentity: HashText(candidate.CandidateIdentity + ":" + status));
            }, cancellationToken, candidateIdentity: candidate.CandidateIdentity);
    }

    public ValueTask<FileChangeResult> MaterializeUnifiedAsync(EffectInvocation invocation, WorkspaceId workspace,
        TextUnifiedPatchCandidate candidate, TextUnifiedImportOptions? importOptions = null,
        TextChangeOptions? textOptions = null, FileChangeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Done(FileChangeStatus.Cancelled);
        importOptions ??= TextUnifiedImportOptions.Default;
        textOptions ??= TextChangeOptions.Default;
        limits ??= FileChangeLimits.Default;
        importOptions = importOptions with { };
        textOptions = textOptions with { };
        limits = limits with { };
        if (candidate is null || candidate.Files.Count is < 1 || candidate.Files.Count > importOptions.MaxFiles ||
            !importOptions.IsValid || !textOptions.IsValid)
            return Done(FileChangeStatus.InvalidInput);
        var specs = new List<(string Path, FileChangeInputRole Role)>(candidate.Files.Count);
        foreach (var file in candidate.Files)
        {
            if (file is null) return Done(FileChangeStatus.InvalidInput);
            specs.Add((file.RelativePath, FileChangeInputRole.UnifiedTarget));
        }
        if (!TryInputs(workspace, limits, specs, out var inputs) || inputs.Count > limits.MaxFiles)
            return Done(FileChangeStatus.InvalidInput);
        return RunAsync(invocation, workspace, FileChangeOperation.UnifiedImport, inputs,
            textOptions.MaxInputBytes, limits, textOptions, importOptions,
            (snapshots, ct, profile) => ComputeUnified(workspace, candidate, snapshots,
                importOptions, textOptions, limits, profile, ct), cancellationToken);
    }

    private async ValueTask<FileChangeResult> RunAsync(EffectInvocation invocation, WorkspaceId workspace,
        FileChangeOperation operation, IReadOnlyList<FileChangeResource> inputs, int maxFileBytes,
        FileChangeLimits limits, TextChangeOptions textOptions, TextUnifiedImportOptions? importOptions,
        Func<IReadOnlyList<FileChangeInputSnapshot>, CancellationToken, string, FileChangeResult> compute,
        CancellationToken cancellationToken, string? candidateIdentity = null)
    {
        if (cancellationToken.IsCancellationRequested) return Empty(FileChangeStatus.Cancelled);
        if (!ValidInvocation(invocation) || workspace.Value != _workspace.Id || workspace != _provider.Workspace ||
            !limits.IsValid || maxFileBytes < 1 || inputs.Count is < 1 or > 32)
            return Empty(FileChangeStatus.InvalidInput);
        limits = limits with { };
        textOptions = textOptions with { };
        importOptions = importOptions is null ? null : importOptions with { };
        var capabilities = _provider.Capabilities;
        if (!capabilities.SupportsReads) return Empty(FileChangeStatus.UnsupportedProfile);
        if (string.IsNullOrWhiteSpace(capabilities.ReadProfile) || capabilities.ReadProfile.Length > 256)
            return Empty(FileChangeStatus.UnsupportedProfile);
        var providerProfile = capabilities.ReadProfile;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.TimeoutMilliseconds);
        var ct = deadline.Token;
        var operationId = Guid.NewGuid().ToString("N");
        var inputSnapshot = Array.AsReadOnly(inputs.ToArray());
        var hostInvocation = new HostInvocation(Guid.NewGuid().ToString("N"), invocation.SubjectId,
            invocation.EffectId, invocation.AttemptId, Guid.NewGuid().ToString("N"), null, default);
        var snapshots = new List<FileChangeInputSnapshot>(inputs.Count);
        try
        {
            var preflight = await Authorize(new(invocation, workspace, operationId, operation,
                FileChangePhase.Preflight, inputSnapshot, textOptions, importOptions, limits,
                ProviderProfile: providerProfile), ct).ConfigureAwait(false);
            if (preflight != FileChangeAuthorizationStatus.Permit)
                return Empty(ToAuthorizationFailure(preflight));

            foreach (var resource in inputs)
            {
                ct.ThrowIfCancellationRequested();
                var target = await Authorize(new(invocation, workspace, operationId, operation,
                    FileChangePhase.TargetAdmission, inputSnapshot, textOptions, importOptions, limits,
                    Resource: resource, ProviderProfile: providerProfile), ct).ConfigureAwait(false);
                if (target != FileChangeAuthorizationStatus.Permit)
                    return Empty(ToAuthorizationFailure(target));
            }

            using var access = new ProviderAccess(this, invocation, workspace, operationId, operation,
                inputSnapshot, hostInvocation, textOptions, importOptions, limits, providerProfile, ct);
            IWorkspaceReaderSession reader;
            try { reader = _provider.OpenReader(access, new(MaxEntries: 100_000, MaxCandidatesScanned: 100_000, MaxTotalCandidatesScanned: 100_000)); }
            catch { return await ReleaseOnlyFailure(FileChangeStatus.ProviderFailure).ConfigureAwait(false); }
            using (reader)
            {
                long totalRead = 0;
                var byPath = new Dictionary<string, (ResourceVersion Version, ImmutableBytes Bytes, string Text)>(StringComparer.Ordinal);
                foreach (var resource in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (byPath.ContainsKey(resource.Path.Value)) continue;
                    var remaining = limits.MaxTotalReadBytes - totalRead;
                    if (remaining <= 0) return await ReleaseOnlyFailure(FileChangeStatus.LimitExceeded).ConfigureAwait(false);
                    var bound = (int)Math.Min(maxFileBytes, remaining);
                    var request = new FileReadRequest(hostInvocation, workspace, resource.Path, new(bound));
                    var identity = ResourceRequestIdentity.Compute(request);
                    access.Register(identity, resource, bound);
                    ResourceResult<FileReadResult> result;
                    try
                    {
                        result = await reader.ReadFileAsync(request with
                        { Invocation = request.Invocation with { RequestIdentity = identity } }, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { return await ReleaseOnlyFailure(FileChangeStatus.ProviderFailure).ConfigureAwait(false); }
                    var enforcement = access.Complete(identity);
                    if (enforcement != FileChangeAuthorizationStatus.Permit)
                        return await ReleaseOnlyFailure(ToAuthorizationFailure(enforcement)).ConfigureAwait(false);
                    if (result is null || !result.Succeeded)
                        return await ReleaseOnlyFailure(MapFailure(result?.Failure)).ConfigureAwait(false);
                    var read = result.Value!;
                    if (read.Content.Length > bound || !ValidVersion(read.Version))
                        return await ReleaseOnlyFailure(FileChangeStatus.ProviderFailure).ConfigureAwait(false);
                    var raw = read.Content.ToArray();
                    totalRead = checked(totalRead + raw.Length);
                    if (totalRead > limits.MaxTotalReadBytes)
                    {
                        CryptographicOperations.ZeroMemory(raw);
                        return await ReleaseOnlyFailure(FileChangeStatus.LimitExceeded).ConfigureAwait(false);
                    }
                    string text;
                    try { text = StrictUtf8.GetString(raw); }
                    catch (DecoderFallbackException)
                    {
                        CryptographicOperations.ZeroMemory(raw);
                        return await ReleaseOnlyFailure(FileChangeStatus.UnsupportedProfile).ConfigureAwait(false);
                    }
                    var bytes = new ImmutableBytes(raw);
                    CryptographicOperations.ZeroMemory(raw);
                    byPath.Add(resource.Path.Value, (read.Version, bytes, text));
                }

                foreach (var resource in inputs)
                {
                    var captured = byPath[resource.Path.Value];
                    snapshots.Add(new FileChangeInputSnapshot(resource, captured.Version, captured.Bytes));
                }
                FileChangeResult computed;
                try { computed = compute(Array.AsReadOnly(snapshots.ToArray()), ct, providerProfile); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is DecoderFallbackException or InvalidDataException or ArgumentException or OverflowException)
                { computed = Empty(FileChangeStatus.InvalidInput); }
                var resultIdentity = computed.ResultIdentity ?? ComputeResultIdentity(operation, computed.Status,
                    snapshots, computed.Candidates, computed.Conflicts);
                var retained = RetainedBytes(computed.Candidates);
                foreach (var conflict in computed.Conflicts)
                    retained = checked(retained + StrictUtf8.GetByteCount(conflict.BaseText) +
                        StrictUtf8.GetByteCount(conflict.OursText) + StrictUtf8.GetByteCount(conflict.TheirsText));
                if (retained > limits.MaxTotalRetainedBytes)
                {
                    computed = Empty(FileChangeStatus.LimitExceeded);
                    resultIdentity = ComputeResultIdentity(operation, computed.Status, snapshots, null, null);
                }
                var release = await Authorize(new(invocation, workspace, operationId, operation,
                    FileChangePhase.ResultRelease, inputSnapshot, textOptions, importOptions, limits,
                    ObservedVersion: snapshots.Count == 1 ? snapshots[0].Version : null,
                    ContentSha256: snapshots.Count == 1 ? snapshots[0].Sha256 : null,
                    ContentByteLength: snapshots.Count == 1 ? snapshots[0].ByteLength : null,
                    CandidateIdentity: candidateIdentity ?? (computed.Candidates.Count == 1 ? computed.Candidates[0].CandidateIdentity : null),
                    ResultIdentity: resultIdentity, Outcome: computed.Status,
                    Observations: Array.AsReadOnly(snapshots.ToArray()), ProviderProfile: providerProfile), ct).ConfigureAwait(false);
                if (release != FileChangeAuthorizationStatus.Permit)
                    return Empty(ToAuthorizationFailure(release));
                return computed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Empty(FileChangeStatus.Cancelled); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { return Empty(FileChangeStatus.LimitExceeded); }
        catch (Exception)
        { return Empty(FileChangeStatus.ProviderFailure); }

        async ValueTask<FileChangeResult> ReleaseOnlyFailure(FileChangeStatus status)
        {
            var release = await Authorize(new(invocation, workspace, operationId, operation,
                FileChangePhase.ResultRelease, inputSnapshot, textOptions, importOptions, limits,
                CandidateIdentity: candidateIdentity, ResultIdentity: ComputeResultIdentity(operation, status, snapshots, null, null),
                Outcome: status, Observations: Array.AsReadOnly(snapshots.ToArray()), ProviderProfile: providerProfile), ct).ConfigureAwait(false);
            return release == FileChangeAuthorizationStatus.Permit ? Empty(status) : Empty(ToAuthorizationFailure(release));
        }
    }

    private FileChangeResult ComputeDiff(WorkspaceId workspace, IReadOnlyList<FileChangeResource> inputs,
        IReadOnlyList<FileChangeInputSnapshot> snapshots, TextChangeOptions options, FileChangeLimits limits, string providerProfile,
        CancellationToken ct)
    {
        var target = ByRole(snapshots, FileChangeInputRole.Target);
        var source = ByRole(snapshots, FileChangeInputRole.ProposedSource);
        var diff = TextDiffEngine.Diff(Decode(target.Content), Decode(source.Content), options, ct);
        if (diff.Status != TextDiffStatus.Succeeded) return Empty(Map(diff.Status));
        if (diff.Edits.Count > 128) return Empty(FileChangeStatus.LimitExceeded);
        var edits = FreezeEdits(diff.Edits);
        var candidate = CreateCandidate(workspace, target, source.Content, edits, snapshots, options,
            limits, FileChangeOperation.Diff, providerProfile);
        if (!CandidateReconstructs(candidate)) return Empty(FileChangeStatus.InvalidInput);
        return new(FileChangeStatus.Succeeded, [candidate], diff: diff, resultIdentity: candidate.CandidateIdentity);
    }

    private FileChangeResult ComputeMerge(WorkspaceId workspace, IReadOnlyList<FileChangeResource> inputs,
        IReadOnlyList<FileChangeInputSnapshot> snapshots, TextChangeOptions options, FileChangeLimits limits, string providerProfile,
        CancellationToken ct)
    {
        var @base = ByRole(snapshots, FileChangeInputRole.Base);
        var ours = ByRole(snapshots, FileChangeInputRole.Ours);
        var theirs = ByRole(snapshots, FileChangeInputRole.Theirs);
        if (options.MaxWorkCells < 2) return Empty(FileChangeStatus.LimitExceeded);
        var mergeWork = options.MaxWorkCells / 2;
        var targetDiffWork = options.MaxWorkCells - mergeWork;
        var merge = TextMergeEngine.Merge(Decode(@base.Content), Decode(ours.Content), Decode(theirs.Content),
            options with { MaxWorkCells = mergeWork }, ct);
        if (merge.Status == TextMergeStatus.Conflicted)
        {
            var identity = ComputeConflictIdentity(merge);
            return new(FileChangeStatus.Conflicted, conflicts: merge.Conflicts, merge: merge, resultIdentity: identity);
        }
        if (merge.Status != TextMergeStatus.Clean || merge.Value is null) return Empty(Map(merge.Status));
        var output = StrictUtf8.GetBytes(merge.Value);
        if (merge.Result is null || output.Length != merge.Result.ByteLength || !Sha256(output).Equals(merge.Result.Sha256, StringComparison.OrdinalIgnoreCase))
        { CryptographicOperations.ZeroMemory(output); return Empty(FileChangeStatus.InvalidInput); }
        if (merge.Edits.Count > 128) { CryptographicOperations.ZeroMemory(output); return Empty(FileChangeStatus.LimitExceeded); }
        var targetDiff = TextDiffEngine.Diff(Decode(ours.Content), merge.Value,
            options with { MaxWorkCells = targetDiffWork }, ct);
        if (targetDiff.Status != TextDiffStatus.Succeeded || targetDiff.Edits.Count > 128)
        { CryptographicOperations.ZeroMemory(output); return Empty(targetDiff.Status == TextDiffStatus.Succeeded ? FileChangeStatus.LimitExceeded : Map(targetDiff.Status)); }
        var candidate = CreateCandidate(workspace, ours, new ImmutableBytes(output), FreezeEdits(targetDiff.Edits), snapshots,
            options, limits, FileChangeOperation.Merge, providerProfile);
        CryptographicOperations.ZeroMemory(output);
        if (!CandidateReconstructs(candidate)) return Empty(FileChangeStatus.InvalidInput);
        return new(FileChangeStatus.Succeeded, [candidate], merge: merge, resultIdentity: candidate.CandidateIdentity);
    }

    private FileChangeResult ComputeUnified(WorkspaceId workspace, TextUnifiedPatchCandidate candidate,
        IReadOnlyList<FileChangeInputSnapshot> snapshots, TextUnifiedImportOptions importOptions,
        TextChangeOptions textOptions, FileChangeLimits limits, string providerProfile, CancellationToken ct)
    {
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots) originals.Add(snapshot.Resource.Path.Value, Decode(snapshot.Content));
        var materialized = TextUnifiedImport.Materialize(candidate, originals, importOptions, textOptions, ct);
        if (materialized.Status != TextUnifiedMaterializationStatus.Succeeded)
            return Empty(materialized.Status switch
            {
                TextUnifiedMaterializationStatus.Stale => FileChangeStatus.Stale,
                TextUnifiedMaterializationStatus.InvalidInput => FileChangeStatus.InvalidInput,
                TextUnifiedMaterializationStatus.LimitExceeded => FileChangeStatus.LimitExceeded,
                TextUnifiedMaterializationStatus.Cancelled => FileChangeStatus.Cancelled,
                _ => FileChangeStatus.ProviderFailure
            });
        if (materialized.Files.Count != snapshots.Count) return Empty(FileChangeStatus.InvalidInput);
        long retainedOutputBytes = 0;
        var candidates = new List<FilePatchCandidate>(materialized.Files.Count);
        for (var i = 0; i < materialized.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = materialized.Files[i];
            var target = snapshots.SingleOrDefault(s => s.Resource.Path.Value == file.RelativePath);
            if (target is null || !target.Sha256.Equals(file.Before.Sha256, StringComparison.OrdinalIgnoreCase) || target.ByteLength != file.Before.ByteLength)
                return Empty(FileChangeStatus.Stale);
            var output = StrictUtf8.GetBytes(file.ProposedText);
            if (output.Length != file.After.ByteLength || !Sha256(output).Equals(file.After.Sha256, StringComparison.OrdinalIgnoreCase))
            { CryptographicOperations.ZeroMemory(output); return Empty(FileChangeStatus.InvalidInput); }
            if (file.Edits.Count > 128) { CryptographicOperations.ZeroMemory(output); return Empty(FileChangeStatus.LimitExceeded); }
            retainedOutputBytes = checked(retainedOutputBytes + output.Length +
                file.Edits.Sum(edit => (long)StrictUtf8.GetByteCount(edit.Replacement)));
            if (retainedOutputBytes > limits.MaxTotalRetainedBytes)
            { CryptographicOperations.ZeroMemory(output); return Empty(FileChangeStatus.LimitExceeded); }
            var candidateForFile = CreateCandidate(workspace, target, new ImmutableBytes(output), FreezeEdits(file.Edits),
                [target], textOptions, limits, FileChangeOperation.UnifiedImport, providerProfile);
            CryptographicOperations.ZeroMemory(output);
            if (!CandidateReconstructs(candidateForFile)) return Empty(FileChangeStatus.InvalidInput);
            candidates.Add(candidateForFile);
        }
        var resultIdentity = HashText(string.Join("\n", candidates.Select(c => c.CandidateIdentity)));
        return new(FileChangeStatus.Succeeded, candidates, resultIdentity: resultIdentity);
    }

    private FilePatchCandidate CreateCandidate(WorkspaceId workspace, FileChangeInputSnapshot target,
        ImmutableBytes proposed, IReadOnlyList<FrozenTextPatch> edits, IReadOnlyList<FileChangeInputSnapshot> inputs,
        TextChangeOptions options, FileChangeLimits limits, FileChangeOperation operation, string providerProfile)
    {
        var profile = $"penghou.luban.file-change.v1/windows-workspace-path.v1/{TextChangeProfile.Identity}";
        var identity = CandidateIdentity(workspace, target.Resource.Path.Value, target.Version, target.Content,
            proposed, edits, inputs, options, limits, operation, providerProfile, profile);
        return new(workspace, target.Resource.Path.Value, target.Version, target.Content, proposed, edits,
            inputs, options, limits, operation, providerProfile, profile, identity);
    }

    private static IReadOnlyList<FrozenTextPatch> FreezeEdits(IReadOnlyList<TextEdit> edits)
    {
        var frozen = new FrozenTextPatch[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            var replacement = StrictUtf8.GetBytes(edits[i].Replacement);
            frozen[i] = new(edits[i].StartOffset, edits[i].DeleteLength, new ImmutableBytes(replacement));
            CryptographicOperations.ZeroMemory(replacement);
        }
        return Array.AsReadOnly(frozen);
    }

    private static bool CandidateReconstructs(FilePatchCandidate candidate)
    {
        try
        {
            if (candidate.Edits.Count == 0)
            {
                var before = candidate.OriginalContent.ToArray();
                var after = candidate.ProposedContent.ToArray();
                try
                {
                    _ = StrictUtf8.GetCharCount(before);
                    _ = StrictUtf8.GetCharCount(after);
                    return before.AsSpan().SequenceEqual(after);
                }
                finally { CryptographicOperations.ZeroMemory(before); CryptographicOperations.ZeroMemory(after); }
            }
            var patches = candidate.Edits.Select(e => new TextPatch(e.StartOffset, e.DeleteLength, e.ReplacementUtf8.ToArray())).ToArray();
            var rebuilt = Utf8PatchMaterializer.Materialize(candidate.OriginalContent.ToArray(), patches,
                new PatchLimits(128, candidate.Options.MaxOutputBytes, candidate.Options.MaxOutputBytes));
            try { return rebuilt.AsSpan().SequenceEqual(candidate.ProposedContent.ToArray()); }
            finally { CryptographicOperations.ZeroMemory(rebuilt); }
        }
        catch { return false; }
    }

    private static bool ValidateCandidate(FilePatchCandidate candidate)
    {
        try
        {
            if (!candidate.Options.IsValid || !candidate.Limits.IsValid || candidate.ProfileIdentity != $"penghou.luban.file-change.v1/windows-workspace-path.v1/{TextChangeProfile.Identity}" ||
                !ValidPath(candidate.TargetPath) || !ValidVersion(candidate.OriginalVersion) || candidate.Edits.Count is < 0 or > 128 ||
                candidate.OriginalContent.Length != candidate.OriginalByteLength || candidate.OriginalContent.Sha256 != candidate.OriginalSha256 ||
                candidate.ProposedContent.Length != candidate.ProposedByteLength || candidate.ProposedContent.Sha256 != candidate.ProposedSha256 ||
                candidate.Inputs.Count is < 1 or > 32) return false;
            foreach (var input in candidate.Inputs)
                if (input.Resource.Workspace != candidate.Workspace || !ValidPath(input.Resource.Path.Value) ||
                    !ValidVersion(input.Version) || input.Content.Length != input.ByteLength || input.Content.Sha256 != input.Sha256)
                    return false;
            var target = candidate.Inputs.FirstOrDefault(i => i.Resource.Path.Value == candidate.TargetPath &&
                i.Version == candidate.OriginalVersion && i.Content.Sha256 == candidate.OriginalSha256 &&
                i.Content.Length == candidate.OriginalByteLength);
            if (target is null || !target.Content.ToArray().AsSpan().SequenceEqual(candidate.OriginalContent.ToArray())) return false;
            var op = candidate.Operation;
            if (op is not (FileChangeOperation.Diff or FileChangeOperation.Merge or FileChangeOperation.UnifiedImport)) return false;
            var roles = candidate.Inputs.Select(i => i.Resource.Role).ToArray();
            if (op == FileChangeOperation.Diff && (roles.Length != 2 || !roles.Contains(FileChangeInputRole.Target) || !roles.Contains(FileChangeInputRole.ProposedSource)) ||
                op == FileChangeOperation.Merge && (roles.Length != 3 || !roles.Contains(FileChangeInputRole.Base) || !roles.Contains(FileChangeInputRole.Ours) ||
                    !roles.Contains(FileChangeInputRole.Theirs) || target.Resource.Role != FileChangeInputRole.Ours) ||
                op == FileChangeOperation.UnifiedImport && (roles.Length != 1 || target.Resource.Role != FileChangeInputRole.UnifiedTarget)) return false;
            if (CandidateIdentity(candidate.Workspace, candidate.TargetPath, candidate.OriginalVersion,
                candidate.OriginalContent, candidate.ProposedContent, candidate.Edits, candidate.Inputs,
                candidate.Options, candidate.Limits, op, candidate.ProviderProfile, candidate.ProfileIdentity) != candidate.CandidateIdentity) return false;
            return CandidateReconstructs(candidate);
        }
        catch { return false; }
    }

    private static string CandidateIdentity(WorkspaceId workspace, string targetPath, ResourceVersion version,
        ImmutableBytes original, ImmutableBytes proposed, IReadOnlyList<FrozenTextPatch> edits,
        IReadOnlyList<FileChangeInputSnapshot> inputs, TextChangeOptions options, FileChangeLimits limits, FileChangeOperation operation,
        string providerProfile, string profile)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        Write("Penghou.Luban.FilePatchCandidate"); writer.Write(1); Write(profile); Write(providerProfile); writer.Write((int)operation);
        Write(workspace.Value); Write(targetPath); Write(version.Value); Write(original.Sha256); writer.Write(original.Length);
        Write(proposed.Sha256); writer.Write(proposed.Length);
        writer.Write(options.MaxInputBytes); writer.Write(options.MaxLines); writer.Write(options.MaxWorkCells);
        writer.Write(options.MaxMatrixBytes); writer.Write(options.MaxEdits); writer.Write(options.MaxOutputBytes); writer.Write(options.MaxConflicts);
        writer.Write(limits.MaxFiles); writer.Write(limits.MaxTotalReadBytes); writer.Write(limits.TimeoutMilliseconds); writer.Write(limits.MaxTotalRetainedBytes);
        writer.Write(inputs.Count);
        foreach (var input in inputs)
        {
            writer.Write((int)input.Resource.Role); Write(input.Resource.Workspace.Value); Write(input.Resource.Path.Value);
            Write(input.Version.Value); Write(input.Sha256); writer.Write(input.ByteLength);
        }
        writer.Write(edits.Count);
        foreach (var edit in edits)
        {
            writer.Write(edit.StartOffset); writer.Write(edit.DeleteLength);
            var replacement = edit.ReplacementUtf8.ToArray(); writer.Write(replacement.Length); writer.Write(replacement);
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        void Write(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            writer.Write(bytes.Length); writer.Write(bytes);
        }
    }

    private static FileChangeLimits Intersect(FileChangeLimits captured, FileChangeLimits requested) => new(
        Math.Min(captured.MaxFiles, requested.MaxFiles),
        Math.Min(captured.MaxTotalReadBytes, requested.MaxTotalReadBytes),
        Math.Min(captured.TimeoutMilliseconds, requested.TimeoutMilliseconds),
        Math.Min(captured.MaxTotalRetainedBytes, requested.MaxTotalRetainedBytes));

    private static long RetainedBytes(IReadOnlyList<FilePatchCandidate> candidates)
    {
        var seen = new HashSet<ImmutableBytes>(ReferenceEqualityComparer.Instance);
        long total = 0;
        foreach (var candidate in candidates)
        {
            Add(candidate.OriginalContent);
            Add(candidate.ProposedContent);
            foreach (var input in candidate.Inputs) Add(input.Content);
            foreach (var edit in candidate.Edits) Add(edit.ReplacementUtf8);
        }
        return total;

        void Add(ImmutableBytes bytes)
        {
            if (seen.Add(bytes)) total = checked(total + bytes.Length);
        }
    }

    private async ValueTask<FileChangeAuthorizationStatus> Authorize(FileChangeAuthorizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var decision = await _authorizer.AuthorizeAsync(request, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            return decision?.Status ?? FileChangeAuthorizationStatus.Unavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return FileChangeAuthorizationStatus.Unavailable; }
    }

    private static FileChangeStatus ToAuthorizationFailure(FileChangeAuthorizationStatus status) => status switch
    {
        FileChangeAuthorizationStatus.Deny => FileChangeStatus.AuthorizationDenied,
        _ => FileChangeStatus.AuthorizationUnavailable
    };

    private static FileChangeStatus MapFailure(ResourceFailureKind? failure) => failure switch
    {
        ResourceFailureKind.NotFound => FileChangeStatus.NotFound,
        ResourceFailureKind.AuthorizationDenied => FileChangeStatus.AuthorizationDenied,
        ResourceFailureKind.AuthorizationUnavailable => FileChangeStatus.AuthorizationUnavailable,
        ResourceFailureKind.AccessDenied => FileChangeStatus.AccessDenied,
        ResourceFailureKind.InvalidPath or ResourceFailureKind.InvalidRequest => FileChangeStatus.InvalidInput,
        ResourceFailureKind.TooLarge => FileChangeStatus.LimitExceeded,
        ResourceFailureKind.Unsupported => FileChangeStatus.UnsupportedProfile,
        _ => FileChangeStatus.ProviderFailure
    };

    private static FileChangeStatus Map(TextDiffStatus status) => status switch
    {
        TextDiffStatus.InvalidInput => FileChangeStatus.InvalidInput,
        TextDiffStatus.LimitExceeded => FileChangeStatus.LimitExceeded,
        TextDiffStatus.Cancelled => FileChangeStatus.Cancelled,
        _ => FileChangeStatus.Succeeded
    };
    private static FileChangeStatus Map(TextMergeStatus status) => status switch
    {
        TextMergeStatus.Conflicted => FileChangeStatus.Conflicted,
        TextMergeStatus.InvalidInput => FileChangeStatus.InvalidInput,
        TextMergeStatus.LimitExceeded => FileChangeStatus.LimitExceeded,
        TextMergeStatus.Cancelled => FileChangeStatus.Cancelled,
        _ => FileChangeStatus.Succeeded
    };

    private static string Decode(ImmutableBytes bytes) => StrictUtf8.GetString(bytes.ToArray());
    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value)));
    private static FileChangeInputSnapshot ByRole(IReadOnlyList<FileChangeInputSnapshot> inputs, FileChangeInputRole role) =>
        inputs.First(input => input.Resource.Role == role);
    private static FileChangeResult Empty(FileChangeStatus status) => new(status);
    private static ValueTask<FileChangeResult> Done(FileChangeStatus status) => ValueTask.FromResult(Empty(status));

    private static string ComputeResultIdentity(FileChangeOperation operation, FileChangeStatus status,
        IReadOnlyList<FileChangeInputSnapshot> snapshots, IReadOnlyList<FilePatchCandidate>? candidates,
        IReadOnlyList<TextMergeConflict>? conflicts)
    {
        var builder = new StringBuilder($"{(int)operation}:{(int)status}\n");
        foreach (var input in snapshots) builder.Append((int)input.Resource.Role).Append('|').Append(input.Resource.Path.Value)
            .Append('|').Append(input.Version.Value).Append('|').Append(input.Sha256).Append('|').Append(input.ByteLength).Append('\n');
        if (candidates is not null) foreach (var candidate in candidates) builder.Append(candidate.CandidateIdentity).Append('\n');
        if (conflicts is not null) foreach (var conflict in conflicts)
            builder.Append(conflict.BaseStartLine).Append('|').Append(conflict.BaseEndLine).Append('|')
                .Append(conflict.BaseStartOffset).Append('|').Append(conflict.BaseDeleteLength).Append('|')
                .Append(HashText(conflict.BaseText)).Append('|').Append(HashText(conflict.OursText)).Append('|')
                .Append(HashText(conflict.TheirsText)).Append('\n');
        return HashText(builder.ToString());
    }

    private static string ComputeConflictIdentity(TextMergeResult result) => HashText(string.Join("\n",
        result.Conflicts.Select(c => $"{c.BaseStartLine}|{c.BaseEndLine}|{c.BaseStartOffset}|{c.BaseDeleteLength}|{HashText(c.BaseText)}|{HashText(c.OursText)}|{HashText(c.TheirsText)}")));

    private static bool TryInputs(WorkspaceId workspace, FileChangeLimits limits,
        IReadOnlyList<(string Path, FileChangeInputRole Role)> specs, out IReadOnlyList<FileChangeResource> inputs)
    {
        inputs = Array.Empty<FileChangeResource>();
        if (!limits.IsValid || specs.Count is < 1 or > 32 || specs.Count > limits.MaxFiles ||
            string.IsNullOrWhiteSpace(workspace.Value) || workspace.Value.Length > 256) return false;
        var resources = new List<FileChangeResource>(specs.Count);
        var folded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var spec in specs)
            {
                var normalized = WindowsWorkspacePath.Normalize(new WorkspacePath(spec.Path), allowRoot: false).Value;
                if (!string.Equals(normalized, spec.Path, StringComparison.Ordinal)) return false;
                if (folded.TryGetValue(normalized, out var existing) && !string.Equals(existing, normalized, StringComparison.Ordinal)) return false;
                folded[normalized] = normalized;
                resources.Add(new(workspace, new WorkspacePath(normalized), spec.Role));
            }
        }
        catch (ArgumentException) { return false; }
        inputs = Array.AsReadOnly(resources.ToArray());
        return true;
    }

    private bool ValidInvocation(EffectInvocation? invocation) => invocation is not null && ValidId(invocation.SubjectId) &&
        ValidId(invocation.EffectId) && ValidId(invocation.AttemptId);
    private static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; } catch (EncoderFallbackException) { return false; }
    }
    private static bool ValidPath(string? path)
    {
        if (path is null) return false;
        try { return WindowsWorkspacePath.Normalize(new WorkspacePath(path), allowRoot: false).Value == path; }
        catch (ArgumentException) { return false; }
    }
    private static bool ValidVersion(ResourceVersion version) => !string.IsNullOrWhiteSpace(version.Value) &&
        version.Value.Length <= 256 && !version.Value.Any(char.IsControl);

    private sealed class ProviderAccess : IResourceAuthorizer, IDisposable
    {
        private readonly FileChangeRuntime _runtime;
        private readonly EffectInvocation _effectInvocation;
        private readonly WorkspaceId _workspace;
        private readonly string _operationId;
        private readonly FileChangeOperation _operation;
        private readonly IReadOnlyList<FileChangeResource> _inputs;
        private readonly HostInvocation _hostInvocation;
        private readonly CancellationToken _ct;
        private RequestIdentity _pendingIdentity;
        private FileChangeResource? _pendingResource;
        private int _pendingReadLimit;
        private int _calls;
        private FileChangeAuthorizationStatus _status = FileChangeAuthorizationStatus.Permit;

        private readonly TextChangeOptions _textOptions;
        private readonly TextUnifiedImportOptions? _importOptions;
        private readonly FileChangeLimits _limits;
        private readonly string _providerProfile;
        private int _fileReadCalls;

        internal ProviderAccess(FileChangeRuntime runtime, EffectInvocation effectInvocation, WorkspaceId workspace,
            string operationId, FileChangeOperation operation, IReadOnlyList<FileChangeResource> inputs,
            HostInvocation hostInvocation, TextChangeOptions textOptions, TextUnifiedImportOptions? importOptions,
            FileChangeLimits limits, string providerProfile, CancellationToken ct)
        {
            _runtime = runtime; _effectInvocation = effectInvocation; _workspace = workspace; _operationId = operationId;
            _operation = operation; _inputs = inputs; _hostInvocation = hostInvocation; _ct = ct;
            _textOptions = textOptions; _importOptions = importOptions; _limits = limits; _providerProfile = providerProfile;
        }

        internal void Register(RequestIdentity identity, FileChangeResource resource, int maxBytes)
        {
            _pendingIdentity = identity; _pendingResource = resource; _calls = 0; _fileReadCalls = 0;
            _pendingReadLimit = maxBytes;
            _status = FileChangeAuthorizationStatus.Permit;
        }

        internal FileChangeAuthorizationStatus Complete(RequestIdentity identity) =>
            _pendingIdentity == identity && _calls > 0 && _fileReadCalls > 0 ? _status : FileChangeAuthorizationStatus.Unavailable;

        public async ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null || _pendingResource is null ||
                request.SnapshotRequestIdentity != _pendingIdentity || request.Invocation.RequestIdentity != _pendingIdentity ||
                request.Invocation with { RequestIdentity = default } != _hostInvocation)
            {
                _status = FileChangeAuthorizationStatus.Deny;
                return new(AuthorizationStatus.Deny);
            }
            var action = request.Resource switch
            {
                ResourceBinding.WorkspaceFile file when request.Action == ResourceAction.ReadFile &&
                    file.Workspace == _workspace && file.Path == _pendingResource.Path => ResourceAction.ReadFile,
                ResourceBinding.WorkspaceEntry entry when request.Action == ResourceAction.ReadMetadata &&
                    entry.Workspace == _workspace && IsAncestor(entry.Path.Value, _pendingResource.Path.Value) => ResourceAction.ReadMetadata,
                _ => (ResourceAction?)null
            };
            if (action is null)
            {
                _status = FileChangeAuthorizationStatus.Deny;
                return new(AuthorizationStatus.Deny);
            }
            if (++_calls > 4096)
            {
                _status = FileChangeAuthorizationStatus.Unavailable;
                return new(AuthorizationStatus.Unavailable);
            }
            if (action == ResourceAction.ReadFile) _fileReadCalls++;
            var resource = new FileChangeResource(_workspace,
                request.Resource is ResourceBinding.WorkspaceFile f ? f.Path : ((ResourceBinding.WorkspaceEntry)request.Resource).Path,
                _pendingResource.Role);
            var auth = await _runtime.Authorize(new(_effectInvocation, _workspace, _operationId, _operation,
                FileChangePhase.ResourceAccess, _inputs, _textOptions, _importOptions, _limits,
                Resource: resource, Action: action, RequestIdentity: _pendingIdentity, ProviderProfile: _providerProfile,
                ReadLimitBytes: _pendingReadLimit),
                cancellationToken).ConfigureAwait(false);
            if (auth != FileChangeAuthorizationStatus.Permit)
                _status = auth == FileChangeAuthorizationStatus.Deny ? FileChangeAuthorizationStatus.Deny :
                    _status == FileChangeAuthorizationStatus.Deny ? _status : FileChangeAuthorizationStatus.Unavailable;
            return new(auth switch
            {
                FileChangeAuthorizationStatus.Permit => AuthorizationStatus.Permit,
                FileChangeAuthorizationStatus.Deny => AuthorizationStatus.Deny,
                _ => AuthorizationStatus.Unavailable
            });
        }

        private static bool IsAncestor(string candidate, string target) => candidate.Length == 0 ||
            string.Equals(candidate, target, StringComparison.Ordinal) || target.StartsWith(candidate + "/", StringComparison.Ordinal);

        public void Dispose() { }
    }
}
