# Luban implementation plan

## Luban completion boundary

Luban is the language and its runtime for bounded AI tool effects. Its completion
criteria cover syntax, typed operations, analysis/preflight, provider execution,
preview/application semantics, documentation and conformance. Hosts inject
required authorization and journal contracts; those contracts do not require
Hufu or a workflow engine.

Hufu is an optional authorization integration with its own qualification and
release work. Zhinu owns workflow durability outside Luban and is not a Luban
implementation milestone, dependency or completion gate. A host may use it
independently. Preserve required admission, live resource checks, start/outcome
evidence and explicit uncertain results without selecting a particular host.


## Resource-abstraction correction — 2026-10-02

Follow the [resource-abstractions architecture](../../Penghou/docs/resource-abstractions-architecture.md) and
[public-type inventory](../../Penghou/docs/resource-abstractions-inventory.md).
The corrected provider injection, opaque versions and pure patch materialization
are implemented and candidate-package qualified. See the selected
[ADR 0003](../../Penghou/docs/decisions/0003-replaceable-resource-providers.md).
Published-package adoption remains pending under RA-5C; historical Local slices
below remain regression evidence.

- [x] **RA-1/RA-3 (candidate qualification):** inject narrow IO contracts into file, language and preview
  runtimes; isolate physical roots, Local constructors and Local-specific public
  executor configuration in host/integration composition.
- [x] **RA-G4:** keep diff/merge/text materialization in Luban and qualify a
  conditional persistence contract before replacing Local's locked patch path.
  Preserve semantic admission, exact bytes/versions, final checks and outcomes.
- [ ] **RA-5C:** consume the corrected published IO packages through pinned
  PackageReferences, remove normal-build sibling-source dependencies, and verify
  neutral dependency/API closure plus read/preview/execution integration.
  See the [CI/publication/adoption sequence](../../Penghou/docs/resource-abstractions-corrective-plan.md).
- [ ] **VFS-5, deferred:** normal execution over an injected snapshot/overlay
  provider as a separate explicit mode. Existing capture-only WhatIf remains
  read-only with CanCommit=false.
- [ ] **VFS-7, deferred:** apply an approved immutable delta only through fresh
  real-resource admission, version checks, durable starts and reconciliation.

Do not add Hufu policy, a VFS implementation, HTTP/process redesign or broad
package migration to this correction. Each handoff cites its RA/VFS gate.

Status: Revised delivery order, 2026-10-02. Steps 1–6 have implemented narrow
profiles: shared bounded Local reader, minimal language/IR, capture-only WhatIf,
standalone exact-target executor, and bounded exact-target batch execution with
trusted-host recovery inspection. Optional authority and workflow integrations have separate completion gates. The controlled namespace profile does not establish general
alias confinement or atomic multi-file writes. See the [runtime](typed-effect-runtime.md),
[language](language-syntax-spec.md), [preview/commit](preview-resolution-commit-barrier.md),
[single-patch execution profile](single-patch-execution-profile.md), and
[batch execution profile](batch-execution-profile.md).

## Current delivery status

The first six implementation steps have landed as narrow profiles. The next
work is published IO adoption and the D4–D7 language/runtime delivery track.
Resolve implementation details within those bounds and record decisions; block
only unsupported guarantees.

The delivered foundation is a real Windows Local reader and separate single-file
and narrow batch writer/executors. WhatIf remains read-only and cannot dispatch
either. Host-supplied admission and recovery snapshots are required; no durable
store or production host integration is implied. Next work qualifies the
authorized file/application primitives and broader language profiles.

## Delivery order and ownership

| Step | Concrete delivery | Owner and prerequisite |
| --- | --- | --- |
| 1 | Resource identity/profile and real Windows read-only provider | Penghou; existing I/O interfaces, no parser/Hufu dependency |
| 2 | Existing Read/Find/SearchText through shared provider | Luban; step 1 |
| 3 | Typed IR, minimal read language and complete static preflight — complete | Luban; stable descriptors/identity and migrated reads |
| 4 | Capture-only WhatIf; Local read-only, no callback/dispatch — complete | Luban; steps 2–3 and exact capture contracts |
| 5 | Standalone exact-target patch executor over Local NTFS patcher — implemented | Penghou Local + Luban; required host admission/start/journal contract |
| 6 | Narrow exact-target batches and trusted-host recovery inspection — implemented | Luban/host; qualified single-file execution |
| Optional integration | Hufu authorization adapter | Hufu-owned qualification; not a Luban completion gate |
| D1–D7 | Independent diff/merge delivery track; pure text first, authorized files/import/application later | Luban; [ADR 0005](decisions/0005-deterministic-diff-merge.md), existing provider/execution boundaries |

