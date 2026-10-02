# Penghou.Luban roadmap

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
7. **Hufu/Zhinu integration — in progress.** Hufu has a narrow current-snapshot
   and known-root read-authorizer prototype; see the [current authority prototype
   profile](../Penghou.Hufu/docs/current-authority-profile.md). Real grants,
   exclusions, approvals and whole-plan admission remain open. Hufu's optional
   SQLite store and co-located Zhinu start adapter are implemented; sequential
   snapshot lookup/acquisition still provides no atomic fence. A real governed
   mutation host and exact terminal recovery remain pending. Qualify local before
   authenticated supervisor routing.

Milestones 1–6 are implemented; milestone 7 has started with the limited Hufu
prototype above. Native writer evidence applies only to the
explicit HostControlled NTFS namespace; it does not establish general namespace
confinement. Recovery tests exercise host-supplied snapshots and receipts, not
durable-store behavior. Standalone host contracts are not Hufu enforcement.

## Diff and merge delivery track

[ADR 0005](docs/decisions/0005-deterministic-diff-merge.md) adds deterministic,
Git-independent structured diff/three-way merge. The
[reviewed specification](docs/diff-merge-spec.md) tightens exact hashes, UTF-8
byte coordinates, conflict semantics, input/work/output bounds and authorization.
See the [D1–D7 implementation plan](docs/implementation-plan.md#diff-and-merge-delivery-track).

- D1–D2 — implemented: bounded pure in-memory text diff/merge and structured conflicts; [qualified with 284 total Luban tests per runtime](docs/text-change-profile.md).
- D3 — implemented: canonical structured transport, display hunks/unified serialization and
  bounded modified-file import and exact pure materialization; see the [D3 profile](docs/text-change-transport-profile.md).
- D4: same-workspace authorized file diff/merge and read-only exact candidate validation.
- D5: route existing-file changes through capture and separate admitted single/batch
  execution; mandatory hashes/start/outcomes, explicit non-atomic batches.
- D6–D7: optional new language profile, then directory/snapshot/structural and
  cross-workspace/rename profiles after coverage/provider qualification.

Pure computation performs no I/O. It cannot grant permission or make a patch
execution-ready. Complete governed application still depends on Step 7; this
track adds no alternate writer, Git process, automatic AI resolver or transaction.

## Review follow-up — 2026-10-02

Compiler glob-budget parity and original-source diagnostic offsets are fixed. The direct API now has strict Unicode identities, whole-operation deadlines, concrete action/request metadata, and bounded result-release checks. Search continues after an oversized individual file and reports incomplete coverage. Pure diff trims equal leading tokens before bounded LCS allocation without changing edit tie-breaking. The read-only `LanguageToolRuntime` adds host-bound capability discovery, typed JSON results and safe execution diagnostics; the language manual and independent-policy host sample cover current syntax.

Line-range/context reads, match spans, broader provider injection and richer language operations remain separate qualification gates. The tool wrapper does not complete Step 7 or the D4–D7 authorized file/application track.

## Later profiles

Ranges, directory metadata, broader mutations, richer search, Git, web and native
developer tools follow actual qualified semantics and consumers. Git inspection
requires hostile helper/configuration and excluded-content testing; build/test
remains explicit ApprovedTools. Unsupported effects never fall back to a shell.
No owned sandbox, arbitrary process route, virtual filesystem or code emulator.

Use Luna for bounded provider/test work where possible and review cross-project
identity/native consistency changes at each gate. Publish support only after
matching conformance; package publication is a separate decision.

Current Step 7 evidence: the narrow Hufu typed Cedar/known-root read consumer passes 44 tests on each runtime, with 86 shared-provider and 167 Luban regressions passing. Unicode/short-name alias guards are qualified locally. See [the Hufu qualification record](../Penghou.Hufu/docs/authority-profile-qualification.md). Hufu now supplies an optional locally qualified current-state/evidence SQLite slice; see the [store qualification](../Penghou.Hufu/docs/durable-authority-store-qualification.md). The narrow co-located Hufu/Zhinu start transaction is now implemented and [qualified with 95 Hufu tests per runtime](../Penghou.Hufu/docs/operation-start-qualification.md). Governed single-patch admission/resource checks and exact terminal-outcome recovery are next; complete authority lifecycle and batch integration remain open. Step 7 is not complete.


