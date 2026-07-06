using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Editing;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP7 — the table STRUCTURAL operations (spec §5.3): insert/delete/move rows and columns, set column
/// alignment, delete table, clear cell. Driven end-to-end through the real markdown surface
/// (<see cref="MarkdownEditingHarness"/>). Each op is asserted to (a) produce the expected GFM source line(s),
/// (b) re-parse (Markdig) to the intended <see cref="TableModel"/> shape (RowCount/ColumnCount/CellContent/
/// Alignment), (c) be ONE sealed undo group that restores the exact pre-op source + caret, and (d) land the
/// caret in the right cell. The [EDGE] cases (delete-header-promotes, delete-last-column-deletes-table,
/// move-can't-cross-delimiter, insert-column-defaults-left, move-column-carries-alignment) are covered explicitly.
/// </summary>
public sealed class TableStructuralTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── insert row ─────────────────────────────

    [Fact]
    public void InsertRowAbove_BodyRow_InsertsEmptyRow_CaretInIt()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 16);
        EnterCell(h, 1, 0); // body row 1 ("1")
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableInsertRowAbove();

        Assert.Equal("|  |  |", Line(h, 2));        // a fresh empty row spliced above the body row's line
        var model = Model(h);
        Assert.Equal(4, model.RowCount);            // header + empty + "1 2" + "3 4"
        Assert.Equal("", model.CellContent(1, 0));  // the new row is logical row 1
        Assert.Equal("1", model.CellContent(2, 0));
        Assert.Equal((1, 0), Cell(h));              // caret in the new row's first cell

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void InsertRowBelow_ViaAltDown_Keybind(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", preset, columns: 40, rows: 16);
        EnterCell(h, 1, 0); // body row 1 ("1")

        h.Key(Key.DownArrow, KeyModifiers.Alt); // Alt+↓ inserts a row below

        Assert.Equal("|  |  |", Line(h, 3));
        var model = Model(h);
        Assert.Equal(4, model.RowCount);
        Assert.Equal("1", model.CellContent(1, 0));
        Assert.Equal("", model.CellContent(2, 0)); // the new row is logical row 2 (below "1 2")
        Assert.Equal((2, 0), Cell(h));
    }

    [Fact]
    public void InsertRowAbove_ViaAltUp_Keybind()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);
        EnterCell(h, 1, 0);

        h.Key(Key.UpArrow, KeyModifiers.Alt); // Alt+↑ inserts a row above

        Assert.Equal(3, Model(h).RowCount);
        Assert.Equal((1, 0), Cell(h));
        Assert.Equal("", Model(h).CellContent(1, 0));
    }

    [Fact]
    public void InsertRow_OnHeader_InsertsFirstBodyRow_KeepingHeaderStructure()
    {
        // A body row cannot precede the header/delimiter — on the header, insert as the first BODY row.
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);
        EnterCell(h, 0, 0); // header

        h.Caret.TableInsertRowAbove();

        Assert.Equal("| A | B |", Line(h, 0));  // header untouched
        Assert.Equal("|---|---|", Line(h, 1));  // delimiter untouched
        Assert.Equal("|  |  |", Line(h, 2));    // new empty first body row (below the delimiter)
        var model = Model(h);
        Assert.Equal(3, model.RowCount);
        Assert.True(model.IsHeaderRow(0));
        Assert.Equal("A", model.CellContent(0, 0)); // still the header
        Assert.Equal("", model.CellContent(1, 0));  // the new first body row
        Assert.Equal((1, 0), Cell(h));
    }

    [Fact]
    public void InsertRow_OnHeaderOnlyTable_AddsFirstBodyRow()
    {
        using var h = MarkdownEditingHarness.Create("| H |\n|---|\n", columns: 40, rows: 12);
        EnterCell(h, 0, 0);

        h.Caret.TableInsertRowBelow();

        var model = Model(h);
        Assert.Equal(2, model.RowCount);        // header + the new body row
        Assert.Equal("H", model.CellContent(0, 0));
        Assert.Equal("", model.CellContent(1, 0));
        Assert.Equal((1, 0), Cell(h));
    }

    // ───────────────────────────── insert column ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void InsertColumnRight_InsertsEmptyColumn_DefaultsLeftAlign(string preset)
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 40, rows: 14);
        EnterCell(h, 0, 0); // column 0
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableInsertColumnRight();

        Assert.Equal("| A |  | B |", Line(h, 0));           // empty cell inserted into the header
        Assert.Equal("| --- | --- | --- |", Line(h, 1));    // and into the delimiter — the new marker defaults to "---"
        Assert.Equal("| 1 |  | 2 |", Line(h, 2));           // and into the body
        var model = Model(h);
        Assert.Equal(3, model.ColumnCount);
        Assert.Equal("A", model.CellContent(0, 0));
        Assert.Equal("", model.CellContent(0, 1));          // the new column
        Assert.Equal("B", model.CellContent(0, 2));
        Assert.Equal(ColumnAlignment.None, model.Alignment(1)); // "---" — default alignment, rendered left
        Assert.Equal((0, 1), Cell(h));                       // caret in the new column
        Assert.StartsWith("┌", h.RowTrimmed(0));             // borders re-drawn for the new column

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Fact]
    public void InsertColumnLeft_InsertsEmptyColumnBeforeCaretColumn()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);
        EnterCell(h, 0, 1); // column 1 ("B")

        h.Caret.TableInsertColumnLeft();

        Assert.Equal("| A |  | B |", Line(h, 0)); // the empty column lands at index 1 (left of "B")
        var model = Model(h);
        Assert.Equal(3, model.ColumnCount);
        Assert.Equal("", model.CellContent(0, 1));
        Assert.Equal("B", model.CellContent(0, 2));
        Assert.Equal((0, 1), Cell(h));
    }

    // ───────────────────────────── delete row ─────────────────────────────

    [Fact]
    public void DeleteRow_Body_RemovesLine_CaretInAdjacentRow()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 16);
        EnterCell(h, 1, 0); // body row 1 ("1")
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableDeleteRow();

        Assert.Equal("| 3 | 4 |", Line(h, 2)); // row "3 4" moved up into the deleted row's slot
        var model = Model(h);
        Assert.Equal(2, model.RowCount);
        Assert.Equal("3", model.CellContent(1, 0));
        Assert.Equal((1, 0), Cell(h)); // same column, adjacent row

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Fact]
    public void DeleteRow_Header_PromotesNextRowToHeader()
    {
        // [EDGE] a GFM table must lead with header + delimiter, so deleting the header promotes the next body row.
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 16);
        EnterCell(h, 0, 0); // header
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableDeleteRow();

        Assert.Equal("| 1 | 2 |", Line(h, 0)); // row 1 promoted to header
        Assert.Equal("|---|---|", Line(h, 1)); // delimiter still valid and in place
        Assert.Equal("| 3 | 4 |", Line(h, 2));
        var model = Model(h);
        Assert.Equal(2, model.RowCount);
        Assert.True(model.IsHeaderRow(0));
        Assert.Equal("1", model.CellContent(0, 0)); // the new header
        Assert.Equal((0, 0), Cell(h));              // caret in the (new) header, same column

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Fact]
    public void DeleteRow_OnlyRow_DeletesTable()
    {
        using var h = MarkdownEditingHarness.Create("| H |\n|---|\n", columns: 40, rows: 12);
        EnterCell(h, 0, 0);

        h.Caret.TableDeleteRow(); // header is the only row — nothing valid remains

        Assert.False(HasTable(h));
        Assert.Equal("", h.Buffer.GetText()); // header AND delimiter gone — no orphaned "|---|" line
    }

    [Fact]
    public void InsertColumn_OnHeaderOnlyTable_KeepsExactlyOneDelimiterRow()
    {
        // Header-only table (RowCount == 1): the delimiter is the last physical line, below the only model row.
        // A column op must rebuild across it — never leave the original delimiter behind as a garbage body row.
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n", columns: 40, rows: 12);
        EnterCell(h, 0, 0);

        h.Caret.TableInsertColumnRight();

        Assert.Equal("| A |  | B |", Line(h, 0));
        Assert.Equal("| --- | --- | --- |", Line(h, 1));
        Assert.Equal("", Line(h, 2));      // no stray leftover delimiter line
        var model = Model(h);
        Assert.Equal(1, model.RowCount);   // still a header-only table
        Assert.Equal(3, model.ColumnCount);
        Assert.Equal((0, 1), Cell(h));
    }

    [Fact]
    public void DeleteTable_OnHeaderOnlyTable_RemovesDelimiterToo()
    {
        using var h = MarkdownEditingHarness.Create("| H |\n|---|\n\ntail\n", columns: 40, rows: 14);
        EnterCell(h, 0, 0);

        h.Caret.TableDelete();

        Assert.False(HasTable(h));
        Assert.DoesNotContain("---", h.Buffer.GetText()); // the delimiter went with the header
        Assert.Equal("\ntail\n", h.Buffer.GetText());
    }

    // ───────────────────────────── delete column ─────────────────────────────

    [Fact]
    public void DeleteColumn_RemovesColumnFromEveryRow_CaretInAdjacentColumn()
    {
        using var h = MarkdownEditingHarness.Create("| A | B | C |\n|---|---|---|\n| 1 | 2 | 3 |\n", columns: 40, rows: 14);
        EnterCell(h, 0, 1); // column 1 ("B")
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableDeleteColumn();

        Assert.Equal("| A | C |", Line(h, 0));       // "B" removed from the header
        Assert.Equal("| --- | --- |", Line(h, 1));   // and the delimiter marker removed too (one per column)
        Assert.Equal("| 1 | 3 |", Line(h, 2));
        var model = Model(h);
        Assert.Equal(2, model.ColumnCount);
        Assert.Equal("C", model.CellContent(0, 1));
        Assert.Equal((0, 1), Cell(h)); // the column that was to the right slides into the deleted slot

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Fact]
    public void DeleteColumn_LastColumn_DeletesTable()
    {
        // [EDGE] deleting the only column deletes the whole table.
        using var h = MarkdownEditingHarness.Create("| A |\n|---|\n| 1 |\n", columns: 40, rows: 12);
        EnterCell(h, 0, 0);

        h.Caret.TableDeleteColumn();

        Assert.False(HasTable(h));
        Assert.Equal("", h.Buffer.GetText()); // the whole table's source is gone
    }

    // ───────────────────────────── move row ─────────────────────────────

    [Fact]
    public void MoveRowDown_SwapsWithNeighbour_CaretFollows()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 16);
        EnterCell(h, 1, 0); // body row 1 ("1")
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableMoveRowDown();

        Assert.Equal("| 3 | 4 |", Line(h, 2));
        Assert.Equal("| 1 | 2 |", Line(h, 3)); // "1 2" moved down
        var model = Model(h);
        Assert.Equal(3, model.RowCount);        // count unchanged
        Assert.Equal("1", model.CellContent(2, 0));
        Assert.Equal((2, 0), Cell(h));          // caret followed the moved row

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Fact]
    public void MoveRowUp_SwapsWithNeighbour_CaretFollows()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 16);
        EnterCell(h, 2, 0); // body row 2 ("3")

        h.Caret.TableMoveRowUp();

        Assert.Equal("| 3 | 4 |", Line(h, 2)); // "3 4" moved up
        Assert.Equal("| 1 | 2 |", Line(h, 3));
        Assert.Equal("3", Model(h).CellContent(1, 0));
        Assert.Equal((1, 0), Cell(h));
    }

    [Fact]
    public void MoveRow_CannotCrossDelimiterOrMoveHeader()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 40, rows: 16);

        // The first body row cannot move up above the delimiter.
        EnterCell(h, 1, 0);
        var (text1, caret1) = Snapshot(h);
        h.Caret.TableMoveRowUp();
        Assert.Equal(text1, h.Buffer.GetText()); // no-op
        Assert.Equal(caret1, h.Caret.Position);

        // The header does not move.
        EnterCell(h, 0, 0);
        var text2 = h.Buffer.GetText();
        h.Caret.TableMoveRowDown();
        Assert.Equal(text2, h.Buffer.GetText()); // no-op
    }

    // ───────────────────────────── move column ─────────────────────────────

    [Fact]
    public void MoveColumnRight_CarriesAlignment()
    {
        // Column 0 is left-aligned, column 1 right-aligned; moving column 0 right must carry the alignment.
        using var h = MarkdownEditingHarness.Create("| A | B |\n| :--- | ---: |\n| 1 | 2 |\n", columns: 40, rows: 14);
        var before = Model(h);
        Assert.Equal(ColumnAlignment.Left, before.Alignment(0));
        Assert.Equal(ColumnAlignment.Right, before.Alignment(1));

        EnterCell(h, 0, 0); // column 0 (Left)
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableMoveColumnRight();

        Assert.Equal("| B | A |", Line(h, 0)); // columns swapped in the data rows
        Assert.Equal("| 2 | 1 |", Line(h, 2));
        var model = Model(h);
        Assert.Equal(ColumnAlignment.Right, model.Alignment(0)); // alignment travelled with the columns
        Assert.Equal(ColumnAlignment.Left, model.Alignment(1));
        Assert.Equal("A", model.CellContent(0, 1));
        Assert.Equal((0, 1), Cell(h)); // caret followed its column to index 1

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    [Fact]
    public void MoveColumnLeft_AtLeftEdge_IsNoOp()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);
        EnterCell(h, 0, 0);
        var text = h.Buffer.GetText();

        h.Caret.TableMoveColumnLeft();

        Assert.Equal(text, h.Buffer.GetText()); // already at the left edge — no-op
    }

    // ───────────────────────────── set alignment ─────────────────────────────

    [Fact]
    public void SetAlignment_UpdatesOnlyDelimiter_AndRoundTrips()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 14);
        EnterCell(h, 0, 1); // column 1
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableSetColumnAlignment(ColumnAlignment.Center);

        Assert.Equal("| A | B |", Line(h, 0));       // data rows untouched
        Assert.Equal("| --- | :---: |", Line(h, 1)); // only the delimiter changed — column 1 now centered
        Assert.Equal("| 1 | 2 |", Line(h, 2));
        var model = Model(h);
        Assert.Equal(ColumnAlignment.None, model.Alignment(0));
        Assert.Equal(ColumnAlignment.Center, model.Alignment(1));

        // Round-trip: re-open the saved source into a fresh surface — the alignment persists.
        using var reopened = MarkdownEditingHarness.Create(h.Buffer.GetText(), columns: 40, rows: 14);
        Assert.Equal(ColumnAlignment.Center, Model(reopened).Alignment(1));

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    // ───────────────────────────── delete table ─────────────────────────────

    [Fact]
    public void DeleteTable_RemovesWholeConstruct_CaretWhereItWas()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n\ntail\n", columns: 40, rows: 16);
        EnterCell(h, 1, 0);
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableDelete();

        Assert.False(HasTable(h));
        Assert.Equal("tail", Line(h, 1));     // the content that followed the table survives
        Assert.False(h.Caret.IsInTable);      // caret left the table
        Assert.Equal(0, h.Caret.Position.Line); // landed where the table's top was

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    // ───────────────────────────── clear cell (WP4 reuse) ─────────────────────────────

    [Fact]
    public void ClearCell_EmptiesContent_KeepsStructure_OneUndoGroup()
    {
        using var h = MarkdownEditingHarness.Create("| abc | B |\n|---|---|\n| 1 | 2 |\n", columns: 40, rows: 12);
        EnterCell(h, 0, 0);
        var (beforeText, beforeCaret) = Snapshot(h);

        h.Caret.TableClearCell();

        var model = Model(h);
        Assert.Equal(2, model.ColumnCount);
        Assert.Equal("", model.CellContent(0, 0));
        Assert.Equal("B", model.CellContent(0, 1));

        AssertOneUndoRestores(h, beforeText, beforeCaret);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static string Line(MarkdownEditingHarness h, int line) => h.Buffer.GetLine(line).Text;

    private static TableModel Model(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
        {
            if (h.Blocks[i].Kind == BlockKind.Table)
                return h.Bridge.GetTableModel(i)!;
        }

        throw new Xunit.Sdk.XunitException("no table block");
    }

    private static bool HasTable(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
        {
            if (h.Blocks[i].Kind == BlockKind.Table)
                return true;
        }

        return false;
    }

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

    /// <summary>Places the caret in cell (<paramref name="row"/>, <paramref name="column"/>) by entering the table and navigating deterministically.</summary>
    private static void EnterCell(MarkdownEditingHarness h, int row, int column)
    {
        h.Click(2, 1); // land in the header's first cell (0,0)
        Assert.Equal((0, 0), Cell(h));

        for (var r = 0; r < row; r++)
            h.Key(Key.DownArrow);
        for (var c = 0; c < column; c++)
            h.Key(Key.Tab);

        Assert.Equal((row, column), Cell(h));
    }

    private static (string Text, TextPosition Caret) Snapshot(MarkdownEditingHarness h)
        => (h.Buffer.GetText(), h.Caret.Position);

    /// <summary>Asserts that a single undo restores the exact pre-op source and caret (proving the op was ONE undo group).</summary>
    private static void AssertOneUndoRestores(MarkdownEditingHarness h, string beforeText, TextPosition beforeCaret)
    {
        h.Chord('z', KeyModifiers.Control);
        Assert.Equal(beforeText, h.Buffer.GetText());
        Assert.Equal(beforeCaret, h.Caret.Position);
    }
}
