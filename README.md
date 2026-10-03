# Penghou.Luban

**A typed tool language and runtime for AI agents, with authorization built into execution.**

Luban is designed primarily for AI tool usage and the security of those tool calls. It gives an AI agent a bounded way to express work such as reading files, finding source code, searching text, and proposing changes. It compiles supported commands into typed operations so a host can check what is requested, which resources are involved, and whether the caller is allowed to proceed.

The host supplies the workspace, caller identity, authorization implementation, and execution services. Luban supplies the language, operation contracts and runtime checks; the host composes a qualified I/O provider. This makes the authorization boundary part of how operations run, rather than a convention the agent is expected to follow.

## Why use it for AI tools

An AI model can propose an operation, but that proposal is not permission to execute it. Luban gives the host a structured request it can validate and authorize before protected access. Unsupported operations fail explicitly; they do not fall back to a shell.

An agent tool can accept Luban source or construct typed requests directly. The host binds each invocation to the caller and workspace, supplies the authorization policy, and returns typed results to the agent. Bounds on input, traversal, work, and output constrain each supported operation. Concrete resource checks and, for writes, exact-state preconditions remain necessary even when the overall tool call is allowed.

The security goal is to make AI tool access explicit, bounded, and enforceable at the runtime/provider boundary. Prompts and model cooperation are not the enforcement mechanism. Applications without AI can use the same APIs.

## How it works

A small read pipeline looks like this:

```text
fd src "**/*.cs" | grep "Authority" | take 20
```

This finds C# files under `src`, searches their contents for the literal text `Authority`, and returns at most 20 matches. The pipe connects typed values between supported operations. The compiler validates the complete document and rejects unknown commands, options, or incompatible pipeline stages.

```text
Luban source or typed requests
             |
             v
     Compiled typed operations
             |
             v
     Host authorization checks
             |
             v
 Trusted provider + resource checks
             |
             v
         Typed results
```

Static preflight checks known requirements before protected target I/O. Execution checks the operation and the concrete resources it discovers, and checks whether results may be released. An allowed search does not authorize a later write; a returned path does not carry permission to access that file. Each subsequent operation must satisfy its own checks.

Luban is a language with an authorization-aware runtime. It does not provide an OS or container sandbox. Its guarantees depend on the selected provider profile, the host's authorization decisions, and the resource and consistency checks that profile supports. The current strict language accepts a closed set of operations; arbitrary commands, shell substitutions, and process execution are unsupported.

## Authorization and Hufu

