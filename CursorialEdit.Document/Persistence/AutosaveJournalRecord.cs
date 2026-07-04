namespace CursorialEdit.Document.Persistence;

/// <summary>
/// A successfully read and checksum-verified journal: the header identity fields plus the exact
/// document text (line endings preserved as written). M6's recovery prompt consumes this shape;
/// in M1 it exists so round-trip fidelity is provable.
/// </summary>
/// <param name="Source">The header's source identity — <see cref="DocumentKey.Descriptor"/> as written.</param>
/// <param name="Timestamp">When the journaled snapshot was captured (the "unsaved changes from &lt;time&gt;" time).</param>
/// <param name="Text">The full document text, byte-exact through UTF-8 with original per-line endings.</param>
public sealed record AutosaveJournalRecord(string Source, DateTimeOffset Timestamp, string Text);
