using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace CursorialEdit.Layout;

/// <summary>
/// M1.WP6 — pure caret-navigation functions over <b>one line of text</b> (architecture §2.4; the FB-1
/// fallback): grapheme-cluster boundary motion, word-boundary motion, UTF-16 column ↔ display-cell
/// mapping, and soft-wrap segmentation with end-of-line affinity — the app-side reimplementation of the
/// framework's <c>internal</c> <c>GraphemeLayout</c>/<c>TextLayout</c>/<c>TextNavigation</c> trio, built
/// on the <b>public</b> <see cref="GraphemeWidth"/>/<see cref="GraphemeEnumerator"/> primitives and
/// behavior-matched against a real <c>TextBox</c> by <c>TextNavigationProbeTests</c> (risk R4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Project home (2026-07-05).</b> This type lives in the <c>CursorialEdit.Document</c> project (not the
/// app project) so the framework word-wrap can be shared by both callers: the run-map layout in the app
/// project (prose reveal) <i>and</i> <see cref="CursorialEdit.Document.Model.TableModel"/>'s word-aware cell
/// wrap in the Document project. It has no app-layer dependency, so the relocation is behaviour-neutral; the app project reaches it
/// through its ordinary reference to the Document project (namespace unchanged).
/// </para>
/// <para>
/// <b>Coordinates.</b> A <i>col</i> is a UTF-16 code-unit offset into the line (the document model's
/// <c>TextPosition.Col</c> convention); a <i>cell</i> is a terminal display column measured by
/// <see cref="GraphemeWidth"/> (goal columns are cells — architecture §2.4). Every col this class
/// returns is a grapheme-cluster boundary: the caret never lands inside a cluster.
/// </para>
/// <para>
/// <b>Scope.</b> The line must not contain hard line breaks (<c>\n</c>/<c>\r</c>) — document buffer
/// lines carry their ending out-of-band (<c>Line.Ending</c>). Cross-line motion (Up at row 0, document
/// Home/End, wrap-row prefix sums) is composition work owned by the caret/selection package (M1.WP8);
/// this class has no document or block knowledge.
/// </para>
/// <para>
/// <b>Word semantics.</b> Words are whitespace-delimited runs — exactly the framework
/// <c>TextNavigation</c> classifier <c>TextBox</c> ships (skip a whitespace run, then a non-whitespace
/// run), with the landing pinned to the cluster boundary at-or-before it, mirroring
/// <c>TextBox.SetCaretAndSelection</c>'s pin. Punctuation adheres to its word and unspaced CJK is one
/// word; see the probe's divergence report for why this is kept as-is.
/// </para>
/// </remarks>
public static class CaretNavigator
{
    // ───────────────────────────── cluster-boundary motion ─────────────────────────────

    /// <summary>
    /// The cluster boundary at or before <paramref name="col"/> (clamped to <c>[0, line.Length]</c>) —
    /// the pin applied to any externally produced offset before it becomes a caret position.
    /// </summary>
    public static int SnapToCluster(ReadOnlySpan<char> line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);

