using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.IO.Local;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Xunit;

namespace Penghou.Luban.Execution.Tests;

public sealed class BatchPatchExecutorTests
{
    [Fact]
    public async Task CapturedPayloadBeyondRetainedPlanBudgetIsRejectedBeforeAdmission()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var limits = new PreviewLimits(MaxReadBytes: 16384, MaxFileBytes: 8192,
            MaxReplacementBytes: 8192, MaxPlanBytes: 8192);
        var original = new ImmutableBytes(Enumerable.Repeat((byte)'o', 5000).ToArray());
        var proposed = new ImmutableBytes(Enumerable.Repeat((byte)'p', 5000).ToArray());
        var patch = new FrozenTextPatch(0, original.Length, new ImmutableBytes(proposed.ToArray()));
        var operation = new ExactPatchOperation("a.txt", Array.AsReadOnly(new[] { patch }), null);
        var documentIdentity = PreviewIdentity.Document(workspace.Id, limits, new PreviewOperation[] { operation });
        var nodeIdentity = PreviewIdentity.Node(documentIdentity, 0);
        var document = new CompiledPreviewDocument(workspace.Id, limits, documentIdentity,
            new[] { new CompiledPreviewNode(0, nodeIdentity, operation) });
        var invocation = new EffectInvocation("subject", "effect", "oversized-plan");
        var proposal = new CapturedFilePatch("a.txt", new ResourceVersion("opaque-v1"),
            original.Sha256, original.Length, proposed.Sha256, proposed.Length,
            Array.AsReadOnly(new[] { patch }), original, proposed);
        var node = new ResolvedPreviewNode(nodeIdentity, "files.patch", PreviewNodeState.Proposed,
            PreviewSelection.Unspecified, Array.AsReadOnly(new[] { proposal }), Array.Empty<string>(), null);
        var observation = new PreviewObservation(nodeIdentity, "a.txt", ResourceAction.ReadFile,
            new RequestIdentity("request-v1"), new ResourceVersion("opaque-v1"), original.Sha256, original.Length, true);
        var nodes = new[] { node };
        var observations = new[] { observation };
        var identity = PreviewIdentity.Plan(invocation, document, observations, nodes);
        var forgedPlan = new ResolvedEffectPlan(invocation, document, identity, observations, nodes);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(document, forgedPlan, "oversized-retained-plan");

