# ADR 0001: Luban owns typed effects

Status: Accepted, 2026-10-01.

Amended by [ADR 0002](0002-typed-surface-language.md): Luban also owns the
planned surface language/compiler and versioned typed IR. Finite typed dataflow
and closed pure filters are permitted; executable predicates here mean
user-supplied code or callbacks. Shell pipelines and workflow loops remain
excluded. The implemented profile is Windows Read/Find/literal SearchText plus
the minimal read language, typed IR, and static preflight; ApplyPatch remains
pending.

## Context

Agents need common source-workspace operations without treating an arbitrary
shell program as the default request. The original proposal placed an initial
implementation on Hufu's roadmap and used Penghou.Effects as a working name.
The [reviewed design](../typed-effect-runtime.md) narrows the guarantee and
selects Penghou.Luban as the independent owner from the start. Hufu's
[ADR 0004](../../../Penghou.Hufu/docs/decisions/0004-typed-effects-over-sandbox.md)
selects this direction over an owned sandbox.

## Decision

Penghou.Luban owns neutral typed requests and results, trusted versioned effect
descriptors, provider contracts, bounded handlers, and their conformance tests.
Its core contracts must not depend on Hufu, Fuwen, Zhinu, a host UI, MCP, or a
particular agent. Keep one library until real consumers justify package splits.

Hufu owns grants, authority evaluation, revocation, admission, and authority
evidence. Its adapter derives exact requirements from Luban's trusted
descriptors and enforces the final current-authority/start check. Fuwen owns
workflow control flow; Zhinu owns durable workflow execution. Neither behavior
is reimplemented as an effect scripting language or a competing evidence store.
Standalone hosts can provide their own admission and journal contract but must
not claim Hufu authority without Hufu-governed composition.

StrictEffects is the first execution surface: only bounded, qualified effects
may run. Unknown effects, arbitrary process/shell requests, executable
predicates, and scripting are rejected. Typed build/test adapters are possible
later under explicit ApprovedTools admission because project code can execute.
No OS or container sandbox belongs to this roadmap. UnrestrictedProcess is an
external-provider/host mode requiring a separate decision, never a fallback.

## Consequences

The implemented read slice is Files.Read, Files.Find, and Files.SearchText,
qualified on Windows. Files.ApplyPatch remains planned and must be qualified
before support is advertised. Request
and provider semantics must specify limits, path binding, links, preconditions,
recovery, and result validation before making enforcement claims. Hufu
integration follows the neutral contract rather than introducing Hufu types
into Luban's public API. Git inspection waits for explicit helper/configuration
qualification. Penghou.Effects and Penghou.Hufu.Effects remain historical names
in the archived proposal, not package names for new work.
