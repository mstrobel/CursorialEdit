using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The box-drawing glyphs a table grid is built from (<c>┌┬┐├┼┤└┴┘─│</c>, §5.1). Each is a
/// width-1 cell under <see cref="Cursorial.Text.GraphemeWidth"/>, so borders and content align in
/// any font when every cell is pinned (§5.1 [CRITICAL]).
/// </summary>
internal static class TableBox
{
    public const string Horizontal = "─";
    public const string Vertical = "│";

    public const string TopLeft = "┌";
    public const string TopTee = "┬";
    public const string TopRight = "┐";

    public const string MidLeft = "├";
    public const string Cross = "┼";
    public const string MidRight = "┤";

    public const string BottomLeft = "└";
    public const string BottomTee = "┴";
    public const string BottomRight = "┘";

    /// <summary>Cells of horizontal padding inside each cell box, on each side of the content (<c>│ content │</c>).</summary>
    public const int Padding = 1;
}

/// <summary>
/// The fixed horizontal geometry of a table grid, derived purely from a <see cref="TableModel"/>'s
/// per-column widths (M3.WP2): where each vertical divider sits, where each column's content region
/// begins, and the grid's total width — all in cells. The presenter and its per-row children share one
/// instance so borders and cells line up by construction; two grids compare equal iff their divider
/// layout is identical, which the presenter uses to tell a content-only edit (re-raster one row) from a
/// width change (re-raster the border band).
/// </summary>
internal sealed class TableGridMetrics : IEquatable<TableGridMetrics>
{
    private readonly int[] _border;    // x of the vertical divider before column c; [ColumnCount] = right edge
    private readonly int[] _contentX;  // x where column c's content region starts
    private readonly int[] _width;     // column c's content width in cells
    private readonly ColumnAlignment[] _align;

    private TableGridMetrics(int[] border, int[] contentX, int[] width, ColumnAlignment[] align, int totalWidth)
    {
        _border = border;
        _contentX = contentX;
        _width = width;
        _align = align;
        Width = totalWidth;
    }

    /// <summary>The number of columns.</summary>
    public int ColumnCount => _width.Length;

    /// <summary>The grid's total width in cells (left border to right border, inclusive).</summary>
    public int Width { get; }

    /// <summary>The x of the vertical divider before column <paramref name="c"/> (<c>c == ColumnCount</c> is the right border).</summary>
    public int BorderX(int c) => _border[c];

    /// <summary>The x where column <paramref name="c"/>'s content region begins (after the divider and left padding).</summary>
    public int ContentX(int c) => _contentX[c];

    /// <summary>The content width of column <paramref name="c"/> in cells.</summary>
    public int ColumnWidth(int c) => _width[c];

    /// <summary>The resolved per-column content widths in cells — passed straight to <see cref="TableModel.LayoutRow(int, IReadOnlyList{int}, TableOverflow)"/> so the wrapped cell fragments use the same widths the borders are drawn at.</summary>
    public IReadOnlyList<int> ColumnWidths => _width;

    /// <summary>The alignment of column <paramref name="c"/>.</summary>
    public ColumnAlignment Alignment(int c) => _align[c];

    /// <summary>The x a fragment of <paramref name="fragmentWidth"/> cells draws at within column <paramref name="c"/>, honouring its alignment.</summary>
    public int AlignedX(int c, int fragmentWidth)
    {
        int slack = Math.Max(0, _width[c] - fragmentWidth);
        return _align[c] switch
        {
            ColumnAlignment.Right => _contentX[c] + slack,
            ColumnAlignment.Center => _contentX[c] + slack / 2,
            _ => _contentX[c],
        };
    }

    /// <summary>The non-content chrome of a <paramref name="columns"/>-column grid in cells: per column a divider + two padding cells, plus the trailing right border. The content budget is the viewport width minus this.</summary>
    public static int ChromeWidth(int columns) => columns * (1 + 2 * TableBox.Padding) + 1;