Hufu contracts/store work may proceed independently. Local development uses
explicit host-supplied policy/journal contracts and test fixtures; no permit-by-
default implementation ships. Controlled-policy tests do not prove real Hufu
enforcement or production revocation/durability.

## Step 1: contract and read-only provider

Freeze a versioned canonical resource-request encoding with domain-separated
digests, operation kind, normalized target, limits/preconditions and payload
bytes. Provide one neutral pure codec shared by trusted caller and provider,
with equivalence/conflict vectors. Exclude the digest itself to avoid recursive
hashing and bind authenticated invocation/scope separately. Use one codec;
do not reinterpret the existing direct API JSON digest. Keep semantic effect,
resolved plan and backend
request identities distinct; host mappings retain parent effect/scope ceilings.

Implement IWorkspaceReader only: bounded bytes, file metadata/existence and
authorized-only direct directory pages. Pin a Windows profile with path/case/
encoding rules, version semantics, byte/candidate/output/work limits, continuation
binding and observed reparse-point rejection. The current interface has no
directory-stat operation; keep it out rather than inventing file metadata.
List supports directory checks needed for Find/Search traversal.

Authorize each read/probe and candidate before type/metadata inspection or
disclosure. Denial and unavailable authorizers remain distinct and never fall
through to local access. Root permission does not grant descendants. Source
cannot choose machine roots. State replacement-race, hard-link and mount limits
until stronger binding is demonstrated; the provider is not a sandbox.

Gate: real temporary-workspace tests for content/versions, root/child exclusions,
bounded denied/no-match scans, paging, cancellation, malformed/unknown context,
invalid paths and observed links on .NET 8 and 10. No shell or mutation route is
introduced by the first provider; mutation guarantees require their own gate.

## Step 2: migrate existing reads

Keep FileEffectRuntime's required IEffectAuthorizer above resource authorization.
Adapt authenticated subject/effect/attempt and host scope mappings to concrete
read/list requests. Find cannot confer content-read/write authority. Parent
requirements and concrete rights both apply; child digests are not parent
SearchText digests.

Use bounded ListDirectory pages and ReadFile observations for Find/SearchText.
Preserve current results: UTF-8 Read content/byte length/hash, Find paths, and
SearchText path/line/text plus truncation. Preserve recursive basename-pattern
semantics in the legacy API; language path-globs need separate qualification.
Do not invent context, columns, ranges or richer FileRef fields.

Move target I/O out of Luban handlers, sharing path qualification, paging and
resource checks. Pin the shared repository revision and document a reproducible
sibling-checkout integration build initially; never add an unavailable NuGet
reference. Qualified package publication is a separate delivery decision.

Gate: existing Luban behavior tests against the real provider plus parent/child
identity conflicts, denied metadata/candidates, input snapshot identity, limits
and unavailable authorization. Keep unsupported strong guarantees unavailable.

## Step 3: IR, language and static preflight — complete

The current [read profile](read-language-profile.md) defines the implemented
bounded source surface. It supports read, find, literal search, take, and count;
the [semantic IR profile](semantic-ir.md) records current versions and canonical
identity. The runtime requires `ILanguageAuthorizer` checks at Preflight,
EffectStart, ResourceAccess, and Release. Direct legacy effect calls continue to
require `IEffectAuthorizer`.

Language Find/search use root-relative `*`, `?`, and whole-segment `**` path
globs with bounded matching. Legacy direct Find/SearchText keep recursive
basename-pattern behavior unchanged. `take` and `count` are the only transforms;
the larger transform catalogue, ranges, richer search, and mutation syntax remain
pending. This stage implements bounded execution, not lazy streaming or
backpressure guarantees.

## Step 4: capture-only WhatIf — complete

