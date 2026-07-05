using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;

using static CursorialEdit.Tests.Presenters.PresenterHarness;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7a review fixes for code blocks and the highlighter:
/// (1) a closing fence must match the OPENING fence character, so a `~~~` content line does not close a
/// ` ``` ` block and vanish; (2) indented code renders its indent verbatim so the active/revealed line
/// stays column-aligned with the inactive rows (no caret jump); (3) the string scanner does not apply
/// C-style backslash escapes to XAML, so a value ending in `\` keeps its closing quote.
/// </summary>
public sealed class CodeBlockFixTests
{
    private static int FirstRowContaining(PresenterHarness h, string needle)
    {
        for (var row = 0; row < h.Rows; row++)
            if (h.Row(row).Contains(needle))
                return row;
        return -1;
    }

    [Fact]
    public void MismatchedTildeLine_DoesNotCloseABacktickFence_AndStillRenders()
    {
        // A ```-fence, a content line, then a `~~~` line: `~~~` does not close a backtick fence, so it is
        // ordinary content and must render (the pre-fix bug hid it as a "closing fence").
        var presenter = new CodeBlockPresenter(Lines("```\ncontent\n~~~"), BlockKind.FencedCode);
        var h = Show([presenter]);

        Assert.True(FirstRowContaining(h, "~~~") >= 0, "the ~~~ content line should render, not be hidden as a fence");
        Assert.True(FirstRowContaining(h, "content") >= 0);
    }

    [Fact]
    public void IndentedCode_RendersItsIndent_AndTheActiveLineDoesNotJump()
    {
        var presenter = new CodeBlockPresenter(Lines("    var x = 1;"), BlockKind.IndentedCode);
        var h = Show([presenter]);

        int inactiveCol = h.Row(0).IndexOf("var", StringComparison.Ordinal);
        Assert.Equal(4, inactiveCol); // the 4-space indent is drawn verbatim

        h.SetActive(0, 0); // land the caret on the line
        int activeCol = h.Row(0).IndexOf("var", StringComparison.Ordinal);
        Assert.Equal(inactiveCol, activeCol); // no horizontal jump between inactive and revealed
    }

    [Fact]
    public void XamlStringEndingInBackslash_KeepsItsClosingQuote()
    {
        // <Image Source="C:\Pics\"/>  — the value ends in a backslash; XAML has no C-style escapes, so
        // the closing quote must terminate the string (not be swallowed, running the token to EOF).
        const string line = "<Image Source=\"C:\\Pics\\\"/>";
        var tokens = MiniHighlighter.Tokenize(CodeLanguage.Xaml, line);

        var stringToken = tokens.Single(t => t.Class == CodeTokenClass.String);
        Assert.True(stringToken.Start + stringToken.Length < line.Length,
            "the XAML string must end at its closing quote, not consume the trailing /> to EOF");
    }
}
