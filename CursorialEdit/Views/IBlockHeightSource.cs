namespace CursorialEdit.Views;

/// <summary>
/// The <see cref="DocumentPanel"/>'s pull-model view of block heights: an ordered list of document
/// blocks, each occupying a whole number of terminal rows. The panel derives its scroll extent from
/// a prefix sum over these heights and realizes only the blocks intersecting the scroll band.
/// </summary>
/// <remarks>
/// This is the M1.WP3 seam between the scrolling surface and the document model. WP3 consumes it
/// through a fixed-height stub; M1.WP7 replaces the <i>producer</i> with the real
/// <c>BlockList</c>-backed source (wrap-row prefix sums) without touching the panel. Heights are
/// cell rows — whole-cell integers, never fractional, never character counts.
/// </remarks>
public interface IBlockHeightSource
{
    /// <summary>The number of blocks in the document.</summary>
    int BlockCount { get; }

    /// <summary>The height of block <paramref name="index"/> in terminal rows (≥ 0).</summary>
    int GetBlockHeight(int index);

    /// <summary>
    /// Raised when <see cref="BlockCount"/> or any height changes. The panel invalidates its prefix
    /// sums and refines the published scroll extent through
    /// <see cref="Cursorial.UI.Controls.ScrollContentPresenter.InvalidateScrollExtent"/>.
    /// </summary>
    event Action? HeightsChanged;
}