The programmatic `PreviewCompiler` and `PreviewRuntime.WhatIfAsync` use the
qualified Windows Local reader only. They capture an existing-file typed UTF-8
scalar-safe byte patch; there is no textual patch command in read-language
version 1. Freeze explicit-authorized-view glob targets first, then obtain
admission for every selected target before any file-content reads. All-match
coverage is unavailable: return an incomplete plan with an unresolved node and
zero file-content reads, never a proposed prefix.

`Build`, `Test`, and `Git.Commit` are deferred tools; use
`DependsOnOpaqueEffect` for dependent later nodes. AllMatches, incomplete
selection, and repeated-target patch nodes block every later node with
`DependsOnUnresolvedEffect` and zero later-node I/O. Initial v1 uses ordered
segments without virtual state. Bound aggregate I/O, work, plan size, and
authorization calls. Each actual provider read preserves its ResourceVersion.
Copy only the exact immutable TextPatch byte payload with offsets and replacement
bytes into the plan. Capture catalogue v2 retains owned immutable original and proposed bytes for
exact materialization checks, within the aggregate plan budget. Their release
is protected; observations also bind hashes, lengths, and opaque versions. `IPreviewAuthorizer` uses Preflight,
TargetAdmission, ResourceAccess, ProposalAdmission, and Release; WhatIf reruns
preflight.

Plan identity binds document and versions, authenticated invocation, node order,
observations, targets, payloads, dependencies, and coverage. `CaptureComplete`
records completion of capture only; `CanCommit` is always false. The preview
profile exposes no writer callback, adapter, or write API. The final Release
action checks plan identity, node identity, and protected observations; denial
returns no partial plan. See [capture profile](capture-preview-profile.md) and
[semantic preview identity](semantic-preview.md).

## Step 5: controlled exact-target patcher and executor — implemented

The separate `SinglePatchExecutor` accepts one complete exact-target captured
plan. It validates compiled path/payload and observation alignment, binds the
concrete request to fresh whole-plan admission, and requires current authority,
revision/fence and committed start evidence at the locked-object boundary.
`LocalWorkspaceWriter` uses the same exclusive NTFS file handle for version
verification, conditional persistence, flush and postimage checks. Luban owns
strict UTF-8 patch materialization before that boundary. Required completion evidence
reports NoMutation, Completed or Ambiguous; document bounds apply throughout.
WhatIf remains read-only and CanCommit stays false.

Native tests showed CreateHardLink can succeed during exclusive file sharing.
The observed attempt was rejected before mutation by a subsequent link-count
check, which cannot close all alias races. Default/unknown namespaces return
Unsupported before I/O; trusted composition must explicitly select HostControlled
and ensure untrusted actors cannot change aliases, mounts or directory
configuration. General race-proof confinement is unsupported. Case-sensitive
directory enablement could not be exercised on this host. See the
[native profile](../../Penghou/docs/local-patch-profile.md) and
[execution profile](single-patch-execution-profile.md).

The following requirements preserve the rationale and later qualification gates.

Qualify one existing-file byte patch with exact original version, bounded ordered
nonoverlapping UTF-8 scalar-safe changes, exact payload and source scope. The
shared TextPatch uses original-version byte offsets. A trusted bounded adapter
must translate textual unified hunks against the exact authorized snapshot,
with defined line endings and no fuzzy application. Programmatic byte patches
may deliver first; textual patch syntax waits for conversion qualification.

Define source --hash comparison and the provider's opaque ResourceVersion
binding to the actual committed object/version. A hash check then unrelated
path write is insufficient. Prove a supported object-bound check/version/mutation
protocol, state concurrent-writer guarantees, and return unsupported when required
consistency is unavailable. Do not weaken the guarantee to make a demo work.

Admit the whole one-mutation plan and pin identities, profiles and approvals.
Coordinate current authority, revision/fence and mandatory start evidence at
an executor barrier. The host supplies tested admission/start ordering and an
operation journal contract; absent required guarantees block real dispatch.
Recheck actual I/O and barrier probes. WhatIf cannot release a barrier or change
providers. Cleanup remains a separate authorized effect.

Gate: denied/stale plans and failed preconditions cause zero mutation. Changed
payload/target/version cannot reuse admission. Revocation between preflight,
resolution, admission and start obeys documented ordering. Cancellation/failure
after possible commit records actual or ambiguous outcome. Test native binding
and concurrent writers, not only mocked writes.

## Step 6: narrow batches and trusted-host recovery inspection — implemented

