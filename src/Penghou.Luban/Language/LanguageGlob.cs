using System.Text;
using Penghou.IO.Abstractions;

namespace Penghou.Luban.Language;

/// <summary>Bounded root-relative path glob operations for the language profile.</summary>
public static class LanguageGlob
{
    private const int MaxPatternLength = 256;
    private const int MaxPathLength = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Normalize(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        if (glob.Length == 0 || glob.Length > MaxPatternLength)
            throw new ArgumentException("Glob must contain 1 to 256 UTF-16 code units.", nameof(glob));
        ValidateUnicode(glob, nameof(glob));
        string value = glob.Replace('\\', '/');
        if (value.StartsWith('/') || (value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':'))
            throw new ArgumentException("Glob must be root-relative.", nameof(glob));
        if (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        if (value.Length == 0) throw new ArgumentException("Glob cannot denote the root.", nameof(glob));
        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                throw new ArgumentException("Glob contains an empty or traversal path segment.", nameof(glob));
            foreach (char c in segment)
            {
                if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' )
                    throw new ArgumentException("Glob contains an invalid Windows path character.", nameof(glob));
            }
            if (segment.Contains('{') || segment.Contains('}') || segment.Contains('[') || segment.Contains(']'))
                throw new ArgumentException("Brace and character-class operators are unsupported.", nameof(glob));
            if (segment.Contains("**", StringComparison.Ordinal) && segment != "**")
                throw new ArgumentException("** is supported only as a complete path segment.", nameof(glob));
            // Reject invalid literal names using the same Windows path contract as file effects.
            if (!segment.Contains('*') && !segment.Contains('?'))
                _ = WindowsWorkspacePath.Normalize(new WorkspacePath(segment), false);
        }
        return value;
    }

    public static bool IsMatch(string glob, string path, CancellationToken cancellationToken = default) =>
        IsMatch(glob, path, static _ => { }, cancellationToken);

    internal static bool IsMatch(string glob, string path, Action<int> charge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charge);
        Charge(charge, cancellationToken, InitialLengthWork(glob, MaxPatternLength, path, MaxPathLength));
        string normalized = Normalize(glob);
        string target = NormalizeTarget(path);
        string[] patternSegments = normalized.Split('/');
        string[] pathSegments = target.Split('/');

