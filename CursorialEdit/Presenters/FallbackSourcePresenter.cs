using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Presenters;

/// <summary>
/// The fallback presenter (§2.4): renders a block's raw source lines as <b>dimmed literal</b> text,
/// one row per source line, never interpreting them and never crashing on any block. It is the
/// renderer for raw HTML (passed through dimmed, never an HTML subsystem — §2.4) and the standing
/// fallback for constructs whose dedicated presenters have not landed yet: tables (M3) and the
/// extension constructs — alerts, definition lists, footnotes, math, link-reference definitions (M4).
/// </summary>
/// <remarks>
/// Because it draws source verbatim it needs no run map and no inline realization: <see cref="Kind"/>
/// can be anything, the block may be malformed, and rendering is a bounded per-line literal draw. A
/// caret on any line is the ordinary source position; there are no marks to reveal.
/// </remarks>
public sealed class FallbackSourcePresenter : LeafBlockPresenter
{
    /// <summary>Creates the fallback presenter for a block of the given <paramref name="kind"/>.</summary>
    public FallbackSourcePresenter(IReadOnlyList<Line> lines, BlockKind kind)
        : base(lines, [], kind, headingLevel: null, WrapMode.NoWrap)
    {
    }

    /// <inheritdoc/>
    protected override int MeasuredRowCount(int width) => Lines.Count;

    /// <inheritdoc/>
    protected override void RenderRows(RenderContext context, int width, int rows)
    {
        for (var row = 0; row < rows && row < Lines.Count; row++)
        {
            string text = Lines[row].Text;
            if (text.Length > 0)
                context.DrawText(0, row, text, MarkdownStyles.FrontMatterBrush(this), null, MarkdownStyles.Dim(this));
        }
    }
}
