using Cursorial.Rendering.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The presenter for paragraphs and <b>headings</b> (ATX <c>#</c>..<c>######</c> and setext
/// <c>===</c>/<c>---</c>), §2.1. It is the reveal-on-edit spike that retired risk R1 (M2.WP6), now
/// generalized onto <see cref="LeafBlockPresenter"/>: inactive rows render the formatted text with
/// syntax marks hidden and emphasis/strong/inline-code/strikethrough/links styled; the active row
/// reveals its marks, slid and grapheme-snapped clipped. Headings add a distinct color + weight per
/// level (<see cref="MarkdownStyles.HeadingBrush"/>/<see cref="MarkdownStyles.HeadingAttributes"/>);
/// the ATX <c>#</c> prefix and a setext underline line are hidden marks that reveal on the active
/// line (both derived in <see cref="RunMapBuilder"/>).
/// </summary>
public sealed class ParagraphPresenter : LeafBlockPresenter
{
    /// <summary>Creates the presenter for a paragraph or heading block.</summary>
    /// <param name="lines">The block's source lines (≥ 1), buffer-split with endings preserved.</param>
    /// <param name="inlineRuns">The block's block-relative inline runs (<see cref="Block.InlineRuns"/>).</param>
    /// <param name="kind">The block's kind (<see cref="BlockKind.Paragraph"/> or <see cref="BlockKind.Heading"/>).</param>
    /// <param name="headingLevel">The heading level (1–6) when <paramref name="kind"/> is <see cref="BlockKind.Heading"/>.</param>
    /// <param name="wrapMode">Wrap-on (<see cref="WrapMode.WordWrap"/>, default) or wrap-off (<see cref="WrapMode.NoWrap"/>).</param>
    public ParagraphPresenter(
        IReadOnlyList<Line> lines,
        IReadOnlyList<InlineRun> inlineRuns,
        BlockKind kind,
        int? headingLevel = null,
        WrapMode wrapMode = WrapMode.WordWrap)
        : base(lines, inlineRuns, kind, headingLevel, wrapMode)
    {
    }
}
