using Penghou.Luban;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class DirectFileEffectRuntimeRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-direct-regression-" + Guid.NewGuid().ToString("N"));
    private static readonly EffectInvocation Invocation = new("subject-A", "effect-A", "attempt-A");

    public DirectFileEffectRuntimeRegressionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Never_completing_admission_times_out_and_fails_closed()
    {
        var auth = new DeferredAuthorizer();
        var runtime = Runtime(auth, TimeSpan.FromMilliseconds(100));
        var result = await runtime.ReadAsync(Invocation, new("exists.txt"));
        Assert.Equal(EffectStatus.DeadlineExceeded, result.Status);
        Assert.Single(auth.Requests);
    }

    [Fact]
    public async Task Find_and_search_admission_waits_share_the_bounded_deadline()
    {
        var findAuth = new DeferredAuthorizer();
        var find = await Runtime(findAuth, TimeSpan.FromMilliseconds(100)).FindAsync(Invocation, new FindRequest());
        Assert.Equal(EffectStatus.DeadlineExceeded, find.Status);

        var searchAuth = new DeferredAuthorizer();
        var search = await Runtime(searchAuth, TimeSpan.FromMilliseconds(100)).SearchTextAsync(Invocation,
            new SearchTextRequest("", "*.txt", "needle"));
        Assert.Equal(EffectStatus.DeadlineExceeded, search.Status);
    }

    [Theory]
    [InlineData(AuthorizationDecision.Permit)]
    [InlineData(AuthorizationDecision.Deny)]
    public async Task Late_authorization_decision_after_caller_cancellation_cannot_continue(AuthorizationDecision decision)
    {
        var auth = new DeferredAuthorizer();
        using var cancellation = new CancellationTokenSource();
        var task = Runtime(auth).ReadAsync(Invocation, new("exists.txt"), cancellation.Token).AsTask();
        await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        auth.Decision.SetResult(decision);
        Assert.Single(auth.Requests);
    }

    [Fact]
    public async Task Invalid_utf8_or_over_256_byte_ids_are_rejected_before_authorization()
    {
        var auth = new DeferredAuthorizer();
        var runtime = Runtime(auth);
        var malformed = await runtime.ReadAsync(new("\uD800", "effect-A", "attempt-A"), new("exists.txt"));
        var tooManyCodeUnits = await runtime.ReadAsync(new(new string('a', 257), "effect-A", "attempt-A"), new("exists.txt"));
        Assert.Equal(EffectStatus.InvalidRequest, malformed.Status);
        Assert.Equal(EffectStatus.InvalidRequest, tooManyCodeUnits.Status);
        Assert.Empty(auth.Requests);
        Assert.Throws<ArgumentException>(() => new FileEffectRuntime(new WorkspaceReference("\uD800", _root), auth));

        var unicodeAuth = new ImmediateAuthorizer();
        var unicode = await Runtime(unicodeAuth).ReadAsync(new(new string('é', 129), "effect-A", "attempt-A"), new("missing.txt"));
        Assert.Equal(EffectStatus.NotFound, unicode.Status);
        Assert.Contains(unicodeAuth.Requests, x => x.Phase == EffectAuthorizationPhase.Admission);
    }

    [Fact]
    public async Task Search_skips_one_oversized_file_and_finds_later_small_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a-large.txt"), "0123456789");
        await File.WriteAllTextAsync(Path.Combine(_root, "z-small.txt"), "needle");
        var result = await Runtime(new ImmediateAuthorizer()).SearchTextAsync(Invocation,
            new SearchTextRequest("", "*.txt", "needle", MaxFileBytes: 8, MaxBytesScanned: 64));
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Truncated);
        Assert.Equal("z-small.txt", Assert.Single(result.Value.Matches).RelativePath);
        Assert.Equal(6, result.Value.BytesScanned);
    }

    [Fact]
    public async Task Search_skips_per_file_limit_even_when_it_exceeds_remaining_aggregate_budget()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a-too-large.txt"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(_root, "z-small.txt"), "needle");
        var result = await Runtime(new ImmediateAuthorizer()).SearchTextAsync(Invocation,
            new SearchTextRequest("", "*.txt", "needle", MaxFileBytes: 10, MaxBytesScanned: 20));
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Truncated);
        Assert.Equal("z-small.txt", Assert.Single(result.Value.Matches).RelativePath);
        Assert.Equal(6, result.Value.BytesScanned);
    }

    [Fact]
    public async Task Search_stops_at_aggregate_budget_without_reading_later_files()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a-consumed.txt"), new string('x', 15));
        await File.WriteAllTextAsync(Path.Combine(_root, "b-over-budget.txt"), new string('y', 10));
        await File.WriteAllTextAsync(Path.Combine(_root, "z-late.txt"), "needle");
        var auth = new PolicyAuthorizer(_ => AuthorizationDecision.Permit);
        var result = await Runtime(auth).SearchTextAsync(Invocation,
            new SearchTextRequest("", "*.txt", "needle", MaxFileBytes: 100, MaxBytesScanned: 20));
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Truncated);
        Assert.Empty(result.Value.Matches);
        Assert.Equal(15, result.Value.BytesScanned);
        Assert.DoesNotContain(auth.Requests, x => x.Phase == EffectAuthorizationPhase.ResourceAccess && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile && x.RelativePath == "z-late.txt");
    }

    [Fact]
    public async Task Direct_resource_authorization_includes_phase_action_and_request_identity()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "read.txt"), "read me");
        var auth = new ImmediateAuthorizer();
        var result = await Runtime(auth).ReadAsync(Invocation, new("read.txt"));
        Assert.True(result.Succeeded);
        var resource = Assert.Single(auth.Requests, x => x.Phase == EffectAuthorizationPhase.ResourceAccess && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile);
        Assert.Equal(Penghou.IO.Abstractions.ResourceAction.ReadFile, resource.Action);
        Assert.NotNull(resource.ResourceRequestIdentity);
        Assert.Contains(auth.Requests, x => x.Phase == EffectAuthorizationPhase.Admission);
        Assert.Contains(auth.Requests, x => x.Phase == EffectAuthorizationPhase.Release && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile);
    }

    [Fact]
    public async Task Release_denial_withholds_read_result()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "read.txt"), "secret");
        var auth = new PolicyAuthorizer(x => x.Phase == EffectAuthorizationPhase.Release && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile
            ? AuthorizationDecision.Deny : AuthorizationDecision.Permit);
        var result = await Runtime(auth).ReadAsync(Invocation, new("read.txt"));
        Assert.Equal(EffectStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(auth.Requests, x => x.Phase == EffectAuthorizationPhase.Release && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile);
    }

    [Fact]
    public async Task Whole_operation_release_can_deny_publication_by_original_selector()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "read.txt"), "secret");
        var auth = new PolicyAuthorizer(x => x.Phase == EffectAuthorizationPhase.Release && x.Action is null
            ? AuthorizationDecision.Deny : AuthorizationDecision.Permit);
        var result = await Runtime(auth).ReadAsync(Invocation, new("read.txt"));
        Assert.Equal(EffectStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(auth.Requests, x => x.Phase == EffectAuthorizationPhase.Release && x.Action is null && x.RelativePath == "read.txt");
    }

    [Fact]
    public async Task Release_denial_withholds_find_result()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "found.txt"), "contents");
        var auth = new PolicyAuthorizer(x => x.Phase == EffectAuthorizationPhase.Release && x.Action == Penghou.IO.Abstractions.ResourceAction.ListDirectory
            ? AuthorizationDecision.Deny : AuthorizationDecision.Permit);
        var result = await Runtime(auth).FindAsync(Invocation, new FindRequest(Pattern: "*.txt"));
        Assert.Equal(EffectStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task No_match_search_still_releases_successfully_read_content()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "read.txt"), "nothing here");
        var auth = new PolicyAuthorizer(x => x.Phase == EffectAuthorizationPhase.Release && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile
            ? AuthorizationDecision.Deny : AuthorizationDecision.Permit);
        var result = await Runtime(auth).SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle"));
        Assert.Equal(EffectStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Value);
        Assert.Contains(auth.Requests, x => x.Phase == EffectAuthorizationPhase.ResourceAccess && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile);
        Assert.Contains(auth.Requests, x => x.Phase == EffectAuthorizationPhase.Release && x.Action == Penghou.IO.Abstractions.ResourceAction.ReadFile);
    }

    [Fact]
    public async Task Release_dependency_cap_fails_closed_without_returning_values()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "read.txt"), "contents");
        var runtime = new FileEffectRuntime(new WorkspaceReference("workspace-A", _root), new ImmediateAuthorizer(),
            new FileEffectRuntimeOptions(TimeSpan.FromSeconds(2), MaxReleaseDependencies: 1));
        var result = await runtime.ReadAsync(Invocation, new("read.txt"));
        Assert.Equal(EffectStatus.AuthorizationUnavailable, result.Status);
        Assert.Null(result.Value);
    }

    private FileEffectRuntime Runtime(IEffectAuthorizer auth, TimeSpan? timeout = null) =>
        new(new WorkspaceReference("workspace-A", _root), auth,
            new FileEffectRuntimeOptions(timeout ?? TimeSpan.FromSeconds(2)));

    private sealed class ImmediateAuthorizer : IEffectAuthorizer
    {
        public List<EffectAuthorizationRequest> Requests { get; } = [];
        public ValueTask<AuthorizationDecision> AuthorizeAsync(EffectAuthorizationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(AuthorizationDecision.Permit);
        }
    }

    private sealed class PolicyAuthorizer(Func<EffectAuthorizationRequest, AuthorizationDecision> decide) : IEffectAuthorizer
    {
        public List<EffectAuthorizationRequest> Requests { get; } = [];
        public ValueTask<AuthorizationDecision> AuthorizeAsync(EffectAuthorizationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(decide(request));
        }
    }

    private sealed class DeferredAuthorizer : IEffectAuthorizer
    {
        public List<EffectAuthorizationRequest> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AuthorizationDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<AuthorizationDecision> AuthorizeAsync(EffectAuthorizationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Started.TrySetResult();
            return new(Decision.Task);
        }
    }
}
