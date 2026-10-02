# Existing-file candidate application

The D5 bridge connects a bounded immutable `FilePatchCandidate` set to Luban's
existing capture and single/batch executors. It never opens a writer. Candidates
are computation results, not admitted plans or authorization tokens.

`FileChangeCaptureBridge` requires the logical workspace, registered provider and
an explicit `IPreviewAuthorizer`. `CaptureAsync` snapshots a set of 1–64 candidates,
checks workspace and retained-content/replacement bounds before copying payloads,
then compiles exact `FilePatchStage` operations with the observed opaque versions.
The current preview profile permits at most 128 aggregate edits. Unsupported
edit counts or mixed unchanged/changed sets return no plan. An entirely unchanged
set returns `NoChanges` without dispatch or authorization calls. That describes the supplied proposals; it is not a fresh live equality check.

Fresh preview preflight and whole-set target admission precede all content reads.
The bridge compares every recaptured original and proposed hash, length and
version with the candidate before releasing its document/plan pair. A stale
destination, denied later target, invalid payload, incomplete capture or denied
release returns no plan. Version equality never substitutes for hash equality.
Captured plans retain owned original/proposed bytes under catalogue v2's bounds.

Pass a successful pair separately to `SinglePatchExecutor` or
`BatchPatchExecutor`. Their host must supply fresh plan admission, live concrete
resource checks, serialized start evidence and required outcome recording.
Hufu may implement those contracts; no authority library or workflow engine is
required by Luban. Missing or ambiguous outcomes preserve the existing recovery
rules. Neither candidates nor `CaptureComplete` confer write permission;
`CanCommit` remains false on capture-only plans.

The profile supports strict UTF-8 modifications of existing files in one
workspace. It does not add create/delete/rename, fuzzy application, conflict-marker
application, rollback, automatic retry or atomic multi-file writes. A provider
must explicitly support the selected persistence guarantee profile. The Windows
Local writer requires the documented controlled NTFS namespace.

```csharp
var changes = await new FileChangeRuntime(workspace, provider, changeAuthorizer)
    .DiffAsync(invocation, new(workspace.Id), new("target.txt", "proposed.txt"));
if (changes.Status == FileChangeStatus.Succeeded)
{
    var captured = await new FileChangeCaptureBridge(workspace, provider, previewAuthorizer)
        .CaptureAsync(invocation, changes.Candidates);
    if (captured.Status == FileChangeCaptureStatus.Succeeded)
        await new SinglePatchExecutor(workspace, provider, executionHost)
            .ExecuteAsync(captured.Document!, captured.Plan!, operationId);
}
```

The host supplies invocation, authorizers, execution host and operation ID. The
example does not install a permissive policy or interpret a clean diff as approval.

Qualification includes real Local diff→capture→single execution and candidate-set
capture→batch execution, stale later targets, denial before content reads, hash
mismatch despite equal version tokens, result-release denial, no-op behavior and
retention limits. See the [completion ledger](leaf-completion.md) for test evidence.
