using Cursorial.Rendering.Text;

using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.RunMapHarness;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// M2.WP5 gate — the <b>binding</b> caret-visibility invariant (architecture Decision 8 / §2.4). The
/// horizontal slide offset is <i>defined</i> as <see cref="HorizontalSlide.Compute"/>, the function
/// that keeps the caret within the visible span with two cells of edge slack. These tests script the
/// long-line edits the plan names — typing at the line end and in the middle, a find-style jump, and
/// an undo restore — and assert after every step that the caret's published column stays inside
/// <c>[0, viewport)</c>. A caret outside the visible span is a failure, not a UX nit.
/// </summary>
public sealed class CaretVisibilityTests
{
    private const int Viewport = 20;
    private const int Slack = 2; // viewport > 4 ⇒ two-cell edge slack (the TextBox convention)

    /// <summary>The invariant: the caret's published column is inside the visible span.</summary>
    private static void AssertVisible(int caretCell, int slide)
        => Assert.InRange(caretCell - slide, 0, Viewport - 1);

    /// <summary>Recomputes the slide for a caret at <paramref name="caretOffset"/> and asserts visibility; returns the new slide.</summary>
    private static int Follow(RunMap map, int caretOffset, int previousSlide)
    {
        var (row, cell) = map.Locate(caretOffset);
        int slide = HorizontalSlide.Compute(previousSlide, cell, map.RowWidth(row), Viewport);
        AssertVisible(cell, slide);
        return slide;
    }

    [Fact]
    public void TypingAtLineEnd_KeepsCaretVisible_WrapOff()
    {
        var text = string.Empty;
        int slide = 0;
        for (var i = 0; i < 90; i++)
        {
            text += (char) ('a' + (i % 26));
            slide = Follow(Plain(text, wrap: 0, mode: WrapMode.NoWrap), text.Length, slide);
        }

        Assert.True(slide > 0, "a 90-char line in a 20-cell viewport must have slid");
    }

    [Fact]
    public void TypingInTheMiddle_KeepsCaretVisible_WrapOff()
    {
        var text = new string('x', 120);
        int caret = 60;
        int slide = Follow(Plain(text, mode: WrapMode.NoWrap), caret, previousSlide: 0);

        // Insert 40 characters one at a time at the caret; the caret advances with each insertion.
        for (var i = 0; i < 40; i++)
        {
            text = text[..caret] + '#' + text[caret..];
            caret++;
            slide = Follow(Plain(text, mode: WrapMode.NoWrap), caret, slide);
        }
    }

    [Fact]
    public void FindStyleJump_BringsCaretIntoView_FromEitherDirection()
    {
        var map = Plain(new string('a', 200), mode: WrapMode.NoWrap);
        int slide = 0;

        slide = Follow(map, 150, slide); // jump far right
        slide = Follow(map, 5, slide);   // jump back near the start
        slide = Follow(map, 199, slide); // jump to the end
        _ = Follow(map, 0, slide);       // jump home
    }

    [Fact]
    public void UndoRestore_ToAnEarlierCaret_KeepsCaretVisible()
    {
        // Type to the end (slide advanced far right), then "undo" restores the caret to an earlier
        // offset — the slide must follow it back into view.
        var text = new string('q', 100);
        int slide = Follow(Plain(text, mode: WrapMode.NoWrap), text.Length, previousSlide: 0);

        var shorter = new string('q', 80); // as if the tail were undone
        _ = Follow(Plain(shorter, mode: WrapMode.NoWrap), 12, slide); // caret restored near the start
    }

    [Fact]
    public void RevealedActiveLine_SlidesToKeepCaretVisible_InWrapOnMode()
    {
        // The reveal substrate (R1): even with wrap-on, the active line is one natural-width row that
        // slides. Type at its end; the caret stays visible though the line far exceeds the viewport.
        var text = string.Empty;
        int slide = 0;
        for (var i = 0; i < 60; i++)
        {
            text += (char) ('a' + (i % 26));
            var map = Plain(text, wrap: Viewport, mode: WrapMode.WordWrap, active: 0);
            Assert.Equal(1, map.RowCount); // active line never wraps
            slide = Follow(map, text.Length, slide);
        }
    }

    // ───────────────────────────── the two-cell edge slack ─────────────────────────────

    [Fact]
    public void RightwardJump_LeavesTwoCellsOfSlackFromTheRightEdge()
    {
        var map = Plain(new string('a', 200), mode: WrapMode.NoWrap);
        // From a left-anchored window (slide 0), jump the caret to cell 50.
        var (_, cell) = map.Locate(50);
        int slide = HorizontalSlide.Compute(0, cell, map.RowWidth(0), Viewport);

        Assert.Equal(Viewport - 1 - Slack, cell - slide); // caret sits two cells in from the right edge
    }

    [Fact]
    public void LeftwardJump_LeavesTwoCellsOfSlackFromTheLeftEdge()
    {
        var map = Plain(new string('a', 200), mode: WrapMode.NoWrap);
        // Start with a window scrolled to show cell 50 (two cells from the right), then jump left to 30.
        int slide = HorizontalSlide.Compute(0, map.Locate(50).Cell, map.RowWidth(0), Viewport);
        var (_, cell) = map.Locate(30);
        slide = HorizontalSlide.Compute(slide, cell, map.RowWidth(0), Viewport);

        Assert.Equal(Slack, cell - slide); // caret sits two cells in from the left edge
    }

    [Fact]
    public void Slide_NeverExceedsTheRoomForTheEndCaret()
    {
        var map = Plain(new string('a', 40), mode: WrapMode.NoWrap);
        // Caret at the very end: the window shows the end caret at the last column, no further.
        int slide = HorizontalSlide.Compute(0, map.RowWidth(0), map.RowWidth(0), Viewport);
        int published = map.RowWidth(0) - slide;
        Assert.Equal(Viewport - 1, published); // the end caret sits exactly at the last visible column
    }
}
