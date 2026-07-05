using Cursorial.Drawing.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The raw-source presenter (architecture Decision 12 / implementation-plan §7 WP10): in
/// <see cref="Views.ViewMode.Raw"/> the bridge selects this for <b>every</b> block regardless of kind, and
/// it renders the block's source lines <b>verbatim</b> — every markdown mark shown literally (<c>#</c>,
/// <c>**</c>, <c>`</c>, <c>-</c>, <c>&gt;</c>, fences…) with syntax-token coloring
/// (<see cref="RawMarkdownHighlighter"/>) — one visual row per source line. There is <b>no reveal, no
/// hidden marks, and no active-line slide</b>: the run map is <b>identity</b>
/// (<see cref="RunMapBuilder.BuildRaw"/>, source offset == display cell 1:1), so the caret walks raw
/// source directly and every existing caret/selection operation works unchanged. Toggling modes preserves
/// the caret's source anchor by construction (§4.2).
/// </summary>
/// <remarks>
/// Kind is irrelevant to raw rendering (it is all just source text), but the real block kind is threaded
/// through for honesty. Height is the raw line count (wrap-off — no wrap collapsing), so a block that
/// wrapped/folded in formatted mode occupies its literal line count here.
/// </remarks>
public sealed class RawSourcePresenter : LeafBlockPresenter
{
    /// <summary>Creates the raw-source presenter for a block of the given <paramref name="kind"/>.</summary>
    /// <param name="lines">The block's source lines (≥ 1), buffer-split with endings preserved.</param>
    /// <param name="kind">The block's structural kind (inert to raw rendering — every kind renders verbatim).</param>
    public RawSourcePresenter(IReadOnlyList<Line> lines, BlockKind kind)
        : base(lines, [], kind, headingLevel: null, WrapMode.NoWrap)
    {
    }

    /// <summary>The identity run map (source verbatim, 1:1 source↔cell) — reveal state is ignored in raw mode.</summary>
    protected override RunMap BuildMap(int width, int? activeLine) => RunMapBuilder.BuildRaw(Lines, width);

    /// <summary>The raw height: one row per source line (wrap-off, no collapsing).</summary>
    protected override int MeasuredRowCount(int width) => Lines.Count;

    /// <summary>Raw mode paints no active-block well — the surface is a clean, mode-independent source dump.</summary>
    protected override void PaintActiveWell(RenderContext context, int width, int rows) { }

    /// <inheritdoc/>
    protected override void RenderRows(RenderContext context, int width, int rows)
    {
        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        int activeRow = ActiveLine ?? -1;

        for (var row = 0; row < rows && row < Lines.Count; row++)
        {
            string text = Lines[row].Text;
            if (text.Length == 0)
                continue;

            // The caret's line is drawn horizontally slid so a source line wider than the viewport stays
            // reachable (raw lines do not wrap); every other line draws from column 0. The negative origin
            // scrolls the left of the active line off the render context's clip, mirroring the formatted
            // active-row slide. Every other line draws from column 0.
            int xOffset = row == activeRow ? -SlideOffset : 0;

            // Draw the literal source line, then overdraw the highlighted mark spans (same overdraw
            // discipline as CodeBlockPresenter: a running (char, cell) cursor, tokens in ascending order).
            context.DrawText(xOffset, row, text, foreground, null);

            int prevChar = 0;
            int prevCell = xOffset;
            foreach (var token in RawMarkdownHighlighter.Tokenize(text))
            {
                if (token.Start < prevChar || token.Start >= text.Length || token.Length <= 0)
                    continue;

                int length = Math.Min(token.Length, text.Length - token.Start);
                prevCell += GraphemeWidth.StringWidth(text.AsSpan(prevChar, token.Start - prevChar));
                prevChar = token.Start;
                context.DrawText(prevCell, row, text.AsSpan(token.Start, length), MarkdownStyles.RawMarkBrush(this, token.Class), null);
            }
        }
    }
}
