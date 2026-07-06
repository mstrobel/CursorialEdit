using Cursorial.Input;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Blocks;
using CursorialEdit.Tests.Editing;
using CursorialEdit.Tests.Presenters;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP6 — the two table overflow behaviours on top of the viewport-aware layout (spec §5.6):
/// <list type="bullet">
/// <item><b>Truncate</b> (<see cref="TableOverflow.Truncate"/>) — a wide cell renders on ONE visual row clipped
/// with a trailing <c>…</c>; the focused cell reveals its full content while siblings stay truncated; the caret
/// lands correctly in a truncated cell; toggling the mode re-lays-out.</item>
/// <item><b>Column-window</b> — a table wider than the viewport even at MinWidth draws a presenter-internal
/// horizontal window (NO nested ScrollViewer — FB-6 sidestep); the window follows the caret; the document never
/// scrolls horizontally; a click in an on-window cell lands right. Exercised at viewport widths 40 AND 80.</item>
/// </list>
/// </summary>
public class TableOverflowTests
{
    private static (TableModel Model, string Source) BuildModel(string markdown)
    {
        var harness = BlockHarness.Create(markdown);
        for (var i = 0; i < harness.Blocks.Count; i++)
        {
            if (harness.Blocks[i].Kind == BlockKind.Table)
            {
                string source = harness.TextOf(i);
                return (TableModel.Build(harness.Blocks[i], source)!, source);
            }
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static TablePresenter BuildPresenter(string markdown, TableOverflow overflow = TableOverflow.Wrap)
    {
        var harness = BlockHarness.Create(markdown);
        for (var i = 0; i < harness.Blocks.Count; i++)
        {
            if (harness.Blocks[i].Kind != BlockKind.Table)
                continue;

            int start = harness.Blocks.GetStartLine(i);
            var lines = Enumerable.Range(0, harness.Blocks[i].LineCount).Select(k => harness.Buffer.GetLine(start + k)).ToList();
            string source = harness.TextOf(i);
            return new TablePresenter(lines, TableModel.Build(harness.Blocks[i], source)!, source, overflow: overflow);
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static string FragText(string source, CellFragment f) =>
        f.IsEmpty ? string.Empty : source.Substring(f.SrcStart, f.SrcLength);

    private static TablePresenter Table(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
            if (h.Blocks[i].Kind == BlockKind.Table)
                return Assert.IsType<TablePresenter>(h.Presenter(i));
        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static string Line(MarkdownEditingHarness h, int line) => h.Buffer.GetLine(line).Text;

    /// <summary>A many-column table (headers a.., body A..) that overflows a narrow viewport even at MinWidth.</summary>
    private static string ManyColumns(int n)
    {
        var headers = string.Join(" | ", Enumerable.Range(0, n).Select(i => ((char)('a' + i)).ToString()));
        var body = string.Join(" | ", Enumerable.Range(0, n).Select(i => ((char)('A' + i)).ToString()));
        var delim = string.Concat(Enumerable.Repeat("---|", n));
        return $"| {headers} |\n|{delim}\n| {body} |\n";
    }

    // ───────────────────────────── 1. Truncate — LayoutRow branch ─────────────────────────────

    [Fact]
    public void Truncate_WideCell_OneVisualRow_WithEllipsis_PrefixIsAWholeClusterPrefix()
    {
        var (model, source) = BuildModel("| alpha beta gamma delta | x |\n|---|---|\n| a | b |\n");

        // Column 0 forced to width 8: Wrap would grow the row; Truncate keeps ONE visual row.
        var wrap = model.LayoutRow(0, new[] { 8, 3 }, TableOverflow.Wrap);
        var trunc = model.LayoutRow(0, new[] { 8, 3 }, TableOverflow.Truncate);

        Assert.True(wrap.VisualRowCount > 1, "the wide cell wraps under Wrap");
        Assert.Equal(1, trunc.VisualRowCount); // Truncate: exactly one visual row (no wrap-growth)

        var cell = trunc.VisualRows[0].Cell(0);
        Assert.True(cell.Ellipsis, "the clipped cell flags a trailing …");
        Assert.True(cell.Width <= 8 - TableModel.EllipsisWidth, "prefix + … fits the column width");
        Assert.Equal("alpha b", FragText(source, cell)); // 7 cells of prefix (width 8 − the … cell), a whole-cluster prefix
    }

    [Fact]
    public void Truncate_CaretMap_IsOneRowPerLogicalRow_AndRoundTripsFullContent()
    {
        var (model, source) = BuildModel("| alpha beta gamma delta | short |\n|---|---|\n| x | y |\n");
        var metrics = TableGridMetrics.BuildForViewport(model, 24); // narrow enough that Wrap would wrap column 0
        // Cell (0,0) is the focused cell here, so it is drawn in full (the reveal) and its stop spans the whole
        // content — every source offset round-trips to itself, the caret landing consistently across the reveal.
        var map = TableCaretMap.Build(model, metrics, source, TableOverflow.Truncate, activeCell: (0, 0));

        var (start, end) = model.CellContentRange(0, 0);
        for (var offset = start; offset <= end; offset++)
        {
            var (row, cell) = map.Locate(offset);
            Assert.Equal(offset, map.OffsetAt(row, cell));
        }

        // Grid height: top + (content + separator) per logical row — exactly one content row each (no wrap-growth).
        Assert.Equal(1 + 2 * model.RowCount, map.RowCount);
    }

    [Fact]
    public void Truncate_CellThatFits_IsUnchanged_NoEllipsis()
    {
        var (model, source) = BuildModel("| ab | x |\n|---|---|\n| a | b |\n");
        var trunc = model.LayoutRow(0, new[] { 8, 3 }, TableOverflow.Truncate);

        var cell = trunc.VisualRows[0].Cell(0);
        Assert.False(cell.Ellipsis);
        Assert.Equal("ab", FragText(source, cell));
    }

    // ───────────────────────────── 2. Truncate — render, reveal, caret, toggle ─────────────────────────────

    private const string TruncateDoc =
        "| aaaaaaaaaaaaaaaaaaaa | bbbbbbbbbbbbbbbbbbbb |\n|---|---|\n| cccccccccccccccccccc | dddddddddddddddddddd |\n";

    [Theory]
    [InlineData(nameof(TestCapabilities.KittyTruecolor))]
    [InlineData(nameof(TestCapabilities.Ansi16Legacy))]
    public void Truncate_ShowsEllipsis_AndCollapsesRowHeightToOne(string preset)
    {
        using var h = MarkdownEditingHarness.Create(TruncateDoc, preset, columns: 40, rows: 16);
        var table = Table(h);

        int wrapHeight = table.Rows[0].RowHeight; // header row wraps under the default Wrap mode
        Assert.True(wrapHeight > 3, "under Wrap the wide header column wraps to a taller row");

        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        Assert.Equal(3, table.Rows[0].RowHeight);        // top + ONE content row + separator — the wrap growth is gone
        Assert.Contains("…", h.RowTrimmed(1));           // the header content row is truncated with …
        Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaa", h.RowTrimmed(1)); // the full 20-a content is NOT shown (clipped)
    }

    [Fact]
    public void Truncate_ActiveCell_RevealsFullContent_WhileSiblingRowStaysTruncated()
    {
        using var h = MarkdownEditingHarness.Create(TruncateDoc, columns: 40, rows: 16);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        // Before focus: the body cell is truncated (frame row 3 = body content).
        Assert.DoesNotContain("cccccccccccccccccccc", h.RowTrimmed(3));

        h.Click(2, 3); // the body cell (column 0, its content start)
        h.AssertCaret(2, 2);

        // The focused cell reveals its FULL content (overflowing rightward); the header row (a different zone)
        // stays truncated with its …
        Assert.Contains("cccccccccccccccccccc", h.RowTrimmed(3)); // all 20 c's now visible — the reveal
        Assert.Contains("…", h.RowTrimmed(1));                    // the header sibling stays truncated
    }

    [Fact]
    public void Truncate_CaretLandsRightInATruncatedCell()
    {
        using var h = MarkdownEditingHarness.Create(TruncateDoc, columns: 40, rows: 16);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        // Click the 4th visible glyph of the truncated body cell — the caret must resolve to that source offset
        // (cell 2 is the content start; cell 5 is three clusters in), not somewhere off in the clipped tail.
        string before = Line(h, 2);
        h.Click(5, 3);
        h.AssertCaret(2, 5);

        h.Type("Z");
        Assert.Equal(before.Insert(5, "Z"), Line(h, 2)); // the edit landed at source col 5, inside the cell
        Assert.Contains("cccZ", Line(h, 2));
    }

    [Fact]
    public void Truncate_ClickOnCellRightOfAWideCell_LandsInThatCell_NotTheWideOne()
    {
        // Column 0 is far wider than its resolved width (truncated); its caret-map click-box must be clamped to
        // the DRAWN cell, else clicking the visible text of column 1 (which sits to the right) would resolve into
        // the wide column 0. The 'q' cell is short so column 1 stays visible next to the truncated column 0.
        using var h = MarkdownEditingHarness.Create(
            "| aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | q |\n|---|---|\n| x | y |\n", columns: 40, rows: 12);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        var table = Table(h);
        int col1ContentX = /* the visible 'q' header cell */ FindColumnContentX(h, 'q', frameRow: 1);
        Assert.True(col1ContentX > 0, "column 1's 'q' must be visible next to the truncated column 0");

        h.Click(col1ContentX, 1);
        var (row, col) = (h.Caret.Position.Line, h.Caret.Position.Col);
        // The caret must be inside column 1 (the 'q' cell on header line 0), not back in the wide column 0.
        var cell = table.Model.CellOfOffset(BlockRelOffset(h, row, col));
        Assert.Equal(1, cell!.Value.Column);
    }

    private static int FindColumnContentX(MarkdownEditingHarness h, char glyph, int frameRow)
    {
        string row = h.RowTrimmed(frameRow);
        int i = row.LastIndexOf(glyph);
        return i;
    }

    private static int BlockRelOffset(MarkdownEditingHarness h, int line, int col)
    {
        int blockIndex = h.Blocks.IndexOfLine(line);
        int startLine = h.Blocks.GetStartLine(blockIndex);
        int blockStart = h.Buffer.GetOffset(new TextPosition(startLine, 0));
        return h.Buffer.GetOffset(new TextPosition(line, col)) - blockStart;
    }

    [Fact]
    public void Truncate_LeavingTheTable_ReTruncatesTheCell()
    {
        using var h = MarkdownEditingHarness.Create(TruncateDoc + "\noutro\n", columns: 40, rows: 16);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        h.Click(2, 3); // reveal the body cell
        Assert.Contains("cccccccccccccccccccc", h.RowTrimmed(3));

        h.Key(Key.DownArrow); // move the caret out of the table (down to the outro paragraph)
        h.Key(Key.DownArrow);
        Assert.DoesNotContain("cccccccccccccccccccc", h.RowTrimmed(3)); // re-truncated once unfocused
        Assert.Contains("…", h.RowTrimmed(3));
    }

    [Fact]
    public void Truncate_RightAlignedFittingCell_FocusedStaysFlushRight_NotShiftedLeft()
    {
        // A short, right-aligned cell that FITS its column must keep its alignment when focused — the reveal
        // must not re-draw a fitting cell left-anchored (it only reveals over-wide cells).
        using var h = MarkdownEditingHarness.Create(
            "| r |\n|--:|\n| ab |\n", columns: 40, rows: 12);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        // "ab" (width 2) fits its (min 3) column, right-aligned → flush to the right padding.
        string before = h.RowTrimmed(3);
        Assert.EndsWith("ab │", before);

        h.Click(4, 3); // focus the fitting cell
        Assert.EndsWith("ab │", h.RowTrimmed(3)); // still flush right — not shifted to the left edge
    }

    [Fact]
    public void Truncate_InCellSelectionHighlight_StillPaints()
    {
        using var h = MarkdownEditingHarness.Create(TruncateDoc, columns: 40, rows: 16);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();

        h.Click(2, 3);                              // body cell (its content start)
        h.Key(Key.RightArrow, KeyModifiers.Shift);  // select the first 'c'
        var fill = h.BackgroundAt(2, 3);
        Assert.NotEqual(fill, h.BackgroundAt(0, 3)); // the selection fill is not the border-glyph background
    }

    // ───────────────────────────── 3. Column-window — presenter render ─────────────────────────────

    [Theory]
    [InlineData(40, nameof(TestCapabilities.KittyTruecolor))]
    [InlineData(80, nameof(TestCapabilities.KittyTruecolor))]
    [InlineData(40, nameof(TestCapabilities.Ansi16Legacy))]
    [InlineData(80, nameof(TestCapabilities.Ansi16Legacy))]
    public void ColumnWindow_TableTooWideEvenAtMinWidth_RendersAWindowWithinTheViewport(int viewport, string preset)
    {
        // 16 single-char columns: at MinWidth the grid is 6·16+1 = 97 cells, wider than 40 AND 80.
        var presenter = BuildPresenter(ManyColumns(16));
        using var harness = PresenterHarness.Show([presenter], preset, columns: viewport, rows: 12);

        Assert.True(presenter.GridWidth > viewport, "the full grid overflows the viewport (window engaged)");
        Assert.True(presenter.RenderedWidth <= viewport, $"the drawn window ({presenter.RenderedWidth}) fits the viewport ({viewport})");
        Assert.True(presenter.RenderedWidth > 0);

        // Borders span exactly the drawn window, left corner intact.
        Assert.StartsWith("┌", harness.RowTrimmed(0));
        Assert.Equal(presenter.RenderedWidth, harness.RowTrimmed(0).Length);

        // The leftmost columns are drawn; the last column ('p') is off-window right.
        Assert.Contains("a", harness.RowTrimmed(1)); // header content row
        Assert.DoesNotContain("p", harness.RowTrimmed(1));
        Assert.Equal(0, presenter.WindowColumn); // anchored at the first column (caret has not moved it)
    }

    // ───────────────────────────── 4. Column-window — FB-6 sidestep + caret follow ─────────────────────────────

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public void ColumnWindow_DocumentNeverScrollsHorizontally(int viewport)
    {
        using var h = MarkdownEditingHarness.Create(ManyColumns(16), columns: viewport, rows: 12);
        var scroll = h.Editor.ScrollViewerPart!;
        var table = Table(h);

        Assert.True(table.GridWidth > viewport, "the table overflows the viewport (window engaged)");

        // FB-6 sidestep: the document's horizontal extent equals the viewport (no horizontal scroll room), and the
        // horizontal offset is pinned at 0 — the wide table is windowed INSIDE the presenter, not by the document.
        Assert.Equal(viewport, scroll.Extent.Columns);
        Assert.Equal(0, scroll.HorizontalOffset);

        // Scroll the presenter window by tabbing into an off-window column: the document offset/extent do not move.
        h.Click(2, 1); // land in the first header cell
        for (var i = 0; i < 12; i++)
            h.Key(Key.Tab);

        Assert.Equal(viewport, scroll.Extent.Columns);
        Assert.Equal(0, scroll.HorizontalOffset);
    }

    [Fact]
    public void ColumnWindow_TabIntoOffWindowColumn_ScrollsItIntoView()
    {
        using var h = MarkdownEditingHarness.Create(ManyColumns(16), columns: 40, rows: 12);
        var table = Table(h);

        h.Click(2, 1); // header cell 'a'
        Assert.Equal(0, table.WindowColumn);
        Assert.Contains("a", h.RowTrimmed(1));

        // Tab across the columns until the last one ('p', column 15) is reached — the window must scroll to show it.
        for (var i = 0; i < 15; i++)
            h.Key(Key.Tab);

        Assert.True(table.WindowColumn > 0, "the window scrolled right to follow the caret");
        Assert.Contains("p", h.RowTrimmed(1));        // the previously off-window last column is now visible
        Assert.DoesNotContain("a", h.RowTrimmed(1));  // the first column scrolled off-window left
        Assert.True(table.RenderedWidth <= 40);       // still within the viewport
    }

    [Fact]
    public void ColumnWindow_ClickOnOnWindowCell_LandsInTheRightCell()
    {
        using var h = MarkdownEditingHarness.Create(ManyColumns(16), columns: 40, rows: 12);
        var table = Table(h);
        Assert.Equal(0, table.WindowColumn); // window at the origin — the click's cell needs no window offset

        // 'c' is the third header column; its content sits at ContentX(2) = 14. A click there must land on it
        // (the caret map is offset by the window through ActiveSlide, which is 0 here).
        h.Click(14, 1);
        h.Type("Z");
        Assert.StartsWith("| a | b | Zc |", Line(h, 0));
    }

    [Fact]
    public void ColumnWindow_ClickOnScrolledCell_LandsInTheRightCell()
    {
        using var h = MarkdownEditingHarness.Create(ManyColumns(16), columns: 40, rows: 12);
        var table = Table(h);

        // Scroll the window right, then click a now-visible column — the click adds the window offset back
        // (via ActiveSlide) so it lands in the right cell despite the presenter-internal scroll.
        h.Click(2, 1);
        for (var i = 0; i < 10; i++)
            h.Key(Key.Tab);
        Assert.True(table.WindowColumn > 0, "the window scrolled");

        // The caret is now in an on-window column; typing edits that exact cell (proving the offset round-trips).
        int caretLine = h.Caret.Position.Line;
        int caretCol = h.Caret.Position.Col;
        h.Type("Z");
        Assert.Equal(caretCol + 1, h.Caret.Position.Col); // the edit landed at the caret, in-cell
        Assert.Equal('Z', Line(h, caretLine)[caretCol]);
    }

    // ───────────────────────────── 5. the two modes compose with the column-window ─────────────────────────────

    [Fact]
    public void ColumnWindow_UnderTruncate_StillWindowsWithinTheViewport()
    {
        using var h = MarkdownEditingHarness.Create(ManyColumns(16), columns: 40, rows: 12);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();
        var table = Table(h);

        Assert.Equal(TableOverflow.Truncate, table.OverflowMode);
        Assert.True(table.GridWidth > 40, "overflows at MinWidth");
        Assert.True(table.RenderedWidth <= 40, "the window fits the viewport under Truncate too");
    }

    // ───────────────────────────── 6. WindowOffset ↔ caret integration (independent review) ─────────────────────────────

    [Fact]
    public void ColumnWindow_VerticalOutOfAWindowedTable_UsesTheVisibleGoalColumn() // review #1
    {
        // Table (16 cols, windowed at 40) with a wide paragraph below. Tab to a right column so the window scrolls;
        // the caret's VISIBLE column is far left of its unclipped grid cell. Down out of the table must land at the
        // VISIBLE column in the paragraph, not the unclipped ~50.
        using var h = MarkdownEditingHarness.Create(ManyColumns(16) + "\n" + new string('z', 60) + "\n", columns: 40, rows: 16);
        var table = Table(h);

        h.Click(2, 1); // header cell 'a'
        for (var i = 0; i < 8; i++)
            h.Key(Key.Tab); // reach header column 8 — the window scrolls right to show it
        Assert.True(table.WindowColumn > 0, "the window scrolled to reveal column 8");

        int visibleColumn = h.Caret.VisualDocumentPosition().Cell; // the on-screen column (< the unclipped grid cell)
        Assert.True(visibleColumn < table.RenderedWidth);

        h.Key(Key.DownArrow); // header col 8 → body col 8 (goal captured as the VISIBLE column)
        h.Key(Key.DownArrow); // body col 8 → the paragraph below the table
        int paragraphLine = h.Caret.Position.Line;
        Assert.Equal('z', Line(h, paragraphLine)[0]); // landed in the paragraph

        // The paragraph has no window/slide, so its source column == the visible goal — NOT the unclipped ~50.
        Assert.Equal(visibleColumn, h.Caret.Position.Col);
    }

    [Fact]
    public void ColumnWindow_LeavingTheTable_ResetsTheWindowToTheLeft() // review #2
    {
        using var h = MarkdownEditingHarness.Create(ManyColumns(16) + "\noutro\n", columns: 40, rows: 16);
        var table = Table(h);

        h.Click(2, 1);
        for (var i = 0; i < 12; i++)
            h.Key(Key.Tab); // scroll the window well to the right
        Assert.True(table.WindowColumn > 0, "the window scrolled right");
        Assert.DoesNotContain("a", h.RowTrimmed(1)); // column 0 ('a') is off-window left

        // Click out of the table (into the outro paragraph). The window must reset so the leftmost columns —
        // including the header start — are reachable again on an inactive wide table.
        int outroFrameRow = table.MeasuredHeight(40); // the paragraph sits just below the grid
        h.Click(0, outroFrameRow);
        Assert.Equal(0, table.WindowColumn);
        Assert.Contains("a", h.RowTrimmed(1)); // the grid redraws from column 0
    }

    [Fact]
    public void Truncate_CaretDeepInAFocusedOverWideCell_PublishesWithinTheClip() // review #3
    {
        // A single wide column at a narrow viewport: Truncate clips it to ~16 cells. Focusing it and moving to its
        // END puts the caret's unclipped map cell far past the viewport; the published visible column must be
        // clamped to the drawn width so the caret stays on-screen.
        using var h = MarkdownEditingHarness.Create(
            "| " + new string('a', 60) + " |\n|---|\n| x |\n", columns: 20, rows: 12);
        h.Bridge.OverflowMode = TableOverflow.Truncate;
        h.Settle();
        var table = Table(h);
        Assert.True(table.RenderedWidth <= 20);

        h.Click(2, 1);        // focus the header cell (its content start)
        h.Key(Key.End);       // move to the end of its 60-char content (unclipped cell ~62)

        int published = h.Caret.VisualDocumentPosition().Cell;
        Assert.True(published <= table.RenderedWidth, $"published column {published} must stay within the drawn width {table.RenderedWidth}");
        Assert.True(published <= 20, "and within the viewport");
    }

    [Fact]
    public void ColumnWindow_ResizeReSyncsTheWindowToTheCaret() // review #4
    {
        // 16 cols at viewport 80: columns 0..12 fit (windowed grid 97 > 80). Put the caret in column 12, then
        // shrink to 40 where only ~6 columns fit — the window must re-follow so the caret stays on-screen.
        using var h = MarkdownEditingHarness.Create(ManyColumns(16), columns: 80, rows: 12);
        var table = Table(h);
        Assert.True(table.GridWidth > 80, "windowed at 80");

        h.Click(2, 1);
        for (var i = 0; i < 12; i++)
            h.Key(Key.Tab); // reach column 12 ('m'), visible at width 80
        Assert.Contains("m", h.RowTrimmed(1));

        h.Host.SendResize(40, 12); // only ~6 columns now fit — the window must re-follow the caret
        h.Settle();

        Assert.True(table.RenderedWidth <= 40);
        Assert.True(table.WindowColumn > 0, "the window re-followed the caret after the resize");
        Assert.Contains("m", h.RowTrimmed(1)); // column 12 is still on-screen
        Assert.True(h.Caret.VisualDocumentPosition().Cell <= table.RenderedWidth, "the caret stays within the drawn window");
    }
}
