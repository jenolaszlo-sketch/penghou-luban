# Penghou.Luban contributor guidance

Read the [resource-abstractions architecture](../Penghou/docs/resource-abstractions-architecture.md)
before new provider/API work. RA-1/RA-3 target injected neutral IO capabilities
and Local composition outside neutral runtime code. Existing profiles below
remain the regression requirements until replacements qualify. Diff/merge stay
here; provider persistence stays in IO. The current capture-only WhatIf rules
do not describe deferred VFS-5 virtual execution, which requires a separate mode.
Handoffs must cite the canonical document, RA/VFS ID, open gates and evidence.

Read docs/typed-effect-runtime.md, docs/language-syntax-spec.md, and the decision/implementation documents
before designing APIs. Luban owns typed requests/results, trusted effect
descriptors, language execution and its conformance. Shared resource providers
live in Penghou.IO packages. Hufu owns authority, Fuwen control
flow, and Zhinu durable execution. Neutral contracts must not depend on those
projects, a host UI, MCP, or a particular agent.

ADR 0003 selects Penghou.IO.Abstractions for planned shared resource contracts.
Preserve semantic effect admission and final per-resource checks together; do
not treat a pipeline value or earlier read as write authority. Migration is
implemented for Windows Read/Find/SearchText. Providers own target binding, precondition consistency, and checks for
reads/metadata/enumeration as well as writes; generic pre-write events are insufficient.

Read docs/preview-resolution-commit-barrier.md and ADR 0004. Static preflight
performs no protected target I/O; the current read language, IR, and capture-only
WhatIf profile are implemented. An injected conditional writer supplies persistence; IO.Local NTFS supports writes only when the host explicitly selects `LocalPatchNamespace.HostControlled`; the default or unknown mode is Unsupported before I/O. The host must keep the root/drive/mount/directory namespace controlled against untrusted actors; this profile is not general filesystem confinement. The separate
`SinglePatchExecutor` implements one standalone exact-target patch with required
host admission, live rights checks, serialized start evidence, and journaled
outcomes. `BatchPatchExecutor` separately executes a complete ordered manifest
of 1–64 distinct exact-target patches. It requires whole-manifest admission and
checks every remaining target's rights/readiness before the first write. Recovery
uses a host-authoritative ordered receipt snapshot: only a matching Completed
prefix is skipped; uncertain or NoMutation states block with no automatic retry.
Each call is a separately admitted segment; a dependent later segment needs a
fresh capture. The optional Hufu current-state/evidence store and co-located Hufu/Zhinu start gate are implemented in Hufu. No complete governed Luban mutation host or exact terminal-outcome recovery adapter is implemented. This does
not add a callback or mutation dispatch to WhatIf:
`CaptureComplete` means only that capture finished, and `CanCommit` remains
false. Preserve target-admission-before-content-read ordering, unresolved later
nodes, and Release checks. Do not add atomic multi-file, rollback, durable-store,
or general namespace-confinement claims.

StrictEffects is the initial direction. No arbitrary commands, scripting,
executable predicates, generic process escape hatch, or owned sandbox.
Bounded typed dataflow and closed pure filters are specified under ADR 0002;
they permit no shell pipes, callbacks, loops, or workflow decisions. The parser,
compiler, canonical IR, and bounded `take`/`count` execution are implemented
for the default v1 read profile. Opt-in v2 adds authorized file diff/merge,
bounded line windows and UTF-8 search spans/context. Broader transforms and
mutation source commands remain pending. Typed build/test
requests remain broader ApprovedTools work, not strict operations.
Preserve limits, exact resource binding, precondition/reconciliation requirements,
trusted metadata, and explicit unknown/unsupported results. No security claims
without boundary-level evidence.

Use docs/leaf-completion.md as the current initial-baseline evidence and
docs/consumer-impact.md to track package migrations and consumer effects. D1–D6
are qualified for this bounded preview; D7 and additional profiles are separate
scope. Hufu and workflow integration do not block Luban feature completion.

Keep scaffolding minimal. Add runtime APIs and tests with meaningful behavior,
not placeholder types. No publishing or remote repository creation is implied.
Do not edit other repositories without task scope. API/package names in the
source proposal are illustrative; Penghou.Luban is the selected project name.

Read docs/diff-merge-spec.md, docs/text-change-profile.md and ADR 0005 before changing diff/merge behavior. Pure text computation must perform no I/O or implicitly approve/apply candidates. Preserve exact UTF-8 bytes/newlines, deterministic tie-breaking, shared merge budgets, byte-coordinate edits and explicit structured conflicts. File/import/application profiles remain separate gates; do not bypass capture/admission/execution or weaken mandatory original hash/version checks.