        Assert.Equal(ResourceFailureKind.InvalidRequest, result.Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Empty(host.ResourceChecks);
        Assert.Empty(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task WholeBatchDenialAndLastTargetDenialPerformNoReadsOrWrites()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")]);

        var admissionHost = new RecordingHost { AdmissionStatus = LanguageAuthorityStatus.Deny };
        var admissionDenied = await Executor(workspace, admissionHost).ExecuteAsync(capture.Document, capture.Plan, "deny-batch");
        Assert.Equal(ResourceFailureKind.AuthorizationDenied, admissionDenied.Failure);
        Assert.Empty(admissionHost.ResourceChecks);
        Assert.Empty(admissionHost.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        Assert.Equal(old, workspace.ReadBytes("b.txt"));

        var targetHost = new RecordingHost
        {
            ResourceDecision = check => check.Resource.Action == ResourceAction.PatchFile && PathOf(check) == "b.txt"
                ? AuthorizationStatus.Deny : AuthorizationStatus.Permit
        };
        var targetDenied = await Executor(workspace, targetHost).ExecuteAsync(capture.Document, capture.Plan, "deny-last-target");
        Assert.Equal(ResourceFailureKind.AuthorizationDenied, targetDenied.Failure);
        Assert.Contains(targetHost.ResourceChecks, c => c.Resource.Action == ResourceAction.PatchFile && PathOf(c) == "a.txt");
        Assert.Contains(targetHost.ResourceChecks, c => c.Resource.Action == ResourceAction.PatchFile && PathOf(c) == "b.txt");
        Assert.DoesNotContain(targetHost.ResourceChecks, c => c.Resource.Action == ResourceAction.ReadFile &&
            c.Resource.SnapshotRequestIdentity != c.Admission.ResourceRequestIdentity);
        Assert.Empty(targetHost.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        Assert.Equal(old, workspace.ReadBytes("b.txt"));
    }

    [Fact]
    public async Task MissingInspectionSnapshotFailsClosedBeforeProtectedIo()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var host = new RecordingHost { ReturnNullInspection = true };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "missing-snapshot");

        Assert.Equal(ResourceFailureKind.AuthorizationUnavailable, result.Failure);
        Assert.Empty(host.ResourceChecks);
        Assert.Empty(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task AllTargetRightsAndVersionChecksPrecedeFirstWrite()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")]);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "ordered");

        Assert.True(result.Succeeded, $"{result.Failure}: {string.Join(" | ", host.Events)}");
        Assert.Null(host.LastAdmission!.Predecessor);
        Assert.Equal(new[] { "new", "new" }, new[] { Text(workspace, "a.txt"), Text(workspace, "b.txt") });
        var firstStart = host.Events.FindIndex(e => e.StartsWith("start:", StringComparison.Ordinal));
        Assert.True(firstStart > 0);
        var patchChecks = host.ResourceEvents.Where(item => item.Check.Resource.Action == ResourceAction.PatchFile &&
            item.Check.Resource.SnapshotRequestIdentity == item.Check.Admission.ResourceRequestIdentity).ToArray();
        Assert.Equal(2, patchChecks.Select(item => PathOf(item.Check)).Distinct().Count());
        var bPreRead = host.ResourceEvents.First(item => item.Check.Resource.Action == ResourceAction.ReadFile &&
            PathOf(item.Check) == "b.txt" && item.Check.Resource.SnapshotRequestIdentity != item.Check.Admission.ResourceRequestIdentity);
        Assert.All(patchChecks.GroupBy(item => PathOf(item.Check)), group =>
            Assert.True(group.Min(item => item.Index) < bPreRead.Index));
        Assert.True(bPreRead.Index < firstStart);
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(BatchRecoveryState.Completed, e.State));
    }

    [Fact]
    public async Task StaleLaterTargetPreventsEveryWrite()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        var changed = Encoding.UTF8.GetBytes("BAD");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")]);
        workspace.WriteBytes("b.txt", changed);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "stale-second");

        Assert.Equal(ResourceFailureKind.PreconditionFailed, result.Failure);
        Assert.Empty(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        Assert.Equal(changed, workspace.ReadBytes("b.txt"));
    }

    [Fact]
    public async Task RevocationAfterCompletedPrefixStopsAndRestartSkipsPrefix()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")]);
        var denyBWriterCheck = true;
        var bPatchChecks = 0;
        var host = new RecordingHost
        {
            ResourceDecision = check =>
            {
                if (check.Resource.Action == ResourceAction.PatchFile && PathOf(check) == "b.txt")
                {
                    bPatchChecks++;
                    if (denyBWriterCheck && bPatchChecks > 1) return AuthorizationStatus.Deny;
                }
                return AuthorizationStatus.Permit;
            }
        };

