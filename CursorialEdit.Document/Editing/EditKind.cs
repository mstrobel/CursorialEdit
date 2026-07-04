namespace CursorialEdit.Document.Editing;

/// <summary>
/// How an <see cref="Edit"/> folds into the undo history (architecture §2.1). The kind is the
/// caller's declaration of intent — the controller never infers it from the edit's content.
/// </summary>
public enum EditKind
{
    /// <summary>
    /// Ordinary character-level editing: typed runs, Backspace, and forward Delete. The only
    /// coalescing kind — adjacent Typing edits merge into one undo group per the
    /// splice-adjacency rules (see <see cref="EditController"/>). A Typing edit that both
    /// removes and inserts (typing over a selection) is recorded but is atomic: it neither
    /// joins an open group nor accepts followers, matching the <c>TextBox</c> reference shape.
    /// </summary>
    Typing = 0,

    /// <summary>Enter — a line break. Always its own undo group (§3.3).</summary>
    Newline = 1,

    /// <summary>
    /// A structural command (block formatting, table ops, list renumbering, replace, …).
    /// Always its own undo group.
    /// </summary>
    Structural = 2,

    /// <summary>A paste splice. Always its own undo group.</summary>
    Paste = 3,

    /// <summary>
    /// A replay of history through the same splice path: applied and announced via
    /// <see cref="EditController.Changed"/>, but <b>never recorded</b> — this is the kind
    /// undo/redo use internally. A Replay edit seals the open coalescing group (the document
    /// moved underneath it). External callers using this kind own the coherence of the undo
    /// history with the buffer; an incoherent history fails loudly at the next undo/redo.
    /// </summary>
    Replay = 4,
}
