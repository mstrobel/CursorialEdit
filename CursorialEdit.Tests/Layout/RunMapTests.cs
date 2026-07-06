using Cursorial.Rendering.Text;
using Cursorial.Text;

using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.NavigationFixtures;
using static CursorialEdit.Tests.Layout.RunMapHarness;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// M2.WP5 gate — the run-map layout (architecture Decision 8). Proves the source↔cell mapping is
/// <b>total in both directions</b> over ASCII, CJK/emoji/ZWJ, and wrap-boundary fixtures in both wrap
/// modes; that a plain block reproduces M1's <see cref="BlockRunMap"/> exactly; and that the three
/// overlay kinds behave as specified — hidden marks are zero-width at their true source positions,
/// revealed marks occupy cells, and synthetic markers map atomically to their marker source.
/// </summary>
public sealed class RunMapTests
{
    // ───────────────────────────── totality (both directions, both wrap modes) ─────────────────────────────

    public static TheoryData<string, int, WrapMode> TotalityFixtures()
    {
        var data = new TheoryData<string, int, WrapMode>();
        string[] texts =
        [
            "hello world",
            ClusterFixture,        // a é 漢 👍 ❤️ family(ZWJ) x — wide + combining + ZWJ
            WordFixture,
            Wrap28Fixture,
            StraddleFixture,       // wide cluster at the wrap edge
            CjkRowFixture,
            "line one\nline two\nline three",
            "",
        ];
        foreach (var text in texts)
            foreach (var width in new[] { 0, 6, 28 })
                foreach (var mode in new[] { WrapMode.WordWrap, WrapMode.NoWrap })
                    data.Add(text, width, mode);
        return data;
    }

    [Theory]
    [MemberData(nameof(TotalityFixtures))]
    public void Mapping_IsTotalBothDirections_AllFixturesAndModes(string text, int width, WrapMode mode)
        => AssertTotalMapping(Plain(text, width, mode));

    [Theory]
    [MemberData(nameof(TotalityFixtures))]
    public void Mapping_IsTotal_WhenLineIsTheRevealedActiveLine(string text, int width, WrapMode mode)
        => AssertTotalMapping(Plain(text, width, mode, active: 0));

    [Theory]
    [InlineData("**bold** and *em* `code` [link](/u) text")]
    [InlineData("# A heading with **strong** inside")]
    [InlineData("- a *marked* list item that is quite long indeed")]
    [InlineData("> a quoted line with `code` and more words")]
    public void Mapping_IsTotal_OverRealMarkdownConstructs(string markdown)
    {
        foreach (var mode in new[] { WrapMode.WordWrap, WrapMode.NoWrap })
            foreach (var active in new int?[] { null, 0 })
                AssertTotalMapping(Map(markdown, wrap: 12, mode: mode, active: active));
    }

    // ───────────────────────────── parity with M1 BlockRunMap (plain text) ─────────────────────────────

    public static TheoryData<string, int> ParityFixtures() => new()
    {
        { "hello world", 0 },
        { ClusterFixture, 0 },
        { ClusterFixture, 6 },
        { Wrap28Fixture, 28 },
        { StraddleFixture, 28 },
        { CjkRowFixture, 28 },
        { "line one\nline two\nfinal", 0 },
    };

    [Theory]
    [MemberData(nameof(ParityFixtures))]
    public void PlainText_ReproducesM1BlockRunMap_ExactlyBothDirections(string text, int width)
    {
        var lines = Lines(text);
        var m1 = BlockRunMap.Build(lines, width);
        var m2 = Plain(text, width);

        Assert.Equal(m1.RowCount, m2.RowCount);
        Assert.Equal(m1.SourceLength, m2.SourceLength);

        for (var src = 0; src <= m1.SourceLength; src++)
        {
            Assert.Equal(m1.Locate(src, endAffinity: false), m2.Locate(src, endAffinity: false));
            Assert.Equal(m1.Locate(src, endAffinity: true), m2.Locate(src, endAffinity: true));
        }

        for (var row = 0; row < m1.RowCount; row++)
        {
            Assert.Equal(m1.RowWidth(row), m2.RowWidth(row));
            for (var cell = 0; cell <= m1.RowWidth(row); cell++)
                Assert.Equal(m1.OffsetAt(row, cell), m2.OffsetAt(row, cell));
        }
    }

