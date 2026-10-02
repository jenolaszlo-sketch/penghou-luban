using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;

[assembly: InternalsVisibleTo("Penghou.Luban.Tests")]

namespace Penghou.Luban.Resolution;

/// <summary>Versioned, domain-separated binary identities for frozen preview data.</summary>
internal static class PreviewIdentity
{
    private const int EncodingVersion = 1;
    private const string DocumentDomain = "Penghou.Luban.PreviewDocument";
    private const string NodeDomain = "Penghou.Luban.PreviewNode";
    private const string PlanDomain = "Penghou.Luban.ResolvedPreviewPlan";
    private const string DirectoryDomain = "Penghou.Luban.AuthorizedDirectoryPage";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Document(WorkspaceId workspace, PreviewLimits limits, IReadOnlyList<PreviewOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var count = operations.Count;
        if (count is < 1 or > 64) throw new InvalidDataException("Preview document node count exceeds its profile.");
        using var w = new CanonicalWriter(limits.MaxPlanBytes);
        w.Text(DocumentDomain); w.I32(EncodingVersion);
        w.Text(PreviewProfile.SchemaVersion); w.Text(PreviewProfile.CatalogueVersion); w.Text(PreviewProfile.ProviderProfile);
        w.I32(LanguageProfile.MaxGlobWork);
        w.Text(workspace.Value); Limits(w, limits); w.I32(count);
        for (var i = 0; i < count; i++)
        {
            w.I32(i);
            switch (operations[i])
            {
                case ExactPatchOperation exact:
                    w.Byte(1); Path(w, exact.Path); NullableVersion(w, exact.ExpectedVersion); Patches(w, exact.Patches); break;
                case SelectedPatchOperation selected:
                    w.Byte(2); Path(w, selected.Root); Glob(w, selected.Pattern); w.I32((int)selected.Selection);
                    Patches(w, selected.Patches); Traversal(w, selected.Limits); break;
                case DeferredToolOperation deferred:
                    w.Byte(3); w.I32((int)deferred.Tool); Path(w, deferred.Root); break;
                default: throw new InvalidDataException("Unregistered preview operation.");
            }
        }
        return Hash(w.ToArray());
    }

    internal static string Node(string documentIdentity, int index)
    {
        using var w = new CanonicalWriter(1024);
        w.Text(NodeDomain); w.I32(EncodingVersion); w.Text(documentIdentity); w.I32(index);
        return Hash(w.ToArray());
    }

    internal static string Plan(EffectInvocation invocation, CompiledPreviewDocument document,
        IReadOnlyList<PreviewObservation> observations, IReadOnlyList<ResolvedPreviewNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(invocation); ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(observations); ArgumentNullException.ThrowIfNull(nodes);
        var observationCount = observations.Count;
        var nodeCount = nodes.Count;
        if (observationCount > document.Limits.MaxResourceCalls || nodeCount > document.Limits.MaxNodes)
            throw new InvalidDataException("Resolved plan exceeds its profile bounds.");
        using var w = new CanonicalWriter(HardPlanBytes);
        WritePlan(w, invocation, document, observations, nodes, observationCount, nodeCount);
        return Hash(w.ToArray());
    }

    internal static long PlanSize(EffectInvocation invocation, CompiledPreviewDocument document,
        IReadOnlyList<PreviewObservation> observations, IReadOnlyList<ResolvedPreviewNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(invocation); ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(observations); ArgumentNullException.ThrowIfNull(nodes);
        var observationCount = observations.Count;
        var nodeCount = nodes.Count;
        if (observationCount > document.Limits.MaxResourceCalls || nodeCount > document.Limits.MaxNodes)
            throw new InvalidDataException("Resolved plan exceeds its profile bounds.");
        using var w = new CountingWriter(HardPlanBytes);
        try { WritePlan(w, invocation, document, observations, nodes, observationCount, nodeCount); }
        catch (PlanSizeExceededException) { return (long)document.Limits.MaxPlanBytes + 1; }
        return w.Length;
    }

