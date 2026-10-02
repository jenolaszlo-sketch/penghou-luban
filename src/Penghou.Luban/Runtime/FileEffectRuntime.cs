using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Enumeration;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;

namespace Penghou.Luban;

public enum FileEffectKind { FilesRead, FilesFind, FilesSearchText }
public enum AuthorizationDecision { Permit, Deny }
public enum EffectStatus { Succeeded, NotFound, AuthorityDenied, AuthorizationUnavailable, InvalidPath, OutsideWorkspace, UnsupportedPlatform, InvalidRequest, LimitExceeded, AccessDenied, UnsupportedProfile, ProviderFailure, DeadlineExceeded }
public sealed record WorkspaceReference(string Id, string RootPath) { public string FullRootPath { get; } = Path.GetFullPath(RootPath ?? throw new ArgumentNullException(nameof(RootPath))); }
public sealed record EffectInvocation(string SubjectId, string EffectId, string AttemptId);
public sealed record FileEffectRuntimeOptions(TimeSpan OperationTimeout, int MaxReleaseDependencies = 100_000)
{
    public static FileEffectRuntimeOptions Default { get; } = new(TimeSpan.FromSeconds(30));
}
public enum EffectAuthorizationPhase { Admission, ResourceAccess, Release }
public sealed record EffectAuthorizationRequest(EffectInvocation Invocation, FileEffectKind EffectKind, string RequestDigest, string WorkspaceId, string RelativePath, EffectAuthorizationPhase Phase = EffectAuthorizationPhase.Admission, ResourceAction? Action = null, RequestIdentity? ResourceRequestIdentity = null);
public interface IEffectAuthorizer { ValueTask<AuthorizationDecision> AuthorizeAsync(EffectAuthorizationRequest request, CancellationToken cancellationToken); }
public sealed record ReadRequest(string RelativePath, int MaxBytes = 1_048_576);
public sealed record ReadResult(string RelativePath, string Content, int ByteLength, string Sha256);
public sealed record FindRequest(string Directory = "", string Pattern = "*", int MaxDepth = 16, int MaxEntries = 10_000, int MaxMatches = 1_000, int MaxOutputBytes = 262_144);
public sealed record FindResult(IReadOnlyList<string> Paths, bool Truncated);
public sealed record SearchTextRequest(string Directory, string Pattern, string Text, int MaxDepth = 16, int MaxEntries = 10_000, int MaxFileBytes = 1_048_576, int MaxBytesScanned = 10_485_760, int MaxMatches = 1_000, int MaxOutputBytes = 262_144);
public sealed record TextMatch(string RelativePath, int LineNumber, string Line);
public sealed record SearchTextResult(IReadOnlyList<TextMatch> Matches, bool Truncated, int BytesScanned);
public sealed record EffectResult<T>(EffectStatus Status, T? Value = default, string? Detail = null) { public bool Succeeded => Status == EffectStatus.Succeeded; }

/// <summary>Legacy Read/Find/SearchText API implemented through the shared local reader.</summary>
public sealed class FileEffectRuntime
{
    private const int MaxRequestBytes = 64 * 1024, MaxFileBytes = 16 * 1024 * 1024, MaxReleaseDependencies = 100_000;
    private readonly WorkspaceReference _workspace;
    private readonly IEffectAuthorizer _authorizer;
    private readonly TimeSpan _operationTimeout;
    private readonly int _maxReleaseDependencies;
    public FileEffectRuntime(WorkspaceReference workspace, IEffectAuthorizer authorizer, FileEffectRuntimeOptions? options = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        options ??= FileEffectRuntimeOptions.Default;
        if (options.OperationTimeout <= TimeSpan.Zero || options.OperationTimeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(options), "Operation timeout must be positive and no greater than 30 seconds.");
        if (options.MaxReleaseDependencies is < 1 or > MaxReleaseDependencies) throw new ArgumentOutOfRangeException(nameof(options), "Release dependency limit must be between 1 and 100,000.");
        _operationTimeout = options.OperationTimeout;
        _maxReleaseDependencies = options.MaxReleaseDependencies;
        if (!ValidId(workspace.Id)) throw new ArgumentException("A bounded workspace ID is required.", nameof(workspace));
    }

