# Single exact-target patch execution

Status: Implemented narrow standalone profile, 2026-10-01. Qualification results
are recorded in the implementation audit. This is separate from capture-only
WhatIf. `ResolvedEffectPlan.CanCommit` stays false; no preview callback dispatches
the executor.

Trusted composition must explicitly select `LocalPatchNamespace.HostControlled`
in the executor constructor. The default/unknown provider namespace and an
unspecified executor namespace return Unsupported before protected I/O or host
admission. The admission request binds this selection and the writer profile
`local-windows-ntfs-controlled-patch-v1`. This is a host environment assurance,
not authority a source command can request. Untrusted actors must not be able
to create aliases or change root/mount/reparse/case configuration during the
operation. A native test demonstrated that CreateHardLink can succeed despite
exclusive file sharing; the subsequent link-count check rejected that observed
attempt, but cannot close all alias races. Stronger confinement is unsupported.

`SinglePatchExecutor.ExecuteAsync(document, plan, operationId)` accepts exactly
one complete `FilePatchStage` node and its one captured existing-file proposal.
It rejects incomplete, selected-glob, multi-target and deferred-tool plans. It
recomputes document/node/plan identities and checks the exact compiled path,
patch offsets/replacement bytes, dependency order, original version/hash/length,
and read observation against the proposal. An unkeyed plan hash alone is not
admission. Proposed bytes are recomputed by the writer and the proposed hash and
length must match the capture before start.

The constructor requires a trusted `IPatchExecutionHost`. No default permit or
test journal ships. Whole-plan `AdmitAsync` runs before provider access. Every
provider resource request carries the exact admission, invocation, scope and
concrete request identity. The private bridge accepts only the exact file's
ReadFile/PatchFile actions and metadata for its qualified ancestry. A read,
FileRef, capture success or operation ID confers no authority.

The writer prepares one locked object, verifies the exact original content
version, validates and computes the bounded UTF-8 patch, then repeats live
PatchFile authorization. `StartAsync` receives the immutable plan/admission,
writer profile, exact concrete request, locked object identity, before/after
versions and lengths. The host **must serialize current admission, approvals,
revision/fence and revocation against this start and commit mandatory start
evidence before returning Started**. Admission is not a cached permit. Start is
the documented ordering point: a later revocation cannot undo a begun operation.
The host must enforce any stronger cancellation requirement through its own
serialized operation protocol.

Missing/unknown/denied/malformed start evidence blocks byte mutation.
`AlreadyStarted` blocks dispatch and requires reconciliation; no retry or
replay shortcut exists. Stable `operationId` reuse/conflicts are the trusted
host's responsibility across executor instances and restarts. The executor
does not implement a second authority, journal or durable idempotency store.

The provider records NoMutation, Completed or Ambiguous through `CompleteAsync`
using a bounded independent cancellation token. An acknowledged completion is
required for success; completion failure after a write returns AmbiguousOutcome,
including when the bytes themselves were verified. A started operation can
have NoMutation if cancellation arrives before the first write. Any failure
after writing may have begun is ambiguous and never implies rollback. Callers
must reconcile before attempting another operation. Successful completion
reveals only the resulting content version, not file contents.

The document file/read ceilings, deadline and authorization-call ceiling apply to execution; the
bridge reserves calls for start and outcome evidence. OS filesystem calls are
bounded by bytes and cooperative checks, not a hard interruptible deadline.
The original plus verification reads must fit the admitted aggregate read
budget; an impossible captured plan is rejected before admission. Outcome
evidence has its own five-second timeout after start so a cancelled caller
does not silently discard the receipt.

The [Local patch profile](../../Penghou/docs/local-patch-profile.md) defines the
supported native object binding, content-version semantics and limitations.
An in-place patch can tear on crash and is not an atomic replacement or a
multi-file transaction. The current reader's path-based capture does not prove
that the original captured native file object survived: its version is a content
precondition. At execution the provider binds the current qualified target,
checks those exact bytes and mutates that same locked object.

Hufu policy/store/admission and Zhinu durable journal/fence/recovery adapters
remain pending. Test hosts demonstrate ordering and denial, not production
durability or distributed fencing. A separate [bounded batch executor](batch-execution-profile.md)
now supports complete exact-target manifests and trusted-host recovery snapshots.
Textual mutation syntax, unified hunks, selected-glob execution and production
reconciliation remain later work.
