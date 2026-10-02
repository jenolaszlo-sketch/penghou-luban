# Luban Diff and Merge Specification

## Status

Proposed

## Purpose

Add deterministic diff and merge primitives to Luban.

The goal is to let agents and workflows:

- compare files, text, directories, snapshots, and structured change sets;
- produce machine-readable differences;
- apply changes safely;
- perform deterministic three-way merges;
- detect conflicts without asking an AI model to resolve them automatically;
- expose affected resources to authorization, audit, workflow, and evidence systems.

Diff and merge are generic Luban capabilities. They must not depend on Git.

Git integration may later consume or produce the same Luban change representations.

---

# 1. Design principles

## 1.1 Diff is observation

A diff describes the difference between two states.

```text
A -> B
```

Diff must not mutate either state.

Examples:

```text
diff text old new
diff file before.cs after.cs
diff directory baseline current
diff snapshot previous current
```

---

## 1.2 Merge is deterministic transformation

Merge combines independently modified states relative to a common base.

```text
merge base ours theirs
```

Luban performs deterministic merge processing and identifies conflicts.

Luban must not silently use an LLM to resolve semantic conflicts.

If deterministic merging cannot produce a safe result, the operation returns structured conflicts for the workflow or agent to handle.

---

## 1.3 Structured results are primary

Human-readable unified diff output is useful, but it must not be the internal representation.

Luban should expose typed structures representing:

- files affected;
- additions;
- modifications;
- deletions;
- moves or renames where detectable;
- changed ranges;
- original content;
- replacement content;
- conflicts.

Text formats such as unified diff are serialization formats over this structured representation.

---

## 1.4 Git independence

The following must work without Git:

```text
diff file A B
merge base ours theirs
apply patch
```

Git-specific metadata such as commits, branches, remotes, indexes, and object IDs belongs in a separate Git integration.

---

# 2. Core concepts

Luban should introduce three related concepts:

```text
Diff
Patch
MergeResult
```

A `Diff` describes change.

A `Patch` describes a change that can be applied.

A `MergeResult` describes the result of combining concurrent changes.

These concepts should be related but not treated as identical.

---

# 3. Diff model

A diff should contain one or more changes.

Conceptually:

```csharp
public sealed record Diff(
    IReadOnlyList<ResourceChange> Changes);
```

Initial implementation should focus primarily on files and text.

Possible resource model:

```csharp
public abstract record ResourceChange;

public sealed record FileChange : ResourceChange
{
    public required string Path { get; init; }

    public string? PreviousPath { get; init; }

    public required ChangeKind Kind { get; init; }

    public IReadOnlyList<DiffHunk> Hunks { get; init; } = [];
}
```

`ChangeKind`:

```csharp
public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed
}
```

Rename detection may initially be optional.

---

# 4. Diff hunks

A hunk represents a localized change.

Example:

```csharp
public sealed record DiffHunk
{
    public required SourceRange OriginalRange { get; init; }

    public required SourceRange NewRange { get; init; }

    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
}
```

Lines may be:

```csharp
public enum DiffLineKind
{
    Context,
    Added,
    Removed
}
```

This representation should retain enough source context to:

- display the diff;
- convert it to a patch;
- validate application against a target;
- detect stale edits.

---

# 5. Diff operations

Initial Luban operations should support:

## 5.1 Text diff

```text
diff text before after
```

Produces a structured text diff.

---

## 5.2 File diff

```text
diff file before.cs after.cs
```

Compares the contents of two files.

The paths do not need to be within the same workspace.

---

## 5.3 Directory diff

```text
diff directory ./baseline ./workspace
```

Produces changes including:

- added files;
- modified files;
- deleted files;
- optionally detected renames.

Directory comparison should support exclusions.

Example:

```text
diff directory ./baseline ./workspace
    exclude "**/bin/**"
    exclude "**/obj/**"
```

---

## 5.4 Snapshot diff

Luban should allow callers to compare workspace snapshots where a snapshot provider is available.

```text
diff snapshot baseline current
```

Snapshot storage itself does not need to be owned by the diff subsystem.

---

# 6. Diff algorithms

The first implementation should prioritize predictable behavior over sophisticated heuristics.

For text, use a standard line-based diff algorithm such as:

- Myers diff; or
- another deterministic shortest-edit implementation.

Optional later improvements may include:

- patience diff;
- histogram diff;
- token-aware diff;
- syntax-aware diff.

These should be selectable strategies rather than different Diff representations.

Example:

```csharp
DiffOptions
{
    Algorithm = DiffAlgorithm.Myers
}
```

The default must remain deterministic.

---

# 7. Binary files

Binary resources must not be interpreted as text.

The diff result should indicate:

```text
BinaryModified
BinaryAdded
BinaryDeleted
```

