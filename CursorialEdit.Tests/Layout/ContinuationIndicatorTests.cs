using Cursorial.Rendering.Text;

using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.RunMapHarness;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// M2.WP5 gate — the clip edge machinery (architecture §2.4 / Decision 8). A row wider than its
/// horizontal viewport draws dim <c>❮</c>/<c>❯</c> continuation indicators (the less/vim idiom) in
/// the edge cells whenever content extends beyond the visible span, and never renders half of a
/// 2-cell cluster: a wide cluster straddling a clip edge becomes blank padding, and an indicator that
/// lands on one half of a whole wide cluster blanks the orphaned other half.
/// </summary>
public sealed class ContinuationIndicatorTests
{
    private static ClippedRow Clip(string text, int slideOffset, int viewport)
    {
        var map = Plain(text, mode: WrapMode.NoWrap);
        Assert.Equal(1, map.RowCount);
        return map.ClipRow(0, slideOffset, viewport);
    }

    /// <summary>No published column is half of a wide cluster: every tail has its head immediately before it.</summary>
    private static void AssertNoHalfClusters(ClippedRow clip)
    {
        for (var i = 0; i < clip.Cells.Count; i++)
        {
            if (clip.Cells[i].Kind != ClipCellKind.Tail)
                continue;
            Assert.True(i > 0
                && clip.Cells[i - 1].Kind == ClipCellKind.Head
                && clip.Cells[i - 1].SrcOffset == clip.Cells[i].SrcOffset,
                $"orphaned tail at column {i}");
        }
    }

    // ───────────────────────────── indicator presence/absence ─────────────────────────────

    [Fact]
    public void NothingClipped_ShowsNoIndicators()
    {
        var clip = Clip("abc", slideOffset: 0, viewport: 10);

        Assert.False(clip.LeftIndicator);
        Assert.False(clip.RightIndicator);
        Assert.Equal(ClipCellKind.Head, clip.Cells[0].Kind);
        Assert.Equal(ClipCellKind.Blank, clip.Cells[9].Kind);
        Assert.DoesNotContain(clip.Cells, c => c.Kind is ClipCellKind.LeftIndicator or ClipCellKind.RightIndicator);
    }

    [Fact]
    public void ContentBeyondRightEdge_ShowsRightIndicatorOnly()
    {
        var clip = Clip(new string('a', 40), slideOffset: 0, viewport: 10);

        Assert.False(clip.LeftIndicator);
        Assert.True(clip.RightIndicator);
        Assert.Equal(ClipCellKind.Head, clip.Cells[0].Kind);
        Assert.Equal(ClipCellKind.RightIndicator, clip.Cells[9].Kind);
        Assert.Equal(ClipCell.RightGlyph, '❯');
    }

    [Fact]
    public void SlidRow_ShowsBothIndicators()
    {
        var clip = Clip(new string('a', 40), slideOffset: 5, viewport: 10);

        Assert.True(clip.LeftIndicator);
        Assert.True(clip.RightIndicator);
        Assert.Equal(ClipCellKind.LeftIndicator, clip.Cells[0].Kind);
        Assert.Equal(ClipCellKind.RightIndicator, clip.Cells[9].Kind);
        Assert.Equal(ClipCell.LeftGlyph, '❮');
    }

    [Fact]
    public void SlidToTheEnd_ShowsLeftIndicatorOnly()
    {
        // Window scrolled to the far end: content is clipped left but not right.
        var clip = Clip(new string('a', 40), slideOffset: 31, viewport: 10); // shows cells [31,41): 40 content + end caret

        Assert.True(clip.LeftIndicator);
        Assert.False(clip.RightIndicator);
        Assert.Equal(ClipCellKind.LeftIndicator, clip.Cells[0].Kind);
    }

    // ───────────────────────────── wide clusters at a clip edge ─────────────────────────────

    [Fact]
    public void WideClusterStraddlingRightEdge_BecomesBlankPadding_NeverHalfRendered()
    {
        // "aaaa漢bb": 漢 occupies cells [4,6); a 5-cell window [0,5) cuts it in half at the right edge.
        var clip = Clip("aaaa漢bb", slideOffset: 0, viewport: 5);

        Assert.True(clip.RightIndicator);
        Assert.Equal(ClipCellKind.RightIndicator, clip.Cells[4].Kind);            // the straddled cell → indicator, not half a 漢
        Assert.DoesNotContain(clip.Cells, c => c.SrcOffset == 4 && c.Kind is ClipCellKind.Head or ClipCellKind.Tail);
        AssertNoHalfClusters(clip);
    }

    [Fact]
    public void WideClusterStraddlingLeftEdge_BecomesBlankPadding_NeverHalfRendered()
    {
        // "a漢aaaa": 漢 occupies cells [1,3); a window [2,6) cuts it in half at the left edge.
        var clip = Clip("a漢aaaa", slideOffset: 2, viewport: 4);

        Assert.True(clip.LeftIndicator);
        Assert.Equal(ClipCellKind.LeftIndicator, clip.Cells[0].Kind);
        Assert.DoesNotContain(clip.Cells, c => c.SrcOffset == 1 && c.Kind is ClipCellKind.Head or ClipCellKind.Tail);
        AssertNoHalfClusters(clip);
    }

    [Fact]
    public void LeftIndicatorOnAWholeWideCluster_BlanksTheOrphanedTail()
    {
        // "aa漢bbbb": 漢 sits whole at cells [2,4); a window [2,6) puts its head at column 0, where the
        // left indicator lands — the orphaned tail at column 1 must blank, not render half a 漢.
        var clip = Clip("aa漢bbbb", slideOffset: 2, viewport: 4);

        Assert.Equal(ClipCellKind.LeftIndicator, clip.Cells[0].Kind);
        Assert.Equal(ClipCellKind.Blank, clip.Cells[1].Kind);
        AssertNoHalfClusters(clip);
    }

    [Fact]
    public void RightIndicatorOnAWholeWideCluster_BlanksTheOrphanedHead()
    {
        // "bbbb漢aa": 漢 sits whole at cells [4,6); a window [0,6) puts its tail at the last column, where
        // the right indicator lands — the orphaned head at column 4 must blank.
        var clip = Clip("bbbb漢aa", slideOffset: 0, viewport: 6);

        Assert.Equal(ClipCellKind.RightIndicator, clip.Cells[5].Kind);
        Assert.Equal(ClipCellKind.Blank, clip.Cells[4].Kind);
        AssertNoHalfClusters(clip);
    }

    // ───────────────────────────── slide + clip together ─────────────────────────────

    [Fact]
    public void SlideThenClip_KeepsCaretVisible_AndFlagsClippedContent()
    {
        const int viewport = 16;
        var map = Plain(new string('a', 80), mode: WrapMode.NoWrap);
        int caretCell = map.Locate(60).Cell;

        int slide = HorizontalSlide.Compute(0, caretCell, map.RowWidth(0), viewport);
        var clip = map.ClipRow(0, slide, viewport);

        int published = caretCell - slide;
        Assert.InRange(published, 0, viewport - 1);                 // the caret is inside the window
        Assert.NotEqual(ClipCellKind.LeftIndicator, clip.Cells[published].Kind);  // …and not hidden under an indicator
        Assert.NotEqual(ClipCellKind.RightIndicator, clip.Cells[published].Kind);
        Assert.True(clip.LeftIndicator && clip.RightIndicator);    // content clipped on both sides is flagged
    }
}
