Typed Effect Runtime for Safe Agent Work
Status: Proposed roadmap item
Working name: Penghou.Effects
Initial owner: Hufu roadmap
Long-term direction: independent Penghou library
Primary consumers: Hufu, Fuwen, Zhinu, Marang, Guyabano, Qingniao
Primary use case: source-code work without arbitrary shell execution
1. Summary
Penghou should provide a small, typed effect runtime for common agent operations such as reading files, finding files, searching source content, applying patches, inspecting Git state, and invoking a limited catalogue of approved developer tools.
The runtime exists to avoid making arbitrary shell execution the default mechanism for agent work.
Instead of allowing an agent to generate PowerShell, Bash, Python or arbitrary command lines for routine operations, the agent produces typed effect requests.
For example, instead of:
Get-ChildItem -Recurse -Filter *.cs |
    Select-String "WorkflowAuthority"

the agent requests:
Files.SearchText {
    root: workspace.main,
    include: "**/*.cs",
    text: "WorkflowAuthority"
}

Hufu can authorize the request before execution because its effect and resource requirements are explicit.
The core principle is:
Constrain the set of executable effects rather than attempting to sandbox an unrestricted execution language.

In strict mode, arbitrary shell and process execution are rejected rather than sandboxed.
2. Motivation
Coding agents frequently use shells for operations that do not inherently require a shell:
- listing directories;
- finding files;
- reading files;
- reading file ranges;
- searching source code;
- replacing text;
- applying patches;
- copying or moving files;
- inspecting Git status;
- inspecting diffs;
- checking whether files exist;
- examining tool/environment information.
This introduces unnecessary complexity and risk.
A command such as:
Get-ChildItem ...

is opaque to an authorization system compared with:
Files.List(...)

A generated PowerShell program can also contain loops, pipelines, subprocesses, environment access and arbitrary filesystem operations unrelated to the original intent.
A typed effect API makes the requested action explicit before execution.
This provides:
- predictable authority requirements;
- structured input validation;
- structured results;
- host-independent semantics;
- easier durable replay;
- better audit evidence;
- simpler concurrency controls;
- fewer shell portability problems;
- a substantially smaller security surface.
It does not attempt to contain arbitrary native processes.
3. Goals
The library MUST:
1. Provide typed operations for common source-code and workspace manipulation.
2. Make every operation's intended side effects knowable before execution.
3. Integrate naturally with Hufu authority evaluation.
4. Avoid requiring PowerShell, Bash or another shell for routine work.
5. Work consistently across Windows, Linux and macOS where the underlying operation is portable.
6. Produce structured results rather than console output intended for model parsing.
7. Support durable workflow execution and safe retries.
8. Support optimistic concurrency for mutations.
9. Allow hosts to reject unsupported effects before execution.
10. Allow Marang to delegate effects to a supervising coding agent.
11. Allow Guyabano to execute supported effects directly.
12. Eventually exist as an independent library rather than being part of Hufu itself.
4. Non-goals
The library MUST NOT initially:
- implement a general shell;
- execute arbitrary command strings;
- parse PowerShell/Bash;
- provide arbitrary scripting;
- provide loops or general-purpose control flow;
- provide variables or command substitution;
- provide pipes;
- claim to sandbox arbitrary processes;
- replace Fuwen control flow;
- replace Hufu authorization;
- replace Zhinu durability;
- provide a complete POSIX shell abstraction;
- expose raw operating-system handles to agents.
Control flow belongs to Fuwen and Zhinu.
Authorization belongs to Hufu.
The effect runtime executes a previously authorized, bounded operation.
5. Architecture
The intended flow is:
Agent
  ↓
Fuwen activity
  ↓
Typed Effect Request
  ↓
Hufu authorization
  ↓
Zhinu durable effect boundary
  ↓
Effect executor
  ↓
OS / Git / HTTP / developer tool
  ↓
Structured result
  ↓
Evidence

For Marang:
Marang workflow
      ↓
Effect Request
      ↓
Hufu
      ↓
SupervisorActionRequired
      ↓
Codex / Claude Code / other supervisor
      ↓
supervisor tool
      ↓
Effect Result
      ↓
Marang resumes

For Guyabano:
Guyabano
   ↓
Effect Request
   ↓
Hufu
   ↓
local Effect Executor
   ↓
OS

The same logical effect can therefore be executed by different providers.
6. Effect model
Every operation should be represented as a typed request.
Conceptually:
public interface IEffectRequest
{
    EffectKind Kind { get; }
}

Execution produces a typed result:
public interface IEffectResult
{
    EffectKind Kind { get; }
}

