using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Tests.Blocks;

/// <summary>
/// M2.WP2 — the lazy-inline discipline (architecture Decision 5): a block emerges from
/// segmentation/re-adoption with its inline runs <b>unrealized</b>, and they are projected from the
/// Markdig inline AST only on first access. The <see cref="Block.InlineRunsRealized"/> probe proves
/// parsing never realizes them; the projected runs are block-relative (Decision 8) and reproduce
/// their source.
/// </summary>
public sealed class LazyInlineTests
{
    [Fact]
    public void Parsing_DoesNotRealizeAnyBlocksInlineRuns()
    {
        var h = BlockHarness.Create("# Title\n\nA *para* with `code` and a [link](/x).\n\nmore **text** here");

        Assert.All(h.Blocks, block => Assert.False(block.InlineRunsRealized));
    }

    [Fact]
    public void EditingReSegments_ButDoesNotRealizeInlineRuns()
    {
        var h = BlockHarness.Create("alpha\n\nbeta *emphasis*\n\ngamma");

        for (var i = 0; i < 5; i++)
            h.Insert(new TextPosition(2, 4), "z");

        // Segmentation ran five times; not one block realized its inlines.
        Assert.All(h.Blocks, block => Assert.False(block.InlineRunsRealized));
    }

    [Fact]
    public void FirstAccess_RealizesOnlyThatBlock_AndCaches()
    {
        var h = BlockHarness.Create("A *first* para.\n\nA `second` para.");

        var runs = h.Blocks[0].InlineRuns;

        Assert.True(h.Blocks[0].InlineRunsRealized);
        Assert.False(h.Blocks[1].InlineRunsRealized); // untouched neighbor stays lazy
        Assert.NotEmpty(runs);
        Assert.Same(runs, h.Blocks[0].InlineRuns); // cached — same instance on re-access
    }

    [Fact]
    public void InlineRuns_AreBlockRelative_AndReproduceSource()
    {
        // The emphasised block is NOT at document offset 0, so a bug that used absolute offsets would
        // slice the wrong text.
        var h = BlockHarness.Create("intro paragraph\n\nbody with *stressed* word");
        var block = h.Blocks[1];
        string blockText = h.TextOf(1);

        var emphasis = block.InlineRuns.Single(r => r.Kind == InlineRunKind.Emphasis);
        string slice = blockText.Substring(emphasis.SourceStart, emphasis.SourceLength);

        Assert.Equal("*stressed*", slice);
    }

    [Fact]
    public void InlineRuns_ProjectTheEnabledInlineConstructs()
    {
        var h = BlockHarness.Create("**bold** _em_ ~~struck~~ `code` <https://ex.com> and $x$");
        var kinds = h.Blocks[0].InlineRuns.Select(r => r.Kind).ToHashSet();

        Assert.Contains(InlineRunKind.Strong, kinds);
        Assert.Contains(InlineRunKind.Emphasis, kinds);
        Assert.Contains(InlineRunKind.Strikethrough, kinds);
        Assert.Contains(InlineRunKind.Code, kinds);
        Assert.Contains(InlineRunKind.AutoLink, kinds);
        Assert.Contains(InlineRunKind.Math, kinds);
    }

    [Fact]
    public void CodeAndDegenerateBlocks_YieldNoInlineRuns()
    {
        var fenced = BlockHarness.Create("```\nnot *inline* parsed\n```");
        Assert.Empty(fenced.Blocks[0].InlineRuns);

        var empty = BlockHarness.Create("");
        Assert.Empty(empty.Blocks[0].InlineRuns); // degenerate block: no Markdig backing
    }

    [Fact]
    public void ReAdoptedBlock_StillReportsUnrealized_AfterANeighborEdit()
    {
        var h = BlockHarness.Create("first *para*\n\nsecond para");

        // Force-realize the first block, then edit the SECOND block. The first is re-adopted (Reused)
        // as a kept instance, so it stays realized+valid; the reprocessed second stays lazy.
        _ = h.Blocks[0].InlineRuns;
        h.Insert(new TextPosition(2, 6), "!");

        Assert.True(h.Blocks[0].InlineRunsRealized);  // kept instance retains its cache
        Assert.False(h.Blocks[1].InlineRunsRealized); // re-formed block starts lazy
    }
}
