using Penghou.Luban.Language;
using Xunit;

namespace Penghou.Luban.Language.Tests;

public sealed class LanguageGlobTests
{
    [Theory]
    [InlineData("./src\\*.cs", "src/*.cs")]
    [InlineData("src/**/x?.cs", "src/**/x?.cs")]
    public void NormalizeCanonicalizesSeparatorsAndLeadingDot(string input, string expected) =>
        Assert.Equal(expected, LanguageGlob.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("./")]
    [InlineData("/root/*.cs")]
    [InlineData("C:\\*.cs")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a/**/b**")]
    [InlineData("a/**x/b")]
    [InlineData("a/{x,y}")]
    [InlineData("a/[ab]")]
    [InlineData("a:b")]
    public void NormalizeRejectsUnsupportedOrUnsafePatterns(string input) =>
        Assert.Throws<ArgumentException>(() => LanguageGlob.Normalize(input));

    [Fact]
    public void NormalizeRejectsPatternsLongerThan256Utf16CodeUnits() =>
        Assert.Throws<ArgumentException>(() => LanguageGlob.Normalize(new string('a', 257)));

    [Fact]
    public void QuestionMarkMatchesOneUnicodeScalar()
    {
        Assert.True(LanguageGlob.IsMatch("?.txt", "😀.txt"));
        Assert.False(LanguageGlob.IsMatch("??.txt", "😀.txt"));
    }

    [Fact]
    public void MatchingUsesAsciiOnlyCaseFolding()
    {
        Assert.True(LanguageGlob.IsMatch("SRC/*.Cs", "src/file.cS"));
        Assert.False(LanguageGlob.IsMatch("ä.txt", "Ä.txt"));
    }

    [Fact]
    public void DoubleStarMatchesZeroOrMoreCompleteSegments()
    {
        Assert.True(LanguageGlob.IsMatch("src/**/x.cs", "src/x.cs"));
        Assert.True(LanguageGlob.IsMatch("src/**/x.cs", "src/a/b/x.cs"));
        Assert.False(LanguageGlob.IsMatch("src/*/x.cs", "src/x.cs"));
        Assert.False(LanguageGlob.IsMatch("src/*/x.cs", "src/a/b/x.cs"));
    }

    [Fact]
    public void CanDescendChecksLiteralAndWildcardPrefixViability()
    {
        Assert.True(LanguageGlob.CanDescend("src/deep/*.cs", "src"));
        Assert.True(LanguageGlob.CanDescend("src/deep/*.cs", "src/deep"));
        Assert.False(LanguageGlob.CanDescend("src/deep/*.cs", "other"));
        Assert.False(LanguageGlob.CanDescend("src/deep/*.cs", "src/other"));
        Assert.False(LanguageGlob.CanDescend("*.cs", "nested"));
        Assert.False(LanguageGlob.CanDescend("src/*.cs", "src/file.cs"));
        Assert.True(LanguageGlob.CanDescend("src/*/x.cs", "src/nested"));
        Assert.False(LanguageGlob.CanDescend("src/*/x.cs", "other/nested"));
    }

    [Fact]
    public void CanDescendTracksGlobstarAcrossZeroAndMultipleSegments()
    {
        Assert.True(LanguageGlob.CanDescend("src/**/x.cs", "src"));
        Assert.True(LanguageGlob.CanDescend("src/**/x.cs", "src/a"));
        Assert.True(LanguageGlob.CanDescend("src/**/x.cs", "src/a/b"));
        Assert.False(LanguageGlob.CanDescend("src/**/x.cs", "other/a"));
        // ** can consume the first x.cs and the trailing x.cs can match below it.
        Assert.True(LanguageGlob.CanDescend("src/**/x.cs", "src/a/x.cs"));
        Assert.False(LanguageGlob.CanDescend("src/*/x.cs", "src/a/x.cs"));
        Assert.True(LanguageGlob.CanDescend("**/*.cs", "nested"));
    }

    [Fact]
    public void LongAdversarialPatternCompletesWithBoundedDynamicProgramming()
    {
        string pattern = string.Join('/', Enumerable.Repeat("*a", 50)) + "z";
        Assert.False(LanguageGlob.IsMatch(pattern, string.Join('/', Enumerable.Repeat("a", 50)) + "y"));
    }

    [Fact]
    public void IsMatchHonorsCancellationBeforeStartingWork()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => LanguageGlob.IsMatch("**/*.cs", "src/Main.cs", source.Token));
    }

    [Fact]
    public void CanDescendHonorsCancellationBeforeStartingWork()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => LanguageGlob.CanDescend("**/*.cs", "src/nested", source.Token));
    }
}