        // Dynamic programming over path segments; each cell's work is bounded by the
        // scalar matcher below (at most 256 x 2048 operations for a segment pair).
        bool[] previous = new bool[pathSegments.Length + 1];
        bool[] current = new bool[pathSegments.Length + 1];
        previous[0] = true;
        foreach (string segment in patternSegments)
        {
            Charge(charge, cancellationToken, pathSegments.Length + 1);
            Array.Clear(current);
            if (segment == "**")
            {
                current[0] = previous[0];
                for (int j = 1; j <= pathSegments.Length; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current[j] = previous[j] || current[j - 1];
                }
            }
            else
            {
                for (int j = 1; j <= pathSegments.Length; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current[j] = previous[j - 1] && SegmentMatches(segment, pathSegments[j - 1], charge, cancellationToken);
                }
            }
            (previous, current) = (current, previous);
        }
        return previous[pathSegments.Length];
    }

    public static bool CanDescend(string glob, string relativeDirectory, CancellationToken cancellationToken = default) =>
        CanDescend(glob, relativeDirectory, static _ => { }, cancellationToken);

    internal static bool CanDescend(string glob, string relativeDirectory, Action<int> charge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charge);
        Charge(charge, cancellationToken, InitialLengthWork(glob, MaxPatternLength, relativeDirectory, MaxPathLength));
        string normalized = Normalize(glob);
        string directory = NormalizeTarget(relativeDirectory, allowRoot: true);
        string[] segments = normalized.Split('/');
        string[] prefix = directory.Length == 0 ? Array.Empty<string>() : directory.Split('/');
        Charge(charge, cancellationToken, segments.Length + prefix.Length + 1);
        // NFA-style DP over pattern positions. ** has an epsilon edge to its next
        // segment and a consuming edge back to itself; ordinary segments consume
        // exactly one matching directory segment. The arrays are bounded by 257.
        bool[] states = new bool[segments.Length + 1];
        states[0] = true;
        CloseGlobstars(states, segments, charge, cancellationToken);
        foreach (string part in prefix)
        {
            Charge(charge, cancellationToken, segments.Length + 1);
            var next = new bool[segments.Length + 1];
            for (int i = 0; i < segments.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!states[i]) continue;
                if (segments[i] == "**") next[i] = true;
                else if (SegmentMatches(segments[i], part, charge, cancellationToken)) next[i + 1] = true;
            }
            states = next;
            CloseGlobstars(states, segments, charge, cancellationToken);
        }

        // A state before at least one remaining segment can produce a descendant.
        // A state at the end means the directory itself is the complete match.
        Charge(charge, cancellationToken, segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states[i]) return true;
        }
        return false;
    }

    private static void CloseGlobstars(bool[] states, string[] segments, Action<int> charge, CancellationToken cancellationToken)
    {
        Charge(charge, cancellationToken, segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states[i] && segments[i] == "**") states[i + 1] = true;
        }
    }

    private static string NormalizeTarget(string path, bool allowRoot = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > MaxPathLength) throw new ArgumentException("Path exceeds 2048 UTF-16 code units.", nameof(path));
        ValidateUnicode(path, nameof(path));
        string value = path.Replace('\\', '/');
        if (value.StartsWith('/') || (value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':'))
            throw new ArgumentException("Path must be root-relative.", nameof(path));
        if (value.Length == 0 && allowRoot) return value;
        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..") throw new ArgumentException("Path contains an invalid segment.", nameof(path));
            _ = WindowsWorkspacePath.Normalize(new WorkspacePath(segment), false);
        }
        return value;
    }

    private static bool SegmentMatches(string pattern, string value, Action<int> charge, CancellationToken cancellationToken)
    {
        Charge(charge, cancellationToken, checked(pattern.Length + value.Length));
        int[] p = Scalars(pattern), v = Scalars(value);
        bool[] prev = new bool[v.Length + 1], next = new bool[v.Length + 1];
        Charge(charge, cancellationToken, 1);
        prev[0] = true;
        for (int i = 0; i < p.Length; i++)
        {
            Charge(charge, cancellationToken, v.Length + 1);
            Array.Clear(next);
            if (p[i] == '*')
            {
                next[0] = prev[0];
                for (int j = 1; j <= v.Length; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    next[j] = prev[j] || next[j - 1];
                }
            }
            else
            {
                for (int j = 1; j <= v.Length; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    next[j] = prev[j - 1] && (p[i] == '?' || EqualScalar(p[i], v[j - 1]));
                }
            }
            (prev, next) = (next, prev);
        }
        return prev[v.Length];
    }

    private static void Charge(Action<int> charge, CancellationToken cancellationToken, int units)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (units > 0) charge(units);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int InitialLengthWork(string? first, int firstMaximum, string? second, int secondMaximum) =>
        Math.Min(first?.Length ?? 0, firstMaximum) + Math.Min(second?.Length ?? 0, secondMaximum);

    private static bool EqualScalar(int a, int b)
    {
        if (a is >= 'A' and <= 'Z') a += 'a' - 'A';
        if (b is >= 'A' and <= 'Z') b += 'a' - 'A';
        return a == b;
    }

    private static int[] Scalars(string value)
    {
        var output = new List<int>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) output.Add(char.ConvertToUtf32(value[i], value[++i]));
            else output.Add(value[i]);
        }
        return output.ToArray();
    }

    private static void ValidateUnicode(string value, string parameter)
    {
        try { _ = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException ex) { throw new ArgumentException("Text is not valid Unicode scalar data.", parameter, ex); }
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
