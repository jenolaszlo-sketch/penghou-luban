Spec Amendment: Preview Resolution and Commit Barrier
Status: Proposed amendment
Applies to: Luban execution model, Penghou.IO.Abstractions, Hufu integration
Purpose: Resolve and authorize concrete side effects before mutation whenever possible, while retaining lazy pre-execution enforcement for effects that cannot be fully previewed.
1. Decision
Luban SHOULD support a Preview / WhatIf resolution phase before performing mutating effects.
Because Luban uses a finite set of typed effects rather than arbitrary shell commands, the runtime can often execute read-only discovery operations first, determine the concrete resources a script intends to modify, and authorize the resulting effect set before any mutation occurs.
The execution model becomes:
Parse and validate
        ↓
Static preflight
        ↓
Preview / WhatIf resolution
        ↓
Resolved effect plan
        ↓
Whole-plan authority check
        ↓
Commit barrier
        ↓
Actual effect execution
        ↓
Just-in-time authority + version checks

Preview is a stronger safety mechanism than relying only on lazy checks, but not every effect can be completely resolved in advance. Effects that cannot be fully previewed continue to use the existing lazy before-execution authorization model.
2. Static preflight
Before any effect executes, Luban MUST parse and validate the complete submitted script or pipeline.
Static preflight detects requirements that are already knowable from syntax and typed arguments.
Example:
cat src/Foo.cs
write secrets/key.txt { ... }

If secrets/key.txt is known to be outside the workflow's authority, the entire invocation is rejected before cat runs.
Static preflight answers:
Is there anything already known about this invocation that makes execution impossible or unauthorized?

A known denied downstream effect MUST prevent unnecessary upstream work.
3. Preview / WhatIf resolution
If static preflight succeeds, Luban SHOULD attempt to resolve dynamic effects without performing mutations.
Preview MAY execute:
Files.Exists
Files.Stat
Files.List
Files.Find
Files.Read
Files.ReadRange
Files.SearchText

Git.Status
Git.Diff
Git.Log
Git.Show

pure pipeline transforms

provided those operations are themselves authorized.
Mutating effects are captured rather than performed.
For example:
fd src "**/*.cs"
    | grep "LegacyGrant"
    | patch ...

may resolve to:
Would read:
    src/A.cs
    src/B.cs
    src/C.cs

Would modify:
    src/A.cs
    src/C.cs

The runtime produces a typed:
ResolvedEffectPlan

containing the concrete targets and known preconditions.
4. Preview does not grant authority
Discovery is not authorization.
If Preview determines that a pipeline would modify:
src/A.cs
src/B.cs
src/qwe/C.cs

and src/qwe/** is denied, the resolved mutation set is rejected.
No mutation occurs.
Likewise, authority to perform discovery does not imply authority to perform the discovered mutation.
The following remain separate:
Files.Find     → read/discovery authority
Files.Read     → content-read authority
Files.Patch    → write authority
Files.Delete   → delete authority

5. Resolved effect plan
Preview SHOULD produce a durable typed representation conceptually similar to:
ResolvedEffectPlan
    InvocationId
    SourceFingerprint

    ObservedEffects[]
    ProposedEffects[]

    RequiredAuthority[]

    Preconditions[]
    UnresolvedEffects[]

    PreviewEvidence

ObservedEffects are effects required to resolve the plan, such as reads and listings.
ProposedEffects are effects that would cross the commit barrier.
UnresolvedEffects are effects whose final target or consequences cannot be completely known before execution.
6. Commit barrier
No previewable mutation may execute until the resolved mutation set has passed Hufu authorization.
Conceptually:
Preview
   ↓
ResolvedEffectPlan
   ↓
Hufu evaluates complete known mutation set
   ↓
AUTHORIZED
   ↓
──────── COMMIT BARRIER ────────
   ↓
mutation begins

The commit barrier is an execution boundary, not merely a UI concept.
Before it, previewable mutating effects MUST have produced zero mutation.
7. Whole-plan authorization
Where the complete mutation set is available, Hufu SHOULD evaluate it as one proposed batch.
This prevents cases such as:
modify allowed file
modify allowed file
modify forbidden file

from partially executing before the denied operation is discovered.
The expected behavior is instead:
resolve all three targets
        ↓
one target denied
        ↓
reject proposed mutation batch
        ↓
zero mutations

This should be the preferred behavior for Luban-native filesystem operations.
8. Just-in-time enforcement remains mandatory
Preview authorization MUST NOT replace the actual-I/O authority check.
Immediately before each protected operation, the executor MUST revalidate relevant runtime state.
This includes, where applicable:
grant still active
grant version still current
workflow/revision still current
resource still resolves to the expected target
expected file hash/version still matches
effect preconditions still hold

This protects against time-of-check/time-of-use changes.
For example:
Preview:
    Foo.cs hash = ABC

Another actor modifies Foo.cs.

Commit:
    expected ABC
    actual DEF

→ PreconditionFailed
→ no overwrite

9. Previewability classification
Effect descriptors SHOULD classify how an effect participates in Preview.
Suggested semantic categories:
FullyPreviewable
DiscoveryOnly
PartiallyPreviewable
LazyOnly

FullyPreviewable
The concrete target and intended effect can be determined before mutation.
Examples:
Files.Write explicit path
Files.Delete explicit path
Files.Move explicit paths
Files.ApplyPatch explicit path

DiscoveryOnly
Safe read-only operations used to resolve later effects.
Examples:
Files.Find
Files.List
Files.Read
Files.SearchText

PartiallyPreviewable
Some information can be determined ahead of time, but final consequences depend on execution.
Example:
Git.Commit

The repository and intended operation are known, but the resulting commit identity is not.
LazyOnly
The resource or consequence cannot be reliably determined until immediately before execution.
Such effects continue to use before-run authorization.
10. Lazy fallback
Preview is best effort within the semantics of registered effects, not a requirement to simulate arbitrary execution.
If an effect cannot be safely resolved during Preview, the runtime MUST NOT invent or guess its consequences.
Instead, it remains unresolved:
UnresolvedEffect
    EffectId
    RequiredAuthority
    Reason

and follows the existing lazy execution model:
reach effect
     ↓
resolve concrete target as far as possible
     ↓
authorize immediately before execution
     ↓
execute or deny

This is the fallback behavior for current limitations.
11. Mixed pipelines
A Luban pipeline may contain both previewable and lazy effects.
Example:
fd src "**/*.cs"
    | ...
    | approved-tool operation

