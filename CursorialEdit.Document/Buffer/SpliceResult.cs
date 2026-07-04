namespace CursorialEdit.Document.Buffer;

/// <summary>
/// What a <see cref="DocumentBuffer"/> splice did — the shape WP5's
/// <c>Edit(Start, Removed, Inserted)</c> primitive is built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inversion recipe (for undo).</b> The bytes the splice wrote occupy
/// <c>[StartOffset, StartOffset + insertedLength)</c> in the post-splice document, where
/// <c>insertedLength</c> is the length of the <c>inserted</c> argument the caller passed.
/// <c>buffer.ApplyAtOffset(StartOffset, insertedLength, RemovedText)</c> therefore restores the
/// prior document byte-exactly (and, because buffer line structure is always canonical — see
/// <see cref="DocumentBuffer"/> — structure-exactly). Invert via the <b>offset</b> form, not a
/// position, because a splice at a bare-CR seam can merge the inserted text's boundary into a
/// CRLF terminator, leaving no valid <see cref="TextPosition"/> at <c>StartOffset + insertedLength</c>.
/// </para>
/// </remarks>
/// <param name="StartOffset">
/// Absolute UTF-16 offset of the splice start. Identical before and after the splice (nothing
/// preceding it changed), so it is stable ground for inversion.
/// </param>
/// <param name="RemovedText">
/// Exactly the text the splice removed, terminators included as their literal characters
/// (<c>"\n"</c> / <c>"\r\n"</c>). Empty for pure insertions.
/// </param>
/// <param name="End">
/// The position immediately after the inserted text — the natural caret landing. When the
/// inserted text's trailing <c>'\r'</c> merged with a following <c>'\n'</c> into a CRLF
/// terminator, the exact offset falls inside that terminator and <c>End</c> is snapped to the
/// nearest valid position at or before it (the end of that line's text).
/// </param>
/// <param name="Epoch">The buffer's <see cref="IDocumentBuffer.Epoch"/> after this splice — the stamp for derived data (Decision 13).</param>
public readonly record struct SpliceResult(int StartOffset, string RemovedText, TextPosition End, long Epoch);
