# ADR 0003: Shared resource interfaces beneath Luban effects

Status: Accepted design direction, 2026-10-01; Windows read migration implemented.

Refined by [ADR 0004](0004-preview-resolution-commit-barrier.md): the earlier
advisory preview means static preflight. Authorized read-only resolution and
whole-known-mutation-set admission before an executor commit barrier are added
planned stages. Final per-resource checks and partial-outcome limits remain.

## Decision

Use `Penghou.IO.Abstractions` in the separate
[Penghou repository](https://github.com/jenolaszlo-sketch/penghou) as the neutral
resource boundary for bounded file, directory, and web operations. It owns
resource contracts and provider obligations, not effect-language semantics,
workflow scheduling, or authority policy. Luban owns its compiler, typed effects,
catalogue, and effect handlers. Hufu implements authority through host-selected
adapters; standalone hosts explicitly supply their own authorization policy.

Keep two connected checks: admit the exact semantic effect, then authorize each
concrete resource action immediately before protected access. Carry authenticated
host invocation, effect/request identity and restrictive scope/limit bindings
through the boundary. Source cannot choose its checker or backend. Raw streams,
arbitrary callbacks, and an ambient user context are not the initial public API.

Read, metadata, existence, enumeration, result release, and network access need
checks as well as mutation. Enumeration authorizes candidates before disclosure;
copy/move require both ends. The concrete provider binds check, precondition and
actual access to the same target under its tested platform guarantees. A generic
before-write event or decorator around unrestricted enumeration does not prove
this. No sandbox or process containment follows from dependency injection.

Each typed pipeline node retains its own effect admission and resource checks.

Static preflight validates the complete supported document, profiles, requirements,
known targets and current authority before protected target I/O. Known denied
requirements block before reads. Readiness is advisory, not a grant; dynamic targets
and changed authority still require live checks. The current executor uses bounded
buffered values; lazy execution/backpressure are not claimed. Avoid implicit durable
artifacts or transactions.
Allowed listing and content reads cannot confer write authority on a later node.
File references and values carry provenance, never bearer permission. Add `gc`
as a planned alias of typed `read`, with no PowerShell invocation. Define typed
stream signatures and directory-item handling before enabling that alias in
pipelines. No implicit shell-style concatenation or conversion to write payloads.

## Delivery

The Penghou repository contains a canonical request codec and qualified Windows
read-only Local provider alongside its contracts. Luban's existing
Read/Find/SearchText API uses that reader and retains IEffectAuthorizer for
semantic admission and each concrete target. Luban also implements the minimal
read language, typed IR, static preflight, and take/count execution. Language
authorization requires ILanguageAuthorizer at Preflight, EffectStart,
ResourceAccess, and Release. Capture-only resolution is next; it includes no
writer. Hufu adapter, mutations, writer/barrier execution, and web providers
remain pending.
Reuse existing behavior tests and add boundary tests during migration. Public
source publication is authorized for Penghou; package publication is separate.

See the [architecture](../architecture.md), [language specification](../language-syntax-spec.md),
and [Hufu integration](../../../Penghou.Hufu/docs/luban-integration.md).
