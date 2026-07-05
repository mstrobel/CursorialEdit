using System.Text;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// One logical table row rendered as its own <b>render boundary</b> (architecture §2.5 / Decision 7 —
/// the committed M3 deliverable): a keystroke that edits a cell re-rasters exactly this row's zone, not
/// the whole table. A row draws its content visual rows (<c>│ cell │ cell │</c>, one per wrapped visual
/// row from <see cref="TableModel.LayoutRow"/>) and its trailing border — the <c>├─┼─┤</c> separator
/// between rows or the <c>└─┴─┘</c> bottom border after the last — and logical row 0 additionally owns
/// the <c>┌─┬─┐</c> top border. Its visual lines are built as per-visual-row run maps
/// (<see cref="TableVisualLine"/>): synthetic glyph runs for the box borders and dividers
/// (<c>SrcLen == 0</c>, no caret stop), and a <see cref="RunKind.Text"/> run per cell fragment mapping
/// to its block-relative cell source — so drawing is driven straight from the map WP9 will navigate.
/// </summary>
internal sealed class TableRowPresenter : UIElement
{
    private TableModel _model;
    private TableGridMetrics _metrics;
    private string _blockText;
    private readonly int _logicalRow;

    private TableVisualLine[] _lines;
    private string _signature;

    public TableRowPresenter(TableModel model, TableGridMetrics metrics, string blockText, int logicalRow)
    {
        _model = model;
        _metrics = metrics;
        _blockText = blockText;
        _logicalRow = logicalRow;
        IsRenderBoundary = true; // Decision 7 — a cell edit re-rasters one row zone

        _lines = BuildLines(out _signature);
    }

    /// <summary>The row's rendered height in cells (visual rows + owned borders) — the height the table stacks with.</summary>
    public int RowHeight => _lines.Length;

    /// <summary>Number of <see cref="Render"/> calls — the per-row raster observable the R3 benchmark diffs against.</summary>
    public int RenderCount { get; private set; }

    /// <summary>The row's visual lines (run maps per visual row) — test observability for the border/cell assertions.</summary>
    internal IReadOnlyList<TableVisualLine> VisualLines => _lines;

    /// <summary>
    /// Re-derives the row from a fresh model/metrics/source (the reconcile path) and reports what changed —
    /// the owning <see cref="TablePresenter"/> decides invalidation (a height change re-measures, a
    /// content-only change re-rasters just this one zone, an unchanged row is left untouched, giving the
    /// R3 "one row zone" economy). No invalidation happens here.
    /// </summary>
    /// <returns>Whether the row's height and/or drawn content changed.</returns>
    public (bool HeightChanged, bool ContentChanged) Refresh(TableModel model, TableGridMetrics metrics, string blockText)
    {
        _model = model;
        _metrics = metrics;
        _blockText = blockText;

        int oldHeight = _lines.Length;
        _lines = BuildLines(out string signature);
        bool heightChanged = _lines.Length != oldHeight;
        bool contentChanged = heightChanged || !string.Equals(signature, _signature, StringComparison.Ordinal);
        _signature = signature;

        return (heightChanged, contentChanged);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => new(_metrics.Width, _lines.Length);

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        RenderCount++;

        if (context.Bounds.IsEmpty)
            return;

        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        var borderBrush = MarkdownStyles.RuleBrush(this);
        var headerFill = MarkdownStyles.CodeFillBrush(this);
        var headerStyle = CellStyle.Default.WithAttributes(TextAttributes.Bold);

        for (var row = 0; row < _lines.Length; row++)
        {
            var line = _lines[row];

            if (line is { Kind: TableLineKind.Content, IsHeader: true } && _metrics.Width > 0)
                context.FillRectangle(new Rect(0, row, _metrics.Width, 1), headerFill); // header fill under the glyphs

            foreach (var run in line.Runs)
            {
                if (run.Kind == RunKind.Synthetic)
                {
                    if (run.Glyph is { Length: > 0 } glyph)
                        context.DrawText(run.Col, row, glyph, borderBrush, null, CellStyle.Default);
                    continue;
                }

                if (run.SrcLen <= 0)
                    continue;

                var slice = Slice(run.SrcStart, run.SrcLen);
                if (slice.IsEmpty)
                    continue;

                var style = line.IsHeader ? headerStyle : CellStyle.Default;
                context.DrawText(run.Col, row, slice, foreground, null, style);
            }
        }
    }

