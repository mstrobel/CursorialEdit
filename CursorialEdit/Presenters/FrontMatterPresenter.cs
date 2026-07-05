using Cursorial.Drawing.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Presenters;

/// <summary>
/// The YAML front-matter presenter (§2.3, owned here per plan resolution 5): the metadata block
/// (<c>---</c>…<c>---</c> at file start) renders in a dim "front matter" style, <b>folded by default</b>
/// to a single summary row with an expand affordance. Expanding changes the block's height, so
/// <see cref="HeightChanged"/> fires — the WP7b host wires it to the panel's
/// <c>ScrollContentPresenter.InvalidateScrollExtent()</c> path; the harness just toggles and re-measures.
/// M4 adds only the fold-toggle command wiring and §2.3 conformance (resolution 5).
/// </summary>
public sealed class FrontMatterPresenter : LeafBlockPresenter
{
    private const char CollapsedChevron = '▸';
    private const char ExpandedChevron = '▾';

    private bool _expanded;

    /// <summary>Creates the presenter for a front-matter block (folded by default).</summary>
    public FrontMatterPresenter(IReadOnlyList<Line> lines)
        : base(lines, [], BlockKind.FrontMatter, headingLevel: null, WrapMode.NoWrap)
    {
    }

    /// <summary>Fired when a fold toggle changes the block's height — the host re-invalidates the scroll extent.</summary>
    public event Action? HeightChanged;

    /// <summary>Whether the front matter is expanded (all lines shown) or folded (one summary row).</summary>
    public bool IsExpanded => _expanded;

    /// <summary>Toggles the fold; re-measures and signals <see cref="HeightChanged"/>.</summary>
    public void ToggleFold() => SetExpanded(!_expanded);

    /// <summary>Sets the fold state; re-measures and signals <see cref="HeightChanged"/> when it changed.</summary>
    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;

        _expanded = expanded;
        InvalidateMeasure();
        InvalidateVisual();
        HeightChanged?.Invoke();
    }

    /// <inheritdoc/>
    protected override int MeasuredRowCount(int width) => _expanded ? Lines.Count : 1;

    /// <inheritdoc/>
    protected override void RenderRows(RenderContext context, int width, int rows)
    {
        if (!_expanded)
        {
            context.DrawText(0, 0, $"{CollapsedChevron} front matter", MarkdownStyles.FrontMatterBrush, null, MarkdownStyles.Dim);
            return;
        }

        for (var row = 0; row < rows && row < Lines.Count; row++)
            context.DrawText(0, row, Lines[row].Text, MarkdownStyles.FrontMatterBrush, null, MarkdownStyles.Dim);

        // The collapse affordance sits in the top-right corner — but only when it would not overwrite the
        // first line's content (a full-width metadata value keeps its last cell).
        if (rows > 0 && width > 0 && Lines.Count > 0 && GraphemeWidth.StringWidth(Lines[0].Text) <= width - 2)
            context.DrawText(width - 1, 0, ExpandedChevron.ToString(), MarkdownStyles.FrontMatterBrush, null, MarkdownStyles.Dim);
    }
}
