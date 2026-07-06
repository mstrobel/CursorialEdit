using CursorialEdit.Document.Model;

namespace CursorialEdit.Layout;

/// <summary>
/// The per-presenter view of the document-level selection (architecture §2.3/§2.4): presenters
/// intersect the selection with their block <b>at draw time</b>, so a selection change re-rasters
/// only the presenters whose intersection actually changed. Offsets are <b>block-relative</b> —
/// UTF-16 offsets into the block's source snapshot (the same coordinate space as
/// <see cref="Run.SrcStart"/>), never absolute — so the contract matches the run maps presenters
/// already draw from.
/// </summary>
public interface ISelectionSource
{
    /// <summary>
    /// The selection's intersection with <paramref name="block"/> as a half-open block-relative
    /// range, or <see langword="null"/> when the selection is empty or does not touch the block.
    /// </summary>
    (int Start, int End)? GetSelection(BlockId block);

    /// <summary>
    /// The rectangular whole-cell selection (M3.WP8, spec §5.4) when <paramref name="block"/> is a table whose
    /// two selection ends fall in <b>different cells of that same table</b>, or <see langword="null"/> otherwise —
    /// a single-cell selection (the ordinary in-cell text selection, drawn via <see cref="GetSelection"/>) and a
    /// selection that leaves the table (an ordinary document selection — the transition rule) both return
    /// <see langword="null"/>. A table presenter reads this to highlight <b>whole cells</b> instead of the covered
    /// source span; when it is <see langword="null"/> the presenter falls back to the <see cref="GetSelection"/>
    /// per-cell text highlight.
    /// </summary>
    CellRect? GetCellRect(BlockId block);
}
