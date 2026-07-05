using System.Text;

using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Benchmarks;

/// <summary>
/// M2.WP13 — lazy inline realization on band re-anchor (Decision 5 / §13): a block's inline runs are
/// projected from the Markdig AST only when its presenter first realizes (measures/draws), so opening a
/// tall document — and re-anchoring the render band by scrolling — realizes only a <b>bounded</b> band's
/// worth of inlines, never O(document). The observable is <c>Block.InlineRunsRealized</c>.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class InlineRealizationTests
{
    private const int BlockCount = 400;

    /// <summary>A tall document: <see cref="BlockCount"/> emphasis-bearing paragraphs, blank-line separated (one block each).</summary>
    private static string TallDocument()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < BlockCount; i++)
            sb.Append("**para ").Append(i).Append("** body text\n\n");
        return sb.ToString();
    }

    private static int RealizedInlineCount(MarkdownEditingHarness h)
    {
        int count = 0;
        for (var i = 0; i < h.Blocks.Count; i++)
            if (h.Blocks[i].InlineRunsRealized)
                count++;
        return count;
    }

    [Fact]
    public void OpeningATallDocument_RealizesOnlyABand_NotEveryBlocksInlines()
    {
        using var h = MarkdownEditingHarness.Create(TallDocument(), columns: 40, rows: 12);

        // The producer parsed all 400 blocks, but only the visible band's presenters have realized —
        // so only a bounded fraction of blocks projected their inlines, not all 400.
        Assert.True(h.Blocks.Count >= BlockCount);
        int realized = RealizedInlineCount(h);
        Assert.True(realized < BlockCount / 4, $"expected a band-bounded realized-inline count, got {realized}/{h.Blocks.Count}");

        // A block far past the viewport was never realized — its inlines are still unprojected.
        Assert.False(h.Blocks[BlockCount - 1].InlineRunsRealized, "the last block must not realize its inlines just from opening");
    }

    [Fact]
    public void ReanchoringToTheEnd_LeavesUnvisitedMiddleBlocksUnrealized()
    {
        using var h = MarkdownEditingHarness.Create(TallDocument(), columns: 40, rows: 12);
        Assert.False(h.Blocks[BlockCount / 2].InlineRunsRealized); // a middle block, unrealized at open

        // Jump the caret (and the scroll band) to the end — a band-crossing re-anchor.
        h.Caret.MoveDocumentEnd(extend: false);
        h.Settle();

        // The end block now realized (it entered the band)…
        Assert.True(h.Blocks[BlockCount - 1].InlineRunsRealized, "the end block realizes when the band re-anchors onto it");

        // …but a middle block the band JUMPED OVER (never covered) stayed unrealized — realization is
        // bounded by the visited bands, not the whole document scrolled past.
        Assert.False(h.Blocks[BlockCount / 2].InlineRunsRealized, "a jumped-over middle block must not realize");
    }
}
