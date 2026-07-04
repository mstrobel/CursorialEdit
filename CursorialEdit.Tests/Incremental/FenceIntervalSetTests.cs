using CursorialEdit.Document.Parsing;

namespace CursorialEdit.Tests.Incremental;

/// <summary>
/// M2.WP3 — unit coverage for <see cref="FenceIntervalSet"/>: the cheap line-scan that finds fenced
/// code, front-matter, and <c>$$</c> math regions so the reparse window never starts inside a fence and
/// extends to EOF on a fence-parity flip.
/// </summary>
public sealed class FenceIntervalSetTests
{
    private static FenceIntervalSet Scan(params string[] lines) => FenceIntervalSet.FromLines(lines);

    [Fact]
    public void ClosedCodeFence_IsOneRegion()
    {
        var set = Scan("intro", "", "```", "code", "```", "", "after");
        var region = Assert.Single(set.Regions);
        Assert.Equal(new FenceIntervalSet.Region(2, 4, Closed: true), region);
        Assert.True(set.Contains(3));
        Assert.False(set.Contains(6));
    }

    [Fact]
    public void UnclosedCodeFence_RunsToEndOfDocument()
    {
        var set = Scan("```", "still code", "and more");
        var region = Assert.Single(set.Regions);
        Assert.Equal(new FenceIntervalSet.Region(0, 2, Closed: false), region);
    }

    [Fact]
    public void TildeFence_AndInfoStringWithBacktickInTilde_AreRecognized()
    {
        var set = Scan("~~~", "```not a close inside a tilde fence", "~~~");
        Assert.Equal(new FenceIntervalSet.Region(0, 2, Closed: true), Assert.Single(set.Regions));
    }

    [Fact]
    public void BacktickFence_WithBacktickInInfoString_IsNotAFence()
    {
        // A back-tick fence's info string may not contain a back-tick (CommonMark).
        var set = Scan("``` foo`bar", "not code");
        Assert.Empty(set.Regions);
    }

    [Fact]
    public void FrontMatter_OnlyAtDocumentStart()
    {
        var atStart = Scan("---", "title: x", "---", "body");
        Assert.Equal(new FenceIntervalSet.Region(0, 2, Closed: true), Assert.Single(atStart.Regions));

        var midDocument = Scan("body", "", "---", "not: frontmatter", "---");
        Assert.Empty(midDocument.Regions); // a mid-document --- run is a thematic break / setext, not front matter
    }

    [Fact]
    public void MathBlock_OpensOnlyOnAnExactDollarDollarLine()
    {
        Assert.Equal(new FenceIntervalSet.Region(0, 2, Closed: true), Assert.Single(Scan("$$", "x = y", "$$").Regions));

        // `$$1.` and `$$x` are paragraph text, never a math fence (verified against Markdig).
        Assert.Empty(Scan("$$1. ", "text").Regions);
        Assert.Empty(Scan("$$x$$", "text").Regions);
    }

    [Fact]
    public void StartsInsideRegion_IsFalseAtTheOpeningLine_TrueInTheInterior()
    {
        var set = Scan("```", "a", "b", "```");
        Assert.False(set.StartsInsideRegion(0)); // the opening fence line — a window may start here
        Assert.True(set.StartsInsideRegion(1));
        Assert.True(set.StartsInsideRegion(3)); // the closing fence line is still interior
        Assert.False(set.StartsInsideRegion(4));
    }

    [Fact]
    public void ExtendExclusiveEndPastRegion_PushesABoundaryOutOfAFence()
    {
        var set = Scan("p", "", "```", "a", "```", "", "tail"); // region [2,4]
        Assert.Equal(2, set.ExtendExclusiveEndPastRegion(2)); // fence entirely after the window — unchanged
        Assert.Equal(5, set.ExtendExclusiveEndPastRegion(3)); // cuts the fence → pushed past its end
        Assert.Equal(5, set.ExtendExclusiveEndPastRegion(5)); // just past the fence — unchanged
    }

    [Fact]
    public void HasBareCarriageReturn_DetectsALoneCr()
    {
        Assert.False(Scan("plain", "lines").HasBareCarriageReturn);
        Assert.True(Scan("has a \r bare cr").HasBareCarriageReturn);
    }

    [Fact]
    public void BareCr_SplitsIntoMarkdigLines_ForFenceDetection()
    {
        // A lone CR is a Markdig line break, so the fence opens even though the buffer keeps it as one line.
        var set = Scan("intro\r```", "code", "```");
        Assert.Equal(new FenceIntervalSet.Region(0, 2, Closed: true), Assert.Single(set.Regions));
    }
}
