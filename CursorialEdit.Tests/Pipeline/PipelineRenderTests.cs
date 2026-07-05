using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Layout;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.Pipeline;

/// <summary>
/// M1.WP7 — the plain-text end-to-end gate, exercised through the REAL M1 pipeline
/// (<c>DocumentBuffer → EditController → PlainTextBlockProducer → BlockViewBridge → EditorControl →
/// DocumentPanel → PlainTextPresenter</c>) under <c>UITestHost</c>. Asserts cell-level rendering
/// (wrapped lines, CJK/emoji rows), the raster economics the milestone is gated on (a keystroke
/// re-rasters EXACTLY one block; in-band scroll re-rasters zero), presenter reuse with
/// <see cref="BlockId"/> stability across splits/merges, and the width-change re-measure path.
/// </summary>
/// <remarks>
/// WP7b swapped the <b>application</b> onto the Markdig pipeline + the presenter suite (see
/// <c>MarkdownRenderTests</c>), but the M1 plain pipeline stays in the tree and keeps its
/// reconciliation contract — this suite now constructs it directly (as <c>EditingHarness</c> does)
/// rather than through <see cref="EditorShell"/>, which now formats markdown.
/// </remarks>
public sealed class PipelineRenderTests
{
    private const int Columns = 28;
    private const int Rows = 11;

    /// <summary>
    /// First paragraph (1 row) · wrap fixture (2 rows at width 28) · CJK (1 row) · emoji (1 row),
    /// blank-separated: 8 rendered rows, 4 blocks under the trailing-blank-attachment policy.
    /// </summary>
    private static readonly string MultiParagraphDoc =
        "First paragraph.\n\n" + NavigationFixtures.Wrap28Fixture + "\n\n汉字汉字 wide\n\n👍👍 ok";

    /// <summary>Both §5.1 wire presets — the shared registry (<see cref="TestSupport.CapabilityPresets"/>).</summary>
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private sealed class Harness(
        UITestHost host, EditorControl editor, DocumentBuffer buffer,
        EditController controller, PlainTextBlockProducer producer, BlockViewBridge bridge) : IDisposable
    {
        public UITestHost Host { get; } = host;

        public EditorControl Editor { get; } = editor;

        public DocumentBuffer Buffer { get; } = buffer;

        public EditController Controller { get; } = controller;

        public PlainTextBlockProducer Producer { get; } = producer;

        public BlockViewBridge Bridge { get; } = bridge;

        public void Dispose() => Host.Dispose();
    }

