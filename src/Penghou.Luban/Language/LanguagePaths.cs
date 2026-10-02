using Penghou.IO.Abstractions;

namespace Penghou.Luban.Language;

/// <summary>Canonicalizes source paths within the host-selected Windows workspace.</summary>
public static class LanguagePaths
{
    private const int MaxPathLiteralLength = 8192;

    public static string Normalize(string path, bool allowRoot = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > MaxPathLiteralLength)
            throw new ArgumentException("Workspace path exceeds the language literal limit.", nameof(path));
        var value = path.Replace('\\', '/');
        if (value == ".") value = "";
        else if (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return WindowsWorkspacePath.Normalize(new WorkspacePath(value), allowRoot).Value;
    }
}
