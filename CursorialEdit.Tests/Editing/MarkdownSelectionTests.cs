using Cursorial.Input;
using Cursorial.Output;

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

    // ───────────────────────────── WP11b: the inline-code / code-block hole ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void SelectionOverInlineCode_HighlightsTheCodeCell_NoHole(string preset)
    {
        // Two soft-broken lines in one paragraph: line 0 carries inline `code`, the caret sits on line 1 —
        // so line 0 renders INACTIVE (formatted, the code run's opaque code-fill background present) while
        // the selection covers it. Before WP11b the code fill covered the selection scrim on the `a` cell —
        // a grey hole in the highlight. The fix composes the selection into the run's own draw, so the code
        // cell carries the selection fill exactly like the plain cells around it.
        using var harness = MarkdownEditingHarness.Create("`a` one\nsecond", preset);
        harness.AssertCaret(0, 0);

        harness.Key(Key.DownArrow, KeyModifiers.Shift); // extend into line 1 → line 0 inactive + fully selected
        harness.AssertCaret(1, 0, anchor: new TextPosition(0, 0));

        // Formatted line 0 is "a one": the inline-code `a` renders at cell 0, the plain 'o' of "one" at cell 2.
        var codeCell = harness.BackgroundAt(0, 0);  // selected inline-code cell
        var plainCell = harness.BackgroundAt(2, 0); // selected plain cell on the same inactive line

        Assert.Equal(codeCell, plainCell);                     // no hole: the code cell reads as selected, uniform
        Assert.NotEqual(codeCell, harness.BackgroundAt(20, 6)); // and it is actually painted (≠ an empty default cell)
    }

    [Fact]
    public void SelectionOverInlineCode_ReplacesTheCodeFill_NotJustCoincides()
    {
        // The discriminating check on a tier where the code fill and selection fill are distinct colors
        // (KittyTruecolor): a control render with the same inactive line UNSELECTED shows the code fill; the
        // selected render must differ from it (the highlight replaced the fill — the hole is gone).
        using var selected = MarkdownEditingHarness.Create("`a` one\nsecond");
        selected.Key(Key.DownArrow, KeyModifiers.Shift);
        var selectedCodeCell = selected.BackgroundAt(0, 0);

        using var control = MarkdownEditingHarness.Create("`a` one\nsecond");
        control.Key(Key.DownArrow); // caret to line 1, no selection → line 0 inactive, the code cell shows the code fill
        var codeFill = control.BackgroundAt(0, 0);

        Assert.NotEqual(selectedCodeCell, codeFill);
    }

    [Fact]
    public void SelectionOverACodeBlock_HighlightsTheBodyUniformly_NotTheCodeFill()
    {
        // A fenced code block (block 0) followed by a paragraph (block 1); the caret lands in the paragraph,
        // so the code block renders INACTIVE with its code-fill pre-pass. The selection covers the whole
        // block. Its body row must read uniformly selected — and NOT the code fill (the code-block analogue
        // of the inline-code hole). KittyTruecolor: the selection and code fills are distinct colors.
        using var harness = MarkdownEditingHarness.Create("```\nx=1\n```\ntail");
        for (var i = 0; i < 3; i++)
            harness.Key(Key.DownArrow, KeyModifiers.Shift); // caret to line 3 ("tail") → block 0 inactive + selected

        // Block 0 rows: 0 = open fence, 1 = "x=1" body, 2 = close fence. Block 1 "tail" at row 3.
        var b0 = harness.BackgroundAt(0, 1);
        var b1 = harness.BackgroundAt(1, 1);
        var b2 = harness.BackgroundAt(2, 1);
        Assert.Equal(b0, b1);
        Assert.Equal(b1, b2);                                // the body highlights uniformly
        Assert.NotEqual(b0, harness.BackgroundAt(0, 3));     // ≠ the unselected 'tail'

        using var control = MarkdownEditingHarness.Create("```\nx=1\n```\ntail");
        for (var i = 0; i < 3; i++)
            control.Key(Key.DownArrow); // caret to "tail", no selection → block 0 inactive, its body shows the code fill
        Assert.NotEqual(b0, control.BackgroundAt(0, 1));     // the highlight replaced the code fill — no hole
    }

    // ───────────────────────────── WP11b: NoColor selection via Inverse ─────────────────────────────

    [Fact]
    public void NoColorSelection_IsVisible_ViaInverseAttribute()
    {
        // On the NoColor tier a background fill degrades to nothing, so the selection would be invisible. It
        // must fall back to TextAttributes.Inverse composed into the selected cells' own draw (a scrim's
        // Inverse is overwritten by the glyph) — the §18.3 redundant non-color channel.
        using var harness = MarkdownEditingHarness.Create("hello world", CursorialEdit.Tests.TestSupport.CapabilityPresets.NoColor);

        harness.Click(8, 0, clickCount: 2); // double-click selects "world" (cells 6..10)
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6));

        for (var column = 6; column <= 10; column++)
            Assert.True(harness.AttributesAt(column, 0).HasFlag(TextAttributes.Inverse), $"cell {column} should be reverse-video");

        Assert.False(harness.AttributesAt(0, 0).HasFlag(TextAttributes.Inverse)); // 'h' — unselected, not inverted
    }

    [Fact]
    public void NoColorSelectionOverInlineCode_IsVisible_ViaInverse()
    {
        // The NoColor form of the hole: the inline-code cell on an inactive selected line must carry Inverse
        // too (visible), not silently degrade with the collapsed code fill.
        using var harness = MarkdownEditingHarness.Create("`a` one\nsecond", CursorialEdit.Tests.TestSupport.CapabilityPresets.NoColor);
        harness.Key(Key.DownArrow, KeyModifiers.Shift); // line 0 inactive + selected, caret on line 1

        Assert.True(harness.AttributesAt(0, 0).HasFlag(TextAttributes.Inverse));  // the selected inline-code 'a' cell
        Assert.False(harness.AttributesAt(0, 1).HasFlag(TextAttributes.Inverse)); // 's' of "second" — unselected
    }

    // ───────────────────────────── WP11b: wide-cluster whole-cell discipline ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void PartiallySelectedWideCluster_PaintsWhole(string preset)
    {
        // A 2-cell CJK cluster sits mid-line; the selection begins after the first cell and runs to the line
        // end, so the wide cluster falls inside the selected sub-span. Whole-cell discipline (RunMap.Locate
        // rounds to cluster boundaries, and the draw split never halves a cluster) means BOTH of its cells
        // highlight — never a half-painted glyph.
        using var harness = MarkdownEditingHarness.Create("ab汉cd\nsecond", preset);

        harness.Key(Key.RightArrow);                 // caret (0,1) — before 'b'
        harness.Key(Key.DownArrow, KeyModifiers.Shift);   // anchor (0,1), caret (1,1) → line 0 inactive, selected from col 1
        harness.AssertCaret(1, 1, anchor: new TextPosition(0, 1));

        // Cells on line 0: a@0, b@1, 汉@2-3 (wide), c@4, d@5. Selection covers b 汉 c d (cells 1..5).
        var fill = harness.BackgroundAt(1, 0); // 'b', selected
        Assert.Equal(fill, harness.BackgroundAt(2, 0)); // 汉 left half
        Assert.Equal(fill, harness.BackgroundAt(3, 0)); // 汉 right half — the whole cluster, not a hole
        Assert.Equal(fill, harness.BackgroundAt(4, 0)); // 'c'
        Assert.Equal(fill, harness.BackgroundAt(5, 0)); // 'd'
        Assert.NotEqual(fill, harness.BackgroundAt(0, 0)); // 'a' — unselected (before the selection start)
    }
}
