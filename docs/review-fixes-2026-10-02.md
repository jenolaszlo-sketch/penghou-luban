# Review fixes and qualification — 2026-10-02

The four reproduced defects are fixed. This delivery also closes the direct
API's action/release policy gap, improves search continuation and diff scale,
and adds a read-only AI tool contract with discovery, typed JSON and diagnostics.

## Delivered changes

- Direct admission and resource/release authorization share a bounded deadline.
  Caller cancellation propagates; timeout returns DeadlineExceeded, and late
  decisions are not used as grants.
- Direct IDs use strict Unicode/UTF-8 validation before authority or target I/O.
- Programmatic Find patterns and Search include/exclude selectors obey the
  host-selected literal-byte budget.
- Lexer diagnostic offsets use UTF-16 coordinates in original LF/CRLF/lone-CR
  source, including supplementary Unicode characters.
- Direct policies receive Admission/ResourceAccess/Release, concrete action and
  resource request identity. Whole-operation and private per-resource release
  checks must pass before returning results. Dependencies are capped.
- Search skips individually oversized files and remains incomplete. A per-file
  exclusion precedes aggregate scan checks because it consumes no read budget;
  aggregate exhaustion still stops scanning.
- Diff trims equal leading tokens before bounded LCS allocation. An independent
  full-matrix oracle guards repeated-line edit parity. Tie-breaking and byte
  coordinates are unchanged, and merge shares one work budget.
- LanguageToolRuntime composes compilation/execution, publishes host-selected
  capabilities and produces versioned typed JSON. The host chooses identity,
  workspace, policy and bounds; source chooses none of them.
- Statements expose authorized-view coverage reasons. Execution failures carry
  safe node/limit diagnostics when known, with no partial public values or raw
  provider exception details.
- The [manual](language-manual.md), [direct profile](direct-api-profile.md) and
  [independent-policy host](../samples/AiToolHost/Program.cs) cover current usage
  and demonstrate integration without Hufu.

## Qualification

Windows x64, .NET SDK 10.0.401. Final Release suite: **227 passed, zero failed,
zero skipped on each of .NET 8 and .NET 10**:

```powershell
dotnet test Penghou.Luban.sln -c Release --no-restore
```

The sample was built/run on both runtimes with a permitted source count. A
.NET 10 read of README.md was rejected by its src-only policy, with no protected
statements. Tool tests verify typed JSON, denial without content disclosure,
discovery without authorization calls, and partial take/count coverage.

Independent .NET 10 probes against the fixed Release assemblies:

| Original case | Fixed observation |
| --- | --- |
| Literal budget 8, ten-character glob | Source and typed compilation both reject |
| CRLF command at offset 16 | Diagnostic offset is 16 |
| 2,200-line file with one-character edit | Succeeded |
| Large file before small match | Standalone and piped search return the small match, incomplete |
| Non-cooperating admission plus cancellation | Wait finishes with propagated cancellation |
| Lone surrogate caller ID | InvalidRequest before access |

## Compatibility and scope

Read grammar and canonical IR encoding are unchanged; existing identity and
tie-break vectors pass. Result fields and direct authorization metadata are
additive. Direct authorizers receive additional final release calls; policies
must use phase/action metadata rather than infer access from callback ordinals.
The migration fixture now denies concrete content access while allowing release
of permitted metadata.

Hufu remains optional. This qualification covers neutral/runtime/test policies,
not Hufu lifecycle adapters, production mutation recovery, non-Windows providers
or namespace confinement. No commit, publication or deployment is implied.

Line-range/context reads, match spans, full semantic diagnostic source maps,
broader providers and new catalogues remain separate feature gates. This delivery
adds no writer, shell route, sandbox, authority grant, transaction or automatic
retry. Step 7 and D3–D7 retain their delivery requirements.