    public async ValueTask<EffectResult<ReadResult>> ReadAsync(EffectInvocation invocation, ReadRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = new OperationScope(cancellationToken, _operationTimeout);
        var operationToken = operation.Token;
        if (!OperatingSystem.IsWindows()) return Fail<ReadResult>(EffectStatus.UnsupportedPlatform);
        if (!ValidInvocation(invocation) || request is null || request.RelativePath is null || request.RelativePath.Length > 2048 || request.MaxBytes is < 1 or > MaxFileBytes || !RequestWithinBound(request)) return Fail<ReadResult>(EffectStatus.InvalidRequest);
        var path = Normalize(request.RelativePath, false); if (path.Error is not null) return Fail<ReadResult>(path.Error.Value);
        var digest = Digest(request);
        EffectStatus? admitted;
        try { admitted = await Admit(invocation, FileEffectKind.FilesRead, digest, path.Value!, operationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (operation.IsDeadlineExceeded) { return Fail<ReadResult>(EffectStatus.DeadlineExceeded); }
        if (admitted is not null) return Fail<ReadResult>(admitted.Value);
        var child = Child(invocation);
        var bridge = new ResourceBridge(_authorizer, invocation, FileEffectKind.FilesRead, digest, _workspace.Id, child, ResourceMode.Read, _maxReleaseDependencies, path.Value!);
        try
        {
            using var reader = Reader(bridge);
            var req = Bind(new FileReadRequest(child, new(_workspace.Id), new(path.Value!), new(request.MaxBytes)), bridge);
            var result = await reader.ReadFileAsync(req, operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            if (!result.Succeeded) return Fail<ReadResult>(Map(result.Failure));
            var release = await bridge.ReleaseAsync(operationToken).ConfigureAwait(false);
            if (release is not null) return Fail<ReadResult>(release.Value);
            operationToken.ThrowIfCancellationRequested();
            var bytes = result.Value!.Content.ToArray();
            try { var text = new UTF8Encoding(false, true).GetString(bytes); return new(EffectStatus.Succeeded, new(path.Value!, text, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))); }
            catch (DecoderFallbackException) { return Fail<ReadResult>(EffectStatus.InvalidRequest, "Only valid UTF-8 text is supported."); }
        }
        catch (OperationCanceledException) when (operation.IsDeadlineExceeded) { return Fail<ReadResult>(EffectStatus.DeadlineExceeded); }
    }

    public async ValueTask<EffectResult<FindResult>> FindAsync(EffectInvocation invocation, FindRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = new OperationScope(cancellationToken, _operationTimeout);
        var operationToken = operation.Token;
        if (!OperatingSystem.IsWindows()) return Fail<FindResult>(EffectStatus.UnsupportedPlatform);
        if (!ValidInvocation(invocation) || request is null || request.Directory is null || request.Directory.Length > 2048 || !ValidLimits(request.MaxDepth, request.MaxEntries, request.MaxMatches, request.MaxOutputBytes) || !ValidPattern(request.Pattern) || !RequestWithinBound(request)) return Fail<FindResult>(EffectStatus.InvalidRequest);
        var root = Normalize(request.Directory, true); if (root.Error is not null) return Fail<FindResult>(root.Error.Value);
        var digest = Digest(request); EffectStatus? admitted;
        try { admitted = await Admit(invocation, FileEffectKind.FilesFind, digest, root.Value!, operationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (operation.IsDeadlineExceeded) { return Fail<FindResult>(EffectStatus.DeadlineExceeded); }
        if (admitted is not null) return Fail<FindResult>(admitted.Value);
        var child = Child(invocation); var bridge = new ResourceBridge(_authorizer, invocation, FileEffectKind.FilesFind, digest, _workspace.Id, child, ResourceMode.Find, _maxReleaseDependencies, root.Value!); using var reader = Reader(bridge, request.MaxEntries);
        var paths = new List<string>(); var output = 0; var truncated = false;
        var stack = new Stack<(string Path, int Depth)>(); stack.Push((root.Value!, 0));
        try
        {
            while (stack.Count > 0)
            {
                operationToken.ThrowIfCancellationRequested(); var (dir, depth) = stack.Pop();

                var list = Bind(new DirectoryListRequest(child, new(_workspace.Id), new(dir), 100_000, 100_000, 4 * 1024 * 1024), bridge);
                var pageResult = await reader.ListDirectoryAsync(list, operationToken).ConfigureAwait(false);

                if (!pageResult.Succeeded)
                {
                    if (pageResult.Failure == ResourceFailureKind.TooLarge) { truncated = true; break; }
                    if (!StringComparer.OrdinalIgnoreCase.Equals(dir, root.Value) && pageResult.Failure is ResourceFailureKind.AuthorizationDenied or ResourceFailureKind.NotFound) continue;
                    return Fail<FindResult>(Map(pageResult.Failure));
                }
                var page = pageResult.Value!; if (!page.IsComplete) truncated = true;
                foreach (var entry in page.Entries)
                {
                    operationToken.ThrowIfCancellationRequested(); var relative = Join(dir, entry.Name);
                    if (entry.IsDirectory) { if (depth < request.MaxDepth) stack.Push((relative, depth + 1)); else truncated = true; continue; }
                    if (!FileSystemName.MatchesSimpleExpression(request.Pattern, entry.Name, ignoreCase: true)) continue;
                    var cost = Encoding.UTF8.GetByteCount(relative) + 1;
                    if (paths.Count >= request.MaxMatches || cost > request.MaxOutputBytes - output) { truncated = true; break; }
                    paths.Add(relative); output += cost;
                }
                if (truncated) break;
            }
            var release = await bridge.ReleaseAsync(operationToken).ConfigureAwait(false);
            if (release is not null) return Fail<FindResult>(release.Value);
            operationToken.ThrowIfCancellationRequested();
            return new(EffectStatus.Succeeded, new(paths, truncated));
        }
        catch (OperationCanceledException) when (operation.IsDeadlineExceeded) { return Fail<FindResult>(EffectStatus.DeadlineExceeded); }
        catch (OperationCanceledException) { throw; }
    }

    public async ValueTask<EffectResult<SearchTextResult>> SearchTextAsync(EffectInvocation invocation, SearchTextRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = new OperationScope(cancellationToken, _operationTimeout);
        var operationToken = operation.Token;
        if (!OperatingSystem.IsWindows()) return Fail<SearchTextResult>(EffectStatus.UnsupportedPlatform);
        if (!ValidInvocation(invocation) || request is null || request.Directory is null || request.Directory.Length > 2048 || string.IsNullOrEmpty(request.Text) || request.Text.Length > 4096 || !ValidLimits(request.MaxDepth, request.MaxEntries, request.MaxMatches, request.MaxOutputBytes) || request.MaxFileBytes is < 1 or > MaxFileBytes || request.MaxBytesScanned is < 1 or > 100_000_000 || !ValidPattern(request.Pattern) || !RequestWithinBound(request)) return Fail<SearchTextResult>(EffectStatus.InvalidRequest);
        var root = Normalize(request.Directory, true); if (root.Error is not null) return Fail<SearchTextResult>(root.Error.Value);
        var digest = Digest(request); EffectStatus? admitted;
        try { admitted = await Admit(invocation, FileEffectKind.FilesSearchText, digest, root.Value!, operationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (operation.IsDeadlineExceeded) { return Fail<SearchTextResult>(EffectStatus.DeadlineExceeded); }
        if (admitted is not null) return Fail<SearchTextResult>(admitted.Value);
        var child = Child(invocation); var bridge = new ResourceBridge(_authorizer, invocation, FileEffectKind.FilesSearchText, digest, _workspace.Id, child, ResourceMode.Search, _maxReleaseDependencies, root.Value!); using var reader = Reader(bridge, request.MaxEntries);
        var matches = new List<TextMatch>(); var output = 0; var scannedBytes = 0; var truncated = false; var stopSearch = false;
        var stack = new Stack<(string Path, int Depth)>(); stack.Push((root.Value!, 0));
        try
        {
            while (stack.Count > 0)
            {
                operationToken.ThrowIfCancellationRequested(); var (dir, depth) = stack.Pop();

                var list = Bind(new DirectoryListRequest(child, new(_workspace.Id), new(dir), 100_000, 100_000, 4 * 1024 * 1024), bridge);
                var pageResult = await reader.ListDirectoryAsync(list, operationToken).ConfigureAwait(false);

                if (!pageResult.Succeeded)
                {
                    if (pageResult.Failure == ResourceFailureKind.TooLarge) { truncated = true; stopSearch = true; break; }
                    if (!StringComparer.OrdinalIgnoreCase.Equals(dir, root.Value) && pageResult.Failure is ResourceFailureKind.AuthorizationDenied or ResourceFailureKind.NotFound) continue;
                    return Fail<SearchTextResult>(Map(pageResult.Failure));
                }
                var page = pageResult.Value!; if (!page.IsComplete) truncated = true;
                foreach (var entry in page.Entries)
                {
                    operationToken.ThrowIfCancellationRequested(); var relative = Join(dir, entry.Name);
                    if (entry.IsDirectory) { if (depth < request.MaxDepth) stack.Push((relative, depth + 1)); else truncated = true; continue; }
                    if (!FileSystemName.MatchesSimpleExpression(request.Pattern, entry.Name, ignoreCase: true)) continue;
                    if (entry.Length is > 0 && entry.Length > request.MaxFileBytes) { truncated = true; continue; }
                    if (entry.Length is > 0 && entry.Length > request.MaxBytesScanned - scannedBytes) { truncated = true; stopSearch = true; break; }
                    var remainingBytes = request.MaxBytesScanned - scannedBytes; if (remainingBytes <= 0) { truncated = true; stopSearch = true; break; }
                    var file = await reader.ReadFileAsync(Bind(new FileReadRequest(child, new(_workspace.Id), new(relative), new(Math.Min(request.MaxFileBytes, remainingBytes))), bridge), operationToken).ConfigureAwait(false);
                    if (!file.Succeeded)
                    {
                        if (file.Failure == ResourceFailureKind.AuthorizationDenied) continue;
                        if (file.Failure == ResourceFailureKind.TooLarge) { truncated = true; if (remainingBytes < request.MaxFileBytes) { stopSearch = true; break; } continue; }
                        return Fail<SearchTextResult>(Map(file.Failure));
                    }
                    var data = file.Value!.Content.ToArray(); scannedBytes += data.Length;
                    string text; try { text = new UTF8Encoding(false, true).GetString(data); } catch (DecoderFallbackException) { continue; }
                    var lineNo = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        lineNo++; var normalized = line.EndsWith('\r') ? line[..^1] : line;
                        if (!normalized.Contains(request.Text, StringComparison.Ordinal)) continue;
                        var cost = Encoding.UTF8.GetByteCount(relative) + Encoding.UTF8.GetByteCount(normalized) + 16;
                        if (matches.Count >= request.MaxMatches || cost > request.MaxOutputBytes - output) { truncated = true; stopSearch = true; break; }
                        matches.Add(new(relative, lineNo, normalized)); output += cost;
                    }
                    if (stopSearch) break;
                }
                if (stopSearch) break;
            }
            var release = await bridge.ReleaseAsync(operationToken).ConfigureAwait(false);
            if (release is not null) return Fail<SearchTextResult>(release.Value);
            operationToken.ThrowIfCancellationRequested();
            return new(EffectStatus.Succeeded, new(matches, truncated, scannedBytes));
        }
        catch (OperationCanceledException) when (operation.IsDeadlineExceeded) { return Fail<SearchTextResult>(EffectStatus.DeadlineExceeded); }
        catch (OperationCanceledException) { throw; }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenSource _deadline = new();
        private readonly CancellationTokenSource _linked;
        internal OperationScope(CancellationToken callerToken, TimeSpan timeout)
        {
            _callerToken = callerToken;
            _deadline.CancelAfter(timeout);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _deadline.Token);
        }
        internal CancellationToken Token => _linked.Token;
        internal bool IsDeadlineExceeded => _deadline.IsCancellationRequested && !_callerToken.IsCancellationRequested;
        public void Dispose() { _linked.Dispose(); _deadline.Dispose(); }
    }

    private LocalWorkspaceReader Reader(IResourceAuthorizer authorizer, int maxTotalCandidates = 100_000) => new(new(_workspace.Id), _workspace.FullRootPath, authorizer, new() { MaxEntries = 100_000, MaxCandidatesScanned = 100_000, MaxTotalCandidatesScanned = Math.Clamp(maxTotalCandidates, 1, 100_000) });
    private async ValueTask<EffectStatus?> Admit(EffectInvocation invocation, FileEffectKind kind, string digest, string target, CancellationToken ct)
    {
        try
        {
            var decision = await _authorizer.AuthorizeAsync(new(invocation, kind, digest, _workspace.Id, target), ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return decision switch { AuthorizationDecision.Permit => null, AuthorizationDecision.Deny => EffectStatus.AuthorityDenied, _ => EffectStatus.AuthorizationUnavailable };
        }
        catch (OperationCanceledException) { throw; }
        catch { return EffectStatus.AuthorizationUnavailable; }
    }
    private static HostInvocation Child(EffectInvocation i) => new(Guid.NewGuid().ToString("N"), i.SubjectId, i.EffectId, i.AttemptId, Guid.NewGuid().ToString("N"), null, default);
    private static FileReadRequest Bind(FileReadRequest r, ResourceBridge bridge) { var identity = ResourceRequestIdentity.Compute(r); bridge.Register(identity, ResourceAction.ReadFile, r.Path.Value, isList: false); return r with { Invocation = r.Invocation with { RequestIdentity = identity } }; }
    private static DirectoryListRequest Bind(DirectoryListRequest r, ResourceBridge bridge) { var identity = ResourceRequestIdentity.Compute(r); bridge.Register(identity, ResourceAction.ListDirectory, r.Path.Value, isList: true); return r with { Invocation = r.Invocation with { RequestIdentity = identity } }; }
    private enum ResourceMode { Read, Find, Search }

    private sealed class ResourceBridge(IEffectAuthorizer semantic, EffectInvocation parent, FileEffectKind kind, string digest, string workspace, HostInvocation child, ResourceMode mode, int maxReleaseDependencies, string releaseSelector) : IResourceAuthorizer
    {
        private RequestIdentity _expectedIdentity;
        private (ResourceAction Action, string Path, bool IsList) _expected;
        private readonly object _identityGate = new();
        private readonly List<EffectAuthorizationRequest> _releaseDependencies = [];
        internal void Register(RequestIdentity identity, ResourceAction action, string path, bool isList)
        {
            // The runtime awaits each resource request before registering the next one.
            lock (_identityGate)
            {
                _expectedIdentity = identity;
                _expected = (action, path, isList);
            }
        }

        internal async ValueTask<EffectStatus?> ReleaseAsync(CancellationToken ct)
        {
            try
            {
                var decision = await semantic.AuthorizeAsync(new(parent, kind, digest, workspace, releaseSelector, EffectAuthorizationPhase.Release), ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (decision == AuthorizationDecision.Deny) return EffectStatus.AuthorityDenied;
                if (decision != AuthorizationDecision.Permit) return EffectStatus.AuthorizationUnavailable;
            }
            catch (OperationCanceledException) { throw; }
            catch { return EffectStatus.AuthorizationUnavailable; }

            EffectAuthorizationRequest[] dependencies;
            lock (_identityGate) dependencies = _releaseDependencies.ToArray();
            foreach (var dependency in dependencies)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var decision = await semantic.AuthorizeAsync(dependency with { Phase = EffectAuthorizationPhase.Release }, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (decision == AuthorizationDecision.Deny) return EffectStatus.AuthorityDenied;
                    if (decision != AuthorizationDecision.Permit) return EffectStatus.AuthorizationUnavailable;
                }
                catch (OperationCanceledException) { throw; }
                catch { return EffectStatus.AuthorizationUnavailable; }
            }
            return null;
        }

        public async ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest req, CancellationToken ct = default)
        {
            if (req is null || req.Invocation.InvocationId != child.InvocationId || req.Invocation.SubjectId != parent.SubjectId || req.Invocation.EffectScopeId != child.EffectScopeId || req.Invocation.EffectId != parent.EffectId || req.Invocation.AttemptId != parent.AttemptId || req.Invocation.ParentEffectScopeId is not null || req.SnapshotRequestIdentity != req.Invocation.RequestIdentity) return new(AuthorizationStatus.Deny);
            (ResourceAction Action, string Path, bool IsList) expected;
            lock (_identityGate)
            {
                if (req.SnapshotRequestIdentity != _expectedIdentity) return new(AuthorizationStatus.Deny);
                expected = _expected;
            }
            if (req.Resource is not ResourceBinding.WorkspaceDirectory && req.Resource is not ResourceBinding.WorkspaceEntry && req.Resource is not ResourceBinding.WorkspaceFile) return new(AuthorizationStatus.Deny);
            string resourceWorkspace, path;
            switch (req.Resource)
            {
                case ResourceBinding.WorkspaceDirectory x: resourceWorkspace=x.Workspace.Value; path=x.Path.Value; break;
                case ResourceBinding.WorkspaceEntry x: resourceWorkspace=x.Workspace.Value; path=x.Path.Value; break;
                case ResourceBinding.WorkspaceFile x: resourceWorkspace=x.Workspace.Value; path=x.Path.Value; break;
                default: return new(AuthorizationStatus.Deny);
            }
            if (!StringComparer.Ordinal.Equals(workspace, resourceWorkspace)) return new(AuthorizationStatus.Deny);
            if (!Enum.IsDefined(kind)) return new(AuthorizationStatus.Unavailable);
            var allowed = mode switch
            {
                ResourceMode.Read => req.Action is ResourceAction.ReadFile or ResourceAction.ReadMetadata,
                ResourceMode.Find => req.Action is ResourceAction.ListDirectory or ResourceAction.ReadMetadata,
                ResourceMode.Search => req.Action is ResourceAction.ListDirectory or ResourceAction.ReadMetadata or ResourceAction.ReadFile,
                _ => false
            };
            if (!allowed) return new(AuthorizationStatus.Deny);
            var exact = StringComparer.OrdinalIgnoreCase.Equals(expected.Path, path);
            var ancestor = path.Length == 0 || expected.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
            var directChild = expected.IsList && (expected.Path.Length == 0
                ? !path.Contains('/') && path.Length != 0
                : path.StartsWith(expected.Path + "/", StringComparison.OrdinalIgnoreCase) && !path[(expected.Path.Length + 1)..].Contains('/'));
            var roleMatches = req.Action switch
            {
                ResourceAction.ReadFile => !expected.IsList && exact && req.Resource is ResourceBinding.WorkspaceFile,
                ResourceAction.ListDirectory => expected.IsList && exact && req.Resource is ResourceBinding.WorkspaceDirectory,
                ResourceAction.ReadMetadata => req.Resource is ResourceBinding.WorkspaceEntry && (ancestor || exact || directChild),
                _ => false
            };
            if (!roleMatches) return new(AuthorizationStatus.Deny);
            try
            {
                var result = await semantic.AuthorizeAsync(new(parent, kind, digest, workspace, path, EffectAuthorizationPhase.ResourceAccess, req.Action, req.SnapshotRequestIdentity), ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (result == AuthorizationDecision.Permit)
                {
                    lock (_identityGate)
                    {
                        if (_releaseDependencies.Count >= maxReleaseDependencies) return new(AuthorizationStatus.Unavailable);
                        _releaseDependencies.Add(new(parent, kind, digest, workspace, path, EffectAuthorizationPhase.ResourceAccess, req.Action, req.SnapshotRequestIdentity));
                    }
                    return new(AuthorizationStatus.Permit);
                }
                return result == AuthorizationDecision.Deny ? new(AuthorizationStatus.Deny) : new(AuthorizationStatus.Unavailable);
            }
            catch (OperationCanceledException) { throw; } catch { return new(AuthorizationStatus.Unavailable); }
        }
    }

    private static (string? Value, EffectStatus? Error) Normalize(string value, bool allowRoot)
    {
        try { return (WindowsWorkspacePath.Normalize(new WorkspacePath(value), allowRoot).Value, null); }
        catch (ArgumentException) { return (null, EffectStatus.InvalidPath); }
    }
    private static bool ValidInvocation(EffectInvocation? i) => i is not null && ValidId(i.SubjectId) && ValidId(i.EffectId) && ValidId(i.AttemptId);
    private static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; }
        catch (EncoderFallbackException) { return false; }
    }
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static bool ValidLimits(int d,int e,int m,int o) => d is >=0 and <=64 && e is >=1 and <=100_000 && m is >=1 and <=10_000 && o is >=1 and <=4_194_304;
    private static bool ValidPattern(string p) => !string.IsNullOrWhiteSpace(p) && p.Length <= 256 && !p.Contains('/') && !p.Contains('\\') && !p.Contains(':');
    private static bool RequestWithinBound<T>(T x) => JsonSerializer.SerializeToUtf8Bytes(x).Length <= MaxRequestBytes;
    private static string Digest<T>(T x) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(x)));
    private static string Join(string parent,string child) => parent.Length == 0 ? child : parent + "/" + child;
    private static EffectStatus Map(ResourceFailureKind? f) => f switch
    {
        ResourceFailureKind.NotFound => EffectStatus.NotFound,
        ResourceFailureKind.AuthorizationDenied => EffectStatus.AuthorityDenied,
        ResourceFailureKind.AuthorizationUnavailable => EffectStatus.AuthorizationUnavailable,
        ResourceFailureKind.InvalidPath => EffectStatus.InvalidPath,
        ResourceFailureKind.InvalidRequest => EffectStatus.InvalidRequest,
        ResourceFailureKind.TooLarge => EffectStatus.LimitExceeded,
        ResourceFailureKind.AccessDenied => EffectStatus.AccessDenied,
        ResourceFailureKind.Unsupported => EffectStatus.UnsupportedProfile,
        _ => EffectStatus.ProviderFailure
    };
    private static EffectResult<T> Fail<T>(EffectStatus s,string? detail=null) => new(s,default,detail);
}