`BatchPatchExecutor` accepts a complete captured manifest with 1–64 distinct
exact-file patches. It rejects incomplete/unresolved plans, selected-glob plans,
deferred tools, and duplicate targets; it does not execute an allowed prefix.
Whole-manifest admission and all remaining resource checks precede readiness
reads; every remaining original version and proposed result is validated before
the first write. Each mutation still repeats live authorization, exact-version
checking, and the required locked-object start journal. Aggregate read, plan,
call, target, and deadline bounds apply across the batch.

The executor inspects one authoritative ordered receipt entry per manifest node.
Only a fully matching contiguous Completed prefix is skipped. Uncertain,
NoMutation, malformed or conflicting states stop without access or automatic
retry. Retry after NoMutation requires a new explicit attempt with fresh capture
and admission. A call is one separately admitted segment; an optional predecessor
must be verified by the host, and state-dependent later work requires fresh
capture. Receipts and recovery inspection are host-supplied; no durable store is
implemented here.

The batch is sequential and non-atomic. On any failure execution stops and
retains per-node outcomes; earlier completed writes are not rolled back. It does
not claim power-loss durability, production authority enforcement, distributed fencing,
or general filesystem namespace confinement. See the
[batch execution profile](batch-execution-profile.md) for the contract and gates.

## Optional authorization integration

Luban accepts explicit host-selected authorizers and required start/outcome
journal contracts. Implementations must preserve exact request/resource binding,
current checks, evidence failures and uncertain outcomes. Luban qualifies those
neutral contracts and provider behavior without depending on a specific authority
library or durable workflow runtime.

