using System.Collections.ObjectModel;
using System.Text;
using Penghou.IO.Abstractions;

namespace Penghou.Luban.Language;

/// <summary>Parses and validates the closed Luban read-only language profile.</summary>
public static class LanguageCompiler
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int HardMaxSourceBytes = 65_536, HardMaxTokens = 4096, HardMaxStatements = 64, HardMaxNodes = 128, HardMaxLiteralBytes = 8192, HardMaxDepth = 16;

    public static LanguageCompilation Compile(string source, WorkspaceId workspace, LanguageCompilerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<LanguageDiagnostic>(); options ??= new LanguageCompilerOptions();
        if (source is null) return Fail("LUBAN_SOURCE_NULL", "Source cannot be null.");
        var versions = options.Versions ?? new LanguageVersions();
        if (!VersionsMatch(versions)) diagnostics.Add(new("LUBAN_PROFILE_VERSION", "Requested language profile versions are not supported."));
        if (!ValidOptions(options, out var optionError)) diagnostics.Add(new("LUBAN_OPTIONS", optionError));
        if (diagnostics.Count > 0) return new(null, diagnostics.AsReadOnly());
        var sourceLimit = Math.Min(options.MaxSourceBytes, HardMaxSourceBytes);
        if (source.Length > sourceLimit)
            return new(null, new[] { new LanguageDiagnostic("LUBAN_SOURCE_LIMIT", "Source exceeds the UTF-8 byte limit.") });
        var byteCount = CountSourceBytes(source, sourceLimit, cancellationToken, out var validUnicode);
        if (!validUnicode) diagnostics.Add(new("LUBAN_SOURCE_UTF8", "Source contains invalid Unicode."));
        else if (byteCount > sourceLimit) diagnostics.Add(new("LUBAN_SOURCE_LIMIT", "Source exceeds the UTF-8 byte limit."));
        if (diagnostics.Count > 0) return new(null, diagnostics.AsReadOnly());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = Parse(source, options, cancellationToken, diagnostics);
            if (diagnostics.Count != 0) return new(null, diagnostics.AsReadOnly());
            return Build(parsed, workspace, versions, options.ExecutionLimits ?? new LanguageExecutionLimits(), diagnostics);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or EncoderFallbackException)
        { diagnostics.Add(new("LUBAN_INVALID_VALUE", "A value is invalid for the selected profile.")); return new(null, diagnostics.AsReadOnly()); }

        LanguageCompilation Fail(string code, string message) => new(null, new[] { new LanguageDiagnostic(code, message) });
    }

    public static LanguageCompilation Compile(IReadOnlyList<IReadOnlyList<LanguageStage>> statements, WorkspaceId workspace,
        LanguageCompilerOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<LanguageDiagnostic>(); options ??= new LanguageCompilerOptions();
        var versions = options.Versions ?? new LanguageVersions(); var limits = options.ExecutionLimits ?? new LanguageExecutionLimits();
        if (!VersionsMatch(versions)) diagnostics.Add(new("LUBAN_PROFILE_VERSION", "Requested language profile versions are not supported."));
        if (!ValidOptions(options, out var err)) diagnostics.Add(new("LUBAN_OPTIONS", err));
        if (statements is null) diagnostics.Add(new("LUBAN_IR_NULL", "Statements cannot be null."));
        if (diagnostics.Count > 0) return new(null, diagnostics.AsReadOnly());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clone = SnapshotAndNormalize(statements!, options, diagnostics, cancellationToken);
            if (diagnostics.Count == 0) return Build(clone, workspace, versions, limits, diagnostics);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or EncoderFallbackException)
        { diagnostics.Add(new("LUBAN_IR_INVALID", "Programmatic typed IR contains an invalid value.")); }
        return new(null, diagnostics.AsReadOnly());
    }

    private static LanguageCompilation Build(IReadOnlyList<IReadOnlyList<LanguageStage>> input, WorkspaceId workspace,
        LanguageVersions versions, LanguageExecutionLimits limits, List<LanguageDiagnostic> diagnostics)
    {
        if (input.Count == 0)
            diagnostics.Add(new("LUBAN_EMPTY_DOCUMENT", "A compiled document must contain at least one statement."));
        if (string.IsNullOrWhiteSpace(workspace.Value) || workspace.Value.Length > 256 || workspace.Value.Any(char.IsControl))
            diagnostics.Add(new("LUBAN_WORKSPACE", "Workspace binding must be a nonempty bounded host identity."));
        TypeCheck(input, limits, versions, diagnostics);
        if (diagnostics.Count > 0) return new(null, diagnostics.AsReadOnly());
        var canonical = input.Select(s => (IReadOnlyList<LanguageStage>)new ReadOnlyCollection<LanguageStage>(s.ToArray())).ToArray();
        var digest = SemanticIdentity.Document(versions, workspace, limits, canonical);
        var compiled = canonical.Select((stages, si) =>
        {
            var output = new CompiledNode[stages.Count];
            LanguageValueKind? current = null;
            for (var ni = 0; ni < stages.Count; ni++)
            {
                if (stages[ni] is not TakeStage and not CountStage) current = OutputOf(stages[ni], versions);
                else if (stages[ni] is CountStage) current = LanguageValueKind.Count;
                output[ni] = new CompiledNode(stages[ni], current!.Value, SemanticIdentity.Node(digest, si, ni), si, ni);
            }
            return (IReadOnlyList<CompiledNode>)new ReadOnlyCollection<CompiledNode>(output);
        }).ToArray();
        return new(new CompiledDocument(workspace, versions with { }, limits with { }, digest, compiled), Array.Empty<LanguageDiagnostic>());
    }

    private static IReadOnlyList<IReadOnlyList<LanguageStage>> SnapshotAndNormalize(IReadOnlyList<IReadOnlyList<LanguageStage>> input,
        LanguageCompilerOptions options, List<LanguageDiagnostic> d, CancellationToken ct)
    {
        var statementLimit = Math.Min(options.MaxStatements, HardMaxStatements);
        var statementCount = input.Count;
        if (statementCount > statementLimit)
        {
            d.Add(new("LUBAN_STATEMENT_LIMIT", "Statement limit exceeded."));
            return Array.Empty<IReadOnlyList<LanguageStage>>();
        }
        var result = new List<IReadOnlyList<LanguageStage>>(); var total = 0;
        var nodeLimit = Math.Min(options.MaxNodes, HardMaxNodes);
        for (var si = 0; si < statementCount; si++)
        {
            ct.ThrowIfCancellationRequested(); var stages = input[si];
            if (stages is null) { d.Add(new("LUBAN_IR_EMPTY", "Statements must contain at least one stage.")); continue; }
            var stageCount = stages.Count;
            if (stageCount == 0) { d.Add(new("LUBAN_IR_EMPTY", "Statements must contain at least one stage.")); continue; }
            if (stageCount > nodeLimit - total)
            {
                d.Add(new("LUBAN_NODE_LIMIT", "Pipeline node limit exceeded."));
                return result.AsReadOnly();
            }
            total += stageCount;
            var cloned = new List<LanguageStage>(stageCount);
            for (var ni = 0; ni < stageCount; ni++)
            {
                ct.ThrowIfCancellationRequested(); var stage = stages[ni];
                switch (stage)
                {
                    case ReadStage r:
                        if (r.Path is not null) { var p = LanguagePaths.Normalize(r.Path); CheckLiteral(p, options); cloned.Add(new ReadStage(p, r.MaxBytes, r.StartLine, r.LineCount)); } else cloned.Add(new ReadStage(null, r.MaxBytes, r.StartLine, r.LineCount)); break;
                    case DiffStage f:
                        if (!IsTextChangeProfile(options.Versions ?? new LanguageVersions())) { d.Add(new("LUBAN_IR_STAGE", "Typed IR contains a stage unavailable in the selected profile.")); break; }
                        var before = LanguagePaths.Normalize(f.BeforePath); var after = LanguagePaths.Normalize(f.AfterPath);
                        CheckLiteral(before, options); CheckLiteral(after, options);
                        cloned.Add(new DiffStage(before, after, (f.Options ?? Penghou.Luban.Changes.TextChangeOptions.Default) with { })); break;
                    case MergeStage m:
                        if (!IsTextChangeProfile(options.Versions ?? new LanguageVersions())) { d.Add(new("LUBAN_IR_STAGE", "Typed IR contains a stage unavailable in the selected profile.")); break; }
                        var basePath = LanguagePaths.Normalize(m.BasePath); var oursPath = LanguagePaths.Normalize(m.OursPath); var theirsPath = LanguagePaths.Normalize(m.TheirsPath);
                        CheckLiteral(basePath, options); CheckLiteral(oursPath, options); CheckLiteral(theirsPath, options);
                        cloned.Add(new MergeStage(basePath, oursPath, theirsPath, (m.Options ?? Penghou.Luban.Changes.TextChangeOptions.Default) with { })); break;
                    case FindStage f:
                        if (f.Limits is null) { d.Add(new("LUBAN_IR_LIMITS", "Find limits cannot be null.")); break; }
                        var findRoot = LanguagePaths.Normalize(f.Root, true); CheckLiteral(findRoot, options);
                        var findPattern = LanguageGlob.Normalize(f.Pattern); CheckLiteral(findPattern, options);
                        cloned.Add(new FindStage(findRoot, findPattern, f.Limits with { })); break;
                    case SearchStage s:
                        if (s.Limits is null) { d.Add(new("LUBAN_IR_LIMITS", "Search limits cannot be null.")); break; }
                        CheckLiteral(s.Query, options);
                        var searchRoot = s.Root is null ? null : LanguagePaths.Normalize(s.Root, true);
                        if (searchRoot is not null) CheckLiteral(searchRoot, options);
                        var include = LanguageGlob.Normalize(s.Include); CheckLiteral(include, options);
                        var exclude = s.Exclude is null ? null : LanguageGlob.Normalize(s.Exclude); if (exclude is not null) CheckLiteral(exclude, options);
                        cloned.Add(new SearchStage(s.Query, searchRoot, include, exclude, s.Limits with { }, s.ContextLines)); break;
                    case TakeStage t: cloned.Add(t with { }); break;
                    case CountStage: cloned.Add(new CountStage()); break;
                    default: d.Add(new("LUBAN_IR_STAGE", "Typed IR contains an unsupported stage type.")); break;
                }
            }
            result.Add(cloned.AsReadOnly());
        }
        return result.AsReadOnly();
    }

    private static void CheckLiteral(string value, LanguageCompilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        var max = Math.Min(options.MaxLiteralBytes, HardMaxLiteralBytes);
        if (value.Length > max || StrictUtf8.GetByteCount(value) > max)
            throw new ArgumentException("Typed value exceeds the literal byte budget.");
    }

    private static void TypeCheck(IReadOnlyList<IReadOnlyList<LanguageStage>> statements, LanguageExecutionLimits limits, LanguageVersions versions, List<LanguageDiagnostic> d)
    {
        var nodes = 0;
        foreach (var stages in statements)
        {
            if (stages.Count == 0) { d.Add(new("LUBAN_EMPTY_STATEMENT", "Statements cannot be empty.")); continue; }
            nodes += stages.Count; LanguageValueKind? current = null; bool countSeen = false;
            for (var i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                if (countSeen) { d.Add(new("LUBAN_COUNT_TERMINAL", "Count must be the final node in a statement.")); continue; }
                switch (s)
                {
                    case ReadStage r:
                        if (r.MaxBytes <= 0 || r.MaxBytes > limits.MaxReadBytes) d.Add(new("LUBAN_READ_LIMIT", "Read byte limit is outside the execution ceiling."));
                        if (r.StartLine.HasValue != r.LineCount.HasValue || r.StartLine is < 1 || r.LineCount is < 1 or > 10_000 || (r.StartLine.HasValue || r.LineCount.HasValue) && !IsTextChangeProfile(versions)) d.Add(new("LUBAN_READ_WINDOW", "Line windows require a valid v2 start-line and line-count pair."));
                        if (i == 0 && r.Path is null) d.Add(new("LUBAN_MISSING_INPUT", "A first-stage read requires an explicit path."));
                        if (i > 0 && (r.Path is not null || current != LanguageValueKind.FileReference)) d.Add(new("LUBAN_READ_EDGE", "Piped read requires FileReference input and no path."));
                        current = r.StartLine.HasValue ? LanguageValueKind.FileWindow : LanguageValueKind.FileContent; break;
                    case DiffStage f:
                        if (!IsTextChangeProfile(versions)) d.Add(new("LUBAN_IR_STAGE", "Text diff requires the selected text-change language profile."));
                        ValidateTextOptions(f.Options, d);
                        if (i != 0) d.Add(new("LUBAN_DIFF_EDGE", "Diff cannot consume pipeline input."));
                        current = LanguageValueKind.TextDiff; break;
                    case MergeStage m:
                        if (!IsTextChangeProfile(versions)) d.Add(new("LUBAN_IR_STAGE", "Text merge requires the selected text-change language profile."));
                        ValidateTextOptions(m.Options, d);
                        if (i != 0) d.Add(new("LUBAN_MERGE_EDGE", "Merge cannot consume pipeline input."));
                        current = LanguageValueKind.TextMerge; break;
                    case FindStage f:
                        CheckTraversal(f.Limits, limits, d);
                        if (i != 0) d.Add(new("LUBAN_FIND_EDGE", "Find cannot consume pipeline input."));
                        current = LanguageValueKind.FileReference; break;
                    case SearchStage q:
                        CheckSearch(q.Limits, limits, d);
                        if (q.ContextLines is < 0 or > 20 || q.ContextLines != 0 && !IsTextChangeProfile(versions)) d.Add(new("LUBAN_SEARCH_CONTEXT", "Search context is available only in v2 and is bounded to 20 lines."));
                        if (string.IsNullOrEmpty(q.Query)) d.Add(new("LUBAN_QUERY_EMPTY", "Search query cannot be empty."));
                        if (i == 0 && q.Root is null) d.Add(new("LUBAN_MISSING_INPUT", "A first-stage search requires an explicit root."));
                        if (i > 0 && (q.Root is not null || current != LanguageValueKind.FileReference)) d.Add(new("LUBAN_SEARCH_EDGE", "Piped search requires FileReference input and no root."));
                        if (i > 0 && (q.Include != "**" || q.Exclude is not null)) d.Add(new("LUBAN_PIPE_SEARCH_SELECTION", "Piped search cannot set include or exclude selectors."));
                        current = IsTextChangeProfile(versions) || q.ContextLines > 0 ? LanguageValueKind.SearchContextMatch : LanguageValueKind.SearchMatch; break;
                    case TakeStage t:
                        if (current is null || current == LanguageValueKind.Count) d.Add(new("LUBAN_TAKE_EDGE", "Take requires a value stream."));
                        if (t.Count < 0 || t.Count > 10_000) d.Add(new("LUBAN_TAKE_RANGE", "Take must be between 0 and 10000."));
                        break;
                    case CountStage:
                        if (current is null || current == LanguageValueKind.Count) d.Add(new("LUBAN_COUNT_EDGE", "Count requires a value stream."));
                        current = LanguageValueKind.Count; countSeen = true; break;
                    default: d.Add(new("LUBAN_IR_STAGE", "Typed IR contains an unsupported stage type.")); break;
                }
            }
        }
        if (nodes > HardMaxNodes) d.Add(new("LUBAN_NODE_LIMIT", "Pipeline node limit exceeded."));
    }

    private static void CheckTraversal(TraversalLimits l, LanguageExecutionLimits e, List<LanguageDiagnostic> d)
    { if (l.MaxDepth < 0 || l.MaxDepth > HardMaxDepth || l.MaxEntries < 1 || l.MaxMatches < 0 || l.MaxOutputBytes < 0 || l.MaxEntries > e.MaxResourceCalls || l.MaxMatches > e.MaxValues || l.MaxOutputBytes > e.MaxOutputBytes) d.Add(new("LUBAN_FIND_LIMIT", "Find limits exceed the execution profile.")); }
    private static void CheckSearch(SearchLimits l, LanguageExecutionLimits e, List<LanguageDiagnostic> d)
    { if (l.MaxDepth < 0 || l.MaxDepth > HardMaxDepth || l.MaxEntries < 1 || l.MaxMatches < 0 || l.MaxOutputBytes < 0 || l.MaxFileBytes < 1 || l.MaxBytesScanned < 1 || l.MaxEntries > e.MaxResourceCalls || l.MaxMatches > e.MaxValues || l.MaxOutputBytes > e.MaxOutputBytes || l.MaxFileBytes > e.MaxReadBytes || l.MaxBytesScanned > 10_485_760 || l.MaxBytesScanned > e.MaxReadBytes) d.Add(new("LUBAN_SEARCH_LIMIT", "Search limits exceed the execution profile.")); }

    private static int CountSourceBytes(string source, int limit, CancellationToken ct, out bool validUnicode)
    {
        var bytes = 0; validUnicode = true;
        for (var i = 0; i < source.Length; i++)
        {
            if ((i & 0x3ff) == 0) ct.ThrowIfCancellationRequested();
            var c = source[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= source.Length || !char.IsLowSurrogate(source[i + 1])) { validUnicode = false; return bytes; }
                i++; bytes += 4;
            }
            else if (char.IsLowSurrogate(c)) { validUnicode = false; return bytes; }
            else bytes += c <= 0x7f ? 1 : c <= 0x7ff ? 2 : 3;
            if (bytes > limit) return limit + 1;
        }
        return bytes;
    }

    private static IReadOnlyList<IReadOnlyList<LanguageStage>> Parse(string source, LanguageCompilerOptions o, CancellationToken ct, List<LanguageDiagnostic> d)
    {
        var all = new List<IReadOnlyList<LanguageStage>>(); var current = new List<List<Token>>(); var tokenCount = 0; var nodeCount = 0; var offset = 0; var lineNo = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var end = offset;
            while (end < source.Length && source[end] is not '\r' and not '\n') end++;
            var line = source[offset..end];
            if (++lineNo > HardMaxSourceBytes) { d.Add(new("LUBAN_SOURCE_LIMIT", "Too many source lines.")); return Array.Empty<IReadOnlyList<LanguageStage>>(); }
            if (lineNo == 1 && line.StartsWith("#!", StringComparison.Ordinal))
            {
                var expected = IsTextChangeProfile(o.Versions ?? new LanguageVersions()) ? "#!luban2" : "#!luban1";
                if (line != "#!luban1" && line != "#!luban2") d.Add(new("LUBAN_DIRECTIVE", "Only an exact supported Luban directive is accepted.", 0, line.Length));
                else if (line != expected) d.Add(new("LUBAN_DIRECTIVE_VERSION", "Directive version does not match the selected profile.", 0, line.Length));
            }
            else
            {
                var tokens = LexLine(line, offset, o, d, ct);
                tokenCount += tokens.Count; if (tokenCount > Math.Min(o.MaxTokens, HardMaxTokens)) { d.Add(new("LUBAN_TOKEN_LIMIT", "Token limit exceeded.")); break; }
                if (tokens.Count > 0)
                {
                    bool continuation = tokens[0].Pipe;
                    if (!continuation && current.Count > 0) Finish();
                    if (current.Count == 0) current.Add(new List<Token>());
                    foreach (var tok in tokens)
                    {
                        if (tok.Pipe)
                        {
                            if (current[^1].Count == 0) { d.Add(new("LUBAN_PIPE_EMPTY", "Pipeline stage is empty.", tok.Offset, tok.Length)); continue; }
                            current.Add(new List<Token>());
                        }
                        else current[^1].Add(tok);
                    }
                }
            }
            if (end == source.Length) break;
            offset = end + (source[end] == '\r' && end + 1 < source.Length && source[end + 1] == '\n' ? 2 : 1);
        }
        if (current.Count > 0) Finish();
        if (all.Count > Math.Min(o.MaxStatements, HardMaxStatements)) d.Add(new("LUBAN_STATEMENT_LIMIT", "Statement limit exceeded."));
        if (nodeCount > Math.Min(o.MaxNodes, HardMaxNodes)) d.Add(new("LUBAN_NODE_LIMIT", "Pipeline node limit exceeded."));
        return all;

        void Finish()
        {
            if (current.Any(x => x.Count == 0)) d.Add(new("LUBAN_PIPE_EMPTY", "Pipeline cannot end with an empty stage."));
            else
            {
                var built = new List<LanguageStage>();
                    var selectedLimits = o.ExecutionLimits ?? new LanguageExecutionLimits();
                    for (var stageIndex = 0; stageIndex < current.Count; stageIndex++)
                {
                    ct.ThrowIfCancellationRequested(); var stage = ParseStage(current[stageIndex], d, selectedLimits, stageIndex > 0, o.Versions ?? new LanguageVersions());
                    if (stage is not null) built.Add(stage);
                }
                if (built.Count == current.Count && built.Count > 0)
                {
                    nodeCount += built.Count;
                    if (nodeCount > Math.Min(o.MaxNodes, HardMaxNodes)) d.Add(new("LUBAN_NODE_LIMIT", "Pipeline node limit exceeded."));
                    all.Add(built.AsReadOnly());
                }
            }
            current = new List<List<Token>>();
        }
    }

    private static List<Token> LexLine(string line, int baseOffset, LanguageCompilerOptions o, List<LanguageDiagnostic> d, CancellationToken ct)
    {
        var output = new List<Token>(); int i = 0;
        while (i < line.Length)
        {
            ct.ThrowIfCancellationRequested(); if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '#') break;
            if (line[i] == '|') { output.Add(new("|", baseOffset + i, 1, true)); i++; continue; }
            int start = i; var b = new StringBuilder(); char quote = '\0'; bool wasQuoted = false;
            if (line[i] is '\'' or '"') { quote = line[i++]; wasQuoted = true; }
            if (quote != '\0')
            {
                bool closed = false;
                while (i < line.Length)
                {
                    ct.ThrowIfCancellationRequested(); char c = line[i++];
                    if (c == quote) { closed = true; break; }
                    if (quote == '"' && c == '\\')
                    {
                        if (i >= line.Length) break; char e = line[i++];
                        b.Append(e switch { '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => BadEscape(e, d, baseOffset + i - 2) });
                    }
                    else b.Append(c);
                }
                if (!closed) d.Add(new("LUBAN_QUOTE", "Quoted value is not closed.", baseOffset + start, line.Length - start));
                if (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '|' && line[i] != '#') d.Add(new("LUBAN_TOKEN_JOIN", "Quoted values must be separated by whitespace.", baseOffset + i, 1));
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '|' && line[i] != '#')
                {
                    ct.ThrowIfCancellationRequested(); char c = line[i];
                    if (c is ';' or '&' or '>' or '<' or '`' || c == '$' && i + 1 < line.Length && line[i + 1] == '(')
                    { d.Add(new("LUBAN_SHELL_TOKEN", "Shell operators and substitution are not supported.", baseOffset + i, c == '$' ? 2 : 1)); i += c == '$' ? 2 : 1; continue; }
                    b.Append(c); i++;
                }
            }
            string value = b.ToString();
            try { if (StrictUtf8.GetByteCount(value) > Math.Min(o.MaxLiteralBytes, HardMaxLiteralBytes)) d.Add(new("LUBAN_LITERAL_LIMIT", "Literal exceeds the UTF-8 byte limit.", baseOffset + start, i - start)); }
            catch (EncoderFallbackException) { d.Add(new("LUBAN_LITERAL_UTF8", "Literal contains invalid Unicode.", baseOffset + start, i - start)); }
            output.Add(new(value, baseOffset + start, i - start, false, wasQuoted));
        }
        return output;
        static char BadEscape(char e, List<LanguageDiagnostic> ds, int pos) { ds.Add(new("LUBAN_ESCAPE", "Unsupported double-quote escape.", pos, 2)); return e; }
    }

    private static LanguageStage? ParseStage(List<Token> ts, List<LanguageDiagnostic> d, LanguageExecutionLimits executionLimits, bool hasPipelineInput, LanguageVersions versions)
    {
        if (ts.Count == 0) return null;
        string cmd = ts[0].Text.ToLowerInvariant(); var pos = new List<Token>(); var opts = new Dictionary<string, Token>(StringComparer.Ordinal);
        var v2 = IsTextChangeProfile(versions);
        var allowed = cmd switch
        {
            "read" or "cat" or "gc" or "files.read" => v2 ? new[] { "max-bytes", "start-line", "line-count" } : new[] { "max-bytes" },
            "find" or "fd" or "files.find" => new[] { "max-depth", "max-entries", "max-matches", "max-output-bytes" },
            "search" or "grep" or "files.search-text" => v2 ? new[] { "include", "exclude", "max-depth", "max-entries", "max-matches", "max-output-bytes", "max-file-bytes", "max-bytes-scanned", "context-lines" } : new[] { "include", "exclude", "max-depth", "max-entries", "max-matches", "max-output-bytes", "max-file-bytes", "max-bytes-scanned" },
            "diff" or "merge" when IsTextChangeProfile(versions) => Array.Empty<string>(),
            "take" or "count" => Array.Empty<string>(), _ => null
        };
        if (allowed is null) { d.Add(new("LUBAN_COMMAND", "Unknown command in the selected profile.", ts[0].Offset, ts[0].Length)); return null; }
        for (int i = 1; i < ts.Count; i++)
        {
            var t = ts[i];
            if (!t.WasQuoted && t.Text.StartsWith("--", StringComparison.Ordinal))
            {
                var name = t.Text[2..]; if (!allowed.Contains(name, StringComparer.Ordinal)) { d.Add(new("LUBAN_OPTION", "Unknown option for this command.", t.Offset, t.Length)); continue; }
                if (opts.ContainsKey(name)) { d.Add(new("LUBAN_OPTION_DUPLICATE", "Option appears more than once.", t.Offset, t.Length)); continue; }
                if (i + 1 >= ts.Count || !ts[i + 1].WasQuoted && ts[i + 1].Text.StartsWith("--", StringComparison.Ordinal)) { d.Add(new("LUBAN_OPTION_VALUE", "Option requires a value.", t.Offset, t.Length)); continue; }
                opts.Add(name, ts[++i]);
            }
            else pos.Add(t);
        }
        try
        {
            switch (cmd)
            {
                case "read": case "cat": case "gc": case "files.read":
                    if (pos.Count > 1) return Arity();
                    var hasStart = opts.ContainsKey("start-line"); var hasCount = opts.ContainsKey("line-count");
                    if (hasStart != hasCount) return Arity();
                    return new ReadStage(pos.Count == 0 ? null : LanguagePaths.Normalize(pos[0].Text), IntOpt(opts, "max-bytes", Math.Min(1_048_576, executionLimits.MaxReadBytes)),
                        hasStart ? IntOpt(opts, "start-line", 1) : null, hasCount ? IntOpt(opts, "line-count", 1) : null);
                case "find": case "fd": case "files.find":
                    if (pos.Count is < 1 or > 2) return Arity();
                    string root = pos.Count == 2 ? LanguagePaths.Normalize(pos[0].Text, true) : "";
                    string pattern = LanguageGlob.Normalize(pos[^1].Text);
                    return new FindStage(root, pattern, new TraversalLimits(IntOpt(opts,"max-depth",16), IntOpt(opts,"max-entries",Math.Min(10_000, executionLimits.MaxResourceCalls)), IntOpt(opts,"max-matches",Math.Min(1000, executionLimits.MaxValues)), IntOpt(opts,"max-output-bytes",Math.Min(262_144, executionLimits.MaxOutputBytes))));
                case "search": case "grep": case "files.search-text":
                    if (pos.Count is < 1 or > 2) return Arity();
                    var query = pos[0].Text; var searchRoot = pos.Count == 2 ? LanguagePaths.Normalize(pos[1].Text, true) : hasPipelineInput ? null : "";
                    var include = opts.TryGetValue("include", out var inc) ? LanguageGlob.Normalize(inc.Text) : "**";
                    var exclude = opts.TryGetValue("exclude", out var exc) ? LanguageGlob.Normalize(exc.Text) : null;
                    return new SearchStage(query, searchRoot, include, exclude, new SearchLimits(IntOpt(opts,"max-depth",16), IntOpt(opts,"max-entries",Math.Min(10_000, executionLimits.MaxResourceCalls)), IntOpt(opts,"max-matches",Math.Min(1000, executionLimits.MaxValues)), IntOpt(opts,"max-output-bytes",Math.Min(262_144, executionLimits.MaxOutputBytes)), IntOpt(opts,"max-file-bytes",Math.Min(1_048_576, executionLimits.MaxReadBytes)), IntOpt(opts,"max-bytes-scanned",Math.Min(10_485_760, executionLimits.MaxReadBytes))), IntOpt(opts, "context-lines", 0));
                case "take": if (pos.Count != 1 || !int.TryParse(pos[0].Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var take)) return Arity(); return new TakeStage(take);
                case "diff": if (pos.Count != 2) return Arity(); return new DiffStage(LanguagePaths.Normalize(pos[0].Text), LanguagePaths.Normalize(pos[1].Text), Penghou.Luban.Changes.TextChangeOptions.Default);
                case "merge": if (pos.Count != 3) return Arity(); return new MergeStage(LanguagePaths.Normalize(pos[0].Text), LanguagePaths.Normalize(pos[1].Text), LanguagePaths.Normalize(pos[2].Text), Penghou.Luban.Changes.TextChangeOptions.Default);
                case "count": if (pos.Count != 0) return Arity(); return new CountStage();
                default: return null;
            }
        }
        catch (ArgumentException) { d.Add(new("LUBAN_VALUE", "Path or glob value is invalid.", ts[0].Offset, ts[0].Length)); return null; }
        LanguageStage? Arity() { d.Add(new("LUBAN_ARITY", "Command arguments do not match the registered signature.", ts[0].Offset, ts[0].Length)); return null; }
    }

    private static int IntOpt(Dictionary<string, Token> opts, string key, int fallback)
    {
        if (!opts.TryGetValue(key, out var t)) return fallback;
        if (!int.TryParse(t.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) throw new ArgumentException();
        return n;
    }
    private static LanguageValueKind OutputOf(LanguageStage s, LanguageVersions versions) => s switch { ReadStage { StartLine: not null } => LanguageValueKind.FileWindow, ReadStage => LanguageValueKind.FileContent, FindStage => LanguageValueKind.FileReference, SearchStage when IsTextChangeProfile(versions) => LanguageValueKind.SearchContextMatch, SearchStage { ContextLines: > 0 } => LanguageValueKind.SearchContextMatch, SearchStage => LanguageValueKind.SearchMatch, DiffStage => LanguageValueKind.TextDiff, MergeStage => LanguageValueKind.TextMerge, TakeStage t => LanguageValueKind.FileReference, CountStage => LanguageValueKind.Count, _ => throw new ArgumentException() };
    private static bool VersionsMatch(LanguageVersions v) => v is not null && (v == new LanguageVersions() || IsTextChangeProfile(v));
    private static bool IsTextChangeProfile(LanguageVersions v) => v == new LanguageVersions(LanguageProfile.TextChangeLanguageVersion,
        LanguageProfile.TextChangeIrVersion, LanguageProfile.TextChangeCatalogueVersion, LanguageProfile.TextChangeProviderProfile);
    private static void ValidateTextOptions(Penghou.Luban.Changes.TextChangeOptions? options, List<LanguageDiagnostic> diagnostics)
    { if (options is null || !options.IsValid) diagnostics.Add(new("LUBAN_TEXT_LIMIT", "Text-change bounds are invalid for the selected profile.")); }
    private static bool ValidOptions(LanguageCompilerOptions o, out string error)
    {
        error = "Compiler bounds and execution limits must be positive and no greater than this frozen profile.";
        if (o.MaxSourceBytes is < 1 or > HardMaxSourceBytes || o.MaxTokens is < 1 or > HardMaxTokens || o.MaxStatements is < 1 or > HardMaxStatements || o.MaxNodes is < 1 or > HardMaxNodes || o.MaxLiteralBytes is < 1 or > HardMaxLiteralBytes) return false;
        var e = o.ExecutionLimits ?? new LanguageExecutionLimits();
        return e.MaxResourceCalls is > 0 and <= 100_000 && e.MaxReadBytes is > 0 and <= 16 * 1024 * 1024 && e.MaxIntermediateBytes is > 0 and <= 4 * 1024 * 1024 && e.MaxOutputBytes is > 0 and <= 4 * 1024 * 1024 && e.MaxValues is > 0 and <= 10_000 && e.TimeoutMilliseconds is > 0 and <= 30_000;
    }
    private sealed record Token(string Text, int Offset, int Length, bool Pipe, bool WasQuoted = false);
}
