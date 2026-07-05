using Cursorial.Rendering;
using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;

namespace CursorialEdit.Views;

/// <summary>
/// Binds the document pipeline to the view surface (M1.WP7): an <see cref="IBlockHeightSource"/>
/// over the producer's <see cref="BlockList"/> whose heights are live wrap-row counts from cached
/// <see cref="BlockRunMap"/>s, the <see cref="IBlockRunMapSource"/> presenters draw from, the
/// presenter factory the <c>EditorControl</c> realizes through, and the reconciliation driver
/// that applies each <see cref="BlockListChange"/> to the panel with minimal invalidation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconciliation policy (documented per WP7).</b> <c>Reused</c> blocks keep their presenter
/// and their cached run map — nothing is invalidated; index shifts are absorbed by the panel's
/// identity remap (<see cref="IBlockViewSource"/>) and start-line shifts by the prefix sums.
/// <c>Changed</c> blocks <b>keep their presenter element and re-layout</b> (the map cache entry
/// is evicted and rebuilt, the presenter's zone is invalidated) rather than being recreated —
/// recreation would tear down and re-add an element per keystroke for zero benefit, since the
/// presenter's only per-block state is the id it draws by. <c>Added</c> blocks realize through
/// the ordinary factory path on the next measure; <c>Removed</c> blocks' presenters are torn down
/// by the panel's remap and their cache entries dropped here.
/// </para>
/// <para>
/// <b>Raster economics.</b> <see cref="IBlockHeightSource.HeightsChanged"/> is raised only when
/// structure or heights actually moved (adds/removes, wrap-row-count changes, width changes that
/// altered wrapping). A same-height content edit invalidates exactly the changed block's
/// presenter zone — the "keystroke re-rasters one block" gate falls out of that plus
/// Decision 7's per-presenter render boundaries.
/// </para>
/// <para>
/// <b>Wrap width.</b> Heights wrap to the viewport's column count, learned via
/// <see cref="IBlockViewSource.OnViewportChanged"/>. Until the first viewport arrives the width
/// is unknown and heights are served unwrapped (one row per source line) — an estimate the
/// architecture sanctions, refined through <c>InvalidateScrollExtent</c> as soon as the real
/// width lands (§2.3).
/// </para>
/// <para>
/// <b>Lazy rewrap.</b> A width change re-wraps only the blocks with a live presenter (the
/// realized band); every other cached map keeps its old width-stamp and serves its stale row
/// count as the height <i>estimate</i> — an unmapped block estimates unwrapped. The estimate
/// refines to exact when the block realizes: its presenter's measure builds the map at the
/// current width through <see cref="GetRunMap"/>, which raises
/// <see cref="IBlockHeightSource.HeightsChanged"/> when the exact row count differs from the
/// estimate the panel was served, so prefix sums and the published extent converge as blocks
/// realize (§2.3 estimate-then-refine) instead of an O(document) rewrap per width tick.
/// </para>
/// </remarks>
public sealed class BlockViewBridge : IEditorViewSource, IBlockRunMapSource, ISelectionSource
{
    private readonly IDocumentBuffer _buffer;
    private readonly PlainTextBlockProducer _producer;
    private readonly Dictionary<BlockId, BlockRunMap> _maps = [];
    private readonly Dictionary<BlockId, PlainTextPresenter> _presenters = [];

    /// <summary>The wrap width in cells; 0 until the first viewport arrives (heights unwrapped).</summary>
    private int _wrapWidth;

    /// <summary>
    /// Creates the bridge over <paramref name="producer"/>'s block list, reading source lines
    /// from <paramref name="buffer"/>, and subscribes to the producer's change feed.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public BlockViewBridge(IDocumentBuffer buffer, PlainTextBlockProducer producer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(producer);

