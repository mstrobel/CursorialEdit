using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The GFM table presenter (architecture §2.5 / Decision 11, M3.WP2): the single block presenter the
/// bridge registers for a <see cref="BlockKind.Table"/> block, composing one
/// <see cref="TableRowPresenter"/> per <b>logical</b> row as its visual children — each its own render
/// boundary (the committed M3 deliverable), so an intra-cell keystroke re-rasters one row zone, not the
/// table. The presenter itself paints nothing: it lays the row children out vertically, owns the shared
/// <see cref="TableGridMetrics"/> and the <see cref="TableModel"/>, and reconciles them on every edit —
/// a content-only edit re-rasters the touched row, a width/height change re-measures and re-rasters the
/// affected rows (WP5 narrows this further).
/// </summary>
/// <remarks>
/// It derives from <see cref="LeafBlockPresenter"/> only to slot into the bridge's presenter registry,
/// height/reveal plumbing, and teardown — it overrides the whole draw path away (the row children draw)
/// and reports the grid height so the panel's prefix sums and scroll extent match what is drawn
/// (<see cref="ScrollContentPresenter"/>.<c>InvalidateScrollExtent</c>, via the bridge). Caret entry and
/// cell selection are M3.WP9/WP8 — this is the render-only spike that retires R3.
/// </remarks>
public sealed class TablePresenter : LeafBlockPresenter
{
    private readonly List<TableRowPresenter> _rows = [];

    private TableModel _model;
    private TableGridMetrics _metrics;
    private string _source;
    private int _height;

    // The viewport width (in cells) the current column widths were resolved at — the auto-layout input
    // (Decision 11, revised). A MeasureOverride at a different width re-resolves + re-lays-out the table
    // (the resize trigger, on top of WP5's content-edit reflow); 0 = not yet measured (fallback widths).
    private int _measuredColumns;

    // The cell-overflow mode (§5.6, M3.WP6): Wrap (default) grows a wide cell's row; Truncate keeps one visual
    // row with a trailing … and reveals the focused cell. Toggling re-derives every row and rebuilds the map.
    private TableOverflow _overflow;

    // The column-window (M3.WP6, FB-6 sidestep): the index of the first VISIBLE column when the grid can't fit
    // the viewport even at MinWidth. A presenter-internal render offset (BorderX of this column) — NOT a nested
    // ScrollViewer — scrolls the grid so the caret's column stays on-screen; the document never scrolls sideways.
    private int _windowColumn;

    // The focused cell (row, col) the Truncate reveal un-truncates; null when the caret is not in this table.
    private (int Row, int Column)? _activeCell;

    private TableCaretMap? _caretMap;

    private TableModel? _pendingModel;
    private string? _pendingSource;

    private readonly HashSet<int> _highlightedRows = []; // the row indices whose cells last drew a selection