    // ───────────────────────────── visual-line (run-map) construction ─────────────────────────────

    private TableVisualLine[] BuildLines(out string signature)
    {
        var layout = _model.LayoutRow(_logicalRow);
        bool isLast = _logicalRow == _model.RowCount - 1;

        var lines = new List<TableVisualLine>();
        if (_logicalRow == 0)
            lines.Add(BorderLine(TableLineKind.TopBorder));

        foreach (var visualRow in layout.VisualRows)
            lines.Add(ContentLine(visualRow, layout.IsHeader));

        lines.Add(BorderLine(isLast ? TableLineKind.BottomBorder : TableLineKind.Separator));

        signature = Signature(lines);
        return [.. lines];
    }

    private TableVisualLine BorderLine(TableLineKind kind)
    {
        int columns = _metrics.ColumnCount;
        var runs = new List<Run>(columns * 2 + 1);

        for (var c = 0; c <= columns; c++)
        {
            string junction = Junction(kind, c, columns);
            runs.Add(new Run(0, 0, _metrics.BorderX(c), 1, RunKind.Synthetic) { Glyph = junction });

            if (c < columns)
            {
                int fillStart = _metrics.BorderX(c) + 1;
                int fillWidth = _metrics.BorderX(c + 1) - fillStart;
                if (fillWidth > 0)
                    runs.Add(new Run(0, 0, fillStart, fillWidth, RunKind.Synthetic) { Glyph = Repeat(TableBox.Horizontal, fillWidth) });
            }
        }

        return new TableVisualLine(kind, isHeader: false, [.. runs]);
    }

    private TableVisualLine ContentLine(TableVisualRow visualRow, bool isHeader)
    {
        int columns = _metrics.ColumnCount;
        var runs = new List<Run>(columns * 2 + 1);

        for (var c = 0; c <= columns; c++)
            runs.Add(new Run(0, 0, _metrics.BorderX(c), 1, RunKind.Synthetic) { Glyph = TableBox.Vertical });

        for (var c = 0; c < columns; c++)
        {
            var fragment = visualRow.Cell(c);
            if (fragment.IsEmpty)
                continue;

            int x = _metrics.AlignedX(c, fragment.Width);
            runs.Add(new Run(fragment.SrcStart, fragment.SrcLength, x, fragment.Width, RunKind.Text));
        }

        runs.Sort(static (l, r) => l.Col - r.Col);
        return new TableVisualLine(TableLineKind.Content, isHeader, [.. runs]);
    }

    private static string Junction(TableLineKind kind, int column, int columnCount)
    {
        bool first = column == 0;
        bool last = column == columnCount;
        return kind switch
        {
            TableLineKind.TopBorder => first ? TableBox.TopLeft : last ? TableBox.TopRight : TableBox.TopTee,
            TableLineKind.BottomBorder => first ? TableBox.BottomLeft : last ? TableBox.BottomRight : TableBox.BottomTee,
            _ => first ? TableBox.MidLeft : last ? TableBox.MidRight : TableBox.Cross, // Separator
        };
    }

    private static string Repeat(string glyph, int count)
    {
        if (count <= 1)
            return glyph;
        return string.Create(count, glyph, static (span, g) => span.Fill(g[0]));
    }

    // A cheap render signature: the runs' placed glyphs/slices in draw order, so an intra-cell edit that
    // changes what this row draws is detected (→ re-raster) while an untouched row stays byte-identical.
    private string Signature(List<TableVisualLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            foreach (var run in line.Runs)
            {
                sb.Append(run.Col).Append(':');
                if (run.Kind == RunKind.Synthetic)
                    sb.Append(run.Glyph);
                else
                    sb.Append(Slice(run.SrcStart, run.SrcLen));
                sb.Append('|');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private ReadOnlySpan<char> Slice(int start, int length)
    {
        if (start < 0 || start >= _blockText.Length || length <= 0)
            return default;
        return _blockText.AsSpan(start, Math.Min(length, _blockText.Length - start));
    }
}
