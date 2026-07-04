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
}
