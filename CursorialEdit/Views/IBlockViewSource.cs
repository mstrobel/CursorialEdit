using Cursorial.Rendering;

namespace CursorialEdit.Views;

/// <summary>
/// The M1.WP7 <b>additive</b> extension of <see cref="IBlockHeightSource"/> (the WP3 seam stays
/// frozen; the panel type-tests for this interface and behaves exactly as before when the source
/// does not implement it — the spike's stub path is untouched). It adds the two capabilities the
/// real pipeline needs from the panel:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Block identity</b> — lets <see cref="DocumentPanel"/> remap its realized elements to
/// their blocks' new indices when a <c>BlockListChange</c> shifts the list (presenter reuse
/// across splits/merges instead of tear-down-and-recreate). The panel performs the remap inside
/// its <see cref="IBlockHeightSource.HeightsChanged"/> handling, so an implementation must raise
/// that event for <i>every</i> structural change (block added/removed/height moved).</item>
/// <item><b>Viewport observation</b> — the wrap-width driver. Soft-wrap heights depend on the
/// content width, which only the scroll seam knows (<c>IScrollContentHost.SetViewport</c>); the
/// panel forwards it here so the source can re-derive heights and raise
/// <see cref="IBlockHeightSource.HeightsChanged"/> when wrapping actually changed.</item>
/// </list>
/// Identities travel as raw <see cref="long"/>s so the panel stays document-model-agnostic
/// (they are <c>BlockId.Value</c>s on the pipeline side).
/// </remarks>
internal interface IBlockViewSource : IBlockHeightSource
{
    /// <summary>The stable identity of the block currently at <paramref name="index"/>.</summary>
    long GetBlockIdentity(int index);

    /// <summary>The current index of the block with <paramref name="identity"/>, or −1 when it left the list.</summary>
    int IndexOfBlock(long identity);

    /// <summary>
    /// Called by the panel whenever the scroll seam publishes a new viewport
    /// (<see cref="Cursorial.UI.Controls.IScrollContentHost.SetViewport"/>) — the source derives
    /// its wrap width from the viewport's columns.
    /// </summary>
    void OnViewportChanged(Size viewport);
}
