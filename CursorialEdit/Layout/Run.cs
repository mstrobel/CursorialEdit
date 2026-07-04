namespace CursorialEdit.Layout;

/// <summary>
/// How a <see cref="Run"/>'s cells relate to source text (architecture Decision 8). M1's plain
/// text produces only <see cref="Text"/>; M2 adds <c>HiddenMark</c> (zero-width runs at true
/// source positions), <c>RevealedMark</c> (marks shown on the active line), and <c>Synthetic</c>
/// (cells with no one-to-one source: borders, quote bars, list bullets).
/// </summary>
public enum RunKind
{
    /// <summary>Ordinary visible source text: cells map one-to-one onto grapheme clusters of the source slice.</summary>
    Text = 0,
}

/// <summary>
/// One horizontal run of a block's visual row (architecture Decision 8): the exact shape the
/// caret, selection, and hit-testing map through. Spans are <b>block-relative</b> — offsets into
/// the block's source snapshot (its lines serialized with their terminators), never absolute —
/// so an unedited block's runs stay valid across every edit elsewhere in the document.
/// </summary>
/// <param name="SrcStart">Block-relative UTF-16 offset of the run's source slice.</param>
/// <param name="SrcLen">Length of the source slice in UTF-16 code units (0 for zero-width kinds).</param>
/// <param name="Col">The run's first display cell, row-local (cell 0 is the block box's left edge).</param>
/// <param name="Width">The run's display width in cells (whole-cell <see cref="Cursorial.Text.GraphemeWidth"/> measure).</param>
/// <param name="Kind">How the cells relate to the source slice.</param>
public readonly record struct Run(int SrcStart, int SrcLen, int Col, int Width, RunKind Kind);
