using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.RunMapHarness;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// M2.WP7a review fixes in <c>RunMapBuilder</c>'s hard-break handling:
/// (1) a trailing backslash is a hard break only when UNESCAPED (odd count) — an escaped `\\` is a
/// literal backslash, not a break; (2) the active-line `↵` affordance is a trailing decoration, so the
/// line-end caret maps to the cell BEFORE it, never past it.
/// </summary>
public sealed class HardBreakMappingTests
{
    private static int ActiveRow(RunMap map)
    {
        for (var row = 0; row < map.RowCount; row++)
            if (map.IsActiveRow(row))
                return row;
        throw new Xunit.Sdk.XunitException("no active row");
    }

    private static Run[] HardBreakRuns(RunMap map, int row)
        => [.. map.Runs(row, RunKind.Synthetic).Where(r => r.Glyph == "↵")];

    [Fact]
    public void UnescapedTrailingBackslash_IsAHardBreak()
    {
        // "foo\" (one backslash) followed by a line — a hard break; the active line shows a ↵.
        var map = Plain("foo\\\nbar", active: 0);
        Assert.Single(HardBreakRuns(map, ActiveRow(map)));
    }

    [Fact]
    public void EscapedTrailingBackslash_IsNotAHardBreak()
    {
        // "foo\\" (two backslashes = one literal backslash) — NOT a hard break; no ↵.
        var map = Plain("foo\\\\\nbar", active: 0);
        Assert.Empty(HardBreakRuns(map, ActiveRow(map)));
    }

    [Fact]
    public void ThreeTrailingBackslashes_IsAHardBreak()
    {
        // Odd count: the last backslash stands alone (an escaped pair + one) — a hard break.
        var map = Plain("foo\\\\\\\nbar", active: 0);
        Assert.Single(HardBreakRuns(map, ActiveRow(map)));
    }

    [Fact]
    public void LineEndCaret_OnAHardBreakLine_MapsBeforeTheReturnGlyph()
    {
        // Two trailing spaces → a hard break; the active line hides the spaces and shows a trailing ↵.
        var map = Plain("abc  \nbar", active: 0);
        int row = ActiveRow(map);
        Assert.Single(HardBreakRuns(map, row)); // ↵ present

        int textEnd = "abc  ".Length; // block-relative offset of line 0's text end
        var (locRow, cell) = map.Locate(textEnd, endAffinity: true);

        Assert.Equal(row, locRow);
        // The caret sits before the trailing ↵ decoration, i.e. strictly inside the row's visible cells,
        // not past the affordance (the pre-fix bug placed it at RowWidth, one cell to the ↵'s right).
        Assert.True(cell < map.RowWidth(row), $"caret cell {cell} should be before the ↵ (row width {map.RowWidth(row)})");

        AssertTotalMapping(map); // the reorder keeps the source↔cell map total and round-tripping
    }
}
