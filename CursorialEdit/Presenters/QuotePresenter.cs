using Cursorial.Drawing.Media;
using Cursorial.Rendering.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// The blockquote presenter (§2.1): each line renders a <c>▌</c> quote bar per nesting level (a
/// <see cref="RunKind.Synthetic"/> glyph derived in <see cref="RunMapBuilder"/> — one bar per
/// <c>&gt;</c> level, e.g. <c>&gt; &gt; </c> → <c>▌▌</c>) with the body text following. The bar maps
/// atomically to its <c>&gt; </c> marker source, so caret-left from the body lands before it as one
/// stop. When a line is the active line, its <c>&gt;</c> marker reveals as literal source (the bar
/// gives way to the revealed <c>&gt; </c>) while every other line keeps its bar — architecture §2.4
/// "blockquotes reveal only the active line's <c>&gt;</c>".
/// </summary>
/// <remarks>
/// This is a <b>block-level</b> presenter: it renders all of the top-level quote block's lines (the
/// block model tiles a blockquote as one <see cref="BlockKind.Quote"/> block). The eventual per-line
/// decomposition (architecture §2.3) is a WP7b bridge concern and does not change this rendering.
/// </remarks>
public sealed class QuotePresenter : LeafBlockPresenter
{
    /// <summary>Creates the presenter for a blockquote block.</summary>
    public QuotePresenter(
        IReadOnlyList<Line> lines,
        IReadOnlyList<InlineRun> inlineRuns,
        WrapMode wrapMode = WrapMode.WordWrap)
        : base(lines, inlineRuns, BlockKind.Quote, headingLevel: null, wrapMode)
    {
    }

    /// <inheritdoc/>
    protected override (IBrush Foreground, CellStyle Style) StyleForSynthetic(in Run run, IBrush inherited)
        => (MarkdownStyles.QuoteBarBrush(this), CellStyle.Default);
}
