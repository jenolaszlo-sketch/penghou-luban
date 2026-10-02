# Pure text change profile v1

Status: Initial pure text implementation locally qualified, 2026-10-02. Implements the
pure D1–D2 slice of [ADR 0005](decisions/0005-deterministic-diff-merge.md) and the
[reviewed specification](diff-merge-spec.md). See the
[delivery plan](implementation-plan.md#diff-and-merge-delivery-track).
D3 unified parsing/serialization is implemented in the separate [transport profile](text-change-transport-profile.md). File/resource adapters, workspace validation,
application bridges, language commands, directories and snapshots remain planned.

## API and scope

`Penghou.Luban.Changes.TextDiffEngine.Diff(before, after, options, cancellationToken)`
computes a deterministic bounded line-based diff. `TextMergeEngine.Merge(base,
ours, theirs, options, cancellationToken)` computes a three-way merge or explicit
structured conflicts. The first three input names describe roles, not resources.
There are no paths, workspace providers, filesystem/network calls, authority
callbacks, Git dependencies or mutation dispatch in these engines.

Both result types expose `ProfileIdentity = "penghou.luban.text-lcs.v1"`, from
`TextChangeProfile.Identity`. This pins strategy/tie-breaking and coordinate
semantics; it is not a transport/plan hash or permission.

Inputs are immutable .NET strings containing valid Unicode scalar data. Strict
UTF-8 encoding establishes exact content SHA-256 and byte lengths; hashes are
lowercase hexadecimal. Isolated surrogates/null inputs are InvalidInput. Null
options choose the frozen defaults; invalid bounds fail rather than clamp.
`TextDiffResult` exposes `Status`, `Before`, `After`, `Edits` and `Changed`.
Check `Status == TextDiffStatus.Succeeded` before interpreting `Changed` or
empty edits as equality. `TextMergeResult` exposes `Status`, input snapshots,
`Result`, `Value`, `Edits` and `Conflicts`; only `TextMergeStatus.Clean` carries
complete merged text/result identity and applicable base-relative edits.

```csharp
var diff = TextDiffEngine.Diff("A\nB\nC\n", "A\nB1\nC\n");
var merge = TextMergeEngine.Merge("A\nB\nC\n", "A\nB1\nC\n", "A\nB\nC1\n");
// Clean yields exactly "A\nB1\nC1\n"; this computes text and never applies it.
```

Returned edits/conflicts and their payloads are immutable. A compute result
confers no permission, admitted plan, durable receipt or safe-retry guarantee.

Diff returns zero-based UTF-8 byte edits against one exact original snapshot.
Edits are ordered, non-overlapping and scalar-safe, and independently reconstruct
the exact after bytes. Display line offsets and UTF-16 indices are not writer
coordinates. Content digests identify bytes, not resource versions or object
identity. The separate D3 codec adds a canonical transport identity; it is not a plan hash or permission.
D3 uses a separate versioned encoding without modifying existing language/preview/shared-resource v1 schemas.

## Frozen deterministic semantics

Line tokens include their terminators. BOM, LF/CRLF/mixed endings, empty lines
and the absence of a final newline are preserved exactly. No Unicode, case,
whitespace or newline normalization occurs. Empty text has zero line tokens;
text ending with a terminator adds no synthetic unterminated line token.
LF ends a line token; a preceding CR remains in that token. A lone CR is ordinary
content in this initial profile, not a second implicit newline convention.

The initial strategy is bounded LCS shortest-edit computation with deletion-first
tie-breaking. It consumes an exactly equal leading run before allocating the LCS
matrix; each consumed equal prefix token counts toward the shared work budget,
and the matrix covers only the remaining tails. This preserves the full-matrix traceback
choices because equal leading tokens are always consumed first. The same
inputs/options produce the same ordered edits; exhausting its work/matrix budget
returns LimitExceeded with no alternate strategy.
It is line-based and may conflict on two distinct changes within the same line;
it does not perform character-level or semantic merge.

Merge compares each side to the same base. Identical full sides, one unchanged
side, identical edits and independent nonempty half-open base ranges are clean.
Different insertions at the same point conflict; an insertion touching a
replacement/deletion conflicts even at its boundary. Distinct overlapping edits
form transitive conflict groups. Adjacent nonempty ranges remain independent.
Groups are returned in base order with the base and both alternatives.

Conflicted/error results have no complete clean output. No preferred side,
automatic conflict marker or model call resolves them. Clean textual merge does
not prove semantic compatibility, compilability or program correctness.

## Bounds and cancellation

| Bound | Frozen default / supported maximum |
| --- | --- |
| Strict UTF-8 bytes per input | 131,072 (128 KiB) |
| Line tokens per input | 4,096 |
| Cumulative diff/merge work units | 4,000,000 |
| One LCS matrix allocation | 16,777,216 bytes (16 MiB) |
| Emitted edits | 4,096 |
| Emitted replacement/merge/conflict text | 262,144 UTF-8 bytes (256 KiB) |
| Conflict groups | 4,096 |

`MaxInputBytes` and `MaxLines` apply independently to each supplied input, not
to the assembled clean result. Clean output must fit its output budget including
the emitted base-relative edit payloads. Conflict output counts all three
alternatives across all groups. Equal-side merges compute base-relative edits
once while retaining all three original input snapshots.

Callers may select smaller positive limits. Input/line/arithmetic/option checks
precede algorithm allocation; result limits prevent oversized successful payloads.
The merge call shares one work budget across both side diffs and conflict grouping.
Conflict payload accounting includes base/ours/theirs across emitted conflicts.
Pre-cancelled or cooperatively cancelled computation returns Cancelled, not a
shortened clean result. These are algorithm bounds, not a process-memory sandbox
or hard-preemption guarantee. A single input below its byte/line caps may still
exceed the admitted quadratic matrix/work ceiling when its unmatched tails are
large.

## Qualification

Complete Penghou.Luban.Tests Release: **284 passed, 0 failed, 0 skipped** on each of .NET 8 and .NET 10. The complete run includes text diff/merge, compiler, read/language/capture/executor, direct release/cancellation, and AI-tool contract regressions; no compiler warnings were reported. The latest run includes D3 transport/import conformance; see the [D3 qualification](text-change-transport-profile.md#qualification) and the preceding [review-fix qualification](review-fixes-2026-10-02.md).

Environment: Windows x64, .NET SDK 10.0.401. These runs do not establish a
separate non-Windows qualification. The cancellation fixture checks a pre-cancelled
call; cooperative algorithm checks do not claim hard interruption.

Fixtures exercise exact edit reconstruction and repeated-line tie-breaking;
empty text, BOM, LF/CRLF/mixed endings, astral scalars and final-newline fidelity;
independent/identical changes, insertion/deletion/replacement conflicts and
boundary/transitive groups; side symmetry, invalid input, cancellation, immutable
collections and shared input/work/memory/output/edit/conflict budgets.
Only the cases exercised by the actual suite constitute qualification evidence.
No test of pure compute proves a file-provider, authority or governed writer claim.

An independently calculated tie-break vector uses before `"A\nA\nB\n"` and
after `"A\nB\nA\n"`, each six UTF-8 bytes. Deletion-first edits are
`(StartOffset=2, DeleteLength=2, Replacement="")` then
`(StartOffset=6, DeleteLength=0, Replacement="A\n")`.
The SHA-256 values, calculated separately in PowerShell rather than through the
engines, are `a83c683382655e93a95d5e146c55635e1966c8dbd9a292fe4deaaad612625577`
and `42dae60f8089418e5b92fee52559198aacb62f9291989e137feef34e6e05c637`.

Reproduce from the Luban checkout after restoring dependencies:

```powershell
dotnet test tests/Penghou.Luban.Tests/Penghou.Luban.Tests.csproj -c Release --no-restore
```

| Qualified source | SHA-256 of local bytes |
| --- | --- |
| src/Penghou.Luban/Changes/TextChangeModel.cs | f20b0dc360030a95172484780e1e920bf8577b968284ab0214974f4cf2bbaefe |
| src/Penghou.Luban/Changes/TextDiffEngine.cs | 9faee8486377a7136609bc61779d542929938da8f6f57fe9f92ed75a5e9f426c |
| src/Penghou.Luban/Changes/TextMergeEngine.cs | 977239ac55f6c3e813950e3ea93a98a4b206603a3acc947db4aec9ed084e66f2 |
| tests/Penghou.Luban.Tests/TextChangeTests.cs | f34051e67d13dcb1fdbd9becabf9de02f066d6ea4f56b854215f729aa69ff6e4 |

The original proposal is retained verbatim at
[archive/diff-merge-proposal-2026-10-02.md](archive/diff-merge-proposal-2026-10-02.md),
SHA-256 `d84e3613616fdae04f86e1f1100c8c5f2eb0d2d1554ba7e08142469a61723393`.
Source hashes identify local bytes including line endings. No commit, package,
push or production deployment is implied.

## Next gates

D3 adds closed serialization/hunk/import grammar and conformance. Next add same-workspace
authorized file observations and exact candidate validation. Materialize existing
UTF-8 file modifications into the established capture/standalone executor profiles.
Existing-file original hash/length/provider preconditions, complete-set admission,
current resource checks and mandatory start/outcome evidence remain required.
The current Local writer is in-place, HostControlled and non-atomic; unsupported
atomicity/new mutation kinds cannot be silently implemented by this pure facade.

