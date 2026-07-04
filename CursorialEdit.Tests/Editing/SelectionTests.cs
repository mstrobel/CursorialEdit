using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M1.WP8 — the document-level selection (spec §3.2): Shift+motion extension, Ctrl+A,
/// collapse-on-plain-motion, and the mouse path (click / capture-drag / double-click word /
/// triple-click block), with <b>cell-level assertions</b> that presenters paint the theme's
/// selection fill over exactly the selected clusters — and only re-raster when their
/// intersection actually changed (architecture §2.3). The WP9 gate test
/// <c>SelectionTests.Copy_YieldsExactSourceRange</c> is deliberately absent here (clipboard is
/// M1.WP9's package).
/// </summary>
public sealed class SelectionTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── keyboard extension ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ShiftRight_ExtendsSelection_PaintedWithThemeSelectionFill(string preset)
    {
        using var harness = EditingHarness.Create("hello world", preset);

        for (var i = 0; i < 5; i++)
            harness.Key(Key.RightArrow, KeyModifiers.Shift);

        harness.AssertCaret(0, 5, anchor: new TextPosition(0, 0)); // anchor fixed, active end moved
        harness.AssertSelectionPainted(row: 0, fromColumn: 0, toColumn: 5, plainColumn: 7);
    }

    [Fact]
    public void PlainMotion_CollapsesTheSelection_AndClearsThePaint()
    {
        using var harness = EditingHarness.Create("hello world");

        for (var i = 0; i < 5; i++)
            harness.Key(Key.RightArrow, KeyModifiers.Shift);
        var selectedFill = harness.BackgroundAt(0, 0);

        harness.Key(Key.RightArrow); // plain motion collapses (TextBox parity: from the active end)
        harness.AssertCaret(0, 6);

        Assert.NotEqual(selectedFill, harness.BackgroundAt(0, 0)); // the fill is gone
        Assert.Equal(harness.BackgroundAt(7, 0), harness.BackgroundAt(0, 0));
    }

    [Fact]
    public void ShiftCtrlHomeEnd_ExtendToTheDocumentEnds_KeepingTheAnchor()
    {
        using var harness = EditingHarness.Create("ab\ncd");

        harness.Click(1, 0);
        harness.Key(Key.End, KeyModifiers.Control | KeyModifiers.Shift);
        harness.AssertCaret(1, 2, anchor: new TextPosition(0, 1));

        harness.Key(Key.Home, KeyModifiers.Control | KeyModifiers.Shift);
        harness.AssertCaret(0, 0, anchor: new TextPosition(0, 1)); // same anchor, flipped direction
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void CtrlA_SelectsTheWholeDocument_AcrossBlocks(string preset)
    {
        using var harness = EditingHarness.Create("alpha\n\nbravo", preset);

        harness.Chord('a', KeyModifiers.Control);
        harness.AssertCaret(2, 5, anchor: new TextPosition(0, 0));

        // Both paragraphs paint the fill (the blank separator row has no cells to paint).
        harness.AssertSelectionPainted(row: 0, fromColumn: 0, toColumn: 5, plainColumn: 7);
        harness.AssertSelectionPainted(row: 2, fromColumn: 0, toColumn: 5, plainColumn: 7);
        Assert.Equal(harness.BackgroundAt(0, 0), harness.BackgroundAt(0, 2));
    }

    // ───────────────────────────── mouse ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void MouseClick_PositionsTheCaret_RoundingToTheNearerClusterEdge(string preset)
    {
        using var harness = EditingHarness.Create("汉字 ab", preset);

        harness.Click(2, 0); // the boundary cell after 汉
        harness.AssertCaret(0, 1);
        Assert.Equal((2, 0), harness.Cursor);

        harness.Click(1, 0); // the right half of 汉: equidistant → the earlier boundary (TextBox tie rule)
        harness.AssertCaret(0, 0);
        Assert.Equal((0, 0), harness.Cursor);

        harness.Click(5, 0); // 'a'
        harness.AssertCaret(0, 3);
        Assert.Equal((5, 0), harness.Cursor);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void MouseDrag_ExtendsTheSelection_WithCapture(string preset)
    {
        using var harness = EditingHarness.Create("hello world", preset);

        harness.Drag(0, 0, 5, 0);

        harness.AssertCaret(0, 5, anchor: new TextPosition(0, 0));
        harness.AssertSelectionPainted(row: 0, fromColumn: 0, toColumn: 5, plainColumn: 7);
    }

    [Fact]
    public void MouseDrag_AcrossLines_SelectsTheSpannedRange()
    {
        using var harness = EditingHarness.Create("alpha\nbravo");

        harness.Drag(2, 0, 3, 1);

        harness.AssertCaret(1, 3, anchor: new TextPosition(0, 2));
        harness.AssertSelectionPainted(row: 0, fromColumn: 2, toColumn: 5, plainColumn: 0);
        harness.AssertSelectionPainted(row: 1, fromColumn: 0, toColumn: 3, plainColumn: 4);
    }

    [Fact]
    public void DoubleClick_SelectsTheWord()
    {
        using var harness = EditingHarness.Create("foo bar baz");

        harness.Click(5, 0, clickCount: 2); // inside "bar"

        harness.AssertCaret(0, 7, anchor: new TextPosition(0, 4));
        harness.AssertSelectionPainted(row: 0, fromColumn: 4, toColumn: 7, plainColumn: 0);
    }

    [Fact]
    public void TripleClick_SelectsTheBlock()
    {
        using var harness = EditingHarness.Create("alpha beta\n\ngamma");

        harness.Click(2, 0, clickCount: 3); // block 0 = the first paragraph + its trailing blank line

        harness.AssertCaret(1, 0, anchor: new TextPosition(0, 0));
        harness.AssertSelectionPainted(row: 0, fromColumn: 0, toColumn: 10, plainColumn: 12);

        // The second paragraph is untouched.
        Assert.Equal(harness.BackgroundAt(12, 0), harness.BackgroundAt(0, 2));
    }

    // ───────────────────────────── raster economics ─────────────────────────────

    [Fact]
    public void SelectionChange_InvalidatesOnlyPresentersWhoseIntersectionChanged()
    {
        using var harness = EditingHarness.Create("alpha\n\nbravo\n\ncharlie");

        PlainTextPresenter PresenterOf(int blockIndex)
            => harness.Bridge.GetPresenter(harness.Bridge.Blocks[blockIndex].Id)!;

        harness.Key(Key.RightArrow, KeyModifiers.Shift); // selection [0,1) — inside block 0
        int block1Renders = PresenterOf(1).RenderCount;
        int block2Renders = PresenterOf(2).RenderCount;

        harness.Key(Key.RightArrow, KeyModifiers.Shift); // grows within block 0 only

        Assert.Equal(block1Renders, PresenterOf(1).RenderCount); // untouched siblings: zero re-raster
        Assert.Equal(block2Renders, PresenterOf(2).RenderCount);

        harness.Key(Key.End, KeyModifiers.Control | KeyModifiers.Shift); // now they gain selection
        harness.Settle();
        Assert.True(PresenterOf(1).RenderCount > block1Renders);
        Assert.True(PresenterOf(2).RenderCount > block2Renders);
    }

    // ───────────────────────────── focus ─────────────────────────────

    [Fact]
    public void FocusLoss_ClearsTheTerminalCaret_SelectionStaysPainted()
    {
        using var harness = EditingHarness.Create("hello world");

        for (var i = 0; i < 5; i++)
            harness.Key(Key.RightArrow, KeyModifiers.Shift);
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        var fill = harness.BackgroundAt(0, 0);

        harness.Host.Application.FocusManager.ClearFocus();
        harness.Settle();

        Assert.False(harness.Host.FrameBuffer.CursorVisible); // Clear on focus loss
        Assert.Equal(fill, harness.BackgroundAt(0, 0)); // the selection itself survives
    }
}
