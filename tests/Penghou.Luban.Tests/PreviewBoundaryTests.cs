using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class PreviewBoundaryTests
{
    [Fact]
    public async Task DirectoryDigestCannotReleaseRevokedNonmatchingMetadata()
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "a.cs"), "a");
        File.WriteAllText(Path.Combine(workspace.Root, "other.txt"), "b");
        var authority = new Authority(r => r.Phase == PreviewAuthorizationPhase.Release &&
            r.Action == ResourceAction.ReadMetadata && r.ResourcePath == "other.txt" ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var document = Compile([new GlobPatchStage(".", "*.cs", PreviewSelection.AuthorizedView, Patches(), new())]);
        var result = await new PreviewRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), authority).WhatIfAsync(Invocation(), document);
        Assert.Equal(PreviewRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.Release && r.ResourcePath == "other.txt");
    }

    [Fact]
    public async Task PrivateMetadataRetentionLimitInsideProviderRemainsLimitExceeded()
    {
        using var workspace = new Workspace();
        for (var i = 0; i < 12; i++) File.WriteAllText(Path.Combine(workspace.Root, i + ".txt"), "a");
        var document = Compile([new GlobPatchStage(".", "*.cs", PreviewSelection.AuthorizedView,
            Patches(), new(MaxEntries: 100, MaxMatches: 10, MaxOutputBytes: 1000))], new(MaxPlanBytes: 1500));
        var result = await new PreviewRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), new Authority(_ => LanguageAuthorityStatus.Permit)).WhatIfAsync(Invocation(), document);
        Assert.Equal(PreviewRunStatus.LimitExceeded, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task LaterManifestDenialBlocksEarlierExactFileContentRead()
    {
        using var workspace = new Workspace();
        File.WriteAllText(Path.Combine(workspace.Root, "a.cs"), "a");
        File.WriteAllText(Path.Combine(workspace.Root, "b.txt"), "b");
        var authority = new Authority(r => r.Phase == PreviewAuthorizationPhase.TargetAdmission && r.ResourcePath == "b.txt"
            ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit);
        var document = Compile([new FilePatchStage("a.cs", Patches()),
            new GlobPatchStage(".", "*.txt", PreviewSelection.AuthorizedView, Patches(), new(MaxMatches: 999))]);
        var result = await new PreviewRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), authority).WhatIfAsync(Invocation(), document);
        Assert.Equal(PreviewRunStatus.AuthorityDenied, result.Status);
        Assert.Null(result.Plan);
        Assert.DoesNotContain(authority.Requests, r => r.Phase == PreviewAuthorizationPhase.ResourceAccess && r.Action == ResourceAction.ReadFile);
    }

    [Fact]
    public async Task InvalidUnicodeInvocationFailsBeforeAuthorityOrIo()
    {
        using var workspace = new Workspace();
        var authority = new Authority(_ => LanguageAuthorityStatus.Permit);
        var document = Compile([new FilePatchStage("missing", Patches())]);
        var result = await new PreviewRuntime(new("workspace"), TestLocalProvider.Create("workspace", workspace.Root), authority).WhatIfAsync(new("\uD800", "effect", "attempt"), document);
        Assert.Equal(PreviewRunStatus.InvalidDocument, result.Status);
        Assert.Empty(authority.Requests);
    }

    private static TextPatch[] Patches() => [new(0, 0, Encoding.UTF8.GetBytes("x"))];
    private static EffectInvocation Invocation() => new("subject", "effect", "attempt");
    private static CompiledPreviewDocument Compile(PreviewStage[] stages, PreviewLimits? limits = null)
    {
        var compilation = PreviewCompiler.Compile(stages, new("workspace"), limits);
        Assert.True(compilation.Succeeded, string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
        return compilation.Document!;
    }
    private sealed class Authority(Func<PreviewAuthorizationRequest, LanguageAuthorityStatus> decide) : IPreviewAuthorizer
    {
        internal List<PreviewAuthorizationRequest> Requests { get; } = [];
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return ValueTask.FromResult(new LanguageAuthorityDecision(decide(request))); }
    }
    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "luban-preview-boundary-" + Guid.NewGuid().ToString("N"));
        internal Workspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected cleanup path.");
            Directory.Delete(target, recursive: true);
        }
    }
}
