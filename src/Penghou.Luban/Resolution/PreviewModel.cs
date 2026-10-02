using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;

namespace Penghou.Luban.Resolution;

public static class PreviewProfile
{
    public const string SchemaVersion = "1";
    public const string CatalogueVersion = "windows-patch-capture-v1";
    public const string ProviderProfile = LanguageProfile.ProviderProfile;
}

public sealed record PreviewLimits(int MaxNodes = 64, int MaxTargets = 1000,
    int MaxReadBytes = 16_777_216, int MaxFileBytes = 1_048_576,
    int MaxReplacementBytes = 1_048_576, int MaxPatchCount = 128,
    int MaxPlanBytes = 4_194_304, int MaxResourceCalls = 100_000, int TimeoutMilliseconds = 30_000);

public enum PreviewSelection { Unspecified, AuthorizedView, AllMatches }
public enum DeferredTool { Build, Test, GitCommit }
public abstract record PreviewStage;
public sealed record FilePatchStage(string Path, IReadOnlyList<TextPatch> Patches, ResourceVersion? ExpectedVersion = null) : PreviewStage;
public sealed record GlobPatchStage(string Root, string Pattern, PreviewSelection Selection,
    IReadOnlyList<TextPatch> Patches, TraversalLimits Limits) : PreviewStage;
public sealed record DeferredToolStage(DeferredTool Tool, string Root) : PreviewStage;

/// <summary>Immutable byte ownership; access returns a defensive copy.</summary>
public sealed class ImmutableBytes
{
    private readonly byte[] _bytes;
    internal ImmutableBytes(ReadOnlySpan<byte> bytes) { _bytes = bytes.ToArray(); Sha256 = Convert.ToHexString(SHA256.HashData(_bytes)); }
    public int Length => _bytes.Length;
    public string Sha256 { get; }
    public byte[] ToArray() => (byte[])_bytes.Clone();
    internal ReadOnlySpan<byte> Span => _bytes;
}
public sealed record FrozenTextPatch(int StartOffset, int DeleteLength, ImmutableBytes ReplacementUtf8);
public abstract record PreviewOperation;
public sealed record ExactPatchOperation(string Path, IReadOnlyList<FrozenTextPatch> Patches, ResourceVersion? ExpectedVersion) : PreviewOperation;
public sealed record SelectedPatchOperation(string Root, string Pattern, PreviewSelection Selection,
    IReadOnlyList<FrozenTextPatch> Patches, TraversalLimits Limits) : PreviewOperation;
public sealed record DeferredToolOperation(DeferredTool Tool, string Root) : PreviewOperation;

public sealed class CompiledPreviewNode
{
    internal CompiledPreviewNode(int index, string identity, PreviewOperation operation) { Index = index; Identity = identity; Operation = operation; }
    public int Index { get; }
    public string Identity { get; }
    public PreviewOperation Operation { get; }
    public string Descriptor => Operation switch
    {
        ExactPatchOperation or SelectedPatchOperation => "files.patch",
        DeferredToolOperation t => t.Tool switch { DeferredTool.Build => "dotnet.build", DeferredTool.Test => "dotnet.test", DeferredTool.GitCommit => "git.commit", _ => throw new InvalidOperationException() },
        _ => throw new InvalidOperationException()
    };
}
public sealed class CompiledPreviewDocument
{
    internal CompiledPreviewDocument(WorkspaceId workspace, PreviewLimits limits, string identity, IEnumerable<CompiledPreviewNode> nodes)
    { Workspace = workspace; Limits = limits; Identity = identity; Nodes = Array.AsReadOnly(nodes.ToArray()); }
    public WorkspaceId Workspace { get; }
    public PreviewLimits Limits { get; }
    public string Identity { get; }
    public IReadOnlyList<CompiledPreviewNode> Nodes { get; }
}
public sealed record PreviewCompilation(CompiledPreviewDocument? Document, IReadOnlyList<LanguageDiagnostic> Diagnostics)
{ public bool Succeeded => Document is not null && Diagnostics.Count == 0; }

