using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban;
using Penghou.Luban.Language;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: AiToolHost <workspace-root> [luban-source | --describe]");
    return 2;
}

// Host setup chooses the root, caller identity, policy and limits. Only source
// would come from an AI tool call. Replace these fixed demo identities with
// authenticated invocation context in an application.
var workspace = new WorkspaceReference("demo-workspace");
var provider = new LocalWorkspaceProvider(new WorkspaceId(workspace.Id), args[0]);
var invocation = new EffectInvocation("demo-agent", "demo-read-tool", Guid.NewGuid().ToString("N"));
var tool = new LanguageToolRuntime(workspace, provider, new SourceReadPolicy(),
    new(ExecutionLimits: new(MaxReadBytes: 4 * 1024 * 1024, MaxOutputBytes: 256 * 1024, MaxValues: 1000)));

if (args.Length == 2 && args[1] == "--describe")
{
    Console.WriteLine(LanguageToolRuntime.ToJson(tool.Describe()));
    return 0;
}

var source = args.Length == 2 ? args[1] : "find src **/*.cs | search Authorize | take 5";
var result = await tool.ExecuteAsync(source, invocation);
Console.WriteLine(LanguageToolRuntime.ToJson(result));
return result.Status == LanguageToolStatus.Succeeded ? 0 : 1;

/// <summary>A small independent policy, with no Hufu dependency or permit fallback.</summary>
sealed class SourceReadPolicy : ILanguageAuthorizer
{
    public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Invocation.SubjectId != "demo-agent" || request.Invocation.EffectId != "demo-read-tool" ||
            request.Workspace.Value != "demo-workspace") return Decision(false);

        if (request.Action is { } action)
        {
            var path = request.ResourcePath ?? "";
            return Decision(action switch
            {
                ResourceAction.ReadMetadata => path.Length == 0 || IsSourcePath(path),
                ResourceAction.ListDirectory => IsSourcePath(path),
                ResourceAction.ReadFile => IsSourceFile(path),
                _ => false
            });
        }

        // Semantic admission, effect start and release all inspect the same
        // supported request. A piped read/search still needs concrete checks.
        return Decision(request.Stage switch
        {
            ReadStage read => read.Path is null || IsSourceFile(read.Path),
            FindStage find => IsSourcePath(find.Root),
            SearchStage search => search.Root is null || IsSourcePath(search.Root),
            _ => false
        });
    }

    private static bool IsSourcePath(string path) => path.Equals("src", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("src/", StringComparison.OrdinalIgnoreCase);
    private static bool IsSourceFile(string path) => path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    private static ValueTask<LanguageAuthorityDecision> Decision(bool allowed) =>
        ValueTask.FromResult(new LanguageAuthorityDecision(allowed ? LanguageAuthorityStatus.Permit : LanguageAuthorityStatus.Deny));
}
