# Preview resolution and commit barrier

Status: Revised for the implemented capture-only preview and narrow exact-patch
execution profiles. The programmatic `PreviewCompiler` and
`PreviewRuntime.WhatIfAsync` capture authorized observations through the Windows
Local reader. Separate single-target and bounded 1–64 target batch executors
use whole-manifest admission and trusted-host receipt inspection. WhatIf has no
writer callback, adapter, or write API. `CaptureComplete` is not `CanCommit`,
which is always false. See the [batch execution profile](batch-execution-profile.md).
See the [capture profile](capture-preview-profile.md) and [single-patch execution profile](single-patch-execution-profile.md),
[semantic preview identity](semantic-preview.md), and [read profile](read-language-profile.md).
[ADR 0004](decisions/0004-preview-resolution-commit-barrier.md) accepts the
direction. The [original amendment](archive/preview-resolution-commit-barrier-proposal-2026-10-01.md)
is preserved verbatim.

## Execution stages and ownership

```text
Whole-document syntax/type/profile validation
  -> static preflight of known requirements and targets
  -> implemented Local-only authorized capture (`WhatIfAsync`)
  -> immutable capture plan with explicit coverage and unresolved nodes
  -> separate single-target executor (implemented; HostControlled namespace and host contract)
  -> separate bounded exact-target batch executor (implemented; trusted-host admission and receipt inspection)
  -> future governed Hufu/Zhinu adapters
```

Luban owns typed effect resolution and execution. Shared Penghou.IO.Abstractions
providers supply concrete I/O contracts; Hufu supplies authority through a host
adapter, and Zhinu or another host supplies durability. Standalone hosts may
implement equivalent admission explicitly. Luban does not acquire a Hufu core
dependency, a workflow scheduler or an execution sandbox.

Static preflight reads trusted authority state and mappings, not protected
target data. In the capture profile a known denied patch requirement blocks
before upstream listing/reads; the read language still has no textual write command.
Preview resolution is different: it performs actual authorized discovery reads
to determine dynamic targets and preconditions. Those reads may consume budget,
produce evidence and require data-release controls. This is not Hufu's pure
counterfactual policy simulation, which performs no protected resource effects.

## Implemented capture-only discovery and WhatIf

The programmatic `PreviewCompiler` and `PreviewRuntime.WhatIfAsync` are
Local-provider only. They support an exact existing-file typed UTF-8 scalar-safe
byte patch. There is no textual patch command in read-language version 1. For an
explicit authorized-view glob, freeze the selected target list and obtain
`TargetAdmission` for every selected target before any file-content reads. Do
not use a per-file interleaving of admission and content access. The required
`IPreviewAuthorizer` phases are Preflight, TargetAdmission, ResourceAccess,
ProposalAdmission, and Release. WhatIf reruns preflight.

All-match coverage is unavailable. If requested, return an unresolved plan before
protected target I/O. If a required target selection truncates, return an
incomplete plan with an unresolved node and no proposed target prefix. Repeated-
target mutations are unresolved. Any unresolved patch node caused by AllMatches,
incomplete selection, or repeated targets conservatively blocks every later node
with `DependsOnUnresolvedEffect` and causes zero later-node I/O. Deferred
Build/Test/Git.Commit nodes use `DependsOnOpaqueEffect` for dependent later nodes.
Initial profile v1 uses ordered segments without virtual state; it does not
evaluate later nodes against hypothetical patch results. Bound aggregate I/O,
work, plan size, and authorization calls. Preserve the provider's actual
ResourceVersion on each observation. Every deferred node and later nodes remain
unresolved.

Future Git.Status/Diff/Log/Show require their own strict helper/configuration,
incidental-write and excluded-content qualification before discovery use.
Read-looking names are insufficient. Capture previewability never grants write
permission or commit capability.

Preview captures proposed mutations instead of dispatching them. This profile
has no mutation barrier to cross and no writer entry point. It MUST NOT invoke
opaque/deferred effects, including Build, Test, Git.Commit, custom executable
code, mutating remote APIs, or later nodes dependent on those tools. Report them
unresolved; do not invoke them to discover behavior.
Trusted authority/evidence bookkeeping may still write under host policy. The
claim is no requested mutating effect performed, not zero host/disk/network I/O.

