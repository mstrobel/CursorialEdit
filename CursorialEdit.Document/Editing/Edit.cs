using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Document.Editing;

/// <summary>
/// The single edit primitive (architecture §2.1): replace the text at <paramref name="Start"/>
/// that currently reads <paramref name="Removed"/> with <paramref name="Inserted"/>. Every
/// mutation — keystrokes, commands, paste, table ops, replace-all, undo/redo replay — is
/// expressed as one of these and funneled through <see cref="EditController.Apply"/>.
/// </summary>
/// <remarks>
/// Carrying the removed <i>text</i> (not a length) makes the edit self-describing and
/// self-checking: <see cref="EditController.Apply"/> validates <paramref name="Removed"/>
/// against the buffer before splicing and fails loudly on mismatch, so a stale edit can never
/// silently corrupt the document. Both strings are serialized document text — line terminators
/// appear as their literal characters (<c>"\n"</c> / <c>"\r\n"</c>).
/// </remarks>
/// <param name="Start">The splice start — a valid position in the target buffer.</param>
/// <param name="Removed">
/// Exactly the text currently occupying the replaced range, terminators included. Empty for a
/// pure insertion. Never <see langword="null"/>.
/// </param>
/// <param name="Inserted">The replacement text; empty for a pure deletion. Never <see langword="null"/>.</param>
public readonly record struct Edit(TextPosition Start, string Removed, string Inserted);
