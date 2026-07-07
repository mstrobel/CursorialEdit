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
/// WYSIWYG inline formatting INSIDE table cells (Decision 9 — per-cell reveal, retiring deferred-cleanup #4).
/// An <b>inactive</b> cell renders its inline constructs FORMATTED (marks hidden, content styled exactly like
/// the prose paragraph path); the <b>active</b> cell (the one the caret is in) reveals its RAW markdown so the
/// user edits in place, and its row reflows when the caret enters/leaves. A plain-text cell (the common case)
/// renders byte-identically to before. Copy still emits the RAW source, and the caret/click land on the right
/// SOURCE offset through the per-cell display↔source map.
/// </summary>
public sealed class TableInlineFormatTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private static (TablePresenter Presenter, TableModel Model) Build(string markdown, int availableColumns = 0)
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
        return (new TablePresenter(lines, model, source, availableColumns), model);
    }

    // Body content cell of a "| H | K |\n|---|---|\n| <fmt> | z |" table: both columns clamp to min width 3,
    // so the first cell's content sits at grid x 2 on the body content grid row 3 (top/header/sep/body/bottom).
    private const int BodyContentX = 2;
    private const int PlainContentX = 8;
    private const int BodyRow = 3;

    // ───────────────────────────── 1. each inline kind renders formatted, marks hidden ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Bold_InInactiveCell_HidesMarks_AppliesBoldAttribute(string preset) =>
        AssertFormattedAttribute(preset, "**b**", 'b', TextAttributes.Bold);

    [Theory]
    [MemberData(nameof(Presets))]
    public void Italic_InInactiveCell_HidesMarks_AppliesItalicAttribute(string preset) =>
        AssertFormattedAttribute(preset, "*i*", 'i', TextAttributes.Italic);

    [Theory]
    [MemberData(nameof(Presets))]
    public void Strikethrough_InInactiveCell_HidesMarks_AppliesStrikethroughAttribute(string preset) =>
        AssertFormattedAttribute(preset, "~~s~~", 's', TextAttributes.Strikethrough);

    [Theory]
    [MemberData(nameof(Presets))]
    public void Link_InInactiveCell_HidesMarks_ShowsOnlyTheLinkText(string preset)
    {
        // The link text renders; the [ ]( url ) scaffolding is hidden. (The underline attribute is asserted on
        // the colour tier below — §7.1 gates the link underline per capability tier.)
        var (presenter, _) = Build("| H | K |\n|---|---|\n| [t](http://x) | z |");
        using var h = PresenterHarness.Show([presenter], preset, columns: 40, rows: 14);

        Assert.Equal("t", h.Cell(BodyContentX, BodyRow).Grapheme);
        Assert.DoesNotContain("[", h.RowTrimmed(BodyRow));
        Assert.DoesNotContain("http", h.RowTrimmed(BodyRow));
    }

    [Fact]
    public void Link_OnColourTier_UnderlinesTheLinkText()
    {
        var (presenter, _) = Build("| H | K |\n|---|---|\n| [t](http://x) | z |");
        using var h = PresenterHarness.Show([presenter], nameof(Cursorial.UI.Testing.TestCapabilities.KittyTruecolor), columns: 40, rows: 14);

        Assert.True(h.Cell(BodyContentX, BodyRow).Style.Attributes.HasFlag(TextAttributes.Underline), "link text should be underlined");
    }

    [Fact]
    public void Code_InInactiveCell_HidesBackticks_AppliesCodeFill()
    {
        // The backticks hide and the code span carries the code fill — the same fill the header row uses, and
        // distinct from a plain body cell's default background. (Colour tier: the fill is a real background.)
        var (presenter, _) = Build("| H | K |\n|---|---|\n| `c` | z |");
        using var h = PresenterHarness.Show([presenter], nameof(Cursorial.UI.Testing.TestCapabilities.KittyTruecolor), columns: 40, rows: 14);

        Assert.Equal("c", h.Cell(BodyContentX, BodyRow).Grapheme);
        Assert.DoesNotContain("`", h.RowTrimmed(BodyRow));

        var codeFill = h.Cell(BodyContentX, BodyRow).Style.Background;
        Assert.Equal(h.Cell(BodyContentX, 1).Style.Background, codeFill);        // == the header row's code fill
        Assert.NotEqual(h.Cell(PlainContentX, BodyRow).Style.Background, codeFill); // != a plain body cell's default
    }

    private static void AssertFormattedAttribute(string preset, string markdown, char displayGlyph, TextAttributes attribute)
    {
        var (presenter, _) = Build($"| H | K |\n|---|---|\n| {markdown} | z |");
        using var h = PresenterHarness.Show([presenter], preset, columns: 40, rows: 14);

        // The mark characters are gone: only the content glyph draws, and it carries the inline attribute.
        Assert.Equal(displayGlyph.ToString(), h.Cell(BodyContentX, BodyRow).Grapheme);
        Assert.True(h.Cell(BodyContentX, BodyRow).Style.Attributes.HasFlag(attribute), $"the formatted cell should carry {attribute}");

        // The adjacent PLAIN body cell carries no inline attribute — the formatting is scoped to the one cell.
        Assert.False(h.Cell(PlainContentX, BodyRow).Style.Attributes.HasFlag(attribute), "a plain cell must not be styled");
    }

    // ───────────────────────────── 2. cell width is the DISPLAY width (marks hidden → narrower) ─────────────────────────────

    [Fact]
    public void ColumnWidth_IsTheDisplayWidth_NotTheRawWidth()
    {
        // `**bold**` is 8 raw chars but renders `bold` (4). The column measures the DISPLAY width.
        var (_, model) = Build("| **bold** | y |\n|---|---|\n| a | b |");

        Assert.Equal(4, model.MaxContentWidth(0)); // display `bold`, not raw `**bold**` (8)
        Assert.Equal(4, model.ColumnWidth(0));
    }

    [Fact]
    public void CellInlineRuns_ProjectsTheCellConstructs_CellRelative()
    {
        var (_, model) = Build("| **bold** | `c` |\n|---|---|\n| a | b |");

        var boldRuns = model.CellInlineRuns(0, 0);
        Assert.Contains(boldRuns, r => r.Kind == InlineRunKind.Strong);
        Assert.All(boldRuns, r => Assert.True(r.SourceStart >= 0, "cell inline runs are cell-relative (≥ 0)"));

        Assert.Contains(model.CellInlineRuns(0, 1), r => r.Kind == InlineRunKind.Code);
        Assert.Empty(model.CellInlineRuns(1, 0)); // a plain cell has no formatting runs
    }

    // ───────────────────────────── 3. the ACTIVE cell reveals raw + the row reflows ─────────────────────────────

    [Fact]
    public void ActiveCell_RevealsRawMarks_WhenTheCaretEntersIt()
    {
        // Column 0 is 5 wide (header `aaaaa`), so the raw `**b**` (5) fits without wrapping — a clean raw reveal.
        using var h = MarkdownEditingHarness.Create("| aaaaa | y |\n|---|---|\n| **b** | z |", columns: 40, rows: 16);

        Assert.DoesNotContain("**b**", h.RowTrimmed(BodyRow)); // inactive: formatted, marks hidden

        h.Click(BodyContentX, BodyRow); // caret into the formatted cell → it reveals raw
        h.Settle();

        Assert.Contains("**b**", h.RowTrimmed(BodyRow)); // active: raw marks shown
    }

    [Fact]
    public void ActiveCell_ReflowsTheRow_OnCaretEnterAndLeave()
    {
        // Column 0 is 4 wide (`bold`); raw `**bold**` (8) overflows it and char-wraps to 2 visual rows, so the
        // body row grows on reveal and shrinks again when the caret leaves — the per-cell reveal reflow.
        using var h = MarkdownEditingHarness.Create("| H | y |\n|---|---|\n| **bold** | z |", columns: 40, rows: 16);
        var table = (TablePresenter) h.Presenter(0);

        int bodyHeightInactive = table.Rows[1].RowHeight;
        Assert.Equal(2, bodyHeightInactive); // one content row + separator

        h.Click(BodyContentX, BodyRow); // reveal cell (1,0) raw → it wraps to 2 rows
        h.Settle();
        Assert.Equal(3, table.Rows[1].RowHeight); // two content rows + separator — reflowed taller

        h.Click(PlainContentX, BodyRow); // move the caret to the plain cell (1,1) → (1,0) re-formats
        h.Settle();
        Assert.Equal(2, table.Rows[1].RowHeight); // reflowed back
    }

    // ───────────────────────────── 4. caret / click lands on the right SOURCE offset ─────────────────────────────

    [Fact]
    public void ClickInFormattedCell_LandsOnTheCorrectSourceOffset()
    {
        // Body line "| **bold** | z |": the display `bold` sits at grid x 2..5; clicking the 'l' (display x 4)
        // must land on the RAW source offset of 'l' (line 2, col 6), skipping the hidden `**`.
        using var h = MarkdownEditingHarness.Create("| H | y |\n|---|---|\n| **bold** | z |", columns: 40, rows: 16);

        h.Click(4, BodyRow); // display cell of 'l'
        h.AssertCaret(2, 6);
    }

    [Fact]
    public void ClickOnHiddenMarkPosition_CanonicalizesToItsContent()
    {
        // Clicking the cell's left content edge (display x 2, where the hidden `**` collapsed to) lands on the
        // first CONTENT grapheme 'b' (col 4), never inside the hidden mark (col 2/3).
        using var h = MarkdownEditingHarness.Create("| H | y |\n|---|---|\n| **bold** | z |", columns: 40, rows: 16);

        h.Click(BodyContentX, BodyRow);
        h.AssertCaret(2, 4); // 'b', not the leading '*' (col 2)
    }

    // ───────────────────────────── 5. a plain cell renders byte-identical to before ─────────────────────────────

    [Fact]
    public void PlainCell_RendersUnstyled_ByteIdentical()
    {
        var (presenter, _) = Build("| A | B |\n|---|---|\n| 1 | 22 |");
        using var h = PresenterHarness.Show([presenter], nameof(Cursorial.UI.Testing.TestCapabilities.KittyTruecolor), columns: 40, rows: 14);

        // The rows are the exact pre-formatting output; a plain cell takes the raw path (no projection).
        Assert.Equal("┌─────┬─────┐", h.RowTrimmed(0));
        Assert.Equal("│ A   │ B   │", h.RowTrimmed(1));
        Assert.Equal("│ 1   │ 22  │", h.RowTrimmed(BodyRow));

        // No plain body cell carries any inline attribute, and its background is the default (never a code fill).
        foreach (int x in new[] { 2, 8 })
        {
            Assert.Equal(TextAttributes.None, h.Cell(x, BodyRow).Style.Attributes);
            Assert.Equal(h.Cell(4, BodyRow).Style.Background, h.Cell(x, BodyRow).Style.Background); // uniform default
        }
    }

    // ───────────────────────────── 6. wrap / truncate over the FORMATTED display ─────────────────────────────

    [Fact]
    public void Wrap_OverFormattedCell_WrapsTheDisplayWidth_KeepingStyledRuns()
    {
        // `**hello world**` renders `hello world` (11). Wrapped to a 5-cell column it splits on the word boundary
        // into >1 visual rows, each carrying styled (bold) runs whose source slices reconstruct the display.
        var (_, model) = Build("| **hello world** | y |\n|---|---|\n| a | b |");
        var layout = model.LayoutRow(0, new[] { 5, 3 }, TableOverflow.Wrap);

        Assert.True(layout.VisualRowCount >= 2, "the formatted display should wrap over the narrow column");

        // Every fragment of the formatted cell draws styled (bold) runs, none exceeds the column width, and the
        // runs' source slices — concatenated across the visual rows — reconstruct the marks-hidden display.
        int totalDisplayLen = 0;
        int contentStart = model.CellContentRange(0, 0).Start;
        string content = model.CellContent(0, 0); // "**hello world**" (raw trimmed content), indexed cell-relative
        foreach (var visual in layout.VisualRows)
        {
            var frag = visual.Cell(0);
            if (frag.IsEmpty)
                continue;
            Assert.NotNull(frag.StyledRuns);
            Assert.True(frag.Width <= 5, "a wrapped formatted fragment must fit its column");
            foreach (var run in frag.StyledRuns!)
            {
                Assert.True(run.Style.HasFlag(CellInlineStyle.Bold), "the wrapped content stays bold");
                // Each run's source slice is drawn verbatim (no marks) — it is a slice of the raw cell content.
                string slice = content.Substring(run.SrcStart - contentStart, run.SrcLength);
                Assert.DoesNotContain("*", slice); // a content run never contains a hidden mark
                totalDisplayLen += run.SrcLength;
            }
        }

        // The two words tile the drawn display; the inter-word space is trimmed at the wrap boundary (cells trim
        // trailing whitespace per visual row, unlike prose soft-wrap) → "hello" + "world" = 10 drawn chars.
        Assert.Equal("helloworld".Length, totalDisplayLen);
    }

    [Fact]
    public void Wrap_OverFormattedCell_WordExactlyFillingColumn_AbsorbsTheLoneSpace_NoBlankRow()
    {
        // Pins the shared wrap core's whitespace-only-segment absorption on the FORMATTED path (deferred #5 dedup):
        // `**abcd efgh**` renders `abcd efgh`; at width 4 `abcd` fills the column exactly, so prose WordWrap parks
        // the following space as its own segment. The unified helper must absorb it into the previous fragment —
        // two visual rows (`abcd`, `efgh`), never a lone-space blank row between — with both fragments styled bold.
        var (_, model) = Build("| **abcd efgh** | y |\n|---|---|\n| a | b |");
        var layout = model.LayoutRow(0, new[] { 4, 3 }, TableOverflow.Wrap);

        Assert.Equal(2, layout.VisualRowCount); // exactly two rows — the lone inter-word space did not spill a row
        int contentStart = model.CellContentRange(0, 0).Start;
        string content = model.CellContent(0, 0); // "**abcd efgh**"
        foreach (var visual in layout.VisualRows)
        {
            var frag = visual.Cell(0);
            Assert.NotNull(frag.StyledRuns);
            Assert.True(frag.Width <= 4, "each wrapped formatted fragment fits its column");
            Assert.All(frag.StyledRuns!, run => Assert.True(run.Style.HasFlag(CellInlineStyle.Bold), "the wrapped content stays bold"));
            // The fragment's source span tiles the raw cell content (the absorbed space stays attributed to source).
            Assert.DoesNotContain("*", content.Substring(frag.StyledRuns![0].SrcStart - contentStart, frag.StyledRuns[0].SrcLength));
        }
    }

    [Fact]
    public void Truncate_OverFormattedCell_TruncatesTheDisplay_WithEllipsis()
    {
        // `**hello world**` → `hello world` (11); truncated to a 6-cell column it keeps a 5-cell display prefix
        // plus the … — over the DISPLAY, not the raw marks.
        var (_, model) = Build("| **hello world** | y |\n|---|---|\n| a | b |");
        var layout = model.LayoutRow(0, new[] { 6, 3 }, TableOverflow.Truncate);

        Assert.Equal(1, layout.VisualRowCount);
        var frag = layout.VisualRows[0].Cell(0);
        Assert.True(frag.Ellipsis, "an over-wide formatted cell truncates with an ellipsis");
        Assert.NotNull(frag.StyledRuns);
        Assert.True(frag.Width <= 6 - TableModel.EllipsisWidth);
        Assert.All(frag.StyledRuns!, run => Assert.True(run.Style.HasFlag(CellInlineStyle.Bold)));
    }

    [Fact]
    public void ColumnWindow_OverFormattedTable_HidesMarks_AndDoesNotScrollHorizontally()
    {
        // A 5-column formatted table can't fit a narrow viewport even at MinWidth → the presenter-internal
        // column-window engages. The visible formatted cells still hide their marks, and the grid's on-screen
        // width never exceeds the viewport (FB-6 sidestep — the document never scrolls sideways).
        int viewport = 22;
        using var h = MarkdownEditingHarness.Create(
            "| **aa** | **bb** | **cc** | **dd** | **ee** |\n|---|---|---|---|---|\n| p | q | r | s | t |",
            columns: viewport, rows: 16);
        var table = (TablePresenter) h.Presenter(0);

        Assert.True(table.GridWidth > viewport, "the grid should overflow the viewport (column-window engaged)");
        Assert.True(table.RenderedWidth <= viewport, "the on-screen grid width must not exceed the viewport");

        // The visible header row shows formatted content (e.g. `aa`), never the raw `**aa**`.
        Assert.DoesNotContain("**", h.RowTrimmed(1));
        Assert.Contains("aa", h.RowTrimmed(1));
    }

    // ───────────────────────────── 7. cell-rect copy still emits RAW source ─────────────────────────────

    [Fact]
    public void CellRectCopy_EmitsRawMarkdown_NotTheFormattedDisplay()
    {
        using var h = MarkdownEditingHarness.Create("| **a** | **b** |\n|---|---|\n| c | d |", columns: 40, rows: 14);

        // Drag across the two header cells → a cell-rect (0,0)-(0,1). Copy emits the sub-table with RAW cells.
        h.Drag(BodyContentX, 1, 8, 1);
        string? copied = h.Caret.SelectedText();

        Assert.NotNull(copied);
        Assert.Contains("**a**", copied); // the RAW source, not the formatted `a`
        Assert.Contains("**b**", copied);
    }

    // ───────────────────────────── 8. intra-cell editing is unaffected (edits the RAW active cell) ─────────────────────────────

    [Fact]
    public void TypingInAFormattedCell_EditsTheRawSource()
    {
        using var h = MarkdownEditingHarness.Create("| H | y |\n|---|---|\n| **bold** | z |", columns: 40, rows: 16);

        h.Click(4, BodyRow); // land on 'l' (col 6) inside the now-active raw cell
        h.AssertCaret(2, 6);
        h.Type("X");

        // The edit lands in the RAW source between 'o' and 'l' → `**boXld**` (editing is raw, unaffected by formatting).
        Assert.Equal("| **boXld** | z |", h.Buffer.GetLine(2).Text);
    }

    [Fact]
    public void EmptyDisplayCell_FallsBackToRaw_StaysVisible()
    {
        // A cell whose whole content is all-mark-no-text — an empty-alt image `![](x)` — projects to an EMPTY
        // formatted display. It must fall back to raw rendering (visible source + a clickable stop), not vanish
        // into an invisible, un-clickable cell. (Review finding #1.)
        var (presenter, _) = Build("| H | K |\n|---|---|\n| ![](x) | z |");
        using var h = PresenterHarness.Show([presenter], nameof(Cursorial.UI.Testing.TestCapabilities.KittyTruecolor), columns: 40, rows: 14);

        Assert.Contains("![](x)", h.RowTrimmed(BodyRow)); // rendered raw, not an empty invisible cell
    }

    [Fact]
    public void EndKey_IntoAFormattedCellEndingInMarks_LandsAtContentEnd_NotInsideTheEmphasis()
    {
        // End into a formatted last cell ending in hidden marks must reach the cell's TRUE content end (after
        // `**`), so a following keystroke appends AFTER the emphasis, not inside it (`**d**X`, not `**dX**`).
        // (Review finding #2.)
        using var h = MarkdownEditingHarness.Create("| H | K |\n|---|---|\n| c | **d** |\n", columns: 40, rows: 12);
        h.Click(2, BodyRow); // into body cell 0 "c"
        h.Key(Cursorial.Input.Key.End);
        h.Type("X");

        Assert.Equal("| c | **d**X |", h.Buffer.GetLine(2).Text);
    }
}
