using Cursorial.Input;

using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M2 reveal-wrap (architecture Decision 9, revised 2026-07-05): a <b>prose</b> block's active line wraps
/// in place with its marks revealed — so the surrounding paragraph context stays visible while editing and
/// the block reflows — while code/raw/table cells keep the horizontal <b>slide</b>. Gated by "wrap while
/// editing" (default on); the per-kind rule (code always slides) sits on top.
/// </summary>
public sealed class WrapRevealTests
{
    // A long one-line paragraph that wraps to several rows at width 24, with a **bold** span.
    private const string LongParagraph = "alpha bravo charlie delta **echo** foxtrot golf hotel india juliet";

    private static bool AnyRowContains(MarkdownEditingHarness h, string needle)
    {
        for (var r = 0; r < h.Host.FrameBuffer.Rows; r++)
            if (h.RowTrimmed(r).Contains(needle))
                return true;
        return false;
    }

    [Fact]
    public void WrapReveal_LongActiveParagraph_WrapsInPlace_ShowingContext_AndRevealsMarks()
    {
        using var h = MarkdownEditingHarness.Create(LongParagraph, columns: 24, rows: 12); // caret at origin → active

        // The paragraph wraps across rows (not collapsed to one slid row): a word near the END is visible…
        Assert.True(AnyRowContains(h, "juliet"), "the tail of the paragraph should be visible (wrapped), not slid off-screen");
        // …and the active line's marks are revealed literally in place.
        Assert.True(AnyRowContains(h, "**echo**"), "the active prose line reveals its marks in place");
        // Multiple rows carry content (wrapped), unlike the one-slid-row slide layout.
        int rowsWithText = Enumerable.Range(0, h.Host.FrameBuffer.Rows).Count(r => h.RowTrimmed(r).Length > 0);
        Assert.True(rowsWithText >= 3, $"expected the paragraph to occupy multiple wrapped rows, got {rowsWithText}");
    }

    [Fact]
    public void EditWrapOff_LongActiveParagraph_SlidesToOneRow_TheOldBehavior()
    {
        using var h = MarkdownEditingHarness.Create(LongParagraph, columns: 24, rows: 12);
        h.Bridge.EditWrapEnabled = false;
        h.Settle();

        // Slide reveal: the active line collapses to ONE row (a horizontal window); the tail is slid off.
        Assert.False(AnyRowContains(h, "juliet"), "in slide mode the paragraph tail is off-screen (one slid row)");
        int rowsWithText = Enumerable.Range(0, h.Host.FrameBuffer.Rows).Count(r => h.RowTrimmed(r).Length > 0);
        Assert.Equal(1, rowsWithText); // one slid row; the reserved wrapped rows are blank
    }

    [Fact]
    public void TogglingEditWrap_SwitchesTheActiveParagraphLive()
    {
        using var h = MarkdownEditingHarness.Create(LongParagraph, columns: 24, rows: 12);
        Assert.True(AnyRowContains(h, "juliet")); // wrap on (default)

        h.Bridge.EditWrapEnabled = false; h.Settle();
        Assert.False(AnyRowContains(h, "juliet")); // → slide

        h.Bridge.EditWrapEnabled = true; h.Settle();
        Assert.True(AnyRowContains(h, "juliet")); // → wrap again
    }

    [Fact]
    public void CodeBlock_AlwaysSlides_RegardlessOfEditWrap()
    {
        // A fenced code block whose body line is far wider than the viewport; the caret lands in the body.
        var code = "```\n" + new string('x', 60) + "END\n```";
        using var h = MarkdownEditingHarness.Create(code, columns: 24, rows: 12);
        Assert.True(h.Bridge.EditWrapEnabled); // on

        h.Key(Key.DownArrow); // caret from fence line 0 to the wide body line 1
        h.Settle();

        var presenter = h.Presenter(0);
        Assert.IsType<CodeBlockPresenter>(presenter);
        Assert.False(presenter.RevealWraps); // code keeps the slide even with edit-wrap on (per-kind rule)
    }

    [Fact]
    public void WrapReveal_OnlyTheActiveBlockReflows_BlockAboveIsUnmoved()
    {
        // Two paragraphs; the SECOND is long. Put the caret in the second → it wraps-reveals (grows); the
        // first (above) must not move. (Content below the active block may shift — that is the reflow.)
        using var h = MarkdownEditingHarness.Create("first para\n\n" + LongParagraph, columns: 24, rows: 12);
        Assert.Equal("first para", h.RowTrimmed(0)); // block 0 at row 0

        h.Caret.MoveDocumentEnd(extend: false); // caret into the long second paragraph
        h.Settle();

        Assert.Equal("first para", h.RowTrimmed(0)); // the block above the active one did not move
        Assert.True(AnyRowContains(h, "juliet"));     // the active second paragraph wrapped-revealed
    }
}
