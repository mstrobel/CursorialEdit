using Cursorial.Rendering.Text;

using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.NavigationFixtures;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// M1.WP6 — pure-function coverage of <see cref="CaretNavigator"/>/<see cref="WrappedLine"/>: cluster
/// boundaries, word motion, col↔cell mapping, wrap segmentation, and affinity semantics, on the same
/// grapheme fixtures the R4 probe (<see cref="TextNavigationProbeTests"/>) drives through a real
/// <c>TextBox</c>. Expected values here are hand-computed (pinned), so a shared bug in navigator and
/// reference cannot silently pass the parity probe.
/// </summary>
public sealed class CaretNavigatorTests
{
    // ClusterFixture / ClusterBoundaries / ClusterCells come from NavigationFixtures —
    // shared with the R4 probe suite so the two suites' cluster inventory is identical
    // by construction.

    // ───────────────────────────── cluster boundaries ─────────────────────────────

    [Fact]
    public void NextCluster_WalksEveryBoundary_NeverInsideACluster()
    {
        var col = 0;
        foreach (var expected in ClusterBoundaries.Skip(1))
        {
            col = CaretNavigator.NextCluster(ClusterFixture, col);
            Assert.Equal(expected, col);
        }

        Assert.Equal(ClusterFixture.Length, CaretNavigator.NextCluster(ClusterFixture, col)); // clamped at the end
    }

    [Fact]
    public void PrevCluster_WalksEveryBoundary_Backwards()
    {
        var col = ClusterFixture.Length;
        foreach (var expected in ClusterBoundaries.Reverse().Skip(1))
        {
            col = CaretNavigator.PrevCluster(ClusterFixture, col);
            Assert.Equal(expected, col);
        }

        Assert.Equal(0, CaretNavigator.PrevCluster(ClusterFixture, 0)); // clamped at the start
    }

    [Theory]
    [InlineData(9, 8)]   // inside the family ZWJ sequence → its start
    [InlineData(18, 8)]  // last unit of the family sequence → its start
    [InlineData(2, 1)]   // between e and U+0301 → the é start
    [InlineData(7, 6)]   // between U+2764 and VS16 → the ❤️ start
    [InlineData(4, 4)]   // already a boundary → unchanged
    [InlineData(-5, 0)]  // clamped
    [InlineData(99, 20)] // clamped
    public void SnapToCluster_PinsAtOrBefore(int col, int expected)
        => Assert.Equal(expected, CaretNavigator.SnapToCluster(ClusterFixture, col));

    [Fact]
    public void IsClusterBoundary_TrueExactlyOnBoundaries()
    {
        for (var col = 0; col <= ClusterFixture.Length; col++)
            Assert.Equal(ClusterBoundaries.Contains(col), CaretNavigator.IsClusterBoundary(ClusterFixture, col));
    }

    // ───────────────────────────── col ↔ cell ─────────────────────────────

