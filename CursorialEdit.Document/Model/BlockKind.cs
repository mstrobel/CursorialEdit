namespace CursorialEdit.Document.Model;

/// <summary>
/// The structural kind of a <see cref="Block"/> — one half of the block re-adoption key
/// (architecture §2.2 step 5 / Decision 4: match by kind + first unmodified line). M1 knew only
/// <see cref="Paragraph"/> (the degenerate <see cref="PlainTextBlockProducer"/> emits nothing
/// else); M2's <see cref="MarkdigBlockProducer"/> maps every pinned Markdig top-level block type to
/// one of these.
/// </summary>
/// <remarks>
/// <para>
/// <b>Granularity.</b> The kinds describe <i>top-level</i> Markdig blocks: a list is
/// <see cref="List"/> (not per-item), a blockquote is <see cref="Quote"/> (not per-line). WP7's
/// presenters decompose containers into per-item/per-line visuals; that is a presentation concern
/// and does not change the block model's top-level tiling. <see cref="ListItem"/> is carried for
/// that future decomposition and for the independently-tested kind mapper, but the producer emits
/// <see cref="List"/> at document scope.
/// </para>
/// <para><see cref="Paragraph"/> is deliberately <c>0</c> so a default <see cref="BlockKind"/> is the safe fallback kind.</para>
/// </remarks>
public enum BlockKind
{
    /// <summary>
    /// A plain paragraph (Markdig <c>ParagraphBlock</c>), or the synthetic single block a producer
    /// emits for an empty/blank document. Also the fallback for any unmapped top-level block.
    /// </summary>
    Paragraph = 0,

    /// <summary>An ATX (<c># …</c>) or setext (<c>===</c>/<c>---</c> underlined) heading; the level (1–6) is on <see cref="Block.HeadingLevel"/>.</summary>
    Heading,

    /// <summary>A fenced code block (<c>``` … ```</c> / <c>~~~ … ~~~</c>); the info string is on <see cref="Block.FenceInfo"/>.</summary>
    FencedCode,

    /// <summary>An indented (four-space) code block.</summary>
    IndentedCode,

    /// <summary>A blockquote.</summary>
    Quote,

    /// <summary>A list (ordered or unordered) — a top-level <c>ListBlock</c>.</summary>
    List,

    /// <summary>A single list item — not produced at document scope (WP7 decomposition / mapper coverage only).</summary>
    ListItem,

    /// <summary>A thematic break (<c>---</c>/<c>***</c>/<c>___</c> horizontal rule).</summary>
    ThematicBreak,

    /// <summary>A GFM pipe table.</summary>
    Table,

    /// <summary>A raw HTML block (feature-spec §2.4 — rendered dimmed-literal by the fallback presenter).</summary>
    Html,

    /// <summary>The document-head YAML front matter block (fenced by <c>---</c>).</summary>
    FrontMatter,

    /// <summary>A GitHub alert/callout block (<c>&gt; [!NOTE]</c> etc.).</summary>
    Alert,

    /// <summary>A definition list.</summary>
    DefinitionList,

    /// <summary>A footnote definition (<c>[^id]: …</c>). Markdig relocates these into a footnote group; the producer re-anchors them to their source line.</summary>
    Footnote,

    /// <summary>A block-level mathematics fence (<c>$$ … $$</c>).</summary>
    Math,

    /// <summary>A link reference definition (<c>[label]: url</c>). Markdig relocates these into a link-ref group; the producer re-anchors them to their source line.</summary>
    LinkReferenceDefinition,
}