Optional metadata may include:

- size;
- hash;
- MIME or detected content type.

Luban should not attempt arbitrary binary delta generation in the initial implementation.

---

# 8. Patch relationship

A diff can often be converted into an applicable patch.

Conceptually:

```text
patch = diff.to_patch()
```

A patch should contain enough information to validate that the expected source still exists before modifying it.

Example:

```csharp
public sealed record Patch
{
    public IReadOnlyList<PatchOperation> Operations { get; init; } = [];
}
```

Possible operations:

```text
CreateFile
ModifyFile
DeleteFile
MoveFile
```

A modification should contain:

```csharp
ExpectedContent
ReplacementContent
Anchor
```

or equivalent range/context information.

---

# 9. Patch validation

Before applying a patch, Luban should be able to validate it without mutation.

```text
validate patch against workspace
```

Possible results:

```text
Valid
Stale
Conflict
MissingResource
UnexpectedResource
InvalidPath
```

This is important for agent-generated changes because the workspace may have changed since the agent produced the patch.

Patch validation should be available separately from patch application.

---

# 10. Merge model

Luban should initially support three-way merge.

```text
merge base ours theirs
```

The base represents the common source state.

`ours` and `theirs` represent independently modified states.

Example:

```csharp
public sealed record MergeResult<T>
{
    public T? Value { get; init; }

    public bool IsClean { get; init; }

    public IReadOnlyList<MergeConflict> Conflicts { get; init; } = [];
}
```

---

# 11. Three-way merge behavior

Given:

```text
base
ours
theirs
```

Luban should automatically merge changes when:

- only one side changed a region;
- both sides made the same change;
- changes affect independent regions.

Example:

```text
base:
A
B
C

ours:
A
B1
C

theirs:
A
B
C1
```

Result:

```text
A
B1
C1
```

---

# 12. Conflict detection

A conflict occurs when both sides make incompatible changes to the same logical source region.

Example:

```text
base:
Timeout = 10

ours:
Timeout = 20

theirs:
Timeout = 30
```

Luban should report a conflict rather than selecting one side.

Conceptual representation:

```csharp
public sealed record MergeConflict
{
    public required ConflictKind Kind { get; init; }

    public string? Path { get; init; }

    public required SourceRange Range { get; init; }

    public string? BaseContent { get; init; }

    public string? OursContent { get; init; }

    public string? TheirsContent { get; init; }
}
```

Conflict kinds may include:

```text
ContentConflict
DeleteModifyConflict
RenameConflict
RenameRenameConflict
AddAddConflict
```

The initial implementation may support a smaller subset and expand later.

---

# 13. Conflict markers

Luban may support serialization of unresolved conflicts using conventional markers:

```text
<<<<<<< ours
...
=======
...
>>>>>>> theirs
```

However, conflict markers must not be the primary internal representation.

The structured conflict result is authoritative.

Writing conflict markers into files must be an explicit option.

Default behavior should return the conflict without modifying the target workspace.

---

# 14. File merge

Luban should support:

```text
merge file base ours theirs
```

For text files this performs a three-way textual merge.

For binary files:

- identical versions may merge cleanly;
- one-sided modification may merge cleanly;
- conflicting independent modifications should return a binary conflict.

Luban should not attempt semantic binary merging.

---

# 15. Directory merge

A later or initial extended capability may support:

```text
merge directory base ours theirs
```

This should operate file-by-file and detect structural conflicts including:

```text
modify/modify
delete/modify
rename/modify
rename/rename
add/add
```

Directory merge should return a single structured `MergeResult` containing all clean changes and all conflicts.

No mutation should occur unless the caller explicitly applies the result.

---

# 16. Merge and AI

AI-based conflict resolution is outside the deterministic merge primitive.

A workflow may explicitly delegate unresolved conflicts to an agent.

Example:

```text
result = merge base ours theirs

if result.conflicts.any
    yield needs_judgment(result.conflicts)
```

The agent may then produce a new patch.

That patch must go through the normal Luban validation and authorization pipeline before application.

This keeps the boundary clear:

```text
Luban detects conflict
Agent makes judgment
Luban validates and applies resulting change
```

---

# 17. Authorization integration

Diff and merge should expose affected resources before mutation.

This allows Hufu or another authority layer to determine whether the operation is permitted.

For example:

```text
patch modifies:
    src/Auth/Login.cs
    src/Auth/Token.cs
```

Authorization can evaluate those concrete resources before `apply`.

Read-only diff operations should require only observation rights.

Applying changes should require mutation rights over every affected resource.

Example effect distinction:

```text
workspace.diff
workspace.merge.compute
workspace.patch.validate
workspace.patch.apply
```

Computing a merge is not itself workspace mutation.

Applying the merge is.

---

# 18. Evidence and audit integration