        var boundary = 0;
        var enumerator = line.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            var next = boundary + enumerator.Current.Length;
            if (next > col)
                return boundary;
            boundary = next;
        }

        return boundary;
    }

    /// <summary>The next cluster boundary strictly after <paramref name="col"/> (clamped to the line length) — caret Right.</summary>
    public static int NextCluster(ReadOnlySpan<char> line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);

        var boundary = 0;
        var enumerator = line.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            var next = boundary + enumerator.Current.Length;
            if (next > col)
                return next;
            boundary = next;
        }

        return line.Length;
    }

    /// <summary>The previous cluster boundary strictly before <paramref name="col"/> (clamped to 0) — caret Left.</summary>
    public static int PrevCluster(ReadOnlySpan<char> line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);

        var best = 0;
        var boundary = 0;
        var enumerator = line.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            boundary += enumerator.Current.Length;
            if (boundary >= col)
                break; // boundaries only grow — best can no longer change (O(col), like the siblings)
            best = boundary;
        }

        return best;
    }

    /// <summary>Whether <paramref name="col"/> is a cluster boundary of <paramref name="line"/> (0 and the length always are).</summary>
    public static bool IsClusterBoundary(ReadOnlySpan<char> line, int col)
        => col >= 0 && col <= line.Length && SnapToCluster(line, col) == col;

    // ───────────────────────────── word-boundary motion ─────────────────────────────

    /// <summary>
    /// The col after the next word — skip a leading whitespace run, then the word run — pinned to a
    /// cluster boundary (Ctrl+Right). Mirrors the framework <c>TextNavigation.NextWord</c> +
    /// <c>TextBox</c>'s boundary pin.
    /// </summary>
    public static int NextWord(ReadOnlySpan<char> line, int col)
    {
        var i = Math.Clamp(col, 0, line.Length);
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return SnapToCluster(line, i);
    }

    /// <summary>
    /// The col at the start of the previous word — skip a trailing whitespace run backward, then the
    /// word run — pinned to a cluster boundary (Ctrl+Left). Mirrors <c>TextNavigation.PrevWord</c> +
    /// <c>TextBox</c>'s boundary pin.
    /// </summary>
    public static int PrevWord(ReadOnlySpan<char> line, int col)
    {
        var i = Math.Clamp(col, 0, line.Length);
        while (i > 0 && char.IsWhiteSpace(line[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(line[i - 1])) i--;
        return SnapToCluster(line, i);
    }

    // ───────────────────────────── col ↔ cell mapping ─────────────────────────────

    /// <summary>
    /// The display cell of the cluster boundary at or before <paramref name="col"/> — the caret's cell
    /// column (and the goal column recorded on horizontal motion).
    /// </summary>
    public static int CellOfCol(ReadOnlySpan<char> line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);

        var boundary = 0;
        var cell = 0;
        var enumerator = line.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            if (boundary + cluster.Length > col)
                break;
            boundary += cluster.Length;
            cell += GraphemeWidth.ClusterWidth(cluster);
        }

        return cell;
    }

    /// <summary>
    /// The col of the cluster boundary whose cell is at or before <paramref name="cell"/> — goal-column
    /// landing: the nearest boundary at-or-before the goal cell, so a goal that falls inside a wide
    /// cluster (CJK/emoji) snaps before it, never inside.
    /// </summary>
    public static int ColAtOrBeforeCell(ReadOnlySpan<char> line, int cell)
    {
        var bestCol = 0;
        var boundary = 0;
        var cellCursor = 0;
        var enumerator = line.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            boundary += cluster.Length;
            cellCursor += GraphemeWidth.ClusterWidth(cluster);
            if (cellCursor > cell)
                break; // cells only grow — bestCol can no longer change (same early exit as PrevCluster)
            bestCol = boundary;
        }

        return bestCol;
    }

    /// <summary>
    /// The col of the first cluster boundary whose cell is at or after <paramref name="cell"/> (clamped
    /// to the line length) — the far edge of a cell window (clip/reveal math).
    /// </summary>
    public static int ColAtOrAfterCell(ReadOnlySpan<char> line, int cell)
    {
        if (cell <= 0)
            return 0;

        var boundary = 0;
        var cellCursor = 0;
        var enumerator = line.GetGraphemeEnumerator();
        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            boundary += cluster.Length;
            cellCursor += GraphemeWidth.ClusterWidth(cluster);
            if (cellCursor >= cell)
                return boundary;
        }

        return line.Length;
    }

    // ───────────────────────────── soft-wrap segmentation ─────────────────────────────

    /// <summary>
    /// Segments <paramref name="line"/> into visual rows under soft wrap — the single-line mirror of the
    /// framework's <c>TextLayout.Build</c>. <paramref name="wrapWidth"/> is the cell budget; wrapping
    /// engages only when positive and <paramref name="mode"/> is not <see cref="WrapMode.NoWrap"/>.
    /// <see cref="WrapMode.WordWrap"/> breaks at the boundary after a whitespace cluster (trailing
    /// spaces stay on the row; an over-long word hard-breaks at the overflowing cluster),
    /// <see cref="WrapMode.WordWrapOverflow"/> keeps an over-long word whole, and
    /// <see cref="WrapMode.CharacterWrap"/> breaks at the exact width. Breaks are always cluster
    /// boundaries — a 2-cell cluster never straddles a row edge.
    /// </summary>
    public static WrappedLine Wrap(string line, int wrapWidth, WrapMode mode = WrapMode.WordWrap)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (mode == WrapMode.NoWrap || wrapWidth <= 0 || GraphemeWidth.StringWidth(line) <= wrapWidth)
            return new WrappedLine(line, [(0, line.Length)]);

        return new WrappedLine(line, WrapSegments(line, wrapWidth, mode));
    }

    // The wrap loop — a faithful port of TextLayout.WrapSegments (the probed reference): a break
    // opportunity is the boundary AFTER a whitespace cluster; WordWrap hard-breaks a budget-wide word at
    // the overflowing cluster while WordWrapOverflow lets it run on; CharacterWrap breaks at the width.
    // Built eagerly — the grapheme enumerator is a ref struct and cannot cross a yield boundary.
    private static (int Start, int Length)[] WrapSegments(string line, int wrapWidth, WrapMode mode)
    {
        var segments = new List<(int Start, int Length)>();
        var segStart = 0;
        var segCells = 0;
        var lastBreak = -1; // col (cluster boundary) of the most recent word-break opportunity in this segment
        var pos = 0;
        var enumerator = line.GetGraphemeEnumerator();

        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            var width = GraphemeWidth.ClusterWidth(cluster);

            if (segCells + width > wrapWidth && segCells > 0)
            {
                var breakAt = mode == WrapMode.CharacterWrap ? pos
                    : lastBreak > segStart ? lastBreak
                    : mode == WrapMode.WordWrapOverflow ? -1
                    : pos;

                if (breakAt >= 0)
                {
                    segments.Add((segStart, breakAt - segStart));
                    segStart = breakAt;
                    segCells = CellsBetween(line, segStart, pos);
                    lastBreak = -1;
                }
            }

            segCells += width;
            pos += cluster.Length;

            if (cluster.Length > 0 && char.IsWhiteSpace(cluster[0]))
                lastBreak = pos;
        }

        segments.Add((segStart, line.Length - segStart));
        return [.. segments];
    }

    private static int CellsBetween(string line, int from, int to)
    {
        var cells = 0;
        var enumerator = line.AsSpan(from, to - from).GetGraphemeEnumerator();
        while (enumerator.MoveNext())
            cells += GraphemeWidth.ClusterWidth(enumerator.Current);
        return cells;
    }
}

