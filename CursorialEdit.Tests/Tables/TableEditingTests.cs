using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP4 — the table cell-editing controller and caret-in-cell integration (spec §5.3 / §5.4,
/// architecture Decision 11). Driven end-to-end through the real markdown surface
/// (<see cref="MarkdownEditingHarness"/>): the caret lands inside a cell on click/arrow, intra-cell
/// typing/backspace/delete splice on the cell source with pipe escaping, Tab/Shift+Tab navigate cells
/// (last-cell Tab appends a row), Enter commits downward, paste is sanitised, and every result re-parses
/// (Markdig) to the expected table shape — the §5.3 keyboard-driven acceptance.
/// </summary>
public sealed class TableEditingTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private static string Line(MarkdownEditingHarness h, int line) => h.Buffer.GetLine(line).Text;

    /// <summary>The live table overlay for the first table block (the resulting-shape re-parse oracle).</summary>
    private static TableModel Model(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
        {
            if (h.Blocks[i].Kind == BlockKind.Table)
                return h.Bridge.GetTableModel(i)!;
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    // ───────────────────────────── 0. empty-cell insertion (spike review #6, risk d) ─────────────────────────────

    [Fact]
    public void EmptyMiddleCell_TypingInsertsAtTheCell_NotTheBlockStart()
    {
        using var h = MarkdownEditingHarness.Create("| a | | c |\n|---|---|---|\n", columns: 40, rows: 8);

        // Enter cell (0,0), Tab into the empty middle cell (0,1), then type.
        h.Click(2, 1);
        h.Key(Key.Tab);
        h.Type("X");

        Assert.Equal("| a | X | c |", Line(h, 0)); // inserted AT the empty cell, block start untouched
        var model = Model(h);
        Assert.Equal("a", model.CellContent(0, 0));
        Assert.Equal("X", model.CellContent(0, 1)); // re-parses to the expected middle-cell content
        Assert.Equal("c", model.CellContent(0, 2));
    }

    // ───────────────────────────── 1. caret lands in the right cell ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Click_LandsCaretInsideTheCell(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 40, rows: 12);

        h.Click(2, 3); // window cell 2, grid row 3 = the body cell "1"

        h.AssertCaret(2, 2); // source line 2 ("| 1 | 2 |"), col 2 = the '1'
        Assert.Equal((2, 3), h.Cursor); // terminal caret sits in the cell, not on the raw source line
    }

    [Fact]
    public void ArrowDown_FromHeader_EntersCellBelow_SameColumn()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(8, 1);        // header cell (0,1) "B"
        h.AssertCaret(0, 6);  // '| A   | B' → 'B' at col 6
        h.Key(Key.DownArrow); // move to the cell below in the same column
        h.AssertCaret(2, 6);  // body cell (1,1) "2" — same column
    }

    [Fact]
    public void ArrowDown_FromParagraphAbove_EntersNearestCellByGoalColumn()
    {
        using var h = MarkdownEditingHarness.Create("hi\n\n| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);

        h.Click(0, 0);        // start of the paragraph "hi" (goal column 0)
        h.Key(Key.DownArrow); // onto the blank line
        h.Key(Key.DownArrow); // onto the table's top border → nearest cell by goal column

        int blockIndex = h.Blocks.IndexOfLine(h.Caret.Position.Line);
        Assert.Equal(BlockKind.Table, h.Blocks[blockIndex].Kind); // the caret entered the table
        Assert.Equal(0, Model(h).CellOfOffset(TableRel(h))!.Value.Column); // leftmost cell (goal column 0)
    }

    // ───────────────────────────── 2. intra-cell typing grows + reflows ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Typing_GrowsTheCell_BordersStayAligned(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 40, rows: 12);

        h.Click(3, 3);     // just after the '1' in the body cell
        h.Type("pp");      // widen column 0

        Assert.Equal("| 1pp | 2 |", Line(h, 2));
        Assert.Equal("1pp", Model(h).CellContent(1, 0));
        Assert.StartsWith("┌", h.RowTrimmed(0)); // borders re-drawn
        Assert.Contains("1pp", h.RowTrimmed(3)); // the grown cell content is rendered inside the box
    }

    [Fact]
    public void TypedPipe_IsEscaped_AndStaysInCell()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(3, 3); // after the '1'
        h.Type("|");

        Assert.Equal("| 1\\| | 2 |", Line(h, 2)); // the typed '|' became '\|'
        var model = Model(h);
        Assert.Equal(2, model.ColumnCount);        // still two columns — the '|' did not split the cell
        Assert.Equal("1\\|", model.CellContent(1, 0));
    }

    // ───────────────────────────── 3. backspace / delete within a cell ─────────────────────────────

    [Fact]
    public void Backspace_DeletesWithinCell_BoundedAtCellStart()
    {
        using var h = MarkdownEditingHarness.Create("| abc | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(2, 1);        // before 'a' in the header cell "abc"
        h.Key(Key.RightArrow);
        h.Key(Key.RightArrow); // now after 'b' (between b and c)
        h.Key(Key.Backspace);  // deletes 'b'

        Assert.Equal("| ac | B |", Line(h, 0));

        // Move to cell start and Backspace again — bounded, does not merge the cell into the pipe.
        h.Key(Key.LeftArrow);
        h.Key(Key.Backspace);
        Assert.Equal("| ac | B |", Line(h, 0)); // unchanged: at cell content start, nothing to delete
    }

    [Fact]
    public void Delete_RemovesForwardWithinCell()
    {
        using var h = MarkdownEditingHarness.Create("| abc | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(2, 1);       // before 'a'
        h.Key(Key.Delete);   // deletes 'a'

        Assert.Equal("| bc | B |", Line(h, 0));
    }

    // ───────────────────────────── 4. Tab / Shift+Tab navigation ─────────────────────────────

    [Fact]
    public void Tab_MovesToNextCell_WrappingRows()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(2, 1); // header cell (0,0)
        Assert.Equal((0, 0), Cell(h));
        h.Key(Key.Tab);
        Assert.Equal((0, 1), Cell(h)); // next cell in the header
        h.Key(Key.Tab);
        Assert.Equal((1, 0), Cell(h)); // wrapped to the first cell of the body row
        h.Key(Key.Tab, KeyModifiers.Shift);
        Assert.Equal((0, 1), Cell(h)); // Shift+Tab back across the row boundary
    }

    [Fact]
    public void Tab_InLastCellOfLastRow_AppendsRow_AndEnters()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);

        h.Click(8, 3); // body cell (1,1) "2" (the last cell of the last row)
        Assert.Equal((1, 1), Cell(h));
        h.Key(Key.Tab);

        // A new empty row is appended and the caret enters its first cell.
        var model = Model(h);
        Assert.Equal(3, model.RowCount); // header + original body + the new row
        Assert.Equal((2, 0), Cell(h));
        Assert.Equal("", model.CellContent(2, 0));
        h.Type("z"); // typing lands in the fresh row's first cell
        Assert.Equal("z", Model(h).CellContent(2, 0));
    }

    // ───────────────────────────── 5. Enter commits downward / exits ─────────────────────────────

    [Fact]
    public void Enter_CommitsDownward_SameColumn()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(8, 1); // header cell (0,1)
        h.Key(Key.Enter);
        Assert.Equal((1, 1), Cell(h)); // the cell below, same column
        Assert.Equal("| 1 | 2 |", Line(h, 2)); // no newline inserted — structure intact
        Assert.Equal(2, Model(h).RowCount);
    }

    [Fact]
    public void Enter_OnLastRow_ExitsTableBelow()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n\ntail\n", columns: 40, rows: 14);

        h.Click(2, 3); // body cell (1,0), the last row
        h.Key(Key.Enter);

        int blockIndex = h.Blocks.IndexOfLine(h.Caret.Position.Line);
        Assert.NotEqual(BlockKind.Table, h.Blocks[blockIndex].Kind); // left the table
        Assert.Equal("tail", Line(h, h.Caret.Position.Line));         // landed on the block below
    }

    // ───────────────────────────── 6. paste sanitisation ─────────────────────────────

    [Fact]
    public void Paste_ConvertsNewlinesToSpaces_AndEscapesPipes()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(3, 3); // after the '1' in the body cell
        h.Caret.Paste("x|y\nz");

        Assert.Equal("| 1x\\|y z | 2 |", Line(h, 2)); // '|' → '\|', newline → space, all in one cell
        var model = Model(h);
        Assert.Equal(2, model.ColumnCount);
        Assert.Equal(2, model.RowCount); // header + the one body row — the multiline paste did not add rows
        Assert.Equal("1x\\|y z", model.CellContent(1, 0));
    }

    // ───────────────────────────── 7. undo grouping + cell focus ─────────────────────────────

    [Fact]
    public void IntraCellTyping_IsOneCoalescedUndoGroup_UndoRestoresCellFocus()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(3, 3); // after the '1'
        var before = h.Caret.Position;
        h.Type("a");
        h.Type("b");
        h.Type("c");
        Assert.Equal("| 1abc | 2 |", Line(h, 2));

        h.Chord('z', KeyModifiers.Control); // one undo removes the whole coalesced run
        Assert.Equal("| 1 | 2 |", Line(h, 2));
        Assert.Equal(before, h.Caret.Position); // cell focus (the source offset) restored
        Assert.Equal((1, 0), Cell(h));          // still in the same cell
    }

    [Fact]
    public void TabAppendRow_IsOneSealedUndoGroup()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);

        h.Click(8, 3); // last cell
        h.Key(Key.Tab); // appends a row
        Assert.Equal(3, Model(h).RowCount);

        h.Chord('z', KeyModifiers.Control); // one undo removes the appended row
        Assert.Equal(2, Model(h).RowCount);
    }

    // ───────────────────────────── 8. clear cell + cell break ─────────────────────────────

    [Fact]
    public void ClearCell_EmptiesContent_KeepsStructure()
    {
        using var h = MarkdownEditingHarness.Create("| abc | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(2, 1); // header cell (0,0) "abc"
        h.Caret.TableClearCell();

        var model = Model(h);
        Assert.Equal(2, model.ColumnCount);        // structure kept
        Assert.Equal("", model.CellContent(0, 0)); // content cleared
        Assert.Equal("B", model.CellContent(0, 1));
    }

    [Fact]
    public void InsertCellBreak_InsertsLiteralBr()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);

        h.Click(3, 3); // after the '1'
        h.Caret.TableInsertCellBreak();

        Assert.Contains("<br>", Line(h, 2)); // a literal cell break was inserted in-cell
        Assert.Equal(2, Model(h).ColumnCount);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static int TableRel(MarkdownEditingHarness h)
    {
        int blockIndex = h.Blocks.IndexOfLine(h.Caret.Position.Line);
        int blockStart = h.Buffer.GetOffset(new TextPosition(h.Blocks.GetStartLine(blockIndex), 0));
        return h.Buffer.GetOffset(h.Caret.Position) - blockStart;
    }

    private static (int Row, int Col) Cell(MarkdownEditingHarness h)
    {
        var rc = Model(h).CellOfOffset(TableRel(h))!.Value;
        return (rc.Row, rc.Column);
    }
}
