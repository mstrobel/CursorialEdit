using Cursorial.Text;

using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableAlign = Markdig.Extensions.Tables.TableColumnAlign;

namespace CursorialEdit.Document.Model;

/// <summary>
/// The M3.WP1 table overlay (architecture Decision 1 / 11, §2.5): a <b>derived, invalidatable</b>
/// projection of a <see cref="BlockKind.Table"/> block's source lines — <b>never</b> a second source
/// of truth. It is rebuilt from the block's Markdig <c>Table</c> AST on every (re)parse, so the source
/// text stays canonical and the model is thrown away and re-derived per edit, exactly like
/// <see cref="Block.InlineRuns"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cell spans come from Markdig, never a hand pipe-scanner (M3 risk d).</b> Each cell's
/// <see cref="CellSpan"/> is the block-relative UTF-16 <c>(start, length)</c> of Markdig's precise
/// <c>TableCell.Span</c> (<c>UsePreciseSourceLocation</c>), converted the same way
/// <see cref="Block.RealizeInlineRuns"/> converts inline spans: <c>markdigSpan.Start −
/// <see cref="Block.SourceStartOffset"/></c>. Because both the origin and the spans are in the parse
/// text's coordinate space, this is correct under a full <i>and</i> a windowed parse. Escaped pipes
/// (<c>\|</c>) and backtick-pipes (<c>`a|b`</c>) are grouped by Markdig into one cell, so its span
/// reproduces the exact source slice with no bespoke escaping rules of our own. Markdig normalises a
/// ragged table to a uniform cell count (padding short rows with empty cells whose span is empty), and
/// keeps the delimiter row out of the row set — both surface here unchanged.
/// </para>
/// <para>
/// <b>Widths are measured in cells (§5.1 [CRITICAL]).</b> Per column the model caches
/// <c>(WidthCells, MaxContentWidth, CountAtMax)</c>: <see cref="MaxContentWidth"/> is the maximum
/// <see cref="GraphemeWidth.StringWidth"/> over the column's trimmed cell contents (header and body),
/// <see cref="ColumnWidth"/> is that clamped to <c>[3, 40]</c>, and <see cref="CountAtMax"/> is how
/// many cells sit at the maximum — the O(1) shrink-detection cache (Decision 11): WP5 recomputes a
/// column only when the <i>unique</i> widest cell (<c>CountAtMax == 1</c>) shrank.
/// </para>
/// <para>
/// <b>The cell-layout pass (<see cref="LayoutRow"/>) is the single owner of wrapped-cell → visual-row
/// mapping (M3 risk a).</b> Given the column widths and an overflow mode (WP1 implements
/// <see cref="TableOverflow.Wrap"/> only), it emits, per logical row, the list of visual rows: a cell
/// whose content exceeds its column width wraps to multiple visual rows (splitting only on grapheme
/// boundaries, never a wide cluster), so a logical row occupies <c>N = max-over-cells</c> visual rows
/// and each visual row carries one <see cref="CellFragment"/> per column. WP2's presenter consumes this
/// and never re-derives it.
/// </para>
/// <para>
/// <b>Markdig quarantine.</b> This type lives in the Document project (the only one that references
/// Markdig) and casts <see cref="Block.MarkdigBlock"/> internally; its whole public surface is plain
/// data (offsets, widths, alignment, fragments), so a presenter consumes it without ever naming a
/// Markdig type (<c>ArchitectureTests</c>).
/// </para>
/// </remarks>
public sealed class TableModel
{
    private readonly string _source;
    private readonly Column[] _columns;
    private readonly Row[] _rows;

    private TableModel(string source, Column[] columns, Row[] rows)
    {
        _source = source;
        _columns = columns;
        _rows = rows;
    }

    /// <summary>The number of logical rows (header + body; the delimiter row is not a row).</summary>
    public int RowCount => _rows.Length;

    /// <summary>The number of columns (the widest of the delimiter row and any body row — a ragged table's excess column exists here).</summary>
    public int ColumnCount => _columns.Length;

    /// <summary>The GFM alignment of <paramref name="column"/> (from the delimiter row); <see cref="ColumnAlignment.None"/> for an unaligned or ragged-excess column.</summary>
    public ColumnAlignment Alignment(int column) => _columns[column].Alignment;

    /// <summary>The rendered width of <paramref name="column"/> in cells: <see cref="MaxContentWidth"/> clamped to <c>[3, 40]</c> (§5.1).</summary>
    public int ColumnWidth(int column) => _columns[column].WidthCells;

