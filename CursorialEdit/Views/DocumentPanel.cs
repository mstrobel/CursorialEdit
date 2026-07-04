using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace CursorialEdit.Views;

/// <summary>
/// The editor's virtualizing document surface (architecture Decision 6): a <see cref="Panel"/> that
/// opts into the <see cref="IScrollContentHost"/> delegation seam as the
/// <see cref="ScrollContentPresenter"/>'s direct content. It publishes its extent from a prefix sum
/// over <see cref="HeightSource"/> block heights, realizes one child element per block intersecting
/// the SCP's raster band, arranges children at their true content rows inside the full-extent rect,
/// and tears down de-realized elements.
/// </summary>
/// <remarks>
/// <para>
/// <b>Band reconstruction (FB-16).</b> The SCP's band geometry (<c>BandStartRow</c>/<c>BandLength</c>)
/// is <c>internal</c>, so this panel reconstructs the band from the documented convention
/// (band = viewport + 2K, K = max(viewportRows, 8)) anchored at the <i>current</i>
/// <see cref="ScrollContentPresenter.ScrollOffsetRow"/>. The SCP's real band is anchored at the
/// offset of the <i>last re-anchor</i>. Two lag facts drive the cover math:
/// (1) an offset write within ±K of the anchor slides without re-anchoring, and
/// (2) <b>K shrinks with the viewport without re-anchoring</b> — the SCP's extent/viewport recompute
/// re-clamps the anchor but does not move it to the offset — so the accumulated lag is bounded by
/// the LARGEST K in effect since the last true re-anchor, not by the instantaneous K. The panel
/// ratchets that maximum (<c>_maxBandPadding</c>, released only when the band provably covers the
/// whole extent, where anchoring is irrelevant) and realizes
/// [offset − (Kmax + K), offset + viewport + (Kmax + K)) clamped to the extent — a proven superset
/// of the SCP band including both edge-clamp cases (Kmax ≥ K makes the edge attractors covered
/// whenever active). At steady state Kmax = K and the cover is the familiar
/// [offset − 2K, offset + viewport + 2K). Cost: transiently larger covers after a viewport shrink —
/// the price of the convention not being contractual (FB-16 evidence; an exposed
/// <c>BandStartRow</c>/<c>BandLength</c> deletes this entire dance).
/// </para>
/// <para>
/// <b>Scroll semantics.</b> <see cref="IsLogicalScroll"/> is <see langword="false"/>: the legacy
/// step is already one cell / one viewport (<c>IScrollContentHost.cs:65-69</c>), and the editor
/// scrolls by cells. The offset stays SCP-owned; in-band scrolling is a pure composite slide that
/// never re-measures this panel (nothing invalidates it), so realized block content is never
/// re-rastered by an in-band scroll.
/// </para>
/// </remarks>
public sealed class DocumentPanel : Panel, IScrollContentHost
{
    /// <summary>The SCP's minimum band padding (matrix LD11) — mirrored, not shared (it is private there).</summary>
    private const int MinBandPadding = 8;

    private IBlockHeightSource? _heightSource;
    private Func<int, UIElement>? _blockFactory;

    private readonly Dictionary<int, UIElement> _realized = [];
    private readonly List<int> _derealizeScratch = [];

    // WP7 identity remap (additive): identities of realized blocks, recorded at realize time,
    // populated only when the source implements IBlockViewSource. On HeightsChanged the realized
    // elements are re-keyed to their blocks' new indices so presenters survive index shifts.
    private readonly Dictionary<int, long> _realizedIdentity = [];
    private readonly List<(int NewIndex, UIElement Element, long Identity)> _remapScratch = [];

    // The source instance _realizedIdentity was recorded against (stamped every measure). Raw
    // block ids restart per producer, so identities are only comparable under the SAME source:
    // a swap must tear realized presenters down, never remap them (review wave3-4).
    private IBlockViewSource? _identitySource;

    // Prefix sums over block heights: _prefix[i] = top content row of block i; _prefix[count] = extent rows.
    private int[] _prefix = [];
    private bool _prefixDirty = true;
    private int _prefixCount;

    private Size _viewport;

    // The largest band padding K in effect since the band last provably covered the whole extent —
    // the sound lag bound for the realization cover (see the class remarks; review finding wave1-1).
    private int _maxBandPadding;

    // Realization no-op guard: an unchanged cover/width/extent realizes nothing (the VSP pattern).
    private bool _realizationDirty = true;
    private bool _hasMeasured;
    private int _lastCoverStart = -1;
    private int _lastCoverEnd = -1;
    private int _lastColumns = -1;
    private int _lastBlockCount = -1;
    private Size _cachedDesired;

