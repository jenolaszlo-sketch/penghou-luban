# Text change transport and unified patch profile v1

Status: D3 implementation, 2026-10-02. This profile extends the pure
[text change engines](text-change-profile.md) with canonical structured transport,
display hunks, unified export, and bounded untrusted import. It performs no I/O.
Authorized file observation and provider-version capture remain D4; bridging into
capture and admitted execution remains D5.

## Trust and exactness

Transport identity identifies data, not permission. Deserialization is an untrusted
observation. Parsing unified text does not establish that a target exists, that its
original content matches, or that the caller may access it.

Pure materialization checks caller-supplied complete text at exact declared ranges.
It does not search for nearby context, reverse a patch, normalize whitespace, or
change line endings. Successful materialization computes whole-content SHA-256,
byte lengths and original-relative UTF-8 edits. The host must still admit the target,
read its complete content through an authorized provider, capture its version and
revalidate at the existing execution boundary.

## Canonical transport

`TextChangeTransport.EncodeDiff` and `EncodeMerge` serialize engine results;
`DecodeDiff` and `DecodeMerge` return explicitly untrusted decoded wrappers.
`EncodedTextChange.ToArray()` returns an owned copy of the bytes. The envelope
binds the operation kind, result status, engine profile, actual frozen computation
limits, snapshots and ordered edits or conflicts. Engine results retain their
selected `TextChangeOptions`; transport callers cannot substitute different limits.

The v1 schema is closed JSON with fixed property order. Identity is lowercase
SHA-256 of UTF-8 `penghou.luban.text-change.transport.v1` followed by a zero byte
and the canonical root object excluding its final identity field. This encoding
is separate from the language, preview and shared-resource encodings. Pretty
display output is not the canonical identity input.

Decode requires the exact canonical encoding. Duplicate/unknown fields, changed
property order, added whitespace, alternate escaping, trailing data and invalid
identity are rejected. Unknown schema/version/profile is Unsupported. The whole
document cap is 1,048,576 bytes and JSON depth is bounded. A decoded result is
not proof that the edits reconstruct the claimed hash; that requires exact source
text. Shape, ordering, range, strict scalar and recorded payload limits are checked.

Ordered root fields are `schema`, `version`, `kind`, `profile`, `options`, `status`,
then diff fields `before`, `after`, `edits`, or merge fields `base`, `ours`,
`theirs`, `result`, `value`, `edits`, `conflicts`, then `identity`. Schema is
`penghou.luban.text-change`, version 1, kind `diff` or `merge`, and profile
`penghou.luban.text-lcs.v1`. All seven computation limits appear in options in
the order declared by `TextChangeOptions`. Null fields and empty arrays are
explicit. Snapshots contain `sha256`, `byteLength`; edits contain `start`,
`deleteLength`, `replacement`. Conflict fields retain the model's base line/byte
coordinates followed by exact base/ours/theirs text. Strings use the codec's
fixed `Utf8JsonWriter` escaping; numbers are invariant decimal integers.

Selected valid options are retained on failed compute results too. A compute
failure caused by invalid options has no valid option profile and cannot be
encoded. Encoded bytes are copied on access; decoded and computed collections
own their contents.

Independent fixed default-option vectors:

- Diff `"a\n"` to `"b\n"`: identity
  `426c50acaffb0bf2d2a6583fdcc3d946834f879b4c991cb8c71584b4872c42b5`.
- Merge base `"a\n"`, ours `"b\n"`, theirs `"a\n"`: clean identity
  `883c16399cf77d5601e60b0504eb762a6cb1e47bb86887c3729f148f8ea524a4`.

The test fixtures freeze complete literal canonical bodies. SHA-256 was also
calculated independently with PowerShell using the stated domain and bodies.

## Unified dialect

`TextUnifiedDiffRenderer.Render(before, after, relativePath, options, cancellationToken)`
returns exact input snapshots, immutable display hunks and `UnifiedDiff` text.
`TextDisplayLineRange` uses zero-based start/count coordinates; the serializer
converts these to one-based unified ranges, with zero-count positions represented
by the number of preceding lines. A count of one may be omitted in a header.
Equal input produces empty unified text and no hunks.

`TextUnifiedDiffOptions` selects context (default 3, maximum 100), nested
`TextChangeOptions`, output bytes (maximum 262,144) and hunks (maximum 4,096).
Adjacent display windows merge when the unchanged gap fits both context windows.
These options affect display, not the underlying diff strategy.

The initial dialect accepts plain unified modifications to existing text files:
`--- a/<relative-path>`, `+++ b/<same-relative-path>`, then one or more counted
`@@ -old-start,old-count +new-start,new-count @@` hunks. The fixed prefixes are
removed once. A patch path cannot choose a workspace or provider. Display line
coordinates are separate from executable byte offsets.

Control lines use LF. Content lines retain CR before LF, including mixed endings;
a lone CR is content. An unterminated content line is followed by the exact
`\ No newline at end of file` marker. Empty files have zero line tokens. BOM and
Unicode scalar data remain exact; isolated surrogates are invalid.

The grammar has no timestamps, arbitrary hunk annotations, quoted labels, escaping,
includes or external references. Labels containing control characters are rejected.
Rooted paths, traversal, alternate data streams and Windows device/reserved names
are rejected. Duplicate targets and case-insensitive path collisions reject the
whole patch. Git headers, binary/mode/symlink/submodule/combined forms and
create/delete/rename operations are outside this dialect; `/dev/null` is never a
resource to open.