    private static void WritePlan(ICanonicalSink w, EffectInvocation invocation, CompiledPreviewDocument document,
        IReadOnlyList<PreviewObservation> observations, IReadOnlyList<ResolvedPreviewNode> nodes, int observationCount, int nodeCount)
    {
        w.Text(PlanDomain); w.I32(EncodingVersion);
        w.Text(PreviewProfile.SchemaVersion); w.Text(PreviewProfile.CatalogueVersion); w.Text(PreviewProfile.ProviderProfile);
        w.I32(LanguageProfile.MaxGlobWork);
        w.Text(document.Identity); w.Text(document.Workspace.Value); Limits(w, document.Limits);
        w.Text(invocation.SubjectId, 256); w.Text(invocation.EffectId, 256); w.Text(invocation.AttemptId, 256);
        w.I32(observationCount);
        for (var i = 0; i < observationCount; i++) Observation(w, observations[i]);
        w.I32(nodeCount);
        for (var i = 0; i < nodeCount; i++) ResolvedNode(w, nodes[i]);
    }

    internal static string Directory(DirectoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(page.Entries);
        var count = page.Entries.Count;
        if (count > 100_000) throw new InvalidDataException("Directory page exceeds the entry bound.");
        using var w = new CanonicalWriter(HardPlanBytes);
        w.Text(DirectoryDomain); w.I32(EncodingVersion); w.Bool(page.IsComplete);
        w.Bool(page.TruncationReason is not null);
        if (page.TruncationReason is { } truncation) w.I32((int)truncation);
        // Continuation is intentionally excluded: it is opaque and may be randomized by a provider.
        w.I32(count);
        for (var i = 0; i < count; i++)
        {
            var entry = page.Entries[i];
            w.Text(entry.Name, 2048); w.Bool(entry.IsDirectory);
            w.Bool(entry.Length is not null); if (entry.Length is { } length) w.I64(length);
            NullableVersion(w, entry.Version);
        }
        return Hash(w.ToArray());
    }

    private const int HardPlanBytes = 4 * 1024 * 1024;

