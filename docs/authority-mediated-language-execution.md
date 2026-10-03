# Authority-Mediated Language Execution (AMLE)

Status: proposed shared architectural pattern, documented 2026-10-03 from the
user-supplied AMLE proposal. This names the architectural direction; it does not
expand qualified profiles or establish new security guarantees. Existing
implementation and qualification profiles remain the source of truth.

See [Hufu](../../Penghou.Hufu/README.md) and its complementary
[AMLE guide](../../Penghou.Hufu/docs/authority-mediated-language-execution.md).

**Authority-Mediated Language Execution (AMLE)** is an architecture in which an
actor expresses intended effects through a constrained semantic language, and
each effect is independently mediated against contextual authority before a
trusted adapter accesses the underlying resource.

In Penghou, **Luban supplies expression and execution, Hufu supplies authority
mediation, and Penghou.IO supplies neutral resource contracts and providers.**
The host supplies authenticated identity, workspace bindings, policy and services.
Neither product's core depends on the other. Hufu is optional in Luban, but
authorization is required through a host-supplied authorizer.

```text
Agent intent
  -> Luban source / typed semantic operations
  -> Hufu decisions through a host-selected integration adapter
  -> trusted resource adapter with concrete access/start checks
  -> underlying resource
  -> checked result release and attributable evidence
```

## Four properties

1. **Constrained expression:** the actor requests only operations represented by
   the selected language/profile. Unsupported operations fail explicitly.
2. **Semantic operations:** a read or patch is a typed operation, not an effect
   inferred from an opaque shell string.
3. **Contextual authority:** decisions use authenticated execution context and
   current authority. Activity identity, scope, delegation, approval and revocation
   belong here where the selected authority profile supports them.
4. **Mediated capabilities:** an AMLE host gives the agent no direct unrestricted
   underlying capability. Protected reads, metadata, enumeration, writes and
   disclosure cross the relevant mediation boundary. Registration is not permission.

This applies Language-Oriented Programming: a small domain language makes intent
available for validation, analysis and execution. Typed APIs can share that
semantic boundary without textual source. Pure in-memory diff/merge performs no
resource access; reading inputs or applying a candidate requires separate checks.

## Preflight and runtime checks

Preflight checks statically known requirements without protected target I/O. It
can explain requirements or reject a known denial early. It is not a grant or a
target-state observation. Discovery itself requires authority, and every discovered
resource and later effect still needs runtime checks and applicable release checks.

For example, authority scoped to patch `src/parser/**` could allow a patch to
`src/parser/Parser.cs` while denying `secrets/api-key.txt`. This is an illustrative
authority scenario, not supported Luban source syntax or a claim of a complete
activity-grant lifecycle. A path returned by a read carries no write permission.
Mutation also needs exact-state preconditions, live rights at the real start
boundary, and outcome/recovery evidence.

The rule is **preflight where possible, runtime enforcement always**. This does
not imply lazy streaming execution. Semantic admission and concrete resource
checks complement each other; neither an outer decorator nor one program-level
approval proves the entire boundary.

## Evidence and plan-aware authority

Explicit operations provide a common authorization and audit vocabulary: actor,
activity, operation, resource, decision, authority source and outcome. Evidence
need not reconstruct semantic intent from command text. Durable recording,
provenance and replay guarantees still depend on qualified host/adapters; a
historical allow or recorded success does not authorize a new execution.

Plan-aware authority is the broader direction: workflow activities declare
requirements and request scoped authority. A requirement is not a grant; the
host must authenticate and authorize issuance. Fuwen owns control flow and Zhinu
owns durable execution. Complete issuance, approval, delegation and governed
mutation lifecycles remain separate delivery gates.

## Virtual execution and WhatIf

Replaceable resources create a basis for future simulation: run against virtual
state, inspect effects, then seek admission for execution against real resources.
Simulation must respect authority and cannot grant permission to commit. Real
execution must recheck current authority and target state.

**Current WhatIf is capture-only, not virtual execution.** It captures authorized
observations and patch proposals; `CaptureComplete` does not grant permission to
commit, and `CanCommit` remains false. Stateful VFS/WhatIf execution is deferred
VFS-5 work. Separate patch executors do not change the capture contract.

## Relationship to sandboxing

AMLE begins with a bounded vocabulary and deliberately introduces capabilities.
OS isolation contains the behavior of executing code. They can be combined;
AMLE does not replace operating-system security. Neither Luban nor Hufu owns a
sandbox implementation.

If a future ApprovedTools profile invokes a build, interpreter or native program,
its internal effects may be opaque to Luban. The host must select appropriate
process/container/VM or other isolation. Current StrictEffects has no arbitrary
shell/process escape hatch. Network and approved-tool examples in the proposal
are architectural examples, not additions to the supported command catalogue.

## Luban's implementation boundary

Luban owns the bounded language, typed IR, trusted effect descriptors, pure text
operations, capture and separate execution profiles. See the [language manual](language-manual.md),
[read profile](read-language-profile.md), [capture profile](capture-preview-profile.md)
and [batch execution profile](batch-execution-profile.md). Textual mutation
commands and broader ApprovedTools remain pending. Separate single/batch patch
executors require host admission, live rights, start evidence and journaling;
they do not supply atomic multi-file writes or rollback.

[Hufu's integration contract](../../Penghou.Hufu/docs/luban-integration.md) owns
authority mapping. Its known-root read-language prototype is implemented; a
complete governed Luban mutation host and exact terminal-outcome recovery adapter
are not. HostControlled Local NTFS writes require a host-controlled namespace and
do not provide general filesystem confinement. See [shared I/O integration](shared-io-integration.md)
and the [resource architecture](../../Penghou/docs/resource-abstractions-architecture.md)
for RA-1/RA-3 and deferred VFS gates.
