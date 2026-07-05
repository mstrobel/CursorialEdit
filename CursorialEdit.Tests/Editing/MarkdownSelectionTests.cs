using Cursorial.Input;

using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M2.WP8 — the selection highlight on the markdown presenter surface (the gap the plan flags: the
/// selection was tracked but the <c>LeafBlockPresenter</c>s did not paint it). A document selection is a
/// <b>source range</b>; each presenter intersects it with its block, threads the range through its run
/// map to cells, and paints the selection fill across the selected cells (hidden-mark cells are
/// zero-width and paint nothing; a partially-selected wide cluster paints whole). Asserted against the
/// composited cell backgrounds under both wire presets.
/// </summary>
public sealed class MarkdownSelectionTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    [Theory]
    [MemberData(nameof(Presets))]
    public void SelectingAWord_PaintsTheSelectionFill_OnTheRightCells(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("hello world", preset);

        harness.Click(8, 0, clickCount: 2); // double-click selects "world" (source cols 6..11)
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6));

        // The five "world" cells (6..10) all carry the selection fill; an unselected cell (the 'h' at 0)
        // does not — it carries only the active-block well, a distinct background.
        var fill = harness.BackgroundAt(6, 0);
        for (var column = 7; column <= 10; column++)
            Assert.Equal(fill, harness.BackgroundAt(column, 0));

        Assert.NotEqual(fill, harness.BackgroundAt(0, 0)); // 'h' — unselected
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Selection_IsASourceRange_HighlightSpansTheMarks(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("**bold** rest", preset);

        // Select the SOURCE range `**bold**` (cols 0..8). On the active line the marks are revealed, so
        // all eight source cells render and highlight — the selection is a source range, the rendering is
        // incidental.
        for (var i = 0; i < 8; i++)
            harness.Key(Key.RightArrow, KeyModifiers.Shift);

        var fill = harness.BackgroundAt(0, 0);
        for (var column = 1; column <= 7; column++)
            Assert.Equal(fill, harness.BackgroundAt(column, 0)); // `**bold**` cells all filled

        Assert.NotEqual(fill, harness.BackgroundAt(9, 0)); // 'r' of "rest" — unselected
    }
}
