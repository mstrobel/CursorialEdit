namespace CursorialEdit.Document.Buffer;

/// <summary>
/// The document-buffer seam (architecture §2.1): a line table addressed by
/// <see cref="TextPosition"/> (UTF-16 columns) or absolute UTF-16 offsets, mutated exclusively
/// through the single splice primitive. <see cref="DocumentBuffer"/> is the <c>List&lt;Line&gt;</c>
/// implementation; a rope is a swap-in behind this interface if the size target ever grows.
/// </summary>
/// <remarks>
/// All members are UI-thread-only, matching the framework's single-threaded UI model; off-thread
/// consumers work on snapshots stamped with <see cref="Epoch"/> (Decision 13).
/// </remarks>
public interface IDocumentBuffer
{
    /// <summary>Number of lines. Always at least 1 — an empty document is one empty, unterminated line.</summary>
    int LineCount { get; }

    /// <summary>Total document length in UTF-16 code units, terminators included (the length of <see cref="GetText()"/>).</summary>
    int Length { get; }

    /// <summary>
    /// Monotonic edit counter, bumped once per applied splice (including degenerate ones).
    /// Derived data computed off-thread carries the epoch it saw and is rejected on mismatch.
    /// </summary>
    long Epoch { get; }

    /// <summary>
    /// The mutation stamp assigned to every line rewritten by the most recent splice; bumped
    /// once per applied splice in lockstep with <see cref="Epoch"/>. Compare against
    /// <see cref="Line.Version"/>: a line is unmodified since a recorded stamp <c>S</c> iff its
    /// version is &lt;= <c>S</c>.
    /// </summary>
    int CurrentVersion { get; }

    /// <summary>The buffer's anchor table; anchor positions track every splice.</summary>
    AnchorTable Anchors { get; }

    /// <summary>Returns the line at <paramref name="index"/> (0-based).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    Line GetLine(int index);

    /// <summary>Reassembles the full document text, each line followed by its own terminator — byte-exact by construction.</summary>
    string GetText();

    /// <summary>
    /// Reassembles the half-open range [<paramref name="start"/>, <paramref name="end"/>),
    /// terminators of interior line boundaries included.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either position is invalid.</exception>
    /// <exception cref="ArgumentException"><paramref name="end"/> precedes <paramref name="start"/>.</exception>
    string GetText(TextPosition start, TextPosition end);

    /// <summary>
    /// Reads <paramref name="length"/> UTF-16 code units of the serialized document from
    /// absolute <paramref name="offset"/>. Unlike the position form, the boundaries may fall
    /// inside a CRLF terminator, reading it honestly — the read-side companion of
    /// <see cref="ApplyAtOffset"/> and the validation vehicle for undo/redo.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is negative-sized or falls outside 0..<see cref="Length"/>.</exception>
    string GetTextAtOffset(int offset, int length);

    /// <summary>Converts a valid position to its absolute UTF-16 offset in <see cref="GetText()"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is invalid.</exception>
    int GetOffset(TextPosition position);

    /// <summary>
    /// Converts an absolute offset (0..<see cref="Length"/> inclusive) to a position. An offset
    /// that falls inside a CRLF terminator — which no position can address — snaps to the
    /// nearest valid position before it (the end of that line's text), so the conversion is
    /// total but not injective at those two-unit terminators.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or beyond <see cref="Length"/>.</exception>
    TextPosition GetPosition(int offset);

    /// <summary>
    /// The single splice primitive: replaces the half-open range
    /// [<paramref name="start"/>, <paramref name="end"/>) with <paramref name="inserted"/>.
    /// Everything that mutates the buffer funnels through a splice.
    /// </summary>
    /// <param name="start">Valid range start.</param>
    /// <param name="end">Valid range end; must not precede <paramref name="start"/>.</param>
    /// <param name="inserted">Replacement text; may contain <c>"\n"</c>/<c>"\r\n"</c>, which split lines with their endings detected per line.</param>
    /// <returns>The splice receipt — see <see cref="SpliceResult"/> for the undo/inversion contract.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either position is invalid.</exception>
    /// <exception cref="ArgumentException"><paramref name="end"/> precedes <paramref name="start"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="inserted"/> is <see langword="null"/>.</exception>
    SpliceResult Apply(TextPosition start, TextPosition end, string inserted);

    /// <summary>
    /// Splice with the removal sized in UTF-16 code units of the serialized document
    /// (terminators count as their literal characters: LF = 1, CRLF = 2).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is invalid, or <paramref name="removedLength"/> is negative or reaches beyond the end of the document.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="inserted"/> is <see langword="null"/>.</exception>
    SpliceResult Apply(TextPosition start, int removedLength, string inserted);

    /// <summary>
    /// Byte-exact splice addressed purely by offsets. Unlike the position forms, the boundaries
    /// may fall inside a CRLF terminator, splitting it honestly (removing only its <c>'\r'</c>
    /// leaves an LF ending; the freed halves re-merge with adjacent text by the canonical
    /// line-splitting rules). This is the inversion vehicle for undo — see <see cref="SpliceResult"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is negative-sized or falls outside 0..<see cref="Length"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="inserted"/> is <see langword="null"/>.</exception>
    SpliceResult ApplyAtOffset(int startOffset, int removedLength, string inserted);
}
