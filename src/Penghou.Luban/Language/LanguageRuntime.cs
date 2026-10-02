using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;

namespace Penghou.Luban.Language;

/// <summary>Closed read-only document execution. Static readiness never grants access.</summary>
public sealed class LanguageRuntime
{
    private readonly WorkspaceReference _workspace;
    private readonly ILanguageAuthorizer _authorizer;
    private readonly IWorkspaceProvider _provider;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public LanguageRuntime(WorkspaceReference workspace, IWorkspaceProvider provider, ILanguageAuthorizer authorizer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (_provider.Workspace.Value != workspace.Id) throw new ArgumentException("Provider workspace does not match the logical workspace.", nameof(provider));
        if (!ValidId(workspace.Id)) throw new ArgumentException("A bounded workspace ID is required.", nameof(workspace));
    }

    public async ValueTask<LanguageReadiness> PreflightAsync(EffectInvocation invocation, CompiledDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(invocation, document)) return new(LanguageRunStatus.InvalidDocument, false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(document.Limits.TimeoutMilliseconds);
        try { return await PreflightCore(invocation, document, new Budget(document.Limits), deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(LanguageRunStatus.LimitExceeded, HasDynamic(document)); }
        catch (Failure ex) { return new(ex.Status, HasDynamic(document)); }
    }

    public async ValueTask<LanguageRunResult> ExecuteAsync(EffectInvocation invocation, CompiledDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(invocation, document)) return new(LanguageRunStatus.InvalidDocument, Diagnostic: Diagnostic(LanguageRunStatus.InvalidDocument));
        if (!_provider.Capabilities.SupportsReads) return new(LanguageRunStatus.UnsupportedProfile, Diagnostic: Diagnostic(LanguageRunStatus.UnsupportedProfile));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(document.Limits.TimeoutMilliseconds);
        var ct = deadline.Token;
        string? activeNode = null;
        try
        {
            var budget = new Budget(document.Limits);
            var ready = await PreflightCore(invocation, document, budget, ct).ConfigureAwait(false);
            if (!ready.ReadyForKnownRequirements) return new(ready.Status, Diagnostic: ready.Diagnostic ?? Diagnostic(ready.Status));
            var release = new HashSet<ReleaseBinding>();
            void AddRelease(ReleaseBinding binding)
            {
                if (release.Add(binding)) budget.Dependency(binding.Path);
            }
            var results = new List<LanguageStatementResult>();
            var outputBytes = 0;
            var outputValues = 0;
            foreach (var statement in document.Statements)
            {
                List<LanguageValue> values = [];
                var truncated = false;
                var coverage = LanguageCoverageReason.None;
                foreach (var node in statement)
                {
                    activeNode = node.Identity;
                    ct.ThrowIfCancellationRequested();
                    if (IsEffect(node.Stage))
                        await Demand(invocation, document, node, LanguageAuthorizationPhase.EffectStart, budget, ct).ConfigureAwait(false);
                    switch (node.Stage)
                    {
                        case ReadStage read:
                        {
                            var inputs = read.Path is { } path ? new[] { path } : values.Cast<FileReferenceValue>().Select(v => v.RelativePath).ToArray();
                            var output = new List<LanguageValue>();
                            var bufferBytes = 0;
                            using var access = Access(invocation, document, node, budget,
                                retain: (path, action, identity) => AddRelease(new(node, path, action, identity)));
                            foreach (var file in inputs)
                            {
                                var data = await Read(access, file, read.MaxBytes, ct).ConfigureAwait(false);
                                string text;
                                try { text = StrictUtf8.GetString(data.Bytes); }
                                catch (DecoderFallbackException) { throw new Failure(LanguageRunStatus.InvalidDocument); }
                                LanguageValue value = read.StartLine.HasValue
                                    ? TextWindows.ReadWindow(file, text, data.Bytes, read.StartLine.Value, read.LineCount!.Value, ct)
                                    : new FileContentValue(file, text, data.Bytes.Length, Convert.ToHexString(SHA256.HashData(data.Bytes)));
                                bufferBytes = checked(bufferBytes + Cost(value));
                                budget.Buffer(output.Count + 1, bufferBytes);
                                output.Add(value);
                                AddRelease(new(node, file, ResourceAction.ReadFile, data.Identity));
                            }
                            values = output;
                            break;
                        }
                        case FindStage find:
                        {
                            using var access = Access(invocation, document, node, budget, find.Limits.MaxEntries,
                                (path, action, identity) => AddRelease(new(node, path, action, identity)));
                            var found = await Discover(access, find.Root, find.Pattern, null, find.Limits,
                                (path, identity) => AddRelease(new(node, path, ResourceAction.ListDirectory, identity)), ct).ConfigureAwait(false);
                            values = found.Files.Select(f => (LanguageValue)new FileReferenceValue(f.Path)).ToList();
                            foreach (var file in found.Files) AddRelease(new(node, file.Path, ResourceAction.ReadMetadata, file.Identity));
                            truncated |= found.Truncated;
                            coverage |= found.Reasons;
                            break;
                        }
                        case SearchStage search:
                        {
                            using var access = Access(invocation, document, node, budget, search.Limits.MaxEntries,
                                (path, action, identity) => AddRelease(new(node, path, action, identity)));
                            List<ObservedFile> inputs;
                            if (search.Root is { } root)
                            {
                                var limits = new TraversalLimits(search.Limits.MaxDepth, search.Limits.MaxEntries, 10_000, document.Limits.MaxIntermediateBytes);
                                var found = await Discover(access, root, search.Include, search.Exclude, limits,
                                    (path, identity) => AddRelease(new(node, path, ResourceAction.ListDirectory, identity)), ct).ConfigureAwait(false);
                                inputs = found.Files;
                                truncated |= found.Truncated;
                            coverage |= found.Reasons;
                            }
                            else inputs = values.Cast<FileReferenceValue>().Select(f => new ObservedFile(f.RelativePath, null, default)).ToList();
                            var output = new List<LanguageValue>();
                            var scanned = 0;
                            var bytes = 0;
                            var stopForLimit = false;
                            foreach (var file in inputs)
                            {
                                ct.ThrowIfCancellationRequested();
                                var remaining = search.Limits.MaxBytesScanned - scanned;
                                if (remaining <= 0) { truncated = true; coverage |= LanguageCoverageReason.ScanByteLimit; break; }
                                if (file.Length > search.Limits.MaxFileBytes)
                                { truncated = true; coverage |= LanguageCoverageReason.FileSizeLimit; continue; }
                                if (file.Length > remaining)
                                { truncated = true; coverage |= LanguageCoverageReason.ScanByteLimit; break; }
                                if (budget.RemainingReadBytes <= 0 || file.Length > budget.RemainingReadBytes)
                                    throw new Failure(LanguageRunStatus.LimitExceeded);
                                (byte[] Bytes, RequestIdentity Identity) data;
                                try { data = await Read(access, file.Path, Math.Min(search.Limits.MaxFileBytes, remaining), ct).ConfigureAwait(false); }
                                catch (Failure ex) when (ex.Status is LanguageRunStatus.AuthorityDenied or LanguageRunStatus.NotFound) { continue; }
                                catch (Failure ex) when (ex.Status == LanguageRunStatus.LimitExceeded && !budget.Exhausted && ex.LimitName == "FileReadLimit")
                                {
                                    truncated = true;
                                    if (remaining < search.Limits.MaxFileBytes)
                                    { coverage |= LanguageCoverageReason.ScanByteLimit; break; }
                                    coverage |= LanguageCoverageReason.FileSizeLimit;
                                    continue;
                                }
                                scanned += data.Bytes.Length;
                                // Absence of a match and invalid-text omission can also influence
                                // derived counts; retain every successful protected read.
                                AddRelease(new(node, file.Path, ResourceAction.ReadFile, data.Identity));
                                string text;
                                try { text = StrictUtf8.GetString(data.Bytes); }
                                catch (DecoderFallbackException) { continue; }
                                if (search.ContextLines > 0 || IsTextChangeProfile(document.Versions))
                                {
                                    var contextual = TextWindows.Search(file.Path, text, data.Bytes, search.Query,
                                        search.ContextLines, search.Limits.MaxMatches - output.Count,
                                        search.Limits.MaxOutputBytes - bytes, ct, out var contextTruncated, out var contextReasons);
                                    foreach (var value in contextual)
                                    {
                                        var cost = Cost(value);
                                        budget.Buffer(output.Count + 1, checked(bytes + cost));
                                        output.Add(value);
                                        bytes += cost;
                                    }
                                    if (contextTruncated) { truncated = true; coverage |= contextReasons; }
                                    if (contextTruncated || output.Count >= search.Limits.MaxMatches || bytes >= search.Limits.MaxOutputBytes) break;
                                    continue;
                                }
                                // Streaming line extraction avoids allocating one string for every nonmatching line.
                                var start = 0;
                                var number = 0;
                                while (start <= text.Length)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var end = text.IndexOf('\n', start);
                                    if (end < 0) end = text.Length;
                                    var line = text.AsSpan(start, end - start);
                                    if (line.EndsWith("\r", StringComparison.Ordinal)) line = line[..^1];
                                    number++;
                                    if (line.Contains(search.Query.AsSpan(), StringComparison.Ordinal))
                                    {
                                        var value = new SearchMatchValue(file.Path, number, line.ToString());
                                        var cost = Cost(value);
                                        if (output.Count >= search.Limits.MaxMatches || cost > search.Limits.MaxOutputBytes - bytes)
                                        {
                                            truncated = true;
                                            coverage |= output.Count >= search.Limits.MaxMatches ? LanguageCoverageReason.MatchLimit : LanguageCoverageReason.OutputByteLimit;
                                            stopForLimit = true; break;
                                        }
                                        budget.Buffer(output.Count + 1, checked(bytes + cost));
                                        output.Add(value);
                                        bytes += cost;
                                    }
                                    if (end == text.Length) break;
                                    start = end + 1;
                                }
                                if (stopForLimit || output.Count >= search.Limits.MaxMatches || bytes >= search.Limits.MaxOutputBytes)
                                {
                                    truncated = true;
                                    if (output.Count >= search.Limits.MaxMatches) coverage |= LanguageCoverageReason.MatchLimit;
                                    if (bytes >= search.Limits.MaxOutputBytes) coverage |= LanguageCoverageReason.OutputByteLimit;
                                    break;
                                }
                            }
                            values = output;
                            break;
                        }
                        case DiffStage diff:
                        {
                            var limits = ChangeLimits(document, diff.Options!, 2, budget);
                            var adapter = new LanguageFileChangeAuthorizer(this, invocation, document, node,
                                budget, (path, action, identity) => AddRelease(new(node, path, action, identity)));
                            var changeRuntime = new FileChangeRuntime(_workspace, _provider, adapter);
                            var outcome = await changeRuntime.DiffAsync(invocation, document.Workspace,
                                new FileDiffRequest(diff.BeforePath, diff.AfterPath, diff.Options), limits, ct).ConfigureAwait(false);
                            if (adapter.LimitExceeded) throw new Failure(LanguageRunStatus.LimitExceeded, nameof(LanguageExecutionLimits.MaxResourceCalls));
                            ct.ThrowIfCancellationRequested();
                            if (outcome.Status != FileChangeStatus.Succeeded || outcome.Diff is null)
                                throw new Failure(MapChange(outcome.Status));
                            var before = outcome.Diff.Before!; var after = outcome.Diff.After!;
                            values = [new DiffValue(diff.BeforePath, diff.AfterPath, outcome.Diff.ProfileIdentity,
                                before.Sha256, before.ByteLength, after.Sha256, after.ByteLength, outcome.Diff.Edits)];
                            break;
                        }
                        case MergeStage merge:
                        {
                            var limits = ChangeLimits(document, merge.Options!, 3, budget);
                            var adapter = new LanguageFileChangeAuthorizer(this, invocation, document, node,
                                budget, (path, action, identity) => AddRelease(new(node, path, action, identity)));
                            var changeRuntime = new FileChangeRuntime(_workspace, _provider, adapter);
                            var outcome = await changeRuntime.MergeAsync(invocation, document.Workspace,
                                new FileMergeRequest(merge.BasePath, merge.OursPath, merge.TheirsPath, merge.Options), limits, ct).ConfigureAwait(false);
                            if (adapter.LimitExceeded) throw new Failure(LanguageRunStatus.LimitExceeded, nameof(LanguageExecutionLimits.MaxResourceCalls));
                            ct.ThrowIfCancellationRequested();
                            if (outcome.Status == FileChangeStatus.Conflicted && outcome.Merge is { } conflicted)
                            {
                                values = [new MergeConflictValue(merge.BasePath, merge.OursPath, merge.TheirsPath,
                                    conflicted.ProfileIdentity, conflicted.Conflicts)];
                                break;
                            }
                            if (outcome.Status != FileChangeStatus.Succeeded || outcome.Merge is not { Value: { } text } clean)
                                throw new Failure(MapChange(outcome.Status));
                            var baseSnapshot = clean.Base!; var oursSnapshot = clean.Ours!; var theirsSnapshot = clean.Theirs!; var resultSnapshot = clean.Result!;
                            if (outcome.Candidates.Count != 1) throw new Failure(LanguageRunStatus.InvalidDocument);
                            var targetEdits = outcome.Candidates[0].Edits.Select(e => new TextEdit(e.StartOffset,
                                e.DeleteLength, StrictUtf8.GetString(e.ReplacementUtf8.ToArray()))).ToArray();
                            values = [new MergeValue(merge.BasePath, merge.OursPath, merge.TheirsPath, merge.OursPath, clean.ProfileIdentity,
                                baseSnapshot.Sha256, baseSnapshot.ByteLength, oursSnapshot.Sha256, oursSnapshot.ByteLength,
                                theirsSnapshot.Sha256, theirsSnapshot.ByteLength, resultSnapshot.Sha256, resultSnapshot.ByteLength,
                                text, Array.AsReadOnly(targetEdits))];
                            break;
                        }
                        case TakeStage take:
                            if (values.Count > take.Count) { values = values.Take(take.Count).ToList(); truncated = true; coverage |= LanguageCoverageReason.TakeLimit; }
                            break;
                        case CountStage:
                            values = [new CountValue(values.Count)];
                            break;
                        default: throw new Failure(LanguageRunStatus.InvalidDocument);
                    }
                    budget.Buffer(values);
                }
                outputValues = checked(outputValues + values.Count);
                outputBytes = checked(outputBytes + values.Sum(Cost));
                if (outputValues > document.Limits.MaxValues || outputBytes > document.Limits.MaxOutputBytes)
                    throw new Failure(LanguageRunStatus.LimitExceeded, outputValues > document.Limits.MaxValues ? nameof(LanguageExecutionLimits.MaxValues) : nameof(LanguageExecutionLimits.MaxOutputBytes));
                results.Add(new(Array.AsReadOnly(values.ToArray()), truncated, coverage));
            }
            // Covers protected derived results (including count/take) and live per-resource release.
            foreach (var node in document.Statements.SelectMany(s => s).Where(n => IsEffect(n.Stage)))
            {
                activeNode = node.Identity;
                await Demand(invocation, document, node, LanguageAuthorizationPhase.Release, budget, ct).ConfigureAwait(false);
            }
            foreach (var binding in release)
                await Demand(invocation, document, binding.Node, LanguageAuthorizationPhase.Release, budget, ct, binding.Path, binding.Action, binding.Identity).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(LanguageRunStatus.Succeeded, Array.AsReadOnly(results.ToArray()));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(LanguageRunStatus.LimitExceeded, Diagnostic: Diagnostic(LanguageRunStatus.LimitExceeded, activeNode, nameof(LanguageExecutionLimits.TimeoutMilliseconds))); }
        catch (OperationCanceledException) { throw; }
        catch (Failure ex) { return new(ex.Status, Diagnostic: Diagnostic(ex.Status, ex.NodeIdentity ?? activeNode, ex.LimitName)); }
        catch { return new(LanguageRunStatus.ProviderFailure, Diagnostic: Diagnostic(LanguageRunStatus.ProviderFailure, activeNode)); }
    }

    private async ValueTask<LanguageReadiness> PreflightCore(EffectInvocation invocation, CompiledDocument doc, Budget budget, CancellationToken ct)
    {
        foreach (var node in doc.Statements.SelectMany(s => s).Where(n => IsEffect(n.Stage)))
        {
            budget.Call();
            var status = await Check(Request(invocation, doc, node, LanguageAuthorizationPhase.Preflight), ct).ConfigureAwait(false);
            if (status != LanguageAuthorityStatus.Permit)
                return new(status == LanguageAuthorityStatus.Deny ? LanguageRunStatus.AuthorityDenied : LanguageRunStatus.AuthorizationUnavailable, HasDynamic(doc), Diagnostic(status == LanguageAuthorityStatus.Deny ? LanguageRunStatus.AuthorityDenied : LanguageRunStatus.AuthorizationUnavailable, node.Identity));
        }
        return new(LanguageRunStatus.Succeeded, HasDynamic(doc));
    }

    private bool Validate(EffectInvocation? invocation, CompiledDocument? doc)
    {
        if (invocation is null || !ValidId(invocation.SubjectId) || !ValidId(invocation.EffectId) || !ValidId(invocation.AttemptId) || doc is null || doc.Workspace.Value != _workspace.Id)
            return false;
        var statements = doc.Statements.Select(s => (IReadOnlyList<LanguageStage>)s.Select(n => n.Stage).ToArray()).ToArray();
        var checkedDoc = LanguageCompiler.Compile(statements, doc.Workspace, new(Versions: doc.Versions, ExecutionLimits: doc.Limits));
        if (!checkedDoc.Succeeded || checkedDoc.Document!.Identity != doc.Identity) return false;
        return checkedDoc.Document.Statements.SelectMany(s => s).Select(n => (n.Identity, n.Output, n.Descriptor))
            .SequenceEqual(doc.Statements.SelectMany(s => s).Select(n => (n.Identity, n.Output, n.Descriptor)));
    }

    private static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; }
        catch (EncoderFallbackException) { return false; }
    }
    private static bool IsEffect(LanguageStage stage) => stage is ReadStage or FindStage or SearchStage or DiffStage or MergeStage;
    private static bool IsTextChangeProfile(LanguageVersions versions) => versions == new LanguageVersions(
        LanguageProfile.TextChangeLanguageVersion, LanguageProfile.TextChangeIrVersion,
        LanguageProfile.TextChangeCatalogueVersion, LanguageProfile.TextChangeProviderProfile);
    private static bool HasDynamic(CompiledDocument doc) => doc.Statements.SelectMany(s => s).Any(n => n.Stage is FindStage or SearchStage or ReadStage { Path: null });
    private static string? Selector(LanguageStage stage) => stage switch { ReadStage r => r.Path, FindStage f => f.Root, SearchStage s => s.Root, _ => null };
    private static LanguageAuthorizationRequest Request(EffectInvocation i, CompiledDocument d, CompiledNode n, LanguageAuthorizationPhase phase,
        string? path = null, ResourceAction? action = null, RequestIdentity? identity = null) =>
        new(i, d.Identity, n.Identity, n.Descriptor, LanguageProfile.DescriptorVersion, d.Versions, d.Workspace, n.Stage, phase, path ?? Selector(n.Stage), action, identity);

    private async ValueTask<LanguageAuthorityStatus> Check(LanguageAuthorizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var response = await _authorizer.AuthorizeAsync(request, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return response?.Status switch { LanguageAuthorityStatus.Permit => LanguageAuthorityStatus.Permit, LanguageAuthorityStatus.Deny => LanguageAuthorityStatus.Deny, _ => LanguageAuthorityStatus.Unavailable };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return LanguageAuthorityStatus.Unavailable; }
    }

    private async ValueTask Demand(EffectInvocation i, CompiledDocument d, CompiledNode n, LanguageAuthorizationPhase phase, Budget budget, CancellationToken ct,
        string? path = null, ResourceAction? action = null, RequestIdentity? identity = null)
    {
        budget.Call();
        var status = await Check(Request(i, d, n, phase, path, action, identity), ct).ConfigureAwait(false);
        if (status != LanguageAuthorityStatus.Permit)
            throw new Failure(status == LanguageAuthorityStatus.Deny ? LanguageRunStatus.AuthorityDenied : LanguageRunStatus.AuthorizationUnavailable, nodeIdentity: n.Identity);
    }

    private NodeAccess Access(EffectInvocation i, CompiledDocument d, CompiledNode n, Budget budget, int maxCandidates = 100_000,
        Action<string, ResourceAction, RequestIdentity>? retain = null) =>
        new(this, i, d, n, budget, maxCandidates, retain ?? ((_, _, _) => { }));

    private static async ValueTask<(byte[] Bytes, RequestIdentity Identity)> Read(NodeAccess access, string path, int maximum, CancellationToken ct)
    {
        var bound = Math.Min(maximum, access.Budget.RemainingReadBytes);
        if (bound <= 0) throw new Failure(LanguageRunStatus.LimitExceeded);
        var request = new FileReadRequest(access.Invocation, access.Document.Workspace, new(path), new(bound));
        var identity = ResourceRequestIdentity.Compute(request);
        access.Register(identity, ResourceAction.ReadFile, path);
        var result = await access.Reader.ReadFileAsync(request with { Invocation = request.Invocation with { RequestIdentity = identity } }, ct).ConfigureAwait(false);
        if (access.Budget.Exhausted) throw new Failure(LanguageRunStatus.LimitExceeded);
        if (!result.Succeeded)
        {
            if (result.Failure == ResourceFailureKind.TooLarge && bound < maximum) access.Budget.Exhaust();
            throw new Failure(Map(result.Failure), result.Failure == ResourceFailureKind.TooLarge ? "FileReadLimit" : null);
        }
        var data = result.Value!.Content.ToArray();
        access.Budget.Read(data.Length);
        return (data, identity);
    }

    private static async ValueTask<(List<ObservedFile> Files, bool Truncated, LanguageCoverageReason Reasons)> Discover(NodeAccess access, string root, string include, string? exclude,
        TraversalLimits limits, Action<string, RequestIdentity> observeDirectory, CancellationToken ct)
    {
        var found = new List<ObservedFile>();
        var bytes = 0;
        var truncated = false;
        var coverage = LanguageCoverageReason.None;
        var stack = new Stack<(string Path, int Depth)>();
        var stackBytes = 0;
        void Push(string path, int depth)
        {
            var cost = Encoding.UTF8.GetByteCount(path) + 32;
            stackBytes = checked(stackBytes + cost);
            access.Budget.Buffer(stack.Count + 1, stackBytes);
            stack.Push((path, depth));
        }
        Push(root, 0);
        while (stack.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            stackBytes -= Encoding.UTF8.GetByteCount(dir) + 32;
            var request = new DirectoryListRequest(access.Invocation, access.Document.Workspace, new(dir), 100_000, 100_000, access.Document.Limits.MaxIntermediateBytes);
            var identity = ResourceRequestIdentity.Compute(request);
            access.Register(identity, ResourceAction.ListDirectory, dir);
            var result = await access.Reader.ListDirectoryAsync(request with { Invocation = request.Invocation with { RequestIdentity = identity } }, ct).ConfigureAwait(false);
            if (access.Budget.Exhausted) throw new Failure(LanguageRunStatus.LimitExceeded);
            if (!result.Succeeded)
            {
                if (result.Failure == ResourceFailureKind.TooLarge) return (found, true, coverage | LanguageCoverageReason.TraversalLimit);
                if (dir != root && result.Failure is ResourceFailureKind.AuthorizationDenied or ResourceFailureKind.NotFound) continue;
                throw new Failure(Map(result.Failure), result.Failure == ResourceFailureKind.TooLarge ? "FileReadLimit" : null);
            }
            if (!result.Value!.IsComplete) { truncated = true; coverage |= LanguageCoverageReason.TraversalLimit; }
            observeDirectory(dir, identity);
            foreach (var entry in result.Value.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = dir.Length == 0 ? entry.Name : dir + "/" + entry.Name;
                var relative = root.Length == 0 ? path : path[(root.Length + 1)..];
                if (entry.IsDirectory)
                {
                    if (LanguageGlob.CanDescend(include, relative, access.Budget.Glob, ct))
                    {
                        if (depth < limits.MaxDepth) Push(path, depth + 1);
                        else { truncated = true; coverage |= LanguageCoverageReason.DepthLimit; }
                    }
                    continue;
                }
                if (!LanguageGlob.IsMatch(include, relative, access.Budget.Glob, ct) ||
                    exclude is not null && LanguageGlob.IsMatch(exclude, relative, access.Budget.Glob, ct)) continue;
                var cost = Encoding.UTF8.GetByteCount(path) + 32;
                if (found.Count >= limits.MaxMatches || cost > limits.MaxOutputBytes - bytes) return (found, true, coverage | (found.Count >= limits.MaxMatches ? LanguageCoverageReason.MatchLimit : LanguageCoverageReason.OutputByteLimit));
                found.Add(new(path, entry.Length, identity));
                bytes += cost;
                if (found.Count > access.Document.Limits.MaxValues) throw new Failure(LanguageRunStatus.LimitExceeded);
            }
            if (truncated) break;
        }
        return (found, truncated, coverage);
    }

    private static int Cost(LanguageValue value) => value switch
    {
        FileReferenceValue r => Encoding.UTF8.GetByteCount(r.RelativePath) + 32,
        FileContentValue c => checked(Encoding.UTF8.GetByteCount(c.RelativePath) + Encoding.UTF8.GetByteCount(c.Content) + 128),
        FileWindowValue w => checked(Encoding.UTF8.GetByteCount(w.RelativePath) + Encoding.UTF8.GetByteCount(w.Content) + 160),
        SearchMatchValue m => checked(Encoding.UTF8.GetByteCount(m.RelativePath) + Encoding.UTF8.GetByteCount(m.Line) + 32),
        SearchContextMatchValue m => checked(Encoding.UTF8.GetByteCount(m.RelativePath) + Encoding.UTF8.GetByteCount(m.Line) +
            m.BeforeContext.Sum(Encoding.UTF8.GetByteCount) + m.AfterContext.Sum(Encoding.UTF8.GetByteCount) + 160),
        DiffValue d => checked(Encoding.UTF8.GetByteCount(d.BeforePath) + Encoding.UTF8.GetByteCount(d.AfterPath) +
            d.Edits.Sum(e => Encoding.UTF8.GetByteCount(e.Replacement) + 32) + 160),
        MergeValue m => checked(Encoding.UTF8.GetByteCount(m.BasePath) + Encoding.UTF8.GetByteCount(m.OursPath) +
            Encoding.UTF8.GetByteCount(m.TheirsPath) + Encoding.UTF8.GetByteCount(m.TargetPath) + Encoding.UTF8.GetByteCount(m.ProposedText) +
            m.Edits.Sum(e => Encoding.UTF8.GetByteCount(e.Replacement) + 32) + 192),
        MergeConflictValue m => checked(Encoding.UTF8.GetByteCount(m.BasePath) + Encoding.UTF8.GetByteCount(m.OursPath) +
            Encoding.UTF8.GetByteCount(m.TheirsPath) + m.Conflicts.Sum(c => Encoding.UTF8.GetByteCount(c.BaseText) +
                Encoding.UTF8.GetByteCount(c.OursText) + Encoding.UTF8.GetByteCount(c.TheirsText) + 48) + 128),
        CountValue => 16,
        _ => throw new Failure(LanguageRunStatus.InvalidDocument)
    };

    private static FileChangeLimits ChangeLimits(CompiledDocument document, TextChangeOptions options, int fileCount, Budget budget)
    {
        var aggregate = Math.Min(budget.RemainingReadBytes, checked(options.MaxInputBytes * fileCount));
        aggregate = Math.Clamp(aggregate, 1, FileChangeLimits.Default.MaxTotalReadBytes);
        budget.ReserveReadBytes(aggregate);
        return new FileChangeLimits(fileCount, aggregate, document.Limits.TimeoutMilliseconds);
    }

    private static LanguageRunStatus MapChange(FileChangeStatus status) => status switch
    {
        FileChangeStatus.AuthorizationDenied => LanguageRunStatus.AuthorityDenied,
        FileChangeStatus.AuthorizationUnavailable => LanguageRunStatus.AuthorizationUnavailable,
        FileChangeStatus.UnsupportedProfile => LanguageRunStatus.UnsupportedProfile,
        FileChangeStatus.LimitExceeded => LanguageRunStatus.LimitExceeded,
        FileChangeStatus.Cancelled => LanguageRunStatus.Cancelled,
        FileChangeStatus.NotFound => LanguageRunStatus.NotFound,
        FileChangeStatus.AccessDenied => LanguageRunStatus.AccessDenied,
        FileChangeStatus.ProviderFailure => LanguageRunStatus.ProviderFailure,
        FileChangeStatus.InvalidInput or FileChangeStatus.Stale or FileChangeStatus.Valid => LanguageRunStatus.InvalidDocument,
        _ => LanguageRunStatus.ProviderFailure
    };
    private static LanguageRunStatus Map(ResourceFailureKind? failure) => failure switch
    {
        ResourceFailureKind.AuthorizationDenied => LanguageRunStatus.AuthorityDenied,
        ResourceFailureKind.AuthorizationUnavailable => LanguageRunStatus.AuthorizationUnavailable,
        ResourceFailureKind.NotFound => LanguageRunStatus.NotFound,
        ResourceFailureKind.TooLarge => LanguageRunStatus.LimitExceeded,
        ResourceFailureKind.AccessDenied => LanguageRunStatus.AccessDenied,
        ResourceFailureKind.Unsupported => LanguageRunStatus.UnsupportedProfile,
        ResourceFailureKind.InvalidPath or ResourceFailureKind.InvalidRequest => LanguageRunStatus.InvalidDocument,
        _ => LanguageRunStatus.ProviderFailure
    };

    private sealed record ObservedFile(string Path, long? Length, RequestIdentity Identity);
    private sealed record ReleaseBinding(CompiledNode Node, string Path, ResourceAction Action, RequestIdentity Identity);
    private static LanguageRunDiagnostic Diagnostic(LanguageRunStatus status, string? node = null, string? limit = null) =>
        new("LUBAN_" + status.ToString().ToUpperInvariant(), node, limit);
    private sealed class Failure(LanguageRunStatus status, string? limitName = null, string? nodeIdentity = null) : Exception
    {
        internal LanguageRunStatus Status { get; } = status;
        internal string? LimitName { get; } = limitName;
        internal string? NodeIdentity { get; } = nodeIdentity;
    }

    private sealed class LanguageFileChangeAuthorizer : IFileChangeAuthorizer
    {
        private readonly LanguageRuntime _runtime;
        private readonly EffectInvocation _invocation;
        private readonly CompiledDocument _document;
        private readonly CompiledNode _node;
        private readonly Budget _budget;
        private readonly Action<string, ResourceAction, RequestIdentity> _retain;
        internal bool LimitExceeded { get; private set; }

        internal LanguageFileChangeAuthorizer(LanguageRuntime runtime, EffectInvocation invocation,
            CompiledDocument document, CompiledNode node, Budget budget, Action<string, ResourceAction, RequestIdentity> retain)
        { _runtime = runtime; _invocation = invocation; _document = document; _node = node; _budget = budget; _retain = retain; }

        public async ValueTask<FileChangeAuthorizationDecision> AuthorizeAsync(
            FileChangeAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null || request.Invocation != _invocation || request.Workspace != _document.Workspace)
                return new(FileChangeAuthorizationStatus.Deny);
            var phase = request.Phase switch
            {
                FileChangePhase.Preflight => LanguageAuthorizationPhase.Preflight,
                FileChangePhase.TargetAdmission => LanguageAuthorizationPhase.ResourceAccess,
                FileChangePhase.ResourceAccess => LanguageAuthorizationPhase.ResourceAccess,
                FileChangePhase.ResultRelease => LanguageAuthorizationPhase.Release,
                _ => (LanguageAuthorizationPhase?)null
            };
            if (phase is null) return new(FileChangeAuthorizationStatus.Deny);
            try { _budget.Call(); }
            catch (Failure failure) when (failure.Status == LanguageRunStatus.LimitExceeded)
            { LimitExceeded = true; return new(FileChangeAuthorizationStatus.Unavailable); }
            var path = request.Resource?.Path.Value;
            var action = request.Action ?? (request.Phase == FileChangePhase.TargetAdmission ? ResourceAction.ReadFile : null);
            var decision = await _runtime.Check(Request(_invocation, _document, _node, phase.Value,
                path, action, request.RequestIdentity), cancellationToken).ConfigureAwait(false);
            if (decision == LanguageAuthorityStatus.Permit && request.Phase == FileChangePhase.ResourceAccess &&
                path is not null && action is { } permittedAction && request.RequestIdentity is { } identity)
            {
                try { _retain(path, permittedAction, identity); }
                catch (Failure failure) when (failure.Status == LanguageRunStatus.LimitExceeded)
                { LimitExceeded = true; return new(FileChangeAuthorizationStatus.Unavailable); }
            }
            return new(decision switch
            {
                LanguageAuthorityStatus.Permit => FileChangeAuthorizationStatus.Permit,
                LanguageAuthorityStatus.Deny => FileChangeAuthorizationStatus.Deny,
                _ => FileChangeAuthorizationStatus.Unavailable
            });
        }
    }

    private sealed class Budget(LanguageExecutionLimits limits)
    {
        private int _calls;
        private int _read;
        private int _dependencies;
        private int _dependencyBytes;
        private int _globWork;
        internal bool Exhausted { get; private set; }
        internal void Exhaust() => Exhausted = true;
        internal void MarkDependencyExhausted() => Exhausted = true;
        internal void Glob(int work)
        {
            if (work > LanguageProfile.MaxGlobWork - _globWork) throw new Failure(LanguageRunStatus.LimitExceeded, nameof(LanguageProfile.MaxGlobWork));
            _globWork += work;
        }
        internal int RemainingReadBytes => limits.MaxReadBytes - _read;
        internal void Call() { if (++_calls > limits.MaxResourceCalls) { Exhausted = true; throw new Failure(LanguageRunStatus.LimitExceeded, nameof(LanguageExecutionLimits.MaxResourceCalls)); } }
        internal void Read(int bytes) { _read = checked(_read + bytes); if (_read > limits.MaxReadBytes) throw new Failure(LanguageRunStatus.LimitExceeded, nameof(LanguageExecutionLimits.MaxReadBytes)); }
        internal void ReserveReadBytes(int bytes)
        {
            if (bytes <= 0 || bytes > RemainingReadBytes) throw new Failure(LanguageRunStatus.LimitExceeded, nameof(LanguageExecutionLimits.MaxReadBytes));
            _read = checked(_read + bytes);
        }
        internal void Buffer(IReadOnlyList<LanguageValue> values)
        {
            Buffer(values.Count, values.Sum(Cost));
        }
        internal void Buffer(int count, int bytes)
        {
            if (count > limits.MaxValues || bytes > limits.MaxIntermediateBytes) throw new Failure(LanguageRunStatus.LimitExceeded, count > limits.MaxValues ? nameof(LanguageExecutionLimits.MaxValues) : nameof(LanguageExecutionLimits.MaxIntermediateBytes));
        }
        internal void Dependency(string path)
        {
            try
            {
                _dependencies++;
                _dependencyBytes = checked(_dependencyBytes + Encoding.UTF8.GetByteCount(path) + 128);
                Buffer(_dependencies, _dependencyBytes);
            }
            catch (Failure)
            {
                Exhausted = true;
                throw;
            }
            catch (OverflowException)
            {
                Exhausted = true;
                throw new Failure(LanguageRunStatus.LimitExceeded);
            }
        }
    }

    private sealed class NodeAccess : IResourceAuthorizer, IDisposable
    {
        private readonly LanguageRuntime _runtime;
        private readonly EffectInvocation _parent;
        private readonly CompiledNode _node;
        private readonly Action<string, ResourceAction, RequestIdentity> _retain;
        private RequestIdentity _identity;
        private ResourceAction _action;
        private string _path = "";
        internal NodeAccess(LanguageRuntime runtime, EffectInvocation parent, CompiledDocument document, CompiledNode node, Budget budget, int candidates,
            Action<string, ResourceAction, RequestIdentity> retain)
        {
            _runtime = runtime; _parent = parent; _node = node; Document = document; Budget = budget; _retain = retain;
            Invocation = new(Guid.NewGuid().ToString("N"), parent.SubjectId, parent.EffectId, parent.AttemptId, Guid.NewGuid().ToString("N"), null, default);
            Reader = runtime._provider.OpenReader(this, new(MaxEntries: 100_000, MaxCandidatesScanned: 100_000,
                MaxTotalCandidatesScanned: candidates));
        }
        internal CompiledDocument Document { get; }
        internal HostInvocation Invocation { get; }
        internal Budget Budget { get; }
        internal IWorkspaceReaderSession Reader { get; }
        internal void Register(RequestIdentity identity, ResourceAction action, string path) { _identity = identity; _action = action; _path = path; }
        public async ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request, CancellationToken ct = default)
        {
            if (request.Invocation with { RequestIdentity = default } != Invocation || request.SnapshotRequestIdentity != _identity || request.Invocation.RequestIdentity != _identity)
                return new(AuthorizationStatus.Deny);
            var (workspace, path) = request.Resource switch
            {
                ResourceBinding.WorkspaceFile f => (f.Workspace, f.Path.Value),
                ResourceBinding.WorkspaceDirectory d => (d.Workspace, d.Path.Value),
                ResourceBinding.WorkspaceEntry e => (e.Workspace, e.Path.Value),
                _ => (default, null)
            };
            if (workspace != Document.Workspace || path is null) return new(AuthorizationStatus.Deny);
            var exact = string.Equals(_path, path, StringComparison.Ordinal);
            var ancestor = path.Length == 0 || _path.StartsWith(path + "/", StringComparison.Ordinal);
            var child = _action == ResourceAction.ListDirectory && (_path.Length == 0 ? path.Length != 0 && !path.Contains('/') : path.StartsWith(_path + "/", StringComparison.Ordinal) && !path[(_path.Length + 1)..].Contains('/'));
            var role = request.Action switch
            {
                ResourceAction.ReadFile => _action == request.Action && _node.Stage is ReadStage or SearchStage && exact && request.Resource is ResourceBinding.WorkspaceFile,
                ResourceAction.ListDirectory => _action == request.Action && _node.Stage is FindStage or SearchStage && exact && request.Resource is ResourceBinding.WorkspaceDirectory,
                ResourceAction.ReadMetadata => request.Resource is ResourceBinding.WorkspaceEntry && (exact || ancestor || child),
                _ => false
            };
            if (!role) return new(AuthorizationStatus.Deny);
            Budget.Call();
            var status = await _runtime.Check(Request(_parent, Document, _node, LanguageAuthorizationPhase.ResourceAccess, path, request.Action, _identity), ct).ConfigureAwait(false);
            if (status == LanguageAuthorityStatus.Permit && request.Action == ResourceAction.ReadMetadata)
            {
                try { _retain(path, request.Action, _identity); }
                catch (Failure) { Budget.MarkDependencyExhausted(); return new(AuthorizationStatus.Unavailable); }
            }
            return new(status switch { LanguageAuthorityStatus.Permit => AuthorizationStatus.Permit, LanguageAuthorityStatus.Deny => AuthorizationStatus.Deny, _ => AuthorizationStatus.Unavailable });
        }
        public void Dispose() => Reader.Dispose();
    }
}
