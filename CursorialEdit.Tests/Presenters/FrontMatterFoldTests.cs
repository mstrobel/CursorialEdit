using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7 front-matter fold gate (§2.3 / plan resolution 5): the front-matter block is dim and folded
/// by default; expanding it changes the block's height and fires <see cref="FrontMatterPresenter.HeightChanged"/>
/// (the signal the WP7b host wires to <c>InvalidateScrollExtent()</c>). The harness toggles and
/// re-measures through the real frame loop.
/// </summary>
public sealed class FrontMatterFoldTests
{
    private const string Document = "---\ntitle: x\nauthor: y\n---\n\nBody";

    [Fact]
    public void FoldedByDefault_OneSummaryRow()
    {
        using var harness = PresenterHarness.FromMarkdown(Document);
        var front = (FrontMatterPresenter) harness.Presenters[0];

        Assert.False(front.IsExpanded);
        Assert.Equal(1, harness.Height(0));
        Assert.StartsWith("▸", harness.RowTrimmed(0));
        Assert.Equal("Body", harness.RowTrimmed(1)); // the sibling sits right under the fold
    }

    [Fact]
    public void Expanding_ShowsAllLines_AndGrowsHeight()
    {
        using var harness = PresenterHarness.FromMarkdown(Document);
        var front = (FrontMatterPresenter) harness.Presenters[0];
        int foldedHeight = harness.Height(0);

        front.ToggleFold();
        harness.Settle();

        Assert.True(front.IsExpanded);
        Assert.True(harness.Height(0) > foldedHeight);         // the block grew
        Assert.Contains("title: x", harness.RowTrimmed(1));    // the metadata lines now show
        Assert.Equal("Body", harness.RowTrimmed(harness.TopRow(1))); // the sibling moved down with the growth
    }

    [Fact]
    public void ToggleFold_FiresHeightChanged()
    {
        using var harness = PresenterHarness.FromMarkdown(Document);
        var front = (FrontMatterPresenter) harness.Presenters[0];

        int signals = 0;
        front.HeightChanged += () => signals++;

        front.ToggleFold();
        Assert.Equal(1, signals); // expand
        front.ToggleFold();
        Assert.Equal(2, signals); // collapse
    }

    [Fact]
    public void CollapsingAgain_RestoresTheFold()
    {
        using var harness = PresenterHarness.FromMarkdown(Document);
        var front = (FrontMatterPresenter) harness.Presenters[0];

        front.SetExpanded(true);
        harness.Settle();
        front.SetExpanded(false);
        harness.Settle();

        Assert.False(front.IsExpanded);
        Assert.Equal(1, harness.Height(0));
        Assert.StartsWith("▸", harness.RowTrimmed(0));
    }
}