    [Fact]
    public void CellOfCol_MeasuresClusterWidths()
    {
        for (var i = 0; i < ClusterBoundaries.Length; i++)
            Assert.Equal(ClusterCells[i], CaretNavigator.CellOfCol(ClusterFixture, ClusterBoundaries[i]));

        Assert.Equal(8, CaretNavigator.CellOfCol(ClusterFixture, 12)); // mid-cluster floors to the cluster's cell
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]   // goal 3 falls inside 漢 (cells 2..4) → snaps before it
    [InlineData(4, 4)]
    [InlineData(5, 4)]   // goal 5 falls inside 👍 (cells 4..6) → snaps before it
    [InlineData(9, 8)]   // goal 9 falls inside the family cluster (cells 8..10)
    [InlineData(11, 20)]
    [InlineData(99, 20)] // beyond the line → line end
    public void ColAtOrBeforeCell_LandsOnNearestBoundaryAtOrBeforeGoal(int cell, int expectedCol)
        => Assert.Equal(expectedCol, CaretNavigator.ColAtOrBeforeCell(ClusterFixture, cell));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 4)]   // inside 漢 → after it
    [InlineData(4, 4)]
    [InlineData(9, 19)]  // inside the family cluster → after it
    [InlineData(99, 20)] // beyond → line end
    public void ColAtOrAfterCell_LandsOnFirstBoundaryAtOrAfter(int cell, int expectedCol)
        => Assert.Equal(expectedCol, CaretNavigator.ColAtOrAfterCell(ClusterFixture, cell));

    [Fact]
    public void VariationSelectors_WidthSemantics_VS16Wide_VS15Narrow()
    {
        Assert.Equal(2, CaretNavigator.CellOfCol("\u2764\uFE0Fx", 2)); // ❤️ (VS16) = 2 cells
        Assert.Equal(1, CaretNavigator.CellOfCol("\u2714\uFE0Ex", 2)); // ✔︎ (VS15) = 1 cell
    }

    // ───────────────────────────── word motion ─────────────────────────────

    [Fact]
    public void NextWord_LandsAfterEachWhitespaceDelimitedRun()
    {
        int[] expected = [4, 12, 15, 19];
        var col = 0;
        foreach (var stop in expected)
        {
            col = CaretNavigator.NextWord(WordFixture, col);
            Assert.Equal(stop, col);
        }

        Assert.Equal(19, CaretNavigator.NextWord(WordFixture, 19)); // end of line: stays
    }

    [Fact]
    public void PrevWord_LandsAtEachRunStart()
    {
        int[] expected = [16, 13, 5, 0];
        var col = WordFixture.Length;
        foreach (var stop in expected)
        {
            col = CaretNavigator.PrevWord(WordFixture, col);
            Assert.Equal(stop, col);
        }

        Assert.Equal(0, CaretNavigator.PrevWord(WordFixture, 0)); // start of line: stays
    }

    [Fact]
    public void WordMotion_ResultsAreAlwaysClusterBoundaries()
    {
        // The navigator invariant (M1: caret Col is always a cluster boundary), swept over every
        // start col of every fixture, both directions.
        foreach (var text in new[] { ClusterFixture, WordFixture, Wrap28Fixture, EmojiWordFixture })
        {
            for (var col = 0; col <= text.Length; col++)
            {
                Assert.True(CaretNavigator.IsClusterBoundary(text, CaretNavigator.NextWord(text, col)));
                Assert.True(CaretNavigator.IsClusterBoundary(text, CaretNavigator.PrevWord(text, col)));
                Assert.True(CaretNavigator.IsClusterBoundary(text, CaretNavigator.NextCluster(text, col)));
                Assert.True(CaretNavigator.IsClusterBoundary(text, CaretNavigator.PrevCluster(text, col)));
            }
        }
    }

    // ───────────────────────────── wrap segmentation ─────────────────────────────

    [Fact]
    public void Wrap_WordWrap_BreaksAfterWhitespaceCluster_TrailingSpaceStaysOnRow()
    {
        var wrapped = CaretNavigator.Wrap(Wrap28Fixture, 28);
        Assert.Equal(2, wrapped.RowCount);
        Assert.Equal(0, wrapped.RowStart(0));
        Assert.Equal(11, wrapped.RowEnd(0));   // includes the trailing space
        Assert.Equal(11, wrapped.RowStart(1)); // the affinity-ambiguous wrap col
        Assert.Equal(33, wrapped.RowEnd(1));
        Assert.Equal(11, wrapped.RowWidth(0));
        Assert.Equal(22, wrapped.RowWidth(1));
    }

    [Fact]
    public void Wrap_WordWrap_WideClusterNeverStraddlesTheEdge()
    {
        // 27 a's then CJK: 27+2 > 28 with no whitespace → hard break BEFORE the wide cluster.
        var wrapped = CaretNavigator.Wrap(StraddleFixture, 28);
        Assert.Equal(2, wrapped.RowCount);
        Assert.Equal(27, wrapped.RowStart(1));
        Assert.Equal(27, wrapped.RowWidth(0)); // 27, not 28 — the wide cluster moved whole
        Assert.Equal(6, wrapped.RowWidth(1));
    }

    [Fact]
    public void Wrap_WordWrapOverflow_KeepsOverlongWordWhole()
    {
        var wrapped = CaretNavigator.Wrap("aaaaaaaaaa " + new string('b', 36), 28, WrapMode.WordWrapOverflow);
        Assert.Equal(2, wrapped.RowCount);
        Assert.Equal(11, wrapped.RowStart(1));
        Assert.Equal(36, wrapped.RowWidth(1)); // overflows the 28-cell budget by design
    }

    [Fact]
    public void Wrap_CharacterWrap_BreaksAtExactWidth()
    {
        var wrapped = CaretNavigator.Wrap("abcdef", 3, WrapMode.CharacterWrap);
        Assert.Equal(2, wrapped.RowCount);
        Assert.Equal(3, wrapped.RowStart(1));
    }

    [Fact]
    public void Wrap_NoWrapOrFits_YieldsOneRow()
    {
        Assert.Equal(1, CaretNavigator.Wrap(Wrap28Fixture, 0).RowCount);
        Assert.Equal(1, CaretNavigator.Wrap(Wrap28Fixture, 28, WrapMode.NoWrap).RowCount);
        Assert.Equal(1, CaretNavigator.Wrap("short", 28).RowCount);
        Assert.Equal(1, CaretNavigator.Wrap("", 28).RowCount);
    }

    // ───────────────────────────── affinity + vertical motion ─────────────────────────────

    [Fact]
    public void Locate_WrapBoundaryCol_ResolvesPerAffinity()
    {
        var wrapped = CaretNavigator.Wrap(Wrap28Fixture, 28);

        Assert.Equal((1, 0), wrapped.Locate(11));                    // natural: next row's start
        Assert.Equal((0, 11), wrapped.Locate(11, endAffinity: true)); // end-affinity: earlier row's end

        Assert.True(wrapped.IsRowEndBoundary(0, 11));
        Assert.False(wrapped.IsRowEndBoundary(1, 33)); // the line's true end has no next row — no aliasing
        Assert.False(wrapped.IsRowEndBoundary(0, 5));
    }

    [Fact]
    public void HomeEnd_PerRowLandings_CarryAffinity()
    {
        var wrapped = CaretNavigator.Wrap(Wrap28Fixture, 28);

        Assert.Equal(0, wrapped.HomeCol(5));
        Assert.Equal((11, true), wrapped.EndCol(5));            // row 0's End = the wrap col, end-affinity
        Assert.Equal(0, wrapped.HomeCol(11, endAffinity: true)); // after End, Home belongs to row 0
        Assert.Equal(11, wrapped.HomeCol(11));                   // without affinity the col is row 1's start
        Assert.Equal((33, false), wrapped.EndCol(20));           // row 1's End = line end, no aliasing
    }

    [Fact]
    public void MoveVertical_StickyGoalColumn_SurvivesAShortMiddleRow()
    {
        var wrapped = CaretNavigator.Wrap(ThreeRowFixture, 28);
        Assert.Equal(3, wrapped.RowCount);
        Assert.Equal(28, wrapped.RowStart(1));
        Assert.Equal(31, wrapped.RowStart(2));

        // Down from row 0 cell 10: row 1 is 3 cells wide → clamp to its end, which IS the next wrap col
        // → end-affinity keeps the caret rendered on row 1.
        var (col1, goal1, affinity1) = wrapped.MoveVertical(10, +1);
        Assert.Equal((31, 10, true), (col1, goal1, affinity1));

        // Down again with the sticky goal: row 2 lands back at cell 10.
        var (col2, goal2, affinity2) = wrapped.MoveVertical(col1, +1, goal1, affinity1);
        Assert.Equal((41, 10, false), (col2, goal2, affinity2));

        // Up twice returns to row 0 cell 10.
        var (col3, goal3, affinity3) = wrapped.MoveVertical(col2, -1, goal2, affinity2);
        Assert.Equal((31, 10, true), (col3, goal3, affinity3));
        var (col4, _, affinity4) = wrapped.MoveVertical(col3, -1, goal3, affinity3);
        Assert.Equal((10, false), (col4, affinity4));
    }

    [Fact]
    public void MoveVertical_GoalInsideWideCluster_SnapsBeforeIt()
    {
        // Row 1 is CJK: goal cell 5 falls inside the third 汉 (cells 4..6) → land at cell 4.
        var wrapped = CaretNavigator.Wrap(CjkRowFixture, 28);
        Assert.Equal(2, wrapped.RowCount);

        var (col, goal, _) = wrapped.MoveVertical(5, +1);
        Assert.Equal(30, col); // row start 28 + two CJK units
        Assert.Equal(5, goal); // the goal stays 5 — Up returns to cell 5 exactly
        Assert.Equal((5, 5, false), wrapped.MoveVertical(col, -1, goal, false));
    }

    [Fact]
    public void MoveVertical_AtEdges_ClampsToTheSameRow()
    {
        var wrapped = CaretNavigator.Wrap(Wrap28Fixture, 28);

        Assert.Equal((5, 5, false), wrapped.MoveVertical(5, -1));                    // Up at row 0: stays
        Assert.Equal((16, 5, false), wrapped.MoveVertical(16, +1));                  // Down at last row: stays
        Assert.Equal((0, 0, false), CaretNavigator.Wrap("", 28).MoveVertical(0, 1)); // empty line: inert
    }
}