Operations should expose enough structured information for external systems to record:

- source hashes;
- target hashes;
- affected paths;
- diff summary;
- patch identity;
- merge base identity;
- conflict details;
- resulting content hashes.

Luban itself does not need to own the evidence ledger.

Hongxian or another evidence component should be able to record the operation deterministically.

---

# 19. Hashes and stale-state protection

Patch and merge operations should optionally include source hashes.

Example:

```text
expected SHA-256:
7e9...
```

Before mutation Luban can verify that the source still matches the state used to generate the change.

If it does not:

```text
PatchStale
```

should be returned rather than silently applying against unexpected content.

This is particularly important for long-running agent workflows.

---

# 20. Serialization formats

The internal Luban structures should be independent of serialization format.

Initial useful serializers:

```text
Luban structured representation
Unified diff
Git-style extended unified diff
JSON
```

Git-style serialization does not imply Git integration.

For example:

```text
Diff
  -> UnifiedDiffSerializer
```

and:

```text
UnifiedDiffParser
  -> Patch
```

should work without a repository.

---

# 21. Initial API shape

An approximate API could look like:

```csharp
public interface IDiffEngine
{
    Diff Diff(
        string before,
        string after,
        DiffOptions? options = null);

    Task<Diff> DiffFilesAsync(
        string beforePath,
        string afterPath,
        DiffOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

Merge:

```csharp
public interface IMergeEngine
{
    MergeResult<string> Merge(
        string @base,
        string ours,
        string theirs,
        MergeOptions? options = null);

    Task<MergeResult<FileContent>> MergeFilesAsync(
        string basePath,
        string oursPath,
        string theirsPath,
        MergeOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

Patch:

```csharp
public interface IPatchEngine
{
    PatchValidationResult Validate(
        Patch patch,
        IWorkspace workspace);

    Task<PatchApplyResult> ApplyAsync(
        Patch patch,
        IWorkspace workspace,
        CancellationToken cancellationToken = default);
}
```

Exact interfaces may evolve with Luban's existing architecture.

---

# 22. Safety requirements

Luban must:

- normalize and validate paths before access;
- prevent accidental traversal outside the authorized workspace;
- reject unsupported or malformed patches;
- distinguish text from binary content;
- validate expected source state before mutation where available;
- avoid partial application by default;
- return structured conflicts rather than silently choosing a side.

For multi-file patches, atomic application should be preferred where the underlying workspace supports it.

If atomicity is unavailable, the result must clearly identify partial application.

---

# 23. Non-goals

The initial implementation is not intended to:

- replace Git;
- implement Git repositories or object storage;
- manage commits or branches;
- perform remote operations;
- automatically resolve semantic conflicts with AI;
- support every historical diff or patch format;
- perform arbitrary binary delta generation;
- understand programming-language semantics.

Language-aware or AST-aware diff and merge may be added later as alternative strategies.

---

# 24. Initial implementation scope

The first useful version should include:

1. line-based text diff;
2. file diff;
3. structured `Diff` representation;
4. unified diff serialization;
5. unified/Git-style diff parsing into `Patch`;
6. patch validation;
7. patch application;
8. deterministic three-way text merge;
9. structured conflict reporting;
10. file-level merge;
11. source hashes for stale-state detection.

Directory diff and merge can follow once the file-level model is stable.

---

# 25. Future extensions

Potential future capabilities include:

- directory snapshots;
- rename detection;
- patience and histogram diff;
- syntax-aware diff;
- AST-aware merge;
- semantic conflict classification;
- structured JSON/YAML/XML merge;
- workflow diff and merge;
- Fuwen plan diff and merge;
- visual diff;
- policy-aware patch filtering;
- model-assisted conflict resolution as an explicitly separate operation.

The same primitives could eventually support workflow evolution:

```text
diff workflow.v12 workflow.v13
```

and:

```text
merge workflow.base workflow.agentA workflow.agentB
```

without changing the fundamental Luban model.

---

# 26. Architectural boundary

The intended layering is:

```text
                Luban
                  |
        +---------+---------+
        |                   |
       Diff               Merge
        |                   |
        +---------+---------+
                  |
                Patch
                  |
          Validate / Apply
                  |
              Workspace
```

Future Git support sits alongside this:

```text
Git CLI / Git API
        |
        v
Git semantic layer
        |
        +---- produces/consumes ----> Diff / Patch
```

Git must therefore depend on Luban's change primitives where useful.

Luban's diff and merge subsystem must never depend on Git.

---

# 27. Core rule

The central rule is:

> Luban represents changes structurally, validates them before mutation, and treats unresolved conflicts as explicit data rather than hidden decisions.

This provides a deterministic change foundation that agents, workflows, Hufu authorization, Hongxian evidence, and future Git integration can all build on.