using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace CursorialEdit.Document.Parsing;

/// <summary>
/// The single source of the one pinned <see cref="MarkdownPipeline"/> (architecture Decision 2,
/// Markdig 1.3.2). Every parse in the editor — full document, incremental window (M2.WP2/WP3), and
/// the differential/oracle harness (Decision 14) — runs through <see cref="Shared"/> so there is
/// exactly one parser configuration in the product; a second, differently-configured pipeline would
/// reintroduce the §2 correctness risk the dependency was chosen to retire.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pin (Decision 2).</b> <c>UsePipeTables</c>, <c>UseTaskLists</c>,
/// <c>UseEmphasisExtras(Strikethrough)</c>, <c>UseAutoLinks</c>, <c>UseFootnotes</c>,
/// <c>UseDefinitionLists</c>, <c>UseAlertBlocks</c>, <c>UseMathematics</c>,
/// <c>UseYamlFrontMatter</c>, <c>UsePreciseSourceLocation</c>. All ten <c>Use*</c> methods were
/// verified present in the restored Markdig 1.3.2 (see <see cref="PinnedExtensions"/>); none had to
/// be dropped or substituted.
/// </para>
/// <para>
/// <b>Deliberately unused.</b> Markdig's round-trip/trivia machinery is <i>not</i> enabled: the
/// buffer is the only truth (Decision 1) and saves never serialize the AST, so trivia tracking
/// would only add cost. Extensions beyond the pin (emoji, abbreviations, generic attributes, grid
/// tables, auto-identifiers, …) are intentionally omitted — the surface is exactly the feature-spec
/// §2 construct set, no more.
/// </para>
/// <para>
/// <b>Thread-safety.</b> A built <see cref="MarkdownPipeline"/> is immutable and safe to share
/// across threads (off-thread full reparse, §2.3 threading); <see cref="Shared"/> is the canonical
/// instance. <see cref="Create"/> builds an independent, identically-configured pipeline when a test
/// needs isolation.
/// </para>
/// </remarks>
public static class MarkdownPipelineFactory
{
    private static readonly PinnedExtension[] _pinned =
    [
        new(MarkdownExtension.PipeTables, "UsePipeTables()", ExtensionAvailability.Available,
            "§5 / §2.2", "M3 (TablePresenter; FallbackSourcePresenter until then)",
            "GFM pipe tables — the headline feature; parse-side support ships now, presentation waits for M3."),
        new(MarkdownExtension.TaskLists, "UseTaskLists()", ExtensionAvailability.Available,
            "§2.2", "M4 (checkbox glyph; fallback renders literal until then)",
            "GFM task-list items; the checkbox is a synthetic run, toggling is an M4 command."),
        new(MarkdownExtension.StrikethroughEmphasis,
            "UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)", ExtensionAvailability.Available,
            "§2.2", "M2 (emphasis run styling)",
            "Strikethrough only — sub/super/inserted/marked excluded to keep the surface the §2.2 set."),
        new(MarkdownExtension.AutoLinks, "UseAutoLinks()", ExtensionAvailability.Available,
            "§2.2 [EDGE]", "M2 (link run styling)",
            "Bare http/https/ftp/mailto/tel and www. autolinks — the §2.2 [EDGE] scheme list."),
        new(MarkdownExtension.Footnotes, "UseFootnotes()", ExtensionAvailability.Available,
            "§2.3", "M4 (footnote back-reference navigation)",
            "Footnote definitions and references; label→referencing-block index is M2.WP3."),
        new(MarkdownExtension.DefinitionLists, "UseDefinitionLists()", ExtensionAvailability.Available,
            "§2.3", "M4 (definition-list presentation)",
            "Definition lists; parse-side now, presentation in M4."),
        new(MarkdownExtension.AlertBlocks, "UseAlertBlocks()", ExtensionAvailability.Available,
            "§2.3 (callouts)", "M4 (CalloutPresenter; FallbackSourcePresenter until then)",
            "GitHub alert/callout blocks — present in 1.3.2 (not a newer-Markdig-only feature)."),
        new(MarkdownExtension.Mathematics, "UseMathematics()", ExtensionAvailability.Available,
            "§2.3 [EDGE]", "M4 (math presentation)",
            "Inline $…$ and block $$…$$ with the no-space $ rule."),
        new(MarkdownExtension.YamlFrontMatter, "UseYamlFrontMatter()", ExtensionAvailability.Available,
            "§2.3", "M2 (FrontMatterPresenter, §3.2 resolution 5)",
            "Document-head YAML fenced by --- ; folded/dim presentation is M2.WP7."),
        new(MarkdownExtension.PreciseSourceLocation, "UsePreciseSourceLocation()",
            ExtensionAvailability.Available, "architecture Decision 8/14",
            "M2 (run maps, reveal, find) — load-bearing everywhere",
            "Precise source spans — every derived overlay depends on Span delimiting the exact source."),
    ];

    private static readonly MarkdownPipeline _shared = Create();

    /// <summary>
    /// The one canonical pinned pipeline. Share it; a <see cref="MarkdownPipeline"/> is immutable and
    /// thread-safe once built.
    /// </summary>
    public static MarkdownPipeline Shared => _shared;

    /// <summary>
    /// The pinned-extension catalogue as data — the Decision 2 set with each extension's Markdig
    /// method, 1.3.2 availability, feature-spec section, and owning presentation milestone. Read by
    /// the conformance report and by <c>MarkdownPipelineFactoryTests</c>; kept in lockstep with the
    /// <see cref="Create"/> builder below (a test asserts the two agree).
    /// </summary>
    public static IReadOnlyList<PinnedExtension> PinnedExtensions => _pinned;

    /// <summary>
    /// Builds a fresh pipeline configured with exactly the Decision 2 pin, in the pinned order. Use
    /// <see cref="Shared"/> for normal parsing; this exists for tests that want an isolated instance.
    /// </summary>
    /// <remarks>
    /// The <c>Use*</c> calls here are the ground truth; <see cref="PinnedExtensions"/> is the
    /// human-readable mirror. If Markdig is upgraded and a method disappears or gains an option,
    /// change both together — <c>MarkdownPipelineFactoryTests.PinnedSet_MatchesBuilder</c> fails
    /// until they agree.
    /// </remarks>
    public static MarkdownPipeline Create() =>
        new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseAutoLinks()
            .UseFootnotes()
            .UseDefinitionLists()
            .UseAlertBlocks()
            .UseMathematics()
            .UseYamlFrontMatter()
            .UsePreciseSourceLocation()
            .Build();
}
