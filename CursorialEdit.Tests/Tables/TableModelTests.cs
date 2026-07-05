using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP1 — the <see cref="TableModel"/> overlay: cell-span extraction from Markdig's precise source
/// locations (risk d — never a hand pipe-scanner), per-column GFM alignment, the cell-measured width
/// cache <c>(WidthCells, MaxContentWidth, CountAtMax)</c> (Decision 11), and the cell-layout /
/// visual-row pass (risk a). Every fixture in the plan's done-when is covered: ASCII, CJK (wide),
/// emoji/ZWJ, escaped-pipe, backtick-pipe, alignment variants, a cell wider than 40 (wraps), and a
/// ragged table. Cell-span extraction reproducing the exact source slice is asserted per cell.
/// </summary>
public class TableModelTests
{
    /// <summary>Parses a markdown table and builds the overlay over the (located) table block's serialized source.</summary>
    private static TableModel Build(string markdown)
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
        var model = TableModel.Build(harness.Blocks[index], harness.TextOf(index));
        Assert.NotNull(model);
        return model!;
    }

    // ───────────────────────────── cell-span extraction (risk d) ─────────────────────────────

    [Fact]
    public void Ascii_CellSpansReproduceExactSource_IncludingPadding()
    {
        var model = Build("| A | B |\n|---|---|\n| 1 | 22 |\n");

        Assert.Equal(2, model.ColumnCount);
        Assert.Equal(2, model.RowCount);

        // Markdig's cell boundary for plain text is the inter-pipe slice, padding included.
        Assert.Equal(" A ", model.CellSource(0, 0));
        Assert.Equal(" B ", model.CellSource(0, 1));
        Assert.Equal(" 1 ", model.CellSource(1, 0));
        Assert.Equal(" 22 ", model.CellSource(1, 1));

        // The trimmed content is what renders.
        Assert.Equal("A", model.CellContent(0, 0));
        Assert.Equal("22", model.CellContent(1, 1));
    }

    [Fact]
    public void HeaderAndBodyRows_AreClassified_DelimiterRowSkipped()
    {
        var model = Build("| A | B |\n|---|---|\n| 1 | 2 |\n");

        Assert.True(model.IsHeaderRow(0));
        Assert.False(model.IsHeaderRow(1));
        Assert.Equal(0, model.RowSourceLine(0)); // header on line 0
        Assert.Equal(2, model.RowSourceLine(1)); // body on line 2 (the |---| delimiter line 1 is not a row)
    }

    [Fact]
    public void EscapedPipe_StaysOneCell_SliceReproducesBackslashPipe()
    {
        var model = Build("| a \\| b | c |\n|---|---|\n| x | y |\n");

        Assert.Equal(2, model.ColumnCount); // the \| did NOT split the cell
        Assert.Equal(" a \\| b ", model.CellSource(0, 0));
        Assert.Equal("a \\| b", model.CellContent(0, 0));
    }

    [Fact]
    public void BacktickPipe_StaysOneCell_SliceReproducesCodeSpan()
    {
        var model = Build("| `a|b` | c |\n|---|---|\n| x | y |\n");

        Assert.Equal(2, model.ColumnCount); // the |-in-backticks did NOT split the cell
        Assert.Equal("`a|b`", model.CellSource(0, 0)); // Markdig trims the padding around a code span
        Assert.Equal("`a|b`", model.CellContent(0, 0));
    }

    // ───────────────────────────── alignment ─────────────────────────────

    [Fact]
    public void Alignment_FromDelimiterRow()
    {
        var model = Build("| L | C | R |\n|:--|:-:|--:|\n| a | b | c |\n");

        Assert.Equal(ColumnAlignment.Left, model.Alignment(0));
        Assert.Equal(ColumnAlignment.Center, model.Alignment(1));
        Assert.Equal(ColumnAlignment.Right, model.Alignment(2));
    }

    [Fact]
    public void Alignment_None_ForPlainDelimiter()
    {
        var model = Build("| A | B |\n|---|---|\n| a | b |\n");

        Assert.Equal(ColumnAlignment.None, model.Alignment(0));
        Assert.Equal(ColumnAlignment.None, model.Alignment(1));
    }

    // ───────────────────────────── width cache (Decision 11) ─────────────────────────────

    [Fact]
    public void ColumnWidth_ClampsToMinAndMax()
    {
        var model = Build("| a | " + new string('x', 50) + " |\n|---|---|\n| bb | c |\n");

        // Column 0: widest content is "bb" (2) — clamped up to the min of 3.
        Assert.Equal(2, model.MaxContentWidth(0));
        Assert.Equal(3, model.ColumnWidth(0));

        // Column 1: widest content is the 50-x cell — clamped down to the max of 40.
        Assert.Equal(50, model.MaxContentWidth(1));
        Assert.Equal(40, model.ColumnWidth(1));
    }

    [Fact]
    public void CountAtMax_TracksTheUniqueWidest_ForShrinkDetection()
    {
        // Column 0: "aaaa" (4) is the unique widest → CountAtMax 1 (a shrink recomputes the column).
        // Column 1: "bbbb" and "dddd" tie at 4 → CountAtMax 2 (a shrink of one is masked by the other).
        var model = Build("| aaaa | bbbb |\n|---|---|\n| cc | dddd |\n");

        Assert.Equal(4, model.MaxContentWidth(0));
        Assert.Equal(1, model.CountAtMax(0));

        Assert.Equal(4, model.MaxContentWidth(1));
        Assert.Equal(2, model.CountAtMax(1));
    }

    // ───────────────────────────── CJK / emoji width (§5.1 [CRITICAL]) ─────────────────────────────

    [Fact]
    public void Cjk_WideCellsMeasuredInCellsNotChars()
    {
        var model = Build("| 名前 | 年齢 |\n|---|---|\n| 田中 | 30 |\n");

        Assert.Equal("名前", model.CellContent(0, 0));
        Assert.Equal(4, model.MaxContentWidth(0)); // two wide glyphs = 4 cells, not 2 chars
        Assert.Equal(2, model.CountAtMax(0));       // 名前 and 田中 both 4 wide
        Assert.Equal(4, model.ColumnWidth(0));
    }

    [Fact]
    public void EmojiZwj_IsOneClusterNeverSplit()
    {
        var model = Build("| 👨‍👩‍👧 | x |\n|---|---|\n| a | b |\n");

        // The ZWJ family is one grapheme measured as 2 cells — not split, and not width-1-per-codepoint.
        Assert.Equal(2, model.MaxContentWidth(0));
        Assert.Equal(3, model.ColumnWidth(0)); // clamped up to the min

        var layout = model.LayoutRow(0);
        var fragment = layout.VisualRows[0].Cell(0);
        Assert.Equal(1, layout.VisualRowCount);   // one grapheme → one fragment, one visual row
        Assert.Equal(2, fragment.Width);
        Assert.Equal("👨‍👩‍👧", model.CellContent(0, 0));
    }

    // ───────────────────────────── the cell-layout pass (risk a) ─────────────────────────────

    [Fact]
    public void LayoutRow_MapsFragmentToCellSource()
    {
        var model = Build("| A | B |\n|---|---|\n| 1 | 2 |\n");
        var layout = model.LayoutRow(0);

        Assert.Equal(1, layout.VisualRowCount);
        // " A " begins at block offset 1; the trimmed "A" content is at offset 2, length 1, width 1.
        var cell = layout.VisualRows[0].Cell(0);
        Assert.Equal(2, cell.SrcStart);
        Assert.Equal(1, cell.SrcLength);
        Assert.Equal(1, cell.Width);
    }

    [Fact]
    public void LayoutRow_WrapsCellWiderThanColumn_ToMultipleVisualRows()
    {
        var model = Build("| short | " + new string('x', 50) + " |\n|---|---|\n| a | b |\n");

        Assert.Equal(40, model.ColumnWidth(1));
        var layout = model.LayoutRow(0);

        // The 50-cell header cell wraps to 40 + 10 across two visual rows; "short" occupies only the first.
        Assert.Equal(2, layout.VisualRowCount);
        Assert.Equal(40, layout.VisualRows[0].Cell(1).Width);
        Assert.Equal(10, layout.VisualRows[1].Cell(1).Width);
        Assert.False(layout.VisualRows[0].Cell(0).IsEmpty);
        Assert.True(layout.VisualRows[1].Cell(0).IsEmpty); // "short" has no second fragment
    }

    [Fact]
    public void LayoutRow_WrapNeverSplitsAGraphemeCluster()
    {
        // A column of wide (2-cell) CJK glyphs whose content exceeds the max: every fragment width is even,
        // so no fragment ever ends mid-cluster (a split would leave an odd boundary).
        var model = Build("| " + new string('中', 30) + " | x |\n|---|---|\n| a | b |\n");

        Assert.Equal(40, model.ColumnWidth(0)); // 30 glyphs × 2 = 60 cells → clamped to 40
        var layout = model.LayoutRow(0);
        Assert.True(layout.VisualRowCount >= 2);

        int total = 0;
        foreach (var visualRow in layout.VisualRows)
        {
            int width = visualRow.Cell(0).Width;
            Assert.True(width == 0 || width % 2 == 0, $"fragment width {width} split a 2-cell cluster");
            Assert.True(width <= 40, $"fragment width {width} exceeds the column");
            total += width;
        }

        Assert.Equal(60, total); // all 30 glyphs accounted for, none dropped or duplicated
    }

    // ───────────────────────────── ragged tables ─────────────────────────────

    [Fact]
    public void Ragged_ColumnCountIsTheWidestRow_ShortRowsGetEmptyCells()
    {
        var model = Build("| A | B | C |\n|---|---|---|\n| 1 | 2 |\n| 7 | 8 | 9 | 10 |\n");

        Assert.Equal(4, model.ColumnCount); // the 4-cell body row widens the table past the 3-column header
        Assert.Equal(3, model.RowCount);    // header + 2 body rows

        Assert.Equal(" 1 ", model.CellSource(1, 0));
        Assert.True(model.Cells(1)[2].IsEmpty);        // the short row's missing cells are empty
        Assert.Equal("", model.CellSource(1, 3));
        Assert.Equal(" 10 ", model.CellSource(2, 3));  // the excess column's real cell

        Assert.Equal(ColumnAlignment.None, model.Alignment(3)); // the ragged-excess column has no delimiter
    }

    [Fact]
    public void EmptyLeadingCell_IsEmptyNotWhitespace()
    {
        var model = Build("| | b |\n|---|---|\n| | |\n");

        Assert.True(model.Cells(0)[0].IsEmpty);
        Assert.Equal("", model.CellSource(0, 0));
        Assert.Equal("b", model.CellContent(0, 1));
        Assert.Equal(3, model.ColumnWidth(0)); // all-empty column clamps to the min
    }

    [Fact]
    public void NoLeadingPipe_TableStillExtractsCells()
    {
        var model = Build("A | B\n---|---\n1 | 2\n");

        Assert.Equal(2, model.ColumnCount);
        Assert.Equal("A ", model.CellSource(0, 0)); // no leading pipe → cell starts at offset 0
        Assert.Equal("A", model.CellContent(0, 0));
        Assert.Equal(" B", model.CellSource(0, 1));
    }

    [Fact]
    public void Build_ReturnsNull_ForNonTableBlock()
    {
        var harness = BlockHarness.Create("just a paragraph\n");
        Assert.Null(TableModel.Build(harness.Blocks[0], harness.TextOf(0)));
    }
}