    /// <summary>
    /// The block-height provider this panel virtualizes over. Setting it re-derives the prefix sums
    /// and refines the published extent through the SCP back-channel.
    /// </summary>
    public IBlockHeightSource? HeightSource
    {
        get => _heightSource;
        set
        {
            VerifyAccess();

            if (ReferenceEquals(_heightSource, value))
                return;

            if (_heightSource is { } old)
                old.HeightsChanged -= OnHeightsChanged;

            _heightSource = value;

            if (value is { } source)
                source.HeightsChanged += OnHeightsChanged;

            // A width-aware source wired after layout started must learn the current viewport
            // now — the SCP re-publishes it only on change (WP7 wrap-width seam).
            if (value is IBlockViewSource viewSource && _viewport.Columns > 0)
                viewSource.OnViewportChanged(_viewport);

            OnHeightsChanged();
        }
    }

    /// <summary>
    /// Creates the child element for a block index. WP3 supplies fixed-height stub presenters;
    /// M1.WP7 supplies the real <c>PlainTextPresenter</c>s. Swapping the factory de-realizes every
    /// current child (with <see cref="UIElement.TearDown"/>).
    /// </summary>
    public Func<int, UIElement>? BlockFactory
    {
        get => _blockFactory;
        set
        {
            VerifyAccess();

            if (ReferenceEquals(_blockFactory, value))
                return;

            _blockFactory = value;
            DerealizeAll();
            _realizationDirty = true;
            InvalidateMeasure();
        }
    }

    /// <summary>The realized block elements keyed by block index (test observability).</summary>
    internal IReadOnlyDictionary<int, UIElement> RealizedBlocks => _realized;

    /// <summary>
    /// Cumulative count of block elements this panel has realized (factory calls) — the WP7
    /// realization counter surface; M2.WP13/M7.WP1 extend these counters, never re-create them.
    /// </summary>
    internal int TotalRealizedBlocks { get; private set; }

    /// <summary>Cumulative count of block elements this panel has de-realized (torn down) — see <see cref="TotalRealizedBlocks"/>.</summary>
    internal int TotalDerealizedBlocks { get; private set; }

    // ───────────────────────────── IScrollContentHost ─────────────────────────────

    /// <inheritdoc/>
    public bool IsScrollClient => true;

    /// <inheritdoc/>
    public bool IsLogicalScroll => false;

    /// <inheritdoc/>
    public ScrollContentPresenter? ScrollOwner { get; set; }

    /// <inheritdoc/>
    public bool CanScrollHorizontally { get; set; }

    /// <inheritdoc/>
    public bool CanScrollVertically { get; set; } = true;

    /// <inheritdoc/>
    public Size GetExtent() => new(Math.Max(1, _viewport.Columns), ExtentRows);

    /// <inheritdoc/>
    public void SetViewport(Size viewport)
    {
        if (viewport == _viewport)
            return;

        _viewport = viewport;
        _realizationDirty = true; // the viewport drives K and the band size — re-realize next measure
        InvalidateMeasure();

        // WP7 wrap-width seam: a width-aware source re-derives wrap heights from the viewport's
        // columns and raises HeightsChanged only when a row count actually moved (so this cannot
        // ping-pong: the next arrange publishes the same viewport and returns above).
        (_heightSource as IBlockViewSource)?.OnViewportChanged(viewport);
    }

    /// <inheritdoc/>
    public void InvalidateRealization()
    {
        _realizationDirty = true;
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    public int LineStep(int currentOffset, int sign, bool vertical) => 1;

    /// <inheritdoc/>
    public int PageStep(int currentOffset, int sign, bool vertical) => Math.Max(1, vertical ? _viewport.Rows : _viewport.Columns);

    // ───────────────────────────── prefix sums ─────────────────────────────

    private int ExtentRows
    {
        get
        {
            EnsurePrefix();
            return _prefixCount > 0 ? _prefix[_prefixCount] : 0;
        }
    }

    private void EnsurePrefix()
    {
        var count = _heightSource?.BlockCount ?? 0;
        if (!_prefixDirty && _prefixCount == count)
            return;

        if (_prefix.Length < count + 1)
            _prefix = new int[count + 1];

        var accumulated = 0L;
        _prefix[0] = 0;
        for (var i = 0; i < count; i++)
        {
            accumulated += Math.Max(0, _heightSource!.GetBlockHeight(i));
            _prefix[i + 1] = (int)Math.Min(accumulated, LayoutMath.MaxExtent);
        }

        _prefixCount = count;
        _prefixDirty = false;
    }

    /// <summary>Total content rows (the extent's row count) — the WP8 caret/hit-test row map.</summary>
    internal int ContentRowCount => ExtentRows;

    /// <summary>
    /// The top content row of block <paramref name="index"/> (clamped into the block range), from
    /// the same live prefix sums the arrange pass places blocks by — the WP8 caret's
    /// block → document-row leg agrees with the rendered placement by construction.
    /// </summary>
    internal int GetBlockTopRow(int index)
    {
        EnsurePrefix();
        return _prefixCount == 0 ? 0 : _prefix[Math.Clamp(index, 0, _prefixCount)];
    }

    /// <summary>The index of the block owning <paramref name="contentRow"/> (clamped into the content range) — the WP8 hit-test leg.</summary>
    internal int BlockIndexOfContentRow(int contentRow)
    {
        EnsurePrefix();
        return BlockAtRow(Math.Clamp(contentRow, 0, Math.Max(0, ExtentRows - 1)));
    }

    /// <summary>The block whose [top, top + height) contains <paramref name="contentRow"/> — the largest i with prefix[i] ≤ row.</summary>
    private int BlockAtRow(int contentRow)
    {
        if (_prefixCount <= 0)
            return 0;

        // No early-out for contentRow <= 0: with zero-height leading blocks prefix[] carries
        // duplicate tops, and the owner of row 0 is the LARGEST i with prefix[i] == 0 — the binary
        // search resolves it; returning 0 here skipped the content-bearing block (review finding
        // wave1-2).
        int lo = 0, hi = _prefixCount;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_prefix[mid] <= contentRow)
                lo = mid;
            else
                hi = mid - 1;
        }

