using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Language.Tests;

public sealed class LanguageRuntimeTests
{
    [Fact]
    public async Task PreflightChecksEveryKnownEffectBeforeAnyResourceAccess()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "visible");
        var authority = new RecordingAuthority(request =>
            request.Phase == LanguageAuthorizationPhase.Preflight && request.ResourcePath == "missing.txt"
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, document, invocation) = Create(workspace, authority, "read present.txt\nread missing.txt");

        var readiness = await runtime.PreflightAsync(invocation, document);

        Assert.Equal(LanguageRunStatus.AuthorityDenied, readiness.Status);
        Assert.DoesNotContain(authority.Requests, r => r.Phase is LanguageAuthorizationPhase.ResourceAccess or LanguageAuthorizationPhase.EffectStart or LanguageAuthorizationPhase.Release);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.Preflight && r.ResourcePath == "missing.txt");

        authority.Requests.Clear();
        var result = await runtime.ExecuteAsync(invocation, document);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess);
    }

    [Fact]
    public async Task StaticPreflightDoesNotTouchFilesystemAndFindReportsDynamicTargets()
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "find absent \"*.txt\"");

        var readiness = await runtime.PreflightAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.Succeeded, readiness.Status);
        Assert.True(readiness.HasDynamicTargets);
        Assert.All(authority.Requests, r => Assert.Equal(LanguageAuthorizationPhase.Preflight, r.Phase));
    }

    [Theory]
    [InlineData(LanguageAuthorityStatus.Unavailable, LanguageRunStatus.AuthorizationUnavailable)]
    [InlineData(null, LanguageRunStatus.AuthorizationUnavailable)]
    public async Task PreflightFailsClosedForUnavailableOrThrowingAuthority(LanguageAuthorityStatus? status, LanguageRunStatus expected)
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => status ?? throw new InvalidOperationException("checker failed"));
        var (runtime, doc, invocation) = Create(workspace, authority, "read absent.txt");

        var result = await runtime.PreflightAsync(invocation, doc);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task InvalidWholeDocumentMakesNoAuthorityCalls()
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var compilation = Compile(workspace, "read safe.txt\nunknown-command x");

        Assert.False(compilation.Succeeded);
        Assert.Null(compilation.Document);
        // Invalid source cannot be passed to either runtime entry point; no valid prefix is executable.
        Assert.Empty(authority.Requests);
    }

    [Theory]
    [InlineData("find . *.cs", "Main.cs", true)]
    [InlineData("find . *.cs", "nested\\Other.cs", false)]
    [InlineData("find . **/*.cs", "Main.cs", true)]
    [InlineData("find . **/*.cs", "nested\\Other.cs", true)]
    public async Task FindUsesWorkspaceGlobDepthSemantics(string source, string path, bool included)
    {
        using var workspace = new TestWorkspace();
        workspace.Write("Main.cs", "class Main {}");
        workspace.Write("nested/Other.cs", "class Other {}");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, source);

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var paths = Assert.IsAssignableFrom<IReadOnlyList<LanguageValue>>(result.Statements![0].Values);
        Assert.Equal(included, paths.OfType<FileReferenceValue>().Any(x => string.Equals(x.RelativePath, path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("read present.txt | take 0", LanguageRunStatus.Succeeded)]
    [InlineData("read absent.txt | take 0", LanguageRunStatus.NotFound)]
    public async Task TakeZeroDoesNotEraseProducerRequirementsOrFailures(string source, LanguageRunStatus expected)
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "data");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, source);

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(expected, result.Status);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.Preflight && r.Descriptor == "files.read");
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess);
    }

    [Fact]
    public async Task DeniedReleaseSuppressesAllDerivedStatements()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "secret");
        var authority = new RecordingAuthority(r => r.Phase == LanguageAuthorizationPhase.Release &&
            r.ResourcePath == "present.txt" && r.Action == ResourceAction.ReadFile
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "read present.txt");

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.Release && r.Action == ResourceAction.ReadFile && r.ResourcePath == "present.txt");
    }

    [Fact]
    public async Task LaterStatementFailureDoesNotReturnEarlierStatementResults()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "readable");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "read present.txt\nread absent.txt");

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.NotFound, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task PipelineSearchReadsEachDiscoveredResourceUnderItsOwnCurrentCheck()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("A.cs", "needle here\n");
        workspace.Write("nested/B.cs", "needle there\n");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "find . **/*.cs | grep needle | take 1");

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var fileChecks = authority.Requests.Where(r => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile).ToArray();
        Assert.Contains(fileChecks, r => r.ResourcePath == "A.cs");
        Assert.Contains(fileChecks, r => r.ResourcePath == "nested/B.cs");
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.Preflight && r.Descriptor == "files.search-text" && r.ResourcePath is null);
    }

    [Fact]
    public async Task CountOnlyIncludesResourcesInTheAuthorizedView()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("allowed.txt", "a");
        workspace.Write("hidden.txt", "b");
        var authority = new RecordingAuthority(r =>
            r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.ResourcePath == "hidden.txt"
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "find . *.txt | count");

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var count = Assert.IsType<CountValue>(Assert.Single(result.Statements![0].Values));
        Assert.Equal(1, count.Count);
    }

    [Fact]
    public async Task DeniedReleaseOfNonmatchingCandidateMetadataSuppressesDerivedCount()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("included.txt", "visible");
        workspace.Write("omitted.bin", "not selected");
        var authority = new RecordingAuthority(r =>
            r.Phase == LanguageAuthorizationPhase.Release && r.Action == ResourceAction.ReadMetadata && r.ResourcePath == "omitted.bin"
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "find . *.txt | count");

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Statements);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess &&
            r.Action == ResourceAction.ReadMetadata && r.ResourcePath == "omitted.bin");
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.Release &&
            r.Action == ResourceAction.ReadMetadata && r.ResourcePath == "omitted.bin");
    }

    [Fact]
    public async Task InvalidUnicodeInvocationIdsAreRejectedBeforeAuthority()
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, _) = Create(workspace, authority, "read absent.txt");

        var result = await runtime.ExecuteAsync(new EffectInvocation("\uD800", "effect", "attempt"), doc);

        Assert.Equal(LanguageRunStatus.InvalidDocument, result.Status);
        Assert.Null(result.Statements);
        Assert.Empty(authority.Requests);
    }

    [Fact]
    public async Task MismatchedWorkspaceBindingIsRejectedBeforeAuthorityOrIo()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "data");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var doc = Compile(workspace, "read present.txt").Document!;
        var runtime = new LanguageRuntime(new WorkspaceReference("other-workspace", workspace.Root), authority);

        var result = await runtime.ExecuteAsync(new EffectInvocation("subject", "effect", "attempt"), doc);

        Assert.Equal(LanguageRunStatus.InvalidDocument, result.Status);
        Assert.Empty(authority.Requests);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task CancellationPropagatesAndDoesNotExposeStatements()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "data");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, doc, invocation) = Create(workspace, authority, "read present.txt");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runtime.ExecuteAsync(invocation, doc, cts.Token));
    }

    [Fact]
    public async Task ResourceCallBudgetExhaustionIsLimitExceeded()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "data");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var limits = new LanguageExecutionLimits(MaxResourceCalls: 1);
        var (runtime, doc, invocation) = Create(workspace, authority, "read present.txt", limits);

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess);
    }

    [Fact]
    public async Task PreflightCallsShareTheExecutionResourceCallBudget()
    {
        using var workspace = new TestWorkspace();
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var limits = new LanguageExecutionLimits(MaxResourceCalls: 1);
        var (runtime, doc, invocation) = Create(workspace, authority, "read first.txt\nread second.txt", limits);

        var readiness = await runtime.PreflightAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.LimitExceeded, readiness.Status);
        Assert.Single(authority.Requests);
        Assert.All(authority.Requests, r => Assert.Equal(LanguageAuthorizationPhase.Preflight, r.Phase));
        authority.Requests.Clear();

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
        Assert.Single(authority.Requests);
        Assert.All(authority.Requests, r => Assert.Equal(LanguageAuthorizationPhase.Preflight, r.Phase));
    }

    [Fact]
    public async Task ReadByteBudgetExhaustionIsLimitExceeded()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("present.txt", "0123456789");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var limits = new LanguageExecutionLimits(MaxReadBytes: 4);
        var (runtime, doc, invocation) = Create(workspace, authority, "read present.txt", limits);

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task IntermediateAndFinalOutputBudgetsSuppressResults()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("x", new string('a', 80));
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var intermediateLimits = new LanguageExecutionLimits(MaxIntermediateBytes: 100);
        var (runtime, doc, invocation) = Create(workspace, authority, "read x", intermediateLimits);

        var intermediate = await runtime.ExecuteAsync(invocation, doc);
        Assert.Equal(LanguageRunStatus.LimitExceeded, intermediate.Status);
        Assert.Null(intermediate.Statements);

        workspace.Write("x", "small");
        authority.Requests.Clear();
        var outputLimits = new LanguageExecutionLimits(MaxOutputBytes: 100);
        (runtime, doc, invocation) = Create(workspace, authority, "read x", outputLimits);
        var output = await runtime.ExecuteAsync(invocation, doc);
        Assert.Equal(LanguageRunStatus.LimitExceeded, output.Status);
        Assert.Null(output.Statements);
    }

    [Fact]
    public async Task ValueBudgetCapsDiscoveredResults()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("a.txt", "a");
        workspace.Write("b.txt", "b");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var limits = new LanguageExecutionLimits(MaxValues: 1);
        var (runtime, doc, invocation) = Create(workspace, authority, "read a.txt\nread b.txt", limits);

        var result = await runtime.ExecuteAsync(invocation, doc);

        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
    }

    [Fact]
    public async Task AliasesHaveEquivalentSemanticIdentityAndTrustedNodeIdentity()
    {
        using var workspace = new TestWorkspace();
        var a = Compile(workspace, "read present.txt").Document!;
        var b = Compile(workspace, "cat present.txt").Document!;
        Assert.Equal(a.Identity, b.Identity);
        Assert.Equal(a.Statements[0][0].Identity, b.Statements[0][0].Identity);

        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var (runtime, _, invocation) = Create(workspace, authority, "cat present.txt");
        await runtime.PreflightAsync(invocation, b);
        Assert.All(authority.Requests, r => Assert.Equal(b.Statements[0][0].Identity, r.NodeIdentity));
    }

    private static (LanguageRuntime Runtime, CompiledDocument Document, EffectInvocation Invocation) Create(
        TestWorkspace workspace, RecordingAuthority authority, string source, LanguageExecutionLimits? limits = null)
    {
        var document = Compile(workspace, source, limits).Document ?? throw new Xunit.Sdk.XunitException("Expected source to compile.");
        return (new LanguageRuntime(new WorkspaceReference(workspace.Id.Value, workspace.Root), authority), document,
            new EffectInvocation("subject", "effect", "attempt"));
    }

    private static LanguageCompilation Compile(TestWorkspace workspace, string source, LanguageExecutionLimits? limits = null) =>
        LanguageCompiler.Compile(source, workspace.Id, limits is null ? null : new(ExecutionLimits: limits));

    private sealed class RecordingAuthority(Func<LanguageAuthorizationRequest, LanguageAuthorityStatus> decide) : ILanguageAuthorizer
    {
        public List<LanguageAuthorizationRequest> Requests { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var status = decide(request);
            return ValueTask.FromResult(new LanguageAuthorityDecision(status));
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-language-" + Guid.NewGuid().ToString("N"));
        public WorkspaceId Id { get; } = new("test-workspace");
        public string Root => _root;
        public TestWorkspace() => Directory.CreateDirectory(_root);
        public void Write(string relative, string content)
        {
            var path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test path escaped its temporary workspace.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
