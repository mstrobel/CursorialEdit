using CursorialEdit.Document.Parsing;

using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// M2.WP1 — the one pinned pipeline (architecture Decision 2). Asserts the pinned extension set is
/// exactly the Decision 2 list (as data), that every method exists in Markdig 1.3.2, and that a smoke
/// document exercising every construct parses through <see cref="MarkdownPipelineFactory.Shared"/>
/// into the expected AST nodes without throwing — the behavioural proof that each extension is wired.
/// </summary>
public sealed class MarkdownPipelineFactoryTests
{
    /// <summary>The Decision 2 pin, in order — the single source of truth this test guards.</summary>
    private static readonly MarkdownExtension[] ExpectedPin =
    [
        MarkdownExtension.PipeTables,
        MarkdownExtension.TaskLists,
        MarkdownExtension.StrikethroughEmphasis,
        MarkdownExtension.AutoLinks,
        MarkdownExtension.Footnotes,
        MarkdownExtension.DefinitionLists,
        MarkdownExtension.AlertBlocks,
        MarkdownExtension.Mathematics,
        MarkdownExtension.YamlFrontMatter,
        MarkdownExtension.PreciseSourceLocation,
    ];

    [Fact]
    public void PinnedSet_IsExactlyDecisionTwo_InOrder()
    {
        Assert.Equal(ExpectedPin, MarkdownPipelineFactory.PinnedExtensions.Select(e => e.Extension));
    }

    [Fact]
    public void EveryPinnedExtension_IsAvailableInMarkdig132()
    {
        // The WP1 finding: all ten Use* methods exist in 1.3.2 (including UseAlertBlocks, which is not
        // a newer-Markdig-only feature). If a version bump removes one, this catalogues the regression.
        var unavailable = MarkdownPipelineFactory.PinnedExtensions
            .Where(e => e.Availability == ExtensionAvailability.Unavailable)
            .Select(e => $"{e.Extension} ({e.MarkdigMethod}) — nearest: {e.NearestAlternative}")
            .ToList();

        Assert.True(unavailable.Count == 0,
            "Pinned extensions unavailable in Markdig 1.3.2:\n  " + string.Join("\n  ", unavailable));
    }

    [Fact]
    public void EveryPinnedExtension_HasCompleteCatalogueMetadata()
    {
        Assert.All(MarkdownPipelineFactory.PinnedExtensions, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.MarkdigMethod));
            Assert.False(string.IsNullOrWhiteSpace(e.SpecSection));
            Assert.False(string.IsNullOrWhiteSpace(e.PresentationMilestone));
            Assert.False(string.IsNullOrWhiteSpace(e.Rationale));
            // Absent extensions must name a fallback; available ones need not.
            if (e.Availability == ExtensionAvailability.Unavailable)
                Assert.False(string.IsNullOrWhiteSpace(e.NearestAlternative));
        });
    }

    [Fact]
    public void Shared_IsASingletonInstance()
    {
        Assert.Same(MarkdownPipelineFactory.Shared, MarkdownPipelineFactory.Shared);
    }

    [Fact]
    public void Create_ProducesIndependentButEquivalentPipelines()
    {
        // Fresh instance for isolation, yet parses the same construct identically.
        var fresh = MarkdownPipelineFactory.Create();
        Assert.NotSame(MarkdownPipelineFactory.Shared, fresh);
        Assert.Equal(
            Markdown.ToHtml("~~x~~ https://a.example $y$", MarkdownPipelineFactory.Shared),
            Markdown.ToHtml("~~x~~ https://a.example $y$", fresh));
    }

    [Fact]
    public void SmokeDocument_OfEveryConstruct_ParsesIntoExpectedNodes_WithoutThrowing()
    {
        // YAML front matter must lead the document; every other pinned construct follows.
        const string smoke =
            """
            ---
            title: Smoke
            ---

            # Heading with *emphasis*, `code`, ~~strike~~, and https://bare.example

            A pointy autolink <https://ex.net>, a [link](/x), an ![img](/i.png), and a note[^n].

            [^n]: A footnote.

            > [!WARNING]
            > A callout.

            | A | B |
            | - | - |
            | 1 | 2 |

            - [x] done
            - [ ] todo

            Term
            :   Definition.

            Inline $a+b$ and a block:

            $$
            c = 1
            $$
            """;

        var doc = Markdown.Parse(smoke, MarkdownPipelineFactory.Shared);
        var nodes = doc.Descendants().ToList();

        // One assertion per pinned extension's characteristic node — proof it is wired.
        Assert.Contains(nodes, n => n is YamlFrontMatterBlock);                         // UseYamlFrontMatter
        Assert.Contains(nodes, n => n is Table);                                        // UsePipeTables
        Assert.Contains(nodes, n => n is TaskList);                                     // UseTaskLists
        Assert.Contains(nodes, n => n is EmphasisInline { DelimiterChar: '~' });        // UseEmphasisExtras(Strikethrough)
        Assert.Contains(nodes, n => n is LinkInline { IsAutoLink: true });              // UseAutoLinks (bare URL)
        Assert.Contains(nodes, n => n is FootnoteLink);                                 // UseFootnotes
        Assert.Contains(nodes, n => n is DefinitionList);                               // UseDefinitionLists
        Assert.Contains(nodes, n => n is AlertBlock);                                   // UseAlertBlocks
        Assert.Contains(nodes, n => n is MathInline);                                   // UseMathematics (inline)
        Assert.Contains(nodes, n => n is MathBlock);                                    // UseMathematics (block)

        // UsePreciseSourceLocation: an inline carries a non-empty precise span into the source.
        Assert.Contains(nodes, n => n is EmphasisInline && !n.Span.IsEmpty && n.Span.Start >= 0);

        // And the sanity that rendering the whole thing does not throw.
        Assert.False(string.IsNullOrEmpty(Markdown.ToHtml(smoke, MarkdownPipelineFactory.Shared)));
    }

    [Fact]
    public void StrikethroughIsPinnedButOtherEmphasisExtrasAreNot()
    {
        // Decision 2 pins EmphasisExtraOptions.Strikethrough only. Subscript/superscript/inserted/
        // marked must NOT be active, or the surface would exceed the feature-spec §2.2 set.
        Assert.Contains(
            Markdown.Parse("~~x~~", MarkdownPipelineFactory.Shared).Descendants(),
            n => n is EmphasisInline { DelimiterChar: '~', DelimiterCount: 2 });

        // Superscript (^x^) and subscript (~x~ single) stay inert: no EmphasisInline is produced.
        Assert.DoesNotContain(
            Markdown.Parse("a^b^c", MarkdownPipelineFactory.Shared).Descendants(),
            n => n is EmphasisInline { DelimiterChar: '^' });
        Assert.DoesNotContain(
            Markdown.Parse("H~2~O", MarkdownPipelineFactory.Shared).Descendants(),
            n => n is EmphasisInline { DelimiterChar: '~', DelimiterCount: 1 });
    }
}