An envelope around the request should include durable execution metadata:
public sealed record EffectInvocation(
    EffectId Id,
    WorkflowRunId WorkflowRunId,
    NodeId NodeId,
    IEffectRequest Request,
    EffectPreconditions Preconditions);

The effect request itself MUST remain free of workflow-engine concerns wherever practical.
7. Initial filesystem effects
The first release should focus heavily on source workspace operations.
7.1 Inspection
Files.Exists
Files.Stat
Files.List
Files.Find
Files.Read
Files.ReadRange
Files.SearchText

Files.List
Lists direct children of a directory.
Request:
root
includeFiles
includeDirectories

Result:
path
name
kind
size?
modified?

It should not recursively traverse unless explicitly requested.
Files.Find
Find paths using a constrained path/glob expression.
Example:
root: workspace.main
pattern: "**/*.cs"

The operation returns paths, not file contents.
Files.Read
Reads a file subject to a configured maximum result size.
Result SHOULD contain:
content
encoding
contentHash
length

Files.ReadRange
Reads a bounded line or byte range.
This replaces shell patterns such as:
Get-Content X |
    Select-Object -Skip 100 -First 50

Request example:
path
startLine
lineCount

Files.SearchText
Searches text without requiring grep, rg or PowerShell.
Request:
root
query
include
exclude
caseSensitivity
maximumMatches
contextBefore
contextAfter

Result:
path
line
column
matchedText
contextBefore[]
contextAfter[]

This operation should eventually support literal and bounded regular-expression modes.
Arbitrary executable search predicates are prohibited.
8. Filesystem mutations
Initial mutation effects:
Files.Write
Files.ApplyPatch
Files.ReplaceText
Files.Copy
Files.Move
Files.Delete
Files.CreateDirectory

Mutation operations MUST support preconditions.
For example:
Files.ApplyPatch {
    path: "/src/Foo.cs",
    expectedHash: "abc...",
    patch: ...
}

If the file changed after the agent inspected it:
PreconditionFailed

rather than silently overwriting another actor's change.
This is important for both durable retries and concurrent agents.
9. Prefer patching over complete replacement
For existing source files, Files.ApplyPatch SHOULD generally be preferred over Files.Write.
A patch operation can carry:
expectedContentHash
expectedRevision
patch

and provide clear evidence about what changed.
Files.Write remains necessary for:
- new files;
- generated files;
- complete replacement where explicitly intended.
10. Path handling
Filesystem effects MUST perform canonical path resolution.
Authorization MUST NOT rely only on the input string.
Implementations must consider:
- . and ..;
- relative versus absolute paths;
- path separators;
- Windows case behavior;
- symlinks;
- Windows junctions;
- mount points where detectable;
- paths outside the declared workspace.
A request must be evaluated against the resolved target appropriate for that operation.
For example:
workspace/src/link -> workspace/qwe

must not allow an operation forbidden against workspace/qwe merely because the submitted path was:
workspace/src/link/file.cs

11. Workspace abstraction
Effects should normally use symbolic workspace references rather than unrestricted machine paths.
For example:
WorkspaceReference:
    workspace.main

Path:
    src/Foo.cs

instead of requiring agents to reason about:
C:\Users\jeno\code\project\src\Foo.cs

The host resolves:
workspace.main

to the actual local resource.
This improves portability and authorization.
12. Git effects
Git shell usage is common enough to warrant a typed surface.
Initial read-only effects:
Git.Status
Git.Diff
Git.Show
Git.Log
Git.Branches
Git.CurrentBranch

Possible later mutation effects:
Git.Stage
Git.Unstage
Git.Commit
Git.Checkout
Git.CreateBranch

Remote operations SHOULD remain separately classified:
Git.Fetch
Git.Pull
Git.Push

because network and remote mutation have different authority implications.
Hufu should therefore be able to distinguish:
git.inspect
git.modify.local
git.read.remote
git.write.remote

rather than granting one generic git capability.
13. Source-code-specific operations
A later version MAY provide source-aware effects where generic file operations become inefficient.
Examples:
Source.FindSymbol
Source.FindReferences
Source.ListTypes
Source.ListMethods
Source.ReadSymbol

These would normally sit above language services or project analyzers.
They are not necessary for V1.
The source-code API should not turn the effect runtime into a compiler framework.
14. Developer tool adapters
Full arbitrary process execution should not be required simply to run common developer tools.
The library MAY expose constrained adapters such as:
DotNet.Build
DotNet.Test
DotNet.Restore
DotNet.Format

Node.Build
Node.Test

Git.*