No target read or metadata probe bypasses authorization because it is called
preview. Root permission does not authorize descendants. References carry
provenance, never permission. The final Release action checks plan identity, node
identity, and protected observations; a denial returns no partial plan. The plan
retains only the copied exact TextPatch byte payload with offsets and replacement
text, not generated proposed full-file bytes. Original and proposed full-file
bytes are not retained or exposed. File observations expose only SHA, length,
and version. Diagnostics and plan rendering remain
release-protected; excluded names and content are not exposed.

## Resolved effect plan and identity

The implemented capture plan is immutable and captures completion separately from
commit readiness. `CaptureComplete` records a finished capture; `CanCommit` is
always false. Its identity binds:

- Document and language/IR/catalogue/provider versions, authenticated invocation,
  and ordered node identities.
- Protected observations and provider ResourceVersions, selected target identities,
  exact immutable TextPatch payloads/offsets, and dependency bindings.
- Selection coverage and unresolved nodes/reasons. Identity includes observations,
  targets, payloads, dependencies, and coverage.

An optional source fingerprint is diagnostic provenance, not approval identity.
Aliases/whitespace cannot change semantic identity, and changed targets,
payloads, order/dependencies, preconditions, coverage or versions cannot reuse
the same identity. See [semantic preview identity](semantic-preview.md) for the
current canonical representation. No durable Hufu/journal receipt is implied;
do not copy source or full-file contents into ordinary logs.

Authorize observation storage and later release separately. If a future replay
profile requires source content, it must retain protected snapshots/artifacts; a
hash alone cannot reconstruct content. The current capture stores no full-file
bytes. Apply explicit retention and cleanup policy to any protected artifacts.

## Coverage and dependency rules

Complete means all intended mutations in the admitted declared scope/authorized
view are resolved under the recorded versions and assumptions. It does not mean
all files on disk or all consequences of arbitrary code were examined. List and
Find may omit denied candidates; empty output never proves excluded files do
not exist. Do not expose hidden candidate counts or denial flags as proof.

The current capture profile supports an explicitly selected authorized view only.
All-match coverage is unavailable and returns an unresolved plan before protected
target I/O. Omitted candidates cannot establish all-match coverage. Do not expose
hidden targets or downgrade an all-match request to authorized-view selection.
The target list for an explicit authorized-view glob is frozen, and all selected
targets are admitted before any file-content read.

Traversal/output/time limits, provider errors, unknown requirements or missing
observations leave explicit incomplete coverage. A required truncated selection
returns an incomplete plan with an unresolved node and no proposed target prefix.
Never treat truncation as complete coverage.

Freeze the exact discovered target manifest and all dependent payloads before
admission. Commit must not rerun a glob and silently include new files. Added,
removed or changed targets affecting recorded selection assumptions invalidate
those assumptions; block/re-preview/re-admit rather than widen the plan. A plan
capture freezes a selected set; it does not claim a live-directory snapshot or
protect against concurrent path replacement. Stronger consistency requires the
separate writer/profile gate.

Reads depending on earlier proposed writes cannot observe their future state
by reading the current filesystem. Initial profile v1 uses ordered segments
without virtual state. Any unresolved earlier patch node caused by AllMatches,
incomplete selection, or repeated target marks every later node
`DependsOnUnresolvedEffect` and causes zero later-node I/O. Opaque deferred tools
mark dependent nodes with `DependsOnOpaqueEffect`. No virtual filesystem or
arbitrary-code simulation is selected. Repeated mutations of the same target
remain unresolved; no intermediate-version semantics are claimed.

## Classification and unresolved effects

| Descriptor category | Meaning and handling |
| --- | --- |
| CapturedPatch | Exact existing-file target and typed TextPatch payload are captured; this is descriptive only and cannot be committed by the current profile. |
| FullyPreviewable | Future qualified profile where exact target, payload and preconditions can be resolved; it does not imply a writer exists today. |
| DiscoveryOnly | Qualified bounded observations used to resolve other effects; authorized before each protected access. |
| PartiallyPreviewable | Known requirements/targets with remaining consequences explicit; never claim full coverage from a command name. |
| LazyOnly | A qualified effect cannot be fully resolved ahead of execution; remains an explicit unresolved node. |

