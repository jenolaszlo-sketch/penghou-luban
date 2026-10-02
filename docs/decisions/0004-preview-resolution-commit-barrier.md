# ADR 0004: Resolve previewable effects before committing mutations

Status: Accepted design direction, 2026-10-01; capture-only WhatIf, standalone exact-target execution, and the narrow exact-target batch/recovery profile are implemented. Durable/governed adapters and broader mutation forms remain pending.

Adopt the reviewed [preview/commit design](../preview-resolution-commit-barrier.md).
Extend static preflight with authorized bounded discovery, an immutable typed
ResolvedEffectPlan, admission of the complete known mutation set and an
executor-enforced commit barrier. No known denied mutation may be discovered
only after earlier previewable mutations were needlessly dispatched.

Static preflight observes trusted authority state without protected target I/O;
preview resolution performs real authorized reads. Neither is pure policy
simulation or permission to commit. Hufu or an explicitly selected standalone
host supplies admission; every actual effect/resource still receives current
authority and version checks. Shared I/O contracts do not own the language plan.

WhatIf never dispatches requested mutations or opaque/lazy tools. Unresolved
consequences remain explicit, and unsupported effects, denied access or incomplete
scans cannot silently downgrade to lazy execution. Dependent later work after a
state-changing unresolved effect needs a newly resolved/admitted segment.

Batch admission freezes exact targets, payloads, preconditions and semantic IR
identity. It prevents known denied mutations before commit, but is not a general
transaction or path-race solution. Failures after commit begins may leave partial
outcomes requiring receipts and reconciliation. Restart never treats an old
preview as live authority.

This refines [ADR 0003](0003-shared-resource-interfaces.md)'s earlier use of
"preview" for static advisory preflight. The minimal parser/compiler, semantic
IR, static preflight, bounded read execution, capture-only Local WhatIf,
standalone one-target patch executor, and narrow 1–64-target exact-patch batch
executor with trusted-host recovery inspection are implemented. WhatIf retains
no dispatch and `CanCommit` remains false; separate executors require host
admission, live checks and journaled start/outcome evidence. Batch dispatch is
sequential and non-atomic; uncertain and NoMutation receipts block automatic
retry. No local durable store or Hufu/Zhinu adapter is included. The next
milestone is governed/durable host integration and broader mutation forms.
Shared read-provider migration is implemented. Preserve
the [original amendment](../archive/preview-resolution-commit-barrier-proposal-2026-10-01.md)
as historical input rather than normative implementation status.
