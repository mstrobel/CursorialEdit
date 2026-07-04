using CursorialEdit.Document.Buffer;
using CursorialEdit.Layout;

using CursorialEdit.Tests.Layout;

namespace CursorialEdit.Tests.Pipeline;

/// <summary>
/// M1.WP7 — <see cref="BlockRunMap"/>: the Decision-8 per-visual-row run shape (block-relative
/// spans, cell widths from <c>GraphemeWidth</c>, only <see cref="RunKind.Text"/> in M1) and the
/// total source↔cell mapping in both directions — wrap boundaries with end affinity, terminator
/// interiors snapping to row ends, goal cells pinning to cluster boundaries (the WP8 caret's
/// consumption contract). Fixtures reuse <see cref="NavigationFixtures"/>' cluster inventory.
/// </summary>
public sealed class BlockRunMapTests
{
    private static Line L(string text, LineEnding ending = LineEnding.Lf) => new(text, ending, Version: 0);

    // ───────────────────────────── shape ─────────────────────────────

    [Fact]
    public void UnwrappedParagraph_OneRowPerLine_BlockRelativeSpans()
    {
        // "aaa\n" + "bb\r\n" + blank + "c" (unterminated) — mixed endings, blank row included.
        var map = BlockRunMap.Build([L("aaa"), L("bb", LineEnding.CrLf), L(""), new Line("c", LineEnding.None, 0)], wrapWidth: 40);

        Assert.Equal(4, map.RowCount);
        Assert.Equal(4 + 4 + 1 + 1, map.SourceLength);

        Assert.Equal(new Run(0, 3, 0, 3, RunKind.Text), map.RunsForRow(0)[0]);
        Assert.Equal(new Run(4, 2, 0, 2, RunKind.Text), map.RunsForRow(1)[0]);
        Assert.Equal(new Run(8, 0, 0, 0, RunKind.Text), map.RunsForRow(2)[0]); // the blank row: zero-length text run
        Assert.Equal(new Run(9, 1, 0, 1, RunKind.Text), map.RunsForRow(3)[0]);

        Assert.Equal("aaa", map.RowText(0).ToString());
        Assert.True(map.RowText(2).IsEmpty);
    }

    [Fact]
    public void WrappedLine_RowsFollowCaretNavigatorWrap()
    {
        // Wrap28Fixture: 10 a's + space + 22 b's; width 28 breaks after the space → [0,11) + [11,33).
        var map = BlockRunMap.Build([L(NavigationFixtures.Wrap28Fixture)], wrapWidth: 28);

        Assert.Equal(2, map.RowCount);
        Assert.Equal(new Run(0, 11, 0, 11, RunKind.Text), map.RunsForRow(0)[0]);
        Assert.Equal(new Run(11, 22, 0, 22, RunKind.Text), map.RunsForRow(1)[0]);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbb", map.RowText(1).ToString());
    }

    [Fact]
    public void WideClusters_MeasureInCells_NeverStraddleTheWrapEdge()
    {
        // 27 a's then CJK, no whitespace: the 2-cell cluster at the edge moves whole to row 1.
        var map = BlockRunMap.Build([L(NavigationFixtures.StraddleFixture)], wrapWidth: 28);

        Assert.Equal(2, map.RowCount);
        Assert.Equal(27, map.RowWidth(0)); // 27 cells of 'a' — 漢 (2 cells) would straddle, so it wrapped
        Assert.Equal("漢漢漢", map.RowText(1).ToString());
        Assert.Equal(6, map.RowWidth(1));
    }

    [Fact]
    public void ClusterFixtureRow_CellsMatchTheHandComputedMap()
    {
        var map = BlockRunMap.Build([L(NavigationFixtures.ClusterFixture)], wrapWidth: 40);

        Assert.Equal(1, map.RowCount);
        Assert.Equal(11, map.RowWidth(0));

        // Source→cell over every hand-computed cluster boundary (NavigationFixtures pins both arrays).
        for (var i = 0; i < NavigationFixtures.ClusterBoundaries.Length; i++)
        {
            var (row, cell) = map.Locate(NavigationFixtures.ClusterBoundaries[i]);
            Assert.Equal(0, row);
            Assert.Equal(NavigationFixtures.ClusterCells[i], cell);
        }

        // Cell→source pins to the cluster boundary at or before the cell (inside 👍 → before it).
        Assert.Equal(4, map.OffsetAt(0, 5));
        Assert.Equal(NavigationFixtures.ClusterFixture.Length, map.OffsetAt(0, 99)); // clamps to row end
    }

    // ───────────────────────────── total mapping ─────────────────────────────

    [Fact]
    public void Locate_WrapBoundary_ResolvesByAffinity()
    {
        var map = BlockRunMap.Build([L(NavigationFixtures.Wrap28Fixture)], wrapWidth: 28);

        Assert.Equal((1, 0), map.Locate(11));                    // start affinity: next row's start
        Assert.Equal((0, 11), map.Locate(11, endAffinity: true)); // end affinity: earlier row's visual end
    }

    [Fact]
    public void Locate_TerminatorInterior_SnapsToItsLinesRowEnd()
    {
        var map = BlockRunMap.Build([L("ab", LineEnding.CrLf), L("cd")], wrapWidth: 40);

        Assert.Equal((0, 2), map.Locate(2)); // at the terminator
        Assert.Equal((0, 2), map.Locate(3)); // inside the CRLF pair — no cell renders it; snap to row end
        Assert.Equal((1, 0), map.Locate(4)); // the next line's start
        Assert.Equal((1, 2), map.Locate(99)); // clamps to the block's end
        Assert.Equal((0, 0), map.Locate(-5)); // clamps to the block's start
    }

    [Fact]
    public void LineStartAfterHardBreak_IsNotAWrapBoundary_EndAffinityStays()
    {
        var map = BlockRunMap.Build([L("ab"), L("cd")], wrapWidth: 40);

        // Offset 3 = start of line 2's row; the previous row belongs to another source line,
        // so end affinity must NOT pull the position back across the hard break.
        Assert.Equal((1, 0), map.Locate(3, endAffinity: true));
    }

    [Fact]
    public void RoundTrip_OffsetToCellToOffset_IsStableOnClusterBoundaries()
    {
        var map = BlockRunMap.Build([L(NavigationFixtures.ThreeRowFixture)], wrapWidth: 28);
        Assert.Equal(3, map.RowCount);

        foreach (var offset in new[] { 0, 5, 28, 30, 32, 40, NavigationFixtures.ThreeRowFixture.Length })
        {
            var (row, cell) = map.Locate(offset);
            Assert.Equal(offset, map.OffsetAt(row, cell));
        }
    }

    [Fact]
    public void Build_RequiresAtLeastOneLine()
    {
        Assert.Throws<ArgumentException>(static () => BlockRunMap.Build([], wrapWidth: 10));
    }
}
