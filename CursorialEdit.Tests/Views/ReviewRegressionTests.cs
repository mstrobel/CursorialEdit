using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Views;

using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Views;

/// <summary>
/// Regressions for the wave-1 code-review findings against the R5 panel/control
/// (see <c>docs/framework-feedback.md</c> FB-16 for the band-geometry backstory).
/// </summary>
public sealed class ReviewRegressionTests
{
    /// <summary>
    /// Wave1-1: the SCP's band padding K shrinks with the viewport WITHOUT re-anchoring, so a cover
    /// computed from the instantaneous K under-realizes after a shrink, and a later in-band slide
    /// (which fires no <c>InvalidateRealization</c> and never re-measures the panel) lands the
    /// viewport on unrealized rows. Reproduces the verifier's scenario: 80×30, slide to offset 27
    /// (anchor stays 0), shrink to 20×6 (K drops 30 → 8; lag 27 &gt; 8), Ctrl+Home (in-band slide to
    /// 0) — every viewport row must render.
    /// </summary>
    [Fact]
    public void ViewportShrink_ThenInBandJumpToTop_RendersEveryRow()
    {
        using var harness = PanelSpikeHarness.Create(columns: 80, rows: 30, blockCount: 300, blockHeight: 1);

        harness.ScrollViewer.VerticalOffset = 27; // within K=30 of anchor 0 — a pure slide
        harness.SettleAndAssert();

        harness.Host.SendResize(20, 6);
        harness.SettleAndAssert();

        harness.Host.SendKey(Key.Home, KeyModifiers.Control); // in-band slide back to offset 0
        harness.SettleAndAssert(); // pre-fix: rows 0..5 were never realized — a fully blank screen
    }

    /// <summary>
    /// Wave1-2: with a zero-height leading block, <c>prefix[]</c> carries duplicate tops and the
    /// owner of content row 0 is the LARGEST block whose top ≤ 0; the removed early-out returned
    /// block 0, so the only content-bearing block was never realized.
    /// </summary>
    [Fact]
    public void ZeroHeightLeadingBlock_RealizesTheContentBearingBlock()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 8) });

        var editor = new EditorControl
        {
            HeightSource = new VariableStubHeightSource(0, 1),
            BlockFactory = static index => new StubBlockPresenter(index),
        };

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle());

        // Block 1 (height 1) owns content row 0; block 0 (height 0) owns nothing.
        Assert.StartsWith("B0001R0", host.GetRowText(0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Wave1-4: the spike's Ctrl+Home/End stubs must match the Control modifier EXACTLY —
    /// Ctrl+Shift+Home is the select-to-document-start chord WP8 binds, and a loose mask both
    /// scrolled and swallowed it.
    /// </summary>
    [Fact]
    public void CtrlShiftHome_IsNotConsumedByTheScrollStub()
    {
        using var harness = PanelSpikeHarness.Create();

        harness.ScrollViewer.VerticalOffset = 10;
        harness.SettleAndAssert();

        harness.Host.SendKey(Key.Home, KeyModifiers.Control | KeyModifiers.Shift);
        harness.SettleAndAssert();

        Assert.Equal(10, harness.ScrollViewer.VerticalOffset); // untouched — the chord passed through
    }

    /// <summary>
    /// Wave3-4: raw block ids restart at 1 per producer, so after a <see cref="BlockViewBridge"/>
    /// swap (same factory delegate — the app pattern is a closure over the current bridge) the
    /// old realized presenters' identities collide with the new producer's ids. The identity
    /// remap must not re-key them: they still draw from the OLD bridge, rendering the old
    /// document's text in the new document's slots (or throwing the stale-consumer error). All
    /// old presenters must be torn down and the new document must render.
    /// </summary>
    [Fact]
    public void HeightSourceSwap_SameFactory_TearsDownStalePresenters_AndRendersTheNewDocument()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(40, 10) });
        var time = new FakeTimeProvider();

        static BlockViewBridge CreateBridge(string text, FakeTimeProvider time)
        {
            var buffer = new DocumentBuffer(text);
            var controller = new EditController(buffer, time);
            return new BlockViewBridge(buffer, new PlainTextBlockProducer(controller));
        }

        var bridge = CreateBridge("old alpha\n\nold bravo", time);
        var editor = new EditorControl
        {
            HeightSource = bridge,
            BlockFactory = index => bridge.CreatePresenter(index), // closes over the CURRENT bridge
        };

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle());
        Assert.Equal("old alpha", host.GetRowText(0).TrimEnd());

        var panel = editor.DocumentPanelPart!;
        var oldPresenters = panel.RealizedBlocks.Values.ToList();
        Assert.NotEmpty(oldPresenters);
        var derealizedBefore = panel.TotalDerealizedBlocks;

        bridge = CreateBridge("new one\n\nnew two\n\nnew three", time);
        editor.HeightSource = bridge; // the factory delegate is unchanged — only the source swaps
        Assert.True(host.RunUntilIdle());

        // Every pre-swap presenter was torn down — none re-keyed onto the new producer's ids.
        Assert.Equal(derealizedBefore + oldPresenters.Count, panel.TotalDerealizedBlocks);
        Assert.DoesNotContain(panel.RealizedBlocks.Values, oldPresenters.Contains);

        // And the new document renders (pre-fix: stale text from the old bridge, or the
        // stale-consumer InvalidOperationException out of GetRunMap on the next measure).
        Assert.Equal("new one", host.GetRowText(0).TrimEnd());
        Assert.Equal("new two", host.GetRowText(2).TrimEnd());
        Assert.Equal("new three", host.GetRowText(4).TrimEnd());
    }

    /// <summary>A height source with explicit per-block heights (the fixed-height stub can't express zero-height blocks).</summary>
    private sealed class VariableStubHeightSource(params int[] heights) : IBlockHeightSource
    {
        public int BlockCount => heights.Length;

        public int GetBlockHeight(int index) => heights[index];

        public event Action? HeightsChanged { add { } remove { } }
    }
}
