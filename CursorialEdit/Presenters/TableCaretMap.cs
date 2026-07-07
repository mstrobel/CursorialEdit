using Cursorial.Rendering.Text;
using Cursorial.Text;

using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The composite caret map for a table block (M3.WP4 / WP9 foundation): an <see cref="ICaretMap"/> over
/// the <b>rendered grid</b> — top/separator/bottom border rows plus one content row per wrapped visual
/// row — so <see cref="DocumentCaret"/> maps a block-relative source offset to the exact grid (row, cell)
/// and back, landing the caret <b>inside a table cell</b> rather than on the raw source line. It mirrors
/// the vertical structure <see cref="TableRowPresenter"/> draws (its <see cref="RowCount"/> equals the
/// grid height the panel lays out), and its caret stops are the per-visual-row cell fragments
/// (<see cref="RunKind.Text"/> substrate) — border rows and the delimiter line carry no stop, so a caret
/// query on one snaps to the nearest cell (§3.1 [EDGE]: arrow/click onto a table line enters the nearest
/// cell by goal column).
/// </summary>
/// <remarks>
/// <para>
/// <b>Cell focus survives reparse (Decision 4).</b> The map holds no mutable cell focus: (row, col) is
/// re-derived from the caret's source offset against the <i>current</i> model on every query. A re-adopted
/// table block keeps its <see cref="BlockId"/>, so the caret's source anchor is preserved across the
/// reparse and re-lands in the corresponding cell through the freshly built map.
/// </para>
/// <para>
/// <b>No horizontal slide.</b> A table lays its cells out in whole cells (wrapping at the column width),
/// so unlike prose there is never a horizontal slide — the bridge reports <c>ActiveSlide == 0</c> for a
/// table block, and every cell reported here is the published cell directly.
/// </para>
/// </remarks>
internal sealed class TableCaretMap : ICaretMap
{
    /// <summary>One caret-landable cell fragment on a content grid row: its first draw cell, display width, source range, and emptiness.</summary>
    private readonly record struct Stop(int Cell, int Width, int SrcStart, int SrcLen, bool Empty);

    /// <summary>One grid row: its ordered content stops (empty array = a border/delimiter row, no caret stop).</summary>
    private readonly record struct GridRow(Stop[] Stops);

    private readonly string _source;
    private readonly GridRow[] _rows;

    // All stops flattened in source order (with their grid row) — the Locate binary-search index.
    private readonly int[] _stopSrc;
    private readonly int[] _stopRow;
    private readonly Stop[] _stops;

    private TableCaretMap(string source, GridRow[] rows, int[] stopSrc, int[] stopRow, Stop[] stops)
    {
        _source = source;
        _rows = rows;
        _stopSrc = stopSrc;
        _stopRow = stopRow;
        _stops = stops;
    }

    /// <inheritdoc/>
    public int RowCount => _rows.Length;