        _buffer = buffer;
        _producer = producer;
        producer.Changed += OnBlocksChanged;
    }

    /// <summary>The live block list (owned by the producer).</summary>
    public BlockList Blocks => _producer.Blocks;

    /// <summary>
    /// The render mode (M2.WP10). Plain text has no syntax marks to hide, so raw and formatted render
    /// identically here — the value is stored for interface parity but does not change what a
    /// <c>PlainTextPresenter</c> draws.
    /// </summary>
    public ViewMode ViewMode { get; set; } = ViewMode.Formatted;

    // ───────────────────────────── IEditorViewSource (the caret seam) ─────────────────────────────

    /// <inheritdoc/>
    /// <remarks>The plain-text surface has no reveal, so the caret map is simply the block's M1 <see cref="BlockRunMap"/>.</remarks>
    public ICaretMap GetCaretMap(int blockIndex) => GetRunMap(Blocks[blockIndex].Id, _wrapWidth);

    /// <inheritdoc/>
    /// <remarks>The plain surface never slides a line (no reveal), so the published caret cell is unadjusted.</remarks>
    public int ActiveSlide(int blockIndex, int row) => 0;

    /// <inheritdoc/>
    /// <remarks>No reveal on the plain surface — a caret move touches no presenter's mark state.</remarks>
    public void OnCaretPositioned(TextPosition caret) { }

    /// <inheritdoc/>
    IEnumerable<KeyValuePair<BlockId, UIElement>> IEditorViewSource.RealizedPresenters =>
        _presenters.Select(kv => new KeyValuePair<BlockId, UIElement>(kv.Key, kv.Value));

    // ───────────────────────────── IBlockHeightSource ─────────────────────────────

    /// <inheritdoc/>
    public int BlockCount => Blocks.Count;

    /// <inheritdoc/>
    public int GetBlockHeight(int index)
    {
        // Never builds a map (the panel calls this for EVERY block per prefix-sum derivation):
        // a cached map's row count is exact at the current width or the previous width's count —
        // the estimate for a not-yet-realized block; no map estimates unwrapped (one row per
        // source line, the same pre-first-viewport estimate). Realize-time GetRunMap refines.
        var block = Blocks[index];
        return _maps.TryGetValue(block.Id, out var map) ? map.RowCount : block.LineCount;
    }

    /// <inheritdoc/>
    public event Action? HeightsChanged;

    // ───────────────────────────── IBlockViewSource ─────────────────────────────

    /// <inheritdoc/>
    public long GetBlockIdentity(int index) => Blocks[index].Id.Value;

    /// <inheritdoc/>
    public int IndexOfBlock(long identity) => Blocks.IndexOf(new BlockId(identity));

    /// <inheritdoc/>
    public void OnViewportChanged(Size viewport)
    {
        int width = Math.Max(1, viewport.Columns);
        if (width == _wrapWidth)
            return;

        _wrapWidth = width;

        // Lazy rewrap (see the class remarks; review wave3-6): re-derive heights for the
        // REALIZED blocks only — everything else keeps its old-width map as the estimate, evicted
        // on demand by the width-stamp check in GetOrBuildMap. Raise HeightsChanged only when a
        // row count actually moved (prefix sums and extent are untouched otherwise — the resize
        // itself already re-measures and re-rasters realized presenters at the new width).
        var heightsMoved = false;
        foreach (var id in _presenters.Keys)
        {
            int index = Blocks.IndexOf(id);
            if (index < 0)
                continue; // a just-removed block whose presenter awaits the panel's teardown sweep

            int oldRows = _maps.TryGetValue(id, out var oldMap) ? oldMap.RowCount : -1;
            if (GetOrBuildMap(id, index).RowCount != oldRows)
                heightsMoved = true;
        }

        if (heightsMoved)
            HeightsChanged?.Invoke();
    }

    // ───────────────────────────── IBlockRunMapSource ─────────────────────────────

    /// <inheritdoc/>
    public BlockRunMap GetRunMap(BlockId block, int wrapWidth)
    {
        int index = Blocks.IndexOf(block);
        if (index < 0)
            throw new InvalidOperationException($"Block {block} is not in the list — a stale consumer survived reconciliation.");

        if (wrapWidth != _wrapWidth)
            return BuildMap(block, index, wrapWidth); // transient width mismatch during a resize — uncached one-off

        if (_maps.TryGetValue(block, out var cached) && cached.WrapWidth == _wrapWidth)
            return cached;

        // Realize-time refine (§2.3): this block's height was served as an estimate (stale-width
        // map, or unwrapped line count). Build the exact map, and when the row count moved tell
        // the panel so prefix sums and the published extent re-derive — the estimate-then-refine
        // loop that keeps the extent honest under lazy rewrap.
        int servedRows = cached?.RowCount ?? Blocks[index].LineCount;
        var map = GetOrBuildMap(block, index);
        if (map.RowCount != servedRows)
            HeightsChanged?.Invoke();

        return map;
    }

    // ───────────────────────────── ISelectionSource ─────────────────────────────

    /// <summary>
    /// The installed document selection (M1.WP8's caret installs itself here through
    /// <c>EditorControl.AttachDocument</c>); <see langword="null"/> until a document is attached —
    /// presenters then draw no selection.
    /// </summary>
    public ISelectionSource? SelectionSource { get; set; }

    /// <inheritdoc/>
    /// <remarks>
    /// A pass-through to <see cref="SelectionSource"/>: presenters hold only their
    /// <see cref="IBlockRunMapSource"/> and type-test it for this interface (the panel's
    /// <see cref="IBlockViewSource"/> idiom), so the selection seam needed no presenter surface
    /// change.
    /// </remarks>
    public (int Start, int End)? GetSelection(BlockId block) => SelectionSource?.GetSelection(block);

    // ───────────────────────────── presenter factory + registry ─────────────────────────────

    /// <summary>
    /// The <c>EditorControl.BlockFactory</c> entry point: creates the presenter for the block at
    /// <paramref name="index"/> and registers it for targeted invalidation. The panel calls this
    /// only for unrealized blocks — reused blocks keep their element via the identity remap.
    /// </summary>
    public UIElement CreatePresenter(int index)
    {
        var id = Blocks[index].Id;
        var presenter = new PlainTextPresenter(this, id)
        {
            TornDownCallback = OnPresenterTornDown,
        };

        _presenters[id] = presenter;
        return presenter;
    }

    /// <summary>The live presenter for <paramref name="block"/>, when one is realized (test observability).</summary>
    internal PlainTextPresenter? GetPresenter(BlockId block) => _presenters.GetValueOrDefault(block);

    /// <summary>
    /// The realized presenters by block id — the WP8 selection-repaint walk (invalidate only the
    /// presenters whose selection intersection changed) iterates exactly the realized band.
    /// </summary>
    internal IReadOnlyDictionary<BlockId, PlainTextPresenter> RealizedPresenters => _presenters;

    /// <summary>
    /// The current layout wrap width in cells (0 until the first viewport arrives — maps are then
    /// unwrapped). The WP8 caret passes this back through <see cref="GetRunMap"/> so its cell math
    /// uses exactly the maps the presenters render from.
    /// </summary>
    public int WrapWidth => _wrapWidth;

    /// <summary>
    /// Cumulative count of run-map builds (test observability): a resize storm must rebuild maps
    /// for realized blocks only, never O(document) per width tick.
    /// </summary>
    internal int MapBuildCount { get; private set; }

    private void OnPresenterTornDown(PlainTextPresenter presenter)
    {
        // Remove only the current registration — a re-realized block may already own a newer element.
        if (_presenters.TryGetValue(presenter.Block, out var registered) && ReferenceEquals(registered, presenter))
            _presenters.Remove(presenter.Block);
    }

    // ───────────────────────────── reconciliation ─────────────────────────────

    private void OnBlocksChanged(BlockListChange change)
    {
        bool heightsMoved = change.Added.Count > 0 || change.Removed.Count > 0;

        foreach (var id in change.Removed)
            _maps.Remove(id); // the presenter (if realized) is torn down by the panel's identity remap

        foreach (var id in change.Changed)
        {
            int oldRows = _maps.TryGetValue(id, out var oldMap) ? oldMap.RowCount : -1;
            _maps.Remove(id);

            int index = Blocks.IndexOf(id);
            if (GetOrBuildMap(id, index).RowCount != oldRows)
                heightsMoved = true;

            // Re-layout the same presenter (see the class remarks): its zone re-rasters from the
            // fresh map; measure re-runs only when the slot height moved (HeightsChanged below).
            if (_presenters.TryGetValue(id, out var presenter))
                presenter.InvalidateVisual();
        }

        if (heightsMoved)
            HeightsChanged?.Invoke(); // the panel remaps realized indices by identity, then re-realizes
    }

    // ───────────────────────────── run-map cache ─────────────────────────────

    private BlockRunMap GetOrBuildMap(BlockId id, int index)
    {
        if (_maps.TryGetValue(id, out var map) && map.WrapWidth == _wrapWidth)
            return map;

        map = BuildMap(id, index, _wrapWidth);
        _maps[id] = map;
        return map;
    }

    private BlockRunMap BuildMap(BlockId id, int index, int wrapWidth)
    {
        var block = Blocks[index];
        if (block.Id != id)
            throw new InvalidOperationException($"Block index {index} carries {block.Id}, not {id} — stale index.");

        MapBuildCount++;

        int startLine = Blocks.GetStartLine(index);
        var lines = new Line[block.LineCount];
        for (var i = 0; i < lines.Length; i++)
            lines[i] = _buffer.GetLine(startLine + i);

        return BlockRunMap.Build(lines, wrapWidth);
    }
}
