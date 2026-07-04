using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CursorialEdit.Document.Persistence;

/// <summary>
/// The journal file for one document (spec §12): atomic write, checksum-verified read, delete.
/// Autosave <b>never</b> touches the document's real path — this class only ever writes the
/// derived journal file inside the <see cref="IAppStatePathProvider"/> directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format (schema v1).</b> UTF-8 throughout: a magic line (<c>cursorialedit-journal v1</c>),
/// then <c>key: value</c> header lines — <c>source</c> (<see cref="DocumentKey.Descriptor"/>,
/// percent-escaped for <c>%</c>/CR/LF), <c>timestamp</c> (ISO-8601 round-trip), <c>checksum</c>
/// (<c>sha256:</c> + lowercase hex of the content bytes), <c>length</c> (content byte count) —
/// an empty line, then exactly <c>length</c> content bytes: the document text with its exact
/// per-line endings. An unreadable, truncated, or checksum-mismatched journal reads as absent
/// (<see cref="TryRead"/> returns <see langword="false"/>) — M6's risk posture. M6.WP4 extends
/// the header (BOM/encoding flags) under a bumped schema version.
/// </para>
/// <para>
/// <b>Atomicity.</b> Writes go to a uniquely named temp file <i>in the journal directory</i>
/// (same volume, so the rename never crosses filesystems), are flushed to disk, then
/// <see cref="File.Move(string, string, bool)"/> replaces the journal in one step — a reader
/// never observes partial content, and a failed write leaves the previous journal intact and
/// cleans its temp file up best-effort.
/// </para>
/// <para>
/// <b>Threading.</b> Members are not internally synchronized; <see cref="AutosaveService"/>
/// serializes all journal operations on its single drainer. The journal directory is re-resolved
/// from the provider on every operation (never cached), per the provider's contract.
/// </para>
/// </remarks>
public sealed class AutosaveJournal
{
    /// <summary>The format version this build writes; readers reject other versions.</summary>
    public const int SchemaVersion = 1;

    private const string Magic = "cursorialedit-journal";
    private const string ChecksumPrefix = "sha256:";

    private readonly IAppStatePathProvider _paths;

    /// <summary>Creates the journal handle for <paramref name="key"/> under <paramref name="paths"/>.</summary>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public AutosaveJournal(IAppStatePathProvider paths, DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(key);

        _paths = paths;
        Key = key;
    }

    /// <summary>The document identity this journal is keyed to.</summary>
    public DocumentKey Key { get; }

    /// <summary>Resolves the journal's current full path through the provider (not cached).</summary>
    /// <exception cref="Exception">Whatever the provider throws when resolution fails.</exception>
    public string GetJournalPath() => Path.Combine(_paths.GetJournalDirectory(), Key.JournalFileName);

    /// <summary>Whether a journal file currently exists for this key.</summary>
    public bool Exists() => File.Exists(GetJournalPath());

    /// <summary>
    /// Atomically writes <paramref name="snapshot"/> as the journal's new content: serialize,
    /// checksum, write header + content to a temp file in the journal directory, flush to disk,
    /// rename over the journal. Any-thread safe in itself, but callers must not overlap writes
    /// to the same journal (the service's drainer guarantees this).
    /// </summary>
    /// <param name="snapshot">The snapshot to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The write or rename failed; the previous journal (if any) is intact.</exception>
    public void Write(AutosaveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string directory = _paths.GetJournalDirectory();
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, Key.JournalFileName);

        byte[] content = Encoding.UTF8.GetBytes(snapshot.BuildText());
        string checksum = Convert.ToHexStringLower(SHA256.HashData(content));

        string header =
            $"{Magic} v{SchemaVersion}\n" +
            $"source: {EscapeValue(Key.Descriptor)}\n" +
            $"timestamp: {snapshot.Timestamp.ToString("O", CultureInfo.InvariantCulture)}\n" +
            $"checksum: {ChecksumPrefix}{checksum}\n" +
            $"length: {content.Length.ToString(CultureInfo.InvariantCulture)}\n" +
            "\n";

        // Unique temp name per write: concurrent processes (or an abandoned crash residue)
        // can never collide, and the rename is the single visible mutation.
        string temp = Path.Combine(directory, $"{Key.JournalFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(header));
                stream.Write(content);
                stream.Flush(flushToDisk: true); // temp is durable before it becomes the journal
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp); // best-effort: no residue after a failed write
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            throw;
        }
    }

    /// <summary>
    /// Reads and verifies the journal. <see langword="false"/> — journal treated as absent —
    /// when the file is missing, has an unknown schema, is malformed or truncated, or fails the
    /// checksum; only provider/IO failures beyond "not there" propagate.
    /// </summary>
    /// <param name="record">The verified journal on success.</param>
    public bool TryRead([NotNullWhen(true)] out AutosaveJournalRecord? record)
    {
        record = null;
        string path = GetJournalPath();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        // Locate the header/content boundary: the first empty header line ("\n\n").
        int boundary = FindHeaderEnd(bytes);
        if (boundary < 0)
            return false;

        string[] headerLines = Encoding.UTF8.GetString(bytes, 0, boundary).Split('\n');
        if (headerLines.Length == 0 || headerLines[0] != $"{Magic} v{SchemaVersion}")
            return false;

        string? source = null, timestampText = null, checksumText = null, lengthText = null;
        foreach (string line in headerLines.Skip(1))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
                return false;

            string value = line[(colon + 2)..];
            switch (line[..colon])
            {
                case "source": source = value; break;
                case "timestamp": timestampText = value; break;
                case "checksum": checksumText = value; break;
                case "length": lengthText = value; break;
                default: return false; // v1 has no other keys; unknown = not ours
            }
        }

        if (source is null || timestampText is null || checksumText is null || lengthText is null)
            return false;

        if (!DateTimeOffset.TryParseExact(
                timestampText, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            return false;

        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out int length))
            return false;

        int contentStart = boundary + 2; // past "\n\n"
        if (bytes.Length - contentStart != length)
            return false; // truncated or trailing garbage

        var content = bytes.AsSpan(contentStart, length);
        if (!checksumText.StartsWith(ChecksumPrefix, StringComparison.Ordinal)
            || !string.Equals(
                checksumText[ChecksumPrefix.Length..],
                Convert.ToHexStringLower(SHA256.HashData(content)),
                StringComparison.Ordinal))
        {
            return false;
        }

        record = new AutosaveJournalRecord(UnescapeValue(source), timestamp, Encoding.UTF8.GetString(content));
        return true;
    }

    /// <summary>Removes the journal file if present (clean save/close). Idempotent.</summary>
    public void Delete()
    {
        string path = GetJournalPath();
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Journal directory never created: nothing to remove.
        }
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (int i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte) '\n' && bytes[i - 1] == (byte) '\n')
                return i - 1;
        }

        return -1;
    }

    /// <summary>
    /// Escapes the characters that would corrupt the line-oriented header — <c>%</c>, CR, LF
    /// (all legal in Unix paths) — with percent-encoding; everything else stays readable.
    /// </summary>
    private static string EscapeValue(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
             .Replace("\r", "%0D", StringComparison.Ordinal)
             .Replace("\n", "%0A", StringComparison.Ordinal);

    private static string UnescapeValue(string value) =>
        value.Replace("%0A", "\n", StringComparison.Ordinal)
             .Replace("%0D", "\r", StringComparison.Ordinal)
             .Replace("%25", "%", StringComparison.Ordinal);
}