        var first = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "resume-segment");

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, first.Failure);
        Assert.Equal("new", Text(workspace, "a.txt"));
        Assert.Equal("old", Text(workspace, "b.txt"));
        Assert.Single(host.Starts);
        Assert.Equal(BatchRecoveryState.Completed, Assert.Single(first.Entries, e => e.State == BatchRecoveryState.Completed).State);
        Assert.Equal(BatchRecoveryState.NotStarted, Assert.Single(first.Entries, e => e.State == BatchRecoveryState.NotStarted).State);

        denyBWriterCheck = false;
        var restartedHost = host.CloneForRestart();
        var resumed = await Executor(workspace, restartedHost).ExecuteAsync(capture.Document, capture.Plan, "resume-segment");

        Assert.True(resumed.Succeeded);
        Assert.Equal("new", Text(workspace, "a.txt"));
        Assert.Equal("new", Text(workspace, "b.txt"));
        Assert.DoesNotContain(restartedHost.Starts, s => s.Path == "a.txt");
        Assert.Contains(restartedHost.Starts, s => s.Path == "b.txt");
        Assert.All(resumed.Entries, e => Assert.Equal(BatchRecoveryState.Completed, e.State));
    }

    [Fact]
    public async Task InvalidRecoveryReceiptBlocksBeforeResourceAccess()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var host = new RecordingHost();
        var first = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "receipt-segment");
        Assert.True(first.Succeeded);
        // Simulate a host returning a tampered durable receipt after restart.
        var snapshot = host.SavedSnapshot!;
        var receipt = snapshot.Entries[0].Completion ?? throw new Xunit.Sdk.XunitException("Expected persisted receipt.");
        var bad = snapshot.Entries[0] with { Completion = receipt with
        { Start = receipt.Start with { Path = new WorkspacePath("other.txt") } } };
        host.SavedSnapshot = snapshot with { Entries = Array.AsReadOnly(new[] { bad }) };
        workspace.WriteBytes("a.txt", old);
        var restarted = host.CloneForRestart();

        var result = await Executor(workspace, restarted).ExecuteAsync(capture.Document, capture.Plan, "receipt-segment");

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Empty(restarted.ResourceChecks);
        Assert.Empty(restarted.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task CompletedRecoveryReceiptAcceptsBoundedMultibyteEvidenceAndSkipsStart()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var evidence = string.Concat(Enumerable.Repeat("界", 200)); // 200 UTF-16 chars, 600 UTF-8 bytes.
        var host = new RecordingHost { StartDecision = _ => new(MutationStartStatus.Started, evidence) };

        var first = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "unicode-evidence");
        Assert.True(first.Succeeded);
        var restart = host.CloneForRestart();

        var resumed = await Executor(workspace, restart).ExecuteAsync(capture.Document, capture.Plan, "unicode-evidence");

        Assert.True(resumed.Succeeded);
        Assert.Empty(restart.ResourceChecks);
        Assert.Empty(restart.Starts);
        Assert.Equal("new", Text(workspace, "a.txt"));
    }

    [Fact]
    public async Task ReorderedOrMalformedRecoveryVectorBlocksBeforeResourceAccess()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")]);
        var host = new RecordingHost
        {
            Inspection = request => new BatchRecoverySnapshot(request.SegmentId, request.Plan.Identity,
                Array.AsReadOnly(request.Operations.Reverse().Select((operation, reversedIndex) =>
                {
                    var nodeIndex = request.Operations.Count - 1 - reversedIndex;
                    return new BatchRecoveryEntry(operation.OperationId,
                        request.Document.Nodes[nodeIndex].Identity, BatchRecoveryState.NotStarted);
                }).ToArray()))
        };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "bad-vector");

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Empty(host.ResourceChecks);
        Assert.Empty(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        Assert.Equal(old, workspace.ReadBytes("b.txt"));
    }

    [Fact]
    public async Task CompletionAcknowledgmentFailureReturnsUncertainEntryWithoutReceiptPayload()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var host = new RecordingHost { CompleteDecision = _ => false };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "uncertain-completion");

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(BatchRecoveryState.Uncertain, entry.State);
        Assert.Null(entry.Completion);
        Assert.Equal(Encoding.UTF8.GetBytes("new"), workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task CancellationAfterStartedRetainsAcknowledgedNoMutationReceipt()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        using var cancellation = new CancellationTokenSource();
        var host = new RecordingHost { OnStart = _ => cancellation.Cancel() };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "cancel-after-start", null, cancellation.Token);

        Assert.Equal(ResourceFailureKind.ProviderFailure, result.Failure);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(BatchRecoveryState.NoMutation, entry.State);
        Assert.Equal(MutationOutcome.NoMutation, entry.Completion?.Outcome);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));

        var restartedHost = host.CloneForRestart();
        var retry = await Executor(workspace, restartedHost).ExecuteAsync(capture.Document, capture.Plan, "cancel-after-start");
        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, retry.Failure);
        Assert.Empty(restartedHost.ResourceChecks);
        Assert.Empty(restartedHost.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task LostStartReplyPersistsUncertainAndBlocksLaterWritesAndRestartIo()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")]);
        var host = new RecordingHost { ThrowAfterStart = true };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "lost-start-reply");

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Equal(BatchRecoveryState.Uncertain, Assert.Single(result.Entries, e => e.State == BatchRecoveryState.Uncertain).State);
        Assert.Single(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        Assert.Equal(old, workspace.ReadBytes("b.txt"));

        var restarted = host.CloneForRestart();
        var retry = await Executor(workspace, restarted).ExecuteAsync(capture.Document, capture.Plan, "lost-start-reply");
        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, retry.Failure);
        Assert.Empty(restarted.ResourceChecks);
        Assert.Empty(restarted.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        Assert.Equal(old, workspace.ReadBytes("b.txt"));
    }

    [Theory]
    [InlineData(MutationStartStatus.AlreadyStarted)]
    [InlineData(MutationStartStatus.Unavailable)]
    public async Task UnknownOrPriorStartedDecisionPoisonsRestart(MutationStartStatus status)
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var host = new RecordingHost { StartDecision = _ => new(status) };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "start-uncertain-" + status);

        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, result.Failure);
        Assert.Equal(BatchRecoveryState.Uncertain, Assert.Single(result.Entries).State);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
        var restarted = host.CloneForRestart();
        var retry = await Executor(workspace, restarted).ExecuteAsync(capture.Document, capture.Plan, "start-uncertain-" + status);
        Assert.Equal(ResourceFailureKind.AmbiguousOutcome, retry.Failure);
        Assert.Empty(restarted.ResourceChecks);
        Assert.Empty(restarted.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task SegmentIdCannotBeReusedForDifferentPlan()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var firstCapture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var host = new RecordingHost();
        var first = await Executor(workspace, host).ExecuteAsync(firstCapture.Document, firstCapture.Plan, "immutable-segment");
        Assert.True(first.Succeeded);
        var changedBytes = Encoding.UTF8.GetBytes("new");
        var conflictingCapture = await Capture(workspace, [Patch("a.txt", changedBytes, "BAD")]);
        var restarted = host.CloneForRestart();

        var result = await Executor(workspace, restarted).ExecuteAsync(conflictingCapture.Document, conflictingCapture.Plan, "immutable-segment");

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Empty(restarted.ResourceChecks);
        Assert.Empty(restarted.Starts);
        Assert.Equal(changedBytes, workspace.ReadBytes("a.txt"));

        var predecessorDrift = await Executor(workspace, restarted).ExecuteAsync(firstCapture.Document, firstCapture.Plan,
            "immutable-segment", new BatchPredecessor("some-other-segment", new string('a', 64)));
        Assert.Equal(ResourceFailureKind.AuthorizationDenied, predecessorDrift.Failure);
        Assert.Empty(restarted.ResourceChecks);
        Assert.Empty(restarted.Starts);
        Assert.Equal(changedBytes, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task RealSuccessorSegmentBindsCompletedPredecessorIdentity()
    {
        using var workspace = new TestWorkspace();
        var original = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", original);
        var firstCapture = await Capture(workspace, [Patch("a.txt", original, "new")]);
        var host = new RecordingHost();
        var first = await Executor(workspace, host).ExecuteAsync(firstCapture.Document, firstCapture.Plan, "segment-1");
        Assert.True(first.Succeeded);
        var predecessor = new BatchPredecessor("segment-1", firstCapture.Plan.Identity);

        var afterFirst = Encoding.UTF8.GetBytes("new");
        var secondCapture = await Capture(workspace, [Patch("a.txt", afterFirst, "done")]);
        var second = await Executor(workspace, host).ExecuteAsync(secondCapture.Document, secondCapture.Plan,
            "segment-2", predecessor);

        Assert.True(second.Succeeded);
        Assert.Equal(predecessor, host.LastAdmission!.Predecessor);
        Assert.Equal("done", Text(workspace, "a.txt"));
        Assert.Equal(2, host.Starts.Count);
    }

    [Fact]
    public async Task HostCanRejectSegmentDependencyBeforeResourceAccess()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var predecessor = new BatchPredecessor("previous", new string('a', 64));
        var host = new RecordingHost
        {
            AdmissionDecision = request => request.Predecessor == predecessor
                ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Permit
        };

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "dependent-segment", predecessor);

        Assert.Equal(ResourceFailureKind.AuthorizationDenied, result.Failure);
        Assert.Equal(predecessor, host.LastAdmission!.Predecessor);
        Assert.Empty(host.ResourceChecks);
        Assert.Empty(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task InvalidUnicodeSegmentAndPredecessorAreRejectedWithoutHostCalls()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace, [Patch("a.txt", old, "new")]);
        var host = new RecordingHost();
        var executor = Executor(workspace, host);

        var badSegment = await executor.ExecuteAsync(capture.Document, capture.Plan, "segment-\ud800");
        var badPredecessor = await executor.ExecuteAsync(capture.Document, capture.Plan, "good-segment",
            new BatchPredecessor("previous-\ud800", new string('A', 64)));

        Assert.Equal(ResourceFailureKind.InvalidRequest, badSegment.Failure);
        Assert.Equal(ResourceFailureKind.InvalidRequest, badPredecessor.Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Empty(host.ResourceChecks);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    [Fact]
    public async Task AggregateReadAndCallBudgetsFailBeforeProtectedIo()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        workspace.WriteBytes("b.txt", old);

        // Execution budgets two original reads plus one proposed file per target.
        var byteLimited = await Capture(workspace, [Patch("a.txt", old, "new"), Patch("b.txt", old, "new")],
            new PreviewLimits(MaxReadBytes: 12, MaxFileBytes: 3));
        var bytesHost = new RecordingHost();
        var bytesResult = await Executor(workspace, bytesHost).ExecuteAsync(byteLimited.Document, byteLimited.Plan, "aggregate-bytes");
        Assert.Equal(ResourceFailureKind.TooLarge, bytesResult.Failure);
        Assert.Equal(1, bytesHost.AdmissionCalls);
        Assert.Empty(bytesHost.ResourceChecks);

        // Static single-patch minimum is 9; batch accounting includes provider readiness,
        // semantic patch authorization, conditional write, and start/outcome calls.
        var callsLimited = await Capture(workspace, [Patch("a.txt", old, "new")],
            new PreviewLimits(MaxResourceCalls: 9));
        var callsHost = new RecordingHost();
        var callsResult = await Executor(workspace, callsHost).ExecuteAsync(callsLimited.Document, callsLimited.Plan, "aggregate-calls");
        Assert.Equal(ResourceFailureKind.TooLarge, callsResult.Failure);
        Assert.Equal(0, callsHost.AdmissionCalls);
        Assert.Empty(callsHost.ResourceChecks);
    }

    [Theory]
    [InlineData("a.txt", 19)]
    [InlineData("dir/a.txt", 22)]
    public async Task ExactMinimumCallBudgetSucceedsAndOneLessRejectsBeforeAdmission(string path, int exactCalls)
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes(path, old);
        var stages = new PreviewStage[] { Patch(path, old, "new") };
        var below = await Capture(workspace, stages,
            new PreviewLimits(MaxReadBytes: 9, MaxFileBytes: 3, MaxResourceCalls: exactCalls - 1));
        var belowHost = new RecordingHost();
        var belowResult = await Executor(workspace, belowHost).ExecuteAsync(below.Document, below.Plan,
            "tight-calls-below-" + exactCalls);

        Assert.Equal(ResourceFailureKind.TooLarge, belowResult.Failure);
        Assert.Equal(0, belowHost.AdmissionCalls);
        Assert.Empty(belowHost.ResourceChecks);
        Assert.Empty(belowHost.Starts);
        Assert.Equal("old", Text(workspace, path));

        var exact = await Capture(workspace, stages,
            new PreviewLimits(MaxReadBytes: 9, MaxFileBytes: 3, MaxResourceCalls: exactCalls));
        var host = new RecordingHost();
        var result = await Executor(workspace, host).ExecuteAsync(exact.Document, exact.Plan, "tight-calls-" + exactCalls);

        Assert.True(result.Succeeded, $"{result.Failure}: {string.Join(" | ", host.Events)}");
        Assert.Equal(exactCalls, host.AdmissionCalls + host.InspectionCalls + host.ResourceChecks.Count +
            host.Starts.Count + host.CompletionCalls);
        Assert.Equal("new", Text(workspace, path));
    }

    [Fact]
    public async Task AggregateReadBudgetRequiresBothOriginalAndProposedBytes()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var stages = new PreviewStage[] { Patch("a.txt", old, "new") };
        var below = await Capture(workspace, stages,
            new PreviewLimits(MaxReadBytes: 8, MaxFileBytes: 3, MaxResourceCalls: 19));
        var belowHost = new RecordingHost();
        var tooSmall = await Executor(workspace, belowHost).ExecuteAsync(below.Document, below.Plan, "tight-bytes-below");

        Assert.Equal(ResourceFailureKind.TooLarge, tooSmall.Failure);
        Assert.Equal(1, belowHost.AdmissionCalls);
        Assert.Equal(1, belowHost.InspectionCalls);
        Assert.Empty(belowHost.ResourceChecks);
        Assert.Empty(belowHost.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));

        var exact = await Capture(workspace, stages,
            new PreviewLimits(MaxReadBytes: 9, MaxFileBytes: 3, MaxResourceCalls: 19));
        var exactHost = new RecordingHost();
        var succeeds = await Executor(workspace, exactHost).ExecuteAsync(exact.Document, exact.Plan, "tight-bytes-exact");
        Assert.True(succeeds.Succeeded, $"{succeeds.Failure}: {string.Join(" | ", exactHost.Events)}");
        Assert.Equal("new", Text(workspace, "a.txt"));
    }

    [Fact]
    public async Task UnsupportedGlobPlanIsRejectedBeforeAdmission()
    {
        using var workspace = new TestWorkspace();
        var old = Encoding.UTF8.GetBytes("old");
        workspace.WriteBytes("a.txt", old);
        var capture = await Capture(workspace,
            [new GlobPatchStage("", "*.txt", PreviewSelection.AuthorizedView,
                [new TextPatch(0, 3, Encoding.UTF8.GetBytes("new"))], new TraversalLimits())]);
        var host = new RecordingHost();

        var result = await Executor(workspace, host).ExecuteAsync(capture.Document, capture.Plan, "glob-unsupported");

        Assert.Equal(ResourceFailureKind.Unsupported, result.Failure);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Empty(host.ResourceChecks);
        Assert.Empty(host.Starts);
        Assert.Equal(old, workspace.ReadBytes("a.txt"));
    }

    private static FilePatchStage Patch(string path, byte[] original, string replacement) =>
        new(path, [new TextPatch(0, original.Length, Encoding.UTF8.GetBytes(replacement)),], Version(original));

    private static async Task<(CompiledPreviewDocument Document, ResolvedEffectPlan Plan)> Capture(
        TestWorkspace workspace, IReadOnlyList<PreviewStage> stages, PreviewLimits? limits = null)
    {
        var compilation = PreviewCompiler.Compile(stages, workspace.Id, limits);
        var document = compilation.Document ?? throw new Xunit.Sdk.XunitException("Expected preview document to compile: " +
            string.Join(",", compilation.Diagnostics.Select(d => d.Code)));
        var invocation = new EffectInvocation("subject", "effect", Guid.NewGuid().ToString("N"));
        var preview = await new PreviewRuntime(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), new PermitPreviewAuthority())
            .WhatIfAsync(invocation, document);
        return (document, preview.Plan ?? throw new Xunit.Sdk.XunitException($"Expected a resolved preview, got {preview.Status}."));
    }

    private static BatchPatchExecutor Executor(TestWorkspace workspace, RecordingHost host) =>
        new(new WorkspaceReference(workspace.Id.Value), TestLocalProvider.Create(workspace.Id.Value, workspace.Root), host);

    private static string PathOf(PatchResourceCheck check) => check.Resource.Resource switch
    {
        ResourceBinding.WorkspaceFile file => file.Path.Value,
        ResourceBinding.WorkspaceEntry entry => entry.Path.Value,
        ResourceBinding.WorkspaceDirectory directory => directory.Path.Value,
        _ => "?"
    };

    private static string Text(TestWorkspace workspace, string path) => Encoding.UTF8.GetString(workspace.ReadBytes(path));
    private static ResourceVersion Version(byte[] bytes) => new("local-read-v1:sha256:" + Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed class PermitPreviewAuthority : IPreviewAuthorizer
    {
        public ValueTask<LanguageAuthorityDecision> AuthorizeAsync(PreviewAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
    }

    private sealed class RecordingHost : IBatchPatchExecutionHost
    {
        private readonly Dictionary<string, BatchAdmissionRequest> _bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BatchRecoverySnapshot> _journal = new(StringComparer.Ordinal);
        private readonly HashSet<(string SegmentId, string OperationId)> _startClaims = [];
        public List<string> Events { get; } = [];
        public List<PatchResourceCheck> ResourceChecks { get; } = [];
        public List<(int Index, PatchResourceCheck Check)> ResourceEvents { get; } = [];
        public List<(string OperationId, string Path)> Starts { get; } = [];
        public int AdmissionCalls { get; private set; }
        public int InspectionCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public BatchAdmissionRequest? LastAdmission { get; private set; }
        public BatchRecoverySnapshot? SavedSnapshot { get; set; }
        public Func<BatchAdmissionRequest, BatchRecoverySnapshot?>? Inspection { get; init; }
        public bool ReturnNullInspection { get; init; }
        public LanguageAuthorityStatus AdmissionStatus { get; init; } = LanguageAuthorityStatus.Permit;
        public Func<BatchAdmissionRequest, LanguageAuthorityStatus> AdmissionDecision { get; init; } = request => LanguageAuthorityStatus.Permit;
        public Func<PatchResourceCheck, AuthorizationStatus> ResourceDecision { get; init; } = _ => AuthorizationStatus.Permit;
        public Func<PatchStartRequest, MutationStartDecision> StartDecision { get; init; } = _ => new(MutationStartStatus.Started, "batch-start-evidence");
        public Func<PatchCompletion, bool> CompleteDecision { get; init; } = _ => true;
        public Action<PatchStartRequest>? OnStart { get; init; }
        public bool ThrowAfterStart { get; init; }

        public ValueTask<LanguageAuthorityDecision> AdmitAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default)
        {
            AdmissionCalls++; LastAdmission = request; Events.Add("admit");
            var status = AdmissionStatus == LanguageAuthorityStatus.Permit ? AdmissionDecision(request) : AdmissionStatus;
            if (status != LanguageAuthorityStatus.Permit) return ValueTask.FromResult(new LanguageAuthorityDecision(status));
            if (_bindings.TryGetValue(request.SegmentId, out var bound))
            {
                if (!SameBinding(bound, request)) return ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Deny));
            }
            else
            {
                if (request.Predecessor is { } predecessor &&
                    (!_bindings.TryGetValue(predecessor.SegmentId, out var priorBinding) ||
                     priorBinding.Plan.Identity != predecessor.PlanIdentity ||
                     !_journal.TryGetValue(predecessor.SegmentId, out var priorSnapshot) ||
                     priorSnapshot.Entries.Any(entry => entry.State != BatchRecoveryState.Completed)))
                    return ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Deny));
                _bindings.Add(request.SegmentId, request);
            }
            return ValueTask.FromResult(new LanguageAuthorityDecision(LanguageAuthorityStatus.Permit));
        }

        public ValueTask<BatchRecoverySnapshot?> InspectAsync(BatchAdmissionRequest request, CancellationToken cancellationToken = default)
        {
            InspectionCalls++;
            Events.Add("inspect");
            if (ReturnNullInspection) return ValueTask.FromResult<BatchRecoverySnapshot?>(null);
            if (Inspection is not null) return ValueTask.FromResult(Inspection(request));
            if (SavedSnapshot is { } saved && saved.SegmentId == request.SegmentId) return ValueTask.FromResult<BatchRecoverySnapshot?>(saved);
            if (_journal.TryGetValue(request.SegmentId, out var persisted)) return ValueTask.FromResult<BatchRecoverySnapshot?>(persisted);
            var initial = request.Operations.Select((operation, index) => new BatchRecoveryEntry(operation.OperationId,
                request.Document.Nodes[index].Identity, BatchRecoveryState.NotStarted)).ToArray();
            return ValueTask.FromResult<BatchRecoverySnapshot?>(new BatchRecoverySnapshot(request.SegmentId,
                request.Plan.Identity, Array.AsReadOnly(initial)));
        }

        public ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(BatchAdmissionRequest request,
            PatchResourceCheck check, CancellationToken cancellationToken = default)
        {
            ResourceChecks.Add(check);
            ResourceEvents.Add((Events.Count, check));
            Events.Add($"resource:{check.Resource.Action}:{PathOf(check)}");
            return ValueTask.FromResult(new ResourceAuthorizationDecision(ResourceDecision(check)));
        }

        public ValueTask<MutationStartDecision> StartAsync(BatchAdmissionRequest request, PatchStartRequest start,
            CancellationToken cancellationToken = default)
        {
            var index = request.Operations.ToList().FindIndex(o => o.OperationId == start.Admission.OperationId);
            if (index < 0 || !_startClaims.Add((request.SegmentId, start.Admission.OperationId)))
                return ValueTask.FromResult(new MutationStartDecision(MutationStartStatus.AlreadyStarted));
            var path = request.Plan.Nodes[index].Proposals[0].RelativePath;
            var state = CurrentSnapshot(request);
            SetState(request, state, index, BatchRecoveryState.Uncertain, null);
            Starts.Add((start.Admission.OperationId, path)); Events.Add("start:" + path);
            OnStart?.Invoke(start);
            if (ThrowAfterStart) throw new IOException("Simulated lost start reply after durable intent.");
            var decision = StartDecision(start);
            if (decision.Status == MutationStartStatus.Deny)
            {
                _startClaims.Remove((request.SegmentId, start.Admission.OperationId));
                SetState(request, CurrentSnapshot(request), index, BatchRecoveryState.NotStarted, null);
            }
            return ValueTask.FromResult(decision);
        }

        public ValueTask<bool> CompleteAsync(BatchAdmissionRequest request, PatchCompletion completion,
            CancellationToken cancellationToken = default)
        {
            CompletionCalls++;
            Events.Add("complete:" + completion.Admission.OperationId);
            if (!CompleteDecision(completion)) return ValueTask.FromResult(false);
            var index = request.Operations.ToList().FindIndex(o => o.OperationId == completion.Admission.OperationId);
            if (index < 0) return ValueTask.FromResult(false);
            var state = completion.Mutation.Outcome switch
            {
                MutationOutcome.Completed => BatchRecoveryState.Completed,
                MutationOutcome.NoMutation => BatchRecoveryState.NoMutation,
                _ => BatchRecoveryState.Uncertain
            };
            SavedSnapshot = SetState(request, CurrentSnapshot(request), index, state, completion.Mutation);
            return ValueTask.FromResult(true);
        }

        private BatchRecoverySnapshot CurrentSnapshot(BatchAdmissionRequest request)
        {
            if (SavedSnapshot is { } saved && saved.SegmentId == request.SegmentId) return saved;
            if (_journal.TryGetValue(request.SegmentId, out var stored)) return stored;
            var entries = request.Operations.Select((operation, index) => new BatchRecoveryEntry(operation.OperationId,
                request.Document.Nodes[index].Identity, BatchRecoveryState.NotStarted)).ToArray();
            return new(request.SegmentId, request.Plan.Identity, Array.AsReadOnly(entries));
        }

        private BatchRecoverySnapshot SetState(BatchAdmissionRequest request, BatchRecoverySnapshot snapshot,
            int index, BatchRecoveryState state, MutationCompletion? completion)
        {
            var entries = snapshot.Entries.ToArray();
            entries[index] = new(request.Operations[index].OperationId, request.Document.Nodes[index].Identity, state, completion);
            var updated = snapshot with { Entries = Array.AsReadOnly(entries) };
            _journal[request.SegmentId] = updated;
            SavedSnapshot = updated;
            return updated;
        }

        private static bool SameBinding(BatchAdmissionRequest left, BatchAdmissionRequest right) =>
            left.Plan.Identity == right.Plan.Identity && left.Document.Identity == right.Document.Identity &&
            left.Predecessor == right.Predecessor &&
            left.Operations.Select(operation => operation.OperationId).SequenceEqual(right.Operations.Select(operation => operation.OperationId));

        internal RecordingHost CloneForRestart()
        {
            var clone = new RecordingHost
            {
                SavedSnapshot = SavedSnapshot,
                Inspection = Inspection,
                ResourceDecision = ResourceDecision,
                AdmissionDecision = AdmissionDecision,
                StartDecision = StartDecision,
                CompleteDecision = CompleteDecision,
                ThrowAfterStart = ThrowAfterStart
            };
            foreach (var (segmentId, binding) in _bindings) clone._bindings.Add(segmentId, binding);
            foreach (var (segmentId, snapshot) in _journal) clone._journal.Add(segmentId, snapshot);
            foreach (var startClaim in _startClaims) clone._startClaims.Add(startClaim);
            return clone;
        }
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "luban-batch-executor-" + Guid.NewGuid().ToString("N"));
        internal WorkspaceId Id { get; } = new("batch-executor-test-workspace");
        internal string Root => _root;
        internal TestWorkspace() => Directory.CreateDirectory(_root);
        internal void WriteBytes(string relative, byte[] bytes)
        {
            var root = Path.GetFullPath(_root);
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test path escaped its temporary workspace.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        internal byte[] ReadBytes(string relative) => File.ReadAllBytes(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
