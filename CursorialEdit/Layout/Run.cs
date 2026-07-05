namespace CursorialEdit.Layout;

/// <summary>
/// How a <see cref="Run"/>'s cells relate to source text (architecture Decision 8). M1's plain
/// text produces only <see cref="Text"/>; M2.WP5 adds the three overlay kinds that make the
/// source↔cell mapping <b>total</b> while still hiding syntax in the rendered view.
/// </summary>
public enum RunKind
{
    /// <summary>Ordinary visible source text: cells map one-to-one onto grapheme clusters of the source slice.</summary>
    Text = 0,

    /// <summary>
    /// A syntax mark hidden in the rendered view (emphasis <c>*</c>, code <c>`</c>, the link
    /// <c>[</c>…<c>](url)</c> scaffolding, an ATX heading's <c>#</c> prefix). It occupies
    /// <b>zero visible cells</b> (<see cref="Run.Width"/> is 0) but keeps its <b>true source
    /// position</b> (<see cref="Run.SrcStart"/>/<see cref="Run.SrcLen"/> span the mark's source),
    /// so every source offset still maps to a cell and the caret walks across it structurally
    /// (architecture §2.4). The mapping is total precisely because these runs are present.
    /// </summary>
    HiddenMark,

    /// <summary>
    /// A mark shown on the <b>active line</b> (reveal-on-edit, §4.1): the same syntax a
    /// <see cref="HiddenMark"/> hides, but rendered — it occupies cells
    /// (<see cref="Run.Width"/> &gt; 0, its source slice measured whole-cell) so the editor
    /// surfaces the literal syntax under the caret without re-wrapping the line.
    /// </summary>
    RevealedMark,

    /// <summary>
    /// A glyph with no one-to-one source: a list bullet (<c>•</c>), a blockquote bar (<c>▌</c>),
    /// a hard-break arrow (<c>↵</c>), a checkbox. It either <b>maps to its marker source</b>
    /// (a bullet run spans <c>"- "</c> so caret-left from the item text lands before the item as
    /// exactly one stop — a graft from leverage, §2.4) or carries <see cref="Run.SrcLen"/> = 0
    /// (a decoration with no caret stop). It is atomic: the whole marker is a single caret stop.
    /// </summary>
    Synthetic,
}

/// <summary>
/// One horizontal run of a block's visual row (architecture Decision 8): the exact shape the
/// caret, selection, and hit-testing map through. Spans are <b>block-relative</b> — offsets into
/// the block's source snapshot (its lines serialized with their terminators), never absolute —
/// so an unedited block's runs stay valid across every edit elsewhere in the document.
/// </summary>
/// <param name="SrcStart">Block-relative UTF-16 offset of the run's source slice.</param>
/// <param name="SrcLen">Length of the source slice in UTF-16 code units (0 for zero-width kinds).</param>
/// <param name="Col">The run's first display cell, row-local (cell 0 is the block box's left edge).</param>
/// <param name="Width">The run's display width in cells (whole-cell <see cref="Cursorial.Text.GraphemeWidth"/> measure).</param>
/// <param name="Kind">How the cells relate to the source slice.</param>
public readonly record struct Run(int SrcStart, int SrcLen, int Col, int Width, RunKind Kind)
{
    /// <summary>
    /// The display glyph a <see cref="RunKind.Synthetic"/> run draws (a bullet <c>•</c>, a quote bar
    /// <c>▌</c>, a hard-break <c>↵</c>, later a checkbox) — the substrate WP6 finding 1 flagged as the
    /// blocker for quotes/lists: a synthetic run's <see cref="SrcStart"/>/<see cref="SrcLen"/> point at
    /// its <b>marker source</b> (<c>"- "</c>, <c>"&gt; "</c>) for caret mapping, so the source slice is
    /// <i>not</i> what should be drawn. The presenter draws this glyph instead. <see langword="null"/>
    /// for <see cref="RunKind.Text"/>/<see cref="RunKind.HiddenMark"/>/<see cref="RunKind.RevealedMark"/>
    /// runs, which draw (or hide) their source slice directly. Init-only and defaulted so the M1
    /// <see cref="BlockRunMap"/> and every existing <c>new Run(…)</c> stay value-equal.
    /// </summary>
    public string? Glyph { get; init; }

    /// <summary>
    /// The inline formatting (bold/italic/strikethrough/code/link) that applies to a
    /// <see cref="RunKind.Text"/>/<see cref="RunKind.RevealedMark"/> run's cells, derived from the
    /// enclosing <see cref="Document.Model.InlineRun"/>s so the presenter renders emphasis/strong/
    /// code/strikethrough/links formatted without re-deriving the inline AST (§2.1). Because a style
    /// transition always coincides with a delimiter (a mark) or a run boundary, a content run's style
    /// is uniform. <see cref="RunStyle.None"/> for plain text and for synthetic/hidden runs. Init-only
    /// and defaulted so plain-text run maps stay value-equal to M1's.
    /// </summary>
    public RunStyle Style { get; init; }
}

/// <summary>
/// The inline formatting flags a <see cref="Run"/> carries (§2.1) — the projection of the enclosing
/// <see cref="Document.Model.InlineRunKind"/>s onto a content run, so a presenter maps each to a
/// <see cref="Cursorial.Output.TextAttributes"/>/color without touching the inline AST. Combine with
/// bitwise OR (nested strong+emphasis is <see cref="Bold"/> | <see cref="Italic"/>).
/// </summary>
[Flags]
public enum RunStyle
{
    /// <summary>Plain text — no inline formatting.</summary>
    None = 0,

    /// <summary>Strong emphasis (<c>**…**</c>) → bold weight.</summary>
    Bold = 1 << 0,

    /// <summary>Emphasis (<c>*…*</c>) → italic/slant.</summary>
    Italic = 1 << 1,

    /// <summary>GFM strikethrough (<c>~~…~~</c>) → struck-through.</summary>
    Strikethrough = 1 << 2,

    /// <summary>Inline code (<c>`…`</c>) → code-fill background.</summary>
    Code = 1 << 3,

    /// <summary>A link or autolink → underlined link text (§7.1; <see cref="Cursorial.Rendering.StyleQuantizer"/> gates the underline per tier).</summary>
    Link = 1 << 4,
}
