# Bounded exact-patch batches and recovery

Status: Implemented narrow standalone profile, 2026-10-01. Qualification is recorded in [the batch audit](batch-qualification-2026-10-01.md). `BatchPatchExecutor` executes an
ordered complete captured manifest containing 1–64 distinct exact-file patches.
It uses the existing [single-file boundary](single-patch-execution-profile.md)
and [Local NTFS patcher](../../Penghou/docs/local-patch-profile.md). Selected
globs, incomplete coverage, unresolved nodes and deferred tools cannot execute.
WhatIf remains read-only and `CanCommit` remains false. Textual mutation syntax
is not added to the read language.

## Admission and readiness

The executor validates and freezes the compiled document and captured plan,
including node order, dependencies, exact targets, byte payloads and observations.
Every concrete mutation request is included in one `BatchAdmissionRequest`.
Required `IBatchPatchExecutionHost.AdmitAsync` admits the entire manifest before
protected target access. Partial grants require a newly captured, explicitly
admitted plan; the executor does not select an allowed prefix.

After authoritative recovery inspection, it checks the patch, read and ancestry
metadata rights of **every remaining target before reading any file contents**.
It then reads and verifies every remaining original version, hash and length and
recomputes the captured postimage. A known denied or stale later target therefore
blocks the first write. These readiness reads retain no full-file content in
the manifest or result. The document bounds apply across the entire call: the
aggregate readiness read, locked original read and post-write verification must
fit `MaxReadBytes`; host admission, inspection, resource checks, starts and
completion acknowledgments share `MaxResourceCalls`. One document deadline covers
the operation; started operations retain the provider's independent bounded
completion timeout.

Readiness uses a separately encoded bounded `FileReadRequest`. Its resource
check carries that read request's canonical identity, while its enclosing patch
admission retains the concrete mutation identity. Both bind the same authenticated
operation and full-plan scope. Hosts must validate this exact derived-read
relationship; neither identity can substitute for the other.

Readiness is an observation, not a reservation. Each eventual mutation still
rechecks live rights and binds the current qualified target to its exact locked
object and expected original version. A later version change, denial, native
qualification failure or provider outage may follow earlier completed writes.
Execution stops at the first failure and retains attributable per-node outcomes.
There is no rollback, atomic multi-file transaction, or automatic retry.

## Required host journal and recovery

There is no built-in authority or durable store. The trusted host authenticates
invocation and workspace bindings and binds each stable segment ID to its exact
plan, writer profile, namespace and predecessor. Conflicting reuse must be
rejected even when a changed plan would produce different node operation IDs.
Node operation IDs frame the domain `Penghou.Luban.BatchOperation.v1`, segment
ID and compiled node identity as strict UTF-8, each preceded by a signed 32-bit
little-endian byte length. The ID is `batch-` followed by the uppercase SHA-256
of those frames. Segment/evidence tokens permit at most 256 UTF-16 code units
and 1024 UTF-8 bytes, without control characters or malformed Unicode.
Predecessor plan identities use the existing canonical lowercase hex encoding.
IDs establish identity only.

`InspectAsync` returns an authoritative ordered entry for every manifest node.
An unavailable, malformed, reordered, conflicting or incomplete snapshot blocks
execution. `NotStarted` must mean the host has no committed start for that exact
operation, rather than a missing record treated as permission. The host must
authorize current release of retained receipts before returning them. A host
cannot infer completion merely from the target's current content hash.

The executor skips only a contiguous `Completed` prefix with valid acknowledged
receipts matching the concrete invocation/request, target, provider profile,
original/proposed versions and lengths, evidence, and verified resulting version.
It neither rereads those original contents nor dispatches those mutations again.
Any `Uncertain` or `NoMutation` entry blocks new target access; a completed entry
after an uncompleted predecessor is inconsistent. Uncertain starts must be
reconciled by the host before further work. `NoMutation` does not make an already
started operation retryable: a new explicit attempt requires fresh capture and
admission. Remaining observations and rights are rechecked on every resume.

Start atomically checks current admission, approval, revision/fence and revocation
and commits mandatory start evidence. Concurrent executors may observe the same
not-started snapshot; the serialized host start boundary must prevent duplicate
dispatch. The executor's in-memory bookkeeping is not a distributed lock.
Completion records `Completed`, `NoMutation`, or uncertainty through the same
host. Missing acknowledgment is uncertain even when bytes were verified. The
host's completion callback must also govern release of receipt details. Receipt
retention does not itself confer permission to render protected data later.
Unacknowledged completion payloads are omitted from results. Cancellation returns
a structured stopped result retaining acknowledged earlier outcomes; a confirmed
`NoMutation` receipt remains distinct from an uncertain start.

## Separately admitted segments

Each call is one segment. An optional `BatchPredecessor` binds the preceding
segment ID and plan identity. Admission must verify that exact predecessor has
fully completed under the host's current workflow/fence protocol; callers cannot
declare it complete. A later state-dependent segment needs its own newly compiled
and captured document after the predecessor finishes. This API does not slice an
unresolved preview, execute opaque tools, or implement workflow branching.

## Qualification limits

The profile requires the trusted host's explicit `HostControlled` namespace
selection. It inherits the single-file provider's hard-link race limitation and
in-place writes that can tear on crash. Native qualification of enabled
case-sensitive directories remains unverified on this host. Test journals exercise
receipt binding, restart snapshots, partial failures and missing acknowledgments;
they do not demonstrate power-loss durability, production Hufu enforcement,
Zhinu fencing or resistance to privileged filesystem interference.
Serialized plan/receipt import is not provided. A durable adapter must pin and
validate its persisted schema and executor/provider versions and authenticate
the retained evidence before constructing these runtime objects.