        return Math.Min(lo, _prefixCount - 1);
    }

    private void OnHeightsChanged()
    {
        // WP7 (additive): before heights re-derive, re-key realized elements to their blocks'
        // new indices so a structural change (split/merge/shift) REUSES presenters instead of
        // tearing them down and re-creating them at the shifted indices.
        RemapRealizedByIdentity();

        _prefixDirty = true;
        _realizationDirty = true;

        // The extent-refinement back-channel: re-publishes Extent from GetExtent(), re-coerces both
        // offsets (a shrink mid-scroll snaps the offset back), and marks the SCP measure-dirty.
        //
        // Spike finding (FB-16 evidence): offset coercion is a VIEW over the stored raw value —
        // after a shrink clamps the offset, a later extent re-growth RESURRECTS the pre-shrink raw
        // offset (deliberate raw-value durability, WPF parity). An app-side pin is impossible: the
        // value store fully gates any write whose coerced result equals the current effective value
        // (ValueStore.SetLocalValue's comparer gate), so the raw offset cannot be replaced while the
        // extent is shrunk. Realization stays correct either way (the cover derives from the live
        // effective offset); the resurrected-offset jump is documented framework semantics, pinned
        // by PanelSpikeTests.ExtentRefineMidScroll.
        ScrollOwner?.InvalidateScrollExtent();
        InvalidateMeasure();
    }

    /// <summary>
    /// Re-keys <see cref="_realized"/> by block identity against the current
    /// <see cref="IBlockViewSource"/> — but ONLY when it is the same instance the identities were
    /// recorded under: raw ids restart per producer, so after a source swap a colliding id names
    /// a different block, and every realized element is torn down instead (its world is gone).
    /// Elements whose blocks left the list are torn down; everything else moves to its block's
    /// new index. A non-identity source (the WP3 stub path) skips this entirely — realization
    /// stays purely index-keyed, exactly as before WP7.
    /// </summary>
    private void RemapRealizedByIdentity()
    {
        var source = _heightSource as IBlockViewSource;

        // Source-instance change (either direction, identity or stub): recorded identities are
        // meaningless against the new source — tear down and let it realize fresh (wave3-4).
        // Stub→stub stays index-keyed with no teardown, exactly the pre-WP7 spike behavior.
        if (!ReferenceEquals(source, _identitySource))
        {
            _identitySource = source;
            DerealizeAll();
            return;
        }

        if (_realized.Count == 0)
            return;

        if (source is null)
        {
            _realizedIdentity.Clear();
            return;
        }

        _remapScratch.Clear();
        foreach (var (index, element) in _realized)
        {
            // No recorded identity means the element predates identity tracking under this
            // source — its block cannot be resolved; tear it down.
            var newIndex = _realizedIdentity.TryGetValue(index, out var identity)
                ? source.IndexOfBlock(identity)
                : -1;
            _remapScratch.Add((newIndex, element, identity));
        }

        _realized.Clear();
        _realizedIdentity.Clear();
        foreach (var (newIndex, element, identity) in _remapScratch)
        {
            if (newIndex < 0)
            {
                Children.Remove(element);
                element.TearDown();
                TotalDerealizedBlocks++;
            }
            else
            {
                _realized[newIndex] = element;
                _realizedIdentity[newIndex] = identity;
            }
        }
    }

    // ───────────────────────────── measure (the realization driver) ─────────────────────────────

    /// <summary>
    /// Realizes the block cover for the reconstructed band (see the class remarks) and measures each
    /// realized child at (availableColumns × its block height). De-realized children are removed and
    /// <see cref="UIElement.TearDown"/>-swept.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_heightSource is null || _blockFactory is null || ScrollOwner is null)
        {
            DerealizeAll();
            _hasMeasured = false;
            return Size.Empty;
        }

        EnsurePrefix();
        var contentRows = ExtentRows;
        var blockCount = _prefixCount;

        // Before the first arrange SetViewport has not run — use the (viewport-shaped) measure
        // constraint as the estimate; the SCP measures a host at the viewport, never MaxScrollExtent.
        var viewportRows = _viewport.Rows > 0 ? _viewport.Rows : Math.Min(availableSize.Rows, LayoutLimits.MaxScrollExtent);
        var offset = Math.Clamp(ScrollOwner.ScrollOffsetRow, 0, Math.Max(0, contentRows - viewportRows));

        // The reconstructed convention: band = viewport + 2K, K = max(viewportRows, 8). The anchor
        // lag is bounded by the ratcheted max K, not the instantaneous K (class remarks; the
        // whole-extent case is the one sound release point — anchoring is irrelevant there).
        var k = Math.Max(viewportRows, MinBandPadding);
        var bandLength = Math.Min(contentRows, viewportRows + 2 * k);
        _maxBandPadding = bandLength >= contentRows ? k : Math.Max(_maxBandPadding, k);
        var pad = _maxBandPadding + k;
        var coverStart = Math.Max(0, offset - pad);
        var coverEnd = Math.Min(contentRows, offset + viewportRows + pad);

        if (_hasMeasured && !_realizationDirty
            && coverStart == _lastCoverStart && coverEnd == _lastCoverEnd
            && availableSize.Columns == _lastColumns && blockCount == _lastBlockCount)
        {
            return _cachedDesired;
        }

        var firstBlock = blockCount == 0 ? 0 : BlockAtRow(coverStart);
        var lastBlock = blockCount == 0 || coverEnd <= coverStart ? firstBlock : BlockAtRow(coverEnd - 1) + 1;

        // De-realize blocks outside the cover (TearDown — the INPC-leak rule; detach-without-sweep
        // would pin any subscriptions the block presenters hold).
        _derealizeScratch.Clear();
        foreach (var (index, _) in _realized)
        {
            if (index < firstBlock || index >= lastBlock)
                _derealizeScratch.Add(index);
        }

        foreach (var index in _derealizeScratch)
        {
            var element = _realized[index];
            _realized.Remove(index);
            _realizedIdentity.Remove(index);
            Children.Remove(element);
            element.TearDown();
            TotalDerealizedBlocks++;
        }

        // Realize + measure the cover. Each block is measured at its exact (columns × height) slot —
        // the height source is authoritative in the WP3 skeleton.
        var identitySource = _heightSource as IBlockViewSource;
        _identitySource = identitySource; // identities recorded below belong to THIS source (wave3-4)
        for (var i = firstBlock; i < lastBlock; i++)
        {
            if (!_realized.TryGetValue(i, out var element))
            {
                element = _blockFactory(i);
                _realized[i] = element;
                Children.Add(element);
                TotalRealizedBlocks++;

                if (identitySource is not null)
                    _realizedIdentity[i] = identitySource.GetBlockIdentity(i);
            }

            element.Measure(new Size(availableSize.Columns, _prefix[i + 1] - _prefix[i]));
        }

        _hasMeasured = true;
        _realizationDirty = false;
        _lastCoverStart = coverStart;
        _lastCoverEnd = coverEnd;
        _lastColumns = availableSize.Columns;
        _lastBlockCount = blockCount;
        _cachedDesired = new Size(availableSize.Columns, contentRows);
        return _cachedDesired;
    }

    /// <summary>
    /// Arranges every realized block at its true content row inside the full-extent rect the SCP
    /// hands the content (the scroll slide is composite-time; positions are content coordinates).
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsurePrefix();

        foreach (var (index, element) in _realized)
        {
            if (index >= _prefixCount)
                continue; // stale entry between a shrink and the next measure — swept there

            var top = _prefix[index];
            var height = _prefix[index + 1] - top;
            element.Arrange(new Rect(0, top, Math.Max(1, finalSize.Columns), height));
        }

        return finalSize;
    }

    private void DerealizeAll()
    {
        _realizedIdentity.Clear();

        if (_realized.Count == 0)
            return;

        foreach (var (_, element) in _realized)
        {
            Children.Remove(element);
            element.TearDown();
            TotalDerealizedBlocks++;
        }

        _realized.Clear();
    }

    /// <inheritdoc/>
    protected override void OnTearDown()
    {
        // Release the external height-source subscription (the panel-held, non-binding hook).
        if (_heightSource is { } source)
            source.HeightsChanged -= OnHeightsChanged;

        base.OnTearDown();
    }
}
