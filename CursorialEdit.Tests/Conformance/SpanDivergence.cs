namespace CursorialEdit.Tests.Conformance;

/// <summary>How much a catalogued span divergence hurts, given the milestone that consumes the span.</summary>
public enum SpanDivergenceSeverity
{
    /// <summary>Cosmetic/expected; no consumer is affected (documented Markdig behaviour).</summary>
    Info,

    /// <summary>A consumer must compensate, but a simple per-construct repair covers it.</summary>
    Minor,

    /// <summary>The construct's precise span is materially wrong; the owning milestone must repair or fall back.</summary>
    Major,

    /// <summary>The span is unusable and no fallback preserves the construct's editability — a release blocker.</summary>
    Blocking,
}

/// <summary>
/// A catalogued, per-construct span divergence (architecture Decision 14): a construct whose
/// <c>UsePreciseSourceLocation</c> span in Markdig 1.3.2 does <b>not</b> delimit the construct it
/// claims. The M2.WP1 gate is "oracle green OR divergences catalogued per-construct" — so an
/// observed divergence whose <see cref="Construct"/> appears in <see cref="AcceptedSpanDivergences"/>
/// keeps the suite green (with the compensation recorded for the owning milestone), while a
/// divergence in a construct <i>not</i> catalogued fails the gate (a regression or an unaccounted gap).
/// </summary>
/// <param name="Construct">The construct label (matches <see cref="SpanObservation.Construct"/>).</param>
/// <param name="ExampleDocId">A representative corpus document that exhibits the divergence.</param>
/// <param name="ExampleSlice">What the span actually slices in that example.</param>
/// <param name="Expected">What a correct span would have delimited.</param>
/// <param name="Severity">Impact given the consuming milestone.</param>
/// <param name="Milestone">The milestone that needs the span correct (and owns the repair/fallback).</param>
/// <param name="Note">The compensation strategy (per-kind span repair, mark-visible fallback, etc.).</param>
public sealed record SpanDivergence(
    string Construct,
    string ExampleDocId,
    string ExampleSlice,
    string Expected,
    SpanDivergenceSeverity Severity,
    string Milestone,
    string Note);

/// <summary>
/// The accepted per-construct span-divergence catalogue for Markdig 1.3.2. Populated from the actual
/// span-oracle run over the vendored CommonMark + curated GFM corpora (never guessed) — each entry is
/// a construct whose spans the oracle observed to diverge in a documented, compensable way. A clean
/// run leaves this empty; entries here are the WP1 "catalogued divergences" the gate tolerates and
/// WP2/WP3/M4 must honour.
/// </summary>
public static class AcceptedSpanDivergences
{
    /// <summary>The catalogue. Keyed for lookup by <see cref="SpanDivergence.Construct"/>.</summary>
    public static IReadOnlyList<SpanDivergence> Entries { get; } =
    [
        // Empty: the Markdig 1.3.2 span oracle is clean across the corpus for every pinned construct
        // (see docs/conformance.md). If a future Markdig bump introduces a compensable span gap,
        // catalogue it here with its severity and owning milestone rather than failing the whole suite.
    ];

    /// <summary>The set of construct labels the catalogue accepts divergences for.</summary>
    public static IReadOnlySet<string> AcceptedConstructs { get; } =
        Entries.Select(e => e.Construct).ToHashSet(StringComparer.Ordinal);
}
