using CursorialEdit.Document.Model;

using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

using MdBlock = Markdig.Syntax.Block;

namespace CursorialEdit.Document.Parsing;

/// <summary>
/// Maps a Markdig block to its <see cref="BlockKind"/> — the kind half of Decision 4's re-adoption
/// key. Kept independently testable (rather than buried in <see cref="Model.MarkdigBlockProducer"/>)
/// so the block-kind-mapping gate can assert every pinned construct maps correctly without driving a
/// full edit session.
/// </summary>
/// <remarks>
/// Ordering is <b>most-derived first</b>, mirroring the span oracle's classifier: <c>AlertBlock</c>
/// derives from <c>QuoteBlock</c>, <c>MathBlock</c> and <c>YamlFrontMatterBlock</c> from
/// <c>FencedCodeBlock</c>/<c>CodeBlock</c>, and <c>FootnoteLinkReferenceDefinition</c> from
/// <c>LinkReferenceDefinition</c> — so the subclass must be tested before its base.
/// </remarks>
public static class MarkdigBlockKindMap
{
    /// <summary>
    /// The <see cref="BlockKind"/> for <paramref name="block"/>. An unrecognized block maps to
    /// <see cref="BlockKind.Paragraph"/> (the safe rendered-as-source fallback), never throws.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="block"/> is <see langword="null"/>.</exception>
    public static BlockKind Map(MdBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return block switch
        {
            AlertBlock => BlockKind.Alert,                          // : QuoteBlock — before QuoteBlock
            MathBlock => BlockKind.Math,                            // : FencedCodeBlock — before it
            YamlFrontMatterBlock => BlockKind.FrontMatter,          // : CodeBlock — before CodeBlock
            FencedCodeBlock => BlockKind.FencedCode,                // : CodeBlock — before CodeBlock
            CodeBlock => BlockKind.IndentedCode,
            QuoteBlock => BlockKind.Quote,
            Table => BlockKind.Table,
            DefinitionList => BlockKind.DefinitionList,
            Footnote => BlockKind.Footnote,
            FootnoteLinkReferenceDefinition => BlockKind.Footnote,  // : LinkReferenceDefinition — before it
            LinkReferenceDefinition => BlockKind.LinkReferenceDefinition,
            ListBlock => BlockKind.List,
            ListItemBlock => BlockKind.ListItem,                    // not emitted at document scope; mapper coverage
            HeadingBlock => BlockKind.Heading,
            ThematicBreakBlock => BlockKind.ThematicBreak,
            HtmlBlock => BlockKind.Html,
            ParagraphBlock => BlockKind.Paragraph,
            _ => BlockKind.Paragraph,
        };
    }
}
