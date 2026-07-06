using Cursorial.Rendering.Text;
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="overflow"/> is not <see cref="TableOverflow.Wrap"/> (truncate / column-window are WP6).</exception>
    public TableRowLayout LayoutRow(int row, IReadOnlyList<int> columnWidths, TableOverflow overflow = TableOverflow.Wrap)
    {
        ArgumentNullException.ThrowIfNull(columnWidths);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);
        if (overflow != TableOverflow.Wrap)
            throw new ArgumentOutOfRangeException(nameof(overflow), overflow, "WP1 implements Wrap only; truncate and column-window are M3.WP6.");

        var r = _rows[row];
        int columns = _columns.Length;

        // Per column: the cell's content word-wrapped to its (viewport-resolved) width. The row's visual height
        // is the max fragment count over its cells — the single place wrapped-cell → visual-row mapping is decided.
        var perColumn = new CellFragment[columns][];
        int visualRows = 1;
        for (var c = 0; c < columns; c++)
        {
            int width = c < columnWidths.Count ? columnWidths[c] : _columns[c].WidthCells;
            perColumn[c] = WrapCell(r.Cells[c], width);
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="overflow"/> is not <see cref="TableOverflow.Wrap"/>.</exception>
    public TableRowLayout LayoutRow(int row, TableOverflow overflow = TableOverflow.Wrap)
    {
        var widths = new int[_columns.Length];
        for (var c = 0; c < _columns.Length; c++)
            widths[c] = _columns[c].WidthCells;
        return LayoutRow(row, widths, overflow);
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
        var rowCellCounts = new List<int>(); // Markdig's own cell count per row (before ragged padding)
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

        // Column alignment from the delimiter row; a ragged-excess column has none.
        var columns = new Column[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var align = c < table.ColumnDefinitions.Count ? MapAlignment(table.ColumnDefinitions[c].Alignment) : ColumnAlignment.None;
            columns[c] = MeasureColumn(blockSource, rowSpans, c, align);
        }

        var rows = new Row[rowSpans.Count];
        for (var i = 0; i < rowSpans.Count; i++)
            rows[i] = new Row(rowSpans[i], headerFlags[i], rowLines[i]);

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
        if (text.Length == 0)
            return [new CellFragment(srcStart, 0, 0)];

        int budget = Math.Max(1, width);
        var wrapped = TextLayout.Build(text, budget, WrapMode.WordWrap);
        var textSpan = text.AsSpan();

        // A cell renders TRIMMED content per visual row — unlike prose, whose soft wrap keeps a trailing space.
        // For each wrapped segment we trim trailing whitespace from the RENDERED width (so a right/center-
        // aligned wrapped line stays flush to its edge, and a word that exactly fills the column doesn't spill
        // the following space as its own blank row); the SOURCE range still spans the full segment so the
        // fragments tile the cell and the caret round-trips (the trimmed space stays attributed to its source).
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
                fragments[^1] = fragments[^1] with { SrcLength = srcStart + end - fragments[^1].SrcStart };
                continue;
            }

            int renderWidth = wrapped.LineWidth(v) - (end - renderEnd); // each trimmed trailing char is a width-1 space/tab
            fragments.Add(new CellFragment(srcStart + start, end - start, renderWidth));
        }

        return fragments.Count > 0 ? [.. fragments] : [new CellFragment(srcStart, text.Length, 0)];
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
