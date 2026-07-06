using Cursorial.Input;
using Cursorial.Output;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP5 — live, <b>incremental</b> table reflow (spec §5.5 / architecture Decision 11). Driven
/// end-to-end through the real markdown surface (<see cref="MarkdownEditingHarness"/>): typing a new-widest
/// character widens its column and re-lands the borders without moving the surrounding document; deleting
/// the widest content shrinks the column back (and a non-unique-widest delete does <b>not</b> shrink — the
/// O(1) <see cref="TableModel.CountAtMax"/> cache); damage is bounded to the edited row for a stable-geometry
/// edit and to the table (never the document) for a width change; and the incrementally-reflowed grid is
/// byte-identical to a from-scratch full render (the differential invariant, which also pins the shrink
/// clear — no stale "vertical striping" cells past the narrowed border).
/// </summary>
public sealed class TableReflowTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // A table at the frame top: logical row L's content sits at frame row (1 + 2·L) — row 0 is the top
    // border, then each logical row contributes a content row and a trailing border row.
    private static int ContentRow(int logicalRow) => 1 + 2 * logicalRow;

    private static string Line(MarkdownEditingHarness h, int line) => h.Buffer.GetLine(line).Text;

    private static TablePresenter Table(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
        {
            if (h.Blocks[i].Kind == BlockKind.Table)
                return Assert.IsType<TablePresenter>(h.Presenter(i));
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static int ColumnWidth(MarkdownEditingHarness h, int column)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
        {
            if (h.Blocks[i].Kind == BlockKind.Table)
                return h.Bridge.GetTableModel(i)!.ColumnWidth(column);
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static int[] RenderCounts(TablePresenter table) => [.. table.Rows.Select(r => r.RenderCount)];

    private static int[] DeriveCounts(TablePresenter table) => [.. table.Rows.Select(r => r.DeriveCount)];

    private static int Changed(int[] before, IReadOnlyList<TableRowPresenter> rows, Func<TableRowPresenter, int> now)
    {
        int changed = 0;
        for (var i = 0; i < before.Length; i++)
            if (now(rows[i]) > before[i])
                changed++;
        return changed;
    }

    // ───────────────────────────── §5 acceptance 3: widen on new-widest ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void WidenOnType_WidensTheColumn_RelandsBorders_DocumentDoesNotMove(string preset)
    {
        // A paragraph below the table: a column widen must not disturb or re-raster it (§5.5 [DECISION]).
        using var h = MarkdownEditingHarness.Create(
            "| A | B |\n|---|---|\n| 1 | 2 |\n\noutro\n", preset, columns: 60, rows: 20);

        Assert.Equal(3, ColumnWidth(h, 0)); // clamped to the min width
        var outro = h.Presenter(h.Blocks.Count - 1);
        int outroRastersBefore = outro.RenderCount;

        h.Click(3, ContentRow(1)); // just after the '1' in the body cell
        h.Type("ppppp");           // "1" → "1ppppp" — the new unique widest in column 0

        Assert.Equal("| 1ppppp | 2 |", Line(h, 2));
        Assert.Equal(6, ColumnWidth(h, 0)); // "1ppppp" = 6 cells

        // The border glyphs re-land on the widened column: the top border spans the full grid width.
        Assert.StartsWith("┌", h.RowTrimmed(0));
        Assert.EndsWith("┐", h.RowTrimmed(0));
        Assert.Equal(Table(h).GridWidth, h.RowTrimmed(0).Length); // ┌───…───┐ is exactly the grid wide
        Assert.Contains("1ppppp", h.RowTrimmed(ContentRow(1)));

        // The surrounding document was not re-rastered (a widen changes width, not the table's height).
        Assert.Equal(outroRastersBefore, outro.RenderCount);
    }

    // ───────────────────────────── §5 acceptance 4: shrink on delete (CountAtMax) ─────────────────────────────

    [Fact]
    public void DeleteWidest_ShrinksColumnBack()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(3, ContentRow(1));
        h.Type("ppppp");                    // widen column 0 to hold "1ppppp"
        Assert.Equal(6, ColumnWidth(h, 0)); // width 6

        for (var i = 0; i < 5; i++)         // delete the widest content back out
            h.Key(Key.Backspace);

        Assert.Equal("| 1 | 2 |", Line(h, 2));
        Assert.Equal(3, ColumnWidth(h, 0)); // shrank back to the clamp
    }

    [Fact]
    public void DeleteNonUniqueWidest_DoesNotShrink_CountAtMax()
    {
        // Column 0 has TWO cells at the max width ("aaaa" in both the header and the body): CountAtMax == 2,
        // so deleting from one leaves the other holding the max — the column must NOT reflow (Decision 11).
        using var h = MarkdownEditingHarness.Create("| aaaa | b |\n|---|---|\n| aaaa | c |\n", columns: 40, rows: 12);

        Assert.Equal(4, ColumnWidth(h, 0)); // "aaaa" = 4 cells

        var table = Table(h);
        h.Click(3, ContentRow(1)); // in the body cell "aaaa" (after the first 'a')
        var rasterBefore = RenderCounts(table);
        h.Key(Key.Backspace);      // "aaaa" → "aaa" in the body — no longer at the max

        Assert.Equal("| aaa | c |", Line(h, 2));
        Assert.Equal(4, ColumnWidth(h, 0)); // the header's "aaaa" still holds the max → no shrink

        // Stable geometry ⇒ damage bounded to exactly the edited (body) row.
        Assert.Equal(1, Changed(rasterBefore, table.Rows, r => r.RenderCount));
    }

    // ───────────────────────────── damage bounded (RenderCount / DeriveCount) ─────────────────────────────

    [Fact]
    public void StableGeometryEdit_ReRastersAndReDerivesOnlyTheEditedRow()
    {
        // Typing a char that keeps column 0 at the min width (a stable-geometry edit) must re-raster AND
        // re-derive exactly the edited row — the spike-review #7 economy (before WP5 every row re-derived
        // LayoutRow+run-map+signature each keystroke).
        using var h = MarkdownEditingHarness.Create(
            "| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n| 5 | 6 |\n| 7 | 8 |\n", columns: 40, rows: 24);

        var table = Table(h);
        Assert.Equal(5, table.Rows.Count); // header + 4 body rows

        h.Click(3, ContentRow(3)); // body logical row 3 ("| 5 | 6 |")
        var rasterBefore = RenderCounts(table);
        var deriveBefore = DeriveCounts(table);

        h.Type("p"); // "5" → "5p" — width 2, still under the min-clamp 3 → stable geometry

        Assert.Equal(3, ColumnWidth(h, 0)); // unchanged
        Assert.Equal(1, Changed(rasterBefore, table.Rows, r => r.RenderCount)); // one row re-rastered
        Assert.Equal(1, Changed(deriveBefore, table.Rows, r => r.DeriveCount)); // one row re-derived (#7)
    }

    [Fact]
    public void WidthChange_ReRastersTheTableRows_ButNotTheSurroundingDocument()
    {
        using var h = MarkdownEditingHarness.Create(
            "| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n\noutro\n", columns: 40, rows: 20);

        var table = Table(h);
        var outro = h.Presenter(h.Blocks.Count - 1);

        h.Click(3, ContentRow(1)); // first body row
        int outroBefore = outro.RenderCount;
        var rasterBefore = RenderCounts(table);

        h.Type("ppppp"); // widen column 0 — the divider band shifts, so the table rows re-land their borders

        Assert.True(ColumnWidth(h, 0) > 3, "column widened");
        // A genuine width change re-rasters the table's rows (their arranged width changes — borders moved),
        // but it is bounded to the table: the surrounding document is untouched.
        Assert.True(Changed(rasterBefore, table.Rows, r => r.RenderCount) >= 1, "the width change re-rasters affected table rows");
        Assert.Equal(outroBefore, outro.RenderCount); // the paragraph below was not re-rastered
    }

    // ───────────────────────────── shrink clears vacated cells (no vertical striping) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void WidenThenShrink_LeavesNoStaleCells_ByteIdenticalToAFreshNarrowRender(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 40, rows: 12);

        h.Click(3, ContentRow(1));
        h.Type("pppppppp"); // widen column 0 well past the min
        Assert.True(Table(h).GridWidth > 13, "the table widened");

        for (var i = 0; i < 8; i++)
            h.Key(Key.Backspace); // shrink it all the way back to "| 1 | 2 |"

        Assert.Equal("| 1 | 2 |", Line(h, 2));

        // The narrowed grid must be byte-identical to a from-scratch render of the same (narrow) source —
        // the vacated wider cells (old border '│' + content) are cleared, not left as vertical stripes.
        using var fresh = MarkdownEditingHarness.Create(h.Buffer.GetText(), preset, columns: 40, rows: 12);
        h.Click(3, ContentRow(1));     // put both carets in the same cell so active/reveal/caret state matches
        fresh.Click(3, ContentRow(1));

        for (var row = 0; row < 12; row++)
            Assert.Equal(fresh.Host.GetRowText(row), h.Host.GetRowText(row));

        // And explicitly: nothing survives past the narrow border on the content rows.
        Assert.Equal("┌─────┬─────┐", h.RowTrimmed(0));
        Assert.Equal("│ 1   │ 2   │", h.RowTrimmed(ContentRow(1)));
        Assert.Equal("└─────┴─────┘", h.RowTrimmed(4));
    }

    // ───────────────────────────── differential: incremental ≡ full rebuild ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void IncrementalReflow_IsByteIdenticalToAFullRebuild_ForAScriptedEditSequence(string preset)
    {
        using var h = MarkdownEditingHarness.Create(
            "| head | col |\n|---|---|\n| a | b |\n| cc | dd |\n", preset, columns: 60, rows: 16);

        // A scripted sequence exercising widen, shrink, and cross-row growth.
        h.Click(3, ContentRow(1)); h.Type("XXXX"); // widen column 0 (body row 1 becomes the new widest)
        h.Click(3, ContentRow(2)); h.Type("YY");   // grow another body cell in column 0
        h.Click(6, ContentRow(1));
        for (var i = 0; i < 3; i++) h.Key(Key.Backspace); // shrink column 0 back down from the widest cell

        // The incrementally-reflowed live grid must equal a full from-scratch render of the final source.
        using var fresh = MarkdownEditingHarness.Create(h.Buffer.GetText(), preset, columns: 60, rows: 16);
        h.Click(3, ContentRow(1));
        fresh.Click(3, ContentRow(1));

        for (var row = 0; row < 16; row++)
            Assert.Equal(fresh.Host.GetRowText(row), h.Host.GetRowText(row));

        // The models agree too (widths / CountAtMax derived from the same final source).
        var incremental = Table(h).Model;
        var scratch = Table(fresh).Model;
        Assert.Equal(scratch.ColumnCount, incremental.ColumnCount);
        for (var c = 0; c < scratch.ColumnCount; c++)
        {
            Assert.Equal(scratch.ColumnWidth(c), incremental.ColumnWidth(c));
            Assert.Equal(scratch.MaxContentWidth(c), incremental.MaxContentWidth(c));
            Assert.Equal(scratch.CountAtMax(c), incremental.CountAtMax(c));
        }
    }

    // ───────────────────────────── WP4-deferred #2: in-cell selection highlight ─────────────────────────────

    // Selection is asserted on a BODY cell (the header row carries a fill that, on the 16-color tier,
    // quantizes to the same palette entry as the selection fill — a body cell has a plain background).
    private const string SelectionTable = "| h | w |\n|---|---|\n| hello | world |\n";

    [Theory]
    [MemberData(nameof(Presets))]
    public void StructuralRebuild_WithUnchangedSelection_ClearsTheHighlight_NoStaleBox(string preset)
    {
        // Review finding 1: a RowCount-change reconcile (RebuildChildren) clears the highlighted-row set, but
        // when the selection's range is unchanged the DocumentCaret was==now gate skips InvalidateSelectionOverlay,
        // so the rebuilt rows draw the highlight while the tracking set is empty — a later clear then re-rasters
        // nothing and the highlight goes STALE. RetrackHighlightedRows re-syncs the set after the rebuild.
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| a | b |\n", preset, columns: 40, rows: 12);

        h.Click(2, ContentRow(1));                    // the 'a' of the body cell
        h.Key(Key.RightArrow, KeyModifiers.Shift);    // select "a" → selection (2,2)-(2,3)
        var fill = h.BackgroundAt(2, ContentRow(1));
        Assert.NotEqual(fill, h.BackgroundAt(0, ContentRow(1))); // the border '│' is not the fill — the highlight is real

        // Append a row BELOW the selection, PRESERVING it (before==after) → RowCount 2→3 → RebuildChildren with
        // the was==now gate skipping InvalidateSelectionOverlay. This is the WP7-structural-op shape, forced here.
        var keep = new CaretState(new TextPosition(2, 3), new TextPosition(2, 2));
        h.Controller.Apply(new Edit(new TextPosition(3, 0), string.Empty, "| c | d |\n"), EditKind.Typing, keep, keep);
        h.Settle();
        Assert.Equal(fill, h.BackgroundAt(2, ContentRow(1))); // still selected → still highlighted through the rebuild

        h.Key(Key.LeftArrow); // collapse the selection
        h.Settle();
        Assert.NotEqual(fill, h.BackgroundAt(2, ContentRow(1))); // highlight cleared — not a stale box
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void SelectingWithinACell_HighlightsThoseCells_NotTheBorders(string preset)
    {
        using var h = MarkdownEditingHarness.Create(SelectionTable, preset, columns: 40, rows: 12);

        h.Click(2, ContentRow(1)); // the 'h' of the body cell "hello"
        h.AssertCaret(2, 2);
        for (var i = 0; i < 5; i++)
            h.Key(Key.RightArrow, KeyModifiers.Shift); // select "hello" (source cols 2..7 on line 2)

        // The five "hello" cells (2..6) all carry the selection fill; the border '│' at 0 does not.
        var fill = h.BackgroundAt(2, ContentRow(1));
        for (var column = 3; column <= 6; column++)
            Assert.Equal(fill, h.BackgroundAt(column, ContentRow(1)));

        Assert.NotEqual(fill, h.BackgroundAt(0, ContentRow(1))); // left border '│' — structure, never highlighted
        Assert.NotEqual(fill, h.BackgroundAt(8, ContentRow(1))); // the divider '│' after column 0
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void SelectionSpanningCells_HighlightsCoveredContent_NotTheDividerGlyphs(string preset)
    {
        using var h = MarkdownEditingHarness.Create(SelectionTable, preset, columns: 40, rows: 12);

        h.Click(2, ContentRow(1)); // 'h' of the body "hello"
        for (var i = 0; i < 12; i++)
            h.Key(Key.RightArrow, KeyModifiers.Shift); // extend across the divider into "world"

        var fill = h.BackgroundAt(2, ContentRow(1)); // a selected "hello" content cell
        Assert.Equal(fill, h.BackgroundAt(10, ContentRow(1))); // a "world" content cell is highlighted too
        Assert.NotEqual(fill, h.BackgroundAt(8, ContentRow(1))); // the divider '│' between the cells is NOT
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void SelectionAfterEditingAnEarlierRow_LandsOnTheRightCells(string preset)
    {
        // Regression: an edit on an earlier row shifts every later row's block-relative offsets. The WP5
        // skip re-binds those (unchanged) rows' offsets, so a selection in a LATER row still lands right —
        // it must not be misplaced by the earlier splice's delta.
        using var h = MarkdownEditingHarness.Create(
            "| h | w |\n|---|---|\n| aa | b |\n| hello | world |\n", preset, columns: 40, rows: 14);

        h.Click(3, ContentRow(1)); // body row 1 cell "aa"
        h.Type("qq");              // "aa" → "qaqa"-ish; shifts row-2 offsets, stable geometry (column stays wide)

        // Now select "hello" in the LATER body row (logical row 2). Its offsets were re-bound by the skip.
        h.Click(2, ContentRow(2));
        for (var i = 0; i < 5; i++)
            h.Key(Key.RightArrow, KeyModifiers.Shift);

        var fill = h.BackgroundAt(2, ContentRow(2));
        for (var column = 3; column <= 6; column++)
            Assert.Equal(fill, h.BackgroundAt(column, ContentRow(2))); // "hello" cells highlighted, correctly placed
        Assert.NotEqual(fill, h.BackgroundAt(0, ContentRow(2)));       // not the border
    }

    [Fact]
    public void Selection_OnNoColorTier_UsesInverse()
    {
        using var h = MarkdownEditingHarness.Create(
            SelectionTable, TestSupport.CapabilityPresets.NoColor, columns: 40, rows: 12);

        h.Click(2, ContentRow(1));
        for (var i = 0; i < 5; i++)
            h.Key(Key.RightArrow, KeyModifiers.Shift);

        // On NoColor the highlight rides TextAttributes.Inverse in the cell (a background scrim degrades away).
        Assert.True(h.AttributesAt(2, ContentRow(1)).HasFlag(TextAttributes.Inverse), "selected cell should carry Inverse");
        Assert.False(h.AttributesAt(0, ContentRow(1)).HasFlag(TextAttributes.Inverse), "the border '│' should not be inverted");
    }

    // ───────────────────────────── shrink-clear via undo and resize (other striping triggers) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void UndoAfterWiden_ShrinksAndLeavesNoStaleCells(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 40, rows: 12);

        h.Click(3, ContentRow(1));
        h.Type("pppppppp"); // one coalesced undo group widens column 0
        Assert.True(Table(h).GridWidth > 13);

        h.Chord('z', KeyModifiers.Control); // undo → the column shrinks back in one step
        Assert.Equal("| 1 | 2 |", Line(h, 2));

        using var fresh = MarkdownEditingHarness.Create(h.Buffer.GetText(), preset, columns: 40, rows: 12);
        h.Click(3, ContentRow(1));
        fresh.Click(3, ContentRow(1));
        for (var row = 0; row < 12; row++)
            Assert.Equal(fresh.Host.GetRowText(row), h.Host.GetRowText(row)); // no stale wider-border stripes
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void ResizeAfterWiden_ReLayoutLeavesNoStaleCells(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 80, rows: 16);

        h.Click(3, ContentRow(1));
        h.Type("pppppppp"); // widen column 0
        for (var i = 0; i < 8; i++)
            h.Key(Key.Backspace); // shrink it back

        h.Host.SendResize(50, 14); // terminal resize re-layout (the other striping trigger)
        h.Settle();

        using var fresh = MarkdownEditingHarness.Create(h.Buffer.GetText(), preset, columns: 50, rows: 14);
        h.Click(3, ContentRow(1));
        fresh.Click(3, ContentRow(1));
        for (var row = 0; row < 14; row++)
            Assert.Equal(fresh.Host.GetRowText(row), h.Host.GetRowText(row)); // grid is clean after resize
    }
}
