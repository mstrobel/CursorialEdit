using CursorialEdit.Document.Parsing;

using Markdig;

namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// M2.WP1 gate (implementation-plan §7; architecture Decision 14b): the span-vs-source oracle.
/// Every precise <c>UsePreciseSourceLocation</c> span, across the vendored CommonMark suite and the
/// curated GFM/extension corpus, must have a source slice that reproduces the construct it delimits.
/// The gate is "oracle green OR divergences catalogued per-construct": a slice that fails to
/// reproduce its construct is tolerated only if its construct is in
/// <see cref="AcceptedSpanDivergences"/>; any other failure fails the milestone.
/// </summary>
public sealed class SpanOracleTests
{
    private static readonly MarkdownPipeline Pinned = MarkdownPipelineFactory.Shared;

    /// <summary>Every inline construct the oracle must observe and verify at least once (coverage isn't vacuous).</summary>
    private static readonly string[] RequiredInlineConstructs =
    [
        SpanOracle.Emphasis, SpanOracle.Strikethrough, SpanOracle.CodeSpan, SpanOracle.Link,
        SpanOracle.ReferenceLink, SpanOracle.Image, SpanOracle.GfmAutoLink, SpanOracle.PointyAutolink,
        SpanOracle.MathInlineConstruct, SpanOracle.HtmlInlineConstruct, SpanOracle.HtmlEntity,
        SpanOracle.Literal, SpanOracle.TaskListMarker, SpanOracle.FootnoteReference,
    ];

    /// <summary>Every block construct that must be exercised somewhere in the corpus.</summary>
    private static readonly string[] RequiredBlockConstructs =
    [
        SpanOracle.Heading, SpanOracle.FencedCode, SpanOracle.IndentedCode, SpanOracle.BlockQuote,
        SpanOracle.ListBlockConstruct, SpanOracle.ThematicBreak, SpanOracle.TableBlock,
        SpanOracle.FrontMatter, SpanOracle.MathBlockConstruct, SpanOracle.FootnoteDefinition,
        SpanOracle.DefinitionListConstruct, SpanOracle.Alert,
    ];

    // ───────────────────────────── the gate ─────────────────────────────

    [Fact]
    public void EverySpanReproducesItsConstruct()
    {
        var failures = new List<SpanObservation>();
        foreach (var doc in CorpusLoader.AllDocuments)
            failures.AddRange(SpanOracle.Inspect(doc, Pinned).Where(o => !o.Reproduces));

        // Uncatalogued failures fail the gate; catalogued constructs are tolerated (documented in §5).
        var uncatalogued = failures
            .Where(f => !AcceptedSpanDivergences.AcceptedConstructs.Contains(f.Construct))
            .ToList();

        Assert.True(uncatalogued.Count == 0,
            $"{uncatalogued.Count} uncatalogued span divergence(s) — either a Markdig regression or a "
            + "gap that must be added to AcceptedSpanDivergences with its severity and owning milestone:\n"
            + string.Join("\n", uncatalogued.Take(20)
                .Select(f => $"  [{f.DocId}] {f.Construct} @{f.SpanStart}+{f.SpanLength} "
                    + $"slice='{f.Slice}' :: {f.FailureReason}")));
    }

    [Fact]
    public void EnabledInlineConstructs_AreEachExercisedAndVerified()
    {
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in CorpusLoader.AllDocuments)
            verified.UnionWith(SpanOracle.VerifiedInlineConstructs(doc, Pinned));

        var missing = RequiredInlineConstructs.Where(c => !verified.Contains(c)).ToList();
        Assert.True(missing.Count == 0,
            "No corpus document verifies these inline constructs (coverage gap): " + string.Join(", ", missing));
    }

    [Fact]
    public void EnabledBlockConstructs_AreEachPresentInCorpus()
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in CorpusLoader.AllDocuments)
            present.UnionWith(SpanOracle.PresentBlockConstructs(doc, Pinned));

        var missing = RequiredBlockConstructs.Where(c => !present.Contains(c)).ToList();
        Assert.True(missing.Count == 0,
            "No corpus document exercises these block constructs (coverage gap): " + string.Join(", ", missing));
    }

    [Fact]
    public void Oracle_MakesManyChecks_AcrossTheWholeCorpus()
    {
        // Guards against the oracle silently becoming a no-op (e.g. Descendants() not walking inlines):
        // the corpus is dense enough that a healthy run makes well over a thousand span checks.
        int checks = CorpusLoader.AllDocuments.Sum(d => SpanOracle.Inspect(d, Pinned).Count);
        Assert.True(checks > 1000, $"Span oracle made only {checks} checks — coverage collapsed.");
    }

    // ─────────────── concrete per-construct invariants (regression anchors) ───────────────

    [Theory]
    [InlineData("an *italic* word", "*italic*")]
    [InlineData("a **bold** word", "**bold**")]
    [InlineData("a ~~struck~~ word", "~~struck~~")]
    public void EmphasisSlice_IsTheDelimitedRun(string source, string expectedSlice)
    {
        AssertSingleSlice(source, SpanOracle.Emphasis, SpanOracle.Strikethrough, expectedSlice);
    }

    [Theory]
    [InlineData("call `foo()` now", "`foo()`")]
    [InlineData("a ``code with ` tick`` here", "``code with ` tick``")]
    public void CodeSpanSlice_IsTheBacktickRun(string source, string expectedSlice)
    {
        AssertSingleSlice(source, SpanOracle.CodeSpan, SpanOracle.CodeSpan, expectedSlice);
    }

    [Fact]
    public void LinkSlice_IsTheBracketParenConstruct()
    {
        AssertSingleSlice("see [text](/url \"t\") here", SpanOracle.Link, SpanOracle.Link, "[text](/url \"t\")");
    }

    [Fact]
    public void ImageSlice_IsTheBangBracketParenConstruct()
    {
        AssertSingleSlice("![alt](/img.png) trailing", SpanOracle.Image, SpanOracle.Image, "![alt](/img.png)");
    }

    [Fact]
    public void PointyAutolinkSlice_IsTheAngleBracketedUrl()
    {
        AssertSingleSlice("go <https://ex.com> now", SpanOracle.PointyAutolink, SpanOracle.PointyAutolink,
            "<https://ex.com>");
    }

    [Fact]
    public void InlineMathSlice_IsTheDollarDelimitedRun()
    {
        AssertSingleSlice("the $x+y$ term", SpanOracle.MathInlineConstruct, SpanOracle.MathInlineConstruct, "$x+y$");
    }

    /// <summary>
    /// Parses <paramref name="source"/>, finds the single observation whose construct is one of the
    /// accepted labels, and asserts it reproduced and its slice is exactly <paramref name="expected"/>.
    /// </summary>
    private static void AssertSingleSlice(string source, string labelA, string labelB, string expected)
    {
        var doc = new CorpusDocument("inline-probe", "probe", "probe", source, CorpusSource.CuratedGfm);
        var match = SpanOracle.Inspect(doc, Pinned)
            .Single(o => o.Construct == labelA || o.Construct == labelB);

        Assert.True(match.Reproduces, $"span did not reproduce its construct: {match.FailureReason}");
        Assert.Equal(expected, match.Slice);
    }
}