    private static void Limits(ICanonicalSink w, PreviewLimits limits)
    {
        w.I32(limits.MaxNodes); w.I32(limits.MaxTargets); w.I32(limits.MaxReadBytes); w.I32(limits.MaxFileBytes);
        w.I32(limits.MaxReplacementBytes); w.I32(limits.MaxPatchCount); w.I32(limits.MaxPlanBytes);
        w.I32(limits.MaxResourceCalls); w.I32(limits.TimeoutMilliseconds);
    }
    private static void Traversal(ICanonicalSink w, TraversalLimits limits)
    { w.I32(limits.MaxDepth); w.I32(limits.MaxEntries); w.I32(limits.MaxMatches); w.I32(limits.MaxOutputBytes); }
    private static void Path(ICanonicalSink w, string value) => w.Text(FoldAscii(value), 8192);
    private static void Glob(ICanonicalSink w, string value) => w.Text(FoldAscii(value), 256);
    private static void NullableVersion(ICanonicalSink w, ResourceVersion? value)
    {
        w.Bool(value is not null);
        if (value is { } version) w.Text(version.Value, 256);
    }
    private static void Patches(ICanonicalSink w, IReadOnlyList<FrozenTextPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(patches);
        var count = patches.Count;
        if (count is < 1 or > 128) throw new InvalidDataException("Patch list exceeds its profile bound.");
        w.I32(count);
        for (var i = 0; i < count; i++)
        {
            var p = patches[i]; w.I32(p.StartOffset); w.I32(p.DeleteLength);
            w.I32(p.ReplacementUtf8.Length); w.Bytes(p.ReplacementUtf8.Span);
        }
    }
    private static void Observation(ICanonicalSink w, PreviewObservation observation)
    {
        w.Text(observation.NodeIdentity, 64); Path(w, observation.RelativePath); w.I32((int)observation.Action);
        w.Text(observation.RequestIdentity.Value, 256); NullableVersion(w, observation.Version);
        w.Text(observation.Digest, 128); w.I32(observation.ByteLength); w.Bool(observation.IsComplete);
    }
    private static void ResolvedNode(ICanonicalSink w, ResolvedPreviewNode node)
    {
        w.Text(node.NodeIdentity, 64); w.Text(node.Descriptor, 256); w.I32((int)node.State); w.I32((int)node.Selection);
        var proposalCount = node.Proposals.Count;
        if (proposalCount > 1000) throw new InvalidDataException("Proposal count exceeds its bound.");
        w.I32(proposalCount);
        for (var i = 0; i < proposalCount; i++) Proposal(w, node.Proposals[i]);
        var dependencyCount = node.Dependencies.Count;
        if (dependencyCount > 64) throw new InvalidDataException("Dependency count exceeds its bound.");
        w.I32(dependencyCount);
        for (var i = 0; i < dependencyCount; i++) w.Text(node.Dependencies[i], 64);
        w.Bool(node.UnresolvedReason is not null);
        if (node.UnresolvedReason is { } reason) w.I32((int)reason);
    }
    private static void Proposal(ICanonicalSink w, CapturedFilePatch proposal)
    {
        Path(w, proposal.RelativePath); w.Text(proposal.OriginalVersion.Value, 256);
        w.Text(proposal.OriginalSha256, 128); w.I32(proposal.OriginalByteLength);
        w.Text(proposal.ProposedSha256, 128); w.I32(proposal.ProposedByteLength);
        Patches(w, proposal.Patches);
    }
    private static string FoldAscii(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++) if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + ('a' - 'A'));
        return new string(chars);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private interface ICanonicalSink
    {
        void Byte(byte value); void Bool(bool value); void I32(int value); void I64(long value);
        void Text(string value, int maxCharacters = 8192); void Bytes(ReadOnlySpan<byte> data);
    }

    private sealed class CanonicalWriter : ICanonicalSink, IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private readonly int _maxBytes;
        internal CanonicalWriter(int maxBytes) { _maxBytes = maxBytes; _writer = new BinaryWriter(_stream, Utf8, leaveOpen: true); }
        public void Byte(byte value) { Ensure(1); _writer.Write(value); }
        public void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);
        public void I32(int value) { Ensure(4); Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); _writer.Write(b); }
        public void I64(long value) { Ensure(8); Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, value); _writer.Write(b); }
        public void Text(string value, int maxCharacters = 8192)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > maxCharacters) throw new InvalidDataException("Canonical string exceeds its bound.");
            var data = Utf8.GetBytes(value); I32(data.Length); Bytes(data);
        }
        public void Bytes(ReadOnlySpan<byte> data) { Ensure(data.Length); _writer.Write(data); }
        internal byte[] ToArray() { _writer.Flush(); return _stream.ToArray(); }
        private void Ensure(int bytes)
        {
            if (bytes < 0 || _stream.Length > (long)_maxBytes - bytes) throw new InvalidDataException("Canonical encoding exceeds MaxPlanBytes.");
        }
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }

    private sealed class CountingWriter : ICanonicalSink, IDisposable
    {
        private readonly long _maximum;
        internal CountingWriter(long maximum) => _maximum = maximum;
        internal long Length { get; private set; }
        public void Byte(byte value) => Add(1);
        public void Bool(bool value) => Byte(0);
        public void I32(int value) => Add(4);
        public void I64(long value) => Add(8);
        public void Text(string value, int maxCharacters = 8192)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > maxCharacters) throw new InvalidDataException("Canonical string exceeds its bound.");
            Add(4L + Utf8.GetByteCount(value));
        }
        public void Bytes(ReadOnlySpan<byte> data) => Add(data.Length);
        private void Add(long bytes)
        {
            if (bytes < 0 || Length > _maximum - bytes) throw new PlanSizeExceededException();
            Length += bytes;
        }
        public void Dispose() { }
    }

    private sealed class PlanSizeExceededException : Exception;
}