/// <summary>
/// One line of text segmented into visual rows under soft wrap (<see cref="CaretNavigator.Wrap"/>) — the
/// single-line mirror of the framework's <c>TextLayout</c>, carrying its <b>end-of-line affinity</b>
/// semantics: a col sitting exactly on a soft-wrap boundary is simultaneously one row's content end and
/// the next row's start; <c>endAffinity</c> resolves it to the <b>earlier</b> row's visual end (the
/// affinity End and Up/Down landings leave behind) while <see langword="false"/> resolves it to the next
/// row's start (the natural affinity of Right/Home/typing).
/// </summary>
public readonly struct WrappedLine
{
    private readonly string _line;
    private readonly (int Start, int Length)[] _rows;

    internal WrappedLine(string line, (int Start, int Length)[] rows)
    {
        _line = line;
        _rows = rows;
    }

    private string Line => _line ?? string.Empty;

    private (int Start, int Length)[] Rows => _rows ?? [(0, 0)];

    /// <summary>The number of visual rows (always ≥ 1).</summary>
    public int RowCount => Rows.Length;

    /// <summary>The col of visual <paramref name="row"/>'s first character (the per-row Home target).</summary>
    public int RowStart(int row) => Rows[ClampRow(row)].Start;

    /// <summary>
    /// The col just past visual <paramref name="row"/>'s content (the per-row End target). For a
    /// soft-wrapped row this equals the next row's <see cref="RowStart"/> — the affinity-ambiguous col.
    /// </summary>
    public int RowEnd(int row)
    {
        var (start, length) = Rows[ClampRow(row)];
        return start + length;
    }

    /// <summary>The display width of visual <paramref name="row"/>, in cells.</summary>
    public int RowWidth(int row)
    {
        var (start, length) = Rows[ClampRow(row)];
        return GraphemeWidth.StringWidth(Line.AsSpan(start, length));
    }

    /// <summary>The visual row containing <paramref name="col"/>, resolved per <paramref name="endAffinity"/>.</summary>
    public int RowOfCol(int col, bool endAffinity = false)
    {
        var rows = Rows;
        col = Math.Clamp(col, 0, Line.Length);

        // The last row whose start is ≤ col; a col on a wrap boundary resolves to the NEXT row …
        var best = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i].Start <= col)
                best = i;
            else
                break;
        }

        // … unless end-affinity claims it for the earlier row's visual end (every wrap here is soft, so
        // the predecessor's content end always coincides with this row's start when lengths tile).
        if (endAffinity && best > 0
            && rows[best - 1].Start + rows[best - 1].Length == col && rows[best].Start == col)
            best--;

        return best;
    }

    /// <summary>
    /// Maps <paramref name="col"/> to its visual <c>(row, cell)</c> caret position, resolving a
    /// soft-wrap boundary col per <paramref name="endAffinity"/>. The cell is row-local (a wrapped row
    /// starts at cell 0).
    /// </summary>
    public (int Row, int Cell) Locate(int col, bool endAffinity = false)
    {
        col = Math.Clamp(col, 0, Line.Length);
        var row = RowOfCol(col, endAffinity);
        var start = Rows[row].Start;
        return (row, CaretNavigator.CellOfCol(RowSlice(row), col - start));
    }

    /// <summary>
    /// Maps a visual <c>(row, goal cell)</c> back to a col — the cluster boundary at or before
    /// <paramref name="goalCell"/> within the row, clamped to the row's content (goal-column landing).
    /// </summary>
    public int ColAt(int row, int goalCell)
    {
        row = ClampRow(row);
        return Rows[row].Start + CaretNavigator.ColAtOrBeforeCell(RowSlice(row), Math.Max(0, goalCell));
    }

    /// <summary>
    /// True when <paramref name="col"/> is the content end of soft-wrapped visual <paramref name="row"/>
    /// and coincides with the next row's start — the one caret col that maps to two visual positions,
    /// where the caret should keep end-affinity to render at this row's end.
    /// </summary>
    public bool IsRowEndBoundary(int row, int col)
    {
        row = ClampRow(row);
        var rows = Rows;
        return row < rows.Length - 1
            && col == RowEnd(row) && col > RowStart(row)
            && rows[row + 1].Start == col;
    }

    /// <summary>
    /// The landing for a vertical move of <paramref name="delta"/> visual rows from <paramref name="col"/>
    /// (the line-scoped mirror of <c>TextPresenter.MoveVertical</c>): the caret's row resolves per
    /// <paramref name="endAffinity"/>; the target cell is <paramref name="desiredCell"/> when ≥ 0 (the
    /// sticky goal column carried across a vertical run) else the caret's current cell; the target row is
    /// clamped to this line's rows (cross-line composition is the caller's job). Returns the landing col,
    /// the goal cell to keep sticky, and the landing's end-affinity.
    /// </summary>
    public (int Col, int Cell, bool EndAffinity) MoveVertical(int col, int delta, int desiredCell = -1, bool endAffinity = false)
    {
        var (row, cell) = Locate(col, endAffinity);
        var goal = desiredCell >= 0 ? desiredCell : cell;
        var targetRow = Math.Clamp(row + delta, 0, RowCount - 1);
        var targetCol = ColAt(targetRow, goal);
        return (targetCol, goal, IsRowEndBoundary(targetRow, targetCol));
    }

    /// <summary>The per-row Home landing: the start col of the caret's visual row (always start-affinity).</summary>
    public int HomeCol(int col, bool endAffinity = false) => RowStart(RowOfCol(col, endAffinity));

    /// <summary>
    /// The per-row End landing: the content-end col of the caret's visual row, plus the end-affinity
    /// that keeps the caret rendered at that row's end rather than aliasing to the next row's start.
    /// </summary>
    public (int Col, bool EndAffinity) EndCol(int col, bool endAffinity = false)
    {
        var row = RowOfCol(col, endAffinity);
        var end = RowEnd(row);
        return (end, IsRowEndBoundary(row, end));
    }

    private int ClampRow(int row) => Math.Clamp(row, 0, Rows.Length - 1);

    private ReadOnlySpan<char> RowSlice(int row)
    {
        var (start, length) = Rows[row];
        return Line.AsSpan(start, length);
    }
}
