using Cursorial.Output;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Blocks;
using CursorialEdit.Tests.Editing;
using CursorialEdit.Tests.Presenters;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP2 — the render-only <see cref="TablePresenter"/> + per-row <see cref="TableRowPresenter"/>:
/// box-drawing glyphs land on exact columns for ASCII, CJK, aligned, and wrapped-cell tables; the header
/// row is bold; a wrapped cell grows its row; and the table renders end-to-end through the real
/// <see cref="MarkdownViewBridge"/> / <see cref="EditorControl"/>. Every logical row is its own render
/// boundary (the committed M3 deliverable).
/// </summary>
public class TablePresenterTests
{
    private static TablePresenter Build(string markdown)
    {
        var harness = BlockHarness.Create(markdown);
        int index = -1;
        for (var i = 0; i < harness.Blocks.Count; i++)
        {
            if (harness.Blocks[i].Kind == BlockKind.Table)
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0, "no table block was produced");
        var block = harness.Blocks[index];
        int start = harness.Blocks.GetStartLine(index);
        var lines = Enumerable.Range(0, block.LineCount).Select(k => harness.Buffer.GetLine(start + k)).ToList();
        string source = harness.TextOf(index);
        var model = TableModel.Build(block, source)!;
        Assert.NotNull(model);
        return new TablePresenter(lines, model, source);
    }

    private static PresenterHarness Show(TablePresenter presenter, int columns = 40, int rows = 14)
        => PresenterHarness.Show([presenter], columns: columns, rows: rows);

    // ───────────────────────────── border glyphs on exact columns ─────────────────────────────

    [Fact]
    public void Ascii_BorderGlyphsLandOnExactColumns()
    {
        using var harness = Show(Build("| A | B |\n|---|---|\n| 1 | 2 |\n"));

        // Both columns clamp to the min width 3 → each cell box is 5 cells wide, dividers at x 0/6/12.
        Assert.Equal("┌─────┬─────┐", harness.RowTrimmed(0));
        Assert.Equal("│ A   │ B   │", harness.RowTrimmed(1));
        Assert.Equal("├─────┼─────┤", harness.RowTrimmed(2));
        Assert.Equal("│ 1   │ 2   │", harness.RowTrimmed(3));
        Assert.Equal("└─────┴─────┘", harness.RowTrimmed(4));

        Assert.Equal("┌", harness.Cell(0, 0).Grapheme);
        Assert.Equal("┬", harness.Cell(6, 0).Grapheme);
        Assert.Equal("┐", harness.Cell(12, 0).Grapheme);
        Assert.Equal("┼", harness.Cell(6, 2).Grapheme);
    }

    [Fact]
    public void Cjk_BordersAlignToWideCellWidth()
    {
        using var harness = Show(Build("| 名前 | 年齢 |\n|---|---|\n| 田中 | 30 |\n"));

        // Wide glyphs make each column 4 cells; box width 6, dividers at x 0/7/14.
        Assert.Equal("┌──────┬──────┐", harness.RowTrimmed(0));
        Assert.Equal("│", harness.Cell(0, 1).Grapheme);
        Assert.Equal("名", harness.Cell(2, 1).Grapheme);
        Assert.Equal("│", harness.Cell(7, 1).Grapheme); // the divider lands after the 4-cell content + padding
        Assert.Equal("年", harness.Cell(9, 1).Grapheme);
        Assert.Equal("│", harness.Cell(14, 1).Grapheme);
    }

    [Fact]
    public void Alignment_RightAlignsContentWithinTheCellBox()
    {
        // Column 1 is right-aligned; "R" (width 1) sits at the right edge of its 3-cell content region.
        using var harness = Show(Build("| x | R |\n|---|--:|\n| a | bb |\n"));

        Assert.Equal("R", harness.Cell(10, 1).Grapheme); // right edge of column 1's content region
        Assert.Equal("b", harness.Cell(9, 3).Grapheme);  // "bb" right-aligned → cells 9,10
        Assert.Equal("b", harness.Cell(10, 3).Grapheme);
        Assert.Equal(" ", harness.Cell(8, 3).Grapheme ?? " "); // the left of the region is padding
    }

    // ───────────────────────────── header styling ─────────────────────────────

