namespace Penghou.IO.Abstractions;

/// <summary>Pure UTF-8 byte edit owned by Luban's text-change profile.</summary>
public sealed record TextPatch(int StartOffset, int DeleteLength, ReadOnlyMemory<byte> ReplacementUtf8);

/// <summary>Finite limits for a bounded pure text patch.</summary>
public sealed record PatchLimits(int MaxPatchCount, int MaxReplacementBytes, int MaxOutputBytes);

/// <summary>Legacy pure patch input retained at the Luban boundary during migration.</summary>
public sealed record FilePatchRequest(HostInvocation Invocation, WorkspaceId Workspace, WorkspacePath Path,
    ResourceVersion ExpectedVersion, IReadOnlyList<TextPatch> Patches, PatchLimits Limits);
