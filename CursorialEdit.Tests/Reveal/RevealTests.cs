using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Testing;

using CursorialEdit.Layout;
using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Reveal;

/// <summary>
/// M2.WP6 gate — the reveal-on-edit spike that RETIRES risk R1 (reveal-without-reflow with the
/// grapheme-snapped slide, architecture Decision 9 / §2.4 / §4.1). Every assertion reads the
/// composited cells of a <see cref="ParagraphPresenter"/> stack driven through the real
/// <see cref="UITestHost"/> frame loop, so the whole <b>RunMap → slide → clip → draw</b> path is
/// exercised end to end:
/// <list type="bullet">
/// <item><see cref="ToggleRevealChangesNoCellOutsideActiveBlock"/> — reveal re-rasters exactly the
/// active block; no cell elsewhere moves (§4.1 "no reflow of other lines").</item>
/// <item><see cref="ClipEdgeNeverSplitsWideCluster"/> — a CJK/emoji/ZWJ cluster straddling a clip
/// edge becomes a dim continuation indicator (or blank), never a half glyph, on both edges.</item>
/// <item><see cref="CaretStaysOnGrapheme"/> — as the active line slides under scripted caret motion
/// and typing, the caret's published column stays inside the visible span and lands on a cluster
/// boundary, never mid-cluster and never hidden under an indicator (the WP5 invariant end to end).</item>
/// </list>
/// The rendering-affecting suites run under both §5.1 wire presets.
/// </summary>
public sealed class RevealTests
{
    private const string Cjk = "漢";
    private const string Emoji = "\U0001F44D";                                     // 👍
    private const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466"; // 👨‍👩‍👧‍👦

    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    /// <summary>Wide (2-cell) clusters exercised at the clip edges, across both wire presets.</summary>
    public static TheoryData<string, string> WideClusters()
    {
        var data = new TheoryData<string, string>();
        foreach (var preset in new[] { nameof(TestCapabilities.KittyTruecolor), nameof(TestCapabilities.Ansi16Legacy) })
            foreach (var wide in new[] { Cjk, Emoji, Family })
                data.Add(preset, wide);
        return data;
    }