Hufu can supply authorization through its optional adapters. Its policy, grants,
approvals, credential lifecycle and production host qualification belong in
[Hufu's integration plan](../../Penghou.Hufu/docs/luban-integration.md).
Zhinu is outside Luban's implementation and release criteria.

## Diff and merge delivery track

[ADR 0005](decisions/0005-deterministic-diff-merge.md) adopts the reviewed
[diff/merge specification](diff-merge-spec.md). This track adds generic Luban
change primitives without Git or a second writer. D1–D2 are implemented and [locally qualified](text-change-profile.md); D4–D6 are also implemented and qualified for the initial leaf boundary; D7 remains deferred. See the [completion ledger](leaf-completion.md). Application must qualify the required neutral
admission, live-check and start/outcome contracts; optional Hufu adoption is separate.

| Stage | Delivery | Prerequisite / completion gate |
| --- | --- | --- |
| D1 — implemented | Immutable bounded text model and deterministic shortest-edit diff | Strict UTF-8, byte ranges, frozen tie-breaking, independent reconstruction and byte-faithful fixtures |
| D2 — implemented | Deterministic three-way text merge and structured conflicts | D1; whole-operation budgets, conservative overlap rules, no side preference/markers/clean result on incomplete work |
| D3 — implemented | Versioned structured serialization, display hunks/unified export and narrow modified-file import | D1–D2; canonical vectors, closed grammar, hostile-input caps, exact original context; import is untrusted |
| D4 — implemented | Authorized same-workspace file diff/merge and read-only patch materialization/validation | D1–D3 and qualified shared reader; every input/release checked, complete before/after hashes/lengths/provider versions, no ambient paths |
| D5 — implemented | Existing-file candidate bridge to capture and single/batch execution | D4, existing executor profiles and required host; complete known set admission, locked-object stale checks, mandatory start/outcomes; qualify the neutral host contracts |
| D6 — implemented | Optional language descriptors, typed pipeline values and requirement mappings | Qualified D4–D5 profiles; new catalogue/IR version, full preflight, no widening current language v1 |
| D7 | Directory/snapshot views, cross-workspace inputs, structural merge and optional rename detection | Stable D4–D6 plus qualified coverage/manifest/provider semantics; exclusions never become deletions |

### D1–D2: initial pure text profile

Implement no workspace I/O, mutation dispatch, patch-format parser or textual
commands. Freeze a deterministic bounded line-based LCS shortest-edit strategy,
deletion-first tie-breaking and terminator-preserving tokenization. Preserve BOM,
LF/CRLF/mixed endings, empty lines and final-newline state exactly. Validate scalar
data and translate edit ranges to zero-based UTF-8 byte offsets. Own collection
contents and return explicit typed InvalidInput/LimitExceeded/Cancelled outcomes.

Merge both sides against one exact base with a shared work/memory/output budget.
Equal/one-sided states and identical edits merge cleanly; divergent overlap,
same-position inserts and insertion at a replaced-region boundary conflict.
Adjacent nonempty ranges are independent; transitive overlap groups conflicts.
No automatic model resolution, preferred side, marker output or partial clean
value. Pin strategy/default bounds and source identity semantics in a current
profile document before declaring this gate complete.

Qualification includes independent diff application, repeated-line tie vectors,
Unicode/BOM/newline/empty fixtures, disjoint/identical/divergent edit cases,
delete/insert/boundary/transitive conflicts and side symmetry. Exercise exact
input/line/work/matrix/output/edit/conflict limits, cancellation, total merge
budget accounting and immutable collections. Run the complete Luban Release
suite on both supported runtimes. Do not add a permissive authority fixture to
pure APIs or claim file-provider/governed mutation coverage from these tests.

### D3–D4: transport and authorized observations

Freeze domain-separated versioned canonical bytes independently of pretty output.
Add deterministic hunk rendering and unified diff output, then a closed bounded
existing-file modification parser. Reject unknown/binary/mode/symlink/create/
delete/move forms, duplicate targets, path collisions and unconsumed data before
any proposed writes. Validate newline markers and ranges against exact input.

File adapters use explicit workspace-relative inputs through shared providers,
initially in one workspace. Checks cover every original/base/ours/theirs input
and result release. Truncated/denied/unavailable reads cannot become clean or
missing content. Imported candidates need admitted target discovery, complete
authorized source reads, exact hunk/context validation, and mandatory full
before/after hashes and lengths. Pure/workspace validation is read-only and
cannot issue execution admission. Revalidate again at application.

Gate: hostile import/path/size cases fail closed, serializers round-trip exact
edits under their dialect, excluded source information is not disclosed, and
successful validation performs zero workspace mutations.

### D5–D7: controlled application and later coverage

Translate only supported existing-file UTF-8 modifications into existing
`FilePatchStage`/capture plans. WhatIf remains capture-only with `CanCommit=false`.
No independent Apply engine bypasses single/batch host admission or journaling.
An unsupported/conflicted/incomplete known set gives zero proposed writes.
Existing-file original digest/length/version preconditions are mandatory.

Atomic multi-file requirements reject Unsupported before writes. Sequential
execution requires explicit host admission of the non-atomic profile and exact
per-node Completed/NoMutation/Ambiguous receipts; no automatic retry or rollback.
Test denied later targets before the first write, live stale resources, start
evidence failure, partial completion and uncertain recovery through the bridge.
Qualify these requirements through neutral host contracts and real providers.
A particular Hufu or workflow-runtime integration is not a prerequisite.

Register source syntax only after matching typed providers pass. Preserve new
schema/catalogue identities and closed pure operations; do not adopt the proposal's
workflow `if`/`yield` examples as language control flow. Later manifests explicitly
distinguish authorized-view from complete coverage and pin snapshot consistency.
Do not infer deletions/equality from denied or truncated enumeration. Rename,
create/delete/move, binary apply and broader encodings each need qualified
provider/authority contracts; never silently downgrade unsupported kinds.


## Later work and implementation discipline

Bounded v2 line windows and search context/spans are implemented. Add Exists/Stat, byte-range reads, broader mutations, Git, web and developer profiles as
consumers and qualification justify them. Directory metadata, Copy/Delete/Move/
CreateDirectory, regex and further result types need their own contracts.
Git must reject/disable configured helpers, hooks, filters, pagers, incidental
writes, implicit remote access and excluded-content disclosure before strict
discovery. Build/test remains explicit ApprovedTools. No shell fallback,
arbitrary process route, code emulator or owned sandbox.

Use Luna for bounded scaffolding/provider work/tests where possible. Review
cross-project identity/API changes and native mutation/authority ordering at each
gate. Avoid empty placeholder packages. Run meaningful suites for changed behavior;
repeat/broaden only for a new change, failure or unresolved concern. Keep
implemented status distinct from planned guarantees throughout delivery.

Current Luban qualification: 336 tests pass on each of net8.0 and net10.0 against candidate IO packages. Published IO adoption remains RA-5C. Optional Hufu integration evidence and remaining host-specific gates belong in Hufu documentation.
