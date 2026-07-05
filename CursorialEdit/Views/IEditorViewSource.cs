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

    /// <summary>Creates (and registers) the presenter for the block at <paramref name="index"/> — the panel's block factory.</summary>
    UIElement CreatePresenter(int index);

    /// <summary>The caret map for the block at <paramref name="blockIndex"/> (the active block's map is reveal-aware).</summary>
    ICaretMap GetCaretMap(int blockIndex);

    /// <summary>The horizontal slide applied to the active block's revealed line (cells) — <c>0</c> for a non-active block or the plain surface.</summary>
    int ActiveSlide(int blockIndex);

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
