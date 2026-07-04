namespace CursorialEdit.Document.Parsing;

/// <summary>
/// The identity of one Markdig extension pinned into the single canonical pipeline
/// (architecture Decision 2). Enumerated so the conformance report, the span oracle, and the
/// M2.WP2/WP3 producers can read the pinned set as <b>data</b> rather than re-deriving it from the
/// builder call site — the pipeline is configured in exactly one place
/// (<see cref="MarkdownPipelineFactory"/>) and described in exactly one place (this enum plus
/// <see cref="PinnedExtension"/>).
/// </summary>
/// <remarks>
/// Order matches the Decision 2 pin list. Each member corresponds to one
/// <c>Markdig.MarkdownExtensions.Use*</c> call; the mapping and the 1.3.2 availability of each are
/// recorded in <see cref="MarkdownPipelineFactory.PinnedExtensions"/>.
/// </remarks>
public enum MarkdownExtension
{
    /// <summary>GFM pipe tables (<c>UsePipeTables</c>) — feature-spec §5 / §2.2.</summary>
    PipeTables,

    /// <summary>GFM task-list items <c>- [ ]</c>/<c>- [x]</c> (<c>UseTaskLists</c>) — §2.2.</summary>
    TaskLists,

    /// <summary>
    /// GFM strikethrough <c>~~text~~</c> via <c>UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)</c>
    /// — pinned to <i>strikethrough only</i>, deliberately excluding sub/super/inserted/marked so the
    /// surface stays the feature-spec §2.2 set and no unspecified emphasis flavour leaks in.
    /// </summary>
    StrikethroughEmphasis,

    /// <summary>
    /// Bare-URL autolinks — <c>http</c>/<c>https</c>/<c>ftp</c>/<c>mailto</c>/<c>tel</c> and <c>www.</c>
    /// (<c>UseAutoLinks</c>) — the §2.2 [EDGE] scheme list. (CommonMark <c>&lt;url&gt;</c> pointy
    /// autolinks are core and need no extension.)
    /// </summary>
    AutoLinks,

    /// <summary>Footnote definitions <c>[^id]: …</c> and references <c>[^id]</c> (<c>UseFootnotes</c>) — §2.3.</summary>
    Footnotes,

    /// <summary>Definition lists <c>term</c> / <c>: definition</c> (<c>UseDefinitionLists</c>) — §2.3.</summary>
    DefinitionLists,

    /// <summary>
    /// GitHub alert/callout blocks <c>&gt; [!NOTE]</c> etc. (<c>UseAlertBlocks</c>) — §2.3 callouts.
    /// </summary>
    AlertBlocks,

    /// <summary>
    /// Inline <c>$…$</c> and block <c>$$…$$</c> mathematics with the no-space <c>$</c> rule
    /// (<c>UseMathematics</c>) — §2.3 [EDGE].
    /// </summary>
    Mathematics,

    /// <summary>YAML front matter fenced by <c>---</c> at the document head (<c>UseYamlFrontMatter</c>) — §2.3.</summary>
    YamlFrontMatter,

    /// <summary>
    /// Precise source spans on every parsed object (<c>UsePreciseSourceLocation</c>) — the load-bearing
    /// pin for the whole editor: run maps, reveal-on-edit, find highlighting, and the span oracle all
    /// require <c>MarkdownObject.Span</c> to delimit the exact source of each construct.
    /// </summary>
    PreciseSourceLocation,
}

/// <summary>
/// Whether a pinned extension's <c>Use*</c> method actually exists in the restored Markdig 1.3.2
/// (architecture Decision 2 pins the version, so availability is a fixed fact per build). Absent
/// extensions are catalogued rather than invented (M2.WP1 rule): the pipeline omits them and the
/// conformance report records the gap and the owning presentation milestone's fallback.
/// </summary>
public enum ExtensionAvailability
{
    /// <summary>The <c>Use*</c> method exists in Markdig 1.3.2 and is wired into the pinned pipeline.</summary>
    Available,

    /// <summary>
    /// The <c>Use*</c> method does not exist in Markdig 1.3.2; the pipeline omits it and
    /// <see cref="PinnedExtension.NearestAlternative"/> records the fallback.
    /// </summary>
    Unavailable,
}

/// <summary>
/// One row of the pinned-extension catalogue: which Markdig <c>Use*</c> method a
/// <see cref="MarkdownExtension"/> maps to, whether it is available in 1.3.2, the feature-spec
/// section it serves, the milestone that owns its <i>presentation</i>, and — when absent — the
/// nearest alternative. Consumed by the conformance report and by
/// <c>MarkdownPipelineFactoryTests</c> to assert the pipeline is exactly the Decision 2 set.
/// </summary>
/// <param name="Extension">The pinned extension identity.</param>
/// <param name="MarkdigMethod">
/// The exact <c>Markdig.MarkdownExtensions</c> call the factory makes (including pinned options),
/// e.g. <c>UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)</c>.
/// </param>
/// <param name="Availability">Whether <paramref name="MarkdigMethod"/> exists in Markdig 1.3.2.</param>
/// <param name="SpecSection">The markdown-editor feature-spec section this extension satisfies.</param>
/// <param name="PresentationMilestone">
/// The milestone that owns the construct's rendered presentation (M2 renders core; M3 owns tables;
/// M4 owns callouts/footnotes/task-list glyphs/def-lists/math; M2 renders the rest or a mark-visible
/// fallback). Used to answer "if this were absent, can the owning milestone still proceed?".
/// </param>
/// <param name="Rationale">Why this extension is pinned (one line).</param>
/// <param name="NearestAlternative">
/// When <see cref="ExtensionAvailability.Unavailable"/>, the nearest 1.3.2 substitute or the
/// mark-visible fallback; <see langword="null"/> when the extension is available.
/// </param>
public sealed record PinnedExtension(
    MarkdownExtension Extension,
    string MarkdigMethod,
    ExtensionAvailability Availability,
    string SpecSection,
    string PresentationMilestone,
    string Rationale,
    string? NearestAlternative = null);
