using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class LanguageBoundaryTests
{
    [Fact]
    public async Task GlobWorkBudgetBoundsWholeDiscovery()
    {
        using var workspace = new Workspace();
        for (var i = 0; i < 400; i++)
            File.WriteAllText(Path.Combine(workspace.Root, new string('a', 180) + i + ".txt"), "a");
        var pattern = string.Concat(Enumerable.Repeat("*a", 120)) + "*.txt";
        var result = await Run(workspace, "find . \"" + pattern + "\"", new());
        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task EmptyFindStillRequiresDirectoryObservationRelease()
    {
        using var workspace = new Workspace();
        var compilation = LanguageCompiler.Compile("find . *.txt | count", new("workspace"));
        Assert.True(compilation.Succeeded);
        var runtime = new LanguageRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), new DenyDirectoryRelease());
        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), compilation.Document!);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task SearchCannotTurnGlobalReadExhaustionIntoSuccessfulTruncation()
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "a"), "aaaa");
        File.WriteAllText(Path.Combine(workspace.Root, "b"), "aaaa");
        var result = await Run(workspace, "read a\nsearch a . --include b --max-bytes-scanned 4", new(MaxReadBytes: 4));
        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task NoMatchSearchCountStillRequiresResourceReleaseAuthority()
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "a"), "unrelated content");
        var compilation = LanguageCompiler.Compile("search needle . | count", new("workspace"));
        Assert.True(compilation.Succeeded);
        var runtime = new LanguageRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), new DenyContentRelease());
        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), compilation.Document!);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task DirectoryFrontierCannotGrowBeyondGlobalValueBudget()
    {
        using var workspace = new Workspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "a"));
        Directory.CreateDirectory(Path.Combine(workspace.Root, "b"));
        var result = await Run(workspace, "find . **/*.txt", new(MaxValues: 1));
        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task CountDoesNotEraseDocumentWideReleaseDependencyBudget()
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "a"), "a");
        File.WriteAllText(Path.Combine(workspace.Root, "b"), "b");
        // Final count values fit MaxValues=2, but four distinct effect/resource
        // dependencies would otherwise accumulate independently of stream limits.
        var result = await Run(workspace, "find . * | gc | count", new(MaxValues: 2));
        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task ResourceBudgetFailureInsideProviderIsReportedAsLimitExceeded()
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "a"), "a");
        var authority = new AllowAuthority();
        var compilation = LanguageCompiler.Compile("read a", new("workspace"), new(ExecutionLimits: new(MaxResourceCalls: 3)));
        Assert.True(compilation.Succeeded);
        var runtime = new LanguageRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), authority);
        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), compilation.Document!);
        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess);
        Assert.Null(result.Statements);
    }

    private static async Task<LanguageRunResult> Run(Workspace workspace, string source, LanguageExecutionLimits limits)
    {
        var compilation = LanguageCompiler.Compile(source, new("workspace"), new(ExecutionLimits: limits));
        Assert.True(compilation.Succeeded, string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
        var runtime = new LanguageRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), new AllowAuthority());
        return await runtime.ExecuteAsync(new("subject", "effect", "attempt"), compilation.Document!);
    }

    private sealed class AllowAuthority : ILanguageAuthorizer
    {
        internal List<LanguageAuthorizationRequest> Requests { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
        }
    }

    private sealed class DenyContentRelease : ILanguageAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(request.Phase == LanguageAuthorizationPhase.Release && request.Action == ResourceAction.ReadFile
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit));
    }

    private sealed class DenyDirectoryRelease : ILanguageAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(request.Phase == LanguageAuthorizationPhase.Release && request.Action == ResourceAction.ListDirectory
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit));
    }

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "luban-boundary-" + Guid.NewGuid().ToString("N"));
        internal Workspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }
}
