# Deterministic diff, merge and patch materialization

Status: Accepted feature direction, reviewed 2026-10-02. The initial pure text diff/merge profile is implemented and [locally qualified](text-change-profile.md); D3 transport, D4 authorized file changes, D5 application bridge and D6 opt-in language v2 are also implemented and qualified under the [initial completion boundary](leaf-completion.md). Broader directory/snapshot/structural profiles remain deferred.
This is the reviewed Luban specification for the
[original proposal](archive/diff-merge-proposal-2026-10-02.md), preserved verbatim.
[ADR 0005](decisions/0005-deterministic-diff-merge.md) records ownership and the
selected initial profile. [The implementation plan](implementation-plan.md#diff-and-merge-delivery-track)
orders the work; examples here are proposed APIs, not currently executable syntax.

## Purpose and boundary

Luban will compare bounded text, files and later directory/snapshot views;
represent differences structurally; compute deterministic three-way merges;
and materialize safe patch candidates for its existing mutation boundary.
It must work without Git, command execution, network calls or an AI resolver.
Git adapters may consume these neutral representations later.

Diff observes differences. Merge computes a candidate result. Validation
observes whether an exact candidate still matches its target. None writes a
workspace. A clean result is not approval, a grant, a trusted plan or proof of
program correctness. Application is a separately admitted mutation.

Pure operations on caller-owned immutable values have no resource I/O or
ambient authority. Reading files, looking up metadata, traversing directories,
retrieving snapshots/artifacts and releasing results require their own current
host checks. Original text, hunk context, hashes, paths and conflict details can
disclose protected inputs; result release and durable export remain protected.
Luban does not own grants, approval decisions, an evidence ledger or scheduling.

## Initial and later profiles

| Capability | Selected initial profile | Later gate |
| --- | --- | --- |
| Text diff | Bounded deterministic line-based edit script, structured byte edits; display hunks follow D3 | Patience, histogram, token or AST strategies with pinned versions |
| Text merge | Three-way byte-faithful text merge, explicit conflicts | Structured JSON/YAML/XML or language-aware semantics |
| File comparison/merge | Authorized same-workspace reads through shared providers; explicit resource references | Cross-workspace, broader encodings/platforms and snapshot providers |
| Serialization | Versioned structured codec and unified text output; narrow modified-file unified import | Extended Git forms, binary payloads and broad historical formats |
| Patch validation | Exact original digest, byte length, target/profile and ranges | Additional independently qualified mutation kinds |
| Application | Existing UTF-8 file modification through separate single/batch executors | Create/delete/move/rename only after provider and authority qualification |
| Directories/snapshots | Planned after the file model stabilizes | Authorized coverage, structural conflicts, versioned provider manifests |
| Textual commands | Follow qualified typed APIs under a new catalogue/IR profile | No change to the current read-only language v1 |

No new package split is required. Neutral compute contracts and strategies belong
in Luban. Resource access uses Penghou.IO.Abstractions and qualified providers.
Application reuses the existing preview/execution boundary; it does not introduce
an independent general `IPatchEngine.ApplyAsync` writer or raw-path filesystem API.

## Immutable representations

Names below describe required semantics; final public API names are selected
when implementing the contracts.

- `DiffResult` retains exact before/after content identities, profile/strategy
  versions, bounded edit operations, display hunks and completeness status.
- `ResourceChangeSet` binds each resource to an explicit workspace/snapshot
  identity and normalized relative path. Added/Modified/Deleted/Renamed describe
  change, not the mutation provider's supported operations. Binary is a content
  classification, not a second incompatible set of change-kind enums.
- `TextEdit` describes a replacement against the exact original byte sequence.
  Before/after digests and lengths distinguish empty content from absence.
  Owned immutable payloads prevent caller collection/buffer mutation after hashing.
- `PatchCandidate` binds the exact destination, expected original digest/length,
  ordered byte edits, replacement bytes, proposed digest/length, versions and
  required operation kinds. A pure diff has no implied destination or workspace.
- `MergeResult` uses a closed outcome: Clean, Conflicted, Unsupported, InvalidInput,
  LimitExceeded or Cancelled. Clean carries a complete candidate and no conflicts;
  Conflicted carries bounded structured conflict regions and optional clean
  fragments, never a ready-to-apply full result. Incomplete/error results cannot
  masquerade as clean. Resource/provider adapters preserve existing typed denial,
  unavailable, missing, stale and provider-failure distinctions.

Display ranges and executable byte edits are different types. Text changes
retain enough context for display/import validation without duplicating all
content in every hunk. Optional external content references are exact-version
references and require authorization before resolution; they confer no access.

Each serialized model has a closed versioned schema and canonical identity
encoding. Domain-separated SHA-256 identities bind exact bytes, operation kinds,
workspace/paths, input/output identities, algorithm/tie-breaking/profile options,
limits and ordered operations. Result schemas bind completeness/conflicts.
Define independent golden vectors before exposing durable identities. Approval
and runtime invocation/fence bindings stay distinct from content identity.
Do not change existing preview v1, language v1 or shared request encodings.

## Text fidelity, coordinates and bounds

The initial text profile uses strict UTF-8 and rejects malformed scalar data.
It preserves BOM presence, LF/CRLF (including mixed terminators), empty lines and
the final newline exactly. Line tokens retain their terminators; the initial
profile treats lone CR as content and splits tokens on LF. It performs no
implicit Unicode, whitespace, case, encoding or line-ending normalization.
Unsupported encodings remain explicitly classified/unsupported, never decoded
lossily. Text/binary selection is explicit and validated; a filename extension,
MIME guess or successful permissive decode is not sufficient. Classification
policy is versioned; opaque bytes are never silently interpreted as source text.

Executable ranges are zero-based UTF-8 byte offsets plus delete lengths, with
half-open intervals against one original version. They must not split UTF-8
scalars; offsets/counts must be in bounds without arithmetic overflow. They map
to the existing shared `TextPatch` contract. Structured display line ranges are
separate zero-based start/count values; serializers explicitly convert to unified
diff's one-based and zero-count conventions. No unqualified UTF-16 offset or
display line number can reach the writer as a byte offset.

Canonical executable edits are ordered by original offset and non-overlapping.
Coincident insertions are coalesced or rejected; overlapping edits and duplicate
resources are invalid. There is no fuzzy search, nearest-context relocation,
offset guessing, automatic reversal, whitespace-ignore application or CRLF rewrite.
Reconstructing the exact proposed bytes must match the declared digest/length.

Every entry point has admitted input-byte/line/resource/edit/conflict/output
limits plus algorithm work/memory budgets and cooperative cancellation. Use a
deterministic work counter rather than wall-clock time to choose an alternate
algorithm. Deadline/cancellation returns an explicit incomplete outcome, not a
shortened clean diff. Failure to find a result within the budget is LimitExceeded;
there is no silent heuristic fallback. Choose and freeze numeric ceilings during
the first implementation slice, within the existing provider/preview caps.

The initial strategy is a bounded LCS shortest-edit implementation over
exact line tokens. Freeze tie-breaking, hunk grouping and context options with
golden fixtures. Repeated lines must not make identity depend on hash-map order,
culture, machine state or runtime version. Any later selectable strategy/version
is part of the identity; it cannot silently change an approved result.

## Three-way merge rules

Base, ours and theirs are explicit complete immutable inputs. Derive each side's
canonical edits against the same exact base. Equal complete sides merge cleanly;
if one side equals base, use the other exact side. Identical edits are emitted
once. Independent base regions merge without choosing a preferred side.

Build conflict groups from intersecting original ranges and insertion points:

- Distinct edits to overlapping nonempty base ranges conflict unless identical.
- Different insertions at the same base offset conflict; identical insertions
  are emitted once.
- An insertion at or inside another side's replaced/deleted range conflicts,
  including its boundaries. Touching nonempty half-open ranges are independent.
- Transitive overlap forms one deterministic conflict region; use the same
  grouping regardless of which side is labelled ours or theirs. Do not fabricate
  a single logical conflict from unrelated regions.

This deliberately conservative initial profile returns conflicts rather than
guessing an ordering at a boundary. Non-conflict outcomes reconstruct complete
UTF-8 bytes and preserve each selected edit exactly. Textual non-overlap does not
prove semantic compatibility, compilability or intended behavior; tests/review
remain separate host/workflow operations.

Conflict records bind the base region, both side regions/payloads and content
identities, conflict kind, and resource identities where applicable. Ordered
conflicts are primary data. No default side selection, LLM call or automatic
resolution occurs. A workflow/agent may explicitly resolve a conflict into a
new candidate, which needs fresh normal validation/admission.

Conflict-marker rendering is optional explicit display serialization. Its output
is labelled conflicted and cannot pass ordinary clean-candidate materialization.
Writing markers requires a separately supported and explicitly admitted mutation
profile; it is unavailable in the initial execution bridge. Never treat an empty
conflict list on a failed/truncated operation as IsClean.

For opaque binary content, exact equality or one-sided change may select complete
bytes deterministically; divergent two-sided changes return BinaryConflict.
Initial opaque comparison/merge does not grant binary application support. No
binary delta, semantic binary merge or arbitrary binary decoder is introduced.

## Authorized files, directories and snapshots

Every file input is an explicit `(WorkspaceId, RelativePath)` resource reference
resolved through a host-registered provider. The initial file adapter binds one
workspace. Later cross-workspace inputs need independent read/release checks
for each workspace; no ambient absolute
path is admitted. Reading a source does not authorize a destination. A path in
an imported diff cannot select a machine root, provider, credential or workspace.
Current provider canonical path/case/alias/native namespace limitations apply.

File reads must be complete and bounded. Record the exact observed digest,
length and provider version; reject truncated data. Reads of multiple files are
not a coherent filesystem snapshot. A future snapshot operation must state and
qualify its consistency guarantee and exact manifest identity rather than infer
it from a directory scan.

Future directory/snapshot comparison uses bounded canonical manifest ordering
and explicit coverage. User exclusions narrow requested scope; authorization
exclusions independently prevent metadata/path/content disclosure. Distinguish
an explicitly authorized view from complete requested coverage. Unavailable,
denied or truncated enumeration cannot be classified as a missing/deleted file.
Do not reveal excluded names or hidden counts, and do not claim two opaque
authorized views are globally equal. Required incomplete coverage blocks patch
materialization/application, without applying a visible prefix.

Initial file profiles do not infer renames. Later rename detection is pinned,
bounded and optional; similarity cannot prove object identity or authorize a
move. Structural merge covers presence/deletion/add-add, rename collisions,
case/profile equivalence and directory/file collisions only after manifest
semantics are qualified. Path canonicalization collisions reject the change set;
do not silently collapse two resources.

## Serialization and hostile imports

Structured data is authoritative. Unified diff and a narrow optional Git-style
header dialect are transport/display formats, never Git repository execution.
Publish a supported grammar before enabling import. The initial import gate
supports existing-file textual modifications only; create/delete/move/mode/
symlink/binary/submodule/combined forms remain Unsupported unless independently
qualified. Unknown headers/fields, ambiguous names, malformed counts, overlapping
hunks and trailing unconsumed data are rejected as a whole.

Parse with independent byte/line/file/hunk/payload/work caps and cancellation.
Check counts, zero-length conventions, exact hunk context, missing-final-newline
markers and escape/quoting rules. Duplicate file sections and canonical path
collisions reject. No includes, callbacks, expressions, external references or
executables are resolved during pure parsing. Display labels are escaped and
cannot become paths through heuristic trimming. An explicit parser profile may
strip fixed `a/` and `b/` prefixes once; never strip arbitrary path segments.
`/dev/null` may be recognized as unsupported create/delete syntax, never opened
as a host device. Reject rooted/traversing/ADS/device/reserved names under the
selected workspace profile before resource access.

An imported unified patch usually lacks complete original and proposed hashes.
Parsing yields an untrusted candidate, not an executable plan. After target
admission, authorized materialization checks every hunk against complete observed
original bytes, computes mandatory full before/after SHA-256 and lengths, and
freezes exact byte edits. Source claims/hashes/headers are validated, not trusted.
Ordinary export/import round trips preserve edits, bytes, newline markers and
target mapping under the declared dialect; re-import needs fresh authority.

## Validation and application

Pure validation checks schema, ranges, canonical ownership and candidate identity.
Workspace validation additionally performs authorized reads/version observation,
using typed Valid/Stale/Conflict/MissingResource/UnexpectedResource/InvalidPath/
Unsupported/Denied/Unavailable/LimitExceeded outcomes. These are readiness results,
not admission or a promise that the target cannot change afterwards.

For existing-file application, expected complete original SHA-256 and byte length
are mandatory, alongside the exact provider version and proposed digest/length
captured by current Luban plans. An optional source hash in a display-only diff
must not weaken this invariant. No source state is invented when materialization
lacks complete bytes or an authorized exact source reference.

Materialize the candidate into existing programmatic `FilePatchStage` operations
and authorize capture through the existing `PreviewCompiler`/`PreviewRuntime`.
They remain capture-only: `CaptureComplete` is not `CanCommit`, which stays false.
Freeze the entire known mutation set; any unsupported kind, unresolved conflict,
incomplete coverage, duplicate target or required denial blocks application before
proposed writes. A caller can select a subset only as a new explicit independently
identified/admitted candidate, never as a silent success downgrade.

Application uses separate `SinglePatchExecutor` / `BatchPatchExecutor` contracts
with whole-plan host admission, current per-resource checks, trusted locked-object
binding and mandatory serialized start/outcome evidence. Revalidate original
bytes/versions at the actual writer boundary, even after successful validation.
The optional Hufu/Zhinu start gate orders starts only; complete governed host and
typed terminal recovery wiring remain a separate gate. A stored diff/clean merge
does not create a durable receipt or permit.

The initial Local writer modifies existing files in place. It provides neither
atomic replacement, rollback nor multi-file transactions. RequireAtomic on a
multi-file candidate must fail Unsupported before writes. Sequential application
requires explicit host admission of the known non-atomic profile and per-node
Completed/NoMutation/Ambiguous receipts. Stop after failure; report the committed
prefix and unresolved remainder accurately. Preflight avoids known failures but
cannot guarantee zero partial effects after commit. No automatic retry/reversal.
Do not add a transaction or rollback implementation to satisfy this feature.

## Evidence, language and future integrations

Expose bounded neutral evidence inputs: source/base/result hashes and lengths,
workspace/path identities, algorithm/schema/catalogue/provider versions, change
summary, patch/plan identity, coverage/conflicts, and execution receipt references.
Content/context and conflict payloads are protected optional projections, not
automatic log fields. Mandatory start/outcome evidence remains host/runtime-owned;
Hongxian or other narrative export cannot replace it.

Future language descriptors separate pure `text.diff` / `text.merge` compute from
resource-bearing `files.diff`, `files.merge.compute`, `files.patch.validate` and
`files.patch.apply`. Names are proposed. The attachment's `workspace.*` names are
illustrative, not a competing registered catalogue. Lower to closed typed IR,
pin versions and reject unsupported syntax rather than run external `diff`,
`patch`, `git` or `merge` commands. Workflow branching and judgment remain in
Fuwen/hosts; the proposal's `if`, `yield`, assignments and method-call examples
do not expand Luban language v1.

Directory/snapshot/rename support, semantic merge, visualizations, Git transport
extensions and separately requested model judgment are later profiles. Filtering
or resolving changes produces a new identity and fresh admission. Git commits,
indexes, branches, remotes, repository storage and snapshot persistence do not
belong to the neutral diff/merge subsystem.

## Acceptance gate

Qualify deterministic golden vectors and independent reconstruction, byte-faithful
line/Unicode edge cases, bounded worst-case inputs, exact three-way outcomes and
conflicts, hostile import/path/collision cases, and stale-hash rejection. File
tests prove checks on every input and no excluded content/path disclosure.
Bridge tests prove compute/validate/WhatIf never writes, unsupported/conflicted
change sets perform zero proposed writes, known denied later mutations block
before the first write, live target changes still fail at the provider, and
uncertain post-start outcomes cannot be retried through a new diff/merge facade.
Claims remain limited to the actually tested profile.
