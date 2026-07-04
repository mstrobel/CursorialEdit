using System.Text.Json.Serialization;

namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// One row of the official CommonMark <c>spec.json</c> conformance suite (a markdown fragment, its
/// reference HTML, the example number, and the spec section it exercises). Deserialized from the
/// vendored <c>corpus/commonmark-spec.json</c> embedded resource by <see cref="CorpusLoader"/>.
/// </summary>
/// <param name="Markdown">The input markdown fragment.</param>
/// <param name="Html">The reference HTML the CommonMark reference implementation produces.</param>
/// <param name="Example">The 1-based example number within the spec.</param>
/// <param name="StartLine">The spec-document line the example starts on (provenance only).</param>
/// <param name="EndLine">The spec-document line the example ends on (provenance only).</param>
/// <param name="Section">The spec section heading, e.g. "Emphasis and strong emphasis".</param>
public sealed record SpecExample(
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("example")] int Example,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("section")] string Section);