    /// <summary>Creates the presenter for a table block from its <paramref name="model"/> and serialized <paramref name="source"/>.</summary>
    /// <param name="lines">The block's source lines.</param>
    /// <param name="model">The table overlay built from the block (<see cref="TableModel.Build"/>).</param>
    /// <param name="source">The block's serialized source (must equal the block's <c>BlockText()</c>) — the cell spans index it.</param>
    /// <param name="availableColumns">
    /// The current viewport width in cells, threaded from the bridge so the initial column widths are already
    /// viewport-aware (Decision 11, revised) and the first frame does not reflow. <c>0</c> (unknown) resolves
    /// to the content-clamped fallback until the first <see cref="MeasureOverride"/> supplies a real width.
    /// </param>
    public TablePresenter(IReadOnlyList<Line> lines, TableModel model, string source, int availableColumns = 0, TableOverflow overflow = TableOverflow.Wrap)
        : base(lines, [], BlockKind.Table, headingLevel: null, WrapMode.NoWrap)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);

        _model = model;
        _source = source;
        _measuredColumns = availableColumns;
        _overflow = overflow;
        _metrics = TableGridMetrics.BuildForViewport(model, availableColumns);
        RecomputeWindow(); // seed the cached window before the rows (their WindowProvider reads it)
        BuildChildren();
    }

    /// <summary>The live table overlay (test observability).</summary>
    internal TableModel Model => _model;

    /// <summary>
    /// The rectangular whole-cell selection (M3.WP8, spec §5.4) forwarded to every row so a multi-cell selection
    /// highlights WHOLE cells instead of the covered source span — the bridge wires this to
    /// <see cref="ISelectionSource.GetCellRect"/> for this table's block. <see langword="null"/> ⇒ no cell-rect
    /// (a single-cell text selection or a selection that left the table), and the rows fall back to the WP5
    /// per-cell text highlight from <see cref="LeafBlockPresenter.SelectionProvider"/>.
    /// </summary>
    internal Func<CellRect?>? CellRectProvider { get; set; }

    /// <summary>
    /// The cell-overflow mode (§5.6): <see cref="TableOverflow.Wrap"/> (default) or <see cref="TableOverflow.Truncate"/>.
    /// Settable, mirroring the bridge's <c>EditWrapEnabled</c> — toggling re-derives every row (heights change: a
    /// wrapped tall row collapses to one), rebuilds the caret map, and re-measures. The user-facing command is M5.
    /// </summary>
    public TableOverflow OverflowMode
    {
        get => _overflow;
        set
        {
            if (_overflow == value)
                return;

            _overflow = value;
            _caretMap = null; // Wrap↔Truncate changes row heights and per-cell stop geometry — the next query rebuilds
            _activeCell = null;

            foreach (var row in _rows)
            {
                row.Overflow = value;
                row.ActiveColumn = -1; // the focused-cell reveal is re-established by the bridge's post-toggle RevealActive
                row.Refresh(_model, _metrics, _source);
                row.InvalidateMeasure();
                row.InvalidateVisual();
            }

            _height = SumHeights();
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// The column-window (offset, on-screen width) the whole grid is drawn through — the <b>single source</b> the
    /// caret path (bridge <c>ActiveSlide</c> = <see cref="WindowOffset"/>) and the row render (<see cref="Window"/>)
    /// both read, so they are provably identical (a divergence would land the caret off the drawn text). Recomputed
    /// only when the geometry inputs (<c>_metrics</c>, <c>_measuredColumns</c>, <c>_windowColumn</c>) change, via
    /// <see cref="RecomputeWindow"/> — not re-scanned per row render.
    /// </summary>
    private (int Offset, int DrawWidth) _window;

    /// <summary>The column-window cell offset (0 when the table fits) — the value the bridge returns from <c>ActiveSlide</c>, so the caret publish subtracts it and a click adds it back through the same offset the rows draw with.</summary>
    public int WindowOffset => _window.Offset;

    /// <summary>The grid's on-screen width in cells — the full grid width when it fits, else the column-window's visible sub-grid width (≤ viewport). The FB-6 "grid width ≤ viewport" observable, and the caret-publish clip bound.</summary>
    internal int RenderedWidth => _window.DrawWidth;

    /// <summary>The first visible column index of the active column-window (0 when the table fits) — test observability.</summary>
    internal int WindowColumn => WindowActive ? ClampColumn(_windowColumn) : 0;

    /// <summary>The (offset, on-screen width) each row draws through — the cached single source shared with the caret path.</summary>
    private (int Offset, int DrawWidth) Window() => _window;

    /// <summary>Whether the grid overflows the viewport even at MinWidth widths, so the column-window is engaged (§5.6).</summary>
    private bool WindowActive => _measuredColumns > 0 && _metrics.Width > _measuredColumns;

    /// <summary>Clamps a column index into the current grid.</summary>
    private int ClampColumn(int column) => Math.Clamp(column, 0, Math.Max(0, _metrics.ColumnCount - 1));

    /// <summary>
    /// The exclusive last visible column of the window anchored at <paramref name="firstColumn"/>: the widest sub-grid
    /// <c>[firstColumn, L)</c> whose drawn width fits the viewport (always ≥ <paramref name="firstColumn"/> + 1, so one
    /// column always shows even if it alone exceeds the viewport).
    /// </summary>
    private int LastVisibleColumn(int firstColumn)
    {
        int firstBorder = _metrics.BorderX(firstColumn);
        int last = firstColumn + 1;
        for (var l = firstColumn + 1; l <= _metrics.ColumnCount; l++)
        {
            if (_metrics.BorderX(l) - firstBorder + 1 <= _measuredColumns)
                last = l;
            else
                break;
        }

        return last;
    }

    /// <summary>Recomputes the cached <see cref="_window"/> from the current geometry — called whenever <c>_metrics</c>, <c>_measuredColumns</c>, or <c>_windowColumn</c> moves.</summary>
    private void RecomputeWindow()
    {
        if (!WindowActive)
        {
            _window = (0, _metrics.Width);
            return;
        }

        int first = ClampColumn(_windowColumn);
        int offset = _metrics.BorderX(first);
        int width = Math.Min(_metrics.BorderX(LastVisibleColumn(first)) - offset + 1, _measuredColumns);
        _window = (offset, width);
    }

    /// <summary>
    /// The composite caret map for this table (M3.WP4): maps a block-relative source offset to the grid
    /// (row, cell) and back, so the document caret lands <b>inside a cell</b>. Rebuilt in lockstep with the
    /// model/metrics on every reconcile; cached so repeated caret queries within a frame share one instance.
    /// </summary>
    public ICaretMap CaretMap() => _caretMap ??= TableCaretMap.Build(_model, _metrics, _source, _overflow, _activeCell);

    /// <summary>
    /// Follows the caret (M3.WP6): records the focused cell (un-truncating it under Truncate) and, when the grid
    /// overflows the viewport, scrolls the presenter-internal column-window so the caret's column is on-screen.
    /// Called by the bridge on every caret move inside the table. The caret map is window-independent (unclipped
    /// grid cells), so the window scroll does not rebuild it.
    /// </summary>
    /// <param name="blockRelOffset">The caret's block-relative source offset (locates its cell via the model).</param>
    public void EnsureColumnVisible(int blockRelOffset)
    {
        var cell = _model.CellOfOffset(blockRelOffset);
        SetActiveCell(cell);
        ScrollColumnIntoView(cell?.Column);
    }

    /// <summary>
    /// Scrolls the column-window the minimum needed so <paramref name="column"/> is on-screen (a no-op when the
    /// table fits or the caret is not in the table). Also the resize re-follow (<see cref="ApplyViewportWidths"/>),
    /// so a viewport change that no longer contains the caret re-syncs before the caret re-publishes.
    /// </summary>
    private void ScrollColumnIntoView(int? column)
    {
        if (!WindowActive || column is not { } target0)
            return;

        int first = ClampColumn(_windowColumn);
        int target = ClampColumn(target0);
        if (target < first)
        {
            first = target; // scroll left: the focused column becomes the first visible
        }
        else
        {
            while (first < target && target >= LastVisibleColumn(first))
                first++; // scroll right the minimum needed to bring the focused column fully on-screen
        }

        if (first == _windowColumn)
            return;

        _windowColumn = first;
        RecomputeWindow();
        // The whole grid shifted (a geometry change bounded to the table). A window scroll never changes a row's
        // DESIRED size (it is _metrics.Width × rows regardless), so re-ARRANGE (rows re-land at the new on-screen
        // width) and re-raster (the new offset) — no re-measure. The document is untouched (separate boundaries).
        InvalidateArrange();
        foreach (var row in _rows)
            row.InvalidateVisual();
    }

    /// <summary>
    /// Clears the caret's cell when it leaves the table (the bridge calls this as it deactivates the block): drops
    /// the focused-cell reveal AND resets the column-window to the left, so an inactive wide table is not left
    /// frozen scrolled-right with its leading columns unreachable (its width stays pinned to the viewport).
    /// </summary>
    public void ClearActiveCell()
    {
        SetActiveCell(null);

        if (_windowColumn == 0)
            return;

        _windowColumn = 0;
        RecomputeWindow();
        InvalidateArrange();
        foreach (var row in _rows)
            row.InvalidateVisual();
    }

    /// <summary>
    /// Records the caret's cell (<see cref="_activeCell"/>) and moves the per-cell reveal there (Decision 9): the
    /// old row re-formats its previously-active cell, the new row reveals the caret's cell RAW. This applies in
    /// BOTH overflow modes now — the active cell always shows raw markdown so the user edits in place. Under Wrap
    /// the raw (wider) cell can wrap to more rows, so the row REFLOWS and the table height changes; under Truncate
    /// the height is invariant. The focus-dependent caret map is dropped so the next query rebuilds.
    /// </summary>
    private void SetActiveCell((int Row, int Column)? cell)
    {
        if (_activeCell == cell)
            return;

        if (_activeCell is { } prev && prev.Row < _rows.Count)
            _rows[prev.Row].ActiveColumn = -1;
        if (cell is { } now && now.Row < _rows.Count)
            _rows[now.Row].ActiveColumn = now.Column;

        // The active cell renders RAW (1:1) and every other cell FORMATTED (marks hidden) — the map depends on the
        // focused cell in both modes, so drop it and let the next query rebuild on the current geometry.
        _caretMap = null;
        _activeCell = cell;

        // A Wrap reflow (raw active cell wraps to more/fewer rows) changes the table height — recompute it and
        // re-measure so the panel's prefix sums / scroll extent follow (MeasuredCallback → the bridge's RefreshHeight).
        int newHeight = SumHeights();
        if (newHeight != _height)
        {
            _height = newHeight;
            InvalidateMeasure();
        }
    }

    /// <summary>The per-logical-row child presenters (test observability — the committed per-row render boundaries).</summary>
    internal IReadOnlyList<TableRowPresenter> Rows => _rows;

    /// <summary>The grid's total width in cells (test observability).</summary>
    internal int GridWidth => _metrics.Width;

    /// <summary>
    /// Replaces the table's source lines and overlay (the reconcile path): refreshes the shared metrics
    /// and reconciles the row children in place — a same-shape edit touches only the changed rows.
    /// </summary>
    public void SetTable(IReadOnlyList<Line> lines, TableModel model, string source)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);

        _pendingModel = model;
        _pendingSource = source;
        SetContent(lines, []); // refreshes BlockText + fires OnContentChanged, where the children reconcile
    }

    /// <inheritdoc/>
    protected override void OnContentChanged()
    {
        if (_pendingModel is not { } model || _pendingSource is not { } source)
            return;

        _pendingModel = null;
        _pendingSource = null;

        var oldMetrics = _metrics;
        var oldModel = _model;
        var oldSource = _source;
        _model = model;
        _source = source;
        // Re-resolve widths at the CURRENT viewport (WP5 content reflow is a layer under the viewport-fit): a
        // content edit that changes a column's natural width may or may not move the resolved widths, and the
        // metrics-equality check below (geometryChanged) still bounds the re-raster accordingly.
        _metrics = TableGridMetrics.BuildForViewport(model, _measuredColumns);
        _caretMap = null; // the geometry/source moved — the next caret query rebuilds the map
        _windowColumn = WindowActive ? ClampColumn(_windowColumn) : 0; // keep the window valid / reset when it now fits
        RecomputeWindow(); // a content edit can change the grid width → start/stop or re-anchor the window

        // A row-count / column-count change re-forms the child set; otherwise reconcile in place and
        // touch only what actually moved.
        if (model.RowCount != _rows.Count || model.ColumnCount != oldMetrics.ColumnCount)
        {
            RebuildChildren();
            return;
        }

        // The incremental reflow (§5.5 / Decision 11, M3.WP5). A row's rendered output is a pure function of
        // (its source-line text, the shared column geometry). So:
        //   • Column geometry unchanged (metrics equal — the O(1) shrink via CountAtMax already kept a
        //     non-unique-widest delete from moving the width) AND this row's source line unchanged
        //     → the row is byte-identical: skip its Refresh entirely (no LayoutRow / run-map / signature
        //     rebuild — retiring spike-review deferred #7) and leave its raster untouched.
        //   • Only the edited row's source changed (stable geometry) → re-derive + re-raster that one row.
        //   • A width/alignment change moved the divider band → every row's run map shifts, so all rows
        //     re-derive and re-raster (their arranged width changes; the framework re-rasters a boundary on
        //     any size change) — the border re-lands on the new columns. This is bounded to the table; the
        //     surrounding document never re-rasters (separate block boundaries).
        bool geometryChanged = !_metrics.Equals(oldMetrics);
        var oldLineStarts = geometryChanged ? null : LineStarts(oldSource);
        var newLineStarts = geometryChanged ? null : LineStarts(source);

        for (var i = 0; i < _rows.Count; i++)
        {
            if (!geometryChanged)
            {
                int oldStart = LineStartOf(oldLineStarts!, oldModel.RowSourceLine(i));
                int newStart = LineStartOf(newLineStarts!, model.RowSourceLine(i));
                if (oldStart >= 0 && newStart >= 0
                    && LineSpan(oldSource, oldStart).SequenceEqual(LineSpan(source, newStart)))
                {
                    // Untouched row: reuse its layout/raster, only re-binding its (possibly shifted) offsets so
                    // a later selection still lands right — no re-derive, no re-measure, no re-raster (#7).
                    _rows[i].Rebind(model, _metrics, source, newStart - oldStart);
                    continue;
                }
            }

            var (heightChanged, contentChanged) = _rows[i].Refresh(model, _metrics, source);
            if (geometryChanged)
            {
                _rows[i].InvalidateMeasure();
                _rows[i].InvalidateVisual();
            }
            else if (heightChanged)
            {
                _rows[i].InvalidateMeasure();
            }
            else if (contentChanged)
            {
                _rows[i].InvalidateVisual();
            }
        }

        _height = SumHeights();
    }

    /// <summary>The block-relative offset of source line <paramref name="line"/>, or <c>-1</c> when the index is out of range (conservatively forces a re-derive).</summary>
    private static int LineStartOf(int[] lineStarts, int line) => (uint)line < (uint)lineStarts.Length ? lineStarts[line] : -1;

    /// <summary>The block-relative offset each source line begins at (0, then every position after a <c>'\n'</c>).</summary>
    private static int[] LineStarts(string source)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
                starts.Add(i + 1);
        }

        return [.. starts];
    }

    /// <summary>The text of the source line beginning at <paramref name="start"/> (terminator excluded), as a span — no allocation on the diff.</summary>
    private static ReadOnlySpan<char> LineSpan(string source, int start)
    {
        int end = start;
        while (end < source.Length && source[end] != '\n' && source[end] != '\r')
            end++;
        return source.AsSpan(start, end - start);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // The available viewport width is known here (never at parse). A change from the last measure — a
        // terminal RESIZE — re-resolves the column widths and re-lays-out the table (the new trigger beyond
        // WP5's content-edit reflow); an unchanged width is the common case and skips straight to measuring.
        int available = Math.Max(1, availableSize.Columns);
        if (available != _measuredColumns)
        {
            _measuredColumns = available;
            ApplyViewportWidths();
        }

        foreach (var row in _rows)
            row.Measure(availableSize);

        _height = SumHeights();
        MeasuredCallback?.Invoke(this, _height);
        // Report the ON-SCREEN width (the column-window's visible sub-grid when overflowing), never the full grid —
        // so the presenter's desired width never exceeds the viewport (the document has no horizontal extent, FB-6).
        return new Size(RenderedWidth, _height);
    }

    /// <summary>
    /// Re-resolves the column widths for the current viewport (<see cref="_measuredColumns"/>) and, when they
    /// moved, re-lays-out the table: a resize recomputes widths for ALL columns, so this is a geometry change —
    /// every row re-derives its wrapped layout + run map against the new widths and re-rasters its border band,
    /// exactly like a content-driven width change (it flows through the same per-row reconcile). Bounded to the
    /// table: the surrounding document's presenters are separate render boundaries and are never touched here.
    /// Row/column counts are viewport-invariant, so the child set is reused (no <see cref="RebuildChildren"/>).
    /// </summary>
    private void ApplyViewportWidths()
    {
        var oldWindow = _window;
        var newMetrics = TableGridMetrics.BuildForViewport(_model, _measuredColumns);
        bool metricsMoved = !newMetrics.Equals(_metrics);
        if (metricsMoved)
        {
            _metrics = newMetrics;
            _caretMap = null; // the divider band moved — the next caret query rebuilds the map on the new geometry
            foreach (var row in _rows)
                row.Refresh(_model, _metrics, _source);
        }

        // The viewport width itself changed (that is why we are here), so the window's active-ness and draw width
        // can move even when the resolved column widths did not — re-anchor and recompute, then re-follow the
        // caret so a resize that no longer contains it re-syncs BEFORE the caret re-publishes (#4).
        _windowColumn = WindowActive ? ClampColumn(_windowColumn) : 0;
        RecomputeWindow();
        ScrollColumnIntoView(_activeCell?.Column);

        // Re-arrange/re-raster the rows when the grid geometry OR the window moved (a pure window move does not
        // change a row's desired size, so it re-arranges without re-measuring). Bounded to the table.
        if (metricsMoved || _window != oldWindow)
        {
            InvalidateArrange();
            foreach (var row in _rows)
            {
                if (metricsMoved)
                    row.InvalidateMeasure();
                row.InvalidateVisual();
            }
        }
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        int width = RenderedWidth; // the on-screen (windowed) width — rows clip their draws to it
        int y = 0;
        foreach (var row in _rows)
        {
            int height = row.DesiredSize.Rows;
            row.Arrange(new Rect(0, y, width, height));
            y += height;
        }

        return finalSize;
    }

    /// <inheritdoc/>
    protected override int MeasuredRowCount(int width) => _height;

    /// <summary>The table's visuals are its per-row child boundaries; the parent zone paints nothing itself.</summary>
    protected override void Render(RenderContext context)
    {
    }

    /// <summary>
    /// Routes the document selection change to the row children (M3.WP4-deferred #2). The bridge invalidates
    /// this block presenter on a selection change, but the table paints nothing itself — its rows draw the
    /// cell text and highlight — so the invalidation is forwarded to every row whose source span intersects
    /// the old <b>or</b> new selection (so a row losing the highlight repaints too). Bounded to the table.
    /// </summary>
    internal override void InvalidateSelectionOverlay()
    {
        var now = SelectionProvider?.Invoke();
        var lineStarts = LineStarts(_source); // built once — RowIntersects indexes it, no per-row rescan

        // Re-raster every row that now intersects the selection (it gains/changes highlight) and every row
        // that last intersected it (it must clear). Row indices are stable across a same-shape reconcile, so
        // this stays correct even after an edit shifted the source offsets under the selection.
        var previous = _highlightedRows.Count == 0 ? null : new List<int>(_highlightedRows);
        _highlightedRows.Clear();

        for (var i = 0; i < _rows.Count; i++)
        {
            if (RowIntersects(i, now, lineStarts))
            {
                _highlightedRows.Add(i);
                _rows[i].InvalidateVisual();
            }
        }

        if (previous is not null)
        {
            foreach (int i in previous)
                if (!_highlightedRows.Contains(i) && i < _rows.Count)
                    _rows[i].InvalidateVisual();
        }
    }

    /// <summary>
    /// Repopulate <see cref="_highlightedRows"/> from the current selection <b>without</b> re-rastering — the
    /// caller (<see cref="RebuildChildren"/>) built fresh rows that already draw the current highlight. This
    /// keeps the tracking set in sync so a later clear re-rasters the right rows even when the selection's
    /// block-relative range is unchanged (the <c>DocumentCaret</c> was==now gate skips <see cref="InvalidateSelectionOverlay"/>
    /// in that case, so a rebuild that cleared the set would otherwise leave a stale highlight uncleared).
    /// </summary>
    /// <summary>
    /// Realize-time seed (the bridge calls this once the <see cref="LeafBlockPresenter.SelectionProvider"/> is
    /// wired): a table scrolled into view under an active selection paints its rows' highlight from the live
    /// provider, but its <see cref="_highlightedRows"/> tracking was built empty in the ctor (the provider was
    /// null then). Repopulate it now so a later clear — which reaches this table via the caret's realize-time
    /// <c>_selectionPainted</c> seed — actually re-rasters the highlighted rows instead of stranding them.
    /// </summary>
    internal override void SeedSelectionOverlay() => RetrackHighlightedRows();

    private void RetrackHighlightedRows()
    {
        var now = SelectionProvider?.Invoke();
        var lineStarts = LineStarts(_source);
        _highlightedRows.Clear();
        for (var i = 0; i < _rows.Count; i++)
            if (RowIntersects(i, now, lineStarts))
                _highlightedRows.Add(i);
    }

    /// <summary>Whether logical row <paramref name="row"/>'s source line overlaps the block-relative <paramref name="selection"/> range.</summary>
    private bool RowIntersects(int row, (int Start, int End)? selection, int[] lineStarts)
    {
        if (selection is not { } sel || sel.End <= sel.Start)
            return false;

        int lineStart = Math.Max(0, LineStartOf(lineStarts, _model.RowSourceLine(row)));
        int lineEnd = _model.RowTextEndOffset(row);
        return sel.Start < lineEnd && sel.End > lineStart;
    }

    private void BuildChildren()
    {
        // Shared forwarding delegates for every row (they re-read the fields dynamically) — no per-row closure.
        Func<(int Start, int End)?> forward = () => SelectionProvider?.Invoke();
        Func<CellRect?> rectForward = () => CellRectProvider?.Invoke();
        Func<(int Offset, int DrawWidth)> window = Window;
        for (var r = 0; r < _model.RowCount; r++)
        {
            var row = new TableRowPresenter(_model, _metrics, _source, r, _overflow) { SelectionProvider = forward, CellRectProvider = rectForward, WindowProvider = window };
            _rows.Add(row);
            AddVisualChild(row);
        }

        // The focused-cell reveal is re-established by the bridge's next caret publish (RebuildChildren nulls it),
        // so nothing to restore here — a fresh row starts un-revealed.
        _height = SumHeights();
    }

    private void RebuildChildren()
    {
        foreach (var row in _rows)
            RemoveVisualChild(row);
        _rows.Clear();
        _highlightedRows.Clear(); // row indices are about to change — drop the stale highlight set
        _windowColumn = 0;        // column/row counts changed — reset the window (re-followed on the next caret move)
        _activeCell = null;
        RecomputeWindow();        // the reset window feeds the freshly built rows' WindowProvider

        BuildChildren();
        RetrackHighlightedRows(); // re-sync the set to the fresh rows' highlight, so a later clear re-rasters them
        InvalidateMeasure();
    }

    private int SumHeights()
    {
        int total = 0;
        foreach (var row in _rows)
            total += row.RowHeight;
        return total;
    }
}
