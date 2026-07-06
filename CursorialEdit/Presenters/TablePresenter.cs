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

    private TableCaretMap? _caretMap;

    private TableModel? _pendingModel;
    private string? _pendingSource;

    private readonly HashSet<int> _highlightedRows = []; // the row indices whose cells last drew a selection

    /// <summary>Creates the presenter for a table block from its <paramref name="model"/> and serialized <paramref name="source"/>.</summary>
    /// <param name="lines">The block's source lines.</param>
    /// <param name="model">The table overlay built from the block (<see cref="TableModel.Build"/>).</param>
    /// <param name="source">The block's serialized source (must equal the block's <c>BlockText()</c>) — the cell spans index it.</param>
    public TablePresenter(IReadOnlyList<Line> lines, TableModel model, string source)
        : base(lines, [], BlockKind.Table, headingLevel: null, WrapMode.NoWrap)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);

        _model = model;
        _source = source;
        _metrics = TableGridMetrics.Build(model);
        BuildChildren();
    }

    /// <summary>The live table overlay (test observability).</summary>
    internal TableModel Model => _model;

    /// <summary>
    /// The composite caret map for this table (M3.WP4): maps a block-relative source offset to the grid
    /// (row, cell) and back, so the document caret lands <b>inside a cell</b>. Rebuilt in lockstep with the
    /// model/metrics on every reconcile; cached so repeated caret queries within a frame share one instance.
    /// </summary>
    public ICaretMap CaretMap() => _caretMap ??= TableCaretMap.Build(_model, _metrics, _source);

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
        _metrics = TableGridMetrics.Build(model);
        _caretMap = null; // the geometry/source moved — the next caret query rebuilds the map

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
        foreach (var row in _rows)
            row.Measure(availableSize);

        _height = SumHeights();
        MeasuredCallback?.Invoke(this, _height);
        return new Size(_metrics.Width, _height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        int y = 0;
        foreach (var row in _rows)
        {
            int height = row.DesiredSize.Rows;
            row.Arrange(new Rect(0, y, _metrics.Width, height));
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

        // Re-raster every row that now intersects the selection (it gains/changes highlight) and every row
        // that last intersected it (it must clear). Row indices are stable across a same-shape reconcile, so
        // this stays correct even after an edit shifted the source offsets under the selection.
        var previous = _highlightedRows.Count == 0 ? null : new List<int>(_highlightedRows);
        _highlightedRows.Clear();

        for (var i = 0; i < _rows.Count; i++)
        {
            if (RowIntersects(i, now))
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

    /// <summary>Whether logical row <paramref name="row"/>'s source line overlaps the block-relative <paramref name="selection"/> range.</summary>
    private bool RowIntersects(int row, (int Start, int End)? selection)
    {
        if (selection is not { } sel || sel.End <= sel.Start)
            return false;

        int lineStart = RowLineStart(row);
        int lineEnd = _model.RowTextEndOffset(row);
        return sel.Start < lineEnd && sel.End > lineStart;
    }

    /// <summary>The block-relative offset row <paramref name="row"/>'s source line begins at (the position after the Nth newline, N = the model's line index for the row).</summary>
    private int RowLineStart(int row)
    {
        int target = _model.RowSourceLine(row);
        if (target <= 0)
            return 0;

        int seen = 0;
        for (var i = 0; i < _source.Length; i++)
        {
            if (_source[i] == '\n' && ++seen == target)
                return i + 1;
        }

        return 0;
    }

    private void BuildChildren()
    {
        for (var r = 0; r < _model.RowCount; r++)
        {
            var row = new TableRowPresenter(_model, _metrics, _source, r)
            {
                SelectionProvider = () => SelectionProvider?.Invoke(),
            };
            _rows.Add(row);
            AddVisualChild(row);
        }

        _height = SumHeights();
    }

    private void RebuildChildren()
    {
        foreach (var row in _rows)
            RemoveVisualChild(row);
        _rows.Clear();
        _highlightedRows.Clear(); // row indices are about to change — drop the stale highlight set

        BuildChildren();
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