    [Fact]
    public void PlainText_ProducesOnlyTextRuns()
    {
        var map = Plain(ClusterFixture);
        Assert.All(map.RunsForRow(0).ToArray(), run => Assert.Equal(RunKind.Text, run.Kind));
    }

    // ───────────────────────────── hidden marks (zero-width, true source position) ─────────────────────────────

    [Fact]
    public void HiddenMark_IsZeroWidth_AtItsTrueSourcePosition()
    {
        // "**bold**" inactive: the ** fences hide; only "bold" (4 cells) renders.
        var map = Map("**bold**");

        Assert.Equal(4, map.RowWidth(0));

        var hidden = map.Runs(0, RunKind.HiddenMark);
        Assert.Equal(2, hidden.Length);
        Assert.All(hidden, run => Assert.Equal(0, run.Width)); // zero visible cells

        Assert.Equal((0, 2, 0), (hidden[0].SrcStart, hidden[0].SrcLen, hidden[0].Col)); // leading ** at cell 0
        Assert.Equal((6, 2, 4), (hidden[1].SrcStart, hidden[1].SrcLen, hidden[1].Col)); // trailing ** at cell 4

        // The map stays total across the hidden marks: every source offset resolves to a cell, and the
        // caret's cell walk structurally skips the hidden fences (cell 0 → the 'b', cell 4 → line end).
        Assert.Equal((0, 0), map.Locate(1)); // inside the leading ** → collapses to cell 0
        Assert.Equal((0, 0), map.Locate(2)); // the 'b'
        Assert.Equal((0, 4), map.Locate(7)); // inside the trailing ** → collapses to cell 4
        Assert.Equal(2, map.OffsetAt(0, 0)); // cell 0 maps to the 'b', not the hidden '*'
        Assert.Equal(8, map.OffsetAt(0, 4)); // cell 4 maps to the line end, skipping the trailing '**'
    }

    [Fact]
    public void HiddenMark_HeadingPrefix_CollapsesToZeroWidth()
    {
        var map = Map("## Title here");

        Assert.Equal(GraphemeWidth.StringWidth("Title here"), map.RowWidth(0));
        var hidden = map.SingleRun(0, RunKind.HiddenMark);
        Assert.Equal((0, 3, 0), (hidden.SrcStart, hidden.SrcLen, hidden.Width)); // "## " hidden, zero cells
        Assert.Equal((0, 0), map.Locate(0));
        Assert.Equal(3, map.OffsetAt(0, 0)); // cell 0 is the 'T', the "## " prefix has no cell
    }

    // ───────────────────────────── revealed marks (occupy cells on the active line) ─────────────────────────────

    [Fact]
    public void RevealedMark_OccupiesCells_OnTheActiveLine()
    {
        var map = Map("**bold**", active: 0);

        Assert.Equal(8, map.RowWidth(0)); // the whole source is shown at natural width

        var revealed = map.Runs(0, RunKind.RevealedMark);
        Assert.Equal(2, revealed.Length);
        Assert.All(revealed, run => Assert.Equal(2, run.Width)); // ** occupies two cells
        Assert.Equal((0, 2, 0), (revealed[0].SrcStart, revealed[0].SrcLen, revealed[0].Col));
        Assert.Equal((6, 2, 6), (revealed[1].SrcStart, revealed[1].SrcLen, revealed[1].Col));

        var text = map.SingleRun(0, RunKind.Text);
        Assert.Equal((2, 4, 2, 4), (text.SrcStart, text.SrcLen, text.Col, text.Width)); // "bold" at cells 2..6
    }

    [Fact]
    public void RevealVsHide_ChangesRenderedWidth_ButNotSourceLength()
    {
        var hidden = Map("a *b* c");   // inactive
        var shown = Map("a *b* c", active: 0);

        Assert.Equal(hidden.SourceLength, shown.SourceLength);
        Assert.True(shown.RowWidth(0) > hidden.RowWidth(0)); // the *…* fences add cells when revealed
    }

