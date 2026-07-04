using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Terminal;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.Views;

namespace CursorialEdit.Tests.Views;

/// <summary>
/// M1.WP3 — the R5 spike storm suite (implementation-plan §6 WP3; architecture §2.3 + risk R5):
/// proves the custom <see cref="IScrollContentHost"/> band contract on the real
/// <c>EditorControl</c>/<c>DocumentPanel</c> skeleton. After every storm step the suite asserts the
/// three R5 invariants — no blank rows in the viewport, no extent drift, correct terminal-caret
/// publication — plus de-realization teardown and zero block re-raster on in-band scrolls.
/// </summary>
public sealed class PanelSpikeTests
{
    /// <summary>Both §5.1 wire presets — the shared registry (<see cref="TestSupport.CapabilityPresets"/>).</summary>
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private static TerminalCapabilities Caps(string preset) => TestSupport.CapabilityPresets.Resolve(preset);

    /// <summary>Positive <paramref name="notches"/> scroll down (3 rows per notch — the ScrollViewer default).</summary>
    private static void SendWheel(PanelSpikeHarness harness, int notches)
        => harness.Host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.Wheel,
            Position = new CellPosition(5, 5),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            WheelDeltaY = -120 * notches,
            Timestamp = default, // SendInput restamps a default timestamp on the fake clock
        });

    // Exposes the protected-internal HandlesScrolling for the gate assertion.
    private sealed class HandlesScrollingProbe : EditorControl
    {
        public bool Handles => HandlesScrolling;
    }

    // ───────────────────────────── template / seam wiring (the R5 gate preconditions) ─────────────────────────────

    [Fact]
    public void EditorControl_TemplatesOwnScrollViewer_AndOwnsKeyboardScrolling()
    {
        Assert.True(new HandlesScrollingProbe().Handles); // HandlesScrolling => true

        using var harness = PanelSpikeHarness.Create();

        // The ScrollViewer is a part of the EDITOR's template — its TemplatedParent is the editor,
        // which is exactly what the ScrollViewer.OnKeyDown HandlesScrolling gate checks.
        var scrollViewer = harness.ScrollViewer;
        Assert.Same(harness.Editor, scrollViewer.TemplatedParent);

        // The panel is the SCP's direct content and opted in: the SCP injected itself as ScrollOwner.
        var panel = harness.Panel;
        IScrollContentHost host = panel;
        Assert.True(host.IsScrollClient);
        Assert.False(host.IsLogicalScroll); // the legacy step already scrolls one cell / one viewport
        Assert.NotNull(panel.ScrollOwner);
        Assert.Same(scrollViewer, panel.ScrollOwner!.TemplatedParent); // the SCP inside the editor's ScrollViewer

        // The host extent (300 × 1-row blocks) is published through GetExtent, not DesiredSize.
        Assert.Equal(300, scrollViewer.Extent.Rows);
        harness.AssertViewportIntegrity();
    }

    // ───────────────────────────── wheel / keyboard scrolling ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void WheelScroll_SlidesViewport_NoBlankRows(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));

        SendWheel(harness, 2); // +6 rows
        harness.SettleAndAssert();
        Assert.Equal(6, harness.ScrollViewer.VerticalOffset);

        SendWheel(harness, 5); // +15 rows — crosses the K=12 re-anchor threshold
        harness.SettleAndAssert();
        Assert.Equal(21, harness.ScrollViewer.VerticalOffset);

        SendWheel(harness, -1); // back up 3 rows
        harness.SettleAndAssert();
        Assert.Equal(18, harness.ScrollViewer.VerticalOffset);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void KeyboardScroll_ArrowsPagesAndEnds_NoBlankRows(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));
        harness.Editor.Focus();
        harness.SettleAndAssert();

        for (var i = 0; i < 5; i++)
            harness.Host.SendKey(Key.DownArrow);
        harness.SettleAndAssert();
        Assert.Equal(5, harness.ScrollViewer.VerticalOffset);

        harness.Host.SendKey(Key.PageDown); // + viewport (12)
        harness.SettleAndAssert();
        Assert.Equal(17, harness.ScrollViewer.VerticalOffset);

        harness.Host.SendKey(Key.UpArrow);
        harness.SettleAndAssert();
        Assert.Equal(16, harness.ScrollViewer.VerticalOffset);

        harness.Host.SendKey(Key.Home, KeyModifiers.Control);
        harness.SettleAndAssert();
        Assert.Equal(0, harness.ScrollViewer.VerticalOffset);

        harness.Host.SendKey(Key.End, KeyModifiers.Control);
        harness.SettleAndAssert();
        Assert.Equal(300 - 12, harness.ScrollViewer.VerticalOffset);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PageDownStorm_AtEndOfFile_PinsAtMaxOffset_NoBlankRows(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));
        harness.Editor.Focus();
        harness.Host.SendKey(Key.End, KeyModifiers.Control);
        harness.SettleAndAssert();

        var maxOffset = 300 - harness.ScrollViewer.Viewport.Rows;
        Assert.Equal(maxOffset, harness.ScrollViewer.VerticalOffset);

        for (var i = 0; i < 8; i++)
        {
            harness.Host.SendKey(Key.PageDown); // must coerce, never overshoot, never blank the tail
            harness.SettleAndAssert();
            Assert.Equal(maxOffset, harness.ScrollViewer.VerticalOffset);
        }

        // The very last document row is on screen.
        Assert.StartsWith(harness.Source.ExpectedRowText(299), harness.Host.GetRowText(harness.ScrollViewer.Viewport.Rows - 1));
    }

    [Fact]
    public void MultiRowBlocks_ScrollAcrossBlockInteriors_NoBlankRows()
    {
        using var harness = PanelSpikeHarness.Create(blockCount: 120, blockHeight: 3); // 360 rows
        harness.Editor.Focus();
        harness.SettleAndAssert();

        // Land the viewport mid-block repeatedly (offsets not aligned to block tops).
        foreach (var offset in new[] { 4, 17, 100, 254, 348, 5, 0 })
        {
            harness.ScrollViewer.VerticalOffset = offset;
            harness.SettleAndAssert();
        }
    }

    // ───────────────────────────── resize storms ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ResizeStorm_NoBlankRows_NoExtentDrift(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));
        harness.Editor.Focus();

        var sizes = new[] { (80, 30), (20, 6), (60, 24), (100, 40), (28, 8), (40, 12) };
        foreach (var (columns, rows) in sizes)
        {
            harness.Host.SendResize(columns, rows);
            harness.SettleAndAssert();

            harness.Host.SendKey(Key.PageDown);
            harness.SettleAndAssert();

            SendWheel(harness, -1);
            harness.SettleAndAssert();
        }

        // A resize burst inside one frame (coalesced last-wins, like production).
        harness.Host.SendResize(90, 35);
        harness.Host.SendResize(24, 7);
        harness.Host.SendResize(50, 18);
        harness.SettleAndAssert();
        Assert.Equal(18, harness.ScrollViewer.Viewport.Rows);
    }

    [Fact]
    public void ResizeStorm_AtEndOfFile_OffsetReCoerces_TailStaysPainted()
    {
        using var harness = PanelSpikeHarness.Create();
        harness.Editor.Focus();
        harness.Host.SendKey(Key.End, KeyModifiers.Control);
        harness.SettleAndAssert();

        // Growing the viewport at EOF shrinks the max offset — the end-of-arrange re-coercion must
        // snap back the same frame with the tail fully painted.
        harness.Host.SendResize(40, 36);
        harness.SettleAndAssert();
        Assert.Equal(300 - 36, harness.ScrollViewer.VerticalOffset);

        harness.Host.SendResize(40, 8);
        harness.SettleAndAssert();
    }

    // ───────────────────────────── offset jumps past the band ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void OffsetJumpFarPastBand_RealizesNewBand_TearsDownOldBlocks(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));
        harness.SettleAndAssert();

        var initiallyRealized = harness.Panel.RealizedBlocks.Values.Cast<StubBlockPresenter>().ToList();
        Assert.NotEmpty(initiallyRealized);
        Assert.All(initiallyRealized, presenter => Assert.False(presenter.TornDown));

        // A thumb-drag-style jump far outside the band (band ≈ [0, 36) + cover slack at offset 0).
        harness.ScrollViewer.VerticalOffset = 250;
        harness.SettleAndAssert();

        // The old band's blocks were de-realized AND swept (TearDown — the INPC-leak rule) …
        Assert.All(initiallyRealized, presenter =>
        {
            Assert.False(harness.Panel.RealizedBlocks.ContainsKey(presenter.BlockIndex));
            Assert.True(presenter.TornDown, $"block {presenter.BlockIndex} was de-realized without TearDown()");
        });

        // … and the new band is realized around the new offset.
        Assert.All(harness.Panel.RealizedBlocks.Keys, index => Assert.InRange(index, 200, 299));

        // Jump straight back — the storm's return leg.
        harness.ScrollViewer.VerticalOffset = 0;
        harness.SettleAndAssert();
    }

    // ───────────────────────────── extent refinement mid-scroll ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ExtentRefineMidScroll_ShrinkSnapsOffsetBack_GrowKeepsIt(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));
        harness.Editor.Focus();

        harness.ScrollViewer.VerticalOffset = 150;
        harness.SettleAndAssert();

        // Shrink under the scrolled offset: HeightsChanged → InvalidateScrollExtent must re-publish
        // the extent AND re-coerce the offset into the new range — no drift, no blank tail.
        harness.Source.Reshape(80); // 80 rows; max offset = 68
        harness.SettleAndAssert();
        Assert.Equal(80, harness.ScrollViewer.Extent.Rows);
        Assert.Equal(80 - harness.ScrollViewer.Viewport.Rows, harness.ScrollViewer.VerticalOffset);

        // Grow again mid-scroll: the extent re-publishes and the viewport must stay fully painted.
        // PINNED FRAMEWORK SEMANTICS (spike finding, FB-16 evidence): offset coercion is a view
        // over the stored RAW offset, so regrowing the extent RESURRECTS the pre-shrink offset
        // (150), not the clamped 68 — and the value store's equal-coerced-value gate makes an
        // app-side pin impossible while the extent is shrunk. Realization must survive the jump.
        harness.Source.Reshape(500);
        harness.SettleAndAssert();
        Assert.Equal(500, harness.ScrollViewer.Extent.Rows);
        Assert.Equal(150, harness.ScrollViewer.VerticalOffset);

        // And the newly reachable tail is realizable.
        harness.Host.SendKey(Key.End, KeyModifiers.Control);
        harness.SettleAndAssert();
        Assert.Equal(500 - harness.ScrollViewer.Viewport.Rows, harness.ScrollViewer.VerticalOffset);
    }

    // ───────────────────────────── raster economics (composite-slide scrolling) ─────────────────────────────

    [Fact]
    public void InBandScroll_EmitsNoBlockReRaster_AndNoRealizationChurn()
    {
        using var harness = PanelSpikeHarness.Create();
        harness.SettleAndAssert();

        var realizedBefore = harness.Panel.RealizedBlocks.Values.Cast<StubBlockPresenter>().ToList();
        var countsBefore = realizedBefore.ToDictionary(p => p.BlockIndex, p => p.RenderCount);
        var createdBefore = harness.CreatedBlocks.Count;

        SendWheel(harness, 1); // +3 rows — within K=12 of the anchor: a pure composite slide
        harness.SettleAndAssert();
        Assert.Equal(3, harness.ScrollViewer.VerticalOffset);

        Assert.Equal(createdBefore, harness.CreatedBlocks.Count); // no re-realization on an in-band slide
        Assert.All(realizedBefore, presenter => Assert.Equal(countsBefore[presenter.BlockIndex], presenter.RenderCount));
    }

    [Fact]
    public void BandReAnchor_RealizesNewCover_WithoutReRasteringRetainedBlocks()
    {
        using var harness = PanelSpikeHarness.Create();
        harness.SettleAndAssert();

        var realizedBefore = harness.Panel.RealizedBlocks.Values.Cast<StubBlockPresenter>().ToList();
        var countsBefore = realizedBefore.ToDictionary(p => p.BlockIndex, p => p.RenderCount);

        harness.ScrollViewer.VerticalOffset = 20; // past K=12 → the SCP re-anchors its band
        harness.SettleAndAssert();

        // Blocks retained across the re-anchor are render boundaries with unchanged content — the
        // re-anchor re-rasters the band ZONE, never the retained block zones.
        foreach (var presenter in realizedBefore)
        {
            if (harness.Panel.RealizedBlocks.ContainsKey(presenter.BlockIndex))
                Assert.Equal(countsBefore[presenter.BlockIndex], presenter.RenderCount);
        }
    }

    // ───────────────────────────── caret publication ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CaretPublication_FollowsScroll_ClipsOut_AndClearsOnFocusLoss(string preset)
    {
        using var harness = PanelSpikeHarness.Create(capabilities: Caps(preset));
        harness.Editor.Focus();
        harness.Host.RunUntilIdle();

        harness.Editor.SetStubCaret(column: 3, documentRow: 5);
        harness.Host.RunFrame();
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(3, harness.Host.FrameBuffer.CursorColumn);
        Assert.Equal(5, harness.Host.FrameBuffer.CursorRow);

        // Scroll 3 rows: the caret is a content-coordinate publication — the composite slide moves
        // the terminal cursor with NO re-publication.
        harness.ScrollViewer.ScrollBy(0, 3);
        harness.SettleAndAssert();
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(3, harness.Host.FrameBuffer.CursorColumn);
        Assert.Equal(2, harness.Host.FrameBuffer.CursorRow);

        // Scroll the caret out of the viewport: clipped-out ⇒ CursorVisible = false.
        harness.ScrollViewer.VerticalOffset = 40;
        harness.SettleAndAssert();
        Assert.False(harness.Host.FrameBuffer.CursorVisible);

        // Scroll back: visible again at its document position.
        harness.ScrollViewer.VerticalOffset = 0;
        harness.SettleAndAssert();
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(5, harness.Host.FrameBuffer.CursorRow);

        // Focus loss clears the publication.
        harness.Host.Application.FocusManager.ClearFocus();
        harness.Host.RunUntilIdle();
        Assert.False(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(0, harness.Host.Application.CaretService.PublicationCount);
    }

    [Fact]
    public void CaretPublication_SurvivesResizeAndExtentRefine()
    {
        using var harness = PanelSpikeHarness.Create();
        harness.Editor.Focus();
        harness.Editor.SetStubCaret(column: 2, documentRow: 4);
        harness.SettleAndAssert();
        Assert.True(harness.Host.FrameBuffer.CursorVisible);

        harness.Host.SendResize(60, 20);
        harness.SettleAndAssert();
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(2, harness.Host.FrameBuffer.CursorColumn);
        Assert.Equal(4, harness.Host.FrameBuffer.CursorRow);

        harness.Source.Reshape(50);
        harness.SettleAndAssert();
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(4, harness.Host.FrameBuffer.CursorRow);
    }

    // ───────────────────────────── the combined seeded storm ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CombinedStorm_ScrollEditResize_InvariantsHoldAfterEverySettle(string preset)
    {
        using var harness = PanelSpikeHarness.Create(blockCount: 400, capabilities: Caps(preset));
        harness.Editor.Focus();
        harness.Editor.SetStubCaret(column: 1, documentRow: 0);
        harness.SettleAndAssert();

        var random = new Random(20260703); // deterministic — failures replay from the seed
        var sizes = new[] { (40, 12), (80, 30), (24, 7), (60, 20), (32, 10) };

        for (var step = 0; step < 60; step++)
        {
            switch (random.Next(6))
            {
                case 0:
                    SendWheel(harness, random.Next(-4, 5));
                    break;

                case 1:
                    harness.Host.SendKey(random.Next(2) == 0 ? Key.DownArrow : Key.UpArrow);
                    break;

                case 2:
                    harness.Host.SendKey(random.Next(2) == 0 ? Key.PageDown : Key.PageUp);
                    break;

                case 3:
                    harness.ScrollViewer.VerticalOffset = random.Next(0, harness.Source.TotalRows); // jump (thumb-drag analog)
                    break;

                case 4:
                    var (columns, rows) = sizes[random.Next(sizes.Length)];
                    harness.Host.SendResize(columns, rows);
                    break;

                case 5:
                    harness.Source.Reshape(random.Next(30, 500)); // extent refine mid-scroll
                    break;
            }

            harness.SettleAndAssert();
        }

        // The caret publication survived the whole storm (visible iff its document row is in view).
        var offset = harness.ScrollViewer.VerticalOffset;
        var inView = offset <= 0 && harness.ScrollViewer.Viewport.Rows > 0 && harness.Source.TotalRows > 0;
        Assert.Equal(inView, harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal(1, harness.Host.Application.CaretService.PublicationCount);
    }
}