    /// <summary>The maximum trimmed-content cell width in <paramref name="column"/>, measured in cells (pre-clamp) — the reflow input.</summary>
    public int MaxContentWidth(int column) => _columns[column].MaxContentWidth;

    /// <summary>How many of <paramref name="column"/>'s cells sit at <see cref="MaxContentWidth"/> — the O(1) shrink-detection cache (Decision 11).</summary>
    public int CountAtMax(int column) => _columns[column].CountAtMax;

    /// <summary>Whether logical <paramref name="row"/> is the (always-present) header row — GFM row 0.</summary>
    public bool IsHeaderRow(int row) => _rows[row].IsHeader;

    /// <summary>The block-relative source line index logical <paramref name="row"/> renders (the delimiter line is skipped, so body rows are not contiguous with the header).</summary>
    public int RowSourceLine(int row) => _rows[row].SourceLine;

    /// <summary>The per-column cell spans of logical <paramref name="row"/> (block-relative Markdig cell boundaries; an empty cell has <see cref="CellSpan.IsEmpty"/>).</summary>
    public IReadOnlyList<CellSpan> Cells(int row) => _rows[row].Cells;

    /// <summary>
    /// The exact source slice Markdig delimits for the cell at (<paramref name="row"/>,
    /// <paramref name="column"/>) — the "extraction ≡ Markdig cell boundaries" observable (M3 done-when):
    /// reproduces the source between the pipes (padding and all), or <c>""</c> for an empty cell.
    /// </summary>
    public string CellSource(int row, int column) => Slice(_rows[row].Cells[column]);

    /// <summary>The trimmed visible content of the cell at (<paramref name="row"/>, <paramref name="column"/>) — leading/trailing spaces and tabs removed, as GFM renders it.</summary>
    public string CellContent(int row, int column) => Content(_rows[row].Cells[column]).Text;

    /// <summary>
    /// The cell-layout pass for logical <paramref name="row"/> under <paramref name="overflow"/> (M3 risk a):
    /// the ordered list of visual rows the row occupies, each carrying one <see cref="CellFragment"/> per
    /// column. A cell wider than its column wraps to several fragments (grapheme-snapped), so the row's
    /// visual height is the maximum fragment count over its cells (≥ 1). WP1 implements
    /// <see cref="TableOverflow.Wrap"/> only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="overflow"/> is not <see cref="TableOverflow.Wrap"/> (truncate / column-window are WP6).</exception>
    public TableRowLayout LayoutRow(int row, TableOverflow overflow = TableOverflow.Wrap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);
        if (overflow != TableOverflow.Wrap)
            throw new ArgumentOutOfRangeException(nameof(overflow), overflow, "WP1 implements Wrap only; truncate and column-window are M3.WP6.");

        var r = _rows[row];
        int columns = _columns.Length;

        // Per column: the cell's content wrapped to its column width. The row's visual height is the max
        // fragment count over its cells — the single place wrapped-cell → visual-row mapping is decided.
        var perColumn = new CellFragment[columns][];
        int visualRows = 1;
        for (var c = 0; c < columns; c++)
        {
            perColumn[c] = WrapCell(r.Cells[c], _columns[c].WidthCells);
            visualRows = Math.Max(visualRows, perColumn[c].Length);
        }

        var rows = new TableVisualRow[visualRows];
        for (var v = 0; v < visualRows; v++)
        {
            var cells = new CellFragment[columns];
            for (var c = 0; c < columns; c++)
                cells[c] = v < perColumn[c].Length ? perColumn[c][v] : CellFragment.Empty;
            rows[v] = new TableVisualRow(cells);
        }