    /// <summary>
    /// Builds the caret map from a table <paramref name="model"/>, its shared grid <paramref name="metrics"/>,
    /// its serialized <paramref name="source"/> (the cell-span origin), and the cell-overflow
    /// <paramref name="overflow"/> — so the map's grid height and per-cell stop geometry match what the
    /// <see cref="TableRowPresenter"/> actually draws (Wrap = one row per wrapped visual row; Truncate = exactly
    /// one row per logical row, each cell mapped at its NATURAL full-content geometry, matching both the truncated
    /// prefix a non-focused cell draws and the un-truncated reveal a focused cell draws).
    /// </summary>
    /// <param name="activeCell">
    /// The focused cell under <see cref="TableOverflow.Truncate"/> (its content is drawn in FULL, overflowing its
    /// column), so its stop spans the whole reveal and a click on the revealed overflow round-trips and matches the
    /// overdrawn pixels; every non-focused cell clamps its click-box to its drawn column. <see langword="null"/> (or
    /// Wrap) = no full-reveal cell.
    /// </param>
    public static TableCaretMap Build(TableModel model, TableGridMetrics metrics, string source, TableOverflow overflow = TableOverflow.Wrap, (int Row, int Column)? activeCell = null)
    {
        var rows = new List<GridRow>();
        var flatSrc = new List<int>();
        var flatRow = new List<int>();
        var flatStops = new List<Stop>();
        int columns = model.ColumnCount;

        for (var r = 0; r < model.RowCount; r++)
        {
            if (r == 0)
                rows.Add(new GridRow([])); // top border

            // Per-cell reveal (Decision 9): the focused cell (the one the caret is in) maps its RAW content 1:1;
            // every other cell maps its FORMATTED (marks-hidden) display through the same LayoutRow fragments the
            // presenter draws — so a caret/click in a formatted cell lands on the right SOURCE offset even though
            // the marks are hidden, and the active cell edits raw exactly as today.
            int activeColumn = activeCell is { } a && a.Row == r ? a.Column : -1;

            if (overflow == TableOverflow.Truncate)
            {
                var layout = model.LayoutRow(r, metrics.ColumnWidths, TableOverflow.Truncate, activeColumn);
                var visual = layout.VisualRows[0];
                var stops = new List<Stop>(columns);
                for (var c = 0; c < columns; c++)
                {
                    if (model.IsCellEmpty(r, c))
                    {
                        var (anchor, _) = model.CellContentRange(r, c);
                        stops.Add(new Stop(metrics.AlignedX(c, 0), 0, anchor, 0, Empty: true));
                        continue;
                    }

                    if (c == activeColumn)
                    {
                        // The focused cell reveals its RAW content in full (overflowing its column), so its stop spans
                        // the whole raw content at its natural geometry — a click on the revealed overflow round-trips.
                        var (start, end) = model.CellContentRange(r, c);
                        int fullWidth = GraphemeWidth.StringWidth(model.CellContent(r, c));
                        int cellX = fullWidth <= metrics.ColumnWidth(c) ? metrics.AlignedX(c, fullWidth) : metrics.ContentX(c);
                        stops.Add(new Stop(cellX, fullWidth, start, end - start, Empty: false));
                        continue;
                    }

                    // A non-active truncated cell maps only its DRAWN prefix (the TruncateCell fragment) — no
                    // column-clamped click-box (deferred #4, verified no-op). The ellipsis reserves one cell, so a
                    // truncated cell's drawn width is always ≥ columnWidth − 1: at most ONE padding cell inside the
                    // column box is left uncovered, and it sits distance 1 from this cell's prefix vs. distance ≥ 2
                    // from the neighbour cell's content, so NearestCell's nearest-column snap keeps a click there in
                    // THIS cell. Clamping the stop to the whole column would land identically — hence unneeded. (A
                    // click into the neighbour's own drawn text still resolves to the neighbour: TableOverflowTests
                    // .Truncate_ClickOnCellRightOfAWideCell…, and the padding case: …_LandsInTheTruncatedCell.)
                    AddFragmentStops(stops, metrics, c, visual.Cell(c));
                }

                AddContentRow(rows, flatSrc, flatRow, flatStops, stops);
                rows.Add(new GridRow([])); // separator / bottom border
                continue;
            }

            var wrapLayout = model.LayoutRow(r, metrics.ColumnWidths, TableOverflow.Wrap, activeColumn);
            for (var v = 0; v < wrapLayout.VisualRowCount; v++)
            {
                var stops = new List<Stop>(columns);
                var visual = wrapLayout.VisualRows[v];
                for (var c = 0; c < columns; c++)
                {
                    if (model.IsCellEmpty(r, c))
                    {
                        if (v == 0)
                        {
                            var (anchor, _) = model.CellContentRange(r, c);
                            stops.Add(new Stop(metrics.AlignedX(c, 0), 0, anchor, 0, Empty: true));
                        }

                        continue;
                    }

                    AddFragmentStops(stops, metrics, c, visual.Cell(c));
                }

                AddContentRow(rows, flatSrc, flatRow, flatStops, stops);
            }

            rows.Add(new GridRow([])); // separator between rows, or the bottom border after the last
        }

        // Stable-sort the flat index by source offset (the Locate key). A tie keeps grid order (rows top to
        // bottom, cells left to right) so a wrap boundary resolves deterministically.
        var order = Enumerable.Range(0, flatSrc.Count).ToArray();
        Array.Sort(order, (i, j) => flatSrc[i] != flatSrc[j] ? flatSrc[i] - flatSrc[j] : i - j);
        var stopSrc = new int[order.Length];
        var stopRow = new int[order.Length];
        var stops2 = new Stop[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            stopSrc[i] = flatSrc[order[i]];
            stopRow[i] = flatRow[order[i]];
            stops2[i] = flatStops[order[i]];
        }

        return new TableCaretMap(source, [.. rows], stopSrc, stopRow, stops2);
    }

