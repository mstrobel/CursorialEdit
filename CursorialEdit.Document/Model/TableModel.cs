using Cursorial.Rendering.Text;
using Cursorial.Text;

using Markdig.Syntax;

using MdInline = Markdig.Syntax.Inlines.Inline;
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
/// <b>Widths are measured in cells (§5.1 [CRITICAL]) and are now viewport-aware (Decision 11, revised
/// 2026-07-05).</b> Per column the model caches <c>(WidthCells, MaxContentWidth, CountAtMax)</c>:
/// <see cref="MaxContentWidth"/> is the maximum <see cref="GraphemeWidth.StringWidth"/> over the column's
/// trimmed cell contents (header and body); <see cref="NaturalWidth"/> is that floored at
/// <see cref="MinWidth"/> (no cap) — the width the column <i>wants</i>; and <see cref="CountAtMax"/> is how
/// many cells sit at the maximum (the O(1) shrink-detection cache). The final rendered widths are no longer
/// a hard content clamp: <see cref="ResolveColumnWidths"/> takes the <b>available viewport budget</b> (known
/// at MEASURE time, threaded in by <c>TablePresenter</c>) and does browser-like auto-layout — if the natural
/// table fits, every column grows to its content (may exceed the old 40 cap); if it overflows, the widest
/// column(s) shrink (never below <see cref="MinWidth"/>) and their content word-wraps to fit. The legacy
/// <see cref="ColumnWidth"/> — <see cref="MaxContentWidth"/> clamped to <c>[3, 40]</c> — survives only as a
/// viewport-unaware <b>fallback</b> (pre-measure / off-band caret map), never the primary constraint.
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

    // Per-cell formatted (marks-hidden) projections (Decision 9 — per-cell reveal), computed once at Build (the
    // table is realized, so every cell is about to render). A plain cell's format is cheap (no display string);
    // only a cell carrying an inline construct projects. The per-cell inline runs feeding these are NOT retained
    // (#6) — the test-only CellInlineRuns accessor re-derives them from _table on demand.
    private readonly CellFormat[][] _formats;

    // The backing Markdig table + block origin, retained so the test-only CellInlineRuns can re-project a cell's
    // inline AST on demand (the table is already alive via the block for this model's parse generation, so this
    // aliases an existing object — it is not a second per-cell projection kept for the document's lifetime).
    private readonly MdTable? _table;
    private readonly int _origin;

    private TableModel(string source, Column[] columns, Row[] rows, CellFormat[][] formats, MdTable? table, int origin)
    {
        _source = source;
        _columns = columns;
        _rows = rows;
        _formats = formats;
        _table = table;
        _origin = origin;
    }

    /// <summary>The number of logical rows (header + body; the delimiter row is not a row).</summary>
    public int RowCount => _rows.Length;

    /// <summary>The number of columns (the widest of the delimiter row and any body row — a ragged table's excess column exists here).</summary>
    public int ColumnCount => _columns.Length;

    /// <summary>The GFM alignment of <paramref name="column"/> (from the delimiter row); <see cref="ColumnAlignment.None"/> for an unaligned or ragged-excess column.</summary>
    public ColumnAlignment Alignment(int column) => _columns[column].Alignment;

    /// <summary>
    /// The <b>fallback</b> (viewport-unaware) width of <paramref name="column"/> in cells:
    /// <see cref="MaxContentWidth"/> clamped to <c>[3, 40]</c> (the old §5.1 cap). Used only where no viewport
    /// budget is available (a pre-measure presenter, an off-band caret map); the rendered widths come from
    /// <see cref="ResolveColumnWidths"/>.
    /// </summary>
    public int ColumnWidth(int column) => _columns[column].WidthCells;

    /// <summary>
    /// The width <paramref name="column"/> <i>wants</i>: <see cref="MaxContentWidth"/> floored at
    /// <see cref="MinWidth"/> with <b>no</b> maximum cap — the natural content width the viewport-aware
    /// auto-layout (<see cref="ResolveColumnWidths"/>) grows the column to when the table fits.
    /// </summary>
    public int NaturalWidth(int column) => Math.Max(MinWidth, _columns[column].MaxContentWidth);

    /// <summary>The maximum trimmed-content cell width in <paramref name="column"/>, measured in cells (pre-clamp) — the reflow input.</summary>
    public int MaxContentWidth(int column) => _columns[column].MaxContentWidth;

    /// <summary>How many of <paramref name="column"/>'s cells sit at <see cref="MaxContentWidth"/> — the O(1) shrink-detection cache (Decision 11).</summary>
    public int CountAtMax(int column) => _columns[column].CountAtMax;

    /// <summary>Whether logical <paramref name="row"/> is the (always-present) header row — GFM row 0.</summary>
    public bool IsHeaderRow(int row) => _rows[row].IsHeader;

    /// <summary>
    /// Whether any logical row renders on block-relative source <paramref name="line"/> — the guard that
    /// keeps cell routing off the delimiter line and, above all, off an <b>absorbed trailing blank line</b>
    /// (a table block swallows the blank line after it): pressing Enter below a trailing table lands the
    /// caret on such a blank line, which must edit as an ordinary paragraph, not the last cell (M3.WP4 bug 5).
    /// </summary>
    public bool HasRowOnLine(int line)
    {
        foreach (var row in _rows)
        {
            if (row.SourceLine == line)
                return true;
        }

        return false;
    }

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

    /// <summary>Whether the cell at (<paramref name="row"/>, <paramref name="column"/>) has no content (a GFM blank or ragged-padding cell) — it still carries a real inter-pipe insertion anchor (M3.WP4 point 0).</summary>
    public bool IsCellEmpty(int row, int column) => _rows[row].Cells[column].IsEmpty;

    /// <summary>
    /// The inline runs projected from the cell at (<paramref name="row"/>, <paramref name="column"/>)'s
    /// Markdig inline AST — the per-cell mirror of <see cref="Block.InlineRuns"/> (Decision 5, Decision 9),
    /// the same <see cref="InlineProjection"/> the block path uses. Offsets are <b>cell-relative</b> (measured
    /// from <see cref="CellContentRange"/>'s Start), so they index the cell's trimmed content directly. Empty
    /// for a cell with no inline content (a blank/ragged cell). Feeds the formatted (marks-hidden) rendering.
    /// </summary>
    public IReadOnlyList<InlineRun> CellInlineRuns(int row, int column)
    {
        // #6: not retained. The projected runs are consumed at Build time (to build each CellFormat) and thrown
        // away; only this test-only accessor still needs them, so it re-derives on demand from the retained Markdig
        // table — identical to what Build projected — rather than the model carrying a per-cell run array for life.
        var node = CellNodeAt(row, column);
        if (node is null)
            return [];

        var (contentStart, content) = Content(_rows[row].Cells[column]);
        return ProjectCellRuns(node, _origin, contentStart, content.Length);
    }

    /// <summary>
    /// The block-relative source range <c>[Start, End)</c> of the trimmed <b>content</b> of the cell at
    /// (<paramref name="row"/>, <paramref name="column"/>) — the caret's home range within the cell
    /// (M3.WP4). For an empty cell this is the zero-width inter-pipe insertion anchor <c>(anchor, anchor)</c>.
    /// </summary>
    public (int Start, int End) CellContentRange(int row, int column)
    {
        var (start, text) = Content(_rows[row].Cells[column]);
        return (start, start + text.Length);
    }

    /// <summary>The block-relative offset where a caret entering the cell at (<paramref name="row"/>, <paramref name="column"/>) lands — its content start (M3.WP4 Tab/arrow entry).</summary>
    public int CellEntryOffset(int row, int column) => CellContentRange(row, column).Start;

    /// <summary>
    /// The logical (row, column) whose cell owns the block-relative <paramref name="blockRelOffset"/> — the
    /// cell whose source begins at or before the offset (its trailing padding rounds back into it), or
    /// <see langword="null"/> when the table has no cells. The Decision-4 cell-focus derivation: cell focus
    /// is not stored, it is re-derived from the caret's source offset against the current model.
    /// </summary>
    public (int Row, int Column)? CellOfOffset(int blockRelOffset)
    {
        (int Row, int Column)? best = null;
        int bestStart = int.MinValue;
        for (var r = 0; r < _rows.Length; r++)
        {
            var cells = _rows[r].Cells;
            for (var c = 0; c < cells.Length; c++)
            {
                // The cell owns from its source start (empty anchor, or the span start incl. leading padding).
                // Read the span start directly — no trimmed-string allocation per cell per keystroke (cleanup 9).
                int start = cells[c].Start;
                if (start <= blockRelOffset && start >= bestStart)
                {
                    bestStart = start;
                    best = (r, c);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The whole-cell rectangle a document selection whose two ends fall at block-relative offsets
    /// <paramref name="offsetA"/> and <paramref name="offsetB"/> covers (M3.WP8, spec §5.4): the cells owning
    /// the two offsets (via <see cref="CellOfOffset"/>), normalized to rows <c>[min..max]</c> × columns
    /// <c>[min..max]</c>. Returns <see langword="null"/> when both offsets fall in the <b>same</b> cell (an
    /// ordinary in-cell text selection, unchanged from WP5) or when the table has no cells. The two offsets are
    /// the selection's anchor and active-caret offsets — order-independent, since the rectangle is symmetric.
    /// This is the one place a block-like selection exists, scoped to a single table; the underlying selection
    /// stays an ordinary source range (the model of truth) and is only <i>interpreted</i> as a cell-rect here.
    /// </summary>
    public CellRect? CellRectOfRange(int offsetA, int offsetB)
    {
        if (CellOfOffset(offsetA) is not { } a || CellOfOffset(offsetB) is not { } b)
            return null;
        if (a == b)
            return null; // both ends in one cell → an ordinary in-cell text selection (WP5), not a cell-rect

        return new CellRect(
            Math.Min(a.Row, b.Row), Math.Min(a.Column, b.Column),
            Math.Max(a.Row, b.Row), Math.Max(a.Column, b.Column));
    }

    /// <summary>The block-relative offset at the end of logical <paramref name="row"/>'s source line text (terminator excluded) — where a new row's break is spliced (M3.WP4 Tab-appends-row).</summary>
    public int RowTextEndOffset(int row)
    {
        // Any known in-row offset (a cell start, real or empty anchor) locates the row's line; walk to its end.
        int probe = 0;
        foreach (var cell in _rows[row].Cells)
        {
            if (cell.Start > 0 || !cell.IsEmpty)
            {
                probe = cell.Start;
                break;
            }
        }

        int end = Math.Clamp(probe, 0, _source.Length);
        while (end < _source.Length && _source[end] != '\n' && _source[end] != '\r')
            end++;

        return end;
    }

    /// <summary>
    /// The viewport-aware column widths for a table drawn into <paramref name="contentBudget"/> cells of
    /// content space (the viewport width minus the border/padding chrome — the presenter subtracts the chrome
    /// and passes the remainder here). Browser-like table auto-layout (Decision 11, revised):
    /// <list type="bullet">
    /// <item>If the natural table (Σ <see cref="NaturalWidth"/>) <b>fits</b> the budget, every column keeps its
    /// natural width — columns grow to content, nothing wraps (may exceed the old 40 cap when the viewport has
    /// room). This is the common case that fixes "empty viewport, still wrapping".</item>
    /// <item>If it <b>overflows</b>, the widest column(s) shrink toward a common ceiling (widest-first for the
    /// remainder) until the row fits, but never below <see cref="MinWidth"/>; the shrunk cells then word-wrap
    /// (<see cref="WrapCell"/>).</item>
    /// <item>If the budget is below even <c>ColumnCount × MinWidth</c>, every column sits at <see cref="MinWidth"/>
    /// and the table overflows horizontally (the WP6 column-window will scroll it inside the presenter).</item>
    /// </list>
    /// </summary>
    /// <param name="contentBudget">The cells available for column content (viewport width minus grid chrome).</param>
    /// <returns>One resolved width (in cells) per column, in column order.</returns>
    public int[] ResolveColumnWidths(int contentBudget)
    {
        int n = _columns.Length;
        var natural = new int[n];
        long naturalSum = 0;
        for (var c = 0; c < n; c++)
        {
            natural[c] = Math.Max(MinWidth, _columns[c].MaxContentWidth);
            naturalSum += natural[c];
        }

        if (n == 0 || naturalSum <= contentBudget)
            return natural; // the natural table fits — columns grow to content, no wrap

        long minSum = (long)n * MinWidth;
        if (contentBudget <= minSum)
        {
            var floored = new int[n];
            Array.Fill(floored, MinWidth);
            return floored; // cannot fit even at the floor — accept horizontal overflow (WP6 column-window)
        }

        // Water-fill: the largest common ceiling `cap` with Σ clamp(natural, [Min, cap]) ≤ budget — this lowers
        // only the columns wider than `cap` (the widest), matching HTML auto-layout's "shrink the widest".
        int maxNatural = 0;
        foreach (int w in natural)
            maxNatural = Math.Max(maxNatural, w);

        // The MinWidth floor holds without an inner clamp: every natural[c] ≥ MinWidth (line above) and the
        // search stays cap ≥ mid ≥ lo ≥ MinWidth, so Min(natural, cap) can never dip below MinWidth.
        int lo = MinWidth, hi = maxNatural, cap = MinWidth;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            long sum = 0;
            foreach (int w in natural)
                sum += Math.Min(w, mid);
            if (sum <= contentBudget)
            {
                cap = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var widths = new int[n];
        long used = 0;
        for (var c = 0; c < n; c++)
        {
            widths[c] = Math.Min(natural[c], cap); // ≥ MinWidth by the invariant above
            used += widths[c];
        }

        // Hand the integer remainder (budget − Σcap) to the still-below-natural columns, widest natural first,
        // so the fit is exact and the extra cells land on the columns that most want them.
        int leftover = (int)(contentBudget - used);
        if (leftover > 0)
        {
            var order = new List<int>(n);
            for (var c = 0; c < n; c++)
                if (widths[c] < natural[c])
                    order.Add(c);
            order.Sort((a, b) => natural[b] != natural[a] ? natural[b] - natural[a] : a - b);
            foreach (int c in order)
            {
                if (leftover == 0)
                    break;
                widths[c]++;
                leftover--;
            }
        }

        return widths;
    }

    /// <summary>
    /// The cell-layout pass for logical <paramref name="row"/> against the resolved
    /// <paramref name="columnWidths"/> under <paramref name="overflow"/> (M3 risk a — <b>the single owner</b>
    /// of wrapped-cell → visual-row mapping): the ordered list of visual rows the row occupies, each carrying
    /// one <see cref="CellFragment"/> per column. A cell wider than its column word-wraps (<see cref="WrapCell"/>)
    /// to several fragments, so the row's visual height is the maximum fragment count over its cells (≥ 1).
    /// <paramref name="columnWidths"/> are the viewport-aware widths from <see cref="ResolveColumnWidths"/>
    /// (the presenter's shared grid metrics); the parameterless overload uses the <see cref="ColumnWidth"/>
    /// fallback. Implements <see cref="TableOverflow.Wrap"/> only.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="columnWidths"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public TableRowLayout LayoutRow(int row, IReadOnlyList<int> columnWidths, TableOverflow overflow = TableOverflow.Wrap, int activeColumn = -1)
    {
        ArgumentNullException.ThrowIfNull(columnWidths);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);

        var r = _rows[row];
        int columns = _columns.Length;

        // Per-cell reveal (Decision 9): the ACTIVE cell — the one the caret is in — renders its RAW markdown
        // (marks shown), exactly what every cell showed before formatting; every other cell renders its FORMATTED
        // display (marks hidden). A plain cell (no marks) is identical either way, so it always takes the raw path
        // (byte-identical — no projection). Hiding marks makes an inactive cell NARROWER, so the active cell (raw,
        // wider) may wrap to more rows — the row reflows on reveal.
        bool RawCell(int c) => c == activeColumn || _formats[row][c].IsPlain;

        // Truncate (§5.6): each cell renders on ONE visual row clipped to its column with a trailing … — so a
        // logical row is exactly one visual row (no wrap-growth). The single owner of cell → visual-row mapping
        // stays here (risk a): this is the Truncate branch, Wrap below is unchanged.
        if (overflow == TableOverflow.Truncate)
        {
            var cells = new CellFragment[columns];
            for (var c = 0; c < columns; c++)
            {
                int w = c < columnWidths.Count ? columnWidths[c] : _columns[c].WidthCells;
                cells[c] = RawCell(c) ? TruncateCell(r.Cells[c], w) : TruncateCellFormatted(_formats[row][c], w);
            }

            return new TableRowLayout(row, r.IsHeader, r.SourceLine, [new TableVisualRow(cells)]);
        }

        // Wrap: per column, the cell's content word-wrapped to its (viewport-resolved) width. The row's visual
        // height is the max fragment count over its cells — the place wrapped-cell → visual-row mapping is decided.
        var perColumn = new CellFragment[columns][];
        int visualRows = 1;
        for (var c = 0; c < columns; c++)
        {
            int width = c < columnWidths.Count ? columnWidths[c] : _columns[c].WidthCells;
            perColumn[c] = RawCell(c) ? WrapCell(r.Cells[c], width) : WrapCellFormatted(_formats[row][c], width);
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
    /// The cell-layout pass using the viewport-unaware <see cref="ColumnWidth"/> fallback widths — for callers
    /// with no viewport budget (a direct model query / test). The presenter path uses the
    /// <see cref="LayoutRow(int, IReadOnlyList{int}, TableOverflow)"/> overload with the resolved widths.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public TableRowLayout LayoutRow(int row, TableOverflow overflow = TableOverflow.Wrap, int activeColumn = -1)
    {
        var widths = new int[_columns.Length];
        for (var c = 0; c < _columns.Length; c++)
            widths[c] = _columns[c].WidthCells;
        return LayoutRow(row, widths, overflow, activeColumn);
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
        var rowNodes = new List<MdTableCell?[]>(); // the per-cell Markdig node, for the lazy inline projection
        var headerFlags = new List<bool>();
        var rowCellCounts = new List<int>(); // Markdig's own cell count per row (before ragged padding)
        int columnCount = table.ColumnDefinitions.Count;

        foreach (var rowObj in table)
        {
            if (rowObj is not MdTableRow mdRow)
                continue;

            var cells = new List<CellSpan>();
            var nodes = new List<MdTableCell?>();
            foreach (var cellObj in mdRow)
            {
                if (cellObj is MdTableCell mdCell)
                {
                    cells.Add(ToCellSpan(mdCell.Span, origin, sourceLength));
                    nodes.Add(mdCell);
                }
            }

            columnCount = Math.Max(columnCount, cells.Count);
            rowSpans.Add([.. cells]);
            rowNodes.Add([.. nodes]);
            headerFlags.Add(mdRow.IsHeader);
            rowCellCounts.Add(cells.Count);
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

            if (rowNodes[i].Length < columnCount)
            {
                var padded = new MdTableCell?[columnCount];
                Array.Copy(rowNodes[i], padded, rowNodes[i].Length);
                rowNodes[i] = padded; // trailing ragged cells have no node → no inline projection
            }
        }

        // Row source lines, source-accurate (bug 4): a row with any real cell span is located directly from
        // that span (robust to a leading blank/whitespace line in the block source — a hardcoded header(0)/
        // delimiter(1)/body(i+1) index would resolve the wrong physical line there). An all-empty row (whose
        // Markdig span is empty — e.g. a just-appended `|   |   |`) is interpolated from its neighbours over
        // the GFM line structure: body rows are physically consecutive, and the delimiter sits only between
        // the header row 0 and the first body row 1 (so the row-0→row-1 gap is 2 lines, every other gap 1).
        var lineStarts = LineStartOffsets(blockSource);
        var rowLines = new int[rowSpans.Count];
        for (var i = 0; i < rowSpans.Count; i++)
        {
            rowLines[i] = -1;
            foreach (var span in rowSpans[i])
            {
                if (!span.IsEmpty)
                {
                    rowLines[i] = SourceLineAt(blockSource, span.Start);
                    break;
                }
            }
        }

        // An ALL-EMPTY table (every cell empty, e.g. after clearing all cells or a fresh multi-row insert) has
        // no non-empty span anywhere to seed the interpolation, leaving every row at -1. The header always
        // occupies the block's first physical line, so anchor row 0 there; the forward pass then places the body
        // rows (row 1 after the delimiter, the rest consecutively). Without this every RowSourceLine is -1 and
        // any line-of-row computation (AbsLineOfRow → GetLine) throws. (WP11 fuzz: DeleteRow/MoveRow all-empty.)
        if (rowLines.Length > 0 && rowLines[0] < 0)
            rowLines[0] = 0;

        for (var i = 1; i < rowLines.Length; i++)
        {
            if (rowLines[i] < 0 && rowLines[i - 1] >= 0)
                rowLines[i] = rowLines[i - 1] + (i == 1 ? 2 : 1); // +2 skips the delimiter after the header
        }

        for (var i = rowLines.Length - 2; i >= 0; i--)
        {
            if (rowLines[i] < 0 && rowLines[i + 1] >= 0)
                rowLines[i] = Math.Max(0, rowLines[i + 1] - (i + 1 == 1 ? 2 : 1));
        }

        if (rowLines.Length > 0 && rowLines[0] < 0)
            rowLines[0] = 0; // an all-empty table with no locatable row — degenerate; header at the origin

        // Spike review #6 / M3.WP4 point 0: give every empty cell a real inter-pipe *insertion anchor*
        // (Start), not the block origin. An empty cell's <see cref="CellSpan"/> keeps IsEmpty (Length 0),
        // but its Start now locates the cell so a caret lands in it and an edit splices there — derived
        // from Markdig's surrounding-cell spans (never a hand pipe-scanner over the whole row: the gap
        // between two Markdig cell spans is whitespace + pipes only, so scanning IT for '|' is escape-safe).
        for (var i = 0; i < rowSpans.Count; i++)
        {
            int lineStart = lineStarts[Math.Clamp(rowLines[i], 0, lineStarts.Length - 1)];
            AssignEmptyCellAnchors(blockSource, rowSpans[i], rowCellCounts[i], lineStart);
        }

        // Per-cell inline projection + formatted display (Decision 9): cell-relative inline runs projected the
        // same way Block.RealizeInlineRuns does, then the marks-hidden display each inactive cell renders. A
        // plain cell (no inline construct) projects cheaply and keeps its raw width — no display string is built.
        // The projected runs are consumed by CellFormat.Build and then dropped (#6): only the formatted result is
        // retained; the test-only CellInlineRuns re-derives the runs from the Markdig table when asked.
        var formats = new CellFormat[rowSpans.Count][];
        for (var i = 0; i < rowSpans.Count; i++)
        {
            formats[i] = new CellFormat[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                var (contentStart, content) = ContentOf(blockSource, rowSpans[i][c]);
                var runs = ProjectCellRuns(rowNodes[i][c], origin, contentStart, content.Length);
                formats[i][c] = CellFormat.Build(content, contentStart, runs);
            }
        }

        // Column alignment from the delimiter row; a ragged-excess column has none. Widths are measured over the
        // formatted DISPLAY width (marks hidden → narrower), so an inactive `**bold**` cell is 4 cells, not 8.
        var columns = new Column[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var align = c < table.ColumnDefinitions.Count ? MapAlignment(table.ColumnDefinitions[c].Alignment) : ColumnAlignment.None;
            columns[c] = MeasureColumn(formats, c, align);
        }

        var rows = new Row[rowSpans.Count];
        for (var i = 0; i < rowSpans.Count; i++)
            rows[i] = new Row(rowSpans[i], headerFlags[i], rowLines[i]);

        return new TableModel(blockSource, columns, rows, formats, table, origin);
    }

    /// <summary>
    /// The Markdig cell node backing logical (<paramref name="row"/>, <paramref name="column"/>), or
    /// <see langword="null"/> for a ragged-padding column (beyond the row's real cells). Walks the retained
    /// <see cref="_table"/> in the same order <see cref="Build"/> collected the rows/cells, so the indexing
    /// matches <c>_rows</c> exactly — the on-demand basis for <see cref="CellInlineRuns"/> (#6).
    /// </summary>
    private MdTableCell? CellNodeAt(int row, int column)
    {
        if (_table is null || row < 0 || column < 0)
            return null;

        int r = 0;
        foreach (var rowObj in _table)
        {
            if (rowObj is not MdTableRow mdRow)
                continue;
            if (r == row)
            {
                int c = 0;
                foreach (var cellObj in mdRow)
                {
                    if (cellObj is not MdTableCell mdCell)
                        continue;
                    if (c == column)
                        return mdCell;
                    c++;
                }

                return null; // a ragged/padding column — no backing node (matches Build's null padding)
            }

            r++;
        }

        return null;
    }

    /// <summary>
    /// Projects one cell's Markdig inline AST into <b>cell-relative</b> <see cref="InlineRun"/>s (offsets from
    /// <paramref name="contentStart"/>), reusing the shared <see cref="InlineProjection"/>. The projection origin
    /// is the absolute content start (<paramref name="origin"/> + <paramref name="contentStart"/>) and the bound
    /// is <paramref name="contentLength"/>, so a run outside the cell's own trimmed content is dropped — the same
    /// bound discipline <see cref="Block.RealizeInlineRuns"/> applies.
    /// </summary>
    private static IReadOnlyList<InlineRun> ProjectCellRuns(MdTableCell? node, int origin, int contentStart, int contentLength)
    {
        if (node is null || contentLength == 0)
            return [];

        return InlineProjection.Project(node.Descendants().OfType<MdInline>(), origin + contentStart, contentLength);
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

    /// <summary>The 0-based source line index containing block-relative <paramref name="offset"/> (a <c>'\n'</c> count) — the source-accurate row-line locator (bug 4).</summary>
    private static int SourceLineAt(string source, int offset)
    {
        int line = 0;
        int end = Math.Clamp(offset, 0, source.Length);
        for (var i = 0; i < end; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    /// <summary>The block-relative offset each source line begins at (<c>0</c>, then every position after a <c>'\n'</c>).</summary>
    private static int[] LineStartOffsets(string source)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
                starts.Add(i + 1);
        }

        return [.. starts];
    }

    /// <summary>
    /// Replaces every empty cell's <see cref="CellSpan.Empty"/> (<c>Start = 0</c>) with a zero-length span
    /// whose <see cref="CellSpan.Start"/> is the cell's real inter-pipe insertion anchor (M3.WP4 point 0).
    /// The anchor is bracketed by the row's surrounding <b>non-empty</b> Markdig cell spans (or the line
    /// bounds), and — for a run of consecutive empty cells — the pipes are read from the bracketed gap,
    /// which Markdig has already proven to be whitespace + <c>|</c> only (no escaped/backtick pipes hide in
    /// an empty cell), so this never hand-parses cell boundaries out of arbitrary source (risk d).
    /// </summary>
    private static void AssignEmptyCellAnchors(string source, CellSpan[] cells, int markdigCellCount, int lineStart)
    {
        lineStart = Math.Clamp(lineStart, 0, source.Length);
        int lineEnd = lineStart;
        while (lineEnd < source.Length && source[lineEnd] != '\n' && source[lineEnd] != '\r')
            lineEnd++;

        for (var c = 0; c < cells.Length; c++)
        {
            if (!cells[c].IsEmpty)
                continue;

            // A ragged-padding cell (beyond Markdig's own cells) has no source region at all — anchor it at
            // the row's text end so a caret at least lands on the row, not the block origin.
            if (c >= markdigCellCount)
            {
                cells[c] = new CellSpan(lineEnd, 0);
                continue;
            }

            // Bracket this cell's run of consecutive empty in-source cells by the nearest real spans.
            int runStart = c;
            while (runStart > 0 && cells[runStart - 1].IsEmpty && runStart - 1 < markdigCellCount)
                runStart--;

            int leftBound = runStart > 0 ? cells[runStart - 1].Start + cells[runStart - 1].Length : lineStart;

            int runEnd = c;
            while (runEnd + 1 < markdigCellCount && cells[runEnd + 1].IsEmpty)
                runEnd++;

            int rightBound = runEnd + 1 < markdigCellCount ? cells[runEnd + 1].Start : lineEnd;

            cells[c] = new CellSpan(AnchorInGap(source, leftBound, rightBound, c - runStart), 0);
        }
    }

    /// <summary>
    /// The insertion anchor for the <paramref name="runIndex"/>-th empty cell in the whitespace+pipe gap
    /// <c>[left, right)</c>: the offset just inside the pipe that opens that cell's inter-pipe region.
    /// </summary>
    private static int AnchorInGap(string source, int left, int right, int runIndex)
    {
        left = Math.Clamp(left, 0, source.Length);
        right = Math.Clamp(right, left, source.Length);

        // The pipes partitioning the gap (safe to scan: an empty cell holds only whitespace, never an
        // escaped or backtick-guarded pipe). Cell j sits after pipe j.
        var pipes = new List<int>();
        for (var i = left; i < right; i++)
        {
            if (source[i] == '|')
                pipes.Add(i);
        }

        if (runIndex < pipes.Count)
            return Math.Min(pipes[runIndex] + 1, right); // just inside the opening pipe of this cell

        // No pipe found (a leading/trailing region without a bar, e.g. a pipeless GFM table): anchor at the
        // gap start, clamped past a single leading pad space so a bare insert lands inside the region.
        return Math.Clamp(left, 0, right);
    }

    // ───────────────────────────── width cache ─────────────────────────────

    private static Column MeasureColumn(CellFormat[][] formats, int column, ColumnAlignment align)
    {
        // Single pass max + count-at-max over the FORMATTED display width (marks hidden), so a column of
        // `**bold**` cells sizes to `bold` (4), not the raw 8. When a new maximum appears the count resets to 1;
        // equals to the running max increment it — so the final count is the tally at the final maximum (Decision 11).
        int max = 0;
        int countAtMax = 0;
        foreach (var row in formats)
        {
            int width = row[column].DisplayWidth;
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
    /// Word-wraps one cell's trimmed content to <paramref name="width"/> cells (M3 risk a), reusing the
    /// <b>same framework word-wrap the prose blocks use</b> — <see cref="TextLayout.Build"/> under
    /// <see cref="WrapMode.WordWrap"/>: breaks land at word boundaries; a single word longer than the column
    /// hard-breaks at the overflowing grapheme cluster (the char-level fallback, only for an over-long word);
    /// and every break is a grapheme-cluster boundary, so a wide cluster is never halved. The segments tile
    /// the content exactly, so the fragments' source slices reconstruct the whole cell (the caret round-trips).
    /// An empty cell yields a single empty fragment so it still occupies one visual row.
    /// </summary>
    private CellFragment[] WrapCell(CellSpan span, int width)
    {
        var (srcStart, text) = Content(span);
        return WrapContent(text, width, srcStart, format: null);
    }

    /// <summary>
    /// Clips one cell's trimmed content to <paramref name="width"/> cells for <see cref="TableOverflow.Truncate"/>
    /// (§5.6): the single visual-row fragment the cell occupies. A cell that fits is the whole content unchanged;
    /// an over-wide cell keeps the longest grapheme-cluster <b>prefix</b> fitting <c>width − 1</c> cells (never
    /// splitting a wide cluster) and sets <see cref="CellFragment.Ellipsis"/> so the presenter appends the one-cell
    /// <c>…</c> — total drawn width ≤ <paramref name="width"/>. The prefix source range still tiles from the cell
    /// start, so a caret in the visible prefix lands correctly.
    /// </summary>
    private CellFragment TruncateCell(CellSpan span, int width)
    {
        var (srcStart, text) = Content(span);
        return TruncateContent(text, width, srcStart, format: null);
    }

    // ───────────────────────────── formatted (marks-hidden) content + wrapping ─────────────────────────────

    /// <summary>
    /// Word-wraps a cell's <b>formatted</b> (marks-hidden) display to <paramref name="width"/> cells (Decision 9),
    /// the marks-hidden analogue of <see cref="WrapCell"/>: the wrap runs over the <see cref="CellFormat.Display"/>
    /// (narrower than the raw content), and each visual-row fragment carries its <see cref="CellStyledRun"/>s so the
    /// presenter draws the styled content (bold/italic/code/link) without re-deriving the inline AST, and the caret
    /// map builds its per-run stops from the same fragments. Reuses the same framework word-wrap the prose blocks use.
    /// </summary>
    private CellFragment[] WrapCellFormatted(CellFormat format, int width) =>
        WrapContent(format.Display, width, format.ContentStart, format);

    /// <summary>
    /// Clips a cell's <b>formatted</b> (marks-hidden) display to <paramref name="width"/> cells for
    /// <see cref="TableOverflow.Truncate"/> (§5.6) — the marks-hidden analogue of <see cref="TruncateCell"/>:
    /// a fitting cell renders whole; an over-wide one keeps the widest whole-cluster display prefix fitting
    /// <c>width − 1</c> and sets <see cref="CellFragment.Ellipsis"/>. The fragment carries its styled runs.
    /// </summary>
    private CellFragment TruncateCellFormatted(CellFormat format, int width) =>
        TruncateContent(format.Display, width, format.ContentStart, format);

    // ───────────────────────────── shared wrap/truncate core (raw ≡ formatted, #5) ─────────────────────────────

    /// <summary>
    /// The single owner of cell wrapping (M3 risk a), shared by the raw (<see cref="WrapCell"/>) and formatted
    /// (<see cref="WrapCellFormatted"/>) paths so their caret-mappings can't drift. <paramref name="text"/> is the
    /// string that renders — the raw trimmed content, or the marks-hidden <see cref="CellFormat.Display"/> — and
    /// <paramref name="format"/> selects the mode: <see langword="null"/> (raw) maps a text index 1:1 onto source
    /// (<c>contentSrcStart + index</c>) and emits plain fragments; a non-null format maps through
    /// <see cref="CellFormat.SourceOf"/> (so hidden marks collapse onto the adjacent content) and attaches each
    /// fragment's <see cref="CellStyledRun"/>s. A cell renders TRIMMED content per visual row (unlike prose soft-wrap,
    /// which keeps a trailing space): each segment trims trailing whitespace from its RENDERED width (right/center
    /// wrapped lines stay flush; a word that exactly fills the column doesn't spill the following space as a blank
    /// row), while its SOURCE range spans the full segment so the fragments tile the cell and the caret round-trips.
    /// An empty cell yields one empty fragment so it still occupies one visual row.
    /// </summary>
    private CellFragment[] WrapContent(string text, int width, int contentSrcStart, CellFormat? format)
    {
        if (text.Length == 0)
            return [new CellFragment(contentSrcStart, 0, 0)];

        int SourceAt(int index) => format is null ? contentSrcStart + index : format.SourceOf(index);

        int budget = Math.Max(1, width);
        var wrapped = TextLayout.Build(text, budget, WrapMode.WordWrap);
        var textSpan = text.AsSpan();

        var fragments = new List<CellFragment>(wrapped.LineCount);
        for (var v = 0; v < wrapped.LineCount; v++)
        {
            int start = wrapped.LineContentStart(v);
            int end = wrapped.LineContentEnd(v);

            int renderEnd = end;
            while (renderEnd > start && (textSpan[renderEnd - 1] == ' ' || textSpan[renderEnd - 1] == '\t'))
                renderEnd--;

            if (renderEnd == start && fragments.Count > 0)
            {
                // A whitespace-only segment (the lone space between two exactly-fitting words): don't emit a
                // blank visual row — extend the previous fragment's source to absorb it, keeping tiling intact.
                var prev = fragments[^1];
                fragments[^1] = prev with { SrcLength = SourceAt(end) - prev.SrcStart };
                continue;
            }

            // Render width of the trimmed segment [start, renderEnd), preserving each mode's original computation
            // (they diverge only on a trailing TAB, which GraphemeWidth measures as 0 but the raw subtraction as 1):
            // raw subtracts the trimmed trailing chars from the wrapped line width; formatted measures the display
            // slice directly (its trailing marks already collapsed). Both are the drawn cell width.
            int renderWidth = format is null
                ? wrapped.LineWidth(v) - (end - renderEnd)
                : CellsBetween(text, start, renderEnd);
            var runs = format is null ? null : StyledRunsOf(format, start, renderEnd);
            int fragSrcStart = SourceAt(start);
            fragments.Add(new CellFragment(fragSrcStart, SourceAt(end) - fragSrcStart, renderWidth) { StyledRuns = runs });
        }

        // Unreachable defensive fallback: non-empty text yields LineCount ≥ 1 and the first segment always emits a
        // fragment (the absorb branch requires Count > 0), so fragments is non-empty here whenever text is non-empty.
        return fragments.Count > 0 ? [.. fragments] : [new CellFragment(contentSrcStart, 0, 0)];
    }

    /// <summary>
    /// The single owner of cell truncation (§5.6), shared by the raw (<see cref="TruncateCell"/>) and formatted
    /// (<see cref="TruncateCellFormatted"/>) paths. <paramref name="text"/> and <paramref name="format"/> select the
    /// mode exactly as <see cref="WrapContent"/> does. A fitting cell renders whole; an over-wide cell keeps the
    /// widest whole-cluster prefix fitting <c>width − 1</c> cells (never splitting a wide cluster) and flags
    /// <see cref="CellFragment.Ellipsis"/> for the presenter's trailing <c>…</c>. The prefix source range tiles from
    /// the cell start, so a caret in the visible prefix lands correctly.
    /// </summary>
    private CellFragment TruncateContent(string text, int width, int contentSrcStart, CellFormat? format)
    {
        if (text.Length == 0)
            return new CellFragment(contentSrcStart, 0, 0);

        int SourceAt(int index) => format is null ? contentSrcStart + index : format.SourceOf(index);
        IReadOnlyList<CellStyledRun>? RunsUpTo(int len) => format is null ? null : StyledRunsOf(format, 0, len);

        int fullWidth = format?.DisplayWidth ?? GraphemeWidth.StringWidth(text); // == StringWidth(text) either way
        if (fullWidth <= width)
        {
            int fitStart = SourceAt(0);
            return new CellFragment(fitStart, SourceAt(text.Length) - fitStart, fullWidth) { StyledRuns = RunsUpTo(text.Length) };
        }

        // Reserve one cell for the … and keep the widest whole-cluster prefix that still fits the remainder.
        int budget = Math.Max(0, width - EllipsisWidth);
        int used = 0, len = 0;
        var clusters = text.AsSpan().GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            int w = GraphemeWidth.StringWidth(clusters.Current);
            if (used + w > budget)
                break;
            used += w;
            len += clusters.Current.Length;
        }

        int start2 = SourceAt(0);
        return new CellFragment(start2, SourceAt(len) - start2, used) { StyledRuns = RunsUpTo(len), Ellipsis = true };
    }

    /// <summary>
    /// Splits the display range <c>[from, to)</c> into <see cref="CellStyledRun"/>s: a fresh run starts whenever
    /// the inline style changes OR a hidden mark was collapsed (source discontinuity), so within each run the
    /// display is 1:1 with a contiguous source slice — its source slice reproduces its display text, and the
    /// caret walks it like a plain cell.
    /// </summary>
    private static CellStyledRun[] StyledRunsOf(CellFormat format, int from, int to)
    {
        var runs = new List<CellStyledRun>();
        int cellBase = 0;
        int k = from;
        while (k < to)
        {
            var style = format.StyleAt(k);
            int srcStart = format.SourceOf(k);
            int j = k + 1;
            while (j < to && format.StyleAt(j) == style && format.ContiguousAfter(j - 1))
                j++;

            int cells = CellsBetween(format.Display, k, j);
            runs.Add(new CellStyledRun(cellBase, cells, srcStart, j - k, style));
            cellBase += cells;
            k = j;
        }

        return [.. runs];
    }

    private static int CellsBetween(string display, int from, int to) =>
        to <= from ? 0 : GraphemeWidth.StringWidth(display.AsSpan(from, to - from));

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

    /// <summary>The display width in cells of the truncation ellipsis <c>…</c> (U+2026, a single cell) — reserved at the column's trailing edge under <see cref="TableOverflow.Truncate"/>.</summary>
    public const int EllipsisWidth = 1;

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

/// <summary>
/// A rectangular block of whole table cells (M3.WP8, spec §5.4): the inclusive, normalized range of rows
/// <c>[<see cref="Row0"/>..<see cref="Row1"/>]</c> × columns <c>[<see cref="Col0"/>..<see cref="Col1"/>]</c>
/// (<c>Row0 ≤ Row1</c>, <c>Col0 ≤ Col1</c>) a multi-cell document selection is interpreted as — the one place
/// a block-like selection exists, scoped to a single table. Derived from a selection whose two ends fall in
/// <b>different</b> cells of the same table (<see cref="TableModel.CellRectOfRange"/>); a single-cell selection
/// stays an ordinary in-cell text selection and yields no rect.
/// </summary>
/// <param name="Row0">The top logical row (inclusive).</param>
/// <param name="Col0">The left column (inclusive).</param>
/// <param name="Row1">The bottom logical row (inclusive).</param>
/// <param name="Col1">The right column (inclusive).</param>
public readonly record struct CellRect(int Row0, int Col0, int Row1, int Col1)
{
    /// <summary>Whether cell (<paramref name="row"/>, <paramref name="column"/>) lies inside the rectangle.</summary>
    public bool Contains(int row, int column) => row >= Row0 && row <= Row1 && column >= Col0 && column <= Col1;

    /// <summary>Whether logical <paramref name="row"/> is within the rectangle's row span.</summary>
    public bool ContainsRow(int row) => row >= Row0 && row <= Row1;

    /// <summary>The number of rows the rectangle spans (≥ 1).</summary>
    public int RowSpan => Row1 - Row0 + 1;

    /// <summary>The number of columns the rectangle spans (≥ 1).</summary>
    public int ColumnSpan => Col1 - Col0 + 1;
}

/// <summary>
/// The cell-overflow mode the cell-layout pass (<see cref="TableModel.LayoutRow(int, IReadOnlyList{int}, TableOverflow)"/>)
/// honours (§5.6). Both are per-cell layout modes; the automatic <b>column-window</b> horizontal scroll is
/// orthogonal — a presenter-internal render offset applied in <i>either</i> mode when the grid can't fit even at
/// <see cref="TableModel.MinWidth"/> (M3.WP6, FB-6 sidestep) — so it is <b>not</b> a value here.
/// </summary>
public enum TableOverflow
{
    /// <summary>Content wider than the column wraps within the cell — the row grows taller (the v1 default, §5.6).</summary>
    Wrap = 0,

    /// <summary>
    /// Content wider than the column renders on <b>one</b> visual row, clipped to the column width with a
    /// trailing <c>…</c> (U+2026, width 1) — the logical row is exactly one visual row (no wrap-growth, §5.6).
    /// The focused cell reveals its full content (parallel to reveal-on-edit); non-focused cells stay truncated.
    /// A configurable alternative to <see cref="Wrap"/> (the user-facing toggle lands with M5).
    /// </summary>
    Truncate,
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
    public bool IsEmpty => SrcLength <= 0 && StyledRuns is not { Count: > 0 };

    /// <summary>
    /// Whether a trailing <c>…</c> (U+2026) follows this fragment's text — set only by
    /// <see cref="TableOverflow.Truncate"/> when the cell's content was clipped to the column width
    /// (<see cref="SrcStart"/>/<see cref="SrcLength"/> then span the visible <b>prefix</b>, <see cref="Width"/>
    /// its cell width, and the ellipsis draws in the one cell after it — total ≤ the column width). The
    /// presenter draws the glyph; <see langword="false"/> for every wrapped/fitting fragment.
    /// </summary>
    public bool Ellipsis { get; init; }

    /// <summary>
    /// The <b>formatted</b> (marks-hidden) styled runs this fragment draws (Decision 9 — per-cell reveal), or
    /// <see langword="null"/> when the cell renders RAW (a plain cell with no marks, or the active cell the caret
    /// is in). When present the presenter draws each <see cref="CellStyledRun"/>'s source slice styled instead of
    /// the raw <see cref="SrcStart"/>/<see cref="SrcLength"/> slice, and the caret map builds a stop per run; the
    /// runs tile the fragment's display, so <see cref="Width"/> is the display width the alignment/… key off.
    /// </summary>
    public IReadOnlyList<CellStyledRun>? StyledRuns { get; init; }
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
