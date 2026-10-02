# Shared I/O integration build

Read/Find/literal SearchText, the read language, capture and patch execution take
an explicit `IWorkspaceProvider`. Neutral runtime code imports no Local provider
and carries no physical root. IO.Abstractions owns contracts; IO.Protocols owns
the shared codec and namespace profile. Tests and the sample compose IO.Local.
The required IEffectAuthorizer
still admits the semantic request and checks every concrete resource. This is a
standalone neutral bridge; Hufu grants, approvals and persistent scope mappings
are not implemented.

## Default packages and explicit source development

Default builds pin IO.Abstractions and IO.Protocols `0.1.0-preview.1`; tests and
the sample pin IO.Local separately. A normal package build needs no Penghou
checkout. Publication and actual NuGet.org restores are tracked in the
[corrective ledger](../../Penghou/docs/resource-abstractions-corrective-plan.md).
Local candidate-feed qualification is intermediate, not a published release.

Use the .NET 10 SDK and sibling checkouts named Penghou.Luban and Penghou.
The shared source is available at [GitHub](https://github.com/jenolaszlo-sketch/penghou).
Use the coordinated corrective changes in both repositories, then run:

```powershell
dotnet test ..\Penghou\Penghou.IO.slnx -c Release
dotnet test Penghou.Luban.sln -c Release -p:UsePenghouSource=true
```

The projects target net8.0 and net10.0. Both runtimes are needed for the full
suite. PenghouRoot defaults to the sibling directory and can be supplied as an
absolute MSBuild property for another layout:

```powershell
dotnet test Penghou.Luban.sln -c Release -p:UsePenghouSource=true -p:PenghouRoot=C:\source\Penghou
```

Source references are opt-in development tooling. The final delivery gate uses
pinned published packages and real integration tests without the IO source
checkout. No contract implementation is copied into a consumer.

## Semantic and resource identities

The existing public request digest remains SHA-256 of its JSON bytes. Each
concrete read/list request separately uses ResourceRequestIdentity.Compute.
The operation-local bridge retains the current concrete identity and exact
target/action roles, verifies invocation/workspace/scope bindings, and asks the
required semantic checker with the original parent digest. Find cannot perform
content reads, and no supported mode grants writes. The bridge does not compare
a child read hash to the parent SearchText digest. It is private trusted runtime
composition, not an agent-supplied authorizer or source-selected scope.

## Bounded traversal

Legacy Find/SearchText preserve recursive, case-insensitive basename patterns
and typed path/line/text outputs. The language Find/search layer uses bounded
root-relative `*`, `?`, and whole-segment `**` path-globs; it does not change
legacy matching. Each
semantic operation owns and disposes one shared reader. Its private aggregate
candidate allowance is the public MaxEntries limit, spanning all directories
and including denied candidates. End-of-enumeration probes consume work too;
this intentionally gives conservative truncation without leaking hidden counts.
The runtime captures at most one bounded page per directory and stops on any
incomplete page or exhausted allowance. It never treats truncated coverage as
complete. Internal directory output is capped at 4 MiB, independently of the
public match-output budget.

Search checks authorized metadata sizes and remaining scanned-byte allowance
before reads, then checks actual returned bytes. Invalid UTF-8 files are omitted
from text search; Read reports invalid text. Live candidate read denial omits
content, unavailable authorization aborts without a partial result, and semantic
root denial blocks the operation. Appended UnsupportedProfile and ProviderFailure
statuses distinguish qualified-profile rejection and provider faults from OS access
denial; original status values are unchanged.

The shared [reader profile](../../Penghou/docs/local-reader-profile.md) states
path-race, hard-link, mount, Unicode identity and cooperative OS-deadline limits.
These reads do not supply confinement or atomic mutation consistency. The
minimal parser, typed semantic IR, static preflight and bounded take/count
execution are implemented. Language authorization uses Preflight, EffectStart,
ResourceAccess, and Release phases; direct legacy effects retain their required
IEffectAuthorizer. Capture-only resolution is implemented and includes no
writer callback. Separate single-target and [bounded exact-target batch executors](batch-execution-profile.md)
use an injected conditional byte writer, fresh admission and required
start/outcome evidence. Batch recovery uses authoritative host snapshots and
validated completion receipts. Selected-glob execution, production recovery
stores, and Hufu/Zhinu integration remain later deliveries.
Execution uses bounded buffered values, without lazy-stream or
backpressure guarantees.