    /// <summary>
    /// Emits the caret stops for one cell fragment on a visual row. A <b>formatted</b> (marks-hidden) fragment
    /// carries one styled run per content run — each 1:1 with a contiguous source slice, so it becomes one stop
    /// whose display maps to source exactly like a plain cell (the hidden marks fall between runs and canonicalize
    /// onto the adjacent content). A <b>raw</b> fragment (a plain cell, or the active cell) becomes one whole-cell
    /// stop, byte-identical to the pre-formatting map.
    /// </summary>
    private static void AddFragmentStops(List<Stop> stops, TableGridMetrics metrics, int column, CellFragment fragment)
    {
        if (fragment.IsEmpty)
            return; // this cell's content did not wrap this far — no stop on this visual row

        if (fragment.StyledRuns is { Count: > 0 } styled)
        {
            int alignedX = fragment.Ellipsis ? metrics.ContentX(column) : metrics.AlignedX(column, fragment.Width);
            int fragEnd = fragment.SrcStart + fragment.SrcLength; // the fragment's content end (past trailing hidden marks)
            for (var i = 0; i < styled.Count; i++)
            {
                var run = styled[i];
                // The LAST run of the fragment absorbs any trailing hidden marks up to the content end, so
                // End / a caret at the cell's visual end lands at the TRUE content end (past e.g. a closing
                // `**`), not before the marks. Only extends the stop's source SPAN — its display width is
                // unchanged, and a formatted cell's trailing-mark offsets are never Located (the caret's cell
                // is always the active/raw one), so clicks still land on the visible text.
                int srcLen = i == styled.Count - 1 ? fragEnd - run.SrcStart : run.SrcLength;
                stops.Add(new Stop(alignedX + run.CellOffset, run.Width, run.SrcStart, srcLen, Empty: false));
            }
            return;
        }

        int x = fragment.Ellipsis ? metrics.ContentX(column) : metrics.AlignedX(column, fragment.Width);
        stops.Add(new Stop(x, fragment.Width, fragment.SrcStart, fragment.SrcLength, Empty: false));
    }

    /// <summary>Sorts one visual row's <paramref name="stops"/> by cell, appends them to the flat Locate index at the next grid row, and records the <see cref="GridRow"/>.</summary>
    private static void AddContentRow(List<GridRow> rows, List<int> flatSrc, List<int> flatRow, List<Stop> flatStops, List<Stop> stops)
    {
        stops.Sort(static (a, b) => a.Cell - b.Cell);
        int gridRow = rows.Count;
        foreach (var stop in stops)
        {
            flatSrc.Add(stop.SrcStart);
            flatRow.Add(gridRow);
            flatStops.Add(stop);
        }

        rows.Add(new GridRow([.. stops]));
    }