Each adapter owns argument construction.
The agent requests:
DotNet.Test {
    project: "Penghou.Hufu.Tests.csproj",
    configuration: "Release"
}

rather than:
powershell -Command "dotnet test ... && ..."

This substantially reduces the command surface.
15. Tool adapters are not full sandboxing
An approved developer tool may itself execute arbitrary code.
For example:
dotnet test

can execute:
- MSBuild tasks;
- test assemblies;
- generated code;
- subprocesses.
Therefore tool adapters need an explicit execution-risk classification.
For example:
EffectRisk.Brokered
EffectRisk.NativeTool
EffectRisk.ArbitraryProcess

Files.Read is brokered.
DotNet.Test executes native/code workloads.
Hosts must not claim that native tool adapters provide complete filesystem/network containment unless an isolation provider is also active.
16. Execution modes
Hosts should expose at least three modes.
Strict Effects
StrictEffects

Allowed:
typed effect catalogue only

Rejected:
arbitrary shell
arbitrary process
unknown executable
script execution

This is the primary Hufu-safe mode.
Approved Tools
ApprovedTools

Allows typed effects plus explicitly registered developer tool adapters.
Example:
DotNet.Test
Git.Status

No arbitrary command strings.
Unrestricted Process
UnrestrictedProcess

Allows arbitrary native execution.
This mode MUST require an execution provider explicitly advertising suitable isolation or an explicit host policy accepting the weaker guarantee.
It should never be an accidental fallback.
17. No automatic downgrade
If an activity requires:
Process.Run arbitrary executable

but the host supports only:
StrictEffects

the runtime MUST NOT reinterpret the request into a shell call or weaker operation.
It returns:
UnsupportedEffect

or:
ExecutionCapabilityUnavailable

The planner may then replan.
18. Hufu integration
Every effect type must have a deterministic authority mapping.
Example:
Files.Read(workspace.main, "src/A.cs")
    =>
workspace.read(workspace.main, "src/A.cs")

Files.ApplyPatch(workspace.main, "src/A.cs")
    =>
workspace.write(workspace.main, "src/A.cs")

Git.Push(origin, branch)
    =>
git.remote.write(repository, origin, branch)

The mapping MUST NOT be supplied by the agent.
It is part of the trusted effect catalogue.
19. Effect descriptors
Each effect should have trusted metadata.
Conceptually:
public sealed record EffectDescriptor(
    EffectKind Kind,
    EffectRisk Risk,
    bool MutatesState,
    bool ReplaySafe,
    bool RequiresNativeExecution,
    AuthorityRequirementFactory AuthorityRequirements);

Potential additional metadata:
maximum input size
maximum output size
allowed workspace types
network behavior
credential requirements
provider requirements

20. Hufu admission
Before execution:
effect request
    ↓
derive required authority
    ↓
compare with activity authority
    ↓
compare with workflow envelope
    ↓
Allow / Deny

The invariant remains:
EffectAuthority
    ⊆ ActivityAuthority
    ⊆ WorkflowAuthority

A denied request performs zero protected I/O.
21. Fuwen integration
Fuwen should be able to declare effects as activity capabilities.
Example syntax is non-normative:
activity inspect-source {
    effects {
        Files.Find
        Files.Read
        Files.SearchText
    }

    requires {
        workspace.read(workspace.main, "src/**")
    }
}

Mutation activity:
activity implement-fix {
    effects {
        Files.Read
        Files.ApplyPatch
    }

    requires {
        workspace.read(workspace.main, "src/**")
        workspace.write(workspace.main, "src/**")
    }
}

Effect categories should participate in static workflow validation.
22. Fuwen replaces shell control flow
The effect language MUST NOT grow loops or branching.
Instead:
Files.Find
    ↓
Fuwen foreach / Zhinu FanOut
    ↓
Files.Read
    ↓
analysis activity

rather than:
foreach ($file in Get-ChildItem ...) {
    ...
}

This is one of the primary architectural benefits.
23. Zhinu integration
Effect execution should be a durable workflow boundary.
Conceptually:
EffectPrepared
EffectAuthorized
EffectStarted
EffectCompleted

The runtime should persist sufficient information to determine whether an operation can safely be retried.
For mutations, a durable effect identity plus preconditions is required.
24. Idempotency and replay
Effects should declare replay semantics.
Example classifications:
Naturally replay-safe
Files.Exists
Files.Stat
Files.Read
Files.Find
Files.SearchText
Git.Status
Git.Diff

Conditionally replay-safe
Files.Write
Files.ApplyPatch
Files.Move
Files.Delete
Git.Commit

These require:
- expected hashes;
- version/precondition checks;
- idempotency keys;
- result reconciliation.
Unsafe/externally effectful
Git.Push
Http.Post
deployment
package publication

