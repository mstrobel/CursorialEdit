namespace CursorialEdit.Layout;

/// <summary>
/// The caret's view of one block's layout (M2.WP7b): the minimal source↔cell mapping surface the
/// document caret and selection consume, abstracted over the M1 <see cref="BlockRunMap"/> (raw
/// per-row text) and the M2 <see cref="RunMap"/> (formatted, mark-hiding, reveal-aware) so
/// <c>DocumentCaret</c> drives either the plain-text surface or the markdown surface unchanged.
/// </summary>
/// <remarks>
/// Every offset is <b>block-relative</b> (the <see cref="Run.SrcStart"/> coordinate space) and every
/// cell is <b>unclipped</b> (cell 0 is the block box's left edge — the caret owner subtracts the
/// active line's horizontal slide for publication). Both implementations already carry
/// <see cref="Locate"/>/<see cref="OffsetAt"/> in this exact shape; the two extra members below let
/// the caret's End motion and mouse hit-test stay map-agnostic.
/// </remarks>
public interface ICaretMap
{
    /// <summary>The number of visual rows (the block's rendered height in terminal rows, ≥ 1).</summary>
    int RowCount { get; }

    /// <summary>
    /// Maps a block-relative source <paramref name="srcOffset"/> to its unclipped visual (row, cell).
    /// A soft-wrap boundary is resolved by <paramref name="endAffinity"/> (the earlier row's end when
    /// <see langword="true"/>).
    /// </summary>
    (int Row, int Cell) Locate(int srcOffset, bool endAffinity = false);

    /// <summary>
    /// Maps a visual (<paramref name="row"/>, <paramref name="cell"/>) back to the block-relative
    /// source offset at the grapheme-cluster boundary at or before the cell (the goal-column landing).
    /// </summary>
    int OffsetAt(int row, int cell);

    /// <summary>The block-relative source offset at the content end of visual <paramref name="row"/> — the End-key landing.</summary>
    int RowEndOffset(int row);

    /// <summary>
    /// The block-relative source offset nearest to (<paramref name="row"/>, <paramref name="cell"/>) —
    /// the mouse hit-test landing (rounded to the closer cluster boundary where the map can).
    /// </summary>
    int NearestOffset(int row, int cell);

    /// <summary>
    /// Whether visual <paramref name="row"/> carries a caret stop — <see langword="true"/> for every ordinary
    /// text/formatted row, <see langword="false"/> for a row the caret can never rest on (a table's
    /// box-drawing border / delimiter rows). Vertical motion steps <b>past</b> non-caret rows in the travel
    /// direction rather than snapping back, so the caret is never trapped inside a table (M3.WP4 bug 1).
    /// </summary>
    bool HasCaretStop(int row) => true;
}
