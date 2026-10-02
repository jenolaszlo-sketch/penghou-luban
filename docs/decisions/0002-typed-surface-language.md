# ADR 0002: Compile concise syntax into bounded typed dataflow

Date: 2026-10-01.

Status: **Accepted; minimal read parser, compiler, semantic IR, static preflight, and take/count execution implemented. Broader syntax and transforms remain pending.**

## Decision

Adopt the reviewed [language syntax specification](../language-syntax-spec.md).
The implemented profile supports read, find, literal search, take, and count,
compiled to typed semantic IR and validated before execution; no command is
passed to a system shell. See the [read profile](../read-language-profile.md)
and [semantic IR profile](../semantic-ir.md). Broader aliases, pipelines, and
transforms remain pending.

This refines ADR 0001 and the runtime design's earlier blanket exclusions of
pipelines and predicates. Typed dataflow and catalogue-defined pure comparisons
are permitted. Shell pipes, executable predicates, arbitrary callbacks, loops,
workflow conditionals, substitutions, process escape and unrestricted scripting
remain excluded. Fuwen/Zhinu still own orchestration and durable decisions.

Aliases and normalized syntax compile to one versioned semantic IR. Canonical
identity binds typed values, defaults, catalogue/semantic versions, effect and
transform nodes, and input edges; source spelling/comments do not add authority.
Current JSON request digests belong to the direct runtime API and are not yet
this canonical language/IR identity.

Static extraction identifies effect kinds, requirements and bounded selectors.
Concrete targets discovered by a pipeline still require the mandatory host
authorizer before use or release. A typed FileRef carries provenance, not a
permission. Pure transforms add no external effect but consume bounded work.

## Staging and qualifications

The full source proposal is a design envelope, not a claim that its suggested
V1 catalogue exists. Range reads, metadata projections, regex, mutations, Git,
and developer namespaces require their corresponding qualified semantics before
admission. Language root-relative `*`, `?`, and whole-segment `**` path globs are
implemented; direct API recursive basename patterns retain separate semantics.
Unknown or unsupported commands/options
fail before any effect starts; there is no shell fallback.

Specify lexical rules, ranges, payload delimiters, mutation preconditions,
resource/work budgets, type checks and whole-document validation explicitly in
the reviewed spec. Parsing all statements does not make their effects atomic;
runtime failures and authority changes still require normal recovery.

## References

- [Reviewed syntax](../language-syntax-spec.md)
- [Original source, preserved verbatim](../archive/language-syntax-proposal-2026-10-01.md)
- [Typed runtime](../typed-effect-runtime.md)
- [Ownership decision](0001-luban-owns-typed-effects.md)
- [Hufu integration](../../../Penghou.Hufu/docs/luban-integration.md)
