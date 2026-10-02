# Exact-patch batch qualification

Date: 2026-10-01. This follows the [single-file audit](audit-2026-10-01.md)
and qualifies the narrow [batch profile](batch-execution-profile.md), not
production Hufu enforcement or a durable journal implementation.

## Delivered behavior

- A complete frozen manifest admits every exact-file mutation together before
  protected I/O. Every remaining target's required rights are checked before
  readiness content reads; all remaining versions and recomputed patches are
  verified before the first write.
- The original single-file executor remains a single-file public API. The batch
  path reuses its locked-object, live-rights, required-start and completion
  boundary with the original full document/plan identities. It shares one host
  call budget and checks aggregate readiness/original/verification bytes.
- Stable segment and per-node IDs support authoritative host inspection. Only
  a valid completed prefix is skipped. Uncertain or already-started operations
  and confirmed no-mutation starts are never automatically dispatched again.
- Freshly captured successor segments carry the exact predecessor segment/plan
  binding for the host to verify. Conflicting segment reuse blocks access.
- Partial execution returns acknowledged receipts. Failed or lost completion
  acknowledgments return uncertainty without releasing the unacknowledged
  receipt payload. Cancellation preserves a confirmed no-mutation receipt.

## Review corrections

The review corrected an unsafe initial interpretation of a null recovery
snapshot as an empty journal. Absence now means AuthorizationUnavailable;
NotStarted must be an explicit entry for every node. Receipt validation uses the
actual MutationCompletion and exact request/invocation bindings rather than
reference equality against a newly reconstructed admission object.

Predecessor validation now accepts canonical lowercase plan hashes while file
hashes remain uppercase. Token checks reject malformed UTF-16 without escaping
as an exception and preserve the neutral contract's 1024-byte UTF-8 ceiling;
bounded multibyte evidence therefore remains valid after restart. Replacement,
node, observation and plan bounds apply before defensive copies/comparisons.
Resource bridges validate the concrete workspace and separately encoded read
identity as well as the patch scope.

## Validation and limits

The native Windows tests use real temporary workspaces and the existing Local
reader/NTFS patcher. Test hosts record starts before replies, reject conflicting
segment/operation reuse, and retain outcome vectors across new executor/host
instances. Cases exercise known denial, stale later targets, revocation after a
completed prefix, lost start replies, unavailable/already-started outcomes,
missing acknowledgments, cancellation, malformed/reordered receipts, Unicode
evidence and IDs, explicit successor admission, and tight aggregate bounds.

`dotnet test Penghou.Luban.sln -c Release --no-restore` passed **167/167 tests
on net8.0 and 167/167 on net10.0, with no skips**. The 22 new batch cases include
exact successful ceilings: a direct file uses 15 host calls, one nested directory
uses 18, and a three-byte original/postimage uses nine aggregate read bytes.
One fewer call blocks before admission; an eight-byte read ceiling blocks before
target access. Existing shared provider code was unchanged; its earlier 81/81
native qualification is referenced rather than represented as a new run here.

The restart cases copy simulated host journal state; they do not terminate a
process or test storage after power loss. Native namespace/handle guarantees
remain those of the existing controlled-namespace profile. Late changes and
native qualification failures can leave a completed prefix. In-place writes
remain vulnerable to tearing on crash, with no rollback or atomic batch claim.
Selected globs, deferred tools, textual mutations, production reconciliation,
Hufu governance and Zhinu durable fencing still require their own gates.
