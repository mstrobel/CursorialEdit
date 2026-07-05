namespace CursorialEdit.Views;

/// <summary>
/// How the editor surface renders the document (architecture Decision 12 / implementation-plan §7
/// WP10). Raw mode is a <b>view mode</b>, not an edit-model change: the buffer, blocks, undo stack, and
/// edit pipeline are identical in both modes — only presenter selection and layout differ, so toggling
/// preserves the caret's source anchor by construction (§4.2).
/// </summary>
public enum ViewMode
{
    /// <summary>WYSIWYG: syntax marks hidden, structural markers as glyphs, reveal-on-edit on the active line (today's behavior).</summary>
    Formatted = 0,

    /// <summary>
    /// Verbatim source: every block renders its source lines literally — all markdown marks shown
    /// (<c>#</c>, <c>**</c>, <c>`</c>, <c>-</c>, <c>&gt;</c>, fences…) with syntax-token coloring — through
    /// an <b>identity</b> run map (source offset == display cell, 1:1), so the caret walks raw source
    /// directly and every caret/selection operation works unchanged.
    /// </summary>
    Raw = 1,
}
