using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Penghou.Luban.Changes;

public enum FileChangeCaptureStatus
{
    Succeeded, NoChanges, InvalidCandidate, UnsupportedCandidate, Stale,
    CaptureFailed
}

/// <summary>A fresh capture, not write permission. Executors require their own host admission.</summary>
public sealed record FileChangeCaptureResult(FileChangeCaptureStatus Status,
    CompiledPreviewDocument? Document = null, ResolvedEffectPlan? Plan = null,
    PreviewRunStatus? CaptureStatus = null);

/// <summary>Connects exact existing-file candidates to the qualified capture/execution boundary.</summary>
public sealed class FileChangeCaptureBridge
{
    private readonly WorkspaceReference _workspace;
    private readonly PreviewRuntime _preview;
    private readonly string _readProfile;

    public FileChangeCaptureBridge(WorkspaceReference workspace, IWorkspaceProvider provider,
        IPreviewAuthorizer authorizer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _preview = new PreviewRuntime(workspace, provider, authorizer);
        _readProfile = provider.Capabilities.ReadProfile;
    }

    public async ValueTask<FileChangeCaptureResult> CaptureAsync(EffectInvocation invocation,
        IReadOnlyList<FilePatchCandidate> candidates, PreviewLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates is null) return new(FileChangeCaptureStatus.InvalidCandidate);
        var count = candidates.Count;
        if (count is < 1 or > 64)
            return new(FileChangeCaptureStatus.InvalidCandidate);
        // Snapshot the entire bounded caller-owned collection before admission.
        var frozen = new FilePatchCandidate[count];
        for (var i = 0; i < frozen.Length; i++)
        {
            frozen[i] = candidates[i];
            if (frozen[i] is null || frozen[i].Workspace.Value != _workspace.Id ||
                frozen[i].ProviderProfile != _readProfile)
                return new(FileChangeCaptureStatus.InvalidCandidate);
        }
        if (frozen.All(candidate => candidate.Edits.Count == 0))
            return new(FileChangeCaptureStatus.NoChanges);
        if (frozen.Any(candidate => candidate.Edits.Count is < 1 or > 128))
            return new(FileChangeCaptureStatus.UnsupportedCandidate);

        // Bound retained content and replacement copies before allocating them.
        long retainedBytes = 0;
        long replacementBytes = 0;
        foreach (var candidate in frozen)
        {
            retainedBytes += (long)candidate.OriginalByteLength + candidate.ProposedByteLength;
            foreach (var edit in candidate.Edits)
                replacementBytes += edit.ReplacementUtf8.Length;
        }
        if (retainedBytes > Math.Min(limits?.MaxPlanBytes ?? 4_194_304, 4_194_304) ||
            replacementBytes > Math.Min(limits?.MaxReplacementBytes ?? 1_048_576, 1_048_576))
            return new(FileChangeCaptureStatus.UnsupportedCandidate);

        var stages = new PreviewStage[frozen.Length];
        for (var i = 0; i < frozen.Length; i++)
        {
            var candidate = frozen[i];
            var patches = candidate.Edits.Select(edit => new TextPatch(edit.StartOffset,
                edit.DeleteLength, edit.ReplacementUtf8.ToArray())).ToArray();
            stages[i] = new FilePatchStage(candidate.TargetPath, Array.AsReadOnly(patches),
                candidate.OriginalVersion);
        }

        var compiled = PreviewCompiler.Compile(Array.AsReadOnly(stages), new(_workspace.Id),
            limits, cancellationToken);
        if (!compiled.Succeeded) return new(FileChangeCaptureStatus.InvalidCandidate);
        // Preview reruns whole-document preflight and admits every destination
        // before content reads. The bridge itself cannot open a writer.
        var captured = await _preview.WhatIfAsync(invocation, compiled.Document!, cancellationToken)
            .ConfigureAwait(false);
        if (captured.Status != PreviewRunStatus.Succeeded || captured.Plan is null)
            return new(captured.Status == PreviewRunStatus.StaleObservation
                ? FileChangeCaptureStatus.Stale : FileChangeCaptureStatus.CaptureFailed,
                CaptureStatus: captured.Status);

        var plan = captured.Plan;
        if (!plan.CaptureComplete || plan.Nodes.Count != frozen.Length)
            return new(FileChangeCaptureStatus.CaptureFailed, CaptureStatus: captured.Status);
        for (var i = 0; i < frozen.Length; i++)
        {
            var candidate = frozen[i];
            var proposals = plan.Nodes[i].Proposals;
            if (proposals.Count != 1) return new(FileChangeCaptureStatus.InvalidCandidate);
            var actual = proposals[0];
            if (actual.OriginalVersion != candidate.OriginalVersion ||
                actual.OriginalByteLength != candidate.OriginalByteLength ||
                actual.ProposedByteLength != candidate.ProposedByteLength ||
                !string.Equals(actual.OriginalSha256, candidate.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.ProposedSha256, candidate.ProposedSha256, StringComparison.OrdinalIgnoreCase))
                return new(FileChangeCaptureStatus.Stale);
        }
        return new(FileChangeCaptureStatus.Succeeded, compiled.Document, plan, captured.Status);
    }
}
