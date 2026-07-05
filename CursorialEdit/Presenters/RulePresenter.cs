using Cursorial.Rendering.Text;
using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The horizontal-rule presenter (§2.1): a thematic break (<c>---</c>/<c>***</c>/<c>___</c>) renders
/// as a full-width rule of box-drawing <c>─</c> cells. The source marks reveal on the active line —
/// when the caret sits on the rule it shows its literal <c>---</c> source (slid + clipped through the
/// base <see cref="LeafBlockPresenter"/> reveal path), the standard hidden-mark→revealed behavior.
/// </summary>
public sealed class RulePresenter : LeafBlockPresenter
{
    private const char RuleGlyph = '─';

    /// <summary>Creates the presenter for a thematic-break block.</summary>
    public RulePresenter(IReadOnlyList<Line> lines)
        : base(lines, [], BlockKind.ThematicBreak, headingLevel: null, WrapMode.NoWrap)
    {
    }

    /// <inheritdoc/>
    protected override void RenderRows(RenderContext context, int width, int rows)
    {
        if (ActiveLine is not null)
        {
            base.RenderRows(context, width, rows); // reveal the literal `---`, slid + clipped
            return;
        }

        if (rows > 0 && width > 0)
            context.DrawText(0, 0, new string(RuleGlyph, width), MarkdownStyles.RuleBrush(this));
    }
}