        return new TableRowLayout(row, r.IsHeader, r.SourceLine, rows);
    }

    /// <summary>
    /// Builds a <see cref="TableModel"/> from <paramref name="block"/> (which must be a
    /// <see cref="BlockKind.Table"/> block carrying a Markdig <c>Table</c>) over its serialized
    /// <paramref name="blockSource"/> — the same string a presenter's <c>BlockText()</c> produces, so
    /// the block-relative cell spans index it directly. Returns <see langword="null"/> when
    /// <paramref name="block"/> has no Markdig table backing (a degenerate/synthetic block).
    /// </summary>
    /// <param name="block">The table block; its <see cref="Block.MarkdigBlock"/> is read internally.</param>
    /// <param name="blockSource">The block's serialized source (lines + terminators) — the block-relative span origin.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static TableModel? Build(Block block, string blockSource)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(blockSource);

        if (block.MarkdigBlock is not MdTable table)
            return null;

        int origin = block.SourceStartOffset;
        int sourceLength = blockSource.Length;

        // Pass 1 — collect each row's cells as block-relative spans, tracking the widest cell count so a
        // ragged table's excess column is represented (Markdig already normalises rows, but be defensive).
        var rowSpans = new List<CellSpan[]>();
        var headerFlags = new List<bool>();
        var rowStartOffsets = new List<int>();
        int columnCount = table.ColumnDefinitions.Count;

        foreach (var rowObj in table)
        {
            if (rowObj is not MdTableRow mdRow)
                continue;

            var cells = new List<CellSpan>();
            foreach (var cellObj in mdRow)
            {
                if (cellObj is MdTableCell mdCell)
                    cells.Add(ToCellSpan(mdCell.Span, origin, sourceLength));
            }

            columnCount = Math.Max(columnCount, cells.Count);
            rowSpans.Add([.. cells]);
            headerFlags.Add(mdRow.IsHeader);
            rowStartOffsets.Add(RowStartOffset(mdRow, cells, origin, sourceLength));
        }

        columnCount = Math.Max(columnCount, 1);

        // Pad every row to the column count (short rows get empty trailing cells — GFM's implicit blanks).
        for (var i = 0; i < rowSpans.Count; i++)
        {
            if (rowSpans[i].Length < columnCount)
            {
                var padded = new CellSpan[columnCount];
                Array.Copy(rowSpans[i], padded, rowSpans[i].Length);
                for (var c = rowSpans[i].Length; c < columnCount; c++)
                    padded[c] = CellSpan.Empty;
                rowSpans[i] = padded;
            }
        }

        // Column alignment from the delimiter row; a ragged-excess column has none.
        var columns = new Column[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var align = c < table.ColumnDefinitions.Count ? MapAlignment(table.ColumnDefinitions[c].Alignment) : ColumnAlignment.None;
            columns[c] = MeasureColumn(blockSource, rowSpans, c, align);
        }

        // Row source lines: count line terminators up to each row's block-relative start (self-contained;
        // the delimiter line is naturally skipped because no row starts on it).
        var rows = new Row[rowSpans.Count];
        for (var i = 0; i < rowSpans.Count; i++)
        {
            int line = SourceLineAt(blockSource, rowStartOffsets[i]);
            rows[i] = new Row(rowSpans[i], headerFlags[i], line);
        }

        return new TableModel(blockSource, columns, rows);
    }

    // ───────────────────────────── span extraction ─────────────────────────────

    private static CellSpan ToCellSpan(Markdig.Syntax.SourceSpan span, int origin, int sourceLength)
    {
        if (span.IsEmpty || span.Length <= 0)
            return CellSpan.Empty;

        int start = span.Start - origin;
        int length = span.Length;

        // Drop a span pointing outside the block's own source (a Markdig precise-span quirk) rather than
        // hand back an out-of-bounds slice — the same defense Block.RealizeInlineRuns applies to inlines.
        if (start < 0 || start + length > sourceLength)
            return CellSpan.Empty;

        return new CellSpan(start, length);
    }

    private static int RowStartOffset(MdTableRow mdRow, List<CellSpan> cells, int origin, int sourceLength)
    {
        // Prefer the row's own precise span; fall back to its first non-empty cell (an all-empty row keeps 0).
        if (!mdRow.Span.IsEmpty && mdRow.Span.Length > 0)
        {
            int start = mdRow.Span.Start - origin;
            if (start >= 0 && start <= sourceLength)
                return start;
        }

        foreach (var cell in cells)
        {
            if (!cell.IsEmpty)
                return cell.Start;
        }

        return 0;
    }

    private static int SourceLineAt(string source, int offset)
    {
        int line = 0;
        int end = Math.Min(offset, source.Length);
        for (var i = 0; i < end; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    // ───────────────────────────── width cache ─────────────────────────────

    private static Column MeasureColumn(string source, List<CellSpan[]> rowSpans, int column, ColumnAlignment align)
    {
        // Single pass max + count-at-max: when a new maximum appears the count resets to 1; equals to the
        // running max increment it — so the final count is the tally at the final maximum (Decision 11).
        int max = 0;
        int countAtMax = 0;
        foreach (var cells in rowSpans)
        {
            int width = ContentWidth(source, cells[column]);
            if (width > max)
            {
                max = width;
                countAtMax = 1;
            }
            else if (width == max)
            {
                countAtMax++;
            }
        }

        return new Column(align, Math.Clamp(max, MinWidth, MaxWidth), max, countAtMax);
    }

    private static int ContentWidth(string source, CellSpan span)
    {
        // Measure over the trimmed slice directly — no discarded substring allocation on the width pass
        // (this runs per cell on every reflow/keystroke).
        if (span.IsEmpty)
            return 0;

        var slice = source.AsSpan(span.Start, span.Length);
        int lead = 0;
        while (lead < slice.Length && (slice[lead] == ' ' || slice[lead] == '\t'))
            lead++;
        int tail = slice.Length;
        while (tail > lead && (slice[tail - 1] == ' ' || slice[tail - 1] == '\t'))
            tail--;

        return GraphemeWidth.StringWidth(slice[lead..tail]);
    }

    // ───────────────────────────── content + wrapping ─────────────────────────────

    private (int SrcStart, string Text) Content(CellSpan span) => ContentOf(_source, span);

    private static (int SrcStart, string Text) ContentOf(string source, CellSpan span)
    {
        if (span.IsEmpty)
            return (Math.Clamp(span.Start, 0, source.Length), string.Empty);

        var slice = source.AsSpan(span.Start, span.Length);
        int lead = 0;
        while (lead < slice.Length && (slice[lead] == ' ' || slice[lead] == '\t'))
            lead++;
        int tail = slice.Length;
        while (tail > lead && (slice[tail - 1] == ' ' || slice[tail - 1] == '\t'))
            tail--;

        return (span.Start + lead, slice[lead..tail].ToString());
    }

    private string Slice(CellSpan span) =>
        span.IsEmpty ? string.Empty : _source.Substring(span.Start, span.Length);

    /// <summary>
    /// Wraps one cell's trimmed content to <paramref name="width"/> cells, splitting only on grapheme
    /// boundaries (a wide cluster is never halved — M3 risk a). An empty cell yields a single empty
    /// fragment so it still occupies one visual row.
    /// </summary>
    private CellFragment[] WrapCell(CellSpan span, int width)
    {
        var (srcStart, text) = Content(span);
        if (text.Length == 0)
            return [new CellFragment(srcStart, 0, 0)];

        int budget = Math.Max(1, width);
        var fragments = new List<CellFragment>();

        int fragStart = srcStart;
        int fragChars = 0;
        int fragWidth = 0;
        int offset = 0;

        var clusters = text.AsSpan().GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            int clusterWidth = GraphemeWidth.ClusterWidth(cluster);

            if (fragWidth > 0 && fragWidth + clusterWidth > budget)
            {
                fragments.Add(new CellFragment(fragStart, fragChars, fragWidth));
                fragStart = srcStart + offset;
                fragChars = 0;
                fragWidth = 0;
            }

            fragChars += cluster.Length;
            fragWidth += clusterWidth;
            offset += cluster.Length;
        }

        fragments.Add(new CellFragment(fragStart, fragChars, fragWidth));
        return [.. fragments];
    }

    private static ColumnAlignment MapAlignment(MdTableAlign? align) => align switch
    {
        MdTableAlign.Left => ColumnAlignment.Left,
        MdTableAlign.Center => ColumnAlignment.Center,
        MdTableAlign.Right => ColumnAlignment.Right,
        _ => ColumnAlignment.None,
    };

    /// <summary>The minimum rendered column width in cells (§5.1 [DECISION]).</summary>
    public const int MinWidth = 3;

    /// <summary>The maximum rendered column width in cells before content wraps (§5.1 [DECISION]).</summary>
    public const int MaxWidth = 40;

    private readonly record struct Column(ColumnAlignment Alignment, int WidthCells, int MaxContentWidth, int CountAtMax);

    private readonly record struct Row(CellSpan[] Cells, bool IsHeader, int SourceLine);
}