Parser byte, line, file, hunk, payload and work ceilings are independent and can
only be reduced. Unknown/trailing input, malformed ranges/counts, overlapping
hunks and invalid newline-marker placement fail as a whole. Cancellation never
returns a shortened successful candidate.

`TextUnifiedImport.Parse(text, options, cancellationToken)` yields an immutable
`TextUnifiedPatchCandidate`. `Materialize(candidate, originals, importOptions,
textOptions, cancellationToken)` requires an exact path-to-complete-source-text
dictionary for all candidate files. It returns `TextUnifiedMaterializedFile`
values containing `RelativePath`, `ProposedText`, `Before`, `After`, and `Edits`.
Any stale context/range or malformed candidate fails the whole call with no
materialized prefix. Missing/extra source-map entries are invalid.

```csharp
var display = TextUnifiedDiffRenderer.Render("old\n", "new\n", "src/example.txt");
if (display.Status != TextUnifiedDiffStatus.Succeeded) return;
var parsed = TextUnifiedImport.Parse(display.UnifiedDiff);
if (parsed.Status != TextUnifiedImportStatus.Succeeded) return;
var checkedText = TextUnifiedImport.Materialize(parsed.Candidate,
    new Dictionary<string, string> { ["src/example.txt"] = "old\n" });
if (checkedText.Status != TextUnifiedMaterializationStatus.Succeeded) return;
var proposed = checkedText.Files[0].ProposedText; // exact "new\n", no resource accessed
```

| Import bound | Frozen maximum |
| --- | ---: |
| Input UTF-8 bytes | 1,048,576 |
| Physical transport lines | 65,536 |
| File sections | 128 |
| Hunks across the document | 8,192 |
| Payload UTF-8 bytes | 524,288 |
| Parser/materialization work units | 4,000,000 |

Pure source/proposed-text computation also obeys `TextChangeOptions`; one shared
text work budget covers all file comparisons in a materialization call. Proposed
text byte limits are checked before appending tokens. Materializing a transport
can fail its stricter text/work ceilings even when parsing succeeded.
These are computation bounds, not hard process-memory isolation or preemption.

## Qualification

The complete Release suite passes **284 tests, 0 failures, 0 skips** on each of
.NET 8 and .NET 10, Windows x64, .NET SDK 10.0.401. This includes the preceding
227 regressions and 57 D3 test cases. An independent integration fixture runs
300 export/import combinations across ten exact text inputs and context values
0, 1 and 3, checking exact proposed text, snapshots and canonical byte edits.

Tests cover fixed diff/merge transport vectors, canonical ownership, option
provenance, closed/noncanonical/duplicate/tampered and invalid-Unicode transport;
display zero-count ranges and separate hunks; BOM, scalar Unicode, LF/CRLF/mixed
endings, lone CR, empty and unterminated text; hostile paths and collisions;
unsupported Git/mode/binary/combined/create/delete/rename forms; malformed
counts/ranges/markers, exact stale context, no partial materialization, shared
multi-file work limits, lowered caps and cancellation.

Reproduce from the Luban checkout after restoring dependencies:

```powershell
dotnet test tests/Penghou.Luban.Tests/Penghou.Luban.Tests.csproj -c Release --no-restore
```

This evidence covers pure computation and the stated grammar. It establishes no
authorized file-provider, governed mutation, non-Windows or crash-recovery claim.

## Next gates

This profile adds no source commands to the current read language, mutation
dispatcher, file resolver or provider. It does not complete Hufu/Zhinu governed
mutation integration. The current single/batch execution contracts still require
host admission, current rights, exact original hashes/lengths/provider versions,
serialized start evidence and terminal outcomes.


## Qualified local source bytes

| Source | SHA-256 |
| --- | --- |
| src/Penghou.Luban/Changes/TextChangeModel.cs | f20b0dc360030a95172484780e1e920bf8577b968284ab0214974f4cf2bbaefe |
| src/Penghou.Luban/Changes/TextDiffEngine.cs | 9faee8486377a7136609bc61779d542929938da8f6f57fe9f92ed75a5e9f426c |
| src/Penghou.Luban/Changes/TextMergeEngine.cs | 977239ac55f6c3e813950e3ea93a98a4b206603a3acc947db4aec9ed084e66f2 |
| src/Penghou.Luban/Changes/TextChangeTransport.cs | 5de6bb0c5d34ef5df5646b44e998cc83f430c231471d451898bae02dfd9414ed |
| src/Penghou.Luban/Changes/TextUnifiedDiff.cs | 403b91dcb25c84768d92ba090913ddf9355560edbf65f647efea2c6110046c5d |
| src/Penghou.Luban/Changes/TextUnifiedImport.cs | 0ca4e524a110d602ccfcc0cb4898512d1c8b015b3d23609bdedcee1e03265092 |
| tests/Penghou.Luban.Tests/TextChangeTransportTests.cs | 58f86b82a424038c5c703c23cf8e4f01959043dc385eb942707026ffe797fb61 |
| tests/Penghou.Luban.Tests/TextUnifiedDiffTests.cs | acc59e3ea4203ac64a5180dfe03f52a03515b626c4ca91596eea4d7123dd468f |
| tests/Penghou.Luban.Tests/TextUnifiedImportTests.cs | 7f5e8651a12736372f54cde7068afab41880b161a12bc720469fd1ea9d678bde |
| tests/Penghou.Luban.Tests/TextUnifiedRoundTripTests.cs | 25c099cbf39186977acadd0c79d7b1d72571c7fe6b116cd78ccb50b26b7b2374 |

No commit, push, package publication or deployment is implied.
