using CursorialEdit.Document.Model;

namespace CursorialEdit.Layout;

/// <summary>
/// Provides the current <see cref="BlockRunMap"/> for a block — the seam between presenters /
/// caret math (consumers) and whoever owns the layout cache (M1: <c>Views.BlockViewBridge</c>).
/// </summary>
public interface IBlockRunMapSource
{
    /// <summary>
    /// The run map for <paramref name="block"/> wrapped at <paramref name="wrapWidth"/> cells.
    /// Served from cache when the width matches the current layout width; a mismatched width
    /// (transient during a resize) builds an uncached one-off.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="block"/> is not in the block list (a stale consumer — fails loudly).</exception>
    BlockRunMap GetRunMap(BlockId block, int wrapWidth);
}