    private static Harness CreatePipeline(
        string document, string preset = nameof(TestCapabilities.KittyTruecolor), int columns = Columns, int rows = Rows)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
        });

        var buffer = new DocumentBuffer(document);
        var controller = new EditController(buffer, host.Time);
        var producer = new PlainTextBlockProducer(controller);
        var bridge = new BlockViewBridge(buffer, producer);
        var editor = new EditorControl();
        editor.AttachDocument(controller, bridge);

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");
        return new Harness(host, editor, buffer, controller, producer, bridge);
    }

    private static CaretState Caret(TextPosition position) => new(position);

    private static void Type(EditController controller, int line, int col, string removed, string inserted)
    {
        var start = new TextPosition(line, col);
        controller.Apply(new Edit(start, removed, inserted), EditKind.Typing, Caret(start), Caret(start));
    }

    private static IReadOnlyDictionary<int, PlainTextPresenter> Realized(EditorControl editor)
        => editor.DocumentPanelPart!.RealizedBlocks.ToDictionary(kv => kv.Key, kv => (PlainTextPresenter)kv.Value);

    // ───────────────────────────── rendering through the real pipeline ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void MultiParagraphDocument_RendersWrappedCjkAndEmojiRows(string preset)
    {
        using var harness = CreatePipeline(MultiParagraphDoc, preset);
        var host = harness.Host;

        Assert.Equal("First paragraph.", host.GetRowText(0).TrimEnd());
        Assert.Equal("", host.GetRowText(1).TrimEnd());
        Assert.Equal("aaaaaaaaaa", host.GetRowText(2).TrimEnd());             // wrap row 1 (trailing space stays on the row)
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbb", host.GetRowText(3).TrimEnd()); // wrap row 2
        Assert.Equal("", host.GetRowText(4).TrimEnd());
        Assert.Equal("汉字汉字 wide", host.GetRowText(5).TrimEnd());           // wide clusters, whole-cell
        Assert.Equal("", host.GetRowText(6).TrimEnd());
        Assert.Equal("👍👍 ok", host.GetRowText(7).TrimEnd());                 // emoji clusters

        // The published extent is the wrap-row prefix total — heights are live, not line counts.
        Assert.Equal(8, harness.Editor.ScrollViewerPart!.Extent.Rows);

        // One presenter per block, each its own render boundary (Decision 7).
        var realized = Realized(harness.Editor);
        Assert.Equal(4, realized.Count);
        Assert.All(realized.Values, presenter => Assert.True(presenter.IsRenderBoundary));
    }

    // ───────────────────────────── raster economics (the done-when) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Keystroke_ReRastersExactlyOneBlock_SiblingPresentersReused(string preset)
    {
        using var harness = CreatePipeline(MultiParagraphDoc, preset);
        var host = harness.Host;

        var before = Realized(harness.Editor);
        var countsBefore = before.ToDictionary(kv => kv.Key, kv => kv.Value.RenderCount);
        var idsBefore = before.ToDictionary(kv => kv.Key, kv => kv.Value.Block);

        Type(harness.Controller, line: 0, col: 16, removed: "", inserted: "!"); // same-height edit inside block 0
        Assert.True(host.RunUntilIdle());

        Assert.Equal("First paragraph.!", host.GetRowText(0).TrimEnd());

        var after = Realized(harness.Editor);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (index, presenter) in after)
        {
            Assert.Same(before[index], presenter);              // element reuse across the change
            Assert.Equal(idsBefore[index], presenter.Block);    // BlockId stability
            Assert.Equal(
                countsBefore[index] + (index == 0 ? 1 : 0),     // EXACTLY one block re-rastered
                presenter.RenderCount);
        }
    }

    [Fact]
    public void InBandScroll_StillReRastersNothing_ThroughTheRealPipeline()
    {
        // 30 one-line paragraphs (2 rows each incl. the trailing blank) — extent 59 ≫ viewport.
        var document = string.Join("\n\n", Enumerable.Range(0, 30).Select(i => $"Paragraph {i:D2}"));
        using var harness = CreatePipeline(document);
        var host = harness.Host;

        var scrollViewer = harness.Editor.ScrollViewerPart!;
        Assert.Equal(59, scrollViewer.Extent.Rows); // 29×2 + the unterminated last paragraph's 1

        var panel = harness.Editor.DocumentPanelPart!;
        var before = Realized(harness.Editor);
        var countsBefore = before.ToDictionary(kv => kv.Key, kv => kv.Value.RenderCount);
        var realizedBefore = panel.TotalRealizedBlocks;

        scrollViewer.ScrollBy(0, 3); // well within K — a pure composite slide
        Assert.True(host.RunUntilIdle());
        Assert.Equal(3, scrollViewer.VerticalOffset);

        // Doc row r+3 lands on viewport row r: paragraph i sits at doc row 2i, so viewport row 1
        // (doc row 4) shows "Paragraph 02" and viewport row 0 (doc row 3) is a blank separator.
        Assert.Equal("", host.GetRowText(0)[..20].TrimEnd());
        Assert.Equal("Paragraph 02", host.GetRowText(1)[..20].TrimEnd());
        Assert.Equal(realizedBefore, panel.TotalRealizedBlocks);    // no realization churn
        foreach (var (index, presenter) in Realized(harness.Editor))
            Assert.Equal(countsBefore[index], presenter.RenderCount); // zero block re-raster
    }

    // ───────────────────────────── split / merge (BlockListChange end to end) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ParagraphSplit_AddsBlock_ReusesShiftedSiblings(string preset)
    {
        using var harness = CreatePipeline("alpha\n\nbravo\n\ncharlie", preset);
        var host = harness.Host;

        var ids = harness.Producer.Blocks.Select(b => b.Id).ToArray();
        var before = Realized(harness.Editor);
        Assert.Equal(3, before.Count);

        BlockListChange? change = null;
        harness.Producer.Changed += c => change = c;

        Type(harness.Controller, line: 2, col: 3, removed: "", inserted: "\n\n"); // "bra" ¶ "vo" — a real paragraph split
        Assert.True(host.RunUntilIdle());

        // The reconciliation contract: split → Added + Reused with shift (+ the split block Changed).
        Assert.NotNull(change);
        Assert.Equal([ids[1]], change!.Changed);
        Assert.Single(change.Added);
        Assert.Equal([ids[0], ids[2]], change.Reused);
        Assert.Empty(change.Removed);
        Assert.Equal(2, change.LineShift);

        // Sibling presenters were REUSED across the index shift (charlie: index 2 → 3), ids stable.
        var after = Realized(harness.Editor);
        Assert.Equal(4, after.Count);
        Assert.Same(before[0], after[0]);
        Assert.Same(before[1], after[1]);
        Assert.Same(before[2], after[3]);
        Assert.Equal(ids[2], after[3].Block);
        Assert.Equal(change.Added[0], after[2].Block); // the new paragraph got a fresh presenter

        Assert.Equal("alpha", host.GetRowText(0).TrimEnd());
        Assert.Equal("bra", host.GetRowText(2).TrimEnd());
        Assert.Equal("vo", host.GetRowText(4).TrimEnd());
        Assert.Equal("charlie", host.GetRowText(6).TrimEnd());
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void ParagraphMerge_RemovesSwallowedBlock_TearsDownItsPresenter(string preset)
    {
        using var harness = CreatePipeline("alpha\n\nbravo\n\ncharlie", preset);
        var host = harness.Host;

        var ids = harness.Producer.Blocks.Select(b => b.Id).ToArray();
        var panel = harness.Editor.DocumentPanelPart!;
        var before = Realized(harness.Editor);
        var derealizedBefore = panel.TotalDerealizedBlocks;

        BlockListChange? change = null;
        harness.Producer.Changed += c => change = c;

        Type(harness.Controller, line: 1, col: 0, removed: "\n", inserted: ""); // delete the separator: alpha+bravo merge
        Assert.True(host.RunUntilIdle());

        Assert.NotNull(change);
        Assert.Equal([ids[0]], change!.Changed);
        Assert.Equal([ids[1]], change.Removed);
        Assert.Equal([ids[2]], change.Reused);
        Assert.Empty(change.Added);
        Assert.Equal(-1, change.LineShift);

        // The swallowed block's presenter is gone (torn down + deregistered); survivors reused.
        Assert.Equal(derealizedBefore + 1, panel.TotalDerealizedBlocks);
        Assert.Null(harness.Bridge.GetPresenter(ids[1]));

        var after = Realized(harness.Editor);
        Assert.Equal(2, after.Count);
        Assert.Same(before[0], after[0]);
        Assert.Same(before[2], after[1]);

        Assert.Equal("alpha", host.GetRowText(0).TrimEnd());
        Assert.Equal("bravo", host.GetRowText(1).TrimEnd());
        Assert.Equal("", host.GetRowText(2).TrimEnd());
        Assert.Equal("charlie", host.GetRowText(3).TrimEnd());
    }

    // ───────────────────────────── width change (live heights) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void WidthChange_ReWrapsAndReMeasures_ExtentFollows(string preset)
    {
        using var harness = CreatePipeline(MultiParagraphDoc, preset);
        var host = harness.Host;
        Assert.Equal(8, harness.Editor.ScrollViewerPart!.Extent.Rows); // wrap fixture spans 2 rows at 28

        host.SendResize(60, Rows);
        Assert.True(host.RunUntilIdle());

        // The wrap fixture (33 cells) fits one row at width 60 — heights are live wrap-row counts.
        Assert.Equal(7, harness.Editor.ScrollViewerPart.Extent.Rows);
        Assert.Equal("First paragraph.", host.GetRowText(0).TrimEnd());
        Assert.Equal(NavigationFixtures.Wrap28Fixture, host.GetRowText(2).TrimEnd());
        Assert.Equal("汉字汉字 wide", host.GetRowText(4).TrimEnd());
        Assert.Equal("👍👍 ok", host.GetRowText(6).TrimEnd());

        host.SendResize(Columns, Rows);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(8, harness.Editor.ScrollViewerPart.Extent.Rows); // narrow again → re-wrapped
        Assert.Equal("aaaaaaaaaa", host.GetRowText(2).TrimEnd());
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbb", host.GetRowText(3).TrimEnd());
    }

    [Fact]
    public void ResizeStorm_LargeDocument_RewrapsOnlyTheRealizedBand_ExtentStaysHonest()
    {
        // Review wave3-6: a width tick must NOT rewrap the whole document — only realized blocks
        // rebuild their maps; unrealized blocks serve estimates that refine as they realize.
        const int paragraphCount = 400;
        var document = string.Join("\n\n", Enumerable.Range(0, paragraphCount).Select(_ => NavigationFixtures.Wrap28Fixture));
        using var harness = CreatePipeline(document, columns: 60, rows: Rows);
        var host = harness.Host;

        var bridge = harness.Bridge;
        int buildsBefore = bridge.MapBuildCount;

        foreach (var width in new[] { 50, 40, 28, 24, 60 })
        {
            host.SendResize(width, Rows);
            Assert.True(host.RunUntilIdle());
        }

        // Pre-fix: every tick rebuilt every block's map (5 × 400 ≥ 2000 builds). Post-fix the
        // storm touches the realized band only — bounded well below one full-document pass.
        int stormBuilds = bridge.MapBuildCount - buildsBefore;
        Assert.InRange(stormBuilds, 1, paragraphCount - 1);

        // The realized band re-wrapped correctly at the final width (60: one row per paragraph).
        Assert.Equal(NavigationFixtures.Wrap28Fixture, host.GetRowText(0)[..NavigationFixtures.Wrap28Fixture.Length]);
        Assert.Equal("", host.GetRowText(1)[..20].TrimEnd());

        // Estimate-then-refine keeps the extent honest: exact for everything visited at this
        // width, a bounded estimate for blocks not realized since the storm (§2.3). The exact
        // total is 399 two-row blocks + the last one-row one.
        const int exactRows = 2 * (paragraphCount - 1) + 1;
        var scrollViewer = harness.Editor.ScrollViewerPart!;
        scrollViewer.VerticalOffset = int.MaxValue; // coerced to extent − viewport
        Assert.True(host.RunUntilIdle());

        Assert.InRange(scrollViewer.Extent.Rows, exactRows, exactRows + 30);
        Assert.Equal(scrollViewer.Extent.Rows - scrollViewer.Viewport.Rows, scrollViewer.VerticalOffset);
        Assert.Equal(
            NavigationFixtures.Wrap28Fixture,
            host.GetRowText(scrollViewer.Viewport.Rows - 1)[..NavigationFixtures.Wrap28Fixture.Length]); // the last paragraph, at the bottom

        // Visiting the whole document realizes every block once at the final width, so every
        // estimate refines and the extent converges to the exact total.
        for (var offset = 0; offset <= scrollViewer.Extent.Rows; offset += scrollViewer.Viewport.Rows)
        {
            scrollViewer.VerticalOffset = offset;
            Assert.True(host.RunUntilIdle());
        }

        Assert.Equal(exactRows, scrollViewer.Extent.Rows);
    }

    // ───────────────────────────── re-wiring (the open-file seam) ─────────────────────────────

    [Fact]
    public void AttachDocument_Again_ReplacesThePipelineAndTheSurface()
    {
        using var harness = CreatePipeline("old content");
        var host = harness.Host;
        var oldPresenters = Realized(harness.Editor).Values.ToList();

        var buffer = new DocumentBuffer("new one\n\nnew two");
        var controller = new EditController(buffer, host.Time);
        var producer = new PlainTextBlockProducer(controller);
        var bridge = new BlockViewBridge(buffer, producer);
        harness.Editor.AttachDocument(controller, bridge);
        Assert.True(host.RunUntilIdle());

        Assert.Equal("new one", host.GetRowText(0).TrimEnd());
        Assert.Equal("new two", host.GetRowText(2).TrimEnd());

        // The old presenters were de-realized by the factory swap.
        Assert.DoesNotContain(Realized(harness.Editor).Values, oldPresenters.Contains);
    }
}
