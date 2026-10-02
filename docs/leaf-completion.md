# Luban leaf completion ledger

Status (2026-10-03): **initial feature baseline implemented and qualified locally and in CI**.
Remote CI and public-feed publication are tracked separately below. This ledger
is the current evidence; counts in older qualification reports are historical.

## Frozen initial boundary

Luban is the bounded language/runtime for AI tool use and security. It owns
semantic operations, preflight, text diff/merge, exact change candidates and their
capture/application bridge. The core depends only on `Penghou.IO.Abstractions`
and `Penghou.IO.Protocols`; hosts select a provider separately. Hufu is optional.
Zhinu and other workflow engines are outside Luban's completion criteria.

The initial preview is `0.1.0-preview.1`. Language v1 remains the default, with
its existing semantic identity vectors and result shapes. Hosts explicitly opt
into language/IR `2`, catalogue `windows-text-change-v2` and provider profile
`local-windows-read-v1`. Source cannot grant authority or change that selection.

| Gate | Evidence / status |
| --- | --- |
| D1–D3 pure text and transport | Implemented; deterministic bytes/edits, conflicts, hostile import bounds and canonical vectors in the full suite |
| D4 authorized file changes | Implemented; complete input-set admission, provider checks, versions/hashes/lengths, read-only validation and independently encoded candidate identity |
| D5 application bridge | Implemented; fresh capture and exact candidate comparisons, single/batch application through existing host contracts, stale/denied/release/retained-data cases |
| D6 language v2 | Implemented; diff/merge, line windows, search spans/context, typed JSON and unchanged v1 identities/shapes |
| Windows conformance | **337 passed, zero failed/skipped on each of net8.0 and net10.0**, Release, against exact candidate IO packages |
| Package qualification | `.nupkg`/`.snupkg` inspection and isolated package-only consumer pass on both frameworks; exact IO closure, README/license and no Local/Hufu/Fuwen/Zhinu core reference |
| Remote CI | [Run 37039062513](https://github.com/jenolaszlo-sketch/penghou-luban/actions/runs/37039062513) passes for `4bbbd84bbaaf7a3376f85fc1e59da3fd13901693`: Windows tests/sample/package consumer and Linux neutral-core build |
| Public feed | Pending IO publication followed by Luban publication and public-feed restore qualification |
| Consumer adoption | Hufu is next; see the [consumer impact guide](consumer-impact.md). Its integration does not block this feature baseline |

The test matrix covers real Windows Local reader/writer behavior. Native writes
are qualified only for the explicit HostControlled NTFS namespace and remain
non-atomic. Linux CI qualifies compilation of the provider-neutral core, not
Local behavior. Candidate feeds prove package closure without asserting NuGet.org
availability.

## Semantics that consumers must preserve

Language execution is read-only. Clean diff/merge values are proposals with exact
observations; conflicts never become a clean file. Application goes through
`FileChangeCaptureBridge`, then the separate single or batch executor with fresh
host admission, live checks and start/outcome evidence. Capture does not grant
`CanCommit`. Batches are sequential and can leave an earlier completed prefix;
uncertain outcomes do not permit automatic retries.

All authorizers are required and have no permissive default. A consumer that
understands only v1 can continue selecting v1. Unknown v2 semantic requests must
fail closed until its policy supports them. The package boundary does not choose
Hufu, a policy language, a journal store or a workflow engine.

## Later features, not reopening gates

D7 directory/snapshot/structural/rename profiles, additional providers, binary or
create/delete/move mutations, Git, shell/process access and workflow control flow
are new scoped profiles. They are outside this initial baseline. New work must
name its profile and consumer; it must not silently widen v1 or v2.

Before a stable release, review the preview API with consumers and capture a
public API compatibility baseline. The current preview is not a stable 1.0 ABI
promise. Required changes from that review should be tracked explicitly rather
than treating all possible future tooling as unfinished Luban work.

## Reading and handoff order

1. This ledger: completion boundary and evidence.
2. [Consumer impact](consumer-impact.md): package graph and migration owner.
3. [Language manual](language-manual.md): supported syntax and host opt-in.
4. [File changes](file-change-profile.md) and [application](file-change-application-profile.md): authority, identity and application guarantees.
5. [Roadmap](../ROADMAP.md): optional future profiles.

Resume prompt: **Preserve Luban's qualified initial baseline. Work on the named
consumer or release gate, cite this ledger and consumer-impact.md, and record any
necessary producer change and affected consumers before editing the package. Do
not add Hufu or workflow-engine dependencies to Luban core.**