[Penghou.Hufu](https://github.com/jenolaszlo-sketch/penghou-hufu) can translate Luban's typed requests and resource checks into authorization decisions. Its grants, approvals, revocation and authority evidence are maintained and qualified in Hufu's own project.

**Hufu is optional.** Luban's core does not depend on Hufu, Cedar, Fuwen, Zhinu, a host UI, or a particular agent. Another authorization system can implement the same interfaces and apply its own policy:

- `ILanguageAuthorizer` checks language preflight, effect start, concrete resource access, and result release.
- `IEffectAuthorizer` checks direct Read/Find/SearchText admission, concrete resource access and final result release; each resource check includes its action and request identity.
- `IFileChangeAuthorizer` checks the whole input set, each target, provider resource access and release of exact diff/merge candidates.
- `IPreviewAuthorizer` checks target and proposal admission, resource access, and release during capture.
- The separate mutation executors require host admission, live resource checks, start evidence, and outcome journaling.

Read APIs require an injected authorizer and have no permissive default. Replacing Hufu means supplying those decisions and, for mutation, the required host contracts. It does not remove the runtime's resource checks or stale-state preconditions. See the [architecture](docs/architecture.md) and [Hufu integration contract](../Penghou.Hufu/docs/luban-integration.md).

## Current capabilities

| Area | Implemented scope |
| --- | --- |
| Read language | Read, find, literal search, and typed `take`/`count` pipelines; bounded parsing, versioned semantic IR, and static preflight |
| Opt-in language v2 | Authorized `diff`/`merge`, bounded line windows, and UTF-8 search match spans with optional context; separate catalogue and IR identities |
| Direct API | Typed UTF-8 `Files.Read`, `Files.Find`, and `Files.SearchText` with explicit authorization and bounded results |
| Change preview | Read-only Local capture of existing-file UTF-8 byte patches, exact observations, and explicit unresolved coverage |
| Patch execution | Separate single-target and sequential batch executors for 1–64 distinct exact-target patches, with required host admission and per-node start/outcome evidence |
| Recovery inspection | Host-supplied authoritative receipts; resume only a matching completed prefix, with uncertain outcomes blocking automatic retry |
| Text diff and merge | Pure deterministic diff/merge, canonical transport, display hunks/unified export, and bounded untrusted import with exact in-memory materialization |

Language execution computes and returns observations and change proposals. It performs no writes. Applying a supported candidate uses the separate programmatic capture bridge and executor with fresh host admission and evidence. There are no textual apply/write commands.

The current leaf qualification passes **338 tests on each of .NET 8 and .NET 10** on Windows. The [completion ledger](docs/leaf-completion.md) records feature, package and CI evidence; the [consumer impact guide](docs/consumer-impact.md) records dependency and migration work.

## Preview, execution, and consistency

Preview captures a proposal; executing it requires separate admission. `CaptureComplete` means capture finished, while `CanCommit` remains false. WhatIf has no writer callback or mutation dispatch.

The separate executors check exact targets, payloads, original versions, current authorization, and required start evidence before mutation. Outcomes distinguish completed work, no mutation, and ambiguous results. Batches are sequential and non-atomic: earlier completed writes are not rolled back if a later patch fails.

The Local writer is qualified only for explicitly selected host-controlled Windows NTFS namespaces. The host must control aliases, mounts, and directory configuration against untrusted changes. It uses in-place writes and does not claim crash atomicity or general filesystem confinement. The read provider's path checks also do not prevent every concurrent path-replacement race. See the [single-patch](docs/single-patch-execution-profile.md), [batch](docs/batch-execution-profile.md), and [shared I/O](docs/shared-io-integration.md) profiles for exact guarantees.

## Diff and merge

Luban's text engines compute structured differences and deterministic three-way merges without filesystem I/O, Git, or authorization callbacks. They preserve UTF-8 bytes and newline state and report conflicts explicitly. A clean merge is a computed candidate; it grants no permission to apply it.

D3 adds canonical structured diff/merge transport, display hunks, unified export, and a bounded plain unified importer with exact caller-owned-source validation. Imported data remains untrusted; these APIs perform no I/O or grant authority. See the [transport profile and examples](docs/text-change-transport-profile.md). Authorized same-workspace file diff/merge now freezes exact hashes, lengths and provider versions, supports read-only candidate validation, and bridges into fresh capture and separate application. See the [file-change profile](docs/file-change-profile.md) and [application profile](docs/file-change-application-profile.md). Directory/snapshot operations, broader mutation kinds, richer language transforms, and Git integration have separate delivery gates. See the [diff/merge specification](docs/diff-merge-spec.md) and [roadmap](ROADMAP.md).

## Using Luban

The library and tests target .NET 8 and .NET 10. Neutral runtimes take an injected
`IWorkspaceProvider` and a logical `WorkspaceReference` containing only an ID.
The default build pins `Penghou.IO.Abstractions` and `Penghou.IO.Protocols`
`0.1.0-preview.1`; tests and the sample separately compose IO.Local. The physical
Local profile is Windows-only. Publication remains a delivery gate until the
[corrective ledger](../Penghou/docs/resource-abstractions-corrective-plan.md)
records the released packages.

For development against the coordinated source change, use sibling checkouts:

```text
repos/
  Penghou/
  Penghou.Luban/
```

From the Luban checkout:

```powershell
dotnet test Penghou.Luban.sln -c Release -p:UsePenghouSource=true
```

An explicit source build uses `-p:UsePenghouSource=true`; a different checkout
can be selected with `-p:PenghouRoot=C:\source\Penghou`. See
[shared I/O integration](docs/shared-io-integration.md) for candidate-feed and
published-package qualification. Luban is packable as `0.1.0-preview.1`; CI qualifies its exact dependency closure and a package-only consumer. Public NuGet publication remains a separate release gate.

For AI tool embedding, use `LanguageToolRuntime`: the host supplies identity, workspace, authorization and bounds, while the agent supplies Luban source. `Describe()` returns the supported command/profile catalogue; `ExecuteAsync` returns structured compilation or execution results, and `ToJson` preserves typed values and named status codes. See the [language manual](docs/language-manual.md) and [runnable host sample](samples/AiToolHost/Program.cs), which implements a scoped policy without Hufu. The [read-language profile](docs/read-language-profile.md) documents the underlying runtime; the [direct API profile](docs/direct-api-profile.md) explains its compatibility contract.

## Design and delivery

Luban owns typed operations, trusted effect descriptions, language execution and its conformance. Shared resource contracts and providers live in the Penghou.IO packages. Within Penghou, Hufu supplies authority, Fuwen supplies workflow control flow, and Zhinu supplies durable execution. Hosts can integrate other authorization and orchestration systems through the corresponding contracts.

- [Documentation index](docs/README.md)
- [Architecture](docs/architecture.md)
- [Current language manual and examples](docs/language-manual.md), [language specification](docs/language-syntax-spec.md), and [implemented read profile](docs/read-language-profile.md)
- [Typed-effect runtime](docs/typed-effect-runtime.md)
- [Roadmap](ROADMAP.md) and [implementation plan](docs/implementation-plan.md)

Licensed under [Apache-2.0](LICENSE).