Descriptors define the category for their exact supported profile. Explicit path
alone does not prove a fully resolved payload, link target or transitive effect.
An unresolved node records effect identity, known requirements/scope ceilings,
dependencies, reason and required mode/provider guarantees. Unknown effects,
denials, missing guarantees, authorization outages or exhausted resolution
budgets MUST NOT be reclassified as LazyOnly to bypass blocking.

In actual execution, a supported explicitly admitted unresolved effect resolves
its concrete targets as far as possible and passes fresh effect/resource checks.
It cannot run during WhatIf. ApprovedTools retains its explicit broader trust;
preview does not make build/test code strict or prove its nested effects.

An unresolved tool that may change state splits dependent later work into a new
segment. Resolve and admit that segment after the earlier effect finishes;
previous assumptions do not authorize freshly discovered targets. The presence
of a lazy node cannot weaken the barrier for previewable known mutations.

## Implemented bounded exact-patch batch execution

The separate `BatchPatchExecutor` accepts a complete ordered manifest of 1–64
distinct exact-target patch nodes from a fully captured plan. It rejects
incomplete/unresolved nodes, selected globs and deferred tools; partial grants
never turn into an allowed prefix. One host admission binds the entire manifest.
Every remaining target's rights are checked before readiness reads, and every
remaining original version and proposed result is verified before the first
write. Each write still repeats live authority and exact-version/object checks.
The operation is sequential and non-atomic: a late denial or provider failure
can follow earlier completed writes, and later nodes stop. There is no rollback.

Before new target access, recovery uses a trusted host's authoritative ordered
receipt snapshot. Only a fully matching contiguous Completed prefix is skipped.
Uncertain or NoMutation entries block without automatic retry; a new explicit
attempt requires fresh capture and admission. Each call is one separately
admitted segment; an optional predecessor must be verified by the host, and
state-dependent follow-on work needs a fresh capture. No durable journal/store
or governed host adapter is implemented in this repository. The exact contract,
limits, receipt fields and current qualification boundaries are in the
[batch execution profile](batch-execution-profile.md).

The Local NTFS patcher is enabled only for a trusted HostControlled namespace.
The observed hard-link race, in-place write crash limits, and lack of general
filesystem confinement are documented in the [single-patch execution profile](single-patch-execution-profile.md)
and [native profile](../../Penghou/docs/local-patch-profile.md).

## Delivery and qualification

Capture-only Local resolution, immutable payload capture, explicit coverage,
deferred-tool handling, final-release checks, and semantic identity are implemented.
The standalone exact-target writer and narrow exact-target batch/recovery
contract are implemented. Broader selected-target execution, mutation forms,
durable recovery, and governed Hufu/Zhinu execution remain future work; no
multi-file atomicity is claimed.

Required tests cover known downstream denial before discovery, unauthorized
preview reads, dynamic denied mutation sets with zero writes, truncation blocked
before commit, no lazy tool dispatch in WhatIf, manifest drift and changed
payloads, write-dependent discovery, partial grants, revocation after admission,
stale versions, post-start partial failures, restart and ambiguous outcomes.
The narrow batch-specific qualification status appears in the [batch execution profile](batch-execution-profile.md).
The CLI `luban --whatif` is an optional later rendering of this typed mechanism,
not an implemented command or an alternative source of authority.

## Diff and merge candidates

[ADR 0005](decisions/0005-deterministic-diff-merge.md) introduces read-only diff,
deterministic merge and candidate validation. Pure computed or imported changes
are not resolved/admitted plans. Authorized materialization binds explicit targets,
complete original/proposed digests and lengths, captured provider versions and
scalar-safe byte edits before existing capture/approval. Unsupported kinds,
unresolved conflicts and required incomplete coverage block proposed writes.
An explicitly chosen subset is a new candidate needing fresh identity/admission.

The later bridge uses FilePatchStage and existing separate single/batch executors.
WhatIf remains capture-only, CanCommit stays false, and no diff/merge callback
dispatches a writer. Exact known-set admission and live locked-object checks
remain mandatory; prior validation is not permission or a stale-state exemption.
Non-atomic batches and uncertain receipts retain their current profile semantics.
The [diff/merge specification](diff-merge-spec.md) adds no rollback, atomicity or
general confinement guarantee.
