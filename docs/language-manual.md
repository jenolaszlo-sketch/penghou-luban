# Luban language manual

This manual covers the default Windows read profile (v1) and the separately
selected text-change profile (v2).
It is intended for AI tool authors and agents using Luban. The broader
[syntax specification](language-syntax-spec.md) also contains proposed features;
commands listed here work through `LanguageToolRuntime` or `LanguageRuntime`.
Source snippets are Luban input, not commands for an operating-system shell.

## Start with a workspace and authorization

The host selects the workspace root, authenticated invocation, authorizer and
bounds. The agent supplies source. Each effect and concrete resource must pass
authorization, including release of protected results. Hufu can implement the
policy, or the host can supply another `ILanguageAuthorizer`.

The [AI tool host sample](../samples/AiToolHost/Program.cs) uses an independent
policy permitting C# reads under `src`. From the checkout on Windows:

```powershell
dotnet run --project samples/AiToolHost -f net10.0 -p:UsePenghouSource=true -- . --describe
dotnet run --project samples/AiToolHost -f net10.0 -p:UsePenghouSource=true -- . 'find src **/*.cs | count'
```

Use `-f net8.0` for .NET 8. The source build also needs the sibling Penghou
checkout; see [shared I/O integration](shared-io-integration.md).

`Describe()` reports command names, aliases, signatures, options, profile
versions, platform and host bounds. Capabilities describe available operations;
they do not reveal or grant permissions.

## Commands

| Command | Result | Aliases |
| --- | --- | --- |
| `read path [--max-bytes n]` | UTF-8 content, byte length and SHA-256 | `cat`, `gc`, `files.read` |
| `find [root] pattern` | File references | `fd`, `files.find` |
| `search query [root]` | Matching lines with path and one-based line number | `grep`, `files.search-text` |
| `take n` | At most n incoming values | none |
| `count` | Number of incoming authorized observations | none |

An omitted root or `.` means the host-bound workspace. `read` needs an explicit
path when first in a pipeline. Piped `read` and `search` consume file references;
piped `search` cannot select a second root or set include/exclude globs. `count`
must be the final stage. Search is literal and case sensitive.

```text
read "src/Program.cs" --max-bytes 16384
find src "**/*.cs"
find src "**/*.cs" | search "Authorize" | take 10
find src "**/*.cs" | take 3 | read
find src "**/*.cs" | count
search "TODO" src --include "**/*.cs" --exclude "**/Generated/**"
```

`take` limits returned values; it does not cancel upstream discovery or reads.
There is no implicit lazy evaluation, stable first-N ordering or global snapshot.

## Paths and globs

Use workspace-relative paths. `/` and native `\` separators are normalized;
one leading `./` is allowed. Absolute paths, `..` traversal, alternate streams
and invalid Windows paths are rejected.

- `*` matches within one path segment: `*.cs` selects direct files.
- `?` matches one Unicode scalar within a segment.
- A whole `**` segment matches zero or more segments: `**/*.cs` includes direct
  files and descendants.

Globs are relative to the selected root and compare ASCII letters without case.
Other Unicode scalars compare exactly. Bracket expressions and brace expansion
are unsupported. Source selectors can narrow selection; they cannot widen policy.

## Quoting and documents

Use single quotes for literal characters. Double quotes recognize only `\"`,
`\\`, `\n`, `\r` and `\t`. Neither form interpolates variables or executes text.
Quote values beginning with `--` so they are interpreted as data.

```text
#!luban1
# A bounded source search
find src "**/*.cs"
  | search 'AuthorizationDenied'
  | take 10
```

Each new nonempty line begins a statement unless it starts with `|`, which
continues the previous pipeline. Blank lines and comments are allowed. The
optional first-line directive is `#!luban1` for v1 or `#!luban2` for v2;
it must match the host's selection. Diagnostic offsets and
lengths use UTF-16 code units in the original source, including CRLF separators.

## Limits and incomplete results

Options use `--name value`; numbers are plain decimal integers without units.
Unknown or duplicate options and incompatible pipeline edges fail compilation.

Find accepts `--max-depth`, `--max-entries`, `--max-matches`,
`--max-output-bytes`. Search accepts those plus `--max-file-bytes` and
`--max-bytes-scanned`; standalone search also accepts `--include` and `--exclude`.
The host can lower global bounds. Source cannot exceed the frozen profile.