    // ───────────────────────────── R1 gate 1: no reflow of other lines ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ToggleRevealChangesNoCellOutsideActiveBlock(string preset)
    {
        // A stack of three formatted paragraphs, each one row wide enough to fit unwrapped.
        using var harness = RevealHarness.Show(
            [
                RevealHarness.Markdown("**alpha** one"),
                RevealHarness.Markdown("**bravo** two"),
                RevealHarness.Markdown("*charlie* three"),
            ],
            preset, columns: 40, rows: 6);

        // Inactive: marks hidden — the formatted view.
        Assert.Equal("alpha one", harness.Row(0).TrimEnd());
        Assert.Equal("bravo two", harness.Row(1).TrimEnd());
        Assert.Equal("charlie three", harness.Row(2).TrimEnd());

        int block1Row = harness.TopRow(1);
        Assert.Equal(1, block1Row); // one row per block

        var before = harness.SnapshotCells();
        int render0 = harness.Presenters[0].RenderCount;
        int render2 = harness.Presenters[2].RenderCount;

        // Activate the MIDDLE block: its marks reveal (**bravo** shows its fences).
        harness.SetActive(block: 1, activeLine: 0, slide: 0);
        Assert.Equal("**bravo** two", harness.Row(block1Row).TrimEnd());

        var after = harness.SnapshotCells();

        // The R1 claim: NO cell outside the active block's row changed (cell-level diff).
        for (var row = 0; row < harness.Rows; row++)
        {
            if (row == block1Row)
                continue;

            for (var column = 0; column < harness.Columns; column++)
                Assert.True(before[column, row] == after[column, row],
                    $"cell ({column},{row}) changed outside the active block");
        }

        // …and the active block's row DID change (reveal is observable, not a no-op).
        Assert.False(
            Enumerable.Range(0, harness.Columns).All(c => before[c, block1Row] == after[c, block1Row]),
            "the active block's row must change when its marks reveal");

        // The sibling render boundaries never re-rastered — reveal touches exactly one zone (Decision 7).
        Assert.Equal(render0, harness.Presenters[0].RenderCount);
        Assert.Equal(render2, harness.Presenters[2].RenderCount);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void RevealingOneLine_MovesNoOtherRow_WithinAMultiLineBlock(string preset)
    {
        // A single paragraph block spanning three source lines; activate the MIDDLE line only.
        using var harness = RevealHarness.Show(
            [RevealHarness.Markdown("*a* one\n*b* two\n*c* three")],
            preset, columns: 40, rows: 6);

        Assert.Equal("a one", harness.Row(0).TrimEnd());
        Assert.Equal("b two", harness.Row(1).TrimEnd());
        Assert.Equal("c three", harness.Row(2).TrimEnd());

        var before = harness.SnapshotCells();

        harness.SetActive(block: 0, activeLine: 1, slide: 0);
        Assert.Equal("*b* two", harness.Row(1).TrimEnd()); // only the active line reveals

        var after = harness.SnapshotCells();

        for (var row = 0; row < harness.Rows; row++)
        {
            if (row == 1)
                continue;

            for (var column = 0; column < harness.Columns; column++)
                Assert.True(before[column, row] == after[column, row],
                    $"cell ({column},{row}) moved when a sibling line revealed");
        }
    }

    // ───────────────────────────── R1 gate 2: clip never splits a wide cluster ─────────────────────────────

    [Theory]
    [MemberData(nameof(WideClusters))]
    public void ClipEdgeNeverSplitsWideCluster_RightEdge(string preset, string wide)
    {
        // "aaaa{W}bb" at width 5, active + slide 0: {W} occupies cells [4,6) and straddles the right
        // edge → the edge cell is the ❯ indicator, never half of {W}.
        using var harness = RevealHarness.Show(
            [RevealHarness.Paragraph("aaaa" + wide + "bb", WrapMode.NoWrap)],
            preset, columns: 5, rows: 3);
        harness.SetActive(block: 0, activeLine: 0, slide: 0);

        Assert.Equal(ClipCell.RightGlyph.ToString(), harness.Cell(4, 0).Grapheme); // ❯ at the straddled edge
        AssertClusterAbsentAndNoHalfGlyph(harness, row: 0, wide);
    }

    [Theory]
    [MemberData(nameof(WideClusters))]
    public void ClipEdgeNeverSplitsWideCluster_LeftEdge(string preset, string wide)
    {
        // "a{W}aaaa" at width 4, active + slide 2: {W} occupies cells [1,3) and straddles the left
        // edge of the window → the edge cell is the ❮ indicator, never half of {W}.
        using var harness = RevealHarness.Show(
            [RevealHarness.Paragraph("a" + wide + "aaaa", WrapMode.NoWrap)],
            preset, columns: 4, rows: 3);
        harness.SetActive(block: 0, activeLine: 0, slide: 2);

        Assert.Equal(ClipCell.LeftGlyph.ToString(), harness.Cell(0, 0).Grapheme); // ❮ at the straddled edge
        AssertClusterAbsentAndNoHalfGlyph(harness, row: 0, wide);
    }

    [Theory]
    [MemberData(nameof(WideClusters))]
    public void ClipEdgeNeverSplitsWideCluster_BothEdges(string preset, string wide)
    {
        // "aa{W}aa{W}aa" at width 4, active + slide 3: window [3,7) straddles the first {W} on the
        // left and the second {W} on the right → both indicators, neither {W} half-rendered.
        using var harness = RevealHarness.Show(
            [RevealHarness.Paragraph("aa" + wide + "aa" + wide + "aa", WrapMode.NoWrap)],
            preset, columns: 4, rows: 3);
        harness.SetActive(block: 0, activeLine: 0, slide: 3);

        Assert.Equal(ClipCell.LeftGlyph.ToString(), harness.Cell(0, 0).Grapheme);  // ❮ (left edge)
        Assert.Equal(ClipCell.RightGlyph.ToString(), harness.Cell(3, 0).Grapheme); // ❯ (right edge)
        AssertClusterAbsentAndNoHalfGlyph(harness, row: 0, wide);
    }

    [Theory]
    [MemberData(nameof(WideClusters))]
    public void WideClusterFullyInsideTheWindow_RendersWhole(string preset, string wide)
    {
        // The complement: a wide cluster that fits whole renders as WideLeft + WideContinuation — the
        // presenter suppresses ONLY straddles, never a cluster that fits.
        using var harness = RevealHarness.Show(
            [RevealHarness.Paragraph("a" + wide + "b", WrapMode.NoWrap)],
            preset, columns: 6, rows: 3);
        harness.SetActive(block: 0, activeLine: 0, slide: 0);

        Assert.Equal(wide, harness.Cell(1, 0).Grapheme);                    // the whole cluster at its left cell
        Assert.Equal(CellKind.WideLeft, harness.Cell(1, 0).Kind);
        Assert.Equal(CellKind.WideContinuation, harness.Cell(2, 0).Kind);   // its right half, in-window
        Assert.Equal("b", harness.Cell(3, 0).Grapheme);
    }

    /// <summary>Asserts <paramref name="wide"/> never renders in the row and no wide-left glyph is missing its continuation.</summary>
    private static void AssertClusterAbsentAndNoHalfGlyph(RevealHarness harness, int row, string wide)
    {
        for (var column = 0; column < harness.Columns; column++)
        {
            var cell = harness.Cell(column, row);
            Assert.NotEqual(wide, cell.Grapheme); // the straddling cluster is never drawn (whole-cell discipline)

            // A wide-left glyph must have its continuation immediately to the right, inside the viewport —
            // never clinging to the last column as a half glyph.
            if (cell.Kind == CellKind.WideLeft)
            {
                Assert.True(column + 1 < harness.Columns, $"wide-left glyph at the last column {column}");
                Assert.Equal(CellKind.WideContinuation, harness.Cell(column + 1, row).Kind);
            }
        }
    }

    // ───────────────────────────── R1 gate 3: caret stays on grapheme + visible ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CaretStaysOnGrapheme(string preset)
    {
        // A long active line mixing ASCII, CJK, and emoji clusters — far wider than the viewport, so it
        // must slide as the caret moves.
        const string line = "aaaa漢字bbbb👍cccc漢dddd";
        const int columns = 10;

        using var harness = RevealHarness.Show(
            [RevealHarness.Paragraph(line, WrapMode.NoWrap)],
            preset, columns, rows: 3);
        var presenter = harness.Presenters[0];
        harness.SetActive(block: 0, activeLine: 0, slide: 0);

        int slide = 0;

        // Walk the caret RIGHT one cluster at a time to the end, then LEFT back to the start; at every
        // stop the caret sits on a cluster boundary, inside the visible span, on a drawable column.
        var forward = ClusterOffsets(line);
        var caretScript = forward.Concat(Enumerable.Reverse(forward)).ToArray();

        foreach (int caret in caretScript)
        {
            Assert.True(CaretNavigator.IsClusterBoundary(line, caret), $"scripted caret {caret} is not a cluster boundary");
            slide = AssertCaretVisibleAndOnGrapheme(harness, presenter, caret, slide, columns);
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Typing_GrowsTheActiveLine_CaretStaysVisibleAndOnGrapheme(string preset)
    {
        const int columns = 12;
        using var harness = RevealHarness.Show(
            [RevealHarness.Paragraph("a", WrapMode.NoWrap)],
            preset, columns, rows: 3);
        var presenter = harness.Presenters[0];
        harness.SetActive(block: 0, activeLine: 0, slide: 0);

        // Type clusters at the end — plain letters interleaved with wide CJK/emoji — growing the line far
        // past the viewport; the caret (always at the line end) must stay visible and on a boundary.
        var text = "a";
        int slide = 0;
        string[] appended = ["b", Cjk, "c", Emoji, "d", Cjk, "e", "f", Emoji, "g", Cjk, "h", "i", "j", "k", "l"];

        foreach (var chunk in appended)
        {
            text += chunk;
            harness.SetText(block: 0, text);
            harness.SetActive(block: 0, activeLine: 0, slide);
            Assert.True(CaretNavigator.IsClusterBoundary(text, text.Length));
            slide = AssertCaretVisibleAndOnGrapheme(harness, presenter, caret: text.Length, slide, columns);
        }

        Assert.True(slide > 0, "a line grown well past the viewport must have slid");
    }

    /// <summary>
    /// Recomputes the caret-visibility slide for a caret at source offset <paramref name="caret"/> (the
    /// active single line), drives it through the presenter, and asserts the published caret column is
    /// inside the visible span and lands on a drawable grapheme boundary (never a wide cluster's tail,
    /// never an indicator). Returns the new slide.
    /// </summary>
    private static int AssertCaretVisibleAndOnGrapheme(
        RevealHarness harness, ParagraphPresenter presenter, int caret, int previousSlide, int viewport)
    {
        var map = presenter.MapForWidth(viewport);
        var (row, caretCell) = map.Locate(caret);
        int slide = HorizontalSlide.Compute(previousSlide, caretCell, map.RowWidth(row), viewport);

        presenter.SetReveal(0, slide);
        harness.Settle();

        int published = caretCell - slide;
        Assert.InRange(published, 0, viewport - 1); // the WP5 invariant: caret inside the visible span

        var clip = map.ClipRow(row, slide, viewport);
        var cell = clip.Cells[published];
        Assert.NotEqual(ClipCellKind.Tail, cell.Kind);           // never mid-cluster
        Assert.NotEqual(ClipCellKind.LeftIndicator, cell.Kind);  // never hidden under a continuation indicator
        Assert.NotEqual(ClipCellKind.RightIndicator, cell.Kind);
        return slide;
    }

    /// <summary>Every cluster-boundary offset of <paramref name="line"/>, ascending (0 … length).</summary>
    private static int[] ClusterOffsets(string line)
    {
        var offsets = new List<int> { 0 };
        int col = 0;
        while (col < line.Length)
        {
            col = CaretNavigator.NextCluster(line, col);
            offsets.Add(col);
        }

        return [.. offsets];
    }
}
