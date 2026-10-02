# Penghou.Luban roadmap

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


## Current initial baseline — 2026-10-03

D1–D6 and the AI usability slice are implemented and locally qualified. The
[completion ledger](docs/leaf-completion.md) is the current feature/package/CI
record; [consumer impact](docs/consumer-impact.md) is the migration checklist.
Finish release gates, then qualify Hufu as a consumer. D7 and additional effect
profiles are future scoped work, not prerequisites for this baseline.

## Resource-abstraction correction — 2026-10-02

Follow the [resource-abstractions architecture](../Penghou/docs/resource-abstractions-architecture.md) and
[public-type inventory](../Penghou/docs/resource-abstractions-inventory.md).
Provider injection and semantic ownership are candidate-qualified; Local slices remain the
regression baseline. Published-package adoption remains open.

- [x] **RA-1/RA-3 (candidate qualification):** inject narrow IO contracts into file, language and preview
  runtimes; isolate physical roots, Local constructors and Local-specific public
  executor configuration in host/integration composition.
- [x] **RA-G4:** keep diff/merge/text materialization in Luban and qualify a
  conditional persistence contract before replacing Local's locked patch path.
  Preserve semantic admission, exact bytes/versions, final checks and outcomes.
- [ ] **RA-5C:** consume the corrected published IO packages through pinned
  PackageReferences, remove normal-build sibling-source dependencies, and verify
  neutral dependency/API closure plus read/preview/execution integration.
  See the [CI/publication/adoption sequence](../Penghou/docs/resource-abstractions-corrective-plan.md).
- [ ] **VFS-5, deferred:** normal execution over an injected snapshot/overlay
  provider as a separate explicit mode. Existing capture-only WhatIf remains
  read-only with CanCommit=false.
- [ ] **VFS-7, deferred:** apply an approved immutable delta only through fresh
  real-resource admission, version checks, durable starts and reconciliation.

Do not add Hufu policy, a VFS implementation, HTTP/process redesign or broad
package migration to this correction. Each handoff cites its RA/VFS gate.

Status: Revised 2026-10-02. The Windows direct Read/Find/literal SearchText
runtime, injected IEffectAuthorizer, shared Local reader migration, current
versions model, and minimal read language with typed IR/static preflight are implemented. The
[implementation plan](docs/implementation-plan.md) is the ordered delivery guide.
The [runtime](docs/typed-effect-runtime.md), [language](docs/language-syntax-spec.md),
and [preview/commit](docs/preview-resolution-commit-barrier.md) specifications
distinguish current narrow implementations from broader required semantics.

## Implemented foundation

The shared canonical request codec, Windows read-only Local provider, read migration,
minimal typed language, capture-only WhatIf, single-file executor, and narrow batch
executor with trusted-host recovery inspection are implemented. The controlled-namespace
native profile passes on both supported runtimes. Preserve semantic effect
admission, child-request bindings, current results, legacy basename-pattern behavior,
and the separation between read-only WhatIf and the executor. See the
[shared provider plan](../Penghou/docs/implementation-plan.md) and
[single-patch execution profile](docs/single-patch-execution-profile.md) and
[batch execution profile](docs/batch-execution-profile.md).

## Dependency-ordered milestones

1. **Shared read provider — complete.** Real bounded byte/file-metadata/list operations,
   explicit authorizer, candidate exclusions, canonical request identity,
   versions, paging/cancellation and an accurately qualified Windows profile.
2. **Read migration — complete.** Use that reader for Read/Find/SearchText; retain existing
   effect checks and results, with no duplicated path/resource enforcement.
3. **IR and minimal language — complete.** Versioned semantic IR, bounded parser,
   read/find/literal-search effects, root-relative path globs, take/count and
   complete static preflight. Broader syntax and transforms remain pending.
   Known static denial blocks before protected target I/O.
4. **Capture-only WhatIf — complete.** Programmatic Local-only capture, frozen
   explicit-authorized-view glob targets admitted before any file-content reads,
   exact existing-file UTF-8 scalar-safe TextPatch payloads, bounded work, and
   explicit unresolved coverage/deferred tools. CaptureComplete does not mean
   CanCommit; there is no writer callback or adapter.
