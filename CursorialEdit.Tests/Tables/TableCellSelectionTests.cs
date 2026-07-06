using Cursorial.Input;
using Cursorial.Output;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP8 — the rectangular whole-cell selection (spec §5.4 [DECISION]): a selection whose two ends fall in
/// different cells of the same table is interpreted as a RECTANGLE of whole cells (rows [min..max] × columns
/// [min..max]). It highlights every rect cell as a FULL cell (column width incl. padding) on both wire presets;
/// copies as a valid GFM sub-table (delimiter carries the selected columns' alignment; a header-less rect stays
/// valid); Delete/Cut clears every selected cell as ONE undo group; and it is a cell-rect only while BOTH ends
/// stay in the same table (the transition rule). A single-cell selection is unchanged (WP5 in-cell text select).
/// </summary>
public sealed class TableCellSelectionTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // A 2×2 table (both columns clamp to the 3-cell min): dividers at grid x 0/6/12, content at 2/8; the header
    // content is grid row 1 and the body content grid row 3.
    private const string Table2x2 = "| AB | CD |\n|---|---|\n| ef | gh |\n";

    private static int ContentRow(int logicalRow) => 1 + 2 * logicalRow;

    // ───────────────────────────── 1. cross-cell → whole-cell rectangle ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CrossCellSelection_HighlightsWholeCells_IncludingPadding(string preset)
    {
        using var h = MarkdownEditingHarness.Create(Table2x2, preset, columns: 40, rows: 12);

        // Drag from the header's first cell (0,0) to the body's last cell (1,1): anchor and active land in
        // different cells → a 2×2 whole-cell rectangle.
        h.Drag(2, ContentRow(0), 9, ContentRow(1));
        Assert.NotNull(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id)); // a cell-rect is engaged

        // The whole box of every rect cell carries the fill — content AND padding (the WP8 differentiator over the
        // WP5 text highlight, which only filled the covered characters). On the color tier the fill is the
        // SelectionBrush; on NoColor it is the Inverse swap (a selected cell's composited background = the fg).
        // The body row is the clean reference — the header row also fills its code-fill background (so the legacy
        // 16-color tier, which quantizes both to the same palette slot, cannot separate them there).
        var fill = h.BackgroundAt(2, ContentRow(1)); // body cell (1,0) content
        Assert.Equal(fill, h.BackgroundAt(1, ContentRow(1)));  // cell (1,0) LEFT padding — whole cell, not just text
        Assert.Equal(fill, h.BackgroundAt(5, ContentRow(1)));  // cell (1,0) RIGHT padding
        Assert.Equal(fill, h.BackgroundAt(7, ContentRow(1)));  // cell (1,1) left padding
        Assert.Equal(fill, h.BackgroundAt(9, ContentRow(1)));  // cell (1,1) content 'h'
        Assert.Equal(fill, h.BackgroundAt(11, ContentRow(1))); // cell (1,1) right padding

        // The header row's cells are in the rect too — their full boxes carry the SAME selection fill (over the
        // header's own code-fill), padding included.
        Assert.Equal(fill, h.BackgroundAt(2, ContentRow(0)));  // header cell (0,0) content
        Assert.Equal(fill, h.BackgroundAt(1, ContentRow(0)));  // header cell (0,0) padding — whole cell over the header fill
        Assert.Equal(fill, h.BackgroundAt(8, ContentRow(0)));  // header cell (0,1) content

        // Structure is never highlighted: asserted on the body row (its non-cell background is the plain default,
        // so it separates from the fill on every tier — the header row's code-fill would collide on the 16-color wire).
        Assert.NotEqual(fill, h.BackgroundAt(0, ContentRow(1)));  // left border │
        Assert.NotEqual(fill, h.BackgroundAt(6, ContentRow(1)));  // interior divider │ (left as a normal glyph)
        Assert.NotEqual(fill, h.BackgroundAt(12, ContentRow(1))); // right border │
    }

    [Fact]
    public void CrossCellSelection_OnNoColorTier_WholeCellCarriesInverse()
    {
        using var h = MarkdownEditingHarness.Create(Table2x2, TestSupport.CapabilityPresets.NoColor, columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 9, ContentRow(1));

        // The full box rides TextAttributes.Inverse on NoColor — padding cells too, not just the content glyphs.
        Assert.True(h.AttributesAt(1, ContentRow(0)).HasFlag(TextAttributes.Inverse), "cell padding should carry Inverse");
        Assert.True(h.AttributesAt(2, ContentRow(0)).HasFlag(TextAttributes.Inverse), "cell content should carry Inverse");
        Assert.False(h.AttributesAt(0, ContentRow(0)).HasFlag(TextAttributes.Inverse), "the border │ should not be inverted");
        Assert.False(h.AttributesAt(6, ContentRow(0)).HasFlag(TextAttributes.Inverse), "the interior divider │ should not be inverted");
    }

    // ───────────────────────────── 2. a single-cell selection stays a text selection (WP5) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void SingleCellSelection_StaysTextSelect_NotWholeCell(string preset)
    {
        // Selected in a BODY cell so the unselected padding is the plain default background (the header's code-fill
        // would collide with the selection on the 16-color wire).
        using var h = MarkdownEditingHarness.Create("| abcd | ef |\n|---|---|\n| ghij | k |\n", preset, columns: 40, rows: 12);

        h.Click(2, ContentRow(1)); // 'g' of the body cell "ghij"
        h.Key(Key.RightArrow, KeyModifiers.Shift);
        h.Key(Key.RightArrow, KeyModifiers.Shift); // select "gh" — both ends stay in cell (1,0)

        Assert.Null(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id)); // NOT a cell-rect — an in-cell text select

        var fill = h.BackgroundAt(2, ContentRow(1)); // selected 'g'
        Assert.Equal(fill, h.BackgroundAt(3, ContentRow(1)));    // selected 'h'
        Assert.NotEqual(fill, h.BackgroundAt(4, ContentRow(1))); // 'i' is NOT selected (text select stops at the range)
        Assert.NotEqual(fill, h.BackgroundAt(1, ContentRow(1))); // left padding is NOT filled (would be, for a whole cell)
    }

    // ───────────────────────────── 3. copy → GFM sub-table ─────────────────────────────

    [Fact]
    public void CopyCellRect_YieldsValidGfmSubTable_ThatReparsesToTheSelectedCells()
    {
        using var h = MarkdownEditingHarness.Create(Table2x2, columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 9, ContentRow(1)); // the full 2×2 rectangle
        h.Chord('c', KeyModifiers.Control);

        string clip = h.Editor.Clipboard.Text!;
        Assert.Equal("| AB | CD |\n| --- | --- |\n| ef | gh |", clip); // header + synthesized delimiter + body

        // Re-parses to exactly the selected cells.
        using var re = MarkdownEditingHarness.Create(clip, columns: 40, rows: 12);
        var m = Model(re);
        Assert.Equal(2, m.RowCount);
        Assert.Equal(2, m.ColumnCount);
        Assert.Equal("AB", m.CellContent(0, 0));
        Assert.Equal("CD", m.CellContent(0, 1));
        Assert.Equal("ef", m.CellContent(1, 0));
        Assert.Equal("gh", m.CellContent(1, 1));
    }

    [Fact]
    public void CopyCellRect_DelimiterCarriesTheSelectedColumnsAlignment()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|:--|--:|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 10, ContentRow(1)); // col 1 is right-aligned → its '2' sits at the right edge
        h.Chord('c', KeyModifiers.Control);

        string clip = h.Editor.Clipboard.Text!;
        Assert.Equal("| A | B |\n| :--- | ---: |\n| 1 | 2 |", clip); // left/right markers survive

        using var re = MarkdownEditingHarness.Create(clip, columns: 40, rows: 12);
        var m = Model(re);
        Assert.Equal(ColumnAlignment.Left, m.Alignment(0));
        Assert.Equal(ColumnAlignment.Right, m.Alignment(1));
    }

    [Fact]
    public void CopyCellRect_NotIncludingHeader_StillEmitsValidGfm()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 14);

        // Rows 1..2 (both BODY rows) × cols 0..1 — the model's header row 0 is NOT in the rectangle.
        h.Drag(2, ContentRow(1), 8, ContentRow(2));
        h.Chord('c', KeyModifiers.Control);

        string clip = h.Editor.Clipboard.Text!;
        Assert.Equal("| 1 | 2 |\n| --- | --- |\n| 3 | 4 |", clip); // top selected row becomes the sub-table header

        using var re = MarkdownEditingHarness.Create(clip, columns: 40, rows: 12);
        var m = Model(re);
        Assert.Equal(2, m.RowCount);
        Assert.Equal(2, m.ColumnCount);
        Assert.Equal("1", m.CellContent(0, 0)); // the former body row is now the header
        Assert.Equal("4", m.CellContent(1, 1));
    }

    // ───────────────────────────── 4. delete / clear over a rect ─────────────────────────────

    [Fact]
    public void DeleteOverCellRect_ClearsEveryCell_OneUndoGroup_StaysValidGfm()
    {
        using var h = MarkdownEditingHarness.Create(Table2x2, columns: 40, rows: 12);
        string before = h.Buffer.GetText();

        h.Drag(2, ContentRow(0), 9, ContentRow(1)); // the full 2×2 rectangle
        h.Key(Key.Delete);

        var m = Model(h);
        Assert.Equal(2, m.RowCount);    // structure kept
        Assert.Equal(2, m.ColumnCount);
        Assert.Equal("", m.CellContent(0, 0));
        Assert.Equal("", m.CellContent(0, 1));
        Assert.Equal("", m.CellContent(1, 0));
        Assert.Equal("", m.CellContent(1, 1));
        Assert.Equal("|---|---|", h.Buffer.GetLine(1).Text); // delimiter untouched → still valid GFM
        Assert.Equal(0, h.Caret.Position.Line);              // caret lands on the top row (the rect's top-left cell)
        Assert.Equal(1, h.Controller.UndoDepth);             // ONE undo group

        h.Chord('z', KeyModifiers.Control);
        Assert.Equal(before, h.Buffer.GetText()); // one undo restores the whole table
    }

    [Fact]
    public void DeleteOverPartialRect_ClearsOnlySelectedColumns()
    {
        // A rect that covers column 0 of both rows but leaves column 1 alone.
        using var h = MarkdownEditingHarness.Create("| AB | CD |\n|---|---|\n| ef | gh |\n", columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 3, ContentRow(1)); // cells (0,0) and (1,0) only
        h.Key(Key.Delete);

        var m = Model(h);
        Assert.Equal("", m.CellContent(0, 0));
        Assert.Equal("CD", m.CellContent(0, 1)); // column 1 preserved
        Assert.Equal("", m.CellContent(1, 0));
        Assert.Equal("gh", m.CellContent(1, 1));
        Assert.Equal((0, 0), CellOf(h)); // caret in the rect's top-left cell (the table is not all-empty, so it resolves)
    }

    [Fact]
    public void TypingOverCellRect_ClearsRect_TextInTopLeft_CaretStaysOnItsLine()
    {
        using var h = MarkdownEditingHarness.Create(Table2x2, columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 9, ContentRow(1)); // the full 2×2 rectangle
        h.Type("HELLOWORLDXYZ");                     // a payload long enough to push the naive caret target onto line 1

        var m = Model(h);
        Assert.Equal("HELLOWORLDXYZ", m.CellContent(0, 0)); // the typed text lands in the top-left cell
        Assert.Equal("", m.CellContent(0, 1));              // the rest of the rect cleared
        Assert.Equal("", m.CellContent(1, 0));
        Assert.Equal("", m.CellContent(1, 1));
        Assert.Equal(0, h.Caret.Position.Line);            // caret stays on the top-left cell's line (not the delimiter)
        Assert.Equal((0, 0), CellOf(h));
        Assert.False(h.Caret.HasSelection);                // selection collapsed
    }

    [Fact]
    public void PasteOverCellRect_PayloadLongerThanBuffer_DoesNotThrow()
    {
        using var h = MarkdownEditingHarness.Create(Table2x2, columns: 80, rows: 12);

        h.Drag(2, ContentRow(0), 9, ContentRow(1));
        h.Caret.Paste(new string('Z', 50)); // longer than the whole table buffer — a pre-edit GetPosition target would throw
        h.Settle();

        var m = Model(h);
        Assert.Equal(new string('Z', 50), m.CellContent(0, 0));
        Assert.Equal("", m.CellContent(1, 1));
        Assert.Equal(0, h.Caret.Position.Line);
    }

    [Fact]
    public void CellRect_NoColorTruncateReveal_RevealedCellStaysInverse()
    {
        // Truncate mode + NoColor: a rect whose focused (active) end is an over-wide cell reveals its full content;
        // that revealed content must stay Inverse (it draws over the whole-cell Inverse box, not plain).
        using var h = MarkdownEditingHarness.Create(
            "| " + new string('a', 20) + " | B |\n|---|---|\n| c | d |\n",
            TestSupport.CapabilityPresets.NoColor, columns: 16, rows: 12);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        // Anchor in the body cell (1,0), drag the active end UP into the over-wide header cell (0,0): rect rows 0-1,
        // column 0, with (0,0) focused → it reveals full content.
        h.Drag(2, ContentRow(1), 2, ContentRow(0));
        Assert.NotNull(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id));

        Assert.True(h.AttributesAt(2, ContentRow(0)).HasFlag(TextAttributes.Inverse),
            "the revealed over-wide rect cell must stay Inverse on NoColor (not redrawn plain over the box)");
    }

    // ───────────────────────────── review regressions ─────────────────────────────

    [Fact]
    public void CopyCellRect_FromCrlfDocument_UsesCrlfBetweenRows()
    {
        // Review bug 1: SubTableMarkdown defaulted to "\n", so a rect-copy from a CRLF document produced mixed
        // endings. It must carry the document's prevailing ending (verbatim copy is byte-exact; the rect copy too).
        using var h = MarkdownEditingHarness.Create("| AB | CD |\r\n|---|---|\r\n| ef | gh |\r\n", columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 9, ContentRow(1));
        h.Chord('c', KeyModifiers.Control);

        Assert.Equal("| AB | CD |\r\n| --- | --- |\r\n| ef | gh |", h.Editor.Clipboard.Text);
    }

    [Fact]
    public void DeleteOverAllEmptyCellRect_CollapsesTheSelection_NotADeadKey()
    {
        // Review bug 2: an all-empty rect Delete is a NoOp splice, but the key must still COLLAPSE the selection to
        // a caret (like deleting any selection) instead of reading dead and leaving the rect highlighted.
        using var h = MarkdownEditingHarness.Create("| a | b |\n|---|---|\n|  |  |\n", columns: 40, rows: 12);

        h.Drag(2, ContentRow(1), 8, ContentRow(1)); // the two empty body cells (1,0)-(1,1)
        Assert.NotNull(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id));
        Assert.True(h.Caret.HasSelection);

        h.Key(Key.Delete);

        Assert.False(h.Caret.HasSelection);         // collapsed to a caret — not a dead key
        Assert.Equal((1, 0), CellOf(h));            // in the rect's top-left cell
        Assert.Equal("|  |  |", h.Buffer.GetLine(2).Text); // structure unchanged (nothing to clear)
    }

    [Fact]
    public void TypingOverCellRect_EmptyTopLeftCell_PadsLikeAnIntraCellInsert()
    {
        // Review bug 4: typing into an EMPTY top-left cell of a rect must pad to "| X |" (like the WP4 empty-cell
        // insert), not splice unpadded at the bare anchor ("|X  |").
        using var h = MarkdownEditingHarness.Create("|  | b |\n|---|---|\n| c | d |\n", columns: 40, rows: 12);

        h.Drag(2, ContentRow(0), 2, ContentRow(1)); // rect over column 0 (rows 0-1); top-left (0,0) is EMPTY
        h.Type("X");

        Assert.Equal("| X | b |", h.Buffer.GetLine(0).Text); // padded — not "|X  | b |"
        Assert.Equal("|  | d |", h.Buffer.GetLine(2).Text);  // the other rect cell cleared, column 1 preserved
        Assert.Equal("X", Model(h).CellContent(0, 0));
    }

    [Fact]
    public void CellRect_NoColorTruncateReveal_DoesNotPaintNonSelectedNeighbor()
    {
        // Review bug 3: the focused rect cell's revealed content overflowed rightward and painted a NON-selected
        // neighbour column Inverse on NoColor. The Inverse must be clipped to the focused cell's own box.
        using var h = MarkdownEditingHarness.Create(
            "| " + new string('a', 20) + " | b |\n|---|---|\n| c | d |\n",
            TestSupport.CapabilityPresets.NoColor, columns: 16, rows: 12);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        // Rect over column 0 only (rows 0-1); the active (focused) end is the over-wide header cell (0,0), which
        // reveals full content overflowing right into column 1 — which is NOT selected.
        h.Drag(2, ContentRow(1), 2, ContentRow(0));
        Assert.NotNull(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id));

        Assert.True(h.AttributesAt(2, ContentRow(0)).HasFlag(TextAttributes.Inverse),
            "the focused rect cell's own box stays Inverse");
        Assert.False(h.AttributesAt(11, ContentRow(0)).HasFlag(TextAttributes.Inverse),
            "the reveal's overflow must NOT paint the non-selected column-1 neighbour Inverse");
    }

    // ───────────────────────────── 5. the transition rule (selection leaving the table) ─────────────────────────────

    [Fact]
    public void SelectionLeavingTheTable_IsNotACellRect_CopiesVerbatimRange()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n\npara text\n", columns: 40, rows: 16);

        // Anchor inside the table's header cell, active dragged well past the table (clamped onto the paragraph).
        h.Drag(2, ContentRow(0), 0, 20);
        Assert.True(h.Caret.HasSelection);
        Assert.Null(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id)); // NOT a cell-rect — an ordinary selection

        h.Chord('c', KeyModifiers.Control);
        string clip = h.Editor.Clipboard.Text!;
        // It copied the verbatim source range across the boundary — a sub-table would only ever hold the selected
        // table cells, never the paragraph text below, so "para" proves this is the ordinary document selection.
        Assert.Contains("para", clip);
    }

    // ───────────────────────────── 6. cell-rect under the column-window (M3.WP6) ─────────────────────────────

    [Fact]
    public void CellRect_UnderColumnWindow_HighlightsTheOnWindowCells()
    {
        // Four min-width columns → grid width 25; a 16-column viewport engages the presenter-internal column-window.
        using var h = MarkdownEditingHarness.Create("| A | B | C | D |\n|---|---|---|---|\n| 1 | 2 | 3 | 4 |\n", columns: 16, rows: 12);
        Assert.Equal(25, Table(h).GridWidth);
        Assert.True(Table(h).RenderedWidth < 25, "the column-window is engaged (grid wider than the viewport)");

        // Scroll the window right by tabbing to the last column, then select a rect among the now-visible columns.
        h.Click(2, ContentRow(0)); // cell (0,0)
        h.Key(Key.Tab);            // (0,1)
        h.Key(Key.Tab);            // (0,2)
        h.Key(Key.Tab);            // (0,3) → window scrolls right (offset > 0)
        Assert.True(Table(h).WindowOffset > 0, "the window scrolled to follow the caret into the off-screen column");

        // Drag within the scrolled window: window x 2 and x 8 map (through the offset) to the two visible columns.
        h.Drag(2, ContentRow(0), 8, ContentRow(1));
        Assert.NotNull(h.Bridge.SelectionSource!.GetCellRect(h.Blocks[0].Id));

        var fill = h.BackgroundAt(2, ContentRow(0)); // first on-window rect cell (content)
        Assert.Equal(fill, h.BackgroundAt(1, ContentRow(0))); // …its left padding highlights too (whole cell)
        Assert.Equal(fill, h.BackgroundAt(8, ContentRow(0))); // the second on-window rect cell
        Assert.Equal(fill, h.BackgroundAt(8, ContentRow(1))); // …on the body row as well
        Assert.NotEqual(fill, h.BackgroundAt(0, ContentRow(0))); // the window's left border is not highlighted
        Assert.NotEqual(fill, h.BackgroundAt(6, ContentRow(0))); // the interior divider is not highlighted
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static TableModel Model(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
            if (h.Blocks[i].Kind == BlockKind.Table)
                return h.Bridge.GetTableModel(i)!;
        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static TablePresenter Table(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
            if (h.Blocks[i].Kind == BlockKind.Table)
                return (TablePresenter)h.Bridge.GetPresenter(h.Blocks[i].Id)!;
        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static (int Row, int Col) CellOf(MarkdownEditingHarness h)
    {
        int blockIndex = h.Blocks.IndexOfLine(h.Caret.Position.Line);
        int blockStart = h.Buffer.GetOffset(new TextPosition(h.Blocks.GetStartLine(blockIndex), 0));
        int rel = h.Buffer.GetOffset(h.Caret.Position) - blockStart;
        var rc = Model(h).CellOfOffset(rel)!.Value;
        return (rc.Row, rc.Column);
    }
}
