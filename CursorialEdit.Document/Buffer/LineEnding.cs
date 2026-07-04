namespace CursorialEdit.Document.Buffer;

/// <summary>
/// The terminator that follows a <see cref="Line"/>'s text in the document byte stream.
/// </summary>
/// <remarks>
/// Endings are detected per line at construction and preserved per line across edits
/// (architecture Decision 1 / spec §10.1: mixed LF/CRLF documents round-trip byte-exactly).
/// A lone <c>'\r'</c> is <b>not</b> a line terminator — it stays inside <see cref="Line.Text"/>
/// and round-trips as ordinary content; only <c>"\n"</c> and <c>"\r\n"</c> break lines.
/// </remarks>
public enum LineEnding
{
    /// <summary>
    /// No terminator. Exactly the last line of a document has this ending; a document whose
    /// text ends with a newline therefore ends with an empty <see cref="None"/> line.
    /// </summary>
    None = 0,

    /// <summary>A single <c>"\n"</c>.</summary>
    Lf = 1,

    /// <summary>A <c>"\r\n"</c> pair. The pair is atomic: no <see cref="TextPosition"/> addresses the gap between the two characters.</summary>
    CrLf = 2,
}
