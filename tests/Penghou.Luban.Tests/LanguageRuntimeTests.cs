using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Changes;
using System.Text;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Language.Tests;

public sealed class LanguageRuntimeTests
{
    private static readonly LanguageVersions TextChangeVersions = new(LanguageProfile.TextChangeLanguageVersion,
        LanguageProfile.TextChangeIrVersion, LanguageProfile.TextChangeCatalogueVersion,
        LanguageProfile.TextChangeProviderProfile);

    [Fact]
    public async Task V2DiffIsReadOnlyTypedAndBindsEveryTargetAdmissionBeforeProviderReads()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("before.txt", "one\nold\nthree\n");
        workspace.Write("after.txt", "one\nnew\nthree\n");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var document = LanguageCompiler.Compile("#!luban2\ndiff before.txt after.txt", workspace.Id,
            new LanguageCompilerOptions(Versions: TextChangeVersions)).Document!;
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), authority);

        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), document);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var diff = Assert.IsType<DiffValue>(Assert.Single(Assert.Single(result.Statements!).Values));
        Assert.True(diff.IsUntrustedCandidate);
        Assert.Equal("before.txt", diff.BeforePath);
        Assert.Equal("after.txt", diff.AfterPath);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.ResourcePath == "before.txt" && r.ResourceRequestIdentity is null);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.ResourcePath == "after.txt" && r.ResourceRequestIdentity is null);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.ResourcePath == "before.txt" && r.ResourceRequestIdentity is not null);
        Assert.Contains(authority.Requests, r => r.Phase == LanguageAuthorizationPhase.Release);
        Assert.Equal("one\nold\nthree\n", File.ReadAllText(Path.Combine(workspace.Root, "before.txt")));
        Assert.Equal("one\nnew\nthree\n", File.ReadAllText(Path.Combine(workspace.Root, "after.txt")));
    }

    [Fact]
    public async Task V2TargetDenialStopsBeforeReadsAndReleaseDenialSuppressesConflictDetails()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("base.txt", "start\nbase\nend\n");
        workspace.Write("ours.txt", "start\nours\nend\n");
        workspace.Write("theirs.txt", "start\ntheirs\nend\n");
        var deniedTarget = new RecordingAuthority(r => r.Phase == LanguageAuthorizationPhase.ResourceAccess &&
            r.ResourcePath == "theirs.txt" && r.ResourceRequestIdentity is null ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var merge = LanguageCompiler.Compile("merge base.txt ours.txt theirs.txt", workspace.Id,
            new LanguageCompilerOptions(Versions: TextChangeVersions)).Document!;
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), deniedTarget);
        var denied = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), merge);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, denied.Status);
        Assert.Null(denied.Statements);
        Assert.DoesNotContain(deniedTarget.Requests, r => r.Phase == LanguageAuthorizationPhase.ResourceAccess && r.ResourceRequestIdentity is not null);

        var permit = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var permitRuntime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), permit);
        var conflicted = await permitRuntime.ExecuteAsync(new("subject", "effect", "attempt"), merge);
        Assert.Equal(LanguageRunStatus.Succeeded, conflicted.Status);
        var conflictValue = Assert.IsType<MergeConflictValue>(Assert.Single(Assert.Single(conflicted.Statements!).Values));
        Assert.True(conflictValue.IsUntrustedCandidate);
        Assert.Single(conflictValue.Conflicts);

        var revokeAtRelease = new RecordingAuthority(r => r.Phase == LanguageAuthorizationPhase.Release
            ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var revokeRuntime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), revokeAtRelease);
        var revoked = await revokeRuntime.ExecuteAsync(new("subject", "effect", "attempt"), merge);
        Assert.Equal(LanguageRunStatus.AuthorityDenied, revoked.Status);
        Assert.Null(revoked.Statements);
    }

    [Fact]
    public async Task V2ReadWindowAndSearchContextKeepCompleteInputAndUtf8Offsets()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("unicode.txt", "first\r\n😀 needle end\r\nlast");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var options = new LanguageCompilerOptions(Versions: TextChangeVersions);
        var compiled = LanguageCompiler.Compile("read unicode.txt --start-line 2 --line-count 1\nsearch needle . --include unicode.txt --context-lines 1",
            workspace.Id, options);
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Diagnostics.Select(d => d.Code)));
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), authority);

        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), compiled.Document!);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var window = Assert.IsType<FileWindowValue>(Assert.Single(result.Statements![0].Values));
        Assert.Equal("😀 needle end\r\n", window.Content);
        Assert.True(window.CompleteInput);
        Assert.True(window.CompleteWindow);
        var match = Assert.IsType<SearchContextMatchValue>(Assert.Single(result.Statements[1].Values));
        Assert.Equal(2, match.LineNumber);
        Assert.Equal(12, match.MatchStartByte); // first\r\n (7 bytes), then emoji and a space (5 bytes)
        Assert.Equal(6, match.MatchLengthBytes);
        Assert.Equal(new[] { "first" }, match.BeforeContext);
        Assert.Equal(new[] { "last" }, match.AfterContext);
    }

    [Fact]
    public async Task V2SearchSpanIsAbsoluteAfterBomAndManyCrlfLinesWithNoContextOption()
    {
        using var workspace = new TestWorkspace();
        var text = string.Concat(Enumerable.Repeat("row\r\n", 2_000)) + "😀 needle\r\n";
        var path = Path.Combine(workspace.Root, "large.txt");
        var content = new UTF8Encoding(false, true).GetBytes(text);
        var preamble = new UTF8Encoding(true).GetPreamble();
        File.WriteAllBytes(path, preamble.Concat(content).ToArray());
        var document = LanguageCompiler.Compile("search needle . --include large.txt", workspace.Id,
            new LanguageCompilerOptions(Versions: TextChangeVersions)).Document!;
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root),
            new RecordingAuthority(_ => LanguageAuthorityStatus.Permit));

        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), document);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var match = Assert.IsType<SearchContextMatchValue>(Assert.Single(Assert.Single(result.Statements!).Values));
        Assert.Equal(2_001, match.LineNumber);
        Assert.Equal(10_008, match.MatchStartByte); // BOM + 2,000 CRLF records + emoji and its following space.
        Assert.Equal(6, match.MatchLengthBytes);
        Assert.Empty(match.BeforeContext);
        Assert.Empty(match.AfterContext);
        Assert.True(match.CompleteInput);
        Assert.True(match.CompleteWindow);
    }

    [Fact]
    public async Task V2ReadWindowMarksEofClippingAndFinalNewlineLineTokensExplicitly()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("ending.txt", "a\n");
        workspace.Write("empty.txt", string.Empty);
        var document = LanguageCompiler.Compile(
            "read ending.txt --start-line 2 --line-count 1\nread empty.txt --start-line 2 --line-count 1\nread empty.txt --start-line 1 --line-count 1",
            workspace.Id, new LanguageCompilerOptions(Versions: TextChangeVersions)).Document!;
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root),
            new RecordingAuthority(_ => LanguageAuthorityStatus.Permit));

        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), document);

        Assert.Equal(LanguageRunStatus.Succeeded, result.Status);
        var windows = result.Statements!.Select(s => Assert.IsType<FileWindowValue>(Assert.Single(s.Values))).ToArray();
        Assert.Equal(1, windows[0].ReturnedLineCount); // LF adds a final empty line token.
        Assert.True(windows[0].CompleteInput);
        Assert.True(windows[0].CompleteWindow);
        Assert.Equal(0, windows[1].ReturnedLineCount); // Empty input has one line token at line 1.
        Assert.True(windows[1].CompleteInput);
        Assert.False(windows[1].CompleteWindow);
        Assert.Equal(1, windows[2].ReturnedLineCount);
        Assert.True(windows[2].CompleteWindow);
    }

    [Fact]
    public async Task V2OperationsShareConservativeWholeDocumentReadBudget()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("a.txt", "a"); workspace.Write("b.txt", "b");
        workspace.Write("c.txt", "c"); workspace.Write("d.txt", "d");
        var authority = new RecordingAuthority(_ => LanguageAuthorityStatus.Permit);
        var document = LanguageCompiler.Compile("diff a.txt b.txt\ndiff c.txt d.txt", workspace.Id,
            new LanguageCompilerOptions(Versions: TextChangeVersions,
                ExecutionLimits: new LanguageExecutionLimits(MaxReadBytes: 4))).Document!;
        var runtime = new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), authority);

        var result = await runtime.ExecuteAsync(new("subject", "effect", "attempt"), document);

        Assert.Equal(LanguageRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Statements);
        var providerChecks = authority.Requests.Where(r => r.Phase == LanguageAuthorizationPhase.ResourceAccess &&
            r.Action == ResourceAction.ReadFile && r.ResourceRequestIdentity is not null).ToArray();
        Assert.Equal(2, providerChecks.Length);
        Assert.All(providerChecks, r => Assert.Contains(r.ResourcePath, new[] { "a.txt", "b.txt" }));
    }

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
        var runtime = new LanguageRuntime(new WorkspaceReference("other-workspace"), TestLocalProvider.Create("other-workspace", workspace.Root), authority);

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
        return (new LanguageRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), authority), document,
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
