namespace CursorialEdit.Document.Model;

/// <summary>
/// The structural kind of a <see cref="Block"/>. M1 knows only <see cref="Paragraph"/> — the
/// degenerate plain-text producer emits nothing else. M2's parser extends this enum (headings,
/// code fences, callouts, tables, …) and uses the kind as one half of the block re-adoption key
/// (architecture §2.2 step 5: match by kind + first unmodified line).
/// </summary>
public enum BlockKind
{
    /// <summary>
    /// A plain paragraph: a run of non-blank source lines (plus its trailing blank lines under
    /// the M1 producer's attachment policy — see <see cref="PlainTextBlockProducer"/>).
    /// </summary>
    Paragraph = 0,
}
