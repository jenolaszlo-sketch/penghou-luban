# Typed effect runtime for agent source work

Status: Behavioral design, 2026-10-01. Canonical owner: Penghou.Luban. The
language runtime implements Windows Read/Find/SearchText and capture-only WhatIf.
Separate programmatic APIs support one exact patch and a narrow bounded exact-
target batch with host-supplied admission and receipt recovery. Textual mutation
commands, selected-glob execution, broad writers, durable stores and governed
adapters remain unimplemented; this document's other behaviors are design
requirements, not implemented guarantees.
[ADR 0004](../../Penghou.Hufu/docs/decisions/0004-typed-effects-over-sandbox.md) selects the LOP approach
and removes sandbox implementation from Hufu's roadmap.

## Principle and scope

Constrain the executable effect vocabulary. Agents submit typed requests such
as Files.SearchText(workspace.main, include: **/*.cs, text: WorkflowAuthority),
not shell programs. Trusted handlers implement bounded operations. Fuwen/Zhinu
provide iteration, branching, fan-out, and scheduling outside this effect model.
The [surface language](language-syntax-spec.md), accepted by
[ADR 0002](decisions/0002-typed-surface-language.md), has an implemented minimal
bounded profile: read, find, literal search, take, and count, compiled to typed
semantic IR and statically preflighted. Language find/search use root-relative
`*`, `?`, and complete-segment `**` path-globs. The legacy direct Find/SearchText
API keeps its recursive basename-pattern behavior. Broader syntax and transforms
remain pending. The language adds no shell byte streams, unrestricted script
bodies, variables, substitutions, user callbacks, executable predicates, arbitrary
command arguments, raw OS handles, or plugin installation. Its executor uses
bounded buffered values; it does not claim lazy streaming or backpressure.

The strict-mode claim is: through this configured runtime, an agent can request
only supported typed effects that pass the configured host's current authority and execution
requirements. It cannot request arbitrary operating-system commands. This claim
assumes trusted handlers and no alternate agent-accessible execution route.

It does not claim to contain arbitrary host code, compromised handlers, or native
programs launched elsewhere. Host composition must remove alternate shell/tools
from the strict agent's exposed surface. Neither Penghou.Luban nor Hufu will
build a sandbox. Requiring a finite effect vocabulary avoids needing arbitrary
code containment for these operations; it does not eliminate handler correctness.

## Ownership and package direction

Penghou.Luban owns the typed effect contracts, trusted catalogue, provider
boundary, implementation plan, and conformance tests in its own repository.
Requests/results remain free of Hufu and workflow-engine types. Hufu owns
authority and its integration adapter; it does not own the effect implementations.

[ADR 0003](decisions/0003-shared-resource-interfaces.md) refines provider ownership:
`Penghou.IO.Abstractions` owns shared bounded resource contracts;
Luban owns semantic effects and their handlers. Resource implementations enforce
concrete read/metadata/enumeration/write/web checks with host-bound effect
context. The Windows read slice now uses the shared Local reader. Hufu's policy
adapter, mutations and web remain pending. Shared I/O checks supplement exact-effect
admission and do not move authorization into the language compiler.
Penghou.Effects and Penghou.Hufu.Effects are superseded working names, retained
only in historical input.

Do not initially split Abstractions, Files, Git, DotNet, and Http packages. Extract
packages with concrete consumers. Independent hosts may choose ordinary local
execution explicitly; only the Hufu-governed composition claims Hufu authority.

```text
Agent -> Fuwen activity -> typed effect request
      -> Hufu admission -> Zhinu durable intent
      -> provider final current-authority/start check -> bounded handler
      -> typed result + attributable receipt -> runtime journal/evidence
```

Standalone hosts can supply the same operation/journal contract without Zhinu.
The operation envelope has generic effect/subject/attempt IDs, exact request and
descriptor digests, preconditions, provider profile, admission reference, and
fence. Fuwen/Zhinu adapters supply workflow/run/node/revision bindings; those
types do not leak into neutral request/result contracts.

Language runs require `ILanguageAuthorizer` decisions at Preflight, EffectStart,
ResourceAccess, and Release. The direct legacy effect runtime separately continues
to require `IEffectAuthorizer`; the language authorizer does not replace it.

## Trusted descriptors and discovery

Every effect kind has a versioned trusted schema and descriptor: mutation scope,
risk, deterministic authority mapping, input/work/output limits, preconditions,
reconciliation contract, possible network and credential use, and provider needs.
Map Files.Read to the exact resolved read scope; map Files.Copy to both source
read and destination write; map Move to all affected source/destination rights.
Patches authorize every affected path and any create/delete/rename operation.
Agents cannot supply a weaker authority mapping or self-classify tool risk.

Do not rely on a Boolean Supports or ReplaySafe flag alone. Discovery includes
kind/schema versions, platform, semantics, limits, link handling, execution mode,
and recovery guarantees. Pin these at admission and revalidate before use.
Discoverable capability is not a grant. Unknown required fields, incompatible
versions, missing requirements, and unsupported modes fail closed.

Both restrictions apply independently:

```text
DelegatedEffects <= ParentEffects
EffectAuthority <= ActivityAuthority <= WorkflowAuthority
```

Each layer must pass; inheriting a kind does not inherit every resource scope.
Live revocation, approvals, data release, budgets, and fences still apply. Recheck
queued work at the final provider start boundary, including supervisor waits.

## Execution modes

| Mode | Meaning and admission rule |
| --- | --- |
| StrictEffects | Default planned surface. Bounded qualified handlers only; no arbitrary scripts, process requests, project-code execution, or unknown effects. |
| ApprovedTools | Explicit host opt-in to reviewed adapters such as DotNet.Test. Structured arguments reduce command injection but do not contain project/test code. Record accepted broader trust or required external-provider guarantees. |
| UnrestrictedProcess | Reserved external-provider/host mode outside this roadmap. Requires a separate explicit decision and actual provider guarantees or acceptance of weaker guarantees; never a fallback. |

Effect risk describes actual transitive behavior, not the display name or merely
whether the implementation uses native code. A fixed trusted Git executable may
implement a strict bounded operation only after helper/configuration behavior is
qualified. A build/test adapter that executes project code cannot be StrictEffects.

## First useful catalogue

| Family | Planned first useful operations |
| --- | --- |
| Inspect | Files.Exists, Stat, List, Find, Read, ReadRange, SearchText |
| Mutate | Narrow exact-file ApplyPatch batch is implemented; Files.Write, Copy, Move, Delete, CreateDirectory and general ApplyPatch remain future profiles |
| Git inspect | Git.Status, Git.Diff, after strict-provider qualification |

The first Read/Find/SearchText slice and narrow programmatic exact-file patch
profiles have passed their current conformance gates. Complete the catalogue
only as each operation passes its own review. Files.ReplaceText may follow once
match count, exact replacement semantics, and preconditions are defined. Do not
include Process.Run, Shell.Run, PowerShell.Run, or Bash.Run.

List is non-recursive by default. Find returns paths; SearchText returns bounded
matches and context. Define literal/glob semantics, line/column indexing,
encoding/binary handling, newline behavior, and byte versus line ranges. Read
reports encoding, byte length, and the hash of the exact returned content/version;
partial reads distinguish range hashes from full-file hashes. Truncation and
continuation must be explicit, with no implied complete snapshot under mutation.

Limit input bytes, traversal depth/entries, file sizes, result bytes/matches,
context, and work duration. A match cap alone does not bound a no-match scan.
Start with literal search; later regex support needs complexity/time bounds.
No executable search predicate is allowed. Planned pure `where` filters operate
on authorized typed values through a closed AST; they are never callbacks into
file traversal or arbitrary code. Cursors bind the query, subject and
snapshot semantics, and require fresh authority on later pages. Cancellation
does not imply rollback of an operation already committed.

## Filesystem binding and mutations

Use a host-bound WorkspaceReference plus a workspace-relative path. Reject
unexpected absolute/drive/device/UNC paths and traversal rather than letting
machine path parsing select a different resource. Define platform support for
case, separators, junctions/symlinks, hard links, mounts, alternate streams, and
special files. Link creation is outside the initial catalogue. Follow or reject
links only under a tested per-operation rule; a canonical prefix check is not
sufficient. Existence, directory listing, and metadata also need authorization.

Bind checks to the actual object opened or mutated. Protect against path/link
replacement and changes between precondition checking and use. A content hash
check followed by an unrelated write is not optimistic concurrency control.
Serialize cooperating writers and specify what guarantees hold against other
local writers; reject unsupported narrow guarantees instead of claiming an
atomic compare-and-write facility the platform does not supply.

Mutations specify expected source version/hash or expected absence, destination
overwrite rules, exact targets, and bounded scope. Failed preconditions cause
zero mutation for that effect. Copy and Move validate both ends. Delete is
non-recursive unless an explicit bounded subtree operation is supported. The
implemented batch is sequential, exact-target, and non-atomic; it stops at the
first failure, retains host receipts, and has no rollback or automatic retry.
Other multi-file patches and cross-volume moves require their own
atomicity/partial-outcome contracts. No silent best-effort batch.

The current Local writer additionally requires an explicitly selected
HostControlled namespace. The host must control alias creation and directory
configuration against untrusted actors. Native evidence found CreateHardLink can
succeed during exclusive file sharing; a later link-count check caught the
observed attempt but cannot close every race. This profile is not general
filesystem confinement or a privileged-local-attacker defense.

Prefer ApplyPatch for existing source and Write for new/generated files or
explicit complete replacement. Validate patch paths, size, hunk counts, and
exact base versions; avoid fuzzy application to an unexpected revision.
Reserve host policy/config, credential locations, .git control data, hooks, and
privileged automation triggers from ordinary source-write authority. Typed file
writes must not become an indirect way to install executable handlers or cause
an unapproved CI/build effect. Host policy defines additional governed files.

## Git and approved developer adapters

Git.Status/Diff are candidates for strict inspection, not automatically safe
because their names describe reads. Git supports external diff/text conversion
and configurable filesystem-monitor helpers. See the official
[diff options](https://git-scm.com/docs/git-diff) and
[configuration reference](https://git-scm.com/docs/git-config).

The provider must prevent repository/user-configured commands, pagers, hooks,
filters, helper execution, and implicit remote fetching on the supported path;
control inherited configuration/environment and reject unqualified repository
features. Disable or separately declare incidental metadata writes. Pin the
trusted executable/backend; construct arguments internally with no shell or
arbitrary option passthrough. Test malicious config/attributes and path/ref option
injection. These are qualification requirements, not claims about a chosen Git
implementation. If they cannot be met, the operation is unavailable in strict mode.

Git output must honor the same path/read exclusions as filesystem effects;
repository-wide status/diffs and historical blobs cannot disclose excluded
content by switching tools. Register explicit revision and history semantics.

Later Git.Show/Log/Branches/CurrentBranch and local mutations need separate
profiles. Commit may invoke hooks/signing helpers unless prevented or separately
admitted. Stage, Unstage, Checkout, CreateBranch, and Commit are local mutation;
Fetch, Pull, and Push involve separate remote authority, and Pull also mutates
local state. Never grant a generic git permission covering all of them.

DotNet.Build/Test/Restore/Format and Node adapters remain ApprovedTools candidates.
Typed fields constrain arguments, not the code executed by the tool. Their
descriptors must disclose project code, child processes, network, credentials,
and outputs as applicable. Only a host that accepts those actual powers, or an
external provider satisfying requested guarantees, can execute them. No owned
sandbox is an implementation prerequisite or planned solution.

## Durable execution and replay

[ADR 0004](decisions/0004-preview-resolution-commit-barrier.md) and the
[preview/commit design](preview-resolution-commit-barrier.md) add planned
authorized read-only resolution, an immutable resolved effect plan, whole-known-
mutation-set admission and an executor commit barrier. Static preflight performs
no protected target I/O; resolution performs real separately authorized reads.
Known denied or required incomplete mutation sets block before writes. WhatIf
never dispatches requested mutations or opaque/lazy tools. Exact frozen targets,
payloads, versions, selection coverage and dependency assumptions bind admission;
all actual accesses still pass final authority/object/precondition checks.
Admission is not a transaction: post-start failures can leave partial outcomes.
Persist plan/segment/admission identities through the existing host journal and
revalidate on restart; a preview is never a reusable permit.

Use conceptual Prepared, Authorized, Started, Completed/Failed/Unknown states,
with runtime-owned transitions. Stable effect IDs bind exact requests and
preconditions; changed inputs with the same ID are conflicting reuse. Commit
mandatory intent/start evidence before dispatch and outcome/receipt afterward.

Reads are repeatable operations, not deterministic replay of identical results.
If a workflow needs the old value, persist the authorized result or a protected
immutable artifact reference; hashes alone cannot reconstruct it. A fresh read
is a fresh authorized observation. Releasing a cached result requires current
access to that result, not automatic reuse of expired read authority.

Mutations need explicit deduplication and reconciliation. Preconditions prevent
some stale writes but cannot establish whether an interrupted move/delete/patch
completed. Record before/after identity and reconcile through the operation
journal/provider. Returning an already committed result must not redo the effect.
If completion cannot be distinguished from unrelated changes, return
AmbiguousOutcome and stop automatic retries. No exactly-once external-effect
claim follows from an ID, hash, or local journal alone.

The implemented `BatchPatchExecutor` supplies a narrow ordered exact-target
batch protocol, not the durable host named in this design. Its host must inspect
an authoritative full ordered receipt snapshot and validate stable segment/node
identities. The executor skips only a valid contiguous Completed prefix;
Uncertain and NoMutation entries block without automatic replay. Each batch call
is a separately admitted segment, and any predecessor receipt must be verified
by the host. The implementation includes no durable store or Hufu/Zhinu adapter.
See the [batch execution profile](batch-execution-profile.md).

Authorize before target-data access. Denial means zero unauthorized protected I/O;
trusted authority/resource-binding checks and evidence writes remain permitted.
Precondition checks may read authorized state, but failed preconditions cause
zero mutation. This qualification replaces the proposal's unqualified zero-I/O
wording.

## Providers and consuming projects

Guyabano selects LocalEffectExecutor in StrictEffects initially; ApprovedTools
is explicit opt-in. Marang uses a SupervisorEffectExecutor when suitable trusted
supervisor tooling is available. Qingniao attenuates both effect kinds and their
resource/requirement authority. Fuwen validates declared effect/version/provider
needs; Guihua uses discovery to propose feasible plans and report missing effects.
Workflow control flow remains with Fuwen/Zhinu; Luban's planned language only
expresses bounded typed effects and finite dataflow with pure transforms.

The supervisor protocol conceptually offers request_effect, complete_effect,
fail_effect, and reject_effect. Persist pending requests across restart. Bind
every exchange to authenticated provider, effect ID, exact request digest,
workspace/resource binding, subject, revision/fence, attempt, and output schema.
Providers must enforce preconditions and final start authorization, not merely
receive a prior Hufu Allow. Discover and admit their real capabilities.

Validate results before committing them. Unknown, wrong-provider, conflicting,
cancelled, and stale results cannot advance the workflow. Exact duplicates of a
committed completion receive an idempotent acknowledgement without re-execution
or resumption; conflicting duplicates fail. Late receipts may be retained as
historical evidence for reconciliation, without reviving cancelled/stale work.

A supervisor's structured success response alone does not prove which resources
its tools accessed. Where direct enforcement cannot be established, reject the
required guarantee or use a separately admitted candidate-patch mode: supervise
preparation from authorized inputs and apply locally through the bounded handler.
Existing supervisor tooling remains responsible for its own isolation, if any.
No direct dependency on a particular agent or MCP is required.

MissingEffectCapability is planning evidence, never permission. Replanning or
choosing another provider needs normal admission; it cannot route around a denial
or silently translate an unsupported request into a shell command.

## Results, evidence, and acceptance

Use typed outcomes including NotFound, AuthorityDenied, AccessDenied,
PreconditionFailed, InvalidPath, OutsideWorkspace, UnsupportedEffect,
UnsupportedPlatform, ExecutionCapabilityUnavailable, ResultLimitExceeded,
NativeToolFailed, ProviderUnavailable, and AmbiguousOutcome. Version exact codes
with the API; do not infer authority outcomes from human-readable stderr.

Link effect/subject/runtime IDs, descriptor/provider versions, normalized protected
target references, decision/start records, preconditions, outcome and artifact
hashes into existing Hufu evidence. Do not copy source content or secrets into
ordinary telemetry. Luban does not create a competing evidence store.

Acceptance begins with source inspection and a hash-bound patch restricted to
src/**. Demonstrate no shell invocation, bounded search/range reads, excluded-path
and link rejection, zero mutation on stale input, unsupported-shell rejection,
revocation before start, crash recovery without duplicated completed mutations,
and attributable evidence. Test both local and supervisor boundaries, including
stale/duplicate completions. Git strict qualification includes hostile helper
configuration; platforms claim support only after their semantic tests pass.

Repository tests come first. Publish reusable provider conformance helpers when
an independent provider consumer needs them. Later HTTP/download, archive,
source-symbol, package, and remote-Git effects each need their own authority and
recovery contracts. Arbitrary scripting, interactive terminals, PTYs, and OS or
container sandbox implementation remain out of scope.

## Provenance

The [supplied proposal](archive/typed-effect-runtime-proposal-2026-10-01.md) is
preserved verbatim. This design qualifies its replay flag, supervisor duplicate
handling, Git/tool classification, path/precondition atomicity, and zero-I/O
claims while preserving its typed-effects direction. See the
[Luban roadmap](../ROADMAP.md) for staged delivery.

## Diff, merge and exact candidate changes

The [diff/merge specification](diff-merge-spec.md) and
[ADR 0005](decisions/0005-deterministic-diff-merge.md) add bounded pure text
computation and later authorized file/snapshot adapters. Diff observes; merge
computes a candidate; validation checks readiness; only separately admitted
application mutates. Pure input values contain no access rights. Every file
input and disclosure, including original/context/conflict content, remains checked.

Existing-file application requires complete original SHA-256/length/provider
version and exact proposed bytes; optional hashes on display-only diff data
cannot weaken writer preconditions. Candidates materialize into existing
capture/executor profiles rather than a new general writer. Conflicted,
unsupported or incomplete sets cannot execute a successful prefix silently.
Known non-atomic batch execution needs explicit host admission; RequireAtomic
fails Unsupported before writes. Existing receipt/recovery and HostControlled
namespace limits remain unchanged. The [D1–D7 plan](implementation-plan.md#diff-and-merge-delivery-track)
separates pure compute from transport, file providers, mutation and language gates.
