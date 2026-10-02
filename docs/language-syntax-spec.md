# Luban language syntax specification

Status: Revised for the implemented minimal profile, 2026-10-01. The bounded
parser, compiler, typed semantic IR, static preflight, and read pipeline executor
are implemented. The profile supports read, find, literal search, take, and count.
Broader syntax, transforms, mutation, and a command-line frontend remain pending.
Language runs require `ILanguageAuthorizer` at Preflight, EffectStart,
ResourceAccess, and Release. The direct API continues to require
`IEffectAuthorizer`. See the [read profile](read-language-profile.md),
[semantic IR profile](semantic-ir.md), and [ADR 0002](decisions/0002-typed-surface-language.md).

This is the canonical review of the
[supplied syntax proposal](archive/language-syntax-proposal-2026-10-01.md).
Its shell-like examples describe Luban commands, never system command execution.
The [runtime specification](typed-effect-runtime.md) governs effect behavior;
this document defines how proposed source maps to that behavior.

## Compilation and ownership

```text
Bounded source + host-selected language/catalogue versions + workspace binding
  -> lexical parsing -> syntax tree -> typed semantic IR
  -> whole-document type/profile/limit validation -> requirement extraction
  -> host admission -> per-effect/per-resource authorization
  -> bounded effect and transform execution -> typed results and evidence
```

Hufu is an optional host authority implementation. Luban itself requires a
host-supplied checker; source cannot select an allow-all checker or alter policy.
Fuwen supplies workflow branching, loops, model activities and fan-out; Zhinu
supplies durable execution and recovery. Luban composes finite dataflow nodes
and bounded operations without introducing a workflow scripting engine.

Compilation determines possible effect kinds and conservative resource selectors
or ceilings. It need not enumerate every filename before traversal. Runtime
references are validated and each concrete target reauthorized before use.
Source aliases cannot bypass exclusions, delegation limits or release policy.

## Supported versus proposed profiles

| Feature | Current implementation | Broader direction |
| --- | --- | --- |
| Read/Find/literal SearchText | Direct and language APIs on Windows, including aliases | Broader operations require qualification |
| Required authorization | Direct API uses IEffectAuthorizer | Language requires ILanguageAuthorizer at Preflight, EffectStart, ResourceAccess, and Release |
| Glob selection | Language root-relative *, ?, **; legacy recursive basename patterns unchanged | Other pattern features require qualification |
| Results | FileReference, FileContent, SearchMatch and Count language values; legacy results unchanged | Richer fields need their own contracts |
| Source/pipelines/pure transforms | Minimal parser, typed IR, static preflight, take/count | Broader syntax and transforms remain pending |
| Ranges/regex/context/metadata | Not implemented beyond current result fields | Individually qualified options/operations |
| Mutation/Git/developer commands | Not implemented | Separate effect qualification and modes |
| Diff/merge language commands and structured pipeline values | Not implemented in language v1 | Pure text API is separate; qualify new descriptors/catalogue/IR under ADR 0005 |

Do not translate an unsupported language feature into a weaker existing API
silently. The implemented language profile exposes read commands, qualified path
globs, and take/count; the larger proposed command list is not a launch promise.

## Command catalogue and canonical aliases

Commands are host-controlled catalogue entries with canonical name and aliases,
typed arguments/options, input/output types, semantic/schema version, limits,
effect kind/risk, and trusted requirement mapping. Pure transforms have typed
signatures and no external-effect mapper. Reject ambiguous alias registration.

The following table includes broader proposed operations. For the supported catalogue and executable examples, use the [current manual](language-manual.md).

| Canonical syntax | Familiar alias | Typed operation |
| --- | --- | --- |
| list [path] | ls | Files.List, direct children |
| read path [range] | cat, gc | Files.Read or Files.ReadRange |
| find [root] pattern | fd | Files.Find |
| search query [root] | grep | Files.SearchText |
| delete path | rm | Files.Delete |
| copy source destination | cp | Files.Copy |
| move source destination | mv | Files.Move |

