using System.Collections.ObjectModel;
using Penghou.IO.Abstractions;

namespace Penghou.Luban.Language;

public static class LanguageProfile
{
    public const string LanguageVersion = "1";
    public const string IrVersion = "1";
    public const string CatalogueVersion = "windows-read-v1";
    public const string ProviderProfile = "local-windows-read-v1";
    public const string DescriptorVersion = "1";
    public const int MaxGlobWork = 16 * 1024 * 1024;
    public const string TextChangeLanguageVersion = "2";
    public const string TextChangeIrVersion = "2";
    public const string TextChangeCatalogueVersion = "windows-text-change-v2";
    public const string TextChangeProviderProfile = ProviderProfile;
}

public sealed record LanguageVersions(
    string Language = LanguageProfile.LanguageVersion,
    string Ir = LanguageProfile.IrVersion,
    string Catalogue = LanguageProfile.CatalogueVersion,
    string Provider = LanguageProfile.ProviderProfile);

public sealed record LanguageExecutionLimits(
    int MaxResourceCalls = 100_000,
    int MaxReadBytes = 16 * 1024 * 1024,
    int MaxIntermediateBytes = 4 * 1024 * 1024,
    int MaxOutputBytes = 4 * 1024 * 1024,
    int MaxValues = 10_000,
    int TimeoutMilliseconds = 30_000);

public sealed record LanguageCompilerOptions(
    int MaxSourceBytes = 65_536,
    int MaxTokens = 4096,
    int MaxStatements = 64,
    int MaxNodes = 128,
    int MaxLiteralBytes = 8192,
    LanguageVersions? Versions = null,
    LanguageExecutionLimits? ExecutionLimits = null);

public sealed record TraversalLimits(int MaxDepth = 16, int MaxEntries = 10_000, int MaxMatches = 1000, int MaxOutputBytes = 262_144);
public sealed record SearchLimits(
    int MaxDepth = 16, int MaxEntries = 10_000, int MaxMatches = 1000,
    int MaxOutputBytes = 262_144, int MaxFileBytes = 1_048_576, int MaxBytesScanned = 10_485_760);

public enum LanguageValueKind { FileReference, FileContent, SearchMatch, Count, TextDiff, TextMerge, TextMergeConflict, FileWindow, SearchContextMatch }
public enum LanguageEffectKind { Read, Find, Search, Diff, Merge }

public abstract record LanguageStage;
// A null Path/Root is permitted only on a correctly typed pipeline edge.
public sealed record ReadStage(string? Path, int MaxBytes = 1_048_576, int? StartLine = null, int? LineCount = null) : LanguageStage
{
    public ReadStage(string? path, int maxBytes) : this(path, maxBytes, null, null) { }
    public void Deconstruct(out string? path, out int maxBytes) { path = Path; maxBytes = MaxBytes; }
}
public sealed record FindStage(string Root, string Pattern, TraversalLimits Limits) : LanguageStage;
public sealed record SearchStage(string Query, string? Root, string Include, string? Exclude, SearchLimits Limits, int ContextLines = 0) : LanguageStage
{
    public SearchStage(string query, string? root, string include, string? exclude, SearchLimits limits)
        : this(query, root, include, exclude, limits, 0) { }
    public void Deconstruct(out string query, out string? root, out string include, out string? exclude, out SearchLimits limits)
    { query = Query; root = Root; include = Include; exclude = Exclude; limits = Limits; }
}
public sealed record DiffStage(string BeforePath, string AfterPath, Penghou.Luban.Changes.TextChangeOptions? Options = null) : LanguageStage;
public sealed record MergeStage(string BasePath, string OursPath, string TheirsPath, Penghou.Luban.Changes.TextChangeOptions? Options = null) : LanguageStage;
public sealed record TakeStage(int Count) : LanguageStage;
public sealed record CountStage : LanguageStage;

public sealed record LanguageDiagnostic(string Code, string Message, int Offset = 0, int Length = 0);
public sealed record LanguageCompilation(CompiledDocument? Document, IReadOnlyList<LanguageDiagnostic> Diagnostics)
{
    public bool Succeeded => Document is not null && Diagnostics.Count == 0;
}

public sealed class CompiledNode
{
    internal CompiledNode(LanguageStage stage, LanguageValueKind output, string identity, int statement, int index)
    { Stage = stage; Output = output; Identity = identity; StatementIndex = statement; NodeIndex = index; }
    public LanguageStage Stage { get; }
    public LanguageValueKind Output { get; }
    public string Identity { get; }
    public int StatementIndex { get; }
    public int NodeIndex { get; }
    public string Descriptor => Stage switch { ReadStage => "files.read", FindStage => "files.find", SearchStage => "files.search-text", DiffStage => "files.diff-text", MergeStage => "files.merge-text", TakeStage => "pure.take", CountStage => "pure.count", _ => throw new InvalidOperationException() };
}

public sealed class CompiledDocument
{
    internal CompiledDocument(WorkspaceId workspace, LanguageVersions versions, LanguageExecutionLimits limits,
        string identity, IReadOnlyList<IReadOnlyList<CompiledNode>> statements)
    {
        Workspace = workspace; Versions = versions; Limits = limits; Identity = identity;
        Statements = new ReadOnlyCollection<IReadOnlyList<CompiledNode>>(statements.Select(s =>
            (IReadOnlyList<CompiledNode>)new ReadOnlyCollection<CompiledNode>(s.ToArray())).ToArray());
    }
    public WorkspaceId Workspace { get; }
    public LanguageVersions Versions { get; }
    public LanguageExecutionLimits Limits { get; }
    public string Identity { get; }
    public IReadOnlyList<IReadOnlyList<CompiledNode>> Statements { get; }
}

