# Luban documentation

Start with the [current language manual](language-manual.md) and [AI-tool host sample](../samples/AiToolHost/Program.cs). `LanguageToolRuntime` provides capability discovery and typed JSON results through a host-supplied authorizer. The [direct API profile](direct-api-profile.md) documents its admission/resource/release checks and cancellation bounds.

The [typed-effect runtime](typed-effect-runtime.md) defines provider behavior and
ownership. The [language specification](language-syntax-spec.md) defines the
planned textual frontend and typed dataflow. Read both with the
[architecture](architecture.md), [implementation plan](implementation-plan.md),
and [roadmap](../ROADMAP.md).

The [preview/commit design](preview-resolution-commit-barrier.md) includes a
capture-only Local WhatIf profile and separate one-target and narrow batch
execution profiles.
WhatIf freezes and admits selected targets before content reads and captures
typed patches; it never grants `CanCommit` or dispatches a writer. The standalone
executor supports one complete exact-target patch through the Local NTFS profile
and required host admission/start/journal contract. The batch executor accepts
complete ordered manifests of 1–64 distinct exact-target patches and relies on
trusted-host admission and receipt inspection for resume. It is not atomic and
does not retry uncertain/NoMutation entries. Hufu supplies a separate experimental
co-located Zhinu start gate; complete governed mutation hosts, exact terminal
recovery and general writers remain pending. See the [batch execution profile](batch-execution-profile.md).

The [completion ledger](leaf-completion.md) records the initial D1–D6 baseline and release evidence. The [consumer impact guide](consumer-impact.md) records dependencies and migrations. Pure text/transport, authorized file changes, the application bridge and opt-in v2 diff/merge commands are implemented. The Windows Read/Find/literal SearchText runtime, required direct IEffectAuthorizer,
language parser/compiler, typed semantic IR, and static preflight are implemented.
The minimal language supports read, find, literal search, take, and count. Its
qualified `*`, `?`, and `**` path globs are separate from legacy recursive
basename-pattern behavior. V1 executes read effects and take/count; opt-in v2 also computes file diff/merge, line windows and search context/spans;
textual mutation syntax, selected-glob execution, deferred tools, durable
journaling and complete governed mutation adapters remain pending; the separate Hufu known-root read adapter is implemented. The [current read profile](read-language-profile.md)
and [semantic IR profile](semantic-ir.md) describe the implemented versions.

- [API stability](api-stability.md): final ownership, frozen signatures and build enforcement.
- [File-change profile](file-change-profile.md): exact authorized observations, candidates and read-only validation.
- [File-change application](file-change-application-profile.md): capture bridge and separate execution.
- [Review-fix qualification](review-fixes-2026-10-02.md): corrected boundaries and 227-test validation on both runtimes.
- [Diff/merge specification](diff-merge-spec.md): reviewed feature, exact-state and authority boundaries.
- [Text change profile](text-change-profile.md): initial pure diff/merge API, bounds and qualification.
- [Text transport profile](text-change-transport-profile.md): canonical encoding, unified grammar, bounds and pure validation examples.
- [ADR 0005: deterministic diff and merge](decisions/0005-deterministic-diff-merge.md).
- [Diff/merge proposal](archive/diff-merge-proposal-2026-10-02.md): verbatim historical input.
- [Batch qualification](batch-qualification-2026-10-01.md).
- [ADR 0001: ownership](decisions/0001-luban-owns-typed-effects.md).
- [ADR 0002: typed surface language](decisions/0002-typed-surface-language.md).
- [ADR 0003: shared resource interfaces](decisions/0003-shared-resource-interfaces.md).
- [ADR 0004: preview resolution and commit barrier](decisions/0004-preview-resolution-commit-barrier.md).
- [Penghou.IO.Abstractions source](https://github.com/jenolaszlo-sketch/penghou).
- [Hufu integration](../../Penghou.Hufu/docs/luban-integration.md).

The [runtime proposal](archive/typed-effect-runtime-proposal-2026-10-01.md) and
[language proposal](archive/language-syntax-proposal-2026-10-01.md) are verbatim
historical inputs. Their examples and earlier restrictions are superseded where
the reviewed specifications and ADR amendments qualify them.

The [preview/commit amendment](archive/preview-resolution-commit-barrier-proposal-2026-10-01.md)
is also preserved verbatim as historical input.
