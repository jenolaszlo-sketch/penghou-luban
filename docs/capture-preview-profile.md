# Capture-only WhatIf profile

Status: implemented on Windows, .NET 8 and .NET 10, 2026-10-01. This is the
programmatic `Penghou.Luban.Resolution` profile. It extends the
[preview design](preview-resolution-commit-barrier.md) with authorized resolution
and immutable capture. The [read-language profile](read-language-profile.md) and
its version-1 IR remain unchanged; textual patch commands are not enabled.

## Entry points

`PreviewCompiler.Compile` validates a bounded, ordered collection of
`PreviewStage` records, a host workspace ID and optional `PreviewLimits`. It
returns diagnostics or an immutable `CompiledPreviewDocument`. Paths, selectors,
offsets and replacement bytes are normalized and frozen before execution.

```csharp
var draft = PreviewCompiler.Compile(new PreviewStage[]
{
    new FilePatchStage("src/Example.cs", new[]
    {
        new TextPatch(0, 0, Encoding.UTF8.GetBytes("// Reviewed\n"))
    })
}, workspaceId);

if (!draft.Succeeded)
    return; // Surface draft.Diagnostics under the host's disclosure policy.

var runtime = new PreviewRuntime(hostWorkspace, hostProvider, hostPreviewAuthority);
var readiness = await runtime.PreflightAsync(invocation, draft.Document!);
var preview = await runtime.WhatIfAsync(invocation, draft.Document!);
```

The host supplies `WorkspaceReference`, authenticated `EffectInvocation` and
an injected `IWorkspaceProvider` and required `IPreviewAuthorizer`. The logical
workspace holds no physical root. Catalogue `windows-patch-capture-v2` retains
bounded original/proposed bytes for exact semantic validation before execution;
both buffers are protected content and count against `MaxPlanBytes`.
There is no default permit. Static preflight calls
authority only and does no protected target I/O. WhatIf runs preflight again.
This capture profile has no writer callback, process/tool dispatcher, commit method, or batch-admission API. Separate `SinglePatchExecutor` and narrow `BatchPatchExecutor` entry points support exact-target writes; neither is callable from WhatIf. The batch API and its trusted-host recovery contract are documented in the [batch execution profile](batch-execution-profile.md).

## Closed operations

| Stage | Behavior |
| --- | --- |
| FilePatchStage | Read one authorized existing file, preserve its provider version, validate/apply original-version byte edits in memory, capture the proposed patch request. An optional expected version must equal the observed token. |
| GlobPatchStage, AuthorizedView | Enumerate using qualified root-relative globs, freeze the complete selected authorized manifest, then capture the same edit specification independently for every selected file. Selection mode must be explicit. |
| GlobPatchStage, AllMatches | Unresolved, with AllMatchCoverageUnavailable and no discovery. Authorized-only pages cannot prove coverage of hidden files. |
| DeferredToolStage | Only the closed Build, Test and GitCommit enum values are recognized. Report OpaqueEffect; never invoke the tool. |

All concrete targets from all resolvable nodes pass TargetAdmission before the
first file-content read. Directory discovery may already have occurred under
its own resource authority. A denied target blocks the whole capture and returns
no plan, even when other targets were permitted. There is no allowed-prefix
fallback.

Incomplete traversal, depth/result/page bounds and repeated targets leave the
affected node unresolved with no proposed prefix. Every later node is then
unresolved, because its observations could depend on those future mutations.
Opaque effects similarly mark all later nodes DependsOnOpaqueEffect. The profile
does not model future file contents or intermediate mutation versions. Duplicate
explicit paths are rejected during compilation; discovered overlaps are
RepeatedTarget. OrdinalIgnoreCase detects repeated path spellings conservatively;
this does not qualify hard-link or object-alias detection.

## Patch and plan contents

