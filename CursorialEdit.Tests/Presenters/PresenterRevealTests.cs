using Cursorial.Rendering;

using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7 reveal gate — activating a block reveals <b>its</b> marks and changes no cell outside it
/// (the §4.1 no-reflow invariant, per kind), and reveal is <b>height-invariant</b> (Decision 9 / §4.1):
/// a line that wraps to N rows while hidden keeps its N-row footprint when revealed as one slid row,
/// so no sibling moves. Every assertion reads composited cells from the real
/// <see cref="PresenterHarness"/> frame loop, under both §5.1 wire presets where rendering-affecting.
/// </summary>
public sealed class PresenterRevealTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── reveal per kind ─────────────────────────────

    [Theory]
    [InlineData("# Heading one", 0, "# Heading one")]
    [InlineData("**bold** text", 0, "**bold** text")]
    [InlineData("> quoted line", 0, "> quoted line")]
    [InlineData("- item one", 0, "- item one")]
    [InlineData("1. first", 0, "1. first")]
    [InlineData("---", 0, "---")]
    public void ActivatingABlock_RevealsItsMarks(string markdown, int activeLine, string revealed)
    {
        using var harness = PresenterHarness.FromMarkdown(markdown, columns: 40);

        harness.SetActive(block: 0, activeLine);
        Assert.Equal(revealed, harness.RowTrimmed(0)); // the raw source marks show on the active line
    }

    [Theory]
    [InlineData("```csharp\nvar x = 1;\n```", 0, "```csharp")]
    [InlineData("```csharp\nvar x = 1;\n```", 2, "```")]
    public void ActivatingACodeFenceLine_RevealsTheFence(string markdown, int activeLine, string revealed)
    {
        using var harness = PresenterHarness.FromMarkdown(markdown, columns: 40);

        harness.SetActive(block: 0, activeLine);
        Assert.Equal(revealed, harness.RowTrimmed(activeLine));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void RevealingOneBlock_ChangesNoCellOutsideIt(string preset)
    {
        // A mixed stack: heading, blockquote, list, rule — one presenter per kind.
        using var harness = PresenterHarness.FromMarkdown(
            "# Title\n\n> quoted\n\n- item\n\n---",
            preset, columns: 40, rows: 12);

        // Inactive formatted baseline.
        Assert.Equal("Title", harness.RowTrimmed(harness.TopRow(0)));
        int quoteRow = harness.TopRow(1);
        Assert.Equal("▌ quoted", harness.RowTrimmed(quoteRow));

        var before = harness.SnapshotCells();
        var otherRenders = new[] { harness.Presenters[0].RenderCount, harness.Presenters[2].RenderCount, harness.Presenters[3].RenderCount };

        harness.SetActive(block: 1, activeLine: 0); // reveal the blockquote's `>`
        Assert.Equal("> quoted", harness.RowTrimmed(quoteRow));

        var after = harness.SnapshotCells();
        for (var row = 0; row < harness.Rows; row++)
        {
            if (row == quoteRow)
                continue;

            for (var column = 0; column < harness.Columns; column++)
                Assert.True(before[column, row] == after[column, row],
                    $"cell ({column},{row}) changed outside the active block");
        }

        // The other render boundaries never re-rastered — reveal touches exactly one zone (Decision 7).
        Assert.Equal(otherRenders[0], harness.Presenters[0].RenderCount);
        Assert.Equal(otherRenders[1], harness.Presenters[2].RenderCount);
        Assert.Equal(otherRenders[2], harness.Presenters[3].RenderCount);
    }

    // ───────────────────────────── height-invariance under reveal (the proving test) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void RevealingAWrappedActiveLine_KeepsBlockHeight_AndMovesNoSibling(string preset)
    {
        // A paragraph whose single logical line wraps to several visual rows at width 10, followed by a
        // sibling paragraph. While hidden the line is N rows; revealed it is ONE slid row — the block
        // must keep its N-row footprint so the sibling never shifts (§4.1).
        using var harness = PresenterHarness.FromMarkdown(
            "aaaaaaaaaabbbbbbbbbbcccccccccc\n\nSECOND",
            preset, columns: 10, rows: 12);

        int wrappedHeight = harness.Height(0);
        Assert.True(wrappedHeight >= 3, "the long line must wrap to several rows while hidden");

        int siblingTop = harness.TopRow(1);
        Assert.Equal(wrappedHeight, siblingTop);            // the sibling sits right after the wrapped block
        Assert.Equal("SECOND", harness.RowTrimmed(siblingTop));

        var before = harness.SnapshotCells();

        // Activate the wrapped line (reveal → one slid row).
        harness.SetActive(block: 0, activeLine: 0, slide: 0);

        Assert.Equal(wrappedHeight, harness.Height(0));     // height invariant — no shrink
        Assert.Equal(siblingTop, harness.TopRow(1));        // sibling did not move
        Assert.Equal("SECOND", harness.RowTrimmed(siblingTop));

        // No cell at or below the sibling's first row changed.
        var after = harness.SnapshotCells();
        for (var row = siblingTop; row < harness.Rows; row++)
            for (var column = 0; column < harness.Columns; column++)
                Assert.True(before[column, row] == after[column, row],
                    $"sibling cell ({column},{row}) moved when the wrapped line revealed");

        // The active block's first row DID change (reveal is observable), and its freed rows are blank.
        Assert.False(
            Enumerable.Range(0, harness.Columns).All(c => before[c, 0] == after[c, 0]),
            "the revealed slid row must change the active block's first row");
        for (var row = 1; row < wrappedHeight; row++)
            Assert.Equal(string.Empty, harness.RowTrimmed(row)); // reserved wrapped rows are blank
    }

    [Fact]
    public void HardBreak_ShowsReturnGlyph_OnActiveLine()
    {
        using var harness = PresenterHarness.FromMarkdown("line one  \nline two", columns: 40);

        // Inactive: the trailing hard-break spaces are invisible; the break is honored (two rows).
        Assert.Equal("line one", harness.RowTrimmed(0));

        harness.SetActive(block: 0, activeLine: 0);
        Assert.Contains("↵", harness.RowTrimmed(0)); // the ↵ affordance appears when editing the hard-break line
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void DeactivatingRestoresTheFormattedView(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("**bold** text", preset, columns: 40);
        var formatted = harness.SnapshotCells();

        harness.SetActive(block: 0, activeLine: 0);
        Assert.Equal("**bold** text", harness.RowTrimmed(0));

        harness.ClearActive();
        Assert.Equal("bold text", harness.RowTrimmed(0));

        // Toggling back is bit-for-bit the original formatted view (reveal is a pure overlay).
        var again = harness.SnapshotCells();
        for (var row = 0; row < harness.Rows; row++)
            for (var column = 0; column < harness.Columns; column++)
                Assert.True(formatted[column, row] == again[column, row],
                    $"cell ({column},{row}) did not restore after deactivating");
    }
}