Also plan exists, stat, mkdir, rmdir, write, patch and replace when their typed
operations qualify. `files.read` and similar names may be catalogue aliases,
with identical semantic identity. The compiler recognizes namespaces such as
`git status` / `git st` and `dotnet test` by their full registered names, not a
generic executable followed by arguments. Unknown names/subcommands fail.

`gc` is an implemented Get-Content-style alias of the typed read operation; no
PowerShell command runs. [ADR 0003](decisions/0003-shared-resource-interfaces.md)
places concrete I/O behind shared Penghou.IO.Abstractions providers; the Windows read slice now uses Penghou.IO.Local.
The compiler still validates effect semantics; authorization remains a runtime
effect/resource boundary.

## Proposed diff/merge catalogue

[ADR 0005](decisions/0005-deterministic-diff-merge.md) and the
[diff/merge specification](diff-merge-spec.md) add a separate staged feature.
Proposed pure `text.diff`/`text.merge` descriptors consume bounded immutable values;
resource-bearing `files.diff`, `files.merge.compute`, `files.patch.validate` and
`files.patch.apply` require independently qualified input/resource/release and
mutation contracts. These names are illustrative, not registered commands.
The initial pure API does not extend current source parsing or execute external
diff/patch/Git programs. Future syntax needs a new versioned typed catalogue/IR,
whole-document validation and exact approval bindings. Merge computation and
validation do not write; apply reuses capture and separate executor admission.
The original proposal's `if`/`yield`/method calls introduce no workflow control flow.

## Lexical and document rules

The submitted source is bounded by UTF-8 bytes, tokens, statements, pipeline
nodes, literal/payload size and nesting. Parser/compiler work needs its own
budget and cancellation. These bounds are independent of runtime result limits.

The basic form is `command arguments options`; long options use `--name value`
or a registered flag such as `--hidden`. Only catalogue-defined short aliases
exist. Reject unknown/duplicate options, invalid positional counts and conflicting
flags. Values are parsed by the command schema, not inferred from arbitrary text.
Named `key=value` arguments and clustered short flags are deferred.

Double-quoted strings support explicit quote/backslash escaping; single-quoted
strings are literal. The exact escape table must be frozen before parser delivery.
No quote form performs interpolation. `$HOME`, `%USERPROFILE%` and `${TOKEN}`
are literal data when passed as values; assignment/substitution constructs are
invalid. Unquoted Windows path backslashes are path separators, not escapes.

`#` starts a comment outside quotes and opaque payloads. A version directive,
currently `#!luban1`, is recognized only as a dedicated first line before
comment stripping. Interactive hosts may supply a version instead; persisted
compiled work always records it. A directive is not an executable shebang.

Newline separates statements. A following line beginning with `|` continues the
preceding pipeline; otherwise it starts a new statement. Blank/comment lines
between pipeline stages may be ignored under the same continuation rule.
Semicolons are an optional later feature, not silently accepted by the first
profile. `&&`, `||`, background `&`, command substitution and shell redirects
are invalid. The later opaque-payload delimiter below is a dedicated Luban
syntax production, not a shell redirect. Comparison `<` and `>` exist only inside the typed `where` grammar;
they never mean redirection. Backtick execution and `$()` are invalid constructs.

## Paths, selectors and literals

