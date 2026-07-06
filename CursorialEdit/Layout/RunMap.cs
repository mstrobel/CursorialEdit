using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace CursorialEdit.Layout;

/// <summary>
/// The M2.WP5 per-block layout (architecture Decision 8 / §2.4): every source line is wrapped into
/// one or more visual rows, and every visual row is a sequence of <see cref="Run"/>s carrying the
/// four <see cref="RunKind"/>s. Unlike M1's <see cref="BlockRunMap"/> (one <see cref="RunKind.Text"/>
/// run per row), a <see cref="RunMap"/> hides syntax marks (<see cref="RunKind.HiddenMark"/>,
/// zero visible cells at their true source position), reveals them on the active line
/// (<see cref="RunKind.RevealedMark"/>), and substitutes structural markers with glyphs
/// (<see cref="RunKind.Synthetic"/>) — yet the source↔cell mapping stays <b>total in both
/// directions</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuse of the M1 wrap.</b> Each line is rendered to a display string (marks hidden collapse to
/// nothing, revealed marks and synthetic glyphs occupy cells) and wrapped through
/// <see cref="TextLayout.Build"/>/<see cref="TextLayout"/> — so wrap points, end-of-line
/// affinity, and cluster/cell math are exactly the probed M1 behavior. When a line carries no marks
/// (plain text, or the degenerate empty-inline-run case) the display string equals the source and the
/// mapping is bit-for-bit M1's; the mark and synthetic runs are additive on top.
/// </para>
/// <para>
/// <b>Total mapping.</b> <see cref="Locate"/> maps every block-relative source offset in
/// <c>[0, SourceLength]</c> to a (row, cell): offsets inside a hidden mark collapse to the mark's
/// cell position, offsets inside a synthetic marker collapse to the marker's single caret stop, and
/// a terminator interior snaps to its line's last row end. <see cref="OffsetAt"/> maps every
/// (row, cell) back to the grapheme-cluster boundary at or before the cell (synthetic markers map
/// atomically to their marker source) — cluster-pinned and wrap-affinity-correct.
/// </para>
/// <para>
/// <b>Horizontal slide.</b> Cells reported by the runs and by <see cref="Locate"/> are row-local and
/// <i>unclipped</i> (cell 0 is the block box's left edge). A viewport narrower than a row — always in
/// wrap-off mode, and on the revealed active line — is realized by <see cref="HorizontalSlide"/> plus
/// <see cref="ClipRow"/>, which together implement the binding caret-visibility invariant and the
/// less/vim continuation indicators. The document itself never scrolls horizontally.
/// </para>
/// </remarks>
public sealed class RunMap : ICaretMap
{
    // ── per visual row ──
    private readonly Run[][] _rowRuns;         // runs in cell order (includes zero-width hidden marks)
    private readonly RowCluster[][] _rowCells; // the row's visible grapheme clusters (clip/hit-test)
    private readonly int[] _rowWidth;          // rendered width in cells
    private readonly int[] _rowLine;           // which source line this row belongs to

    // ── per source line ──
    private readonly TextLayout[] _wrapped;    // the display string's soft-wrap (marks resolved)
    private readonly int[][] _srcToDisplay;    // line col → display-string index (length textLen+1)
    private readonly int[][] _displayToSrc;    // display-string index → block-relative source offset
    private readonly int[] _lineFirstRow;      // prefix sums: line i's first visual row; [lineCount] = RowCount
    private readonly int[] _lineSrcStart;      // prefix sums: line i's block-relative source start; [lineCount] = SourceLength
    private readonly int[] _lineTextLen;       // line i's text length (terminator excluded)

    internal RunMap(
        Run[][] rowRuns, RowCluster[][] rowCells, int[] rowWidth, int[] rowLine,
        TextLayout[] wrapped, int[][] srcToDisplay, int[][] displayToSrc,
        int[] lineFirstRow, int[] lineSrcStart, int[] lineTextLen,
        int sourceLength, int wrapWidth, WrapMode wrapMode, int? activeLine)
    {
        _rowRuns = rowRuns;
        _rowCells = rowCells;
        _rowWidth = rowWidth;
        _rowLine = rowLine;
        _wrapped = wrapped;
        _srcToDisplay = srcToDisplay;
        _displayToSrc = displayToSrc;
        _lineFirstRow = lineFirstRow;
        _lineSrcStart = lineSrcStart;
        _lineTextLen = lineTextLen;
        SourceLength = sourceLength;
        WrapWidth = wrapWidth;
        WrapMode = wrapMode;
        ActiveLine = activeLine;
    }