    /// <summary>
    /// Builds the grid geometry for <paramref name="model"/> given a <paramref name="availableColumns"/>
    /// viewport width (Decision 11, revised): the column widths come from the viewport-aware auto-layout
    /// (<see cref="TableModel.ResolveColumnWidths"/> over the content budget = viewport − <see cref="ChromeWidth"/>),
    /// so a table that fits grows its columns to content and one that overflows shrinks (and word-wraps) the
    /// widest. A non-positive <paramref name="availableColumns"/> (pre-measure / unknown) falls back to the
    /// content-clamped <see cref="Build(TableModel)"/>.
    /// </summary>
    public static TableGridMetrics BuildForViewport(TableModel model, int availableColumns)
    {
        if (availableColumns <= 0)
            return Build(model);

        var widths = model.ResolveColumnWidths(availableColumns - ChromeWidth(model.ColumnCount));
        return Build(model, widths);
    }

    /// <summary>Builds the geometry from <paramref name="model"/>'s viewport-unaware <see cref="TableModel.ColumnWidth"/> fallback widths (pre-measure / off-band).</summary>
    public static TableGridMetrics Build(TableModel model)
    {
        int columns = model.ColumnCount;
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
            widths[c] = model.ColumnWidth(c);
        return Build(model, widths);
    }

    /// <summary>Builds the geometry from explicit resolved column <paramref name="widths"/> (the viewport-aware path) — the dividers, content origins, and total width all derive from them.</summary>
    public static TableGridMetrics Build(TableModel model, IReadOnlyList<int> widths)
    {
        int columns = model.ColumnCount;
        var border = new int[columns + 1];
        var contentX = new int[columns];
        var width = new int[columns];
        var align = new ColumnAlignment[columns];

        int x = 0;
        for (var c = 0; c < columns; c++)
        {
            width[c] = c < widths.Count ? widths[c] : model.ColumnWidth(c);
            align[c] = model.Alignment(c);
            border[c] = x;
            contentX[c] = x + 1 + TableBox.Padding; // skip the divider and the left padding
            x += 1 + 2 * TableBox.Padding + width[c]; // divider + left pad + content + right pad
        }

        border[columns] = x;
        return new TableGridMetrics(border, contentX, width, align, x + 1);
    }

    public bool Equals(TableGridMetrics? other) =>
        other is not null
        && Width == other.Width
        && _border.AsSpan().SequenceEqual(other._border)
        && _align.AsSpan().SequenceEqual(other._align);

    public override bool Equals(object? obj) => Equals(obj as TableGridMetrics);

    public override int GetHashCode() => HashCode.Combine(Width, ColumnCount);
}

/// <summary>What a table presenter's visual line is (borders vs content) — drives its glyph/fill decoration.</summary>
internal enum TableLineKind
{
    /// <summary>The <c>┌─┬─┐</c> top border (owned by logical row 0).</summary>
    TopBorder,

    /// <summary>A <c>│ cell │</c> content visual row.</summary>
    Content,

    /// <summary>A <c>├─┼─┤</c> separator between two logical rows.</summary>
    Separator,

    /// <summary>The <c>└─┴─┘</c> bottom border (below the last logical row).</summary>
    BottomBorder,
}

/// <summary>
/// One visual line of a table row presenter: its kind and its <see cref="Run"/> map (M3.WP2 deliverable).
/// Border lines carry only <see cref="RunKind.Synthetic"/> glyph runs (<c>SrcLen == 0</c>, no caret
/// stop); a content line carries synthetic <c>│</c> dividers plus a <see cref="RunKind.Text"/> run per
/// cell fragment mapping to its block-relative cell source (so a WP9 caret can land in the cell). The
/// presenter draws straight from these runs, so the map is the single source of what appears on screen.
/// </summary>
internal sealed class TableVisualLine
{
    public TableVisualLine(TableLineKind kind, bool isHeader, Run[] runs)
    {
        Kind = kind;
        IsHeader = isHeader;
        Runs = runs;
    }

    public TableLineKind Kind { get; }

    /// <summary>Whether this is a header content line (bold + fill).</summary>
    public bool IsHeader { get; }

    public Run[] Runs { get; }
}
