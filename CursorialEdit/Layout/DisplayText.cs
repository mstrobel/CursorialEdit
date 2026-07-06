namespace CursorialEdit.Layout;

/// <summary>Display-string sanitization shared by the run-map builders (<see cref="BlockRunMap"/>, <see cref="RunMapBuilder"/>).</summary>
internal static class DisplayText
{
    /// <summary>
    /// Replaces a lone carriage return with its control-picture glyph <c>␍</c> (U+240D) for display/layout.
    /// <para>
    /// The buffer keeps a lone <c>'\r'</c> as ordinary <b>in-line content</b>, not a line terminator (see
    /// <c>LineEnding</c>). But <see cref="Cursorial.Rendering.Text.TextLayout.Build"/> treats <c>'\r'</c> as a
    /// hard break — so a raw CR would (a) split one source line into phantom visual rows with a gap at the CR,
    /// mismapping caret/click offsets, and (b) emit a bare CR to the terminal. Substituting the width-1 control
    /// picture is a <b>1:1-length</b> replacement, so every source↔display offset stays aligned; it merely makes
    /// the stray CR a visible, non-breaking glyph. Returns the input unchanged (no allocation) when there is no
    /// lone <c>'\r'</c> — the common case.
    /// </para>
    /// </summary>
    public static string SanitizeControls(string text) =>
        text.IndexOf('\r') < 0 ? text : text.Replace('\r', '␍');
}
