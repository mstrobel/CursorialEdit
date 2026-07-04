namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// One precise-span check the <see cref="SpanOracle"/> made against a corpus document: the construct
/// it found, the span Markdig reported, the source slice that span delimits, and whether that slice
/// reproduces the construct it claims (architecture Decision 14b — the span-vs-source oracle). A
/// failing observation (<see cref="Reproduces"/> = <see langword="false"/>) is promoted to a
/// <see cref="SpanDivergence"/>.
/// </summary>
/// <param name="DocId">The <see cref="CorpusDocument.Id"/> the construct was found in.</param>
/// <param name="Construct">The construct label — one of <see cref="SpanOracle"/>'s construct constants.</param>
/// <param name="SpanStart">The reported <c>SourceSpan.Start</c> (offset into the document).</param>
/// <param name="SpanLength">The reported <c>SourceSpan.Length</c>.</param>
/// <param name="Slice">The source slice the span delimits (<c>document.Substring(Start, Length)</c>).</param>
/// <param name="Reproduces">Whether the slice reproduces the construct (structural shape + round-trip re-parse).</param>
/// <param name="FailureReason"><see langword="null"/> when it reproduces; otherwise why it did not.</param>
public sealed record SpanObservation(
    string DocId,
    string Construct,
    int SpanStart,
    int SpanLength,
    string Slice,
    bool Reproduces,
    string? FailureReason);
