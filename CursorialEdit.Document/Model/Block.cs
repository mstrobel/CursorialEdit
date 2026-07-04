using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MdBlock = Markdig.Syntax.Block;

namespace CursorialEdit.Document.Model;

/// <summary>
/// One document block: an identity, a kind, and a <b>line count</b> — deliberately <i>not</i> a
/// start line (architecture Decision 8's prefix-sum discipline). Start lines are prefix sums over
/// the owning <see cref="BlockList"/>, recomputed from the edit point, so an edit inside block
/// <i>i</i> re-forms only block <i>i</i> while everything after it shifts implicitly with zero
/// per-block rewriting. A block's source lines are reached through the list:
/// <c>buffer.GetLine(list.GetStartLine(index) + k)</c> for <c>k</c> in <c>[0, LineCount)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Blocks are immutable value-carriers; a re-formed block is a <b>new instance carrying the same
/// <see cref="Id"/></b> (identity lives in the id, never in the object reference). The M2
/// <see cref="MarkdigBlockProducer"/> hangs the Markdig AST reference and the re-adoption inputs
/// (<see cref="ContentStamp"/>, <see cref="ContentHash"/>) off this class; the degenerate M1
/// <see cref="PlainTextBlockProducer"/> uses the bare 3-argument constructor and carries none of it.
/// </para>
/// <para>
/// <b>Lazy inlines (Decision 5).</b> <see cref="InlineRuns"/> is projected from
/// <see cref="MarkdigBlock"/> only on first access and cached; segmentation and re-adoption never
/// touch it, so open/paste cost is bounded to block segmentation plus whatever the render band
/// actually realizes. The one memoized field is the sole mutable state on an otherwise-immutable
/// block; like the rest of the document core it is UI-thread-only, so no synchronization is needed.
/// </para>
/// </remarks>
public sealed class Block
{
    private IReadOnlyList<InlineRun>? _inlineRuns;

    /// <summary>Creates a degenerate block with no Markdig backing (the M1 plain-text producer and unit fixtures).</summary>
    /// <param name="id">The producer-allocated identity.</param>
    /// <param name="kind">The structural kind.</param>
    /// <param name="lineCount">The number of source lines this block owns (≥ 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineCount"/> &lt; 1.</exception>
    public Block(BlockId id, BlockKind kind, int lineCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineCount, 1);

