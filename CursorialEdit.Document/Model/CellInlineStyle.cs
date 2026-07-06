namespace CursorialEdit.Document.Model;

/// <summary>
/// The inline formatting a table cell's content run carries (§2.1, Decision 9 — per-cell reveal) — the
/// Document-project mirror of the app's <c>RunStyle</c>: because <see cref="TableModel"/> lives in the
/// Document project (with Markdig) and the styling vocabulary (<c>RunStyle</c>/<c>TextAttributes</c>)
/// lives in the app, the model expresses a formatted cell's per-run style in <b>these</b> flags and the
/// presenter maps them 1:1 to <c>RunStyle</c> — so an inactive cell styles bold/italic/strikethrough/code/
/// link exactly as the prose paragraph path does for the same inline kinds, without a Markdig type ever
/// crossing into the view (<c>ArchitectureTests</c>). Combine with bitwise OR (nested strong+emphasis is
/// <see cref="Bold"/> | <see cref="Italic"/>).
/// </summary>
[Flags]
public enum CellInlineStyle
{
    /// <summary>Plain text — no inline formatting.</summary>
    None = 0,

    /// <summary>Strong emphasis (<c>**…**</c>) → bold weight.</summary>
    Bold = 1 << 0,

    /// <summary>Emphasis (<c>*…*</c>) → italic/slant.</summary>
    Italic = 1 << 1,

    /// <summary>GFM strikethrough (<c>~~…~~</c>) → struck-through.</summary>
    Strikethrough = 1 << 2,

    /// <summary>Inline code (<c>`…`</c>) → code-fill background.</summary>
    Code = 1 << 3,

    /// <summary>A link or autolink → underlined link text.</summary>
    Link = 1 << 4,
}

/// <summary>
/// One styled content run of a <b>formatted</b> (marks-hidden) table cell fragment (Decision 9 — per-cell
/// reveal): the marks-hidden display draws as a sequence of these runs, each a maximal span of uniform
/// <see cref="Style"/> whose display cells map <b>1:1</b> onto a contiguous slice of the block source
/// (hidden marks fall <i>between</i> runs, never inside one), so the run's source slice reproduces its
/// display text and the caret walks it exactly like a plain cell. <see cref="CellOffset"/> is the run's
/// first cell measured from the fragment's left content edge (runs are contiguous in the display, so the
/// offsets tile), <see cref="Width"/> its display width in cells, and
/// <see cref="SrcStart"/>/<see cref="SrcLength"/> the block-relative source it draws.
/// </summary>
/// <param name="CellOffset">Cells from the fragment's left content edge where this run draws.</param>
/// <param name="Width">The run's display width in cells (whole-cell <c>GraphemeWidth</c> measure).</param>
/// <param name="SrcStart">Block-relative UTF-16 offset of the run's source slice (== its display text).</param>
/// <param name="SrcLength">Length of the run's source slice in UTF-16 code units (1:1 with the display).</param>
/// <param name="Style">The inline formatting the run's cells carry.</param>
public readonly record struct CellStyledRun(int CellOffset, int Width, int SrcStart, int SrcLength, CellInlineStyle Style);
