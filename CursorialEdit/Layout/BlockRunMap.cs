using Cursorial.Rendering.Text;

using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Layout;

/// <summary>
/// One block's layout: per-visual-row <see cref="Run"/> maps built by soft-wrapping the block's
/// source lines through <see cref="TextLayout.Build"/> (architecture Decision 8 / §2.4). The
/// map carries a snapshot of the block's line texts, so rendering and caret math consume it
/// without touching the buffer; a re-formed block gets a fresh map, an unedited block's map stays
/// valid across every edit elsewhere (its spans are block-relative).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape (M1).</b> Every source line yields at least one visual row (a blank line is one
/// empty row); each row carries exactly one <see cref="RunKind.Text"/> run starting at cell 0.
/// M2's presenters extend rows to multiple runs (hidden marks, synthetic cells) without changing
/// the mapping surface below.
/// </para>
/// <para>
/// <b>Total mapping.</b> Source→cell (<see cref="Locate"/>) is total over
/// <c>[0, <see cref="SourceLength"/>]</c>: an offset inside a line maps into its row; an offset
/// inside a terminator (which no cell renders) snaps to its line's last row end. Cell→source
/// (<see cref="OffsetAt"/>) is total over any (row, cell): the cell clamps to the row's content
/// and pins to the grapheme-cluster boundary at or before it — the WP8 caret's landing rule.
/// </para>
/// </remarks>
public sealed class BlockRunMap : ICaretMap
{
    /// <summary>One visual row: which line it renders, the wrapped segment, and its block-relative span.</summary>
    private readonly record struct RowEntry(int Line, int SegStart, int SegLength, int SrcStart, int Width);

    private readonly string[] _lineTexts;
    private readonly RowEntry[] _rows;
    private readonly Run[] _runs; // one per row in M1; RunsForRow slices here

    // The per-line wrap results, retained so Locate/OffsetAt DELEGATE the soft-wrap semantics
    // (end-affinity boundary rule, cluster-pinned cell mapping) to the framework TextLayout instead
    // of mirroring them — one implementation, shared with the WP8 caret (review wave3-9). Each line's
    // text carries no hard break (endings live out-of-band), so its TextLayout is a pure soft-wrap.
    private readonly TextLayout[] _wrapped;

    /// <summary>Prefix sums: entry <c>i</c> is line <c>i</c>'s first visual row; entry <c>lineCount</c> is <see cref="RowCount"/>.</summary>
    private readonly int[] _lineFirstRow;

    /// <summary>Prefix sums: entry <c>i</c> is line <c>i</c>'s block-relative source start; entry <c>lineCount</c> is <see cref="SourceLength"/>.</summary>
    private readonly int[] _lineSrcStart;

    private BlockRunMap(
        string[] lineTexts, RowEntry[] rows, Run[] runs,
        TextLayout[] wrapped, int[] lineFirstRow, int[] lineSrcStart,
        int wrapWidth, int sourceLength)
    {
        _lineTexts = lineTexts;
        _rows = rows;
        _runs = runs;
        _wrapped = wrapped;
        _lineFirstRow = lineFirstRow;
        _lineSrcStart = lineSrcStart;
        WrapWidth = wrapWidth;
        SourceLength = sourceLength;
    }

    /// <summary>The cell budget this map was wrapped for (≤ 0 means no wrapping was applied).</summary>
    public int WrapWidth { get; }

    /// <summary>The block source snapshot's total length in UTF-16 code units, terminators included.</summary>
    public int SourceLength { get; }

    /// <summary>The number of visual rows — the block's rendered height in terminal rows (≥ 1).</summary>
    public int RowCount => _rows.Length;

    /// <summary>
    /// Builds the map for a block's <paramref name="lines"/> at <paramref name="wrapWidth"/>
    /// cells (<see cref="Cursorial.Rendering.Text.WrapMode.WordWrap"/>, the editor's mode).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty (a block owns at least one line).</exception>
    public static BlockRunMap Build(IReadOnlyList<Line> lines, int wrapWidth)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
            throw new ArgumentException("A block owns at least one line.", nameof(lines));

        var lineTexts = new string[lines.Count];
        var wrapped = new TextLayout[lines.Count];
        var lineFirstRow = new int[lines.Count + 1];
        var lineSrcStart = new int[lines.Count + 1];
        var rows = new List<RowEntry>(lines.Count);
        var srcStart = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            // Sanitize a lone '\r' (kept as in-line content by the buffer) to its control picture so
            // TextLayout.Build cannot hard-break the line at it — a 1:1 substitution, so offsets stay aligned.
            string display = DisplayText.SanitizeControls(line.Text);
            lineTexts[i] = display;
            lineFirstRow[i] = rows.Count;
            lineSrcStart[i] = srcStart;

