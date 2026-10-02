# Architecture and ownership

Status: partial implementation. The repository provides a Windows path-based
Read/Find/SearchText provider with a mandatory host-supplied authorizer, a
separate exact-target patch executor, and a narrow 1–64 target batch executor
with host-supplied admission and recovery snapshots. It does not implement a
durable store or governed host adapters. The
[reviewed design](typed-effect-runtime.md) defines the detailed behavioral
requirements; [ADR 0001](decisions/0001-luban-owns-typed-effects.md) records
ownership. [ADR 0002](decisions/0002-typed-surface-language.md) and the
[language specification](language-syntax-spec.md) describe the implemented
minimal compiler layer and broader pending design. The current language supports
read/find/literal search/take/count and does not add workflow control flow.

[ADR 0003](decisions/0003-shared-resource-interfaces.md) selects the shared
resource layer, `Penghou.IO.Abstractions`, in the
[Penghou repository](https://github.com/jenolaszlo-sketch/penghou). Luban effect
handlers now call the bounded Local reader carrying trusted invocation and
effect/request bindings. Future Hufu adapters will govern those providers; neither core
depends on the other. The compiler extracts trusted requirements and validates
the whole document. `ILanguageAuthorizer` is required at Preflight, EffectStart,
ResourceAccess, and Release; direct legacy effects still require
`IEffectAuthorizer`.

[ADR 0004](decisions/0004-preview-resolution-commit-barrier.md) records the
implemented capture-only [WhatIf profile](preview-resolution-commit-barrier.md).
The programmatic `PreviewCompiler` and `PreviewRuntime.WhatIfAsync` are Local-only,
freeze explicit-authorized-view glob targets, and admit every selected target
before any file-content reads using the `IPreviewAuthorizer` TargetAdmission
phase. They
capture typed existing-file patches; `CaptureComplete` is not `CanCommit`, which
is always false. All-match coverage, truncation, repeated targets, deferred tools,
and their later nodes stay unresolved under explicit bounds. Any unresolved
earlier patch node from AllMatches, incomplete selection, or repeated target
blocks every later node as `DependsOnUnresolvedEffect` with zero later I/O.
Deferred tools use `DependsOnOpaqueEffect` for dependent nodes. Initial v1 uses
ordered segments without virtual state. This capture profile has no writer callback or dispatch; separate one-target and narrow exact-target batch execution are implemented. The batch profile rejects selected-glob execution, unresolved plans, and deferred tools. It is non-atomic, has no rollback or automatic retry, and depends on trusted-host receipt recovery. Broader stateful segments and governed host integration remain future work.

```text
Agent / planner
  -> optional Luban source parser/compiler, whole-document validation
  -> versioned typed IR and bounded effect requirements
  -> Fuwen activity and declared effect requirements
  -> static preflight against current authority
  -> implemented authorized read-only capture and immutable capture plan
  -> separate single/batch executor profiles with whole-manifest host admission
  -> trusted-host per-node start/completion and recovery inspection
  -> Luban provider final start check and bounded handler
  -> shared read-only Local provider, per-target checks and bounded I/O
  -> typed results; durable evidence remains host-owned and adapter-dependent
```

Luban's neutral core defines typed read requests/results and the mandatory
authorization boundary. Provider discovery and operation envelopes remain
broader work. The minimal language binds subject, effect, attempt, semantic document/node
identity, descriptor/profile versions, typed inputs and workspace without
importing Hufu or workflow-engine types. Mutation preconditions and batch
admission references are carried by the standalone execution contract; governed
Hufu authority mappings and distributed fences remain future protocol work. Hufu translates descriptor requirements to authority
and owns current grants, approval, revocation, and evidence policy. Fuwen owns
branching and iteration. Zhinu owns durable workflow state and resumes.
Adapters carry their workflow/run/node bindings outside Luban's neutral
requests/results. A standalone host supplies its own admission and journal
contract and states its own guarantees.

The compiler identifies supported effect kinds and scope ceilings, not every future
traversal target. Each effect and concrete discovered resource still passes the
provider's authorization checks. The runtime retains private dependency bindings; returned file references contain only paths and never
grant access. Admission binds exact IR, schema/catalogue/provider versions, and
host workspace bindings; source spelling is not the approval identity. The
legacy JSON-based request digest remains distinct from the implemented canonical IR hash.

The shared layer supplements semantic effect admission. It must authorize
content/metadata reads and enumeration candidates before disclosure, not only
writes. Source/destination checks for copy/move are independent. Pipeline values
do not carry rights: each later read/write still passes admission and resource
checks. Provider-specific target binding and precondition consistency remain
necessary; a callback before changing a path leaves replacement races unresolved.
The current runtime references the abstractions and Local reader; target filesystem access is centralized there. See the [integration build](shared-io-integration.md).

Whole-document preflight checks known effects, targets, scope ceilings and
authority readiness before upstream production. A denied static write blocks
early; unknown dynamic targets remain explicitly incomplete. Static preflight is neither
a grant nor a target version observation. Protected discovery requires its own read
authority. The current executor uses bounded buffered values; it does not promise
lazy execution or backpressure. Cancellation and work/result bounds apply, and
final checks still apply at access. Durable artifacts require an explicit
recovery/release contract rather than automatic persistence.

The provider boundary is a substantive security and recovery boundary. Its
discovery profile describes kind/schema versions, platform, limits, link
handling, execution mode, and reconciliation. Admission pins this profile;
the provider rechecks it and current authority immediately before protected
target I/O. A trusted local handler binds the actual resource it opens or
mutates. A supervisor provider authenticates and correlates pending requests,
then validates completions before Zhinu advances. Provider success alone
does not prove resource containment; a qualified local apply step can be used
for a separately admitted candidate patch.

StrictEffects admits only registered, qualified bounded handlers and rejects
unknown effects, arbitrary commands, process escape, and scripting. ApprovedTools
requires explicit host opt-in for adapters that execute project code, such as
build and test. There is no fallback to UnrestrictedProcess or an owned sandbox.
The current support profile is Windows path-based file Read/Find/SearchText,
the minimal bounded read language and typed IR, standalone one-target patching,
and a narrow exact-target batch executor. Language `*`, `?`, and `**` path-globs
do not change the legacy API's recursive basename-pattern behavior. Textual
mutation syntax, selected-glob batch execution, other mutation forms, Git
inspection, and other platforms require separate profiles and conformance.

## Diff and merge ownership

[ADR 0005](decisions/0005-deterministic-diff-merge.md) adds neutral bounded text
diff/merge, structured edits/conflicts and deterministic strategies inside Luban.
The [text change profile](text-change-profile.md) owns pure in-memory computation;
file adapters use shared providers with independent input/release checks. No
Git, Hufu, workflow, filesystem, network or identity-service dependency is added
to pure compute. Resource-bearing file operations follow qualified descriptors.

Candidate materialization and read-only validation precede the existing capture
and separate mutation executors. Whole-plan admission and writer object/version
checks remain mandatory. No generic Patch.Apply route bypasses them. Imported
text is untrusted; hashes identify exact bytes, not authority. Directory/snapshot
coverage, new mutation kinds and language registration are separate gates in the
[implementation plan](implementation-plan.md#diff-and-merge-delivery-track).
