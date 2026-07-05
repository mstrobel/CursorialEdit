using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7 <see cref="MiniHighlighter"/> gate (§2.1 [DECISION]): the time-boxed built-in highlighter
/// classifies keyword / string / comment / number over the framework's own languages (C#, XAML, JSON,
/// Markdown, shell) and returns <b>no</b> tokens (monochrome) for anything else.
/// </summary>
public sealed class CodeHighlightTests
{
    [Theory]
    [InlineData("csharp", "CSharp")]
    [InlineData("cs", "CSharp")]
    [InlineData("json", "Json")]
    [InlineData("xaml", "Xaml")]
    [InlineData("markdown", "Markdown")]
    [InlineData("sh", "Shell")]
    [InlineData("bash", "Shell")]
    [InlineData("fortran", "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Detect_MapsTheInfoStringToALanguage(string? fenceInfo, string expected)
        => Assert.Equal(expected, MiniHighlighter.Detect(fenceInfo).ToString());

    [Fact]
    public void CSharp_ClassifiesKeywordNumberComment()
    {
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.CSharp, "var x = 1; // hi");

        AssertToken(tokens, "var x = 1; // hi", "var", CodeTokenClass.Keyword);
        AssertToken(tokens, "var x = 1; // hi", "1", CodeTokenClass.Number);
        AssertToken(tokens, "var x = 1; // hi", "// hi", CodeTokenClass.Comment);
    }

    [Fact]
    public void CSharp_ClassifiesStrings()
    {
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.CSharp, "s = \"a b\";");
        AssertToken(tokens, "s = \"a b\";", "\"a b\"", CodeTokenClass.String);
    }

    [Fact]
    public void Json_ClassifiesStringsKeywordsNumbers()
    {
        const string line = "{ \"k\": true, \"n\": 42 }";
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.Json, line);

        AssertToken(tokens, line, "\"k\"", CodeTokenClass.String);
        AssertToken(tokens, line, "true", CodeTokenClass.Keyword);
        AssertToken(tokens, line, "42", CodeTokenClass.Number);
    }

    [Fact]
    public void Shell_ClassifiesKeywordAndComment()
    {
        const string line = "echo hi # a note";
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.Shell, line);

        AssertToken(tokens, line, "echo", CodeTokenClass.Keyword);
        AssertToken(tokens, line, "# a note", CodeTokenClass.Comment);
    }

    [Fact]
    public void Xaml_ClassifiesCommentAndAttributeString()
    {
        const string line = "<Button Text=\"Go\" /> <!-- x -->";
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.Xaml, line);

        AssertToken(tokens, line, "\"Go\"", CodeTokenClass.String);
        AssertToken(tokens, line, "<!-- x -->", CodeTokenClass.Comment);
    }

    [Fact]
    public void CSharp_NumberScan_DoesNotSwallowMemberAccess()
    {
        const string line = "2.ToString()";
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.CSharp, line);

        AssertToken(tokens, line, "2", CodeTokenClass.Number);  // just the "2"
        Assert.DoesNotContain(tokens, t => t.Class == CodeTokenClass.Number && line.Substring(t.Start, t.Length).Contains('.'));
    }

    [Fact]
    public void CSharp_HexAndDecimal_AreWholeNumbers()
    {
        AssertToken(MiniHighlighter.Tokenize(CodeLanguage.CSharp, "x = 0xFF;"), "x = 0xFF;", "0xFF", CodeTokenClass.Number);
        AssertToken(MiniHighlighter.Tokenize(CodeLanguage.CSharp, "y = 3.14;"), "y = 3.14;", "3.14", CodeTokenClass.Number);
    }

    [Fact]
    public void UnknownLanguage_ProducesNoTokens()
        => Assert.Empty(MiniHighlighter.Tokenize(CodeLanguage.Unknown, "var x = 1; // hi"));

    [Fact]
    public void Tokenize_NeverThrows_OnUnterminatedConstructs()
    {
        // Time-boxed line-local scanning must be robust to an unterminated string / block comment.
        _ = MiniHighlighter.Tokenize(CodeLanguage.CSharp, "s = \"unterminated");
        _ = MiniHighlighter.Tokenize(CodeLanguage.CSharp, "/* unterminated");
        _ = MiniHighlighter.Tokenize(CodeLanguage.Xaml, "<!-- unterminated");
    }

    private static void AssertToken(IReadOnlyList<CodeToken> tokens, string line, string text, CodeTokenClass expected)
    {
        Assert.Contains(tokens, t =>
            t.Class == expected && t.Start >= 0 && t.Start + t.Length <= line.Length
            && line.Substring(t.Start, t.Length) == text);
    }
}
