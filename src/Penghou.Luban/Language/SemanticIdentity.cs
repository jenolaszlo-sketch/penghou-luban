using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Penghou.Luban.Language;

/// <summary>Stable, versioned semantic identities for compiled Luban documents and nodes.</summary>
internal static class SemanticIdentity
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string DocumentDomain = "Penghou.Luban.SemanticDocument";
    private const string NodeDomain = "Penghou.Luban.SemanticNode";

    public static string Document(LanguageVersions versions, Penghou.IO.Abstractions.WorkspaceId workspace,
        LanguageExecutionLimits limits, IReadOnlyList<IReadOnlyList<LanguageStage>> statements)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Utf8, true);
        Text(w, DocumentDomain); I32(w, 1);
        Text(w, versions.Language); Text(w, versions.Ir); Text(w, versions.Catalogue); Text(w, versions.Provider); Text(w, LanguageProfile.DescriptorVersion);
        Text(w, workspace.Value);
        I32(w, limits.MaxResourceCalls); I32(w, limits.MaxReadBytes); I32(w, limits.MaxIntermediateBytes);
        I32(w, limits.MaxOutputBytes); I32(w, limits.MaxValues); I32(w, limits.TimeoutMilliseconds);
        I32(w, statements.Count);
        for (var si = 0; si < statements.Count; si++)
        {
            var stages = statements[si]; I32(w, si); I32(w, stages.Count);
            for (var ni = 0; ni < stages.Count; ni++)
            {
                I32(w, ni); var s = stages[ni];
                switch (s)
                {
                    case ReadStage r:
                        Tag(w, 1); NullablePath(w, r.Path); I32(w, r.MaxBytes); Edge(w, ni == 0 ? -1 : ni - 1); break;
                    case FindStage f:
                        Tag(w, 2); Path(w, f.Root, true); Glob(w, f.Pattern);
                        I32(w, f.Limits.MaxDepth); I32(w, f.Limits.MaxEntries); I32(w, f.Limits.MaxMatches); I32(w, f.Limits.MaxOutputBytes); Edge(w, -1); break;
                    case SearchStage q:
                        Tag(w, 3); Text(w, q.Query); NullablePath(w, q.Root); Glob(w, q.Include); NullableGlob(w, q.Exclude);
                        I32(w, q.Limits.MaxDepth); I32(w, q.Limits.MaxEntries); I32(w, q.Limits.MaxMatches); I32(w, q.Limits.MaxOutputBytes);
                        I32(w, q.Limits.MaxFileBytes); I32(w, q.Limits.MaxBytesScanned); Edge(w, q.Root is null ? ni - 1 : -1); break;
                    case TakeStage t: Tag(w, 4); I32(w, t.Count); Edge(w, ni - 1); break;
                    case CountStage: Tag(w, 5); Edge(w, ni - 1); break;
                    default: throw new ArgumentException("Unknown language stage.", nameof(statements));
                }
            }
        }
        w.Flush(); return Hex(SHA256.HashData(stream.ToArray()));
    }

    public static string Node(string documentDigest, int statement, int node)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Utf8, true);
        Text(w, NodeDomain); I32(w, 1); Text(w, documentDigest); I32(w, statement); I32(w, node); w.Flush();
        return Hex(SHA256.HashData(stream.ToArray()));
    }

    private static void Tag(BinaryWriter w, byte value) => w.Write(value);
    private static void Edge(BinaryWriter w, int value) => I32(w, value);
    private static void I32(BinaryWriter w, int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, value); w.Write(b); }
    private static void Text(BinaryWriter w, string value) { var b = Utf8.GetBytes(value); I32(w, b.Length); w.Write(b); }
    private static void Path(BinaryWriter w, string value, bool root) => Text(w, FoldAscii(value));
    private static void NullablePath(BinaryWriter w, string? value) { w.Write(value is not null); if (value is not null) Path(w, value, true); }
    private static void Glob(BinaryWriter w, string value) => Text(w, FoldAscii(value));
    private static void NullableGlob(BinaryWriter w, string? value) { w.Write(value is not null); if (value is not null) Glob(w, value); }
    private static string FoldAscii(string value)
    {
        var chars = value.ToCharArray(); for (var i = 0; i < chars.Length; i++) if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
