using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Luban.Language;

public sealed record LanguageCommandDescription(string Name, IReadOnlyList<string> Aliases,
    string Syntax, IReadOnlyList<LanguageValueKind> InputTypes, LanguageValueKind? OutputType,
    IReadOnlyList<string> Options, bool HasResourceEffects);

/// <summary>Host-selected capabilities describe implementations, never the caller's grants.</summary>
public sealed record LanguageToolCapabilities(string SchemaVersion, LanguageVersions Versions,
    string Platform, bool PlatformAvailable, LanguageExecutionLimits Limits,
    int MaxSourceBytes, int MaxTokens, int MaxStatements, int MaxNodes, int MaxLiteralBytes,
    IReadOnlyList<LanguageCommandDescription> Commands);

public enum LanguageToolStatus { Succeeded, InvalidSource, ExecutionFailed }
public sealed record LanguageToolResult(string SchemaVersion, LanguageToolStatus Status,
    string? DocumentIdentity, LanguageVersions Versions,
    IReadOnlyList<LanguageDiagnostic> CompilationDiagnostics, LanguageRunResult? Execution);

/// <summary>
/// Read-only AI-tool entry point. Identity, workspace, policy and bounds come
/// from the trusted host. Source supplies only the closed language document.
/// </summary>
public sealed class LanguageToolRuntime
{
    public const string ToolSchemaVersion = "1";
    private readonly WorkspaceReference _workspace;
    private readonly LanguageRuntime _runtime;
    private readonly LanguageCompilerOptions _options;

    public LanguageToolRuntime(WorkspaceReference workspace, ILanguageAuthorizer authorizer,
        LanguageCompilerOptions? options = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _runtime = new LanguageRuntime(workspace, authorizer);
        var selected = options ?? new LanguageCompilerOptions();
        var validation = LanguageCompiler.Compile("read probe", new(workspace.Id), selected);
        // Tiny valid host bounds can reject this probe's literal/source size;
        // only configuration/version failures indicate an invalid host setup.
        if (validation.Diagnostics.Any(d => d.Code is "LUBAN_OPTIONS" or "LUBAN_PROFILE_VERSION"))
            throw new ArgumentException("Unsupported language versions or invalid compiler/execution bounds.", nameof(options));
        _options = selected with
        {
            Versions = (selected.Versions ?? new LanguageVersions()) with { },
            ExecutionLimits = (selected.ExecutionLimits ?? new LanguageExecutionLimits()) with { }
        };
    }

    public LanguageToolCapabilities Describe() => new(ToolSchemaVersion, _options.Versions!,
        "windows", OperatingSystem.IsWindows(), _options.ExecutionLimits!,
        _options.MaxSourceBytes, _options.MaxTokens, _options.MaxStatements,
        _options.MaxNodes, _options.MaxLiteralBytes, Commands);

    public async ValueTask<LanguageToolResult> ExecuteAsync(string source, EffectInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var compilation = LanguageCompiler.Compile(source, new(_workspace.Id), _options, cancellationToken);
        if (!compilation.Succeeded)
            return new(ToolSchemaVersion, LanguageToolStatus.InvalidSource, null, _options.Versions!,
                Array.AsReadOnly(compilation.Diagnostics.ToArray()), null);
        var result = await _runtime.ExecuteAsync(invocation, compilation.Document!, cancellationToken).ConfigureAwait(false);
        return new(ToolSchemaVersion, result.Status == LanguageRunStatus.Succeeded
            ? LanguageToolStatus.Succeeded : LanguageToolStatus.ExecutionFailed,
            compilation.Document!.Identity, compilation.Document.Versions,
            Array.AsReadOnly(Array.Empty<LanguageDiagnostic>()), result);
    }

    /// <summary>Versioned JSON transport with explicit value kinds and named statuses.</summary>
    public static string ToJson(LanguageToolResult result) => JsonSerializer.Serialize(result, JsonOptions);
    public static string ToJson(LanguageToolCapabilities capabilities) => JsonSerializer.Serialize(capabilities, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static IReadOnlyList<T> Freeze<T>(params T[] values) => new ReadOnlyCollection<T>(values);
    private static readonly IReadOnlyList<LanguageCommandDescription> Commands = Freeze(
        new LanguageCommandDescription("read", Freeze("cat", "gc", "files.read"),
            "read path [--max-bytes n] | read [--max-bytes n] (with FileReference input)",
            Freeze(LanguageValueKind.FileReference), LanguageValueKind.FileContent, Freeze("max-bytes"), true),
        new LanguageCommandDescription("find", Freeze("fd", "files.find"), "find [root] pattern",
            Freeze<LanguageValueKind>(), LanguageValueKind.FileReference,
            Freeze("max-depth", "max-entries", "max-matches", "max-output-bytes"), true),
        new LanguageCommandDescription("search", Freeze("grep", "files.search-text"), "search query [root]",
            Freeze(LanguageValueKind.FileReference), LanguageValueKind.SearchMatch,
            Freeze("include", "exclude", "max-depth", "max-entries", "max-matches", "max-output-bytes", "max-file-bytes", "max-bytes-scanned"), true),
        new LanguageCommandDescription("take", Freeze<string>(), "take n",
            Freeze(LanguageValueKind.FileReference, LanguageValueKind.FileContent, LanguageValueKind.SearchMatch),
            null, Freeze<string>(), false),
        new LanguageCommandDescription("count", Freeze<string>(), "count (terminal stage)",
            Freeze(LanguageValueKind.FileReference, LanguageValueKind.FileContent, LanguageValueKind.SearchMatch),
            LanguageValueKind.Count, Freeze<string>(), false));
}
