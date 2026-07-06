using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Blocks;
using CursorialEdit.Tests.Editing;
using CursorialEdit.Tests.Presenters;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// Viewport-aware table auto-layout + word-aware cell wrapping (architecture Decision 11, revised
/// 2026-07-05; spec §5.1). Two behaviours the old content-clamped <c>[3, 40]</c> cap got wrong:
/// <list type="bullet">
/// <item><b>Word wrap.</b> A cell wider than its column now wraps at WORD boundaries (reusing the same
/// framework word-wrap the prose blocks use — <see cref="CaretNavigator.Wrap"/> / <c>WordWrap</c>), with a
/// char-level fallback only for a single over-long word, and never a split grapheme cluster.</item>
/// <item><b>Viewport-aware widths.</b> Column widths depend on the available viewport (known at measure time,
/// threaded in by <see cref="TablePresenter"/>): a table that fits grows its columns to content (may exceed the
/// old 40 cap, no wrap); one that overflows shrinks the widest column(s) and word-wraps them, never below
/// <see cref="TableModel.MinWidth"/>. A viewport resize re-lays-out the table through the same reconcile.</item>
/// </list>
/// Fixtures are ASCII (plus stable-width CJK for the cluster case) — deliberately emoji-free so the width
/// assertions don't couple to the just-corrected emoji width.
/// </summary>
public class TableViewportLayoutTests
{
    private static (TableModel Model, string Source) BuildModel(string markdown)
    {
        var harness = BlockHarness.Create(markdown);
        for (var i = 0; i < harness.Blocks.Count; i++)
        {
            if (harness.Blocks[i].Kind == BlockKind.Table)
            {
                string source = harness.TextOf(i);
                var model = TableModel.Build(harness.Blocks[i], source)!;
                Assert.NotNull(model);
                return (model, source);
            }
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static TablePresenter BuildPresenter(string markdown)
    {
        var harness = BlockHarness.Create(markdown);
        for (var i = 0; i < harness.Blocks.Count; i++)
        {
            if (harness.Blocks[i].Kind != BlockKind.Table)
                continue;

            int start = harness.Blocks.GetStartLine(i);
            var lines = Enumerable.Range(0, harness.Blocks[i].LineCount).Select(k => harness.Buffer.GetLine(start + k)).ToList();
            string source = harness.TextOf(i);
            return new TablePresenter(lines, TableModel.Build(harness.Blocks[i], source)!, source);
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

    // ───────────────────────────── 1. word-aware cell wrap ─────────────────────────────

    [Fact]
    public void WordWrap_MultiWordCell_BreaksAtWordBoundaries_NeverMidWord()
    {
        var (model, source) = BuildModel("| alpha beta gamma | x |\n|---|---|\n| a | b |\n");

        // Force column 0 to width 8 (narrower than the 16-cell content). WordWrap breaks after whole words.
        var layout = model.LayoutRow(0, new[] { 8, 3 });
        Assert.Equal(3, layout.VisualRowCount);

        Assert.Equal("alpha ", FragText(source, layout.VisualRows[0].Cell(0)));
        Assert.Equal("beta ", FragText(source, layout.VisualRows[1].Cell(0)));
        Assert.Equal("gamma", FragText(source, layout.VisualRows[2].Cell(0)));

        // Every fragment is a whole word (or words) — no fragment ends inside "alpha"/"beta"/"gamma".
        var whole = new HashSet<string> { "alpha", "beta", "gamma" };
        for (var v = 0; v < layout.VisualRowCount; v++)
            Assert.Contains(FragText(source, layout.VisualRows[v].Cell(0)).Trim(), whole);
    }

    [Fact]
    public void WordWrap_SingleOverLongWord_FallsBackToCharSplit()
    {
        // "supercalifragilistic" (20 cells) is a single word with no break opportunity, so it hard-breaks at
        // the column edge — the char-level fallback that only ever applies to an over-long word.
        var (model, source) = BuildModel("| supercalifragilistic | x |\n|---|---|\n| a | b |\n");

        var layout = model.LayoutRow(0, new[] { 8, 3 });
        Assert.Equal(3, layout.VisualRowCount);

        Assert.Equal("supercal", FragText(source, layout.VisualRows[0].Cell(0)));    // width 8 — mid-word (char split)
        Assert.Equal("ifragili", FragText(source, layout.VisualRows[1].Cell(0)));    // width 8
        Assert.Equal("stic", FragText(source, layout.VisualRows[2].Cell(0)));        // width 4 remainder
        Assert.Equal(8, layout.VisualRows[0].Cell(0).Width);
        Assert.Equal(4, layout.VisualRows[2].Cell(0).Width);
    }

    [Fact]
    public void WordWrap_WideClusters_AreNeverSplitMidCluster()
    {
        // A run of 2-cell CJK glyphs (single word, no break opportunity) wrapped to an ODD width: the split
        // must land on a cluster boundary, so every fragment is an even number of cells (never a half glyph).
        // CJK width has always been 2 (unlike the just-changed emoji width) — a stable, emoji-free fixture.
        var (model, source) = BuildModel("| " + new string('中', 6) + " | x |\n|---|---|\n| a | b |\n");

        var layout = model.LayoutRow(0, new[] { 5, 3 }); // odd budget 5 → clusters pack to 4, never 5
        int total = 0;
        for (var v = 0; v < layout.VisualRowCount; v++)
        {
            int width = layout.VisualRows[v].Cell(0).Width;
            Assert.True(width == 0 || width % 2 == 0, $"fragment width {width} split a 2-cell cluster");
            Assert.True(width <= 5, $"fragment width {width} exceeds the column");
            total += width;
        }

        Assert.Equal(12, total); // all 6 glyphs (12 cells) accounted for
        Assert.Equal(new string('中', 6), model.CellContent(0, 0));
    }

    // ───────────────────────────── 2. viewport-aware width resolution ─────────────────────────────

    [Fact]
    public void ResolveColumnWidths_NaturalFits_GrowsColumnsToContent_ExceedingTheOld40Cap()
    {
        var (model, _) = BuildModel("| " + new string('a', 60) + " | b |\n|---|---|\n| x | y |\n");

        Assert.Equal(60, model.NaturalWidth(0)); // no max cap — the column wants its full content width
        Assert.Equal(3, model.NaturalWidth(1));  // "b"/"y" floored to the min

        // A budget wider than the natural table → columns grow to content; column 0 exceeds the old 40 cap.
        var widths = model.ResolveColumnWidths(200);
        Assert.Equal(60, widths[0]);
        Assert.Equal(3, widths[1]);
    }

    [Fact]
    public void ResolveColumnWidths_Overflow_ShrinksWidest_RespectsMinFloor_FitsBudget()
    {
        var (model, _) = BuildModel("| " + new string('a', 60) + " | " + new string('b', 20) + " |\n|---|---|\n| x | y |\n");

        // Budget 40 < natural (60 + 20 = 80): the widest column shrinks toward a common ceiling until it fits.
        var widths = model.ResolveColumnWidths(40);
        Assert.Equal(40, widths[0] + widths[1]);      // Σ widths == the content budget (an exact fit)
        Assert.True(widths[0] >= TableModel.MinWidth); // never below the floor
        Assert.True(widths[1] >= TableModel.MinWidth);
        Assert.Equal(20, widths[0]);                   // 60 shrank to 20 (the widest lost the most)
        Assert.Equal(20, widths[1]);
    }

    [Fact]
    public void ResolveColumnWidths_ExtremeNarrow_ClampsEveryColumnToTheMinFloor()
    {
        var (model, _) = BuildModel("| aaaa | bbbb | cccc |\n|---|---|---|\n| x | y | z |\n");

        // Below ColumnCount × MinWidth (9) — the table can only sit at the floor and overflow horizontally.
        var widths = model.ResolveColumnWidths(5);
        Assert.All(widths, w => Assert.Equal(TableModel.MinWidth, w));
    }

    // ───────────────────────────── 3. presenter renders fit vs. overflow ─────────────────────────────

    [Fact]
    public void WideViewport_TableFits_RendersUnwrapped_ColumnGrowsBeyondTheOldCap()
    {
        var presenter = BuildPresenter("| " + new string('a', 60) + " | b |\n|---|---|\n| x | y |\n");
        using var harness = PresenterHarness.Show([presenter], columns: 100, rows: 12);

        // Grid: col0 60 (> 40), col1 3, chrome 7 → width 70, all inside the 100-wide viewport, nothing wraps.
        Assert.Equal(70, presenter.GridWidth);
        Assert.Equal(3, presenter.Rows[0].RowHeight); // top + ONE content row + separator ⇒ no wrap
        Assert.Contains(new string('a', 60), harness.RowTrimmed(1)); // all 60 a's on a single visual row
    }

    [Fact]
    public void NarrowViewport_TableOverflows_ShrinksWidest_WordWraps_FitsWithinTheViewport()
    {
        var presenter = BuildPresenter(
            "| " + new string('a', 60) + " | " + new string('b', 20) + " |\n|---|---|\n| x | y |\n");
        using var harness = PresenterHarness.Show([presenter], columns: 40, rows: 24);

        // The natural table (60 + 20 + chrome) far exceeds 40, so the widest column shrinks and both wrap.
        Assert.True(presenter.GridWidth <= 40, $"grid {presenter.GridWidth} must fit the 40-wide viewport");
        Assert.True(presenter.Rows[0].RowHeight > 3, "the shrunk header column wraps to multiple rows");
        Assert.StartsWith("┌", harness.RowTrimmed(0));
        Assert.EndsWith("┐", harness.RowTrimmed(0));
        Assert.Equal(presenter.GridWidth, harness.RowTrimmed(0).Length); // borders span exactly the resolved grid
    }

    // ───────────────────────────── 4. resize re-lays-out the table (not the document) ─────────────────────────────

    [Fact]
    public void ShrinkingTheViewport_WrapsAPreviouslyUnwrappedColumn_WideningRegrowsIt()
    {
        using var h = MarkdownEditingHarness.Create(
            "| " + new string('a', 30) + " | b |\n|---|---|\n| x | y |\n\noutro\n", columns: 60, rows: 20);

        var table = Table(h);
        var outroBefore = h.Presenter(h.Blocks.Count - 1);
        Assert.Equal(3, table.Rows[0].RowHeight); // at 60 the 30-cell column fits unwrapped (top + 1 + sep)

        h.Host.SendResize(24, 20); // shrink the viewport
        h.Settle();

        Assert.True(table.Rows[0].RowHeight > 3, "shrinking the viewport wraps the wide column");
        // Bounded to the table: the surrounding document was re-laid-out, not re-parsed — the outro paragraph's
        // presenter is the SAME instance (no re-realization), so the resize touched the table's rows only.
        Assert.Same(outroBefore, h.Presenter(h.Blocks.Count - 1));

        h.Host.SendResize(60, 20); // widen it back
        h.Settle();
        Assert.Equal(3, table.Rows[0].RowHeight); // the column re-grows and unwraps
    }

    [Fact]
    public void ResizeReLayout_IsByteIdenticalToAFreshRenderAtTheNewSize()
    {
        const string doc = "| " + "one two three four five" + " | z |\n|---|---|\n| a | b |\n";
        using var h = MarkdownEditingHarness.Create(doc, columns: 60, rows: 16);

        h.Host.SendResize(22, 16); // force the multi-word column to word-wrap
        h.Settle();

        using var fresh = MarkdownEditingHarness.Create(doc, columns: 22, rows: 16);
        for (var row = 0; row < 16; row++)
            Assert.Equal(fresh.RowTrimmed(row), h.RowTrimmed(row));
    }

    // ───────────────────────────── 5. caret + selection survive the relayout ─────────────────────────────

    [Fact]
    public void Caret_StaysInItsCell_AcrossAViewportAwareRelayout()
    {
        using var h = MarkdownEditingHarness.Create(
            "| " + new string('a', 30) + " | b |\n|---|---|\n| c | d |\n", columns: 60, rows: 16);

        h.Click(2, 3);          // the "c" body cell (column 0, body content row)
        h.AssertCaret(2, 2);    // source line 2 "| c | d |", "c" at col 2

        h.Host.SendResize(24, 16); // the wide column now wraps — the whole grid re-lays-out
        h.Settle();

        h.AssertCaret(2, 2);    // the caret is a source anchor — its position survives the relayout
        h.Type("Z");            // and the rebuilt caret map still lands the edit in the "c" cell (at its start)
        Assert.Equal("| Zc | d |", Line(h, 2));
    }

    [Fact]
    public void Selection_HighlightSurvivesAViewportAwareRelayout()
    {
        using var h = MarkdownEditingHarness.Create(
            "| " + new string('a', 30) + " | b |\n|---|---|\n| c | d |\n", columns: 60, rows: 16);

        h.Click(2, 3);                              // "c" body cell
        h.Key(Key.RightArrow, KeyModifiers.Shift);  // select "c" → range (2,2)-(2,3)
        var fill = h.BackgroundAt(2, 3);
        Assert.NotEqual(fill, h.BackgroundAt(0, 3)); // the border '│' is not the selection fill

        h.Host.SendResize(24, 16); // header wraps to 3 rows → the body cell moves down to frame row 5
        h.Settle();

        Assert.Equal(new TextPosition(2, 3), h.Caret.Position);       // selection source preserved
        Assert.Equal(new TextPosition(2, 2), h.Caret.SelectionAnchor);
        Assert.Equal(fill, h.BackgroundAt(2, 5));                     // the "c" cell is still highlighted
        Assert.NotEqual(fill, h.BackgroundAt(0, 5));                  // still not the border glyph
    }

    // ───────────────────────────── 6. differential: fragments + caret are internally consistent ─────────────────────────────

    [Fact]
    public void ViewportWidthsAndWrappedFragments_ReconstructTheCell_AndCaretRoundTrips()
    {
        var (model, source) = BuildModel(
            "| alpha beta gamma delta | short |\n|---|---|\n| x | y |\n");

        var metrics = TableGridMetrics.BuildForViewport(model, 24); // narrow enough to wrap column 0
        var widths = metrics.ColumnWidths;
        var layout = model.LayoutRow(0, widths);
        Assert.True(layout.VisualRowCount > 1, "the multi-word column wrapped");

        // The wrapped fragments tile the cell exactly — concatenation reproduces the trimmed content.
        var reconstructed = string.Concat(
            layout.VisualRows.Select(v => FragText(source, v.Cell(0))));
        Assert.Equal(model.CellContent(0, 0), reconstructed);

        // The caret round-trips through the composite grid map built on the same viewport-aware widths.
        var map = TableCaretMap.Build(model, metrics, source);
        var (start, end) = model.CellContentRange(0, 0);
        for (var offset = start; offset <= end; offset++)
        {
            var (row, cell) = map.Locate(offset);
            Assert.Equal(offset, map.OffsetAt(row, cell));
        }
    }

    // ───────────────────────────── 7. WordWrap-reuse trailing-space fixes (review wave) ─────────────────────────────

    [Fact]
    public void WordWrap_WordExactlyFillingColumn_DoesNotSpillALoneSpaceRow()
    {
        // "abcd efgh" in a column that shrinks to width 4: "abcd" fills it exactly, so prose WordWrap parks the
        // following space as its own segment. A CELL must not render that as a blank visual row — "efgh" sits
        // directly below "abcd" (two rows, not three). (Review finding 1.)
        using var h = MarkdownEditingHarness.Create("| x |\n|---|\n| abcd efgh |\n", columns: 8, rows: 12);

        Assert.Equal("│ abcd │", h.RowTrimmed(3));
        Assert.Equal("│ efgh │", h.RowTrimmed(4)); // directly below — no lone-space blank row between
        Assert.Equal("└──────┘", h.RowTrimmed(5)); // bottom border right after ⇒ the cell is 2 rows, not 3
    }

    [Fact]
    public void WordWrap_RightAlignedWrappedCell_TrimsTrailingSpace_StaysFlushRight()
    {
        // Right-aligned "one two" at column width 4: "one " fills exactly WITH a trailing space. That space must
        // be trimmed from the rendered width so "one" is flush to the right edge, not shoved a cell left by the
        // kept space. (Review finding 2.)
        using var h = MarkdownEditingHarness.Create("| r |\n|--:|\n| one two |\n", columns: 8, rows: 12);

        Assert.EndsWith("one │", h.RowTrimmed(3)); // flush against the right padding — not "one  │"
        Assert.EndsWith("two │", h.RowTrimmed(4));
    }
}
