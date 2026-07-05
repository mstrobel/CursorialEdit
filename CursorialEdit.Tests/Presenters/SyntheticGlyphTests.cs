using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.RunMapHarness;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7 synthetic-glyph gate (WP6 finding 1 — the blocker for quotes/lists): the
/// <see cref="RunMapBuilder"/> now carries, on each <see cref="RunKind.Synthetic"/> run, the display
/// <see cref="Run.Glyph"/> the presenter draws (a bullet, an ordered numeral, a <c>▌</c> quote bar,
/// one per nesting level), while the run's <see cref="Run.SrcStart"/>/<see cref="Run.SrcLen"/> keep
/// pointing at the marker source (the atomic caret stop). It also proves the parallel
/// <see cref="Run.Style"/> projection that lets a presenter render emphasis/strong/code/strike/links
/// formatted without touching the inline AST.
/// </summary>
public sealed class SyntheticGlyphTests
{
    // ───────────────────────────── synthetic glyphs ─────────────────────────────

    [Fact]
    public void UnorderedMarker_CarriesBulletGlyph_AtMarkerCells()
    {
        var run = Map("- item one").SingleRun(0, RunKind.Synthetic);

        Assert.Equal("• ", run.Glyph);                 // • replaces "- " (padded to the marker width)
        Assert.Equal((0, 2, 0), (run.SrcStart, run.SrcLen, run.Col)); // maps to the "- " marker source, at cell 0
        Assert.Equal(2, run.Width);
    }

    [Fact]
    public void OrderedMarker_CarriesItsNumeralGlyph()
    {
        var run = Map("1. first").SingleRun(0, RunKind.Synthetic);

        Assert.Equal("1. ", run.Glyph); // ordered markers keep their numerals
        Assert.Equal((0, 3), (run.SrcStart, run.SrcLen));
    }

    [Fact]
    public void QuoteMarker_CarriesBarGlyph()
    {
        var run = Map("> quoted").SingleRun(0, RunKind.Synthetic);

        Assert.Equal("▌ ", run.Glyph); // one bar, padded to the "> " marker width
        Assert.Equal((0, 2), (run.SrcStart, run.SrcLen));
    }

    [Fact]
    public void NestedQuoteMarker_CarriesOneBarPerLevel()
    {
        var run = Map("> outer\n> > nested", blockIndex: 0).RunsForRow(1).ToArray().Single(r => r.Kind == RunKind.Synthetic);

        Assert.StartsWith("▌▌", run.Glyph); // two bars for the depth-2 line
    }

    [Fact]
    public void NonSyntheticRuns_HaveNoGlyph()
    {
        var map = Map("- **bold** item");

        foreach (var run in map.RunsForRow(0).ToArray())
            if (run.Kind != RunKind.Synthetic)
                Assert.Null(run.Glyph);
    }

    [Fact]
    public void SyntheticGlyph_RevealsAsRawMarker_WhenLineIsActive()
    {
        // On the active line the marker reveals as literal source — no synthetic run, so no glyph.
        var map = Map("- item", active: 0);
        Assert.DoesNotContain(map.RunsForRow(0).ToArray(), r => r.Kind == RunKind.Synthetic);
        Assert.Contains(map.RunsForRow(0).ToArray(), r => r.Kind == RunKind.RevealedMark);
    }

    // ───────────────────────────── inline style projection ─────────────────────────────

    [Theory]
    [InlineData("**b** x", RunStyle.Bold, "b")]
    [InlineData("*i* x", RunStyle.Italic, "i")]
    [InlineData("~~s~~ x", RunStyle.Strikethrough, "s")]
    [InlineData("`c` x", RunStyle.Code, "c")]
    [InlineData("[t](/u) x", RunStyle.Link, "t")]
    public void ContentRun_CarriesItsInlineStyle(string markdown, RunStyle expected, string text)
    {
        var map = Map(markdown);
        var styled = map.RunsForRow(0).ToArray().Single(r => r.Kind == RunKind.Text && (r.Style & expected) != 0);

        Assert.True((styled.Style & expected) != 0, $"expected {expected} on '{text}'");
    }

    [Fact]
    public void BareAutolink_SplitsTheContentRun_AtTheLinkBoundary()
    {
        // "see https://z.com now" — no delimiters, but the link portion still carries RunStyle.Link
        // (the style transition splits the content run even without a mark between).
        var map = Map("see https://z.com now");
        var textRuns = map.RunsForRow(0).ToArray().Where(r => r.Kind == RunKind.Text).ToArray();

        Assert.Contains(textRuns, r => (r.Style & RunStyle.Link) != 0);   // the URL
        Assert.Contains(textRuns, r => (r.Style & RunStyle.Link) == 0);   // the plain "see "/" now"
    }

    [Fact]
    public void NestedEmphasis_CombinesFlags()
    {
        // "***x***" → strong + emphasis on the same content.
        var map = Map("***x*** y");
        var run = map.RunsForRow(0).ToArray().Single(r => r.Kind == RunKind.Text && (r.Style & RunStyle.Bold) != 0);

        Assert.True((run.Style & RunStyle.Bold) != 0);
        Assert.True((run.Style & RunStyle.Italic) != 0);
    }
}
