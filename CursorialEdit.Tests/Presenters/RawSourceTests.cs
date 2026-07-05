using Cursorial.Output;
using Cursorial.Rendering;

using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP10 raw-source view mode (architecture Decision 12): the <see cref="RawSourcePresenter"/> renders
/// a block's source lines <b>verbatim</b> — every markdown mark shown literally, one row per source line,
/// with syntax-token coloring — over an <b>identity</b> run map (source offset == display cell, 1:1). The
/// text assertions run under both §5.1 wire presets; the color assertions use palette entries that survive
/// both wires.
/// </summary>
public sealed class RawSourceTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── verbatim rendering ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Heading_ShowsLiteralHashMarks_NotHidden(string preset)
    {
        var presenter = new RawSourcePresenter(PresenterHarness.Lines("## Section"), BlockKind.Heading);
        using var harness = PresenterHarness.Show([presenter], preset);

        // Formatted mode hides "## "; raw shows it literally, and the identity map keeps 'S' at cell 3.
        Assert.Equal("## Section", harness.RowTrimmed(0));
        Assert.Equal("#", harness.Cell(0, 0).Grapheme);
        Assert.Equal("S", harness.Cell(3, 0).Grapheme);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Emphasis_ShowsLiteralDelimiters(string preset)
    {
        var presenter = new RawSourcePresenter(PresenterHarness.Lines("**bold** and `code`"), BlockKind.Paragraph);
        using var harness = PresenterHarness.Show([presenter], preset);

        Assert.Equal("**bold** and `code`", harness.RowTrimmed(0)); // every mark literal — nothing hidden
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void FencedCode_ShowsFencesLiterally_HeightIsRawLineCount(string preset)
    {
        var presenter = new RawSourcePresenter(PresenterHarness.Lines("```csharp\nvar x = 1;\n```"), BlockKind.FencedCode);
        using var harness = PresenterHarness.Show([presenter], preset);

        Assert.Equal(3, harness.Height(0));               // raw height = source line count (no collapsing)
        Assert.Equal("```csharp", harness.RowTrimmed(0));  // the opening fence is shown, not a language label
        Assert.Equal("var x = 1;", harness.RowTrimmed(1));
        Assert.Equal("```", harness.RowTrimmed(2));        // the closing fence is shown, not hidden
    }

    [Fact]
    public void Blockquote_ShowsLiteralAngleBracket_NotABarGlyph()
    {
        var presenter = new RawSourcePresenter(PresenterHarness.Lines("> quoted"), BlockKind.Quote);
        using var harness = PresenterHarness.Show([presenter]);

        Assert.Equal("> quoted", harness.RowTrimmed(0)); // the "> " is literal, not the ▌ bar
        Assert.Equal(">", harness.Cell(0, 0).Grapheme);
    }

    // ───────────────────────────── token coloring ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void MarkCells_CarryADistinctColorFromText(string preset)
    {
        var presenter = new RawSourcePresenter(PresenterHarness.Lines("## Section"), BlockKind.Heading);
        using var harness = PresenterHarness.Show([presenter], preset);

        var markColor = harness.Cell(0, 0).Style.Foreground; // '#'
        var textColor = harness.Cell(3, 0).Style.Foreground; // 'S'
        Assert.NotEqual(markColor, textColor);               // the mark is colored distinctly from prose
        Assert.Equal(Color.Default, textColor);              // plain text keeps the default foreground
    }

    [Fact]
    public void EmphasisDelimiters_AreColored_ContentIsNot()
    {
        var presenter = new RawSourcePresenter(PresenterHarness.Lines("**bold**"), BlockKind.Paragraph);
        using var harness = PresenterHarness.Show([presenter]);

        Assert.NotEqual(harness.Cell(0, 0).Style.Foreground, harness.Cell(2, 0).Style.Foreground); // '*' vs 'b'
        Assert.Equal(Color.Default, harness.Cell(2, 0).Style.Foreground);                           // 'b' plain
    }

    // ───────────────────────────── identity run map ─────────────────────────────

    [Fact]
    public void BuildRaw_IsIdentity_SourceOffsetEqualsCell_BothDirections()
    {
        var lines = PresenterHarness.Lines("## Section");
        var raw = RunMapBuilder.BuildRaw(lines, wrapWidth: 40);

        Assert.Equal(1, raw.RowCount);              // one visual row per source line (wrap-off)
        Assert.Equal(10, raw.SourceLength);         // "## Section"

        for (var offset = 0; offset <= 10; offset++)
        {
            var (row, cell) = raw.Locate(offset);
            Assert.Equal(0, row);
            Assert.Equal(offset, cell);             // source offset N sits at cell N (no mark-skipping)
            Assert.Equal(offset, raw.OffsetAt(0, cell)); // and the inverse round-trips
        }
    }

    [Fact]
    public void BuildRaw_ContrastsFormatted_WhichSkipsHiddenMarks()
    {
        var lines = PresenterHarness.Lines("## Section");

        // Formatted: the "## " prefix is a hidden mark, so 'S' (source col 3) collapses to cell 0.
        var formatted = RunMapBuilder.Build(lines, [], BlockKind.Heading, headingLevel: 2, wrapWidth: 40);
        Assert.Equal(0, formatted.Locate(3).Cell);

        // Raw: no marks hidden — 'S' sits at its true cell 3.
        var raw = RunMapBuilder.BuildRaw(lines, wrapWidth: 40);
        Assert.Equal(3, raw.Locate(3).Cell);
    }

    [Fact]
    public void BuildRaw_MultiLine_OneRowPerLine_OffsetsSpanTerminators()
    {
        var lines = PresenterHarness.Lines("- one\n- two");
        var raw = RunMapBuilder.BuildRaw(lines);

        Assert.Equal(2, raw.RowCount);
        Assert.Equal(0, raw.LineOfRow(0));
        Assert.Equal(1, raw.LineOfRow(1));

        // The list markers are literal source in raw mode, not synthetic bullets.
        Assert.Equal(0, raw.Locate(0).Cell);  // '-' of line 0
        var (row, cell) = raw.Locate(6);       // '-' of line 1 (after "- one\n")
        Assert.Equal(1, row);
        Assert.Equal(0, cell);
    }
}
