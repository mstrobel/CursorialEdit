namespace CursorialEdit.Document.Persistence;

/// <summary>
/// The on-disk encoding a <see cref="DocumentFile"/> detected on load and re-emits on save
/// (spec §10.1: read/write UTF-8 by default, detect UTF-8/UTF-16 on open, preserve a BOM).
/// </summary>
public enum DocumentEncoding
{
    /// <summary>
    /// UTF-8 — the default for new files and the detection result for BOM-less files whose bytes
    /// decode as well-formed UTF-8 (and for files carrying the UTF-8 BOM).
    /// </summary>
    Utf8 = 0,

    /// <summary>UTF-16 little-endian, detected by its <c>FF FE</c> BOM.</summary>
    Utf16LittleEndian = 1,

    /// <summary>UTF-16 big-endian, detected by its <c>FE FF</c> BOM.</summary>
    Utf16BigEndian = 2,

    /// <summary>
    /// The documented fallback for BOM-less files that are <b>not</b> well-formed UTF-8: every
    /// byte decodes as its ISO-8859-1 (Latin-1) code point, which is lossless in both directions
    /// for the loaded bytes — the file round-trips byte-identically. Characters above U+00FF
    /// typed <i>into</i> such a document cannot be represented and save as <c>'?'</c>
    /// (<see cref="System.Text.Encoding.Latin1"/>'s replacement); other encodings are
    /// spec-deferred, so this fallback favors never corrupting the bytes that were read.
    /// </summary>
    Latin1 = 3,
}