Oversized individual search files are skipped without content reads, with
`FileSizeLimit` coverage. An aggregate scan limit stops further scanning.
Pipeline discovery may already have read permitted metadata for a skipped file;
that observation still needs release authorization. `CoverageReasons` identifies
traversal, depth, file-size, scan-byte, match, output-byte and explicit take limits.
`Truncated` remains available for simple consumers.

Completeness applies to the authorized observation view, not all filesystem
content. Denied or missing candidates can be omitted without disclosing hidden
names or counts. A partial count remains in a truncated statement; it is not
evidence that no other files or matches exist.

## Tool results and errors

`LanguageToolRuntime.ExecuteAsync(source, invocation)` compiles and executes
through the same required authorizer as `LanguageRuntime`. Invalid source
returns compilation diagnostics and performs no target I/O. Failed execution
returns no partial statements, even if authorized reads already occurred.

`LanguageToolRuntime.ToJson` emits schema version, document/profile identity,
named status codes and typed values with a `kind` discriminator. Execution
diagnostics carry a safe code, a node identity when known, and a limit name when
known. Provider exceptions and denied resource names are not included.
Caller cancellation propagates; deadline exhaustion is a limit failure.

## Opt-in v2: file changes and focused reads

The host selects v2 explicitly. Source cannot switch the host's catalogue,
provider, authorization or limits:

```csharp
var versions = new LanguageVersions("2", "2", "windows-text-change-v2",
    "local-windows-read-v1");
var tool = new LanguageToolRuntime(workspace, provider, authorizer,
    new LanguageCompilerOptions(Versions: versions));
var result = await tool.ExecuteAsync(
    "#!luban2\ndiff src/before.cs src/proposed.cs", invocation);
```

| V2 command | Result |
| --- | --- |
| `diff before-path after-path` | `text-diff`: exact before/after hashes, byte lengths and byte-coordinate edits |
| `merge base-path ours-path theirs-path` | `text-merge` or explicit `text-merge-conflict`; clean edits target **ours**, not the base |
| `read path --start-line n --line-count n` | `file-window`: bounded lines with input identity and separate input/window completeness |
| `search query [root] [--context-lines n]` | `search-context-match`: absolute UTF-8 match start/length and optional surrounding lines |

```text
#!luban2
diff src/current.cs proposals/current.cs
merge snapshots/base.cs src/current.cs proposals/theirs.cs
read src/current.cs --start-line 20 --line-count 12
search 'Authorize' src --include '**/*.cs' --context-lines 2 | take 10
```

Diff and merge are first-stage operations with literal workspace-relative input
paths; all known inputs pass admission before content is read. Repeated paths
share one observation. Denied, incomplete or invalid inputs cannot become a clean
candidate. Conflicts are protected structured results, never an implicitly
resolved file. `take` and `count` also accept the new v2 result streams; a count
counts result values, not edits or conflict regions. Text-change source commands
use the frozen default text limits; typed stages can select bounded options.

Line windows require both positive options, with at most 10,000 requested lines.
They read a complete bounded authorized UTF-8 input before slicing, preserving
terminators and bytes. They do not enable arbitrarily large-file reads.
`CompleteInput` describes that input observation; `CompleteWindow` means the
requested number of lines was available. A request beyond EOF can return an
empty, incomplete window from a complete input. Empty files and the position
after a final newline do not invent an additional line.

V2 search always returns match byte spans, including with the default zero
context. Context is bounded to 0–20 lines on either side. Offsets are zero-based
UTF-8 bytes in the original input, including BOM and line terminators, rather
than UTF-16 columns. Search retains v1's first literal match per matching line;
v1 keeps its existing `search-match` JSON shape without these fields.

Returned change values are untrusted comparison/proposal facts and carry no write authority. They are not `FilePatchCandidate` objects or captured executor plans. The programmatic `FileChangeRuntime` constructs full candidates with fresh opaque provider versions and candidate identities; a host must compare fresh observations against any previously approved proposal before applying it.
To apply a supported existing-file change, use the programmatic
[capture bridge](file-change-application-profile.md) and separate executor.
The [file-change profile](file-change-profile.md) defines exact-state validation,
authorization phases, bounds and candidate identity.

## Available separately

Patch preview/execution and pure in-memory text diff/merge are programmatic APIs.
There are no source commands for writes, apply, Git, build/test, regex, loops,
variables, shell substitutions or arbitrary processes in either profile.
Unsupported syntax fails explicitly.
See the [read reference](read-language-profile.md) for exact runtime semantics,
and the [roadmap](../ROADMAP.md) for separately qualified later features.