/// <summary>The GFM alignment of a table column (from the delimiter row's <c>:---</c>/<c>:--:</c>/<c>--:</c>).</summary>
public enum ColumnAlignment
{
    /// <summary>No explicit alignment (a plain <c>---</c> delimiter, or a ragged-excess column) — rendered left.</summary>
    None = 0,

    /// <summary>Left-aligned (<c>:---</c>).</summary>
    Left,

    /// <summary>Centered (<c>:--:</c>).</summary>
    Center,

    /// <summary>Right-aligned (<c>--:</c>).</summary>
    Right,
}

/// <summary>The cell-overflow mode the layout pass honours. WP1 implements <see cref="Wrap"/>; truncate and column-window arrive in M3.WP6.</summary>
public enum TableOverflow
{
    /// <summary>Content wider than the column wraps within the cell — the row grows taller (the v1 default, §5.6).</summary>
    Wrap = 0,
}

/// <summary>
/// The block-relative UTF-16 span Markdig delimits for one cell (M3 risk d): <see cref="Start"/> and
/// <see cref="Length"/> index the block's serialized source, reproducing the exact cell slice. An empty
/// cell (GFM implicit blank, or Markdig's ragged padding) has <see cref="IsEmpty"/>.
/// </summary>
/// <param name="Start">Block-relative UTF-16 offset of the cell's source slice.</param>
/// <param name="Length">Length of the slice in UTF-16 code units (0 for an empty cell).</param>
public readonly record struct CellSpan(int Start, int Length)
{
    /// <summary>The empty cell span (no source).</summary>
    public static CellSpan Empty => default;

    /// <summary>Whether the cell has no source (an empty or ragged-padding cell).</summary>
    public bool IsEmpty => Length <= 0;
}

