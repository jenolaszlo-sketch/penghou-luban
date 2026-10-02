using System.Text;
using Penghou.IO.Abstractions;

namespace Penghou.Luban.Changes;

/// <summary>Pure, bounded application of ordered UTF-8 byte-coordinate edits.</summary>
public static class Utf8PatchMaterializer
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Materialize(ReadOnlySpan<byte> original, IReadOnlyList<TextPatch> patches,
        int maximumOutputBytes, CancellationToken cancellationToken = default) =>
        Materialize(original, patches, new PatchLimits(128, 1024 * 1024, maximumOutputBytes), cancellationToken);

    public static byte[] Materialize(ReadOnlySpan<byte> original, IReadOnlyList<TextPatch> patches,
        PatchLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(limits);
        var count = patches.Count;
        if (limits.MaxPatchCount is < 1 or > 128 || limits.MaxReplacementBytes is < 0 or > 1024 * 1024 ||
            limits.MaxOutputBytes < 0 || limits.MaxOutputBytes > 16 * 1024 * 1024 ||
            original.Length > limits.MaxOutputBytes || count is < 1 || count > limits.MaxPatchCount)
            throw new InvalidDataException("Patch bounds are invalid.");
        try { _ = Utf8.GetCharCount(original); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Original bytes are not valid UTF-8.", ex); }

        var frozen = new (int Start, int Delete, byte[] Replacement)[count];
        long resultLength = original.Length;
        long replacementBytes = 0;
        var previousEnd = 0;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = patches[index] ?? throw new InvalidDataException("Patch list contains a null entry.");
            var end = (long)patch.StartOffset + patch.DeleteLength;
            if (patch.StartOffset < previousEnd || patch.StartOffset < 0 || patch.DeleteLength < 0 || end > original.Length ||
                !Boundary(original, patch.StartOffset) || !Boundary(original, (int)end))
                throw new InvalidDataException("Patch offsets must be ordered UTF-8 scalar boundaries.");
            if (patch.ReplacementUtf8.Length > limits.MaxReplacementBytes - replacementBytes)
                throw new InvalidDataException("Replacement bytes exceed their declared bound.");
            replacementBytes += patch.ReplacementUtf8.Length;
            var replacement = patch.ReplacementUtf8.ToArray();
            try { _ = Utf8.GetCharCount(replacement); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Replacement bytes are not valid UTF-8.", ex); }
            resultLength += replacement.Length - (long)patch.DeleteLength;
            frozen[index] = (patch.StartOffset, patch.DeleteLength, replacement);
            previousEnd = (int)end;
        }
        if (replacementBytes > limits.MaxReplacementBytes || resultLength < 0 || resultLength > limits.MaxOutputBytes)
            throw new InvalidDataException("Patch output exceeds its byte bound.");

        var output = new byte[(int)resultLength];
        var sourceOffset = 0;
        var destinationOffset = 0;
        foreach (var patch in frozen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            original.Slice(sourceOffset, patch.Start - sourceOffset).CopyTo(output.AsSpan(destinationOffset));
            destinationOffset += patch.Start - sourceOffset;
            patch.Replacement.CopyTo(output.AsSpan(destinationOffset));
            destinationOffset += patch.Replacement.Length;
            sourceOffset = patch.Start + patch.Delete;
        }
        original[sourceOffset..].CopyTo(output.AsSpan(destinationOffset));
        return output;
    }

    private static bool Boundary(ReadOnlySpan<byte> bytes, int offset) =>
        offset == bytes.Length || offset >= 0 && (bytes[offset] & 0xC0) != 0x80;
}
