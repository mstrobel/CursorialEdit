using Cursorial.Input;

using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Views;

/// <summary>
/// M2.WP9 — reveal + active-block integration on the shell surface. The <c>:active-block</c> well tint
/// (§4.3) is painted behind the block the caret is in and behind no other; and a caret crossing a block
/// boundary re-rasters <b>exactly two zones</b> (the block left and the block entered), asserted through
/// <see cref="CursorialEdit.Presenters.LeafBlockPresenter.RenderCount"/> — the §4.1 no-reflow invariant
/// still holding for every other block.
/// </summary>
public sealed class ActiveBlockTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    [Theory]
    [MemberData(nameof(Presets))]
    public void ActiveBlock_CarriesTheWellTint_OthersDoNot(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("alpha block\n\nbravo block\n\ncharlie block", preset);

        int alphaRow = RowOf(harness, "alpha");
        int bravoRow = RowOf(harness, "bravo");

        // The caret starts at the origin — block 0 ("alpha") is active and its cells carry the well tint;
        // an inactive block's cells (block 1, "bravo") carry the plain background.
        var activeBg = harness.BackgroundAt(0, alphaRow);
        var inactiveBg = harness.BackgroundAt(0, bravoRow);
        Assert.NotEqual(activeBg, inactiveBg);

        // Move the caret into block 1 — the well follows: now "bravo" carries it and "alpha" does not.
        harness.Click(0, bravoRow);
        Assert.Equal(inactiveBg, harness.BackgroundAt(0, alphaRow)); // block 0 lost the tint (now plain)
        Assert.Equal(activeBg, harness.BackgroundAt(0, bravoRow));   // block 1 gained the tint
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void CaretCrossingABlockBoundary_ReRastersExactlyTwoZones(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("one one\n\ntwo two\n\nthree three", preset);

        var before = new[]
        {
            harness.Presenter(0).RenderCount,
            harness.Presenter(1).RenderCount,
            harness.Presenter(2).RenderCount,
        };

        // Cross from block 0 into block 1 (a single boundary crossing).
        harness.Click(0, RowOf(harness, "two"));

        // Exactly the block left (0) and the block entered (1) re-rastered; block 2 never did.
        Assert.Equal(before[0] + 1, harness.Presenter(0).RenderCount);
        Assert.Equal(before[1] + 1, harness.Presenter(1).RenderCount);
        Assert.Equal(before[2], harness.Presenter(2).RenderCount);
    }

    /// <summary>The frame row whose composited text starts with <paramref name="prefix"/> (a block's first content row).</summary>
    private static int RowOf(MarkdownEditingHarness harness, string prefix)
    {
        for (var row = 0; row < 12; row++)
            if (harness.RowTrimmed(row).StartsWith(prefix, StringComparison.Ordinal))
                return row;

        Assert.Fail($"no row starting with '{prefix}'");
        return -1;
    }
}
