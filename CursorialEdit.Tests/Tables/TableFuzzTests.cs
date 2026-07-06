using Cursorial.Input;

using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Tables;

/// <summary>
/// M3.WP11 — round-trip + fuzz closure for the table stack. Drives long randomized sequences of the WP4–8
/// operations (structural insert/delete/move of rows &amp; columns, alignment, clear-cell, and raw cell typing
/// including the inline marks <c>* ` | ~ [</c> that exercise pipe-escaping + the WYSIWYG cell projection)
/// end-to-end through the real markdown surface, asserting after EVERY step that the table remains a
/// well-formed, fully-addressable GFM table (it still re-parses via Markdig to a <see cref="TableModel"/> whose
/// every cell is accessible) and that no operation throws. The only sanctioned way for the table to disappear
/// is a delete of its last row or column; a table that vanishes for any other reason is a corruption and fails.
/// The two regression tests below pin the bugs this fuzz first surfaced.
/// </summary>
public sealed class TableFuzzTests
{
    // Each seed runs a fixed-length op walk. (Counts kept modest for the per-PR lane — every step is a full
    // re-parse + render; raise STEPS / the seed count locally for a deeper soak.)
    private const int Steps = 60;

    public static IEnumerable<object[]> Seeds => Enumerable.Range(0, 20).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomOpWalk_KeepsTheTableGfmValidAndCrashFree(int seed)
    {
        var rng = new Random(seed);
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |\n", columns: 60, rows: 24);
        h.Click(2, 1); // land in the table (header cell 0,0)

        for (var step = 0; step < Steps; step++)
        {
            if (!HasTable(h))
                break; // a delete removed the last row/column — this walk is done

            GotoRandomCell(h, rng);
            var (op, action) = PickOp(h, rng);
            try { action(); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"seed {seed} step {step} op '{op}' threw {ex.GetType().Name}: {ex.Message}\nstate:\n{Dump(h)}"); }

            if (HasTable(h))
                AssertConsistent(Model(h), seed, step, op);
            else
                Assert.True(op is "DeleteRow" or "DeleteColumn",
                    $"seed {seed} step {step}: op '{op}' made the table vanish — corruption, not a delete\nstate:\n{Dump(h)}");
        }
    }

    [Fact] // Tab past the last header cell of a header-only table appends the body row AFTER the delimiter, not before it.
    public void TabPastLastHeaderCell_HeaderOnly_AppendsBodyRowAfterTheDelimiter()
    {
        using var h = MarkdownEditingHarness.Create("| A | B |\n|---|---|\n", columns: 40, rows: 12);
        h.Click(2, 1);   // header cell (0,0)
        h.Key(Key.Tab);  // -> (0,1)
        h.Key(Key.Tab);  // past the last cell -> append a body row

        Assert.Equal("| A | B |", h.Buffer.GetLine(0).Text);       // header unchanged
        Assert.Contains("---", h.Buffer.GetLine(1).Text);          // the delimiter STAYS the second line
        Assert.True(HasTable(h));
        Assert.Equal(2, Model(h).RowCount);                        // header + the new body row (which landed at line 2)
    }

    [Fact] // An all-empty table still resolves every row's source line (the header anchors the interpolation), so line-of-row ops don't crash.
    public void AllEmptyTable_ResolvesRowSourceLines_AndDeleteRowDoesNotCrash()
    {
        using var h = MarkdownEditingHarness.Create("|  |  |\n|---|---|\n|  |  |\n|  |  |\n", columns: 40, rows: 14);

        var m = Model(h);
        for (var r = 0; r < m.RowCount; r++)
            Assert.True(m.RowSourceLine(r) >= 0, $"row {r} has an unresolved SourceLine {m.RowSourceLine(r)}");

        h.Click(2, 3);            // a body row
        h.Caret.TableDeleteRow(); // must not throw (was: GetLine(-1) on the -1 source line)
        Assert.True(HasTable(h));
        Assert.Equal(2, Model(h).RowCount);
    }

    /// <summary>Every logical cell of the re-parsed model is present and addressable (the round-trip invariant: source → Markdig → model with no gaps).</summary>
    private static void AssertConsistent(TableModel m, int seed, int step, string op)
    {
        string ctx = $"seed {seed} step {step} after {op}";
        Assert.True(m.RowCount >= 1, $"{ctx}: RowCount={m.RowCount}");
        Assert.True(m.ColumnCount >= 1, $"{ctx}: ColumnCount={m.ColumnCount}");
        for (var r = 0; r < m.RowCount; r++)
        {
            Assert.True(m.RowSourceLine(r) >= 0, $"{ctx}: row {r} SourceLine={m.RowSourceLine(r)}");
            for (var c = 0; c < m.ColumnCount; c++)
                _ = m.CellContent(r, c); // throws if a cell is missing/ragged → the op corrupted the grid
        }
    }

    private static (string Label, Action Op) PickOp(MarkdownEditingHarness h, Random rng)
    {
        switch (rng.Next(13))
        {
            case 0: return ("InsertRowAbove", h.Caret.TableInsertRowAbove);
            case 1: return ("InsertRowBelow", h.Caret.TableInsertRowBelow);
            case 2: return ("InsertColumnLeft", h.Caret.TableInsertColumnLeft);
            case 3: return ("InsertColumnRight", h.Caret.TableInsertColumnRight);
            case 4: return ("DeleteRow", h.Caret.TableDeleteRow);
            case 5: return ("DeleteColumn", h.Caret.TableDeleteColumn);
            case 6: return ("MoveRowUp", h.Caret.TableMoveRowUp);
            case 7: return ("MoveRowDown", h.Caret.TableMoveRowDown);
            case 8: return ("MoveColumnLeft", h.Caret.TableMoveColumnLeft);
            case 9: return ("MoveColumnRight", h.Caret.TableMoveColumnRight);
            case 10: var a = RandomAlignment(rng); return ("SetAlignment", () => h.Caret.TableSetColumnAlignment(a));
            case 11: return ("ClearCell", h.Caret.TableClearCell);
            default:
                // Raw typing into the active (revealed) cell — the pipe exercises escaping, the emphasis /
                // code / strike / link marks exercise the cell inline projection under mutation.
                char ch = TypeChars[rng.Next(TypeChars.Length)];
                return ("Type", () => h.Type(ch.ToString()));
        }
    }

    private static readonly char[] TypeChars = ['a', 'z', ' ', '*', '`', '|', '~', '[', ']'];

    private static ColumnAlignment RandomAlignment(Random rng)
    {
        var values = Enum.GetValues<ColumnAlignment>();
        return values[rng.Next(values.Length)];
    }

    /// <summary>Re-clicks the header cell and steps to a random (row, column) — non-asserting, so a ragged intermediate never derails the walk.</summary>
    private static void GotoRandomCell(MarkdownEditingHarness h, Random rng)
    {
        h.Click(2, 1); // header cell (0,0) — the table stays anchored at the top of the document
        var m = Model(h);
        for (int i = 0, downs = rng.Next(m.RowCount); i < downs; i++)
            h.Key(Key.DownArrow);
        for (int i = 0, tabs = rng.Next(m.ColumnCount); i < tabs; i++)
            h.Key(Key.Tab);
    }

    private static string Dump(MarkdownEditingHarness h)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < h.Buffer.LineCount; i++)
            sb.Append('|').Append(h.Buffer.GetLine(i).Text).Append("|\n");
        return sb.ToString();
    }

    private static bool HasTable(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
            if (h.Blocks[i].Kind == BlockKind.Table)
                return true;
        return false;
    }

    private static TableModel Model(MarkdownEditingHarness h)
    {
        for (var i = 0; i < h.Blocks.Count; i++)
            if (h.Blocks[i].Kind == BlockKind.Table)
                return h.Bridge.GetTableModel(i)!;
        throw new Xunit.Sdk.XunitException("no table block");
    }
}