Execution may therefore produce:
Resolved:
    filesystem reads
    filesystem writes

Unresolved:
    approved native tool invocation

The runtime MUST clearly distinguish these.
Known mutations still receive whole-plan preflight.
The unresolved effect receives its own just-in-time authorization before execution.
The presence of a lazy effect MUST NOT weaken authorization of effects that can be previewed.
12. No false simulation guarantees
Preview MUST NOT pretend to simulate effects whose behavior depends on arbitrary executable code.
For example:
DotNet.Test
DotNet.Build
custom tool adapter
remote external API

may execute code whose complete filesystem or network behavior is not knowable from the Luban request alone.
For such effects, Preview may report:
Would invoke:
    DotNet.Test

Declared authority:
    process.execute:dotnet-test

Detailed subprocess effects:
    unresolved

The operation remains subject to lazy authorization and the guarantees of its execution provider.
13. Preview consistency
The executor SHOULD retain observations made during Preview when they affect safe commit.
For file mutations this commonly means:
canonical path
content hash
file identity where available
metadata/version

The mutation then carries these values as preconditions.
This converts Preview from merely informational output into useful concurrency protection.
14. Side-effect-free preview guarantee
For effects classified as previewable, Preview MUST NOT perform their mutation.
A --whatif / Preview run should therefore be able to guarantee:
No mutating Luban effect crossed the commit barrier.

Read/discovery effects may still occur and are themselves subject to Hufu authorization.
15. User-facing WhatIf
Luban MAY expose the same mechanism directly:
luban --whatif script.luban

or through an API equivalent.
Example rendering:
Would read:
  src/Hufu.cs
  src/Grant.cs

Would modify:
  src/Hufu.cs

Would delete:
  src/LegacyGrant.cs

Unresolved:
  DotNet.Test

No mutations performed.

The CLI representation is secondary. ResolvedEffectPlan is the normative output.
16. Durable execution
When used under Zhinu, the following boundaries SHOULD be durable where appropriate:
InvocationAccepted
StaticPreflightCompleted
PreviewCompleted
ResolvedPlanAuthorized
CommitStarted
EffectCompleted

On restart, the runtime MUST NOT assume that a previously previewed plan is still safe to commit without revalidating the required versions, grants and resource preconditions.
17. Security invariant
The preferred Luban invariant becomes:
Resolve before mutation where the effect model permits it; reject the whole known mutation set before commit when any known effect is unauthorized; and lazily recheck every actual protected operation immediately before it occurs.

Or more compactly:
Static check
    +
WhatIf resolution
    +
whole-plan admission
    +
just-in-time enforcement

No one stage replaces another.
18. Implementation order
For the first implementation, Preview SHOULD focus on filesystem operations because their targets and preconditions are comparatively well defined.
Initial Preview support:
Files.List
Files.Find
Files.Stat
Files.Exists
Files.Read
Files.ReadRange
Files.SearchText

Files.Write
Files.ApplyPatch
Files.ReplaceText
Files.Copy
Files.Move
Files.Delete
Files.CreateDirectory

Git inspection can follow.
Approved developer tools and external operations may remain LazyOnly initially.
This preserves the current before-run lazy checks while incrementally adding stronger resolution where Luban can actually guarantee it.
19. Roadmap wording
I would summarize the amendment in the roadmap as:
Preview resolution and commit barrier: Luban will attempt a read-only WhatIf pass before mutation, using authorized discovery effects to resolve concrete targets and produce a typed ResolvedEffectPlan. Where the complete mutation set can be resolved, Hufu authorizes the whole set before any mutation crosses the commit barrier. Effects that cannot be fully resolved remain subject to lazy just-in-time authorization. All actual protected I/O is rechecked for current authority, resource identity and version/precondition validity immediately before execution.

This makes Preview a meaningful safety feature without turning Luban into an emulator or requiring every possible effect to be predictable.