        Id = id;
        Kind = kind;
        LineCount = lineCount;
    }

    /// <summary>
    /// Creates a Markdig-backed block. Called only by <see cref="MarkdigBlockProducer"/>, which
    /// supplies the parse-time inputs to Decision 4's re-adoption rule.
    /// </summary>
    /// <param name="id">The re-adopted or freshly-allocated identity.</param>
    /// <param name="kind">The kind mapped from <paramref name="markdigBlock"/>.</param>
    /// <param name="lineCount">The number of source lines this block owns (≥ 1).</param>
    /// <param name="markdigBlock">The backing Markdig top-level block (for span/inline access); never <see langword="null"/> here.</param>
    /// <param name="sourceStartOffset">Absolute UTF-16 offset of this block's first source line at parse time — the origin block-relative inline offsets are measured from (Decision 8).</param>
    /// <param name="contentStamp">The maximum line <see cref="Buffer.Line.Version"/> over this block's lines at (re)parse — the re-adoption input (Decision 4).</param>
    /// <param name="contentHash">An order-sensitive hash of this block's source text — the secondary re-adoption check for moved-but-unchanged blocks (never the primary key).</param>
    /// <param name="headingLevel">The heading level (1–6) when <paramref name="kind"/> is <see cref="BlockKind.Heading"/>; otherwise <see langword="null"/>.</param>
    /// <param name="fenceInfo">The fence info string when <paramref name="kind"/> is <see cref="BlockKind.FencedCode"/> (or the math fence); otherwise <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineCount"/> &lt; 1.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="markdigBlock"/> is <see langword="null"/>.</exception>
    public Block(
        BlockId id,
        BlockKind kind,
        int lineCount,
        MdBlock markdigBlock,
        int sourceStartOffset,
        int contentStamp,
        ulong contentHash,
        int? headingLevel,
        string? fenceInfo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineCount, 1);
        ArgumentNullException.ThrowIfNull(markdigBlock);

        Id = id;
        Kind = kind;
        LineCount = lineCount;
        MarkdigBlock = markdigBlock;
        SourceStartOffset = sourceStartOffset;
        ContentStamp = contentStamp;
        ContentHash = contentHash;
        HeadingLevel = headingLevel;
        FenceInfo = fenceInfo;
    }

    /// <summary>The block's stable identity (see <see cref="BlockId"/>).</summary>
    public BlockId Id { get; }

    /// <summary>The block's structural kind.</summary>
    public BlockKind Kind { get; }

    /// <summary>
    /// The number of consecutive source lines this block owns. Every source line belongs to
    /// exactly one block — the <see cref="BlockList"/> tiles the document.
    /// </summary>
    public int LineCount { get; }

    /// <summary>
    /// The backing Markdig top-level block — the entry point for span and inline-AST access
    /// (presenters may traverse it; that is sanctioned app-side Markdig use, never promotable).
    /// <see langword="null"/> for a degenerate (non-Markdig) block.
    /// </summary>
    public MdBlock? MarkdigBlock { get; }

    /// <summary>
    /// The absolute UTF-16 offset of this block's first source line <b>at the parse that produced
    /// this instance</b> — the origin <see cref="InlineRuns"/> are block-relative to (Decision 8).
    /// Because both this origin and the Markdig spans it subtracts move together under any later
    /// shift, a re-adopted block's runs stay valid without rewriting; consumers re-absolutize with
    /// the block's <i>current</i> start line from the list's prefix sums.
    /// </summary>
    public int SourceStartOffset { get; }

    /// <summary>
    /// The maximum line <see cref="Buffer.Line.Version"/> over this block's lines, recorded at the
    /// (re)parse that produced this instance — the input to Decision 4's re-adoption rule. A line is
    /// unmodified since this block was parsed iff its version is ≤ this stamp.
    /// </summary>
    public int ContentStamp { get; }

    /// <summary>
    /// An order-sensitive hash of this block's source text at parse time. Decision 4's
    /// <b>secondary</b> re-adoption check: it detects a moved-but-unchanged block whose lines were
    /// all rewritten (so no unmodified line anchors it). It is never the primary identity key — the
    /// block being typed in changes its hash every keystroke.
    /// </summary>
    public ulong ContentHash { get; }

    /// <summary>The heading level (1–6) when <see cref="Kind"/> is <see cref="BlockKind.Heading"/>; otherwise <see langword="null"/>.</summary>
    public int? HeadingLevel { get; }

    /// <summary>The fenced-code info string when <see cref="Kind"/> is <see cref="BlockKind.FencedCode"/> (or a math fence); otherwise <see langword="null"/>.</summary>
    public string? FenceInfo { get; }

    /// <summary>
    /// Whether <see cref="InlineRuns"/> has been realized yet — a probe for the lazy-inline
    /// invariant (Decision 5): a block emerges from segmentation/re-adoption with this
    /// <see langword="false"/>, and it becomes <see langword="true"/> only when something reads
    /// <see cref="InlineRuns"/>.
    /// </summary>
    public bool InlineRunsRealized => _inlineRuns is not null;

    /// <summary>
    /// The block's inline runs, projected from <see cref="MarkdigBlock"/>'s inline AST on first
    /// access and cached (Decision 5). Empty for a degenerate block, a block with no inline content
    /// (rules, blank paragraphs), or a code/front-matter block (whose bodies are not inline-parsed).
    /// Offsets are block-relative (see <see cref="InlineRun"/>).
    /// </summary>
    public IReadOnlyList<InlineRun> InlineRuns => _inlineRuns ??= RealizeInlineRuns();

    /// <summary>Compact diagnostic form (<c>#7 Heading ×2</c>).</summary>
    public override string ToString() => $"{Id} {Kind} ×{LineCount}";

    private IReadOnlyList<InlineRun> RealizeInlineRuns()
    {
        if (MarkdigBlock is null)
            return [];

        // A leaf block's inline tree hangs off LeafBlock.Inline; a container block (quote, list,
        // alert, definition list) holds its inlines on nested leaves. Descendants() on the container
        // walks into those leaves; on a leaf it does not, so the leaf's own Inline is walked directly.
        IEnumerable<Inline> inlines = MarkdigBlock switch
        {
            LeafBlock { Inline: { } inline } => inline.Descendants().OfType<Inline>(),
            ContainerBlock container => container.Descendants().OfType<Inline>(),
            _ => [],
        };

        var runs = new List<InlineRun>();
        foreach (var inline in inlines)
        {
            var span = inline.Span;
            if (span.IsEmpty || span.Length <= 0)
                continue;

            var kind = ClassifyInline(inline);
            if (kind is { } k)
                runs.Add(new InlineRun(span.Start - SourceStartOffset, span.Length, k));
        }

        return runs;
    }

    private static InlineRunKind? ClassifyInline(Inline inline) => inline switch
    {
        // Delimiter runs and transient parse scaffolding carry no reproducible run; skip them.
        DelimiterInline => null,
        LiteralInline => InlineRunKind.Text,
        EmphasisInline e => e.DelimiterChar == '~' ? InlineRunKind.Strikethrough
            : e.DelimiterCount >= 2 ? InlineRunKind.Strong
            : InlineRunKind.Emphasis,
        CodeInline => InlineRunKind.Code,
        LinkInline { IsAutoLink: true } => InlineRunKind.AutoLink,
        LinkInline { IsImage: true } => InlineRunKind.Image,
        LinkInline => InlineRunKind.Link,
        AutolinkInline => InlineRunKind.AutoLink,
        Markdig.Extensions.Mathematics.MathInline => InlineRunKind.Math,
        Markdig.Extensions.TaskLists.TaskList => InlineRunKind.TaskMarker,
        Markdig.Extensions.Footnotes.FootnoteLink { IsBackLink: false } => InlineRunKind.FootnoteReference,
        HtmlInline or HtmlEntityInline => InlineRunKind.Html,
        LineBreakInline => InlineRunKind.LineBreak,
        _ => InlineRunKind.Other,
    };
}