public enum LanguageAuthorizationPhase { Preflight, EffectStart, ResourceAccess, Release }
public enum LanguageAuthorityStatus { Unavailable, Deny, Permit }
public sealed record LanguageAuthorityDecision(LanguageAuthorityStatus Status);
public sealed record LanguageAuthorizationRequest(
    EffectInvocation Invocation, string DocumentIdentity, string NodeIdentity,
    string Descriptor, string DescriptorVersion, LanguageVersions Versions,
    WorkspaceId Workspace, LanguageStage Stage, LanguageAuthorizationPhase Phase,
    string? ResourcePath = null, ResourceAction? Action = null, RequestIdentity? ResourceRequestIdentity = null);

/// <summary>Trusted host authority. Preflight observes authority state only and grants no resource access.</summary>
public interface ILanguageAuthorizer
{
    ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default);
}

public enum LanguageRunStatus { Succeeded, InvalidDocument, AuthorityDenied, AuthorizationUnavailable, UnsupportedProfile, LimitExceeded, NotFound, AccessDenied, ProviderFailure, Cancelled }
public sealed record LanguageReadiness(LanguageRunStatus Status, bool HasDynamicTargets, LanguageRunDiagnostic? Diagnostic = null)
{
    public bool ReadyForKnownRequirements => Status == LanguageRunStatus.Succeeded;
}
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(FileReferenceValue), "file-reference")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(FileContentValue), "file-content")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SearchMatchValue), "search-match")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CountValue), "count")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DiffValue), "text-diff")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(MergeValue), "text-merge")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(MergeConflictValue), "text-merge-conflict")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(FileWindowValue), "file-window")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SearchContextMatchValue), "search-context-match")]
public abstract record LanguageValue;
public sealed record FileReferenceValue(string RelativePath) : LanguageValue;
public sealed record FileContentValue(string RelativePath, string Content, int ByteLength, string Sha256) : LanguageValue;
public sealed record SearchMatchValue(string RelativePath, int LineNumber, string Line) : LanguageValue;
public sealed record CountValue(int Count) : LanguageValue;
/// <summary>A bounded line window from a complete authorized input snapshot.</summary>
public sealed record FileWindowValue(string RelativePath, string Content, int StartLine, int RequestedLineCount,
    int ReturnedLineCount, bool CompleteInput, bool CompleteWindow, string InputSha256, int InputByteLength) : LanguageValue;
/// <summary>A match plus bounded surrounding lines and exact absolute UTF-8 byte span.</summary>
public sealed record SearchContextMatchValue(string RelativePath, int LineNumber, string Line,
    int MatchStartByte, int MatchLengthBytes, IReadOnlyList<string> BeforeContext, IReadOnlyList<string> AfterContext,
    bool CompleteInput, bool CompleteWindow, string InputSha256, int InputByteLength) : LanguageValue;
/// <summary>Bounded read-only comparison facts; it contains no write capability.</summary>
public sealed record DiffValue(string BeforePath, string AfterPath, string ProfileIdentity, string BeforeSha256,
    int BeforeByteLength, string AfterSha256, int AfterByteLength, IReadOnlyList<Penghou.Luban.Changes.TextEdit> Edits,
    bool IsUntrustedCandidate = true) : LanguageValue;
/// <summary>Clean three-way merge proposal. It remains an untrusted candidate with no write capability.</summary>
public sealed record MergeValue(string BasePath, string OursPath, string TheirsPath, string TargetPath, string ProfileIdentity,
    string BaseSha256, int BaseByteLength, string OursSha256, int OursByteLength, string TheirsSha256,
    int TheirsByteLength, string ProposedSha256, int ProposedByteLength, string ProposedText,
    IReadOnlyList<Penghou.Luban.Changes.TextEdit> Edits, bool IsUntrustedCandidate = true) : LanguageValue;
public sealed record MergeConflictValue(string BasePath, string OursPath, string TheirsPath,
    string ProfileIdentity, IReadOnlyList<Penghou.Luban.Changes.TextMergeConflict> Conflicts,
    bool IsUntrustedCandidate = true) : LanguageValue;
/// <summary>Public coverage reasons disclose supported limits, never denied names or hidden counts.</summary>
[Flags]
public enum LanguageCoverageReason
{
    None = 0, TraversalLimit = 1, DepthLimit = 2, FileSizeLimit = 4,
    ScanByteLimit = 8, MatchLimit = 16, OutputByteLimit = 32, TakeLimit = 64
}
public sealed record LanguageStatementResult(IReadOnlyList<LanguageValue> Values, bool Truncated,
    LanguageCoverageReason CoverageReasons = LanguageCoverageReason.None)
{
    /// <summary>Completeness applies only to the currently authorized observation view.</summary>
    public bool AuthorizedViewComplete => !Truncated;
}
/// <summary>A safe public failure code and location, without provider exceptions or protected paths.</summary>
public sealed record LanguageRunDiagnostic(string Code, string? NodeIdentity = null, string? LimitName = null);
public sealed record LanguageRunResult(LanguageRunStatus Status, IReadOnlyList<LanguageStatementResult>? Statements = null, LanguageRunDiagnostic? Diagnostic = null);
