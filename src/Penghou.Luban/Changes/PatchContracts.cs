using Penghou.IO.Abstractions;

namespace Penghou.Luban.Changes;

/// <summary>Pure UTF-8 byte edit owned by Luban's text-change profile.</summary>
public sealed record TextPatch(int StartOffset, int DeleteLength, ReadOnlyMemory<byte> ReplacementUtf8);

/// <summary>Finite limits for a bounded pure text patch.</summary>
public sealed record PatchLimits(int MaxPatchCount, int MaxReplacementBytes, int MaxOutputBytes);
