using System.Collections.ObjectModel;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;

namespace Penghou.Luban.Changes;

public enum FileChangeOperation { Diff, Merge, CandidateValidation, UnifiedImport }
public enum FileChangePhase { Preflight, TargetAdmission, ResourceAccess, ResultRelease }
public enum FileChangeInputRole { Target, ProposedSource, Base, Ours, Theirs, UnifiedTarget }
public enum FileChangeStatus
{
    Succeeded, Conflicted, Valid, Stale, InvalidInput, UnsupportedProfile, LimitExceeded,
    Cancelled, AuthorizationDenied, AuthorizationUnavailable, NotFound, AccessDenied, ProviderFailure
}

/// <summary>A logical same-workspace file named for its role in one operation.</summary>
public sealed record FileChangeResource(WorkspaceId Workspace, WorkspacePath Path, FileChangeInputRole Role);

/// <summary>One mandatory file-change authorization check. Inputs are the frozen complete operation set.</summary>
public sealed record FileChangeAuthorizationRequest(
    EffectInvocation Invocation,
    WorkspaceId Workspace,
    string OperationId,
    FileChangeOperation Operation,
    FileChangePhase Phase,
    IReadOnlyList<FileChangeResource> Inputs,
    TextChangeOptions? TextOptions = null,
    TextUnifiedImportOptions? ImportOptions = null,
    FileChangeLimits? Limits = null,
    FileChangeResource? Resource = null,
    ResourceAction? Action = null,
    RequestIdentity? RequestIdentity = null,
    ResourceVersion? ObservedVersion = null,
    string? ContentSha256 = null,
    int? ContentByteLength = null,
    string? CandidateIdentity = null,
    string? ResultIdentity = null,
    FileChangeStatus? Outcome = null,
    IReadOnlyList<FileChangeInputSnapshot>? Observations = null,
    string? ProviderProfile = null,
    int? ReadLimitBytes = null);

public enum FileChangeAuthorizationStatus { Permit, Deny, Unavailable }
public sealed record FileChangeAuthorizationDecision(FileChangeAuthorizationStatus Status);

/// <summary>Host policy hook required for every protected file-change operation.</summary>
public interface IFileChangeAuthorizer
{
    ValueTask<FileChangeAuthorizationDecision> AuthorizeAsync(
        FileChangeAuthorizationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Finite aggregate bounds for one same-workspace read-only file operation.</summary>
public sealed record FileChangeLimits(int MaxFiles = 3, int MaxTotalReadBytes = 393_216,
    int TimeoutMilliseconds = 30_000, int MaxTotalRetainedBytes = 4_194_304)
{
    public static FileChangeLimits Default { get; } = new();
    internal bool IsValid => MaxFiles is >= 1 and <= 32 && MaxTotalReadBytes is >= 1 and <= 4_194_304 &&
        TimeoutMilliseconds is >= 1 and <= 30_000 && MaxTotalRetainedBytes is >= 1 and <= 8_388_608;
}

public sealed record FileDiffRequest(string TargetPath, string ProposedSourcePath,
    TextChangeOptions? Options = null);
public sealed record FileMergeRequest(string BasePath, string OursPath, string TheirsPath,
    TextChangeOptions? Options = null);

/// <summary>Exact protected input captured from the selected provider.</summary>
public sealed class FileChangeInputSnapshot
{
    internal FileChangeInputSnapshot(FileChangeResource resource, ResourceVersion version, ImmutableBytes content)
    {
        Resource = resource;
        Version = version;
        Content = content;
    }

    public FileChangeResource Resource { get; }
    public ResourceVersion Version { get; }
    public string Sha256 => Content.Sha256;
    public int ByteLength => Content.Length;
    public ImmutableBytes Content { get; }
}

/// <summary>
/// An immutable, untrusted existing-file patch candidate. It has no write authority;
/// a host must admit it through the normal preview and execution boundaries.
/// </summary>
public sealed class FilePatchCandidate
{
    internal FilePatchCandidate(WorkspaceId workspace, string targetPath, ResourceVersion originalVersion,
        ImmutableBytes originalContent, ImmutableBytes proposedContent, IReadOnlyList<FrozenTextPatch> edits,
        IReadOnlyList<FileChangeInputSnapshot> inputs, TextChangeOptions options, FileChangeLimits limits,
        FileChangeOperation operation, string providerProfile, string profileIdentity, string identity)
    {
        Workspace = workspace;
        TargetPath = targetPath;
        OriginalVersion = originalVersion;
        OriginalContent = originalContent;
        OriginalSha256 = originalContent.Sha256;
        OriginalByteLength = originalContent.Length;
        ProposedContent = proposedContent;
        ProposedSha256 = proposedContent.Sha256;
        ProposedByteLength = proposedContent.Length;
        Edits = new ReadOnlyCollection<FrozenTextPatch>(edits.ToArray());
        Inputs = new ReadOnlyCollection<FileChangeInputSnapshot>(inputs.ToArray());
        Options = options with { };
        Limits = limits with { };
        Operation = operation;
        ProviderProfile = providerProfile;
        ProfileIdentity = profileIdentity;
        CandidateIdentity = identity;
    }

    public WorkspaceId Workspace { get; }
    public string TargetPath { get; }
    public ResourceVersion OriginalVersion { get; }
    public string OriginalSha256 { get; }
    public int OriginalByteLength { get; }
    public ImmutableBytes OriginalContent { get; }
    public string ProposedSha256 { get; }
    public int ProposedByteLength { get; }
    public ImmutableBytes ProposedContent { get; }
    public IReadOnlyList<FrozenTextPatch> Edits { get; }
    public IReadOnlyList<FileChangeInputSnapshot> Inputs { get; }
    public TextChangeOptions Options { get; }
    public FileChangeLimits Limits { get; }
    public FileChangeOperation Operation { get; }
    public string ProviderProfile { get; }
    public string ProfileIdentity { get; }
    public string CandidateIdentity { get; }
}

/// <summary>Read-only result. Failed operations never expose a partial candidate set.</summary>
public sealed class FileChangeResult
{
    internal FileChangeResult(FileChangeStatus status, IEnumerable<FilePatchCandidate>? candidates = null,
        IEnumerable<TextMergeConflict>? conflicts = null, FilePatchCandidate? validatedCandidate = null,
        TextDiffResult? diff = null, TextMergeResult? merge = null, string? resultIdentity = null)
    {
        Status = status;
        Candidates = new ReadOnlyCollection<FilePatchCandidate>((status == FileChangeStatus.Succeeded ? candidates : null)?.ToArray() ?? []);
        Conflicts = new ReadOnlyCollection<TextMergeConflict>((status == FileChangeStatus.Conflicted ? conflicts : null)?.ToArray() ?? []);
        ValidatedCandidate = status == FileChangeStatus.Valid ? validatedCandidate : null;
        Diff = status == FileChangeStatus.Succeeded ? diff : null;
        Merge = status is FileChangeStatus.Succeeded or FileChangeStatus.Conflicted ? merge : null;
        ResultIdentity = resultIdentity;
    }

    public FileChangeStatus Status { get; }
    public IReadOnlyList<FilePatchCandidate> Candidates { get; }
    public IReadOnlyList<TextMergeConflict> Conflicts { get; }
    public FilePatchCandidate? ValidatedCandidate { get; }
    public TextDiffResult? Diff { get; }
    public TextMergeResult? Merge { get; }
    public string? ResultIdentity { get; }
}