These require stronger recovery contracts and may remain outside the initial library.
25. Marang integration
Marang SHOULD NOT execute an effect directly when the supervising agent already provides suitable tools.
Instead it emits:
SupervisorActionRequest

containing the typed effect.
Example:
EffectId: E-41

Files.Read {
    workspace: workspace.main
    path: "src/Foo.cs"
}

Codex may satisfy this through its own file tooling.
It then returns a structured result associated with E-41.
Marang validates and commits the result.
The supervisor remains responsible for its own sandbox and tool behavior.
26. Marang supervisor protocol
The protocol should conceptually support:
request_effect
complete_effect
fail_effect
reject_effect

A pending request must survive Marang restart.
A result for:
- an unknown effect;
- a completed effect;
- an old workflow revision;
- a cancelled workflow;
must be rejected.
27. Guyabano integration
Guyabano will eventually host local effect executors.
Initially, Guyabano may operate only in:
StrictEffects

or:
ApprovedTools

mode.
This allows useful local coding workflows without claiming arbitrary-process isolation.
A later sandbox provider may enable:
UnrestrictedProcess

for appropriate workloads.
28. Qingniao integration
A delegated activity must receive only the effect capabilities explicitly delegated to it.
For example:
Parent activity may use:
    Files.Read
    Files.ApplyPatch
    Git.Status
    Git.Commit

Child delegation receives:
    Files.Read
    Files.ApplyPatch

The child cannot request Git.Commit.
This gives another attenuation invariant:
DelegatedEffects ⊆ ParentEffects

in addition to:
DelegatedAuthority ⊆ ParentAuthority

29. Unknown operations
Unknown operations MUST fail closed.
Example:
Shell.Execute(...)

when Shell.Execute is not registered:
UnsupportedEffect

The runtime must not attempt to infer an equivalent implementation.
This is deliberate.
30. Escape requests
An agent MAY report that no registered effect can perform required work.
For example:
MissingEffectCapability {
    desiredOperation:
        "run custom schema generator"
}

This is planning evidence.
Guihua may:
- redesign the workflow;
- choose another executor;
- ask the supervisor;
- request stronger execution capability.
It must not silently convert the request into shell execution.
31. Structured errors
Effect failures should remain typed.
Examples:
NotFound
AccessDenied
AuthorityDenied
PreconditionFailed
InvalidPath
OutsideWorkspace
UnsupportedEffect
UnsupportedPlatform
ResultLimitExceeded
NativeToolFailed
ProviderUnavailable
AmbiguousOutcome

Avoid forcing models to classify human-readable stderr whenever a typed condition is available.
32. Result limits
Effects MUST apply bounded output limits.
For example:
Files.Find maximum paths
Files.SearchText maximum matches
Files.Read maximum bytes
Git.Log maximum entries

An agent must explicitly request another page/range rather than accidentally dumping an entire repository into model context.
Pagination should use stable continuation state where practical.
33. Observability
Each effect should emit structured evidence containing:
EffectId
WorkflowRunId
NodeId
EffectKind
normalized target
authority decision reference
start time
completion time
outcome
result summary
content/result hash where relevant

Sensitive content should not automatically enter logs.
For file reads, evidence may record:
path
hash
length

without retaining file content unless required by the workflow evidence policy.
34. Security properties
In Strict Effects mode, Penghou should be able to make the following claim:
The agent cannot directly execute arbitrary operating-system commands through the effect runtime. It can perform only operations represented by registered effect types and authorized by Hufu.

It should NOT claim:
Arbitrary native programs cannot access resources outside the workflow authority envelope.

That stronger claim requires process isolation.
35. Why this reduces sandbox requirements
Without an effect runtime:
Agent
    ↓
PowerShell/Bash
    ↓
arbitrary OS behavior

requires containment of a Turing-complete command environment.
With Strict Effects:
Agent
    ↓
finite typed effect vocabulary
    ↓
trusted handlers

the trusted computing surface becomes the effect handlers.
Each handler is small enough to reason about and test independently.
Sandboxing remains relevant only when the runtime permits native executable code.
36. Package structure
Initial implementation could live under Hufu while contracts stabilize.
For example:
Penghou.Hufu.Effects

Long term, extract it into its own library:
Penghou.Effects

Suggested packages:
Penghou.Effects.Abstractions
Penghou.Effects.Files
Penghou.Effects.Git
Penghou.Effects.DotNet
Penghou.Effects.Http

