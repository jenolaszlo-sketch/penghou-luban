using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban.Changes;
using Xunit;
using Xunit.Abstractions;

namespace Penghou.Luban.Tests;

public sealed class LocalWorkspacePatcherTests
{
    private static readonly WorkspaceId Workspace = new("writer-tests");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly LocalPatchOptions HostControlledOptions = new(Namespace: LocalPatchNamespace.HostControlled);
    private readonly ITestOutputHelper _output;

    public LocalWorkspacePatcherTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(LocalPatchNamespace.Unspecified)]
    [InlineData((LocalPatchNamespace)123)]
    public async Task Unqualified_namespace_profile_is_rejected_before_authorization_or_io(LocalPatchNamespace profile)
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var authorizer = new PermitAuthorizer();
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, authorizer, journal, new LocalPatchOptions(Namespace: profile));

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.Equal(ResourceFailureKind.Unsupported, result.Failure);
        Assert.Empty(authorizer.Requests);
        Assert.Empty(journal.Starts);
        Assert.Empty(journal.Completions);
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Exact_patch_starts_after_preconditions_and_records_completed_version()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "hello\n");
        var authorizer = new PermitAuthorizer();
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, authorizer, journal, HostControlledOptions);
        var request = Request(temp.Invocation, "note.txt", "hello\n", new TextPatch(0, 5, Utf8.GetBytes("world")));

        var result = await patcher.WriteFileAsync(request);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal("world\n", temp.Read("note.txt"));
        Assert.Single(journal.Starts);
        Assert.Equal(MutationOutcome.Completed, Assert.Single(journal.Completions).Outcome);
        Assert.Equal(result.Value, journal.Completions[0].ObservedVersion);
        Assert.StartsWith("ntfs:", journal.Starts[0].ObjectIdentity, StringComparison.Ordinal);
        Assert.Equal(1, authorizer.Requests.Count(r => r.Action == ResourceAction.ReadFile));
        Assert.Equal(2, authorizer.Requests.Count(r => r.Action == ResourceAction.WriteFile));
        Assert.DoesNotContain(authorizer.Requests, r => r.Action == ResourceAction.PatchFile);
        Assert.Contains(authorizer.Requests, r => r.Action == ResourceAction.ReadMetadata &&
            r.Resource is ResourceBinding.WorkspaceEntry e && e.Path == WorkspacePath.Root);
    }

    [Fact]
    public async Task Stale_version_never_starts_journal_or_changes_file()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);
        var request = Request(temp.Invocation, "note.txt", "different", new TextPatch(0, 8, Utf8.GetBytes("changed")));

        var result = await patcher.WriteFileAsync(request);

        Assert.Equal(ResourceFailureKind.PreconditionFailed, result.Failure);
        Assert.Empty(journal.Starts);
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Denied_file_authority_prevents_native_probe_and_journal_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var authorizer = new PermitAuthorizer(r => r.Action == ResourceAction.ReadFile
            ? new ResourceAuthorizationDecision(AuthorizationStatus.Deny)
            : new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, authorizer, journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Empty(journal.Starts);
        Assert.Equal(new[] { ResourceAction.WriteFile, ResourceAction.ReadFile }, authorizer.Requests.Select(r => r.Action));
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Invalid_utf8_scalar_boundary_is_rejected_before_journal_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "a😀b");
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);
        Assert.Throws<InvalidDataException>(() => Request(temp.Invocation, "note.txt", "a😀b", new TextPatch(2, 1, Utf8.GetBytes("x"))));
        Assert.Empty(journal.Starts);
        Assert.Equal("a😀b", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Start_deny_or_unavailable_never_writes()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        foreach (var status in new[] { MutationStartStatus.Deny, MutationStartStatus.Unavailable, MutationStartStatus.AlreadyStarted })
        {
            using var temp = new TemporaryWorkspace();
            temp.Write("note.txt", "original");
            var journal = new RecordingJournal { StartDecision = new(status) };
            var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

            var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, Utf8.GetBytes("changed"))));

            Assert.Equal(status switch
            {
                MutationStartStatus.Deny => ResourceFailureKind.AuthorizationDenied,
                MutationStartStatus.AlreadyStarted => ResourceFailureKind.AmbiguousOutcome,
                _ => ResourceFailureKind.AuthorizationUnavailable
            }, result.Failure);
            Assert.Empty(journal.Completions);
            Assert.Equal("original", temp.Read("note.txt"));
        }
    }

    [Fact]
    public async Task Cancellation_after_start_before_first_write_records_NoMutation()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        using var caller = new CancellationTokenSource();
        var journal = new RecordingJournal { OnStart = _ => { caller.Cancel(); return ValueTask.CompletedTask; } };
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await patcher.WriteFileAsync(
            Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, Utf8.GetBytes("changed"))), caller.Token));

        Assert.Equal("original", temp.Read("note.txt"));
        Assert.Equal(MutationOutcome.NoMutation, Assert.Single(journal.Completions).Outcome);
        Assert.False(journal.Completions[0].ObservedVersion is null);
    }

    [Fact]
    public async Task Hardlink_attempt_inside_start_cannot_mutate_an_alias()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var aliasDirectory = temp.CreateSiblingDirectory("alias");
        var alias = Path.Combine(aliasDirectory, "outside-alias.txt");
        var aliasCreationSucceeded = false;
        var journal = new RecordingJournal
        {
            OnStart = request =>
            {
                _ = request;
                aliasCreationSucceeded = CreateHardLinkW(alias, Path.Combine(temp.Root, "note.txt"), IntPtr.Zero);
                return ValueTask.CompletedTask;
            }
        };
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        _output.WriteLine(aliasCreationSucceeded
            ? "CreateHardLinkW succeeded during Start; the writer must reject the new alias before mutation."
            : "CreateHardLinkW was blocked while the writer held the exclusive target handle.");
        if (aliasCreationSucceeded)
        {
            Assert.True(File.Exists(alias));
            Assert.Equal(ResourceFailureKind.Unsupported, result.Failure);
            Assert.Equal(MutationOutcome.NoMutation, Assert.Single(journal.Completions).Outcome);
            Assert.Equal("original", temp.Read("note.txt"));
            Assert.Equal("original", File.ReadAllText(alias));
        }
        else
        {
            Assert.False(File.Exists(alias));
            Assert.True(result.Succeeded, result.Failure?.ToString());
            Assert.Equal("changed", temp.Read("note.txt"));
        }
        Assert.Equal(8, Assert.Single(journal.Starts).OriginalByteLength);
    }

    [Fact]
    public async Task Existing_writable_mapping_blocks_patch_before_journal_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var file = new FileStream(Path.Combine(temp.Root, "note.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        file.Dispose(); // Prove the mapping itself blocks the exclusive writer open.
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.NotNull(result.Failure);
        Assert.Empty(journal.Starts);
        view.Dispose();
        mapping.Dispose();
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Failed_completion_after_write_reports_ambiguous_outcome()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var journal = new RecordingJournal { CompleteResult = false };
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Equal("changed", temp.Read("note.txt"));
        Assert.Equal(MutationOutcome.Completed, Assert.Single(journal.Completions).Outcome);
    }

    [Fact]
    public async Task Parent_rename_and_target_replacement_are_blocked_while_start_is_held()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("nested/note.txt", "original");
        var parent = Path.Combine(temp.Root, "nested");
        var target = Path.Combine(parent, "note.txt");
        var movedParent = Path.Combine(temp.Root, "moved");
        var backup = Path.Combine(temp.Root, "replacement.txt");
        var renameBlocked = false;
        var deleteBlocked = false;
        var replaceBlocked = false;
        var writeBlocked = false;
        var journal = new RecordingJournal
        {
            OnStart = _ =>
            {
                try { Directory.Move(parent, movedParent); }
                catch (IOException) { renameBlocked = true; }
                catch (UnauthorizedAccessException) { renameBlocked = true; }
                try { Directory.Delete(parent, recursive: true); }
                catch (IOException) { deleteBlocked = true; }
                catch (UnauthorizedAccessException) { deleteBlocked = true; }
                try { File.Move(target, backup); }
                catch (IOException) { replaceBlocked = true; }
                catch (UnauthorizedAccessException) { replaceBlocked = true; }
                try { File.WriteAllText(target, "intruder", Utf8); }
                catch (IOException) { writeBlocked = true; }
                catch (UnauthorizedAccessException) { writeBlocked = true; }
                return ValueTask.CompletedTask;
            }
        };
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "nested/note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.True(renameBlocked);
        Assert.True(deleteBlocked);
        Assert.True(replaceBlocked);
        Assert.True(writeBlocked);
        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal("changed", temp.Read("nested/note.txt"));
        Assert.False(Directory.Exists(movedParent));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task Preexisting_hardlink_is_rejected_without_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var alias = Path.Combine(temp.Root, "alias.txt");
        Assert.True(CreateHardLinkW(alias, Path.Combine(temp.Root, "note.txt"), IntPtr.Zero),
            "The temp NTFS volume must support hard links for this invariant test.");
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.Equal(ResourceFailureKind.Unsupported, result.Failure);
        Assert.Empty(journal.Starts);
        Assert.Equal("original", temp.Read("note.txt"));
        Assert.Equal("original", temp.Read("alias.txt"));
    }

    [Fact]
    public async Task Invalid_start_evidence_is_closed_as_no_mutation()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var journal = new RecordingJournal { StartDecision = new(MutationStartStatus.Started, "bad\nevidence") };
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.Equal(ResourceFailureKind.ProviderFailure, result.Failure);
        Assert.Equal(MutationOutcome.NoMutation, Assert.Single(journal.Completions).Outcome);
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Original_and_combined_read_budgets_are_enforced_before_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var originalJournal = new RecordingJournal();
        var tooSmallOriginal = Writer(temp.Root, new PermitAuthorizer(), originalJournal,
            new LocalPatchOptions(MaxOriginalBytes: 4, MaxReadBytes: 32, Namespace: LocalPatchNamespace.HostControlled));
        var originalResult = await tooSmallOriginal.WriteFileAsync(Request(temp.Invocation, "note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("x"))));
        Assert.Equal(ResourceFailureKind.TooLarge, originalResult.Failure);
        Assert.Empty(originalJournal.Starts);

        var totalJournal = new RecordingJournal();
        var tooSmallTotal = Writer(temp.Root, new PermitAuthorizer(), totalJournal,
            new LocalPatchOptions(MaxOriginalBytes: 8, MaxReadBytes: 12, Namespace: LocalPatchNamespace.HostControlled));
        var totalResult = await tooSmallTotal.WriteFileAsync(Request(temp.Invocation, "note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("replacement"))));
        Assert.Equal(ResourceFailureKind.TooLarge, totalResult.Failure);
        Assert.Empty(totalJournal.Starts);
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Request_identity_binds_a_snapshot_of_replacement_bytes()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var replacement = Utf8.GetBytes("changed");
        var request = Request(temp.Invocation, "note.txt", "original", new TextPatch(0, 8, replacement));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(request.Content, out var content));
        content.Array![content.Offset] = (byte)'X';
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(request);

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Empty(journal.Starts);
        Assert.Equal("original", temp.Read("note.txt"));
    }

    [Fact]
    public async Task Reparse_leaf_is_rejected_before_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("source.txt", "original");
        Assert.True(CreateSymbolicLinkW(Path.Combine(temp.Root, "link.txt"), Path.Combine(temp.Root, "source.txt"), 0x2 | 0x1),
            "This Windows test host must permit creation of a directory/file symbolic link.");
        var journal = new RecordingJournal();
        var patcher = Writer(temp.Root, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "link.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.True(result.Failure is ResourceFailureKind.AccessDenied or ResourceFailureKind.NotFound,
            $"A reparse leaf must fail closed; observed {result.Failure}.");
        Assert.Empty(journal.Starts);
        Assert.Equal("original", temp.Read("source.txt"));
    }

    [Fact]
    public async Task Reparse_workspace_root_is_rejected_before_start()
    {
        Assert.True(OperatingSystem.IsWindows(), "These provider tests exercise the Windows native handle profile.");
        using var temp = new TemporaryWorkspace();
        temp.Write("note.txt", "original");
        var linkRoot = temp.CreateSiblingDirectory("root-link");
        Directory.Delete(linkRoot);
        Assert.True(CreateSymbolicLinkW(linkRoot, temp.Root, 0x1 | 0x2),
            "This Windows test host must permit creation of a directory symbolic link.");
        var journal = new RecordingJournal();
        var patcher = Writer(linkRoot, new PermitAuthorizer(), journal, HostControlledOptions);

        var result = await patcher.WriteFileAsync(Request(temp.Invocation, "note.txt", "original",
            new TextPatch(0, 8, Utf8.GetBytes("changed"))));

        Assert.NotNull(result.Failure);
        Assert.Empty(journal.Starts);
        Assert.Equal("original", temp.Read("note.txt"));
    }

    private static IWorkspaceConditionalWriter Writer(string root, IResourceAuthorizer authorizer,
        IResourceMutationJournal journal, LocalPatchOptions? options = null)
    {
        options ??= HostControlledOptions;
        var provider = new LocalWorkspaceProvider(Workspace, root, options.Namespace);
        return provider.OpenWriter(authorizer, journal,
            new(options.MaxOriginalBytes, options.MaxReadBytes, options.TimeoutMilliseconds));
    }

    private static FileWriteRequest Request(HostInvocation invocation, string path, string original, params TextPatch[] patches)
    {
        var originalBytes = Utf8.GetBytes(original);
        var proposed = Utf8PatchMaterializer.Materialize(originalBytes, patches, 16 * 1024 * 1024);
        var version = new ResourceVersion("local-read-v1:sha256:" + Convert.ToHexString(SHA256.HashData(originalBytes)));
        var request = new FileWriteRequest(invocation, Workspace, new(path), proposed, new(16 * 1024 * 1024),
            new(WritePreconditionKind.MustMatchVersion, version));
        var identity = ResourceRequestIdentity.Compute(request);
        return request with { Invocation = invocation with { RequestIdentity = identity } };
    }

    private sealed class PermitAuthorizer(Func<ResourceAuthorizationRequest, ResourceAuthorizationDecision>? decide = null) : IResourceAuthorizer
    {
        internal List<ResourceAuthorizationRequest> Requests { get; } = [];
        public ValueTask<ResourceAuthorizationDecision> AuthorizeAsync(ResourceAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(decide?.Invoke(request) ?? new ResourceAuthorizationDecision(AuthorizationStatus.Permit));
        }
    }

    private sealed class RecordingJournal : IResourceMutationJournal
    {
        internal List<MutationStartRequest> Starts { get; } = [];
        internal List<MutationCompletion> Completions { get; } = [];
        internal MutationStartDecision StartDecision { get; set; } = new(MutationStartStatus.Started, "evidence-1");
        internal bool CompleteResult { get; set; } = true;
        internal Func<MutationStartRequest, ValueTask>? OnStart { get; init; }
        public async ValueTask<MutationStartDecision> StartAsync(MutationStartRequest request, CancellationToken cancellationToken = default)
        {
            Starts.Add(request);
            if (OnStart is not null) await OnStart(request);
            return StartDecision;
        }
        public ValueTask<bool> CompleteAsync(MutationCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            return ValueTask.FromResult(CompleteResult);
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "luban-patcher-" + Guid.NewGuid().ToString("N"));
        internal string Root { get; }
        internal HostInvocation Invocation { get; } = new("invocation", "subject", "effect", "attempt", "scope", null, new("pending"));
        internal TemporaryWorkspace() { Root = _path; Directory.CreateDirectory(Root); }
        internal void Write(string relative, string content)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, Utf8);
        }
        internal string CreateSiblingDirectory(string suffix)
        {
            var path = _path + "-" + suffix;
            Directory.CreateDirectory(path);
            return path;
        }
        internal string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative), Utf8);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var suffix in new[] { "-alias", "-root-link" })
            {
                try { Directory.Delete(_path + suffix, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkW(string symlinkFileName, string targetFileName, uint flags);

}
