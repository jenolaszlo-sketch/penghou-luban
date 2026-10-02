# ADR 0005: Deterministic diff and merge reuse Luban's patch boundary

Status: Accepted feature direction, 2026-10-02. Initial pure text diff/merge is implemented and [locally qualified](../text-change-profile.md); D3 canonical transport, unified display/import and exact pure materialization are implemented; authorized file changes, application bridge and opt-in language v2 are now qualified; directory/manifest profiles remain deferred. See the [current completion ledger](../leaf-completion.md).

Adopt the reviewed [diff/merge specification](../diff-merge-spec.md). Preserve the
[original proposal](../archive/diff-merge-proposal-2026-10-02.md) verbatim as historical
input; its approximate interfaces and command examples are not shipped APIs.

## Decision

Luban owns bounded pure text diff and three-way merge, neutral structured changes,
serialization, authorized file comparison/materialization and conformance.
There is no Git dependency, AI resolver, shell escape or new package split.
Explicit shared provider references supply file I/O; Hufu and workflow/runtime
types remain outside neutral contracts. Pure compute never writes a workspace.

Use byte-faithful strict UTF-8, deterministic line edits and explicit structured
conflicts in the initial profile. Algorithm versions, tie-breaking, bounds and
serialization rules are part of canonical identity. Freeze a separate schema;
do not rewrite language/preview/shared-resource v1 encodings.

Treat Diff, untrusted PatchCandidate, merged candidate and admitted execution plan
as distinct states. Materialize complete existing-file modifications into the
current `FilePatchStage` / capture-only preview path, then use the separate
single/batch executor contracts. No general `IPatchEngine.ApplyAsync` provider
may bypass whole-plan admission, live resource checks, locked-object binding or
mandatory start/outcome evidence.

Before applying to an existing file, require exact complete original SHA-256,
byte length and captured provider version; proposed bytes and hash must agree.
Unified imports need authorized exact materialization before becoming candidates
for admission. Display line coordinates cannot be passed to the byte-offset writer.
No fuzzy/context relocation, automatic reversal or unresolved-marker application.

The initial writer remains in-place and non-atomic. Multi-file atomicity requests
fail Unsupported. Sequential application needs explicit admission of the non-atomic
profile and preserves per-node outcome/uncertainty evidence. Create/delete/move,
rename, directory/snapshot and binary application wait for separate qualified
providers. Diff/merge must not add a second durability store or rollback mechanism.

## Delivery and consequences

The [D1–D7 plan](../implementation-plan.md#diff-and-merge-delivery-track)
starts with pure contracts/text diff and deterministic merge, then transport/import
validation, authorized file adapters, patch/execution bridging and optional language
descriptors. Application qualifies the neutral admission, live resource checks and mandatory
start/outcome contracts. Hufu adoption is optional and workflow durability
belongs outside Luban's completion criteria.

The reviewed scope tightens the proposal: source hashes are mandatory for actual
existing-file application; cross-workspace inputs have independent authorization;
merge/validation success confers no write authority; non-atomic partial effects
are explicit; and illustrative workflow syntax adds no control flow to Luban.
Directory/snapshot and advanced strategies follow stable file-level contracts.
