namespace CursorialEdit.Tests.Conformance;

/// <summary>Where a <see cref="CorpusDocument"/> came from — its provenance in the conformance report.</summary>
public enum CorpusSource
{
    /// <summary>Vendored verbatim from the official CommonMark <c>spec.json</c> suite (§2 acceptance).</summary>
    CommonMarkSpec,

    /// <summary>
    /// Hand-curated to exercise a GFM/pinned-extension construct the CommonMark core suite does not
    /// cover (tables, task lists, strikethrough, footnotes, def-lists, alerts, math, front matter).
    /// </summary>
    CuratedGfm,
}

/// <summary>
/// One conformance corpus document: a markdown fragment tagged with a stable id, the spec section it
/// exercises, the construct it targets, and its provenance. The span oracle and the conformance
/// reporter both iterate <see cref="CorpusDocument"/>s so the CommonMark suite and the curated GFM
/// set flow through one pipeline uniformly.
/// </summary>
/// <param name="Id">Stable identifier, e.g. <c>cmark-131</c> or <c>gfm-table-align</c>.</param>
/// <param name="Section">
/// The section this document belongs to in the report — the CommonMark spec section for vendored
/// rows, or a feature-spec §2.2/§2.3 label for curated rows.
/// </param>
/// <param name="Construct">The construct this document targets, e.g. <c>Emphasis</c>, <c>PipeTable</c>.</param>
/// <param name="Markdown">The markdown source.</param>
/// <param name="Source">Provenance (official vs curated).</param>
/// <param name="ExpectedHtml">
/// The reference HTML (present for CommonMark-spec rows; <see langword="null"/> for curated GFM,
/// which has no reference implementation to compare against).
/// </param>
public sealed record CorpusDocument(
    string Id,
    string Section,
    string Construct,
    string Markdown,
    CorpusSource Source,
    string? ExpectedHtml = null);