    /// <inheritdoc/>
    public (int Row, int Cell) Locate(int srcOffset, bool endAffinity = false)
    {
        if (_stops.Length == 0)
            return (0, 0);

        // The stop with the largest source start ≤ srcOffset owns the caret (its trailing padding rounds
        // back into it). endAffinity resolves a wrap boundary to the earlier fragment's end.
        int i = LargestStartAtOrBefore(srcOffset);
        if (endAffinity && i > 0 && _stopSrc[i] == srcOffset && !_stops[i].Empty)
        {
            var prev = _stops[i - 1];
            if (!prev.Empty && prev.SrcStart + prev.SrcLen == srcOffset)
                i--;
        }

        var stop = _stops[i];
        int within = Math.Clamp(srcOffset - stop.SrcStart, 0, stop.SrcLen);
        int cell = stop.Empty ? stop.Cell : stop.Cell + CellsOf(stop, within);
        return (_stopRow[i], cell);
    }

    /// <inheritdoc/>
    public int OffsetAt(int row, int cell)
    {
        var (stops, _) = NearestContentRow(row);
        if (stops.Length == 0)
            return 0;

        var stop = NearestCell(stops, cell);
        if (stop.Empty)
            return stop.SrcStart;

        int goal = Math.Max(0, cell - stop.Cell);
        return stop.SrcStart + ColAtOrBefore(stop, goal);
    }

    /// <inheritdoc/>
    public int RowEndOffset(int row)
    {
        var (stops, _) = NearestContentRow(row);
        if (stops.Length == 0)
            return 0;

        var last = stops[^1];
        return last.SrcStart + last.SrcLen;
    }

    /// <inheritdoc/>
    public int NearestOffset(int row, int cell) => OffsetAt(row, cell);

    /// <inheritdoc/>
    public bool HasCaretStop(int row) => (uint)row < (uint)_rows.Length && _rows[row].Stops.Length > 0;

    // ───────────────────────────── helpers ─────────────────────────────

    private int LargestStartAtOrBefore(int srcOffset)
    {
        int lo = 0, hi = _stopSrc.Length - 1;
        if (srcOffset < _stopSrc[0])
            return 0;

        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_stopSrc[mid] <= srcOffset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>The content stops of <paramref name="row"/>, or of the nearest content grid row (down first, then up) when <paramref name="row"/> is a border/delimiter row.</summary>
    private (Stop[] Stops, int Row) NearestContentRow(int row)
    {
        row = Math.Clamp(row, 0, _rows.Length - 1);
        if (_rows[row].Stops.Length > 0)
            return (_rows[row].Stops, row);

        for (var delta = 1; delta < _rows.Length; delta++)
        {
            int down = row + delta;
            if (down < _rows.Length && _rows[down].Stops.Length > 0)
                return (_rows[down].Stops, down);
            int up = row - delta;
            if (up >= 0 && _rows[up].Stops.Length > 0)
                return (_rows[up].Stops, up);
        }

        return ([], row);
    }

    /// <summary>The stop nearest <paramref name="cell"/> — the one whose cell box contains it, else the closest by column (goal-column snap, §3.1 [EDGE]).</summary>
    private static Stop NearestCell(Stop[] stops, int cell)
    {
        var best = stops[0];
        int bestDistance = int.MaxValue;
        foreach (var stop in stops)
        {
            int lo = stop.Cell;
            int hi = stop.Cell + stop.Width;
            int distance = cell < lo ? lo - cell : cell > hi ? cell - hi : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = stop;
                if (distance == 0)
                    break;
            }
        }

        return best;
    }

    private int CellsOf(Stop stop, int within)
    {
        if (within <= 0 || stop.SrcLen <= 0)
            return 0;
        var slice = _source.AsSpan(stop.SrcStart, Math.Min(stop.SrcLen, _source.Length - stop.SrcStart));
        return GraphemeLayout.Build(slice).ColumnOf(Math.Min(within, slice.Length));
    }

    private int ColAtOrBefore(Stop stop, int goalCell)
    {
        if (stop.SrcLen <= 0)
            return 0;
        var slice = _source.AsSpan(stop.SrcStart, Math.Min(stop.SrcLen, _source.Length - stop.SrcStart));
        return GraphemeLayout.Build(slice).CharIndexAtOrBeforeColumn(goalCell);
    }
}
