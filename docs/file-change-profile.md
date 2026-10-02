# Authorized file change profile v1

`FileChangeRuntime` supplies D4 same-workspace file diff, three-way merge,
read-only exact candidate validation and whole-set unified-import materialization.
It uses an injected provider and `IFileChangeAuthorizer`. It never opens a writer.
The underlying text computations remain deterministic, bounded and strict UTF-8.

Inputs name logical workspace-relative files and explicit roles. Diff targets
the original file and reads a separate proposed source. Merge reads base, ours
and theirs; its destination is **ours**, and its candidate edits are computed
against ours. A conflicted merge returns protected structured conflicts without
an applicable full-file candidate. Repeated identical paths share one captured
snapshot while retaining their distinct admitted roles. Reads of different files
do not establish a coherent filesystem snapshot.

## Authority and bounds

Whole-operation Preflight and admission of every input precede provider opening.
The provider receives a checked authorizer that binds the exact invocation,
workspace, request identity and target. Required ancestor metadata probes and
the concrete read are individually checked. A registered provider must meet its
declared profile and invoke those hooks; missing or conflicting checks fail closed.
ResultRelease authorizes protected observations, hashes, edits or conflicts.
Denied/unavailable release returns no partial payload.

Requests expose the frozen text options, optional import options, aggregate
limits and selected provider profile. Default file limits admit three roles,
393,216 total read bytes, 4,194,304 retained bytes and a 30-second cooperative
deadline. Explicit limits can admit at most 32 roles, 4 MiB reads and 8 MiB
retained data. The linked cancellation/deadline applies through text computation.
The text profile adds its own input, line, matrix, deterministic work and output
ceilings. Existing-file candidates are limited to 128 edits for compatibility
with capture/application. No fallback broadens an exhausted limit.

Unsupported encoding, missing/access-denied files, stale candidates, denied or
unavailable authority, limits, cancellation and provider failure have distinct
typed outcomes. Clean computation confers no approval. Candidate validation
checks complete original bytes, hash, length and opaque provider version without
writing. Apply uses the [separate capture bridge](file-change-application-profile.md).

## Immutable candidate identity

Candidates own immutable original/proposed bytes, ordered scalar-safe edits and
the input snapshots used to produce them. Their identity binds the Windows path
profile, text algorithm, selected provider read profile, operation, workspace,
destination, opaque original version, content hashes/lengths, all text and file
limits, ordered input roles/resources/versions/hashes/lengths, and replacement
bytes. The identity is a content/provenance binding, not an authorization token.

Encoding v1 is domain-separated by `Penghou.Luban.FilePatchCandidate`. Integers
are signed 32-bit little-endian; text is strict UTF-8 with a 32-bit byte length.
The candidate hash is uppercase SHA-256. Hash casing is explicit at this layer;
the underlying pure text snapshots retain their existing lowercase form.

Independent vector: workspace `workspace`, target `a.txt` containing `x\n`,
proposed source `b.txt` containing `y\n`, default text/file limits, read profile
`local-windows-read-v1`, and versions `local-read-v1:sha256:<uppercase content hash>`.
The single edit replaces two bytes at offset zero with `y\n`.
The encoding is 848 bytes and its SHA-256 is
`DB70B99AB2CE6DCE1F36A9137CC0F2058B07A2071EC3C516290CF575C0A5D0FE`.
This vector was encoded independently with a PowerShell binary writer and is
asserted by the real-provider regression suite.

Directory manifests, cross-workspace inputs, rename/create/delete, binary
application, structural merge and advanced diff strategies remain separate
profiles. See the [leaf completion ledger](leaf-completion.md) for qualification.
