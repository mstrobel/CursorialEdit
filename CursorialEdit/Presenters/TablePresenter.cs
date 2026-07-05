using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

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

    private TableModel? _pendingModel;
    private string? _pendingSource;

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
        _model = model;
        _source = source;
        _metrics = TableGridMetrics.Build(model);

        // A row-count / column-count change re-forms the child set; otherwise reconcile in place and
        // invalidate only what moved — a width/alignment change re-measures + re-rasters every row (borders
        // shifted), a content-only edit re-rasters just the row that changed.
        if (model.RowCount != _rows.Count || model.ColumnCount != oldMetrics.ColumnCount)
        {
            RebuildChildren();
            return;
        }

        bool geometryChanged = !_metrics.Equals(oldMetrics);
        for (var i = 0; i < _rows.Count; i++)
        {
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

    private void BuildChildren()
    {
        for (var r = 0; r < _model.RowCount; r++)
        {
            var row = new TableRowPresenter(_model, _metrics, _source, r);
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