An omitted root means the host-bound current workspace, not a process working
directory chosen by source. Normalize `.` as that root and an optional leading
`./`. Reject traversal, absolute/drive/device/UNC paths and alternate streams.
The parser may normalize native `\` separators to `/`; providers consume one
workspace-relative representation. A native separator being accepted in source
does not change the direct API's current slash-only contract.

Quoted paths allow spaces. Symbolic `workspace.main:path` is reserved for later
explicit-workspace grammar and must not be mistaken for a machine drive or ADS.
Workspace binding, path/object qualification and current resource checks remain
host/provider responsibilities. Lexical normalization is not containment.

Globs are data; they do not expand in an OS shell. The implemented language
semantics: `*` matches zero or more Unicode scalars within one path segment, `?`
matches one Unicode scalar, and `**` matches zero or more complete segments only
when it is itself a complete segment. Matching covers the complete root-relative
path. ASCII letters compare case-insensitively; other Unicode scalars compare
exactly. Inputs require valid Unicode scalar data and Windows-valid literal
segments. `**/*.cs` includes matching direct children and descendants; `*.cs`
selects direct children of the specified root. Matching and path/glob lengths are
bounded. Do not map this to the legacy recursive basename-pattern API, whose
semantics remain unchanged.

Schemas distinguish strings, integers, booleans, paths, ranges, globs, sizes and
durations. For proposed numeric suffixes use kb/mb = decimal units and KiB/MiB =
binary units; ms/s/m have explicit duration units. Reject overflow, negatives
where invalid, ambiguous units and implicit conversions. Version these choices.

## Reading and searching

`read path` / `cat path` maps to Files.Read. Proposed line ranges are one-based
and inclusive: `100:160`, `100:`, `:160`, or `120` for a single line. Require at
least one endpoint, reject zero/reversed/overflowing ranges, and normalize to a
typed range. Define empty/past-EOF behavior in ReadRange before enabling syntax.
Open-ended ranges still have provider byte/line/work limits; they are not an
unbounded read. Current ReadRange is unimplemented.

`find pattern` uses the current root; `find root pattern` supplies it explicitly.
`list` is non-recursive. Future Find produces bounded FileRef values containing
workspace-relative identity and permitted metadata. Metadata is optional; a
filter on a missing/unsupported field fails validation or has explicitly typed
unknown handling, never silently substitutes zero.

`search query root --include pattern --exclude pattern` selects files under an
admitted scope. Pipeline search takes typed file references instead of a new
root. Reject arguments that ambiguously select both inputs. Literal text is the
initial mode; `--regex`, case options, column/context and richer matches require
separate qualified semantics. Contradictory case flags reject. Hufu exclusions
apply regardless of source include/exclude globs; source cannot broaden them.

Each effect result exposes completeness/truncation and typed errors separately
from its value stream. Empty or truncated results are not proof that no resource
or match exists. The current SearchText result is matched lines, not the full
proposed SearchMatch schema; adapters must not invent missing columns/context.

## Typed pipelines and bounded pure transformations

```text
fd src "**/*.cs" | grep "Authority" | take 20
DirectorySelector -> Stream<FileRef> -> Stream<SearchMatch> -> Stream<SearchMatch>
```

This is typed dataflow, not stdout transport or user-defined iteration. Validate
every edge's input/output type before execution. The built-in search node may
read a bounded discovered file set under its registered contract, with checks
for each file. Source cannot insert arbitrary effect callbacks into that scan.
Effect-derived references must retain workspace/provenance and be revalidated;
projecting a path string does not turn it into an authorized FileRef implicitly.

The implemented transforms are bounded `take` and `count`. Skip, select, where,
sort, and unique remain proposals. `where`
uses catalogue-known fields and typed literals with ==, !=, >, >=, <, <=,
contains, starts-with and ends-with. No function calls, reflection, effects,
dynamic code or host object access. Boolean composition and `%` projection
aliases remain deferred; `% { effect }` is never an iterator. Record projection
needs its own grammar before accepting braces outside literal payloads.

Each implemented transform has item/byte/work limits. Proposed sort/unique may
require bounded materialization; they cannot drain an unbounded source. Count
over truncated input is explicitly partial. Take 20 bounds downstream values,
not all discovery or search work upstream; producers still enforce their own budgets. Do not push
filters/limits across effect nodes unless the change preserves the admitted
effects, versions, errors and completeness semantics. A pure transform adds no
external authority but must not expose metadata absent from its input.

## Literal mutations and preconditions

All mutation syntax remains planned and validates against qualified typed
operations. Existing-file patch/replace/write requires an exact expected
content/object version or an explicitly supported create-if-absent mode.
Copy/move/delete bind all affected resources, source/destination preconditions
and overwrite/absence behavior. Compact syntax must never omit these semantics
or implicitly authorize unconditional overwrite.

Use an opaque delimited literal payload for write/patch when implemented,
provisionally a quoted heredoc delimiter on a dedicated line:

```text
patch src/Foo.cs --hash <full-sha256> <<'LUBAN_PATCH'
@@ -42,1 +42,1 @@
-old value
+new value
LUBAN_PATCH
```

The payload has no interpolation, comment handling or command parsing. Choose a
delimiter absent from content and define byte/newline preservation precisely.
The original proposal's `write path { ... }` form is illustrative: embedded
source braces make a generic block grammar ambiguous, so it is not accepted
until a distinct opaque-block delimiter rule is specified. No parser evaluates
code embedded in a payload. `<full-sha256>` is an explanatory placeholder,
replaced by a complete validated digest; shortened hashes are insufficient.

`patch --hash` is mandatory for an existing file unless host tooling supplies
the exact version in IR before admission. Replace's expected match count is an
additional check, not a substitute for a version precondition. Recursive delete
is explicitly classified and may remain unavailable; rmdir means empty directory
only. Failed preconditions cause zero mutation under the provider's tested
protocol. Whole-document parsing does not make multi-effect mutations atomic.

## Validation, authorization and failure boundaries

Strict workflow execution parses and validates the whole bounded document before
the first effect: syntax, command/option registration, types, versions, catalogue
profile, supported effects and limits. Do not execute a valid prefix followed by
an unknown command. Interactive streaming is a future separately advertised mode.

The implemented static preflight checks effect nodes against known targets, trusted requirement
mappings, scope/limit ceilings, current authority and approval readiness before
upstream work. This static preflight reads authority state, not protected target data.
A known denied target blocks before protected target I/O. Unsupported and
indeterminate checks retain their own typed status; incomplete dynamic-target
coverage must never be rendered as full authorization. Static preflight neither reserves
authority nor supplies a durable permission token. Versions or payloads requiring
protected reads are resolved only through a separately authorized bounded
discovery/preparation phase; do not label those reads as pure preflight.

[ADR 0004](decisions/0004-preview-resolution-commit-barrier.md) and the
[preview/commit design](preview-resolution-commit-barrier.md) define that phase:
authorized bounded reads resolve dynamic targets and capture proposed mutations
in an immutable plan. The complete known mutation set, exact manifests/payloads,
coverage and observation preconditions must pass batch admission before the
executor releases its commit barrier. WhatIf never dispatches requested mutations
or opaque/lazy effects. Required incomplete coverage blocks, without lazy
downgrade. A tool changing future selection assumptions requires a separately
resolved/admitted dependent segment. Parsing/preview/admission does not make the
batch an atomic transaction, and all actual protected I/O still receives live checks.

Each statement remains independently represented in IR. The required language
authorizer receives Preflight, EffectStart, ResourceAccess, and Release phases;
the direct legacy runtime retains its separate required IEffectAuthorizer. A later
denial, precondition failure or
provider outage can occur after earlier effects completed; parsing first neither
rolls them back nor guarantees all statements will succeed. Define stop-on-error
and retained receipts in the document executor before enabling multi-statement
execution. No automatic retry, conditional branch or compensation is implicit.

The current executor processes bounded buffered values with cancellation and
independent upstream work limits; it makes no lazy-stream or backpressure claim.
Keep intermediate values bounded and avoid automatic durable artifacts unless
explicitly required and authorized by the host recovery/debug/release contract.
Define backpressure and cleanup for failure/cancellation before enabling a
pipeline provider. Final mutation begins only after its required inputs,
preconditions and current authorization are ready; preparation is not permission.

Pure transforms add no external rights. A discovery-to-search pipeline needs
traversal plus content-read authority, even if take later discards most matches.

The same rule applies to a proposed list-to-content-to-write pipeline. Allowed
`ls` permits listing only; `gc` separately requires content reads; a later write
requires its own exact-effect and destination authority. If static admission can
determine a required write is denied, block the document before protected I/O.
A denial discovered at runtime may follow completed reads but causes zero
unauthorized write. FileRef/provenance never grants access to the next node.
Before enabling this syntax, define bounded Stream<FileRef> -> Stream<FileContent>
read signatures, directory-item behavior, and supported write input signatures,
payload bounds and version preconditions. Do not silently concatenate file
contents, stringify streams, or accept both pipeline and literal payload inputs.
Whole-plan ceilings remain restrictive; root authorization never grants every
discovered child. Results retain their independent access/release policy.

Unknown commands, unsupported features, malformed types and invalid edges are
structured Luban diagnostics with source spans. Authority/availability failures
come from the host checker; provider/precondition/ambiguity failures retain their
own typed categories. Diagnostic text is a rendering, not a wire contract.
Do not expose actual hidden resource/hash values in errors without authorization.

## Canonical IR and version identity

Record source-language version, catalogue/effect/transform semantic versions,
schema and normalized typed values/defaults, ordered nodes and input edges,
workspace binding and payload digests in a versioned deterministic semantic
representation. Alias, whitespace, comments and equivalent path/range spellings
produce identical IR within the same admitted version/profile. Preserve original
source separately for diagnostics under evidence policy.

Do not hash a pretty-printed source string as durable authority identity. The
current semantic IR and versions are defined in the [semantic IR profile](semantic-ir.md).
Syntax changes with equivalent lowering must not silently reinterpret persisted
IR. Store execution IR/versions or an immutable protected reference; the original
source alone is insufficient.

The implemented `RequestDigest` hashes a direct API request's JSON. It binds that
direct request for host checks but is not the language IR identity or a substitute
for catalogue/version identity. Language execution carries its semantic identity
and versions through the language authorization protocol; this does not change
old direct-request meanings.

Compiled IR is still an input requiring validation. A hash establishes identity,
not trust or permission. Hosts validate its schema, node/edge types, bounds,
catalogue registrations, versions, workspace bindings, and trusted requirement
mapping even when no source parser is involved. A serialized node cannot supply
an alternative handler, weaker rights, or an agent-chosen authorization checker.

## Extensions and delivery gates

Only trusted host registration may introduce command/transform descriptors.
Extensions declare typed input/output, schema/version, authority mapping, bounds,
transitive risk and provider guarantees; registration is not execution authority.
Reject generic exec-anything extensions from StrictEffects. Queries or package
commands require their own constrained semantics; a namespace does not make an
arbitrary query/program safe. Pure transforms cannot smuggle in external I/O.

The implemented order freezes lexing/type/version rules, parser/compiler and
canonical IR, bounded take/count transforms, qualified read effects, and static
preflight. Programmatic capture-only resolution is implemented with no mutation
dispatch. Separate single-target and bounded exact-target batch executors are
implemented under an explicit host-controlled namespace and mandatory
admission/start/completion contract; they add no textual mutation syntax to
language v1 and are not dispatched by WhatIf. Batch executions are sequential,
non-atomic, and stop at the first failure; recovery is host-supplied and uncertain
or NoMutation entries are never retried automatically. See the [batch execution
profile](batch-execution-profile.md). Add other syntax only as effect semantics
and execution boundaries are qualified.
Required tests cover invalid suffixes, unknown commands
after valid prefixes, bad type edges, quoting/comments/directives, rejected shell
operators, parser/work budgets, alias equivalence, per-resource authorization,
partial/truncated streams and canonical-version compatibility. Broader parser
features remain later work.

