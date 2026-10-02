# Luban semantic IR identity, profile 1

This note freezes the document and node identity encoding implemented by the
compiler. It is a semantic identity, not a signature, trust proof, or grant of
authority. Hosts must still validate compiled IR and authorize every effect.

## Document encoding

Integer fields are signed 32-bit little-endian values. Node tags are the
one-byte values shown in the table. Strings are strict UTF-8 preceded by their
byte length as a little-endian i32. Booleans are one byte (`00` false, `01`
true). The byte stream is hashed with SHA-256; the public identity is lowercase
hexadecimal.

The document stream is emitted in this order:

1. Text domain `Penghou.Luban.SemanticDocument`, then i32 format version `1`.
2. Text fields: language, IR, catalogue, provider, and descriptor versions.
3. Text workspace identity.
4. Execution limits: `MaxResourceCalls`, `MaxReadBytes`,
   `MaxIntermediateBytes`, `MaxOutputBytes`, `MaxValues`, and
   `TimeoutMilliseconds`.
5. Statement count. For each ordered statement: its zero-based index, node
   count, then each node's zero-based index, typed payload, and input edge.

The closed node tags and payload fields are:

| Tag | Node | Payload in order | Input edge |
| --- | --- | --- | --- |
| `01` | Read | nullable normalized path, `MaxBytes` | `-1` for an explicit source; previous node index for piped input |
| `02` | Find | normalized root, normalized glob, `MaxDepth`, `MaxEntries`, `MaxMatches`, `MaxOutputBytes` | `-1` |
| `03` | Search | exact query, nullable root, normalized include glob, nullable normalized exclude glob, `MaxDepth`, `MaxEntries`, `MaxMatches`, `MaxOutputBytes`, `MaxFileBytes`, `MaxBytesScanned` | `-1` for explicit root; previous node index for piped input |
| `04` | Take | count | previous node index |
| `05` | Count | no additional payload | previous node index |

Nullable strings are encoded as a one-byte presence flag followed, when present,
by the ordinary length-prefixed string. Paths have already passed the Windows
workspace normalizer. `.` and one leading `./` are normalized away, separators
are `/`, and root is the empty string. Glob strings use `LanguageGlob.Normalize`.
For paths and globs only ASCII `A` through `Z` are folded to lowercase before
encoding; non-ASCII code points remain exact. Search query text is preserved
exactly. The canonical stream contains no source text, comments, whitespace,
command alias, or source span.

The edge is explicit in the byte stream even though this profile permits only
sequential pipeline input. A successful compilation has already type-checked
that edge and its value kind. Unsupported node types have no encoding.

## Node identity

For each node, encode text domain `Penghou.Luban.SemanticNode`, i32 format
version `1`, the document digest as its lowercase 64-character hexadecimal
text, then the statement and node indices as i32 values. SHA-256 of this byte
stream, rendered as lowercase hex, is the node identity. Therefore moving a node
or changing any document semantic changes its node identity.

## Golden vector

This independently assembled vector contains one statement with one default
`ReadStage("a")`, workspace `workspace-1`, and default profile versions and
execution limits. The exact document preimage bytes are:

```text
1e00000050656e67686f752e4c7562616e2e53656d616e746963446f63756d656e7401000000010000003101000000310f00000077696e646f77732d726561642d7631150000006c6f63616c2d77696e646f77732d726561642d763101000000310b000000776f726b73706163652d31a08601000000000100004000000040001027000030750000010000000000000001000000000000000101010000006100001000ffffffff
```

The preimage was independently assembled with PowerShell `BitConverter` and
`SHA256`, rather than calling the compiler serializer. Its expected document
identity is:

```text
670cef2eedcc007f36a42107c0ce0937032464ac670355e19e1919178a070968
```

The matching node identity is:

```text
9fe1cd89cd436d3e549f8b3e6a3cba6d3ab66f748a822348dcaa4b57a532ef05
```

Tests keep these values as frozen vectors. Any intentional encoding change
requires a new identity format version and a migration decision; it must not
silently reinterpret persisted documents.

## Profile validation bounds

The frozen defaults are the defaults in `LanguageCompilerOptions` and
`LanguageExecutionLimits`. A host may lower, but not raise, those compiler and
execution ceilings. Source is checked against its UTF-8 byte budget before
lexing; lexical work checks cancellation and is capped at 4096 tokens, 64
statements, 128 nodes, and 8192 UTF-8 bytes per literal. An empty document is
invalid. Programmatic IR is bounded and cloned before a `CompiledDocument` is
created; unknown stage records and null required fields produce diagnostics.

Find and search traversal depth is capped at 16. Other per-operation limits
must fit the selected execution profile and the `TraversalLimits` or
`SearchLimits` model defaults. `MaxBytesScanned` is capped at 10,485,760 bytes.
The recognized spellings are `read`, `cat`, `gc`, `files.read`; `find`, `fd`,
`files.find`; `search`, `grep`, `files.search-text`; `take`; and `count`.
Pipeline context makes `gc` accept FileReference input without a path and makes
`grep` accept FileReference input without a root. Piped search has the fixed
include selector `**` and no exclude selector; include/exclude options are
standalone search selectors and are rejected after a pipeline edge.
Standalone search without a root is bound to the workspace root (the empty
normalized path); in a pipeline, a missing root represents the FileReference
input edge.

The catalogue also freezes an aggregate ceiling of 16,777,216 glob-work units
per executed document. This fixed profile bound is not source-selectable. Changing
it requires a new catalogue/profile version rather than reinterpreting old IR.
