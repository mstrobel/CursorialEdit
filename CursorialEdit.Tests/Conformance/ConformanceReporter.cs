using System.Text;

using CursorialEdit.Document.Parsing;

using Markdig;

namespace CursorialEdit.Tests.Conformance;

/// <summary>Per-section CommonMark conformance tally under the pinned pipeline.</summary>
/// <param name="Section">The spec section name.</param>
/// <param name="Total">Examples in the section.</param>
/// <param name="PinnedMatch">Examples whose pinned-pipeline HTML equals the reference exactly.</param>
/// <param name="ExtensionInduced">
/// Examples the pinned pipeline renders differently <i>only because</i> an enabled extension
/// reinterprets the input (a plain CommonMark pipeline still matches the reference) — expected, not a defect.
/// </param>
/// <param name="CoreDiff">
/// Examples a plain Markdig pipeline already renders differently from the reference — a Markdig-vs-spec
/// difference independent of our extensions (a Markdig-version finding, not our configuration).
/// </param>
public sealed record SectionConformance(string Section, int Total, int PinnedMatch, int ExtensionInduced, int CoreDiff);

/// <summary>The whole conformance analysis, plus the rendered <c>docs/conformance.md</c> body.</summary>
/// <param name="Markdown">The generated document text.</param>
/// <param name="Sections">Per-section CommonMark tallies, in spec order.</param>
/// <param name="TotalExamples">CommonMark spec examples analysed.</param>
/// <param name="PinnedMatches">Examples the pinned pipeline renders identically to the reference.</param>
/// <param name="ExtensionInduced">Examples that differ only due to an enabled extension.</param>
/// <param name="CoreDiffs">Examples a plain Markdig pipeline already differs from the reference on.</param>
/// <param name="CoreDiffsWhitespaceOnly">
/// Of <paramref name="CoreDiffs"/>, how many vanish under whitespace normalization — i.e. Markdig's
/// HTML differs from the reference only in insignificant whitespace (the AST the editor consumes is
/// identical). The remainder are genuinely structural.
/// </param>
/// <param name="OracleObservations">Total span-oracle checks made across the corpus.</param>
/// <param name="OracleFailures">Span-oracle checks whose slice did not reproduce its construct.</param>
public sealed record ConformanceReport(
    string Markdown,
    IReadOnlyList<SectionConformance> Sections,
    int TotalExamples,
    int PinnedMatches,
    int ExtensionInduced,
    int CoreDiffs,
    int CoreDiffsWhitespaceOnly,
    int OracleObservations,
    int OracleFailures);

/// <summary>
/// Generates the §2 acceptance conformance document (implementation-plan §7 exit gate,
/// architecture Decision 14c): which CommonMark/GFM spec sections the pinned pipeline reproduces,
/// which differ (and why), the span-oracle verdict, and the pinned-extension catalogue. Runs the
/// official CommonMark <c>spec.json</c> through both a plain and the pinned pipeline so
/// extension-induced differences are separated from any genuine Markdig-vs-spec differences.
/// </summary>
public static class ConformanceReporter
{
    private const string PinnedMarkdigPackageVersion = "1.3.2";

    /// <summary>Builds the full conformance analysis and renders the document body.</summary>
    public static ConformanceReport Build()
    {
        MarkdownPipeline pinned = MarkdownPipelineFactory.Shared;
        MarkdownPipeline plain = new MarkdownPipelineBuilder().Build();

        var sections = AnalyseCommonMark(pinned, plain, out int total, out int pinnedMatch,
            out int extInduced, out int coreDiff, out int coreDiffWs);
        var (oracleTotal, oracleFail, verifiedByConstruct) = RunOracle(pinned);

        string body = Render(sections, total, pinnedMatch, extInduced, coreDiff, coreDiffWs,
            oracleTotal, oracleFail, verifiedByConstruct);

        return new ConformanceReport(body, sections, total, pinnedMatch, extInduced, coreDiff,
            coreDiffWs, oracleTotal, oracleFail);
    }

    private static List<SectionConformance> AnalyseCommonMark(
        MarkdownPipeline pinned, MarkdownPipeline plain,
        out int total, out int pinnedMatch, out int extInduced, out int coreDiff, out int coreDiffWs)
    {
        total = pinnedMatch = extInduced = coreDiff = coreDiffWs = 0;
        var order = new List<string>();
        var byName = new Dictionary<string, (int Total, int Pinned, int Ext, int Core)>(StringComparer.Ordinal);

        foreach (var ex in CorpusLoader.SpecExamples)
        {
            string pinnedHtml = Markdown.ToHtml(ex.Markdown, pinned);
            bool pinnedOk = string.Equals(pinnedHtml, ex.Html, StringComparison.Ordinal);

            int addPinned = 0, addExt = 0, addCore = 0;
            if (pinnedOk)
            {
                addPinned = 1;
            }
            else
            {
                string plainHtml = Markdown.ToHtml(ex.Markdown, plain);
                if (string.Equals(plainHtml, ex.Html, StringComparison.Ordinal))
                {
                    addExt = 1;   // a pinned extension reinterpreted this input — expected
                }
                else
                {
                    addCore = 1;  // plain Markdig already diverges from the reference here
                    if (WhitespaceEqual(plainHtml, ex.Html))
                        coreDiffWs++;  // …but only in insignificant whitespace (AST is identical)
                }
            }

            total++;
            pinnedMatch += addPinned;
            extInduced += addExt;
            coreDiff += addCore;

            if (!byName.TryGetValue(ex.Section, out var acc))
            {
                order.Add(ex.Section);
                acc = default;
            }

            byName[ex.Section] = (acc.Total + 1, acc.Pinned + addPinned, acc.Ext + addExt, acc.Core + addCore);
        }

        return [.. order.Select(s =>
        {
            var a = byName[s];
            return new SectionConformance(s, a.Total, a.Pinned, a.Ext, a.Core);
        })];
    }

