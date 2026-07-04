using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Document.Editing;

/// <summary>
/// The caret state an undo group restores (§3.3 [EDGE]): the caret position plus the fixed end
/// of the selection, when one exists. Groups record the state before their first edit and after
/// their last; <see cref="EditController.Undo"/>/<see cref="EditController.Redo"/> return the
/// recorded state for the caller to apply.
/// </summary>
/// <remarks>
/// The controller treats caret states as opaque restore payloads — it stores and returns them
/// but never validates or adjusts them. Producing cluster-snapped, buffer-valid states is the
/// caret owner's job (M1.WP8 supplies the live provider). A state returned by undo is valid by
/// construction for its group's restored document, provided it was valid when recorded.
/// </remarks>
/// <param name="Position">The caret position (the active end of the selection, when one exists).</param>
/// <param name="SelectionAnchor">
/// The fixed end of the selection, or <see langword="null"/> when there is no selection.
/// </param>
public readonly record struct CaretState(TextPosition Position, TextPosition? SelectionAnchor = null)
{
    /// <summary>Whether this state carries a non-empty selection.</summary>
    public bool HasSelection => SelectionAnchor is { } anchor && anchor != Position;
}