public enum PreviewAuthorizationPhase { Preflight, TargetAdmission, ResourceAccess, ProposalAdmission, Release }
public sealed record PreviewAuthorizationRequest(EffectInvocation Invocation, string DocumentIdentity,
    string NodeIdentity, string Descriptor, string SchemaVersion, string CatalogueVersion, string ProviderProfile,
    WorkspaceId Workspace, PreviewOperation Operation, PreviewAuthorizationPhase Phase,
    string? ResourcePath = null, ResourceAction? Action = null, RequestIdentity? ResourceRequestIdentity = null,
    CapturedFilePatch? Proposal = null, string? PlanIdentity = null);
public interface IPreviewAuthorizer
{
    ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request, CancellationToken cancellationToken = default);
}

public enum PreviewRunStatus { Succeeded, Incomplete, InvalidDocument, AuthorityDenied, AuthorizationUnavailable,
    UnsupportedProfile, LimitExceeded, NotFound, AccessDenied, StaleObservation, InvalidPatch, ProviderFailure }
public enum PreviewNodeState { Proposed, Unresolved }
public enum PreviewUnresolvedReason { OpaqueEffect, DependsOnOpaqueEffect, AllMatchCoverageUnavailable, IncompleteSelection, RepeatedTarget, DependsOnUnresolvedEffect }
public sealed record PreviewObservation(string NodeIdentity, string RelativePath, ResourceAction Action,
    RequestIdentity RequestIdentity, ResourceVersion? Version, string Digest, int ByteLength, bool IsComplete);
public sealed record CapturedFilePatch(string RelativePath, ResourceVersion OriginalVersion,
    string OriginalSha256, int OriginalByteLength, string ProposedSha256, int ProposedByteLength,
    IReadOnlyList<FrozenTextPatch> Patches);
public sealed record ResolvedPreviewNode(string NodeIdentity, string Descriptor, PreviewNodeState State,
    PreviewSelection Selection, IReadOnlyList<CapturedFilePatch> Proposals,
    IReadOnlyList<string> Dependencies, PreviewUnresolvedReason? UnresolvedReason);
public sealed class ResolvedEffectPlan
{
    internal ResolvedEffectPlan(EffectInvocation invocation, CompiledPreviewDocument document, string identity,
        IEnumerable<PreviewObservation> observations, IEnumerable<ResolvedPreviewNode> nodes)
    {
        Invocation = invocation; DocumentIdentity = document.Identity; Workspace = document.Workspace; Limits = document.Limits;
        Identity = identity; Observations = new ReadOnlyCollection<PreviewObservation>(observations.ToArray());
        Nodes = new ReadOnlyCollection<ResolvedPreviewNode>(nodes.ToArray());
    }
    public EffectInvocation Invocation { get; }
    public string DocumentIdentity { get; }
    public WorkspaceId Workspace { get; }
    public PreviewLimits Limits { get; }
    public string Identity { get; }
    public string SchemaVersion => PreviewProfile.SchemaVersion;
    public string CatalogueVersion => PreviewProfile.CatalogueVersion;
    public string ProviderProfile => PreviewProfile.ProviderProfile;
    public IReadOnlyList<PreviewObservation> Observations { get; }
    public IReadOnlyList<ResolvedPreviewNode> Nodes { get; }
    public bool CaptureComplete => Nodes.All(n => n.State == PreviewNodeState.Proposed);
    public bool CanCommit => false;
}
public sealed record PreviewReadiness(PreviewRunStatus Status, bool HasDynamicTargets)
{ public bool ReadyForKnownRequirements => Status == PreviewRunStatus.Succeeded; }
public sealed record PreviewRunResult(PreviewRunStatus Status, ResolvedEffectPlan? Plan = null);
