# Windows read-only language profile

Status: implemented and tested on Windows, .NET 8 and .NET 10, 2026-10-01.
This document describes the executable subset of the broader
[language specification](language-syntax-spec.md). Mutation planning, WhatIf,
commit barriers, Hufu governance and durable retention remain separate deliveries.

## Compile and execute

`Penghou.Luban.Language.LanguageCompiler.Compile` accepts bounded source or
programmatic `LanguageStage` statements, a host workspace ID, compiler options
and cancellation. It returns diagnostics or an immutable `CompiledDocument`.
It validates the entire document; no valid prefix survives an invalid suffix.
Compilation and static preflight perform no protected target I/O.

```csharp
var compilation = LanguageCompiler.Compile(
    "fd src \"**/*.cs\" | grep \"Authority\" | take 20", hostWorkspaceId);
if (!compilation.Succeeded)
    return; // Surface compilation.Diagnostics to the caller.

var runtime = new LanguageRuntime(hostWorkspace, hostProvider, hostAuthority);
var readiness = await runtime.PreflightAsync(invocation, compilation.Document!);
var result = await runtime.ExecuteAsync(invocation, compilation.Document!);
```

The host supplies `WorkspaceReference`, `EffectInvocation` and a required
`ILanguageAuthorizer`. There is no permissive default. Source cannot choose a
machine root, policy implementation, provider or host invocation. Execute runs
preflight again, even if the host already called it. Hosts should bind workspace
IDs immutably to their selected roots and retain the compiled document when
mapping its identity to authority requirements. The direct legacy API retains
its separate required `IEffectAuthorizer` and existing JSON request identities.

## Commands and streams

| Command | Aliases | Input | Output |
| --- | --- | --- | --- |
| `read path [--max-bytes n]` | `cat`, `gc`, `files.read` | First stage, explicit file | FileContent |
| `read [--max-bytes n]` | Same | FileReference stream, no path | FileContent stream |
| `find [root] pattern` | `fd`, `files.find` | First stage only | FileReference stream |
| `search query [root]` | `grep`, `files.search-text` | First stage, workspace root by default | SearchMatch stream |
| `search query` | Same | FileReference stream, no root | SearchMatch stream |
| `take n` | None | FileReference, FileContent or SearchMatch stream | Same type, bounded prefix |
| `count` | None | A value stream | One Count value; terminal stage |

Find or standalone search without a root selects the workspace root. `.` also names that root.
Find returns files, not directories. Search is literal, ordinal and case
sensitive; it reads strict UTF-8 and reports one-based lines, stripping terminal
CR from CRLF lines. Invalid UTF-8 files are omitted by search; explicit read
fails with InvalidDocument. Read also returns byte length and SHA-256 of the
exact bytes. No richer range, column, encoding or context schema is implied.

Each nonempty line begins a statement unless it starts with `|`, in which case
it continues the preceding pipeline. Blank lines and `#` comments are allowed.
The optional first-line directive is exactly `#!luban1`. Single quotes preserve
literal characters. Double quotes support only `\"`, `\\`, `\n`, `\r`, `\t` escapes.
Unquoted shell operators, backticks and `$(` substitution are rejected. Quoted
shell-looking text remains literal data. No substitutions or environment
expansion execute. Options use `--name value`; numbers are decimal integers
without units. Unknown commands, options, duplicate options and bad stream
edges fail compilation. `list`/`ls`, ranges, regex, select/where, writes, Git,
web and native developer tools are outside this profile.

Find options: `--max-depth`, `--max-entries`, `--max-matches`,
`--max-output-bytes`. Standalone search additionally accepts `--include` and
`--exclude` globs, `--max-file-bytes` and `--max-bytes-scanned`.
Piped search uses its incoming references; selection globs cannot redefine
that stream. Its traversal limits are retained in the canonical stage but no
directory traversal occurs. All per-node options and global limits participate
in semantic identity, including limits that do not constrain a particular edge.

## Paths and globs

Paths are validated workspace-relative Windows paths, normalized to `/`, with
one optional leading `./` removed. Absolute paths, traversal segments, ADS,
reserved device names and ambiguous trailing dots/spaces are rejected. Source
cannot widen this profile's path or filesystem qualification.

Globs are relative to the selected root: `*` matches zero or more Unicode scalars
inside a segment; `?` matches one scalar; a whole `**` segment matches zero or
more path segments. Thus `*.cs` matches direct files, while `**/*.cs` also matches
files below directories. ASCII case is ignored; other Unicode code points are
compared exactly. There are no bracket/brace expressions, escaping or embedded
globstars. Glob length is at most 256 UTF-16 code units; paths are at most 2048.
Matching and viable-directory-prefix checks use bounded dynamic programming,
with cancellation checks inside matching loops and a fixed document-wide
ceiling of 16,777,216 glob-work units (normalization lengths and DP cells).
Exhausting this profile ceiling returns LimitExceeded; no work count is exposed.
Legacy FileEffectRuntime basename-pattern behavior is unchanged.

## Authority and release

Every effect passes host checks at four phases:

1. Preflight checks every known node before any target access, including known
   downstream requirements and explicit selectors.