5. **Standalone exact-target patch — implemented.** One complete literal-target
   patch uses the host-controlled Local NTFS profile and required host
   admission/live-check/start-journal contract. Both supported runtimes pass
   the suite. This profile does not claim general filesystem namespace
   confinement.
6. **Narrow batches and recovery — implemented.** Execute a complete ordered
   manifest of 1–64 distinct exact-target patches, with whole-manifest admission,
   all-target permission/readiness checks before the first write, stable node IDs,
   trusted-host receipt inspection, completed-prefix resume and separately
   admitted segments. Unresolved selections/deferred tools, automatic retries,
   rollback and multi-file atomicity remain unsupported.

Milestones 1–6 are implemented. Native writer evidence applies only to the
explicit HostControlled NTFS namespace. Recovery tests exercise host-supplied
snapshots and receipts. Hufu integration is optional and tracked in Hufu's
[Luban integration profile](../Penghou.Hufu/docs/luban-integration.md).

## Diff and merge delivery track

[ADR 0005](docs/decisions/0005-deterministic-diff-merge.md) adds deterministic,
Git-independent structured diff/three-way merge. The
[reviewed specification](docs/diff-merge-spec.md) tightens exact hashes, UTF-8
byte coordinates, conflict semantics, input/work/output bounds and authorization.
See the [D1–D7 implementation plan](docs/implementation-plan.md#diff-and-merge-delivery-track).

- D1–D2 — implemented: bounded pure in-memory text diff/merge and structured conflicts; [qualified with 284 total Luban tests per runtime](docs/text-change-profile.md).
- D3 — implemented: canonical structured transport, display hunks/unified serialization and
  bounded modified-file import and exact pure materialization; see the [D3 profile](docs/text-change-transport-profile.md).
- D4 — implemented: same-workspace authorized file diff/merge and read-only exact candidate validation; [profile](docs/file-change-profile.md).
- D5 — implemented: route existing-file changes through capture and separate admitted single/batch
  execution; mandatory hashes/start/outcomes, explicit non-atomic batches.
- D6 — implemented: opt-in v2 diff/merge, bounded line windows and UTF-8 search spans/context; [manual](docs/language-manual.md).
- D7 — deferred: directory/snapshot/structural and cross-workspace/rename profiles after coverage/provider qualification.

Pure computation performs no I/O. It cannot grant permission or make a patch
execution-ready. Application must qualify the required neutral host contracts; this
track adds no alternate writer, Git process, automatic AI resolver or transaction.

## Review follow-up — 2026-10-02

Compiler glob-budget parity and original-source diagnostic offsets are fixed. The direct API now has strict Unicode identities, whole-operation deadlines, concrete action/request metadata, and bounded result-release checks. Search continues after an oversized individual file and reports incomplete coverage. Pure diff trims equal leading tokens before bounded LCS allocation without changing edit tie-breaking. The read-only `LanguageToolRuntime` adds host-bound capability discovery, typed JSON results and safe execution diagnostics; the language manual and independent-policy host sample cover current syntax.

Bounded line windows, search context and exact UTF-8 match spans now qualify under v2 alongside D4–D6. Additional provider profiles and broader operations remain separate gates.

## Later profiles

Byte-range reads, directory metadata, broader mutations, regex, Git, web and native
developer tools follow actual qualified semantics and consumers. Git inspection
requires hostile helper/configuration and excluded-content testing; build/test
remains explicit ApprovedTools. Unsupported effects never fall back to a shell.
No owned sandbox, arbitrary process route, virtual filesystem or code emulator.

Use Luna for bounded provider/test work where possible and review cross-project
identity/native consistency changes at each gate. Publish support only after
matching conformance; package publication is a separate decision.

Current Luban qualification: 337 tests pass on each of net8.0 and net10.0 against candidate IO packages. Published IO adoption remains RA-5C. Optional Hufu integration evidence and remaining host-specific gates belong in Hufu documentation.
