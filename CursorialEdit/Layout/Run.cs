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
public readonly record struct Run(int SrcStart, int SrcLen, int Col, int Width, RunKind Kind);