    // ───────────────────────────── synthetic markers (map to marker source, one caret stop) ─────────────────────────────

    [Fact]
    public void SyntheticBullet_MapsToItsMarkerSource_AsOneCaretStop()
    {
        var map = Map("- item one");

        var bullet = map.SingleRun(0, RunKind.Synthetic);
        Assert.Equal((0, 2, 0), (bullet.SrcStart, bullet.SrcLen, bullet.Col)); // the "- " marker
        Assert.True(bullet.Width > 0);                                          // a glyph occupies cells

        var text = map.SingleRun(0, RunKind.Text);
        Assert.Equal(2, text.SrcStart); // "item one" starts right after the marker

        // Caret-left from the item text lands before the item as exactly one stop: every cell inside the
        // glyph maps back to the marker's start, and the item's first cell maps just past the marker.
        Assert.Equal(0, map.OffsetAt(0, 0));
        Assert.Equal(0, map.OffsetAt(0, 1));
        Assert.Equal(2, map.OffsetAt(0, bullet.Width));
        Assert.Equal((0, 0), map.Locate(1)); // an offset inside the marker collapses to its one stop
    }

    [Fact]
    public void SyntheticOrderedMarker_KeepsItsNumerals()
    {
        var map = Map("1. first");
        var marker = map.SingleRun(0, RunKind.Synthetic);
        Assert.Equal((0, 3), (marker.SrcStart, marker.SrcLen)); // "1. "
        Assert.Equal(3, marker.Width);
    }

    [Fact]
    public void SyntheticQuoteBar_MapsToTheQuoteMarker()
    {
        var map = Map("> quoted words");
        var bar = map.SingleRun(0, RunKind.Synthetic);
        Assert.Equal((0, 2), (bar.SrcStart, bar.SrcLen)); // the "> " marker
        Assert.True(bar.Width > 0);
        Assert.Equal(2, map.OffsetAt(0, bar.Width)); // "quoted" begins just past the bar
    }

    // ───────────────────────────── wrap modes ─────────────────────────────

    [Fact]
    public void WrapOff_RendersOneRowPerLogicalLine()
    {
        var map = Plain(Wrap28Fixture, wrap: 10, mode: WrapMode.NoWrap); // 33 cells, budget 10
        Assert.Equal(1, map.RowCount);
        Assert.Equal(GraphemeWidth.StringWidth(Wrap28Fixture), map.RowWidth(0));
    }

    [Fact]
    public void WrapOn_BreaksLongLinesIntoRows()
    {
        var map = Plain(Wrap28Fixture, wrap: 28, mode: WrapMode.WordWrap);
        Assert.Equal(2, map.RowCount);
    }

    [Fact]
    public void ActiveLine_IsNeverWrapped_EvenInWrapOnMode()
    {
        // A long single-line paragraph, wrap budget 10, revealed active line 0 → still one slidable row.
        var map = Plain(new string('a', 40), wrap: 10, mode: WrapMode.WordWrap, active: 0);
        Assert.Equal(1, map.RowCount);
        Assert.Equal(40, map.RowWidth(0));
    }

    [Fact]
    public void LoneCarriageReturn_Paragraph_IsOneRow_NotHardBroken()
    {
        // A lone '\r' is in-line content (LineEnding), but TextLayout.Build hard-breaks on it — so it is
        // sanitized to its control picture ␍ (1:1). The paragraph "ab\rcd" stays one row of width 5, not two
        // rows split at the CR. (FB-1 adoption review regression.)
        var map = Plain("ab\rcd", wrap: 40);
        Assert.Equal(1, map.RowCount);
        Assert.Equal(5, map.RowWidth(0));
    }

    [Fact]
    public void LoneCarriageReturn_RawView_KeepsVerbatimOneRow()
    {
        // Raw view's "every source line renders verbatim as one visual row" contract must survive a lone '\r'.
        var map = RunMapBuilder.BuildRaw(Lines("ab\rcd"), wrapWidth: 40);
        Assert.Equal(1, map.RowCount);
        Assert.Equal(5, map.RowWidth(0));
    }
}