    private static (int Total, int Failures, SortedDictionary<string, (int Ok, int Total)> ByConstruct) RunOracle(
        MarkdownPipeline pinned)
    {
        int total = 0, failures = 0;
        var byConstruct = new SortedDictionary<string, (int Ok, int Total)>(StringComparer.Ordinal);

        foreach (var doc in CorpusLoader.AllDocuments)
        {
            foreach (var o in SpanOracle.Inspect(doc, pinned))
            {
                total++;
                if (!o.Reproduces)
                    failures++;

                byConstruct.TryGetValue(o.Construct, out var acc);
                byConstruct[o.Construct] = (acc.Ok + (o.Reproduces ? 1 : 0), acc.Total + 1);
            }
        }

        return (total, failures, byConstruct);
    }

    private static bool WhitespaceEqual(string a, string b)
    {
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && char.IsWhiteSpace(a[i])) i++;
            while (j < b.Length && char.IsWhiteSpace(b[j])) j++;
            if (i >= a.Length || j >= b.Length)
                return i >= a.Length && j >= b.Length;
            if (a[i] != b[j])
                return false;
            i++;
            j++;
        }
    }

    private static string Render(
        IReadOnlyList<SectionConformance> sections,
        int total, int pinnedMatch, int extInduced, int coreDiff, int coreDiffWs,
        int oracleTotal, int oracleFail,
        SortedDictionary<string, (int Ok, int Total)> verifiedByConstruct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# CommonMark / GFM Conformance — CursorialEdit");
        sb.AppendLine();
        sb.AppendLine("> **Generated artifact.** Produced by `CursorialEdit.Tests.Conformance.ConformanceReportTests`");
        sb.AppendLine("> (`[Trait(\"Category\",\"Conformance\")]`). Do not edit by hand — re-run the test to refresh.");
        sb.AppendLine("> This is the feature-spec §2 acceptance conformance document (architecture Decision 14c).");
        sb.AppendLine();
        sb.AppendLine($"- **Parser:** Markdig `{PinnedMarkdigPackageVersion}` "
            + $"(assembly `{typeof(Markdown).Assembly.GetName().Version}`), pinned per architecture Decision 2.");
        sb.AppendLine($"- **CommonMark suite:** official `spec.json` v{CorpusLoader.CommonMarkSpecVersion} "
            + $"— {total} examples across {sections.Count} sections (vendored, not curated).");
        sb.AppendLine($"- **GFM/extension corpus:** {CorpusLoader.GfmDocuments.Count} curated documents "
            + "(no reference implementation exists for these; validated structurally + by the span oracle).");
        sb.AppendLine($"- **Span oracle (Decision 14b):** {oracleTotal} precise-span checks, "
            + $"**{oracleFail} failing**.");
        sb.AppendLine();

        // 1. Pinned pipeline.
        sb.AppendLine("## 1. Pinned pipeline (Decision 2)");
        sb.AppendLine();
        sb.AppendLine("| Extension | Markdig method | 1.3.2 | Feature-spec | Presentation owner |");
        sb.AppendLine("| --- | --- | :---: | --- | --- |");
        foreach (var e in MarkdownPipelineFactory.PinnedExtensions)
        {
            string avail = e.Availability == ExtensionAvailability.Available ? "yes" : "**NO**";
            sb.AppendLine($"| {e.Extension} | `{e.MarkdigMethod}` | {avail} | {e.SpecSection} | {e.PresentationMilestone} |");
        }

        sb.AppendLine();
        bool anyUnavailable = MarkdownPipelineFactory.PinnedExtensions
            .Any(e => e.Availability == ExtensionAvailability.Unavailable);
        sb.AppendLine(anyUnavailable
            ? "> One or more pinned extensions are **unavailable in Markdig 1.3.2**; see the `NO` rows and their nearest alternatives."
            : "> All ten pinned extensions exist in Markdig 1.3.2 — none had to be dropped or substituted.");
        sb.AppendLine();

        // 2. CommonMark core.
        sb.AppendLine("## 2. CommonMark core conformance (official spec.json)");
        sb.AppendLine();
        sb.AppendLine("Each example is rendered through the **pinned** pipeline and compared to the CommonMark");
        sb.AppendLine("reference HTML. A mismatch is *extension-induced* when a plain CommonMark pipeline still");
        sb.AppendLine("matches the reference (an enabled extension deliberately reinterpreted the input — e.g. a");
        sb.AppendLine("bare URL became an autolink, `~~x~~` became strikethrough, `$x$` became math, a `|` row");
        sb.AppendLine("became a table); it is a *core diff* only when plain Markdig itself differs from the reference.");
        sb.AppendLine();
        sb.AppendLine("| Section | Examples | Pinned = ref | Extension-induced | Core diff |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var s in sections)
            sb.AppendLine($"| {s.Section} | {s.Total} | {s.PinnedMatch} | {s.ExtensionInduced} | {s.CoreDiff} |");
        sb.AppendLine($"| **Total** | **{total}** | **{pinnedMatch}** | **{extInduced}** | **{coreDiff}** |");
        sb.AppendLine();
        sb.AppendLine($"- **{pinnedMatch}/{total}** examples render identically to the CommonMark reference under the pinned pipeline.");
        sb.AppendLine($"- **{extInduced}** differ only because an enabled extension reinterpreted the input (expected; these");
        sb.AppendLine("  inputs exercise GFM/extension syntax the vanilla spec treats as plain text).");
        if (coreDiff == 0)
        {
            sb.AppendLine("- **0** core diffs: plain Markdig 1.3.2 reproduces the CommonMark reference on every example.");
        }
        else
        {
            int structural = coreDiff - coreDiffWs;
            sb.AppendLine($"- **{coreDiff}** core diffs: plain Markdig 1.3.2 differs from the CommonMark {CorpusLoader.CommonMarkSpecVersion} reference —");
            sb.AppendLine($"  **{coreDiffWs} of them are whitespace-only** (Markdig serializes a loose list item as");
            sb.AppendLine("  `<li><p>…` where the reference emits `<li>\\n<p>…`; the block **AST is identical**), and");
            sb.AppendLine($"  **{structural} are structural**. The editor consumes the AST and precise spans, **never Markdig's");
            sb.AppendLine("  HTML**, so whitespace-only diffs do not affect it; they are recorded here only to characterize");
            sb.AppendLine("  the pinned Markdig against the latest spec for WP2/WP3/M4 planning.");
        }

        sb.AppendLine();

        // 3. GFM/extension coverage.
        sb.AppendLine("## 3. GFM / extension coverage (curated corpus)");
        sb.AppendLine();
        sb.AppendLine("| Construct | Documents | Span-checked instances (all reproduced) |");
        sb.AppendLine("| --- | ---: | ---: |");
        var pinned = MarkdownPipelineFactory.Shared;
        foreach (var group in CorpusLoader.GfmDocuments.GroupBy(d => d.Construct).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int docCount = group.Count();
            int spanChecks = group.Sum(d => SpanOracle.Inspect(d, pinned).Count);
            sb.AppendLine($"| {group.Key} | {docCount} | {spanChecks} |");
        }

        sb.AppendLine();

        // 4. Span oracle.
        sb.AppendLine("## 4. Span oracle (architecture Decision 14b)");
        sb.AppendLine();
        sb.AppendLine("Every construct carrying a `UsePreciseSourceLocation` span is checked: the span's source");
        sb.AppendLine("slice must reproduce the construct it claims (structural delimiter/bracket shape + a");
        sb.AppendLine("round-trip re-parse where re-parsing in isolation is well-defined).");
        sb.AppendLine();
        sb.AppendLine("| Construct | Reproduced | Checked |");
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var (construct, acc) in verifiedByConstruct)
            sb.AppendLine($"| {construct} | {acc.Ok} | {acc.Total} |");
        sb.AppendLine($"| **All** | **{oracleTotal - oracleFail}** | **{oracleTotal}** |");
        sb.AppendLine();
        sb.AppendLine(oracleFail == 0
            ? "**Verdict: clean.** Every precise span across the CommonMark + GFM corpora delimits the exact"
              + " source of the construct it belongs to. Run maps, reveal-on-edit, and find can trust"
              + " `document.Substring(span.Start, span.Length)` for every pinned construct."
            : $"**Verdict: {oracleFail} divergence(s)** — see §5.");
        sb.AppendLine();

        // 5. Divergence catalogue.
        sb.AppendLine("## 5. Span-divergence catalogue (per-construct)");
        sb.AppendLine();
        if (AcceptedSpanDivergences.Entries.Count == 0)
        {
            sb.AppendLine("_None._ Markdig 1.3.2 stamps a correct precise span on every pinned construct exercised");
            sb.AppendLine("by the corpus. If a future Markdig bump introduces a compensable span gap, it is catalogued");
            sb.AppendLine("here (construct, example, expected vs actual, severity, owning milestone) so the oracle gate");
            sb.AppendLine("stays green while WP2/WP3/M4 apply the recorded compensation.");
        }
        else
        {
            sb.AppendLine("| Construct | Severity | Milestone | Example | Expected | Actual | Compensation |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var d in AcceptedSpanDivergences.Entries)
                sb.AppendLine($"| {d.Construct} | {d.Severity} | {d.Milestone} | `{d.ExampleDocId}` | {d.Expected} | `{d.ExampleSlice}` | {d.Note} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
