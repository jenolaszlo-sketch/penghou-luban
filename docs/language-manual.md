# Luban language manual

This manual covers commands implemented by the current Windows read profile.
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
dotnet run --project samples/AiToolHost -f net10.0 -- . --describe
dotnet run --project samples/AiToolHost -f net10.0 -- . 'find src **/*.cs | count'
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
optional first-line directive is exactly `#!luban1`. Diagnostic offsets and
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

## Available separately

Patch preview/execution and pure text diff/merge are separate programmatic APIs.
There are no source commands for writes, patches, Git, build/test, line ranges,
regex, context windows, loops, variables, shell substitutions or arbitrary
processes in this language profile. Unsupported syntax fails explicitly.
See the [read reference](read-language-profile.md) for exact runtime semantics,
and the [roadmap](../ROADMAP.md) for separately qualified later features.