This split should happen only when useful. It need not be the V1 package structure.
37. Provider abstraction
The same effect should be executable through different providers.
Conceptually:
public interface IEffectExecutor
{
    bool Supports(EffectDescriptor effect);

    ValueTask<EffectResult> ExecuteAsync(
        EffectInvocation invocation,
        CancellationToken cancellationToken);
}

Potential implementations:
LocalEffectExecutor
SupervisorEffectExecutor
RemoteWorkerEffectExecutor
SandboxEffectExecutor

No effect should depend directly on MCP or a specific coding agent.
38. Capability discovery
An executor should advertise its supported effect set.
Example:
Local provider:
    Files.*
    Git.Status
    Git.Diff
    DotNet.Build
    DotNet.Test

Codex supervisor:
    Files.*
    Git.*
    approved process operations

Fuwen/Guihua can then avoid planning activities that cannot be executed by the selected provider.
39. Conformance suite
The library should eventually include a public conformance suite.
Every filesystem implementation must prove:
cannot escape workspace through ..
cannot bypass exclusions through symlink
cannot bypass exclusions through junction where applicable
wrong authority performs zero I/O
revoked authority performs zero new I/O
failed precondition performs zero mutation
duplicate successful mutation reconciles correctly
read result limits are enforced
search result limits are enforced
unsupported effects fail closed

Cross-platform handlers should run equivalent semantic tests on supported systems.
40. Initial operation set
For the first useful release I would deliberately keep the scope small:
Files.Exists
Files.Stat
Files.List
Files.Find
Files.Read
Files.ReadRange
Files.SearchText

Files.Write
Files.ApplyPatch
Files.Copy
Files.Move
Files.Delete
Files.CreateDirectory

Git.Status
Git.Diff

That should already eliminate a large portion of routine coding-agent shell usage.
I would not initially add:
Process.Run
Shell.Run
PowerShell.Run
Bash.Run

because those immediately destroy the main security simplification.
41. Second milestone
After the filesystem surface is proven:
Git.Show
Git.Log
Git.Branches
Git.Stage
Git.Commit

DotNet.Build
DotNet.Test
DotNet.Restore

The Git and .NET adapters should remain explicit operations rather than generic CLI invocation.
42. Later milestones
Potential future effect families:
Http.Get
Http.Download

Archive.List
Archive.Extract

Source.FindSymbol
Source.FindReferences

Packages.Query
Packages.Restore

Git remote operations

Operations with consequential external effects should require correspondingly stronger Hufu authority.
43. Explicitly deferred
The following should remain outside the roadmap item until demonstrated necessary:
general scripting language
pipes
shell expressions
environment-variable expansion
arbitrary subprocess execution
interactive terminal sessions
PTY emulation
full container sandbox
OS-specific sandbox implementation

These are separate problems.
44. Acceptance scenario
The first complete scenario should demonstrate:
1. Guihua authors a workflow requiring source inspection and modification.
2. Fuwen declares Files.Find, Files.Read, Files.SearchText and Files.ApplyPatch.
3. Hufu grants read access to the repository and write access only to /src/**.
4. Zhinu starts the admitted workflow.
5. The workflow finds source files without invoking a shell.
6. It searches those files without grep, rg, PowerShell or Bash.
7. It reads only the required ranges.
8. It proposes a patch.
9. The patch carries the previously observed file hash.
10. Hufu verifies the exact write target.
11. The effect runtime applies the patch.
12. A write outside /src/** is rejected with zero filesystem modification.
13. An attempt to request an unregistered shell command returns UnsupportedEffect.
14. Restarting the workflow does not duplicate a completed mutation.
15. Structured evidence shows exactly which effects were authorized and executed.
45. Roadmap placement
I would add this to Hufu roughly as:
Typed effect enforcement
Goal: provide a safe execution surface for common agent operations without requiring arbitrary shell execution or a general OS sandbox.
Direction:
- introduce typed effects for source workspace operations;
- derive Hufu authority requirements from trusted effect descriptors;
- support strict mode where unknown effects and arbitrary process execution are rejected;
- make effects durable and replay-aware through Zhinu;
- allow Marang to forward effects to a supervising coding agent;
- allow Guyabano to execute supported effects through local providers;
- extract the effect model into an independent Penghou.Effects library once the contracts stabilize;
- treat arbitrary process execution and OS sandboxing as a separate future execution-provider concern.
The architectural rule I would put at the top of the roadmap item is:
Prefer a small vocabulary of explicit effects over unrestricted command execution. Fuwen provides control flow, Hufu provides authority, and effect providers perform only the operation they were given.

That gives you a very practical route to strong local permissions without making “build a cross-platform sandbox” a prerequisite for Hufu or Guyabano.