    /// <summary>The block source snapshot's total length in UTF-16 code units, terminators included.</summary>
    public int SourceLength { get; }

    /// <summary>The cell budget the map was wrapped for (≤ 0 means no wrapping was applied).</summary>
    public int WrapWidth { get; }

    /// <summary>The wrap mode the map was built with (<see cref="WrapMode.WordWrap"/> = wrap-on, <see cref="WrapMode.NoWrap"/> = wrap-off).</summary>
    public WrapMode WrapMode { get; }

    /// <summary>The active source line (revealed marks, one un-wrapped slidable row), or <see langword="null"/> when the block is inactive.</summary>
    public int? ActiveLine { get; }

    /// <summary>The number of visual rows — the block's rendered height in terminal rows (≥ 1).</summary>
    public int RowCount => _rowRuns.Length;

    /// <summary>The source line rendered by visual <paramref name="row"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int LineOfRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        return _rowLine[row];
    }

    /// <summary>Whether visual <paramref name="row"/> belongs to the active (revealed, slidable) line.</summary>
    public bool IsActiveRow(int row) => ActiveLine is { } line && LineOfRow(row) == line;

    /// <summary>
    /// The visual-row span a source <paramref name="line"/> occupies: its first visual row and the
    /// number of rows it wrapped into. The presenter uses the <b>inactive</b> map's span to reserve an
    /// active (un-wrapped, slid) line's freed rows as blank — height-invariance under reveal
    /// (Decision 9 / §4.1): a line that wraps to N rows while hidden but renders as one slid row when
    /// revealed keeps its N-row footprint, so revealing never shrinks the block and shifts siblings.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="line"/> is out of range.</exception>
    public (int FirstRow, int RowCount) RowsOfLine(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, _lineTextLen.Length);
        return (_lineFirstRow[line], _lineFirstRow[line + 1] - _lineFirstRow[line]);
    }

    /// <summary>The runs of visual <paramref name="row"/> in ascending cell order (zero-width hidden marks included).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public ReadOnlySpan<Run> RunsForRow(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        return _rowRuns[row];
    }

    /// <summary>The rendered display width of visual <paramref name="row"/>, in cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int RowWidth(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        return _rowWidth[row];
    }

    /// <summary>
    /// Maps a block-relative source <paramref name="srcOffset"/> to its unclipped visual (row, cell).
    /// Clamped to <c>[0, SourceLength]</c>; a terminator interior snaps to its line's last row end; an
    /// offset inside a hidden mark collapses to that mark's cell; an offset inside a synthetic marker
    /// collapses to the marker's single stop. A soft-wrap boundary is resolved by
    /// <paramref name="endAffinity"/> (the earlier row's visual end when <see langword="true"/>).
    /// </summary>
    public (int Row, int Cell) Locate(int srcOffset, bool endAffinity = false)
    {
        srcOffset = Math.Clamp(srcOffset, 0, SourceLength);
        int line = LineOfSrcOffset(srcOffset);

        // Terminator-interior offsets (no cell renders them) clamp to the line's text end; the display
        // mapping then lands them on the line's last visual row end.
        int col = Math.Min(srcOffset - _lineSrcStart[line], _lineTextLen[line]);
        int display = _srcToDisplay[line][col];
        var (rowInLine, cell) = _wrapped[line].Locate(display, endAffinity);
        return (_lineFirstRow[line] + rowInLine, cell);
    }

    /// <summary>
    /// Maps an unclipped visual (<paramref name="row"/>, <paramref name="cell"/>) back to a
    /// block-relative source offset: the grapheme-cluster boundary at or before the cell, clamped to
    /// the row's content (a synthetic marker's cells map atomically to its marker source start).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int OffsetAt(int row, int cell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);

        int line = _rowLine[row];
        int display = _wrapped[line].OffsetAt(row - _lineFirstRow[line], cell);
        return _displayToSrc[line][display];
    }

    /// <summary>
    /// Clips visual <paramref name="row"/> to a horizontal viewport of <paramref name="viewport"/>
    /// cells starting at <paramref name="slideOffset"/> — the grapheme-snapped, whole-cell window the
    /// active line (and every wrap-off row) renders through. A 2-cell cluster straddling either clip
    /// edge is replaced by blank padding (never half-rendered, §2.4), and dim <c>❮</c>/<c>❯</c>
    /// continuation indicators occupy the edge cells whenever content extends beyond the visible span.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slideOffset"/> is negative.</exception>
    public ClippedRow ClipRow(int row, int slideOffset, int viewport)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(slideOffset);

        if (viewport <= 0)
            return new ClippedRow(slideOffset, 0, LeftIndicator: false, RightIndicator: false, []);

        int rowWidth = _rowWidth[row];
        bool leftClipped = slideOffset > 0;
        bool rightClipped = rowWidth > slideOffset + viewport;

        var cells = new ClipCell[viewport];
        Array.Fill(cells, ClipCell.Blank);

        // Only clusters that fit WHOLE inside the window are drawn; a straddling wide cluster leaves
        // its single visible cell blank (whole-cell discipline). Cells outside content stay blank.
        foreach (var cluster in _rowCells[row])
        {
            int start = cluster.Cell;
            int end = cluster.Cell + cluster.Width;
            if (start < slideOffset || end > slideOffset + viewport)
                continue; // outside the window, or a straddle at a clip edge → blank padding

            int p = start - slideOffset;
            cells[p] = new ClipCell(ClipCellKind.Head, cluster.Kind, cluster.SrcOffset) { Glyph = cluster.Glyph };
            if (cluster.Width == 2)
                cells[p + 1] = new ClipCell(ClipCellKind.Tail, cluster.Kind, cluster.SrcOffset) { Glyph = cluster.Glyph };
        }

        // The less/vim idiom: the edge cells signal clipped content on that side, overwriting whatever
        // sat under them (the 2-cell slack keeps the caret clear of the edges, so it is never hidden).
        // An indicator that lands on one half of a whole wide cluster blanks the orphaned other half,
        // so a clipped edge never leaves a half-rendered glyph behind either.
        if (leftClipped)
        {
            if (viewport > 1 && cells[0].Kind == ClipCellKind.Head && cells[1].Kind == ClipCellKind.Tail
                && cells[1].SrcOffset == cells[0].SrcOffset)
                cells[1] = ClipCell.Blank;
            cells[0] = ClipCell.Left;
        }

        if (rightClipped)
        {
            int last = viewport - 1;
            if (last > 0 && cells[last].Kind == ClipCellKind.Tail && cells[last - 1].Kind == ClipCellKind.Head
                && cells[last - 1].SrcOffset == cells[last].SrcOffset)
                cells[last - 1] = ClipCell.Blank;
            cells[last] = ClipCell.Right;
        }

        return new ClippedRow(slideOffset, viewport, leftClipped, rightClipped, cells);
    }

    /// <summary>
    /// The block-relative source offset at the content end of visual <paramref name="row"/> (after its
    /// last visible cluster) — the End-key landing (<see cref="ICaretMap.RowEndOffset"/>). On the
    /// revealed active line this is the un-wrapped line end; on an inactive wrapped row it is the wrap
    /// boundary.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int RowEndOffset(int row) => OffsetAt(row, RowWidth(row));

    /// <summary>
    /// The block-relative source offset at (<paramref name="row"/>, <paramref name="cell"/>) rounded to
    /// the cluster boundary at or before the cell — the mouse hit-test landing
    /// (<see cref="ICaretMap.NearestOffset"/>). Formatting hides marks, so a click maps through the
    /// display→source table like every other cell query.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is out of range.</exception>
    public int NearestOffset(int row, int cell) => OffsetAt(row, cell);

    /// <summary>The line owning <paramref name="srcOffset"/> — the largest <c>i</c> with <c>_lineSrcStart[i] ≤ srcOffset</c> (last on ties: a trailing empty line owns its start).</summary>
    private int LineOfSrcOffset(int srcOffset)
    {
        int lo = 0, hi = _lineTextLen.Length - 1;
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

/// <summary>One rendered grapheme cluster of a visual row: its start cell, cell width, block-relative source offset, and run kind.</summary>
internal readonly record struct RowCluster(int Cell, int Width, int SrcOffset, RunKind Kind)
{
    /// <summary>The display glyph for a <see cref="RunKind.Synthetic"/> cluster (a bullet, quote bar, or <c>↵</c>) whose source slice is its marker, not its glyph; <see langword="null"/> when the cluster draws its source.</summary>
    public string? Glyph { get; init; }
}

/// <summary>
/// The binding caret-visibility invariant (M2.WP5): the horizontal slide offset for a row is
/// <b>defined</b> as the function that keeps the caret within the visible span with two cells of edge
/// slack — the <c>TextBox</c> convention (<c>Cursorial.UI/Controls/TextPresenter.cs</c>). It is
/// recomputed on every caret move / insertion; a caret outside the visible span after any edit is a
/// test failure, not a UX nit.
/// </summary>
public static class HorizontalSlide
{
    /// <summary>
    /// The slide offset that keeps <paramref name="caretCell"/> visible inside a
    /// <paramref name="viewport"/>-cell window over a row of <paramref name="rowWidth"/> cells, given
    /// the <paramref name="previousOffset"/> the row was last drawn at (the offset is sticky: it moves
    /// only enough to bring the caret back within slack). The returned offset guarantees
    /// <c>0 ≤ caretCell − offset &lt; viewport</c> whenever the caret is on the row.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="previousOffset"/> or <paramref name="rowWidth"/> is negative.</exception>
    public static int Compute(int previousOffset, int caretCell, int rowWidth, int viewport)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(previousOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(rowWidth);

        if (viewport <= 0)
            return 0;

        int slack = viewport > 4 ? 2 : 0; // keep a little context around the caret (spec: 2-column edge slack)
        int offset = previousOffset;

        if (caretCell >= 0)
        {
            if (caretCell - slack < offset)
                offset = Math.Max(0, caretCell - slack);
            else if (caretCell + slack >= offset + viewport)
                offset = caretCell + slack - viewport + 1;
        }

        int maxScroll = Math.Max(0, rowWidth + 1 - viewport); // +1: room for the end caret
        return Math.Clamp(offset, 0, maxScroll);
    }
}

/// <summary>What a single published column of a <see cref="ClippedRow"/> holds.</summary>
public enum ClipCellKind
{
    /// <summary>Blank padding: empty space, or a wide cluster's half suppressed at a clip edge.</summary>
    Blank = 0,

    /// <summary>The first (or only) cell of a rendered grapheme cluster.</summary>
    Head,

    /// <summary>The trailing cell of a 2-cell cluster whose <see cref="Head"/> is the preceding column.</summary>
    Tail,

    /// <summary>The dim <c>❮</c> left continuation indicator (more content is clipped to the left).</summary>
    LeftIndicator,

    /// <summary>The dim <c>❯</c> right continuation indicator (more content is clipped to the right).</summary>
    RightIndicator,
}

/// <summary>
/// One published column of a clipped visual row. <see cref="SrcOffset"/> is the block-relative source
/// offset of a <see cref="ClipCellKind.Head"/>/<see cref="ClipCellKind.Tail"/> cell's cluster
/// (−1 for blanks and indicators); <see cref="Run"/> carries the cluster's kind so a presenter styles
/// text, revealed marks, and synthetic glyphs distinctly.
/// </summary>
/// <param name="Kind">What occupies the column.</param>
/// <param name="Run">The cluster's <see cref="RunKind"/> (meaningful only for head/tail cells).</param>
/// <param name="SrcOffset">The block-relative source offset for head/tail cells; −1 otherwise.</param>
public readonly record struct ClipCell(ClipCellKind Kind, RunKind Run, int SrcOffset)
{
    /// <summary>The display glyph for a synthetic head/tail cell (a <c>↵</c> on the active line) whose source slice is not what draws; <see langword="null"/> when the cell draws its source grapheme.</summary>
    public string? Glyph { get; init; }

    /// <summary>The glyph for the left continuation indicator (the less/vim idiom).</summary>
    public const char LeftGlyph = '❮';

    /// <summary>The glyph for the right continuation indicator (the less/vim idiom).</summary>
    public const char RightGlyph = '❯';

    internal static readonly ClipCell Blank = new(ClipCellKind.Blank, RunKind.Text, -1);
    internal static readonly ClipCell Left = new(ClipCellKind.LeftIndicator, RunKind.Text, -1);
    internal static readonly ClipCell Right = new(ClipCellKind.RightIndicator, RunKind.Text, -1);
}

/// <summary>
/// A visual row clipped to a horizontal viewport: the slide offset it was clipped at, its published
/// <see cref="Cells"/> (one per viewport column), and whether continuation indicators are present on
/// each side. The caret's published column is <c>unclippedCaretCell − <see cref="SlideOffset"/></c>,
/// which the caret-visibility invariant keeps inside <c>[0, <see cref="Viewport"/>)</c>.
/// </summary>
/// <param name="SlideOffset">The row-local cell the window starts at.</param>
/// <param name="Viewport">The window width in cells (equals <see cref="Cells"/>'s length).</param>
/// <param name="LeftIndicator">Whether a <c>❮</c> occupies the first column (content clipped to the left).</param>
/// <param name="RightIndicator">Whether a <c>❯</c> occupies the last column (content clipped to the right).</param>
/// <param name="Cells">The published columns, left to right.</param>
public readonly record struct ClippedRow(
    int SlideOffset,
    int Viewport,
    bool LeftIndicator,
    bool RightIndicator,
    IReadOnlyList<ClipCell> Cells);
