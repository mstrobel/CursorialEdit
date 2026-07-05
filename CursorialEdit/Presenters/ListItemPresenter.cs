using Cursorial.Rendering.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The list presenter (§2.1): each item line renders a bullet (<c>•</c>, the normalized unordered
/// marker) or its ordered number, followed by the item text; nesting indent is the item's leading
/// spaces carried through as content. The marker is a <see cref="RunKind.Synthetic"/> run mapping to
/// its <c>- </c>/<c>1. </c> marker source (an atomic single caret stop — a graft from leverage, §2.4),
/// derived in <see cref="RunMapBuilder"/>. When an item line is active, its raw marker reveals.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>block-level</b> presenter: it renders all of a top-level list block's item lines (the
/// block model tiles a list as one <see cref="BlockKind.List"/> block); the per-item decomposition
/// (architecture §2.3, "a 300-item task list re-rasters one item per keystroke") is a WP7b bridge
/// concern and does not change this rendering. The name follows the plan's presenter roster.
/// </para>
/// <para>
/// <b>Task-list checkboxes are deferred to M4.</b> A <c>- [ ] </c>/<c>- [x] </c> item renders its
/// bullet plus the literal <c>[ ]</c>/<c>[x]</c> text (the checkbox <see cref="InlineRunKind.TaskMarker"/>
/// is not a mark, so it stays visible) — the raw-marker fallback the plan sanctions until M4 renders
/// the checkbox Icon and wires toggling.
/// </para>
/// </remarks>
public sealed class ListItemPresenter : LeafBlockPresenter
{
    /// <summary>Creates the presenter for a list block.</summary>
    public ListItemPresenter(
        IReadOnlyList<Line> lines,
        IReadOnlyList<InlineRun> inlineRuns,
        WrapMode wrapMode = WrapMode.WordWrap)
        : base(lines, inlineRuns, BlockKind.List, headingLevel: null, wrapMode)
    {
    }
}