Edits use original-content byte offsets and delete lengths, with strict UTF-8
replacement bytes. They are strictly ordered, nonoverlapping and bounded; both
edit boundaries must be UTF-8 scalar boundaries in the observed original.
Output size is bounded before allocation. There is no fuzzy hunk application,
line-ending conversion, offset relocation or automatic rebasing.

`ResolvedEffectPlan` retains ordered node states/dependencies, the exact frozen
target set, copied immutable edit payloads, observed provider ResourceVersion,
original/proposed hashes and byte lengths, directory/read observations, explicit
selection and unresolved reasons. Byte access returns defensive copies. Full
original/proposed file content is not retained or returned. The exact mutation
payload here is the edit specification plus its original-version precondition;
hashes do not reconstruct source content or provide replay.

Directory observations retain the digest of an authorized page, completeness and
its request identity; they have no fabricated directory version. Selection
promises a fixed observed manifest, not a live-directory snapshot. An empty
authorized selection can be complete; it proves no filesystem-wide absence.
Denied or vanished nested discovery entries can be omitted from that view.

The [canonical preview schema](semantic-preview.md) binds document semantics,
profile/limits, invocation, ordered observations, targets, edit bytes, versions,
hashes, coverage and dependencies into separate document/node/plan identities.
These are identities, not signatures or approvals. Changing the original file
or requested payload cannot reuse the same exact captured plan.

`CaptureComplete` reports whether every node has a fully captured proposal set.
`CanCommit` is always false. Incomplete previews may return a protected plan for
inspection; denial, stale observations, invalid edits, outages or exhausted
document budgets return no partial plan. Already completed authorized reads may
have occurred.

## Authority and bounds

IPreviewAuthorizer receives Preflight, TargetAdmission, ResourceAccess,
ProposalAdmission and Release. Requests bind the compiled document/node,
descriptor/schema/catalogue/provider, typed frozen operation, workspace and host
invocation. Concrete I/O adds its exact action/path and distinct canonical
resource-request identity. ProposalAdmission sees the exact captured edit
request and observed version. Final Release carries the plan identity and
rechecks every node, directory/read observation and proposed patch before return.
Private metadata dependencies also cover authorized nonmatching entries that
contributed to directory-page hashes. They are rechecked without publishing
those additional names and count against the retained-data budget.
Unavailable, unknown, null and throwing decisions fail closed.

The private shared-reader bridge binds the active child identity, host context,
workspace and exact/ancestor/direct-candidate roles. Every root/intermediate
metadata probe and enumeration candidate is checked before provider disclosure.
Patch selection never grants content-read permission, and capture never grants
mutation permission. Persistent storage, later rendering/release and cleanup
need separate host authority; no artifact store or journal is introduced.

Default ceilings: 64 nodes, 1000 targets, 16 MiB total read bytes, 1 MiB per
original/proposed file, 1 MiB declared replacement bytes, 128 declared edits,
4 MiB logical retained plan/exact encoding, 100,000 authority calls and 30 seconds.
Hosts may lower these. Per-file bounds can be raised to 16 MiB within the total
read ceiling; other defaults are hard ceilings. Compiler validation accounts
for declared selection ceilings across nodes. Traversal depth is at most 16;
private candidate probes include denied entries and end probes. Traversal
frontiers are bounded, and glob work has the existing 16,777,216-unit aggregate
ceiling. There is no hidden-candidate counter in public results. Caller
cancellation propagates; deadline exhaustion returns LimitExceeded.

The [shared Windows reader profile](../../Penghou/docs/local-reader-profile.md)
still defines filesystem qualification and path-replacement limitations.
Capturing a content version does not prove that the original native object will
survive until execution. The separate writer/executor profiles describe current
HostControlled namespace assumptions, per-target preconditions, batch limits and
recovery behavior: [single-patch execution](single-patch-execution-profile.md)
and [batch execution](batch-execution-profile.md). Neither changes this capture
profile or makes `CanCommit` true. Governed durable adapters remain future work.
