using System.Text.Json;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class LanguageToolRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-tool-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceReference _workspace;
    private static readonly EffectInvocation Invocation = new("subject", "effect", "attempt");
    public LanguageToolRuntimeTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = new("tool-workspace", _root);
    }

    [Fact]
    public void DiscoveryDescribesOnlyImplementedCommandsAndHostBounds()
    {
        var authority = new Authority();
        var tool = new LanguageToolRuntime(_workspace, authority,
            new(MaxSourceBytes: 128, ExecutionLimits: new(MaxValues: 3)));
        var capabilities = tool.Describe();
        Assert.Equal(new[] { "read", "find", "search", "take", "count" }, capabilities.Commands.Select(c => c.Name));
        Assert.Equal(128, capabilities.MaxSourceBytes);
        Assert.Equal(3, capabilities.Limits.MaxValues);
        Assert.Equal("windows", capabilities.Platform);
        Assert.Empty(authority.Requests);
        using var json = JsonDocument.Parse(LanguageToolRuntime.ToJson(capabilities));
        Assert.Equal("1", json.RootElement.GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public async Task InvalidSourceReturnsCompilerDiagnosticsWithoutAuthorityOrIo()
    {
        var authority = new Authority();
        var result = await new LanguageToolRuntime(_workspace, authority).ExecuteAsync("read x\r\nbogus y", Invocation);
        Assert.Equal(LanguageToolStatus.InvalidSource, result.Status);
        Assert.Null(result.Execution);
        Assert.Null(result.DocumentIdentity);
        Assert.Equal(8, Assert.Single(result.CompilationDiagnostics).Offset);
        Assert.Empty(authority.Requests);
    }

    [Fact]
    public async Task JsonRetainsTypedValuesAndResultIdentity()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        var result = await new LanguageToolRuntime(_workspace, new Authority()).ExecuteAsync("read a.txt", Invocation);
        Assert.Equal(LanguageToolStatus.Succeeded, result.Status);
        using var json = JsonDocument.Parse(LanguageToolRuntime.ToJson(result));
        var value = json.RootElement.GetProperty("execution").GetProperty("statements")[0].GetProperty("values")[0];
        Assert.Equal("file-content", value.GetProperty("kind").GetString());
        Assert.Equal("hello", value.GetProperty("content").GetString());
        Assert.Equal("Succeeded", json.RootElement.GetProperty("status").GetString());
        Assert.NotNull(result.DocumentIdentity);
    }

    [Fact]
    public async Task DenialReturnsSafeDiagnosticAndNoProtectedResults()
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "private");
        var result = await new LanguageToolRuntime(_workspace,
            new Authority(r => r.Phase == LanguageAuthorizationPhase.ResourceAccess ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit))
            .ExecuteAsync("read secret.txt", Invocation);
        Assert.Equal(LanguageToolStatus.ExecutionFailed, result.Status);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Execution!.Status);
        Assert.NotNull(result.Execution.Diagnostic!.NodeIdentity);
        Assert.Null(result.Execution.Statements);
        var json = LanguageToolRuntime.ToJson(result);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("secret.txt", json);
    }

    [Theory]
    [InlineData("search needle --max-file-bytes 10")]
    [InlineData("find *.txt | search needle --max-file-bytes 10")]
    public async Task OversizedFileDoesNotStarveLaterMatches(string source)
    {
        File.WriteAllText(Path.Combine(_root, "a-large.txt"), new string('a', 100));
        File.WriteAllText(Path.Combine(_root, "b-small.txt"), "needle");
        var authority = new Authority();
        var result = await new LanguageToolRuntime(_workspace, authority).ExecuteAsync(source, Invocation);
        Assert.Equal(LanguageToolStatus.Succeeded, result.Status);
        var statement = Assert.Single(result.Execution!.Statements!);
        Assert.Equal("b-small.txt", Assert.IsType<SearchMatchValue>(Assert.Single(statement.Values)).RelativePath);
        Assert.True(statement.Truncated);
        Assert.False(statement.AuthorizedViewComplete);
        Assert.True(statement.CoverageReasons.HasFlag(LanguageCoverageReason.FileSizeLimit));
        Assert.DoesNotContain(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile && r.ResourcePath == "a-large.txt" && source.StartsWith("search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TakeCoverageSurvivesCountAndReleaseRevocation()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        var tool = new LanguageToolRuntime(_workspace, new Authority());
        var result = await tool.ExecuteAsync("find *.txt | take 1 | count", Invocation);
        var statement = Assert.Single(result.Execution!.Statements!);
        Assert.Equal(1, Assert.IsType<CountValue>(Assert.Single(statement.Values)).Count);
        Assert.True(statement.CoverageReasons.HasFlag(LanguageCoverageReason.TakeLimit));
        var revoked = new LanguageToolRuntime(_workspace,
            new Authority(r => r.Phase == LanguageAuthorizationPhase.Release ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit));
        var denied = await revoked.ExecuteAsync("find *.txt | take 1 | count", Invocation);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, denied.Execution!.Status);
        Assert.Null(denied.Execution.Statements);
    }

    [Fact]
    public async Task BudgetFailureNamesLimitWithoutPartialResults()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        var tool = new LanguageToolRuntime(_workspace, new Authority(),
            new(ExecutionLimits: new(MaxOutputBytes: 1)));
        var result = await tool.ExecuteAsync("read a.txt", Invocation);
        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Execution!.Status);
        Assert.Equal(nameof(LanguageExecutionLimits.MaxOutputBytes), result.Execution.Diagnostic!.LimitName);
        Assert.Null(result.Execution.Statements);
    }

    [Fact]
    public void UnsupportedConfigurationCannotBeAdvertised()
    {
        Assert.Throws<ArgumentException>(() => new LanguageToolRuntime(_workspace, new Authority(), new(MaxTokens: 0)));
        Assert.Throws<ArgumentException>(() => new LanguageToolRuntime(_workspace, new Authority(), new(Versions: new(Language: "99"))));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class Authority(Func<LanguageAuthorizationRequest, LanguageAuthorityStatus>? decide = null) : ILanguageAuthorizer
    {
        public List<LanguageAuthorizationRequest> Requests { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new LanguageAuthorityDecision(decide?.Invoke(request) ?? LanguageAuthorityStatus.Permit));
        }
    }
}
