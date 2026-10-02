using System.Text;

namespace Penghou.Luban.Language;

/// <summary>Pure line-window and UTF-8 span helpers over a complete strict-decoded snapshot.</summary>
internal static class TextWindows
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal sealed record Line(int Number, int Start, int ContentEnd, int End)
    {
        internal string Display(string text)
        {
            var end = ContentEnd;
            if (end > Start && text[end - 1] == '\r') end--;
            return text[Start..end];
        }
    }

    internal static IReadOnlyList<Line> Split(string text, CancellationToken ct)
    {
        var lines = new List<Line>();
        var start = 0;
        while (start <= text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var lf = text.IndexOf('\n', start);
            var end = lf < 0 ? text.Length : lf + 1;
            var contentEnd = lf < 0 ? end : lf;
            lines.Add(new(lines.Count + 1, start, contentEnd, end));
            if (lf < 0) break;
            start = end;
        }
        return lines;
    }

    /// <summary>
    /// The source snapshot is always complete. CompleteWindow means the requested
    /// line count was present; EOF may yield a shorter window, while a final LF
    /// contributes the same trailing empty line token as the v1 line scanner.
    /// </summary>
    internal static FileWindowValue ReadWindow(string path, string text, byte[] source, int startLine, int count,
        CancellationToken ct)
    {
        var lines = Split(text, ct);
        var first = Math.Min(startLine - 1, lines.Count);
        var returned = Math.Min(count, lines.Count - first);
        var content = returned == 0 ? string.Empty : text[lines[first].Start..lines[first + returned - 1].End];
        return new(path, content, startLine, count, returned, true, returned == count,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant(), source.Length);
    }

    internal static IReadOnlyList<SearchContextMatchValue> Search(string path, string text, byte[] source,
        string query, int contextLines, int maxMatches, int maxOutputBytes, CancellationToken ct,
        out bool truncated, out LanguageCoverageReason reasons)
    {
        var lines = Split(text, ct);
        var matches = new List<SearchContextMatchValue>();
        var bytes = 0;
        truncated = false;
        reasons = LanguageCoverageReason.None;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
        var absoluteLineByteOffset = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = lines[i];
            var displayed = item.Display(text);
            var at = displayed.IndexOf(query, StringComparison.Ordinal);
            if (at < 0)
            {
                absoluteLineByteOffset = checked(absoluteLineByteOffset + Utf8.GetByteCount(text.AsSpan(item.Start, item.End - item.Start)));
                continue;
            }
            var byteStart = checked(absoluteLineByteOffset + Utf8.GetByteCount(text.AsSpan(item.Start, at)));
            var byteLength = Utf8.GetByteCount(displayed.AsSpan(at, query.Length));
            var before = lines.Skip(Math.Max(0, i - contextLines)).Take(Math.Min(contextLines, i))
                .Select(line => line.Display(text)).ToArray();
            var after = lines.Skip(i + 1).Take(contextLines).Select(line => line.Display(text)).ToArray();
            // Context is complete relative to the full input. At either file edge,
            // fewer surrounding lines are available and the clipped edge is EOF.
            var value = new SearchContextMatchValue(path, item.Number, displayed, byteStart, byteLength,
                Array.AsReadOnly(before), Array.AsReadOnly(after), true, true, hash, source.Length);
            var cost = Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(displayed) +
                before.Sum(Encoding.UTF8.GetByteCount) + after.Sum(Encoding.UTF8.GetByteCount) + 160;
            if (matches.Count >= maxMatches || cost > maxOutputBytes - bytes)
            {
                truncated = true;
                reasons |= matches.Count >= maxMatches ? LanguageCoverageReason.MatchLimit : LanguageCoverageReason.OutputByteLimit;
                break;
            }
            matches.Add(value);
            bytes += cost;
            absoluteLineByteOffset = checked(absoluteLineByteOffset + Utf8.GetByteCount(text.AsSpan(item.Start, item.End - item.Start)));
        }
        return matches.AsReadOnly();
    }
}