            var wrappedLine = TextLayout.Build(display, wrapWidth, WrapMode.WordWrap);
            wrapped[i] = wrappedLine;
            for (var r = 0; r < wrappedLine.LineCount; r++)
            {
                int segStart = wrappedLine.LineContentStart(r);
                int segLength = wrappedLine.LineContentEnd(r) - segStart;
                rows.Add(new RowEntry(i, segStart, segLength, srcStart + segStart, wrappedLine.LineWidth(r)));
            }

            srcStart += line.TotalLength;
        }

        lineFirstRow[lines.Count] = rows.Count;
        lineSrcStart[lines.Count] = srcStart;

        var runs = new Run[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            runs[r] = new Run(row.SrcStart, row.SegLength, Col: 0, row.Width, RunKind.Text);
        }

        return new BlockRunMap(lineTexts, [.. rows], runs, wrapped, lineFirstRow, lineSrcStart, wrapWidth, srcStart);
    }

    /// <summary>The text slice visual <paramref name="row"/> renders (empty for a blank row).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public ReadOnlySpan<char> RowText(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);

        var entry = _rows[row];
        return _lineTexts[entry.Line].AsSpan(entry.SegStart, entry.SegLength);
    }

    /// <summary>The runs of visual <paramref name="row"/> (M1: exactly one <see cref="RunKind.Text"/> run).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public ReadOnlySpan<Run> RunsForRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);
        return _runs.AsSpan(row, 1);
    }

    /// <summary>The display width of visual <paramref name="row"/>'s content, in cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int RowWidth(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);
        return _rows[row].Width;
    }

    /// <summary>
    /// Maps a block-relative source <paramref name="srcOffset"/> to its visual (row, cell)
    /// position (clamped to <c>[0, SourceLength]</c>; terminator interiors snap to their line's
    /// row end). A soft-wrap boundary offset addresses two positions; <paramref name="endAffinity"/>
    /// resolves it to the earlier row's visual end, <see langword="false"/> to the next row's
    /// start — <see cref="TextLayout"/>'s affinity contract, delegated to it after the line-level
    /// lookup so the boundary rule has exactly one implementation.
    /// </summary>
    public (int Row, int Cell) Locate(int srcOffset, bool endAffinity = false)
    {
        srcOffset = Math.Clamp(srcOffset, 0, SourceLength);

        int line = LineOfSrcOffset(srcOffset);

        // A terminator-interior offset (which no cell renders) clamps to the line's text end —
        // TextLayout then lands it on its last row's visual end. End affinity never crosses a
        // hard line break: the offset resolves to ONE line first, and the affinity rule applies
        // only to that line's soft-wrap boundaries.
        int col = Math.Min(srcOffset - _lineSrcStart[line], _lineTexts[line].Length);
        var (row, cell) = _wrapped[line].Locate(col, endAffinity);
        return (_lineFirstRow[line] + row, cell);
    }

    /// <summary>
    /// Maps a visual (<paramref name="row"/>, <paramref name="cell"/>) back to a block-relative
    /// source offset: the grapheme-cluster boundary at or before the cell, clamped to the row's
    /// content — <see cref="TextLayout.OffsetAt"/>'s goal-column landing rule (cells inside a wide
    /// cluster snap before it), delegated after the line-level lookup.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int OffsetAt(int row, int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);

        int line = _rows[row].Line;
        return _lineSrcStart[line] + _wrapped[line].OffsetAt(row - _lineFirstRow[line], cell);
    }

    /// <summary>
    /// The block-relative source offset at the content end of visual <paramref name="row"/> — the row's
    /// single <see cref="RunKind.Text"/> run end (<see cref="ICaretMap.RowEndOffset"/> / the End-key landing).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int RowEndOffset(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);

        var entry = _rows[row];
        return entry.SrcStart + entry.SegLength;
    }

    /// <summary>
    /// The block-relative source offset nearest to (<paramref name="row"/>, <paramref name="cell"/>):
    /// the cluster boundary bracketing the cell, rounded to the closer side (ties toward the earlier
    /// boundary) — the mouse hit-test rule (<c>TextBox.IndexFromPointer</c>'s display-space rounding).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int NearestOffset(int row, int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows.Length);

        int line = _rows[row].Line;
        var glyphs = _wrapped[line].LineGlyphs(row - _lineFirstRow[line]); // the row's line-local cluster layout
        int before = glyphs.CharIndexAtOrBeforeColumn(cell);
        int after = glyphs.NextBoundary(before);
        int chosen = after == before
            || cell - glyphs.ColumnOf(before) <= glyphs.ColumnOf(after) - cell
            ? before
            : after;

        return _rows[row].SrcStart + chosen;
    }

    /// <summary>The line owning <paramref name="srcOffset"/> — the largest <c>i</c> with <c>_lineSrcStart[i] ≤ srcOffset</c>.</summary>
    private int LineOfSrcOffset(int srcOffset)
    {
        // Last-on-ties, like the panel's BlockAtRow: a trailing empty unterminated line shares
        // its start with the previous line's end and must own the offset.
        int lo = 0, hi = _lineTexts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineSrcStart[mid] <= srcOffset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }
}