/// <summary>
/// One cell's content slice on one visual row (the wrapped-cell → visual-row unit, M3 risk a):
/// <see cref="SrcStart"/>/<see cref="SrcLength"/> are the block-relative source of the fragment's text
/// (so a WP9 caret can land in it), and <see cref="Width"/> is its display width in cells.
/// </summary>
/// <param name="SrcStart">Block-relative UTF-16 offset of the fragment's text.</param>
/// <param name="SrcLength">Length of the fragment's text in UTF-16 code units (0 for an empty/absent fragment).</param>
/// <param name="Width">The fragment's display width in cells (whole-cell <see cref="GraphemeWidth"/> measure).</param>
public readonly record struct CellFragment(int SrcStart, int SrcLength, int Width)
{
    /// <summary>An empty fragment — a column with no content on this visual row.</summary>
    public static CellFragment Empty => default;

    /// <summary>Whether the fragment draws nothing.</summary>
    public bool IsEmpty => SrcLength <= 0;
}

/// <summary>One visual row of a logical row: exactly one <see cref="CellFragment"/> per column (absent columns are <see cref="CellFragment.Empty"/>).</summary>
public sealed class TableVisualRow
{
    private readonly CellFragment[] _cells;

    internal TableVisualRow(CellFragment[] cells) => _cells = cells;

    /// <summary>The number of columns (one fragment each).</summary>
    public int ColumnCount => _cells.Length;

    /// <summary>The fragment for <paramref name="column"/> on this visual row.</summary>
    public CellFragment Cell(int column) => _cells[column];

    /// <summary>The per-column fragments in column order.</summary>
    public IReadOnlyList<CellFragment> Cells => _cells;
}

/// <summary>
/// A logical row's full visual layout (<see cref="TableModel.LayoutRow"/>): its ordered
/// <see cref="VisualRows"/> (≥ 1; a wrapped cell adds rows) plus the row's identity/kind — everything
/// WP2's per-row presenter needs, with the wrapped-cell mapping already decided.
/// </summary>
public sealed class TableRowLayout
{
    private readonly TableVisualRow[] _visualRows;

    internal TableRowLayout(int logicalRow, bool isHeader, int sourceLine, TableVisualRow[] visualRows)
    {
        LogicalRow = logicalRow;
        IsHeader = isHeader;
        SourceLine = sourceLine;
        _visualRows = visualRows;
    }

    /// <summary>The logical row index (header is 0).</summary>
    public int LogicalRow { get; }

    /// <summary>Whether this is the header row (bold + fill in WP2).</summary>
    public bool IsHeader { get; }

    /// <summary>The block-relative source line the row renders.</summary>
    public int SourceLine { get; }

    /// <summary>The number of visual rows the logical row occupies (≥ 1).</summary>
    public int VisualRowCount => _visualRows.Length;

    /// <summary>The ordered visual rows, top to bottom.</summary>
    public IReadOnlyList<TableVisualRow> VisualRows => _visualRows;
}
