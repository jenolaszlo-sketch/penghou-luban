using System.Security.Cryptography;
using System.Text;
using Penghou.Luban;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class FileEffectRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly EffectInvocation Invocation = new("subject-A", "effect-A", "attempt-1");

    public FileEffectRuntimeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
    private FileEffectRuntime Runtime(IEffectAuthorizer auth) => new(new WorkspaceReference("workspace-A"), TestLocalProvider.Create("workspace-A", _root), auth);

    [Fact]
    public async Task Read_authorizes_exact_request_before_touching_target()
    {
        var auth = new RecordingAuthorizer(_ => AuthorizationDecision.Deny);
        var result = await Runtime(auth).ReadAsync(Invocation, new ReadRequest("missing.txt"));
        Assert.Equal(EffectStatus.AuthorityDenied, result.Status);
        var check = Assert.Single(auth.Checks);
        Assert.Equal(Invocation, check.Invocation);
        Assert.Equal(FileEffectKind.FilesRead, check.EffectKind);
        Assert.Equal("workspace-A", check.WorkspaceId);
        Assert.Equal("missing.txt", check.RelativePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ReadRequest("missing.txt")))), check.RequestDigest);
    }

    [Fact]
    public async Task Read_returns_utf8_content_and_digest_when_authorized()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "hello", new UTF8Encoding(false));
        var result = await Runtime(new RecordingAuthorizer(_ => AuthorizationDecision.Permit)).ReadAsync(Invocation, new ReadRequest("a.txt"));
        Assert.True(result.Succeeded);
        Assert.Equal("hello", result.Value!.Content);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))), result.Value.Sha256);
    }

    [Fact]
    public async Task Find_and_search_omit_each_denied_descendant()
    {
        Directory.CreateDirectory(Path.Combine(_root, "public"));
        Directory.CreateDirectory(Path.Combine(_root, "secret"));
        await File.WriteAllTextAsync(Path.Combine(_root, "public", "a.txt"), "needle visible");
        await File.WriteAllTextAsync(Path.Combine(_root, "secret", "b.txt"), "needle hidden");
        var authorizer = new RecordingAuthorizer(c => c.RelativePath.StartsWith("secret", StringComparison.OrdinalIgnoreCase) ? AuthorizationDecision.Deny : AuthorizationDecision.Permit);
        var runtime = Runtime(authorizer);
        var find = await runtime.FindAsync(Invocation, new FindRequest(Pattern: "*.txt"));
        var search = await runtime.SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle"));
        Assert.True(find.Succeeded);
        Assert.Equal(new[] { "public/a.txt" }, find.Value!.Paths);
        Assert.True(search.Succeeded);
        Assert.Single(search.Value!.Matches);
        Assert.Equal("public/a.txt", search.Value.Matches[0].RelativePath);
        Assert.Contains(authorizer.Checks, c => c.RelativePath == "secret");
        Assert.DoesNotContain(authorizer.Checks, c => c.RelativePath == "secret/b.txt");
    }

    [Fact]
    public async Task Find_and_search_omit_a_denied_file_but_keep_its_allowed_sibling()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "allowed.txt"), "needle allowed");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "excluded.txt"), "needle excluded");
        var authorizer = new RecordingAuthorizer(c => c.RelativePath == "docs/excluded.txt" ? AuthorizationDecision.Deny : AuthorizationDecision.Permit);
        var runtime = Runtime(authorizer);
        var find = await runtime.FindAsync(Invocation, new FindRequest(Pattern: "*.txt"));
        var search = await runtime.SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle"));
        Assert.True(find.Succeeded);
        Assert.Equal(new[] { "docs/allowed.txt" }, find.Value!.Paths);
        Assert.True(search.Succeeded);
        Assert.Single(search.Value!.Matches);
        Assert.Equal("docs/allowed.txt", search.Value.Matches[0].RelativePath);
        Assert.Contains(authorizer.Checks, c => c.RelativePath == "docs/excluded.txt");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("folder//x")]
    public async Task Rejects_traversal_and_non_relative_forms_before_authorization(string path)
    {
        var auth = new RecordingAuthorizer(_ => AuthorizationDecision.Permit);
        var result = await Runtime(auth).ReadAsync(Invocation, new ReadRequest(path));
        Assert.Equal(EffectStatus.InvalidPath, result.Status);
        Assert.Empty(auth.Checks);
    }

    [Fact]
    public async Task Applies_result_and_entry_limits()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "b");
        var result = await Runtime(new RecordingAuthorizer(_ => AuthorizationDecision.Permit)).FindAsync(Invocation, new FindRequest(Pattern: "*.txt", MaxMatches: 1));
        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Paths);
        Assert.True(result.Value.Truncated);
        var entries = await Runtime(new RecordingAuthorizer(_ => AuthorizationDecision.Permit)).FindAsync(Invocation, new FindRequest(MaxEntries: 1));
        Assert.True(entries.Succeeded);
        Assert.True(entries.Value!.Truncated);
    }

    [Fact]
    public async Task Search_marks_oversized_file_as_truncated_without_reading_it()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), "needle-content");
        var result = await Runtime(new RecordingAuthorizer(_ => AuthorizationDecision.Permit)).SearchTextAsync(Invocation, new SearchTextRequest("", "*.txt", "needle", MaxFileBytes: 4));
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Truncated);
        Assert.Empty(result.Value.Matches);
        Assert.Equal(0, result.Value.BytesScanned);
    }

    [Fact]
    public async Task Rejects_a_reparse_point_when_supported()
    {
        var outside = Path.Combine(Path.GetTempPath(), "luban-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            var auth = new RecordingAuthorizer(_ => AuthorizationDecision.Permit);
            var result = await Runtime(auth).FindAsync(Invocation, new FindRequest());
            Assert.True(result.Succeeded);
            Assert.DoesNotContain(result.Value!.Paths, p => p.StartsWith("link/", StringComparison.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(outside, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Authorization_failure_is_fail_closed()
    {
        var auth = new RecordingAuthorizer(_ => throw new InvalidOperationException("authority unavailable"));
        var result = await Runtime(auth).ReadAsync(Invocation, new ReadRequest("x.txt"));
        Assert.Equal(EffectStatus.AuthorizationUnavailable, result.Status);
    }

    private sealed class RecordingAuthorizer(Func<EffectAuthorizationRequest, AuthorizationDecision> decide) : IEffectAuthorizer
    {
        public List<EffectAuthorizationRequest> Checks { get; } = [];
        public ValueTask<AuthorizationDecision> AuthorizeAsync(EffectAuthorizationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks.Add(request);
            return ValueTask.FromResult(decide(request));
        }
    }
}