2. EffectStart checks current authority when an effect begins.
3. ResourceAccess checks the concrete action/path before provider access,
   including ancestors and enumeration candidates. Find never grants content
   read authority; each piped read/search rechecks its own node and resources.
4. Release checks effects and protected resource dependencies before returning
   any statement results, including results reduced by take/count.

Checks carry semantic document/node identity, descriptor/version, all profile
versions, workspace, stage arguments and host invocation. Concrete checks also
carry the distinct canonical shared-resource request identity. The private
bridge binds the active request, exact action/resource roles and generated host
scope. Source and pipeline values cannot inject a checker or reusable grant.
Unavailable, unknown, null or throwing authorization decisions fail closed.

`ReadyForKnownRequirements` is static readiness, not a grant or complete dynamic
coverage. `HasDynamicTargets` marks find/search and piped reads. Dynamic children
remain individually authorized. Denied listing candidates and denied/missing
search reads are omitted from the authorized view; unavailable authorization
aborts. A count reports only the selected authorized observations, not a global
filesystem count. Completeness describes the authorized observation view;
denied or vanished nested candidates do not prove filesystem-wide absence.
Final release conservatively retains dependencies from
discarded upstream values, successful no-match reads, empty directory observations,
and every permitted metadata probe (including ancestors and nonmatching entries)
that contributed to a derived result or coverage decision. It authorizes immediate
return only; retained content needs a future host retention/release contract.

## Limits, truncation and failures

Hard compiler ceilings are 64 KiB source, 4096 tokens, 64 statements, 128 nodes
and 8192 UTF-8 bytes per literal. Hosts may lower them. Global execution ceilings
are 100,000 authority calls across static preflight, effect-start, resource and
release checks, 16 MiB total read bytes, 4 MiB per
logical intermediate buffer, 4 MiB final output, 10,000 values and 30 seconds.
Preflight work is also bounded by the node ceiling and the same deadline.
Compiler/provider limits cannot be raised beyond the frozen profile.
Logical byte accounting includes fixed record overhead and is a work bound,
not an exact managed-memory measurement. Traversal frontiers and retained
release dependencies have independent value/intermediate-byte caps.

Find defaults: depth 16, 10,000 candidate probes, 1000 matches and 256 KiB result.
Search shares those defaults, with 1 MiB per file and 10 MiB scanned bytes;
defaults are reduced to fit selected global ceilings. Depth is capped at 16. Denied candidates and end-of-enumeration probes consume private provider
work, without disclosing hidden counts. A node captures one bounded page per
directory, stops on incomplete coverage and never interprets a prefix as a full
all-match result. No snapshot or stable traversal-order guarantee is provided.

Pipelines currently buffer bounded stage results. `take` reduces returned values
and marks Truncated, but does not cancel or bypass upstream effects, admission
or limits. Discovery/search node ceilings yield explicit truncated observations;
document-wide budget exhaustion fails with LimitExceeded. A failed document
returns no partial public Statements; already completed authorized reads still
occurred. Caller cancellation propagates; deadline exhaustion returns
LimitExceeded. There are no requested mutations in this profile.

The shared [reader profile](../../Penghou/docs/local-reader-profile.md) defines
supported filesystems and observed reparse-point checks. Windows path access
does not prevent replacement races and is not a sandbox. Cooperative OS I/O
deadlines, hard links and mount boundaries retain that provider's limitations.

For agent embedding, [LanguageToolRuntime](language-manual.md#tool-results-and-errors) composes compilation and execution, provides a versioned capability description, and serializes typed values with explicit kind discriminators. Execution failures include safe node/limit diagnostics when known. Statements expose `CoverageReasons` and `AuthorizedViewComplete` in addition to `Truncated`; completeness applies only to the authorized view. Individual oversized files are skipped with FileSizeLimit coverage, while aggregate scan exhaustion stops scanning. Protected observations from skipped work retain their release dependencies.

See [canonical IR](semantic-ir.md), [integration build](shared-io-integration.md)
and the [implementation plan](implementation-plan.md). Behavioral and golden
identity tests run with `dotnet test Penghou.Luban.sln -c Release` on both targets.


## Separately qualified v2 extension — 2026-10-03

The v1 command/result/identity contract above remains the default. Hosts can
select language/IR `2`, catalogue `windows-text-change-v2` and provider profile
`local-windows-read-v1`. A matching `#!luban2` header is optional; source cannot
select this profile independently of the host.

V2 adds literal-path `diff` and three-way `merge`, bounded `read --start-line n
--line-count n`, and search results with absolute UTF-8 match spans and optional
`--context-lines 0..20`. All effects retain whole-document preflight, concrete
input/resource checks and protected-result release. File-change requests bind
all roles/options and provider observations. A clean merge returns edits against
ours. Conflicts remain structured protected observations.

Whole-document resource, read, intermediate, output and value bounds include
these operations. Diff/merge reserves a conservative aggregate input budget;
repeated stages cannot bypass it. Range reads still require a complete bounded
UTF-8 input. V2 search has a span-bearing result even when context is zero, while
v1 preserves its original SearchMatch JSON shape.

Execution remains read-only. Applying candidates is a separate host operation
through the [capture bridge](file-change-application-profile.md). See the
[manual](language-manual.md) for syntax and the [completion ledger](leaf-completion.md)
for current evidence.
