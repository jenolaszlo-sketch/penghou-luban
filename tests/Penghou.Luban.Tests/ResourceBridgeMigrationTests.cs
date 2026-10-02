using System.Security.Cryptography;
using System.Text.Json;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class ResourceBridgeMigrationTests : IDisposable
{
    private const string TempPrefix = "luban-bridge-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
    private static readonly EffectInvocation Invocation = new("subject", "effect", "attempt");

    public ResourceBridgeMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        var fullRoot = Path.GetFullPath(_root);
        var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(fullRoot);
        if (!StringComparer.OrdinalIgnoreCase.Equals(parent, expectedParent) ||
            !name.StartsWith(TempPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[TempPrefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing to remove a path outside the expected Luban test temp directory.");

        if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, recursive: true);
    }

    [Fact]
    public async Task Find_uses_one_semantic_digest_for_each_provider_target_and_never_file_read_right()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        await File.WriteAllTextAsync(Path.Combine(_root, "d", "a.txt"), "x");
        var auth = new Capture(_ => AuthorizationDecision.Permit);
        var req = new FindRequest(Pattern: "*.txt");

        var result = await Runtime(auth).FindAsync(Invocation, req);

        Assert.True(result.Succeeded);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(req)));
        Assert.All(auth.Requests, item => Assert.Equal(digest, item.RequestDigest));
        Assert.Contains(auth.Requests, item => item.RelativePath == "d/a.txt");
        Assert.DoesNotContain(auth.Requests, item => item.RelativePath == "d/a.txt" && item.EffectKind != FileEffectKind.FilesFind);
    }

    [Fact]
    public async Task Find_and_search_recurse_into_two_directories_with_a_shared_work_budget()
    {
        Directory.CreateDirectory(Path.Combine(_root, "one"));
        Directory.CreateDirectory(Path.Combine(_root, "two"));
        await File.WriteAllTextAsync(Path.Combine(_root, "one", "a.txt"), "needle one");
        await File.WriteAllTextAsync(Path.Combine(_root, "two", "b.txt"), "needle two");
        var findAuth = new Capture(_ => AuthorizationDecision.Permit);
        var searchAuth = new Capture(_ => AuthorizationDecision.Permit);

        var find = await Runtime(findAuth).FindAsync(Invocation, new FindRequest(Pattern: "*.txt", MaxEntries: 10));
        var search = await Runtime(searchAuth).SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle", MaxEntries: 10));

        Assert.True(find.Succeeded);
        Assert.False(find.Value!.Truncated);
        Assert.Equal(new[] { "one/a.txt", "two/b.txt" }, find.Value.Paths.OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.True(search.Succeeded);
        Assert.False(search.Value!.Truncated);
        Assert.Equal(new[] { "one/a.txt", "two/b.txt" }, search.Value.Matches.Select(match => match.RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Find_with_denied_candidates_stops_at_the_aggregate_candidate_limit()
    {
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(Path.Combine(_root, $"denied-{i:D2}.txt"), "hidden");
        var auth = new Capture(request => request.RelativePath.Length == 0 ? AuthorizationDecision.Permit : AuthorizationDecision.Deny);

        var result = await Runtime(auth).FindAsync(Invocation, new FindRequest(Pattern: "*.txt", MaxEntries: 2));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Paths);
        Assert.True(result.Value.Truncated);
        Assert.Equal(2, auth.Requests.Count(request => request.RelativePath.Length != 0));
        Assert.InRange(auth.Requests.Count, 1, 7);
    }

    [Fact]
    public async Task Read_reports_authorization_unavailable_when_resource_authorizer_returns_unknown_decision()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "content");
        var auth = new Capture((_, call) => call == 1 ? AuthorizationDecision.Permit : (AuthorizationDecision)73);

        var result = await Runtime(auth).ReadAsync(Invocation, new ReadRequest("a.txt"));

        Assert.Equal(EffectStatus.AuthorizationUnavailable, result.Status);
        Assert.Null(result.Value);
        Assert.True(auth.Requests.Count >= 2);
    }

    [Fact]
    public async Task Search_does_not_release_content_when_file_read_recheck_is_denied()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "needle must remain hidden");
        var auth = new Capture(request =>
            request.Phase == EffectAuthorizationPhase.ResourceAccess &&
            request.Action == ResourceAction.ReadFile &&
            request.RelativePath == "a.txt"
                ? AuthorizationDecision.Deny
                : AuthorizationDecision.Permit);

        var result = await Runtime(auth).SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle"));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Matches);
        Assert.Contains(auth.Requests, request => request.Phase == EffectAuthorizationPhase.ResourceAccess && request.Action == ResourceAction.ReadFile && request.RelativePath == "a.txt");
        Assert.DoesNotContain(auth.Requests, request => request.Phase == EffectAuthorizationPhase.Release && request.Action == ResourceAction.ReadFile && request.RelativePath == "a.txt");
    }

    [Fact]
    public async Task Search_aborts_without_a_partial_result_when_authorization_becomes_unavailable_after_listing()
    {
        foreach (var directory in new[] { "alpha", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(_root, directory));
            await File.WriteAllTextAsync(Path.Combine(_root, directory, "match.txt"), $"needle in {directory}");
        }
        var rootDirectoryOrder = new List<string>();
        var fileChecks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var auth = new Capture((request, _) =>
        {
            if ((request.RelativePath is "alpha" or "beta") && !rootDirectoryOrder.Contains(request.RelativePath, StringComparer.OrdinalIgnoreCase))
                rootDirectoryOrder.Add(request.RelativePath);
            if (request.RelativePath.EndsWith("/match.txt", StringComparison.OrdinalIgnoreCase))
            {
                fileChecks.TryGetValue(request.RelativePath, out var count);
                fileChecks[request.RelativePath] = ++count;
                if (count >= 2 && !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(request.RelativePath)?.Replace('\\', '/'), rootDirectoryOrder.Last()))
                    throw new InvalidOperationException("authority unavailable");
            }
            return AuthorizationDecision.Permit;
        });

        var result = await Runtime(auth).SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle"));

        Assert.Equal(EffectStatus.AuthorizationUnavailable, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(2, rootDirectoryOrder.Count);
        Assert.Contains(fileChecks.Values, count => count >= 2);
    }

    [Fact]
    public async Task Search_honors_large_file_metadata_before_read()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "big.txt"), "needle and excess");
        var auth = new Capture(_ => AuthorizationDecision.Permit);
        var req = new SearchTextRequest("", "*.txt", "needle", MaxFileBytes: 4);

        var result = await Runtime(auth).SearchTextAsync(Invocation, req);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Truncated);
        Assert.Empty(result.Value.Matches);
        Assert.Equal(0, result.Value.BytesScanned);
        Assert.Contains(auth.Requests, request => request.RelativePath == "big.txt");
    }

    private FileEffectRuntime Runtime(IEffectAuthorizer authorizer) => new(new("workspace"), TestLocalProvider.Create("workspace", _root), authorizer);

    private sealed class Capture : IEffectAuthorizer
    {
        private readonly Func<EffectAuthorizationRequest, int, AuthorizationDecision> _decide;

        public Capture(Func<EffectAuthorizationRequest, AuthorizationDecision> decide)
            : this((request, _) => decide(request)) { }

        public Capture(Func<EffectAuthorizationRequest, int, AuthorizationDecision> decide) => _decide = decide;

        public List<EffectAuthorizationRequest> Requests { get; } = [];

        public ValueTask<AuthorizationDecision> AuthorizeAsync(EffectAuthorizationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(_decide(request, Requests.Count));
        }
    }
}
