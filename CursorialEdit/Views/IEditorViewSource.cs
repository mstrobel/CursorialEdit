using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Views;

/// <summary>
/// The pipeline↔surface seam an <see cref="EditorControl"/> attaches to (M2.WP7b): everything the
/// control and its <see cref="DocumentCaret"/> need from whichever bridge feeds them — heights and
/// identity (via <see cref="IBlockViewSource"/>), the presenter factory, the block list, the current
/// wrap width, the per-block caret map, the active line's publication slide, the reveal hook, the
/// selection sink, and the realized presenters for the selection-repaint walk.
/// </summary>
/// <remarks>
/// The M1 plain-text <see cref="BlockViewBridge"/> and the M2 markdown <c>MarkdownViewBridge</c> both
/// implement it, so <see cref="EditorControl.AttachDocument"/> and <see cref="DocumentCaret"/> drive
/// either surface unchanged. The plain bridge answers <see cref="ActiveSlide"/> as <c>0</c> and
/// <see cref="OnCaretPositioned"/> as a no-op (there is no reveal); the markdown bridge computes the
/// horizontal slide and re-reveals the caret's active line there.
/// </remarks>
public interface IEditorViewSource : IBlockViewSource
{
    /// <summary>The live block list the surface reconciles against.</summary>
    BlockList Blocks { get; }

    /// <summary>The current layout wrap width in cells (0 until the first viewport arrives).</summary>
    int WrapWidth { get; }

    /// <summary>
    /// The render mode (M2.WP10 / Decision 12): <see cref="ViewMode.Formatted"/> (WYSIWYG, the default) or
    /// <see cref="ViewMode.Raw"/> (verbatim source with token coloring). Setting it switches which presenter
    /// each block realizes into and re-estimates block heights; the caret's source anchor is mode-independent,
    /// so it is preserved across the toggle. The markdown bridge honors both modes; the plain-text bridge has
    /// no marks to hide, so raw and formatted render identically there.
    /// </summary>
    ViewMode ViewMode { get; set; }

    /// <summary>Creates (and registers) the presenter for the block at <paramref name="index"/> — the panel's block factory.</summary>
    UIElement CreatePresenter(int index);

    /// <summary>The caret map for the block at <paramref name="blockIndex"/> (the active block's map is reveal-aware; a table block's map is its cell-landing <c>TableCaretMap</c>).</summary>
    ICaretMap GetCaretMap(int blockIndex);

    /// <summary>
    /// The <see cref="TableModel"/> of the block at <paramref name="blockIndex"/> when it is a table
    /// (M3.WP4 cell-editing context), or <see langword="null"/> otherwise — the seam
    /// <see cref="DocumentCaret"/> routes table editing/navigation through. The plain-text surface always
    /// returns <see langword="null"/>.
    /// </summary>
    TableModel? GetTableModel(int blockIndex);

    /// <summary>
    /// The horizontal slide applied to the active block's revealed line (cells) at <paramref name="row"/> —
    /// <c>0</c> for a non-active block, the plain surface, or any row that is NOT the block's slid active
    /// line (only the active line is slid, so a click on another line of the active block gets no slide).
    /// </summary>
    int ActiveSlide(int blockIndex, int row);

    /// <summary>
    /// Notifies the bridge the caret moved to <paramref name="caret"/> — the markdown bridge reveals
    /// that block's line (slid to keep the caret visible) and hides every other block's marks; the
    /// plain bridge does nothing.
    /// </summary>
    void OnCaretPositioned(TextPosition caret);

    /// <summary>The document selection the presenters intersect at draw time (the caret installs itself here).</summary>
    ISelectionSource? SelectionSource { get; set; }

    /// <summary>The realized presenters by block id — the caret's selection-repaint walk iterates exactly the realized band.</summary>
    IEnumerable<KeyValuePair<BlockId, UIElement>> RealizedPresenters { get; }
}
