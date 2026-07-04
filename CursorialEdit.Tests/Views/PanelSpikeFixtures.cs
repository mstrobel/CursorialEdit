using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Testing;

using CursorialEdit.Views;

namespace CursorialEdit.Tests.Views;

/// <summary>
/// The M1.WP3 stub <see cref="IBlockHeightSource"/>: <see cref="BlockCount"/> fixed-height fake
/// blocks. <see cref="Reshape"/> mutates count/height and raises <see cref="HeightsChanged"/> —
/// the extent-refine-mid-scroll driver.
/// </summary>
internal sealed class StubBlockHeightSource(int blockCount, int blockHeight) : IBlockHeightSource
{
    public int BlockCount { get; private set; } = blockCount;

    public int BlockHeight { get; private set; } = blockHeight;

    public int GetBlockHeight(int index) => BlockHeight;

    public event Action? HeightsChanged;

    public int TotalRows => BlockCount * BlockHeight;

    public void Reshape(int newBlockCount, int? newBlockHeight = null)
    {
        BlockCount = newBlockCount;
        BlockHeight = newBlockHeight ?? BlockHeight;
        HeightsChanged?.Invoke();
    }

    /// <summary>The text every cell assertion expects at a document row: <c>B####R#</c> (block index, row-in-block).</summary>
    public string ExpectedRowText(int documentRow)
        => $"B{documentRow / BlockHeight:D4}R{documentRow % BlockHeight}";
}

/// <summary>
/// The fake block element the spike realizes: draws <c>B####R#</c> on each of its rows, counts
/// <see cref="Render"/> calls (the zero-re-raster observable), and records
/// <see cref="UIElement.TearDown"/> (the de-realization sweep observable). A render boundary, per
/// architecture Decision 7 — a keystroke/raster touches one block zone, and an in-band composite
/// slide touches none.
/// </summary>
internal sealed class StubBlockPresenter : UIElement
{
    public StubBlockPresenter(int blockIndex)
    {
        BlockIndex = blockIndex;
        IsRenderBoundary = true;
    }

    public int BlockIndex { get; }

    public int RenderCount { get; private set; }

    public bool TornDown { get; private set; }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override void Render(RenderContext context)
    {
        RenderCount++;

        var bounds = context.Bounds;
        for (var row = 0; row < bounds.Rows; row++)
            context.DrawText(0, row, $"B{BlockIndex:D4}R{row}", Color.FromRgb(220, 220, 220));
    }

    protected override void OnTearDown()
    {
        TornDown = true;
        base.OnTearDown();
    }
}

/// <summary>
/// The storm-suite harness: a headless host showing an <see cref="EditorControl"/> over a
/// <see cref="StubBlockHeightSource"/>, with every factory-created block retained for
/// raster/teardown assertions, plus the settle-and-assert invariant used after every storm step.
/// </summary>
internal sealed class PanelSpikeHarness : IDisposable
{
    private PanelSpikeHarness(UITestHost host, EditorControl editor, StubBlockHeightSource source)
    {
        Host = host;
        Editor = editor;
        Source = source;
    }

    public UITestHost Host { get; }

    public EditorControl Editor { get; }

    public StubBlockHeightSource Source { get; }

    /// <summary>Every presenter the factory ever created, in creation order (realized or since torn down).</summary>
    public List<StubBlockPresenter> CreatedBlocks { get; } = [];

    public Cursorial.UI.Controls.ScrollViewer ScrollViewer => Editor.ScrollViewerPart!;

    public DocumentPanel Panel => Editor.DocumentPanelPart!;

    public static PanelSpikeHarness Create(
        int columns = 40, int rows = 12, int blockCount = 300, int blockHeight = 1,
        TerminalCapabilities? capabilities = null)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = capabilities ?? TestCapabilities.KittyTruecolor,
        });

        var source = new StubBlockHeightSource(blockCount, blockHeight);
        var editor = new EditorControl { HeightSource = source };
        var harness = new PanelSpikeHarness(host, editor, source);

        editor.BlockFactory = index =>
        {
            var presenter = new StubBlockPresenter(index);
            harness.CreatedBlocks.Add(presenter);
            return presenter;
        };

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");
        return harness;
    }

    /// <summary>
    /// The storm invariant, asserted after every settle: (a) every viewport row shows exactly the
    /// document row the offset maps it to — <b>no blank rows</b>; (b) the published extent equals
    /// the source's prefix total — <b>no extent drift</b>; (c) the offset is within scroll range.
    /// </summary>
    public void AssertViewportIntegrity()
    {
        var scrollViewer = ScrollViewer;
        var offset = scrollViewer.VerticalOffset;
        var viewportRows = scrollViewer.Viewport.Rows;
        var total = Source.TotalRows;

        Assert.Equal(total, scrollViewer.Extent.Rows); // no extent drift
        Assert.InRange(offset, 0, Math.Max(0, total - Math.Min(viewportRows, total)));

        var visibleRows = Math.Min(viewportRows, Math.Max(0, total - offset));
        for (var viewRow = 0; viewRow < visibleRows; viewRow++)
        {
            var expected = Source.ExpectedRowText(offset + viewRow);
            var actual = Host.GetRowText(viewRow);
            Assert.True(
                actual.StartsWith(expected, StringComparison.Ordinal),
                $"viewport row {viewRow} (offset {offset}, doc row {offset + viewRow}): expected '{expected}', got '{actual.TrimEnd()}'");
        }
    }

    /// <summary>Runs to idle, then asserts the storm invariant.</summary>
    public void SettleAndAssert()
    {
        Assert.True(Host.RunUntilIdle(), "the frame loop did not settle");
        AssertViewportIntegrity();
    }

    public void Dispose() => Host.Dispose();
}