    [Fact]
    public void Header_IsBold_BodyIsPlain()
    {
        using var harness = Show(Build("| A | B |\n|---|---|\n| 1 | 2 |\n"));

        Assert.True(harness.Cell(2, 1).Style.Attributes.HasFlag(TextAttributes.Bold), "header cell should be bold");
        Assert.False(harness.Cell(2, 3).Style.Attributes.HasFlag(TextAttributes.Bold), "body cell should be plain");
    }

    // ───────────────────────────── wrapped cell grows the row ─────────────────────────────

    [Fact]
    public void WrappedCell_GrowsItsRow_WithoutMovingOtherRows()
    {
        var presenter = Build("| short | " + new string('x', 50) + " |\n|---|---|\n| a | b |\n");
        using var harness = Show(presenter, columns: 60, rows: 16);

        // Header row 0 wraps its 50-cell cell to two content visual rows → row height 4 (top + 2 + sep);
        // the plain body row stays height 2 (content + bottom). Total table height 6.
        Assert.Equal(4, presenter.Rows[0].RowHeight);
        Assert.Equal(2, presenter.Rows[1].RowHeight);
        Assert.Equal(6, presenter.MeasuredHeight(60));

        // The wrap draws a second content row of x's between the header content and the separator.
        Assert.Contains("xxxx", harness.RowTrimmed(2));
        Assert.StartsWith("│", harness.RowTrimmed(2)); // still bordered
    }

    [Fact]
    public void PerRow_IsRenderBoundary()
    {
        var presenter = Build("| A | B |\n|---|---|\n| 1 | 2 |\n");
        using var _ = Show(presenter);

        Assert.Equal(2, presenter.Rows.Count);
        foreach (var row in presenter.Rows)
            Assert.True(row.IsRenderBoundary, "each logical row must be its own render boundary (the committed M3 deliverable)");
    }

    // ───────────────────────────── run maps per visual row ─────────────────────────────

    [Fact]
    public void RunMap_BorderGlyphsAreSyntheticWithNoCaretStop_CellsAreTextRuns()
    {
        var presenter = Build("| A | B |\n|---|---|\n| 1 | 2 |\n");
        using var _ = Show(presenter);

        // Row 0 owns the top border (a synthetic line) then its header content line.
        var row0 = presenter.Rows[0].VisualLines;
        Assert.Equal(TableLineKind.TopBorder, row0[0].Kind);
        Assert.All(row0[0].Runs, run => Assert.Equal(0, run.SrcLen)); // borders carry no caret stop

        var content = row0[1];
        Assert.Equal(TableLineKind.Content, content.Kind);
        var textRuns = content.Runs.Where(r => r.Kind == CursorialEdit.Layout.RunKind.Text).ToArray();
        Assert.Equal(2, textRuns.Length); // one text run per cell (A, B)
        Assert.All(textRuns, run => Assert.True(run.SrcLen > 0)); // each maps to real cell source
    }

    // ───────────────────────────── end-to-end through the real bridge ─────────────────────────────

    [Fact]
    public void EndToEnd_TableRendersThroughEditorControl()
    {
        using var harness = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 16);

        Assert.IsType<TablePresenter>(harness.Presenter(0));
        Assert.Equal("┌─────┬─────┐", harness.RowTrimmed(0));
        Assert.Equal("│ A   │ B   │", harness.RowTrimmed(1));
        Assert.Equal("└─────┴─────┘", harness.RowTrimmed(4));
    }

    [Fact]
    public void EndToEnd_EditingACell_ReRendersTheTable()
    {
        using var harness = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 16);

        // Splice "ppp" into the body cell "1" (after the '1' at line 2, col 3): the reparse + reconcile
        // widens column 0 and re-renders the grid through the real pipeline.
        var caret = new CaretState(new TextPosition(2, 3));
        harness.Controller.Apply(new Edit(new TextPosition(2, 3), "", "ppp"), EditKind.Typing, caret, caret);
        harness.Settle();

        Assert.IsType<TablePresenter>(harness.Presenter(0));
        Assert.Contains("1ppp", harness.RowTrimmed(3)); // the edited cell content is rendered
        Assert.StartsWith("┌", harness.RowTrimmed(0));  // borders re-drawn and still aligned
    }
}
