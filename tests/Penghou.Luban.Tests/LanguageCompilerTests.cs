using Penghou.IO.Abstractions;
using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class LanguageCompilerTests
{
    private static readonly WorkspaceId Workspace = new("workspace-1");

    [Fact]
    public void Aliases_comments_directive_and_equivalent_paths_share_identity()
    {
        var first = LanguageCompiler.Compile("#!luban1\nread ./Docs/Hello.txt --max-bytes 2048 # note", Workspace);
        var second = LanguageCompiler.Compile("cat docs\\hello.txt --max-bytes 2048", Workspace);
        Assert.True(first.Succeeded, string.Join("; ", first.Diagnostics.Select(x => x.Code)));
        Assert.True(second.Succeeded, string.Join("; ", second.Diagnostics.Select(x => x.Code)));
        Assert.Equal(first.Document!.Identity, second.Document!.Identity);
    }

    [Fact]
    public void Canonical_document_and_node_match_the_frozen_golden_vector()
    {
        // Independently serialized by the version-1 format: one ReadStage("a") with defaults.
        var result = LanguageCompiler.Compile("read a", Workspace);
        Assert.True(result.Succeeded);
        Assert.Equal("670cef2eedcc007f36a42107c0ce0937032464ac670355e19e1919178a070968", result.Document!.Identity);
        Assert.Equal("9fe1cd89cd436d3e549f8b3e6a3cba6d3ab66f748a822348dcaa4b57a532ef05", result.Document.Statements[0][0].Identity);
    }

    [Fact]
    public void Identity_binds_workspace_query_and_execution_bounds()
    {
        var a = LanguageCompiler.Compile("search needle src", Workspace);
        var b = LanguageCompiler.Compile("grep other src", Workspace);
        var c = LanguageCompiler.Compile("search needle src", new WorkspaceId("workspace-2"));
        var d = LanguageCompiler.Compile("search needle src", Workspace,
            new LanguageCompilerOptions(ExecutionLimits: new LanguageExecutionLimits(MaxReadBytes: 1024)));
        Assert.True(a.Succeeded);
        Assert.True(b.Succeeded);
        Assert.True(c.Succeeded);
        Assert.True(d.Succeeded);
        Assert.NotEqual(a.Document!.Identity, b.Document!.Identity);
        Assert.NotEqual(a.Document.Identity, c.Document!.Identity);
        Assert.NotEqual(a.Document.Identity, d.Document!.Identity);
    }

    [Fact]
    public void Standalone_search_defaults_to_workspace_root_but_pipeline_search_uses_input()
    {
        var implicitRoot = LanguageCompiler.Compile("search needle", Workspace);
        var explicitRoot = LanguageCompiler.Compile("grep needle .", Workspace);
        var pipeline = LanguageCompiler.Compile("find src **/*.cs | grep needle", Workspace);
        Assert.True(implicitRoot.Succeeded);
        Assert.True(explicitRoot.Succeeded);
        Assert.Equal(implicitRoot.Document!.Identity, explicitRoot.Document!.Identity);
        Assert.True(pipeline.Succeeded, string.Join("; ", pipeline.Diagnostics.Select(x => x.Code)));
        Assert.Null(((SearchStage)pipeline.Document!.Statements[0][1].Stage).Root);
    }

    [Fact]
    public void Unsupported_versions_and_unit_suffixes_are_rejected()
    {
        var version = LanguageCompiler.Compile("read file", Workspace,
            new LanguageCompilerOptions(Versions: new LanguageVersions(Language: "2")));
        var suffix = LanguageCompiler.Compile("read file --max-bytes 2kb", Workspace);
        Assert.Contains(version.Diagnostics, d => d.Code == "LUBAN_PROFILE_VERSION");
        Assert.Contains(suffix.Diagnostics, d => d.Code == "LUBAN_VALUE");
        Assert.False(version.Succeeded);
        Assert.False(suffix.Succeeded);
    }

    [Theory]
    [InlineData("read file; count")]
    [InlineData("read file && count")]
    [InlineData("read file > out")]
    [InlineData("read file $(whoami)")]
    [InlineData("read file --max-bytes 10kb")]
    public void Shell_syntax_and_unregistered_numeric_units_are_rejected(string source)
    {
        var result = LanguageCompiler.Compile(source, Workspace);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Whole_statement_validation_rejects_invalid_suffix_after_a_valid_read()
    {
        var result = LanguageCompiler.Compile("read src/a.txt | take 1 | find *.cs", Workspace);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code is "LUBAN_FIND_EDGE" or "LUBAN_READ_EDGE" or "LUBAN_TAKE_EDGE");
    }

    [Fact]
    public void Pipeline_requires_file_reference_and_count_is_terminal()
    {
        var wrongEdge = LanguageCompiler.Compile("read src/a.txt | gc", Workspace);
        var terminal = LanguageCompiler.Compile("find src *.cs | count | take 1", Workspace);
        Assert.False(wrongEdge.Succeeded);
        Assert.Contains(wrongEdge.Diagnostics, d => d.Code == "LUBAN_READ_EDGE");
        Assert.False(terminal.Succeeded);
        Assert.Contains(terminal.Diagnostics, d => d.Code == "LUBAN_COUNT_TERMINAL");
    }

    [Fact]
    public void Quoted_shell_looking_values_remain_literal_data()
    {
        var result = LanguageCompiler.Compile("search 'a && b' src", Workspace);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(x => x.Code)));
    }

    [Fact]
    public void Programmatic_ir_is_snapshotted_and_unknown_stage_types_are_rejected()
    {
        IReadOnlyList<LanguageStage>[] stages = [new LanguageStage[] { new ReadStage("./a.txt") }];
        var result = LanguageCompiler.Compile(stages, Workspace);
        stages[0] = [new ReadStage("./changed.txt")];
        Assert.True(result.Succeeded);
        Assert.Equal("a.txt", ((ReadStage)result.Document!.Statements[0][0].Stage).Path);

        var invalid = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[] { new LanguageStage[] { new UnknownStage() } }, Workspace);
        Assert.False(invalid.Succeeded);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "LUBAN_IR_STAGE");
    }

    [Fact]
    public void Oversized_source_and_pre_cancelled_calls_stop_before_parsing()
    {
        var oversized = LanguageCompiler.Compile(new string('a', 65_537), Workspace);
        Assert.Contains(oversized.Diagnostics, d => d.Code == "LUBAN_SOURCE_LIMIT");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LanguageCompiler.Compile("read a", Workspace, cancellationToken: cts.Token));
    }

    [Fact]
    public void Programmatic_statement_and_node_budgets_are_checked_before_enumeration()
    {
        var tooManyStatements = LanguageCompiler.Compile(new OversizedStatements(), Workspace);
        var tooManyNodes = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[] { new OversizedStages() }, Workspace);
        Assert.Contains(tooManyStatements.Diagnostics, d => d.Code == "LUBAN_STATEMENT_LIMIT");
        Assert.Contains(tooManyNodes.Diagnostics, d => d.Code == "LUBAN_NODE_LIMIT");
    }

    [Fact]
    public void Programmatic_snapshot_uses_only_bounded_indexed_access()
    {
        var indexed = new IndexedStages(new LanguageStage[] { new ReadStage("a") });
        var result = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[] { indexed }, Workspace);
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(x => x.Code)));
        Assert.Equal("a", ((ReadStage)result.Document!.Statements[0][0].Stage).Path);
    }

    [Theory]
    [InlineData("find", "éé")]
    [InlineData("search-include", "éé")]
    [InlineData("search-exclude", "éé")]
    public void Programmatic_normalized_globs_obey_utf8_literal_budget(string kind, string oversizedGlob)
    {
        LanguageStage stage = kind switch
        {
            "find" => new FindStage("", oversizedGlob, new TraversalLimits()),
            "search-include" => new SearchStage("x", "", oversizedGlob, null, new SearchLimits()),
            _ => new SearchStage("x", "", "**", oversizedGlob, new SearchLimits())
        };
        var result = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[] { new[] { stage } }, Workspace,
            new LanguageCompilerOptions(MaxLiteralBytes: 3));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "LUBAN_IR_INVALID");
    }

    [Theory]
    [InlineData("\n", 8)]
    [InlineData("\r\n", 9)]
    [InlineData("\r", 8)]
    public void Diagnostics_keep_offsets_in_original_newline_coordinates(string newline, int expectedOffset)
    {
        var source = "read 😀" + newline + "unknown command";
        var result = LanguageCompiler.Compile(source, Workspace);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "LUBAN_COMMAND");
        Assert.Equal(expectedOffset, diagnostic.Offset);
        Assert.Equal("unknown", source.Substring(diagnostic.Offset, diagnostic.Length));
    }

    [Fact]
    public void Malformed_nullable_ir_fields_return_diagnostics()
    {
        var missingRoot = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[]
            { new LanguageStage[] { new FindStage(null!, "*", new TraversalLimits()) } }, Workspace);
        var missingQuery = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[]
            { new LanguageStage[] { new SearchStage(null!, "src", "**", null, new SearchLimits()) } }, Workspace);
        var missingLimits = LanguageCompiler.Compile(new IReadOnlyList<LanguageStage>[]
            { new LanguageStage[] { new FindStage("src", "*", null!) } }, Workspace);
        Assert.False(missingRoot.Succeeded);
        Assert.False(missingQuery.Succeeded);
        Assert.Contains(missingLimits.Diagnostics, d => d.Code == "LUBAN_IR_LIMITS");
    }

    [Fact]
    public void Piped_search_rejects_source_file_selection_filters()
    {
        var result = LanguageCompiler.Compile("find src **/*.cs | search Authority --include **/*.cs", Workspace);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "LUBAN_PIPE_SEARCH_SELECTION");
    }

    [Fact]
    public void Empty_source_and_programmatic_documents_are_rejected()
    {
        var source = LanguageCompiler.Compile("# only a comment", Workspace);
        var ir = LanguageCompiler.Compile(Array.Empty<IReadOnlyList<LanguageStage>>(), Workspace);
        Assert.Contains(source.Diagnostics, d => d.Code == "LUBAN_EMPTY_DOCUMENT");
        Assert.Contains(ir.Diagnostics, d => d.Code == "LUBAN_EMPTY_DOCUMENT");
    }

    private sealed record UnknownStage : LanguageStage;

    private sealed class OversizedStatements : IReadOnlyList<IReadOnlyList<LanguageStage>>
    {
        public int Count => 65;
        public IReadOnlyList<LanguageStage> this[int index] => throw new InvalidOperationException("Should not be enumerated.");
        public IEnumerator<IReadOnlyList<LanguageStage>> GetEnumerator() => throw new InvalidOperationException("Should not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OversizedStages : IReadOnlyList<LanguageStage>
    {
        public int Count => 129;
        public LanguageStage this[int index] => throw new InvalidOperationException("Should not be enumerated.");
        public IEnumerator<LanguageStage> GetEnumerator() => throw new InvalidOperationException("Should not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IndexedStages(LanguageStage[] items) : IReadOnlyList<LanguageStage>
    {
        public int Count => items.Length;
        public LanguageStage this[int index] => items[index];
        public IEnumerator<LanguageStage> GetEnumerator() => throw new InvalidOperationException("The compiler must use bounded indexed access.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
