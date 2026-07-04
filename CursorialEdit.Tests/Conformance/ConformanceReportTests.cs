namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// M2.WP1 — generates the checked-in §2 acceptance conformance document (architecture Decision 14c,
/// implementation-plan §7 exit gate). Runs the official CommonMark suite + curated GFM corpus through
/// the pinned pipeline and the span oracle, writes <c>docs/conformance.md</c>, and asserts the
/// artifact was written non-empty with the expected structure. Tagged <c>Conformance</c> so it can be
/// run as its own tier (plan §2.1).
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ConformanceReportTests
{
    [Fact]
    public void GeneratesConformanceDocument()
    {
        var report = ConformanceReporter.Build();

        // Sanity that the analysis actually ran over the vendored suite (not an empty corpus).
        Assert.Equal(CorpusLoader.SpecExamples.Count, report.TotalExamples);
        Assert.True(report.TotalExamples >= 600, $"Only {report.TotalExamples} spec examples analysed.");
        Assert.True(report.PinnedMatches > 0, "No spec example matched the reference — HTML comparison is broken.");
        Assert.True(report.OracleObservations > 1000, $"Only {report.OracleObservations} span checks made.");

        // The gate, restated at report scope: the span oracle is clean (or every failure is catalogued).
        int uncatalogued = report.OracleFailures - AcceptedSpanDivergences.Entries.Count;
        Assert.True(report.OracleFailures == 0 || uncatalogued <= 0,
            $"{report.OracleFailures} span failures but only {AcceptedSpanDivergences.Entries.Count} catalogued.");

        // Markdig-version guard: every plain-Markdig-vs-reference difference must be whitespace-only
        // (a loose-list <li> HTML-serialization quirk). The editor consumes the AST, not Markdig's
        // HTML, so this is benign — but a *structural* core diff introduced by a future Markdig/spec
        // bump would matter, so surface it here instead of letting it pass silently.
        Assert.True(report.CoreDiffs == report.CoreDiffsWhitespaceOnly,
            $"{report.CoreDiffs - report.CoreDiffsWhitespaceOnly} structural core diff(s) appeared — "
            + "plain Markdig now differs from the CommonMark reference in more than whitespace. Review "
            + "docs/conformance.md §2 before accepting.");

        // Write the artifact and assert it exists and is non-empty with the expected sections.
        string path = RepoLocator.ConformanceDocPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.Markdown);

        Assert.True(File.Exists(path), $"Conformance document was not written to {path}.");
        string written = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(written));
        Assert.Contains("# CommonMark / GFM Conformance", written);
        Assert.Contains("## 1. Pinned pipeline", written);
        Assert.Contains("## 2. CommonMark core conformance", written);
        Assert.Contains("## 4. Span oracle", written);
        Assert.Contains("## 5. Span-divergence catalogue", written);
    }
}
