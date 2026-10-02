# Preview identity and canonical encoding (v1)

The preview compiler is a closed, programmatic profile. It does not parse source text, register user commands, execute tools, write files, or claim a plan is commit-ready. All hashes below are lowercase hexadecimal SHA-256 over the exact canonical bytes described here. Encodings are domain-separated, versioned, and deterministic.

## Primitive encoding

- `i32` and `i64` are signed two's-complement little-endian integers.
- `byte` is one octet. Booleans are one byte (`00` false, `01` true).
- `text` is strict UTF-8 preceded by its byte length as `i32`; no BOM or normalization is applied. Field-specific character limits are checked before encoding.
- A nullable version is a boolean followed, only when present, by a `text` value.
- Repeated values are preceded by their item count as `i32`.
- The one-byte operation tag is the sole exception to the integer field encoding: exact patch `01`, selected patch `02`, deferred tool `03`. Every other schema integer, including indices and enum values, is `i32`.
- Paths are already workspace-normalized and ASCII-folded (`A`–`Z` to `a`–`z`); non-ASCII code points remain exact. Globs use the same ASCII fold. Query/replacement payloads are never case-folded. Workspace identity is exact.

## Document identity

`SHA256(EncodeDocument(document))` is the document identity. The byte sequence is, in order:

1. `text("Penghou.Luban.PreviewDocument")`, `i32(1)`.
2. `text(SchemaVersion)`, `text(CatalogueVersion)`, `text(ProviderProfile)`, `i32(LanguageProfile.MaxGlobWork)`.
3. `text(workspace.Value)`.
4. The nine `PreviewLimits` values as `i32`, in this order: `MaxNodes`, `MaxTargets`, `MaxReadBytes`, `MaxFileBytes`, `MaxReplacementBytes`, `MaxPatchCount`, `MaxPlanBytes`, `MaxResourceCalls`, `TimeoutMilliseconds`.
5. `i32(operationCount)`, then each operation in source order, prefixed by `i32(zeroBasedIndex)`:
   - Exact patch: `byte(01)`, `text(asciiFold(path))`, nullable expected version, then patches.
   - Selected patch: `byte(02)`, `text(asciiFold(root))`, `text(asciiFold(pattern))`, `i32(selection)`, patches, then traversal limits (`MaxDepth`, `MaxEntries`, `MaxMatches`, `MaxOutputBytes`) as four `i32` values.
   - Deferred tool: `byte(03)`, `i32(tool)`, `text(asciiFold(root))`.
6. A patch list is `i32(count)`, then each patch in order as `i32(StartOffset)`, `i32(DeleteLength)`, `i32(replacementByteLength)`, and the exact replacement UTF-8 bytes.

The compiled profile validates paths before hashing, so canonical path spelling does not depend on host filesystem casing. Source aliases and comments do not enter this programmatic document schema.

## Node identity

For node index `n`, `SHA256(text("Penghou.Luban.PreviewNode"), i32(1), text(documentIdentity), i32(n))` is its identity. The `text` fields use the primitive encoding above. This binds each node to its exact document and position.

## Resolved plan identity

`SHA256(EncodePlan(plan))` identifies the captured, immutable WhatIf result. The encoded order is:

1. `text("Penghou.Luban.ResolvedPreviewPlan")`, `i32(1)`.
2. `text(SchemaVersion)`, `text(CatalogueVersion)`, `text(ProviderProfile)`, `i32(LanguageProfile.MaxGlobWork)`.
3. `text(document.Identity)`, `text(document.Workspace.Value)`, and the same nine `PreviewLimits` integers.
4. `text(invocation.SubjectId)`, `text(invocation.EffectId)`, `text(invocation.AttemptId)`.
5. `i32(observationCount)`; each ordered observation contains `text(NodeIdentity)`, folded `text(RelativePath)`, `i32(Action)`, `text(RequestIdentity.Value)`, nullable version, `text(Digest)`, `i32(ByteLength)`, and `bool(IsComplete)`.
6. `i32(nodeCount)`; each ordered resolved node contains `text(NodeIdentity)`, `text(Descriptor)`, `i32(State)`, `i32(Selection)`, proposals, dependencies, then an optional unresolved reason encoded as `bool` plus `i32(reason)` when present.
7. Proposal list: `i32(count)`, then each proposal's folded relative path, original provider version, original SHA-256, original byte length, proposed SHA-256, proposed byte length, and exact ordered patch list.
8. Dependency list: `i32(count)` followed by each dependency identity as `text`.

The plan binds observations, provider versions, completeness/coverage, dependencies, unresolved states, and the exact patch offsets and replacement bytes. Full original/proposed file content is not retained in the plan. Opaque continuation tokens are excluded from directory page identity.

## Authorized directory page identity

`SHA256(EncodeDirectoryPage(page))` uses `text("Penghou.Luban.AuthorizedDirectoryPage")`, `i32(1)`, completeness boolean, an optional truncation reason (`bool` then `i32(reason)`), and the ordered entries. Each entry is its exact provider name as text, directory boolean, optional length (`bool` then `i64`), and nullable provider version. Continuation tokens are deliberately excluded because they are opaque and may vary between equivalent pages.

## Independent golden vector

This vector was generated separately in PowerShell using `BinaryWriter` little-endian primitives and `SHA256.HashData`, not by calling the compiler's canonical writer. Inputs: workspace `ws`; default `PreviewLimits`; one exact patch at normalized path `a.txt`, no expected version, patch `(start=0, delete=1, replacement=UTF8("B"))`; node index zero. The encoded document is 177 bytes.

- Document identity: `f9863b107047f7925f852b951f2ed3a7f391e0f1dd0d983294eaffd79a417d0e`
- Node identity: `06cf34965d6021555136135e9459d37618b3a40d0b448edc2008f56e4f9873fd`

The v1 profile is frozen. Any encoding, default, catalogue, provider, or schema change that affects meaning requires a new profile/version and new vectors.


## Closed enum values

Selection: Unspecified `0`, AuthorizedView `1`, AllMatches `2`. Node state:
Proposed `0`, Unresolved `1`. Deferred tools: Build `0`, Test `1`, GitCommit `2`.
Unresolved reasons: OpaqueEffect `0`, DependsOnOpaqueEffect `1`,
AllMatchCoverageUnavailable `2`, IncompleteSelection `3`, RepeatedTarget `4`,
DependsOnUnresolvedEffect `5`. Observations use the pinned shared ResourceAction
values ReadFile `0` and ListDirectory `2`; metadata release dependencies remain
private and are not fabricated resource observations in the plan.

These mappings are part of schema version 1. A schema change requires a new
version; a hash alone is not permission or a serialized trusted-object loader.