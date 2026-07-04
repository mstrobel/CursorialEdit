using System.Reflection;
using System.Text.Json;

namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// Loads the conformance corpus: the vendored official CommonMark <c>spec.json</c> (embedded
/// resource) plus the hand-curated GFM/extension set (<see cref="GfmExtensionCorpus"/>). One place
/// resolves provenance, so the span oracle, the coverage assertions, and the conformance reporter
/// all see the same documents.
/// </summary>
public static class CorpusLoader
{
    /// <summary>
    /// Provenance of the vendored CommonMark suite. Fetched from the canonical spec URL during
    /// M2.WP1; refresh from <c>https://spec.commonmark.org/0.31.2/spec.json</c> when the spec version
    /// bumps.
    /// </summary>
    public const string CommonMarkSpecVersion = "0.31.2";

    private const string SpecResourceSuffix = "commonmark-spec.json";

    private static readonly Lazy<IReadOnlyList<SpecExample>> _specExamples = new(LoadSpecExamples);

    /// <summary>The raw official spec rows (markdown + reference HTML + section), in spec order.</summary>
    public static IReadOnlyList<SpecExample> SpecExamples => _specExamples.Value;

    /// <summary>The official CommonMark suite projected onto <see cref="CorpusDocument"/>s.</summary>
    public static IReadOnlyList<CorpusDocument> CommonMarkDocuments =>
        [.. SpecExamples.Select(e => new CorpusDocument(
            Id: $"cmark-{e.Example}",
            Section: e.Section,
            Construct: e.Section,
            Markdown: e.Markdown,
            Source: CorpusSource.CommonMarkSpec,
            ExpectedHtml: e.Html))];

    /// <summary>The curated GFM/pinned-extension corpus (constructs the core suite omits).</summary>
    public static IReadOnlyList<CorpusDocument> GfmDocuments => GfmExtensionCorpus.Documents;

    /// <summary>Every corpus document — CommonMark core first, then the curated GFM set.</summary>
    public static IReadOnlyList<CorpusDocument> AllDocuments =>
        [.. CommonMarkDocuments, .. GfmDocuments];

    private static IReadOnlyList<SpecExample> LoadSpecExamples()
    {
        var assembly = typeof(CorpusLoader).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith(SpecResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource '*{SpecResourceSuffix}' not found. Confirm the "
                + "<EmbeddedResource Include=\"Conformance/corpus/commonmark-spec.json\" /> entry in "
                + "CursorialEdit.Tests.csproj. Available: "
                + string.Join(", ", assembly.GetManifestResourceNames()));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var examples = JsonSerializer.Deserialize<List<SpecExample>>(stream)
            ?? throw new InvalidOperationException("commonmark-spec.json deserialized to null.");

        return examples;
    }
}
