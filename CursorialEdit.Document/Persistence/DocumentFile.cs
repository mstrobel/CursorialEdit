using System.Text;

using CursorialEdit.Document.Buffer;

using IOPath = System.IO.Path;

namespace CursorialEdit.Document.Persistence;

/// <summary>
/// One document's file identity plus everything detected at load time that a faithful save must
/// re-emit (implementation-plan §6 M1.WP11, spec §10.1): the <see cref="DetectedEncoding"/>, whether
/// the file carried a BOM (<see cref="HadBom"/> — preserved, never added), and the
/// <see cref="DominantEnding"/> used only for the optional ensure-trailing-newline terminator.
/// Per-line endings themselves are never normalized — they live in the <see cref="Line"/> table and
/// re-serialize byte-exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Round-trip guarantee.</b> <see cref="Load"/> followed by <see cref="Save"/> of the unedited
/// buffer produces a byte-identical file, in every detected encoding — modulo only the
/// ensure-trailing-newline option, which (when enabled, the spec default) appends exactly one
/// <see cref="DominantEnding"/> terminator to a file that lacked one. The guarantee holds for
/// well-formed content; the documented degradation points are listed on <see cref="Load"/>.
/// </para>
/// <para>
/// <b>Encoding detection (spec §10.1 decision: UTF-8/UTF-16 on open, others deferred).</b>
/// BOM-first: <c>EF BB BF</c> → UTF-8, <c>FF FE</c> → UTF-16 LE, <c>FE FF</c> → UTF-16 BE. A
/// BOM-less file is decoded as <i>strict</i> UTF-8; if its bytes are not well-formed UTF-8 it
/// falls back to Latin-1 (<see cref="DocumentEncoding.Latin1"/>) rather than a lossy
/// replacement-character decode, so unknown single-byte content round-trips byte-identically
/// instead of being corrupted. BOM-less UTF-16 is not heuristically sniffed (it lands in the
/// Latin-1 fallback) — a documented v1 limitation consistent with the spec's BOM-oriented wording.
/// </para>
/// <para>
/// <b>Atomicity.</b> <see cref="Save"/> writes to a uniquely named temp file <i>in the
/// destination directory</i> (same volume — the rename never crosses filesystems), flushes to
/// disk, then <see cref="File.Move(string, string, bool)"/> replaces the destination in one step:
/// a reader never observes partial content and a failed save leaves the previous file intact,
/// cleaning its temp file up best-effort. The same idiom as <see cref="AutosaveJournal"/>.
/// </para>
/// <para>
/// <b>Threading.</b> Immutable after construction; <see cref="Save"/> reads the UI-thread-only
/// buffer, so call it on the UI thread (M1 saves are small and synchronous — spec §10.2's ~1 MB
/// ceiling; M6 owns any off-thread save pipeline).
/// </para>
/// </remarks>
public sealed class DocumentFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private DocumentFile(string path, DocumentEncoding detectedEncoding, bool hadBom, LineEnding dominantEnding, string text)
    {
        Path = path;
        DetectedEncoding = detectedEncoding;
        HadBom = hadBom;
        DominantEnding = dominantEnding;
        Text = text;
    }

    /// <summary>The document's absolute path (<see cref="IOPath.GetFullPath(string)"/> of the path it was created with).</summary>
    public string Path { get; }

    /// <summary>The encoding detected on load (or <see cref="DocumentEncoding.Utf8"/> for a new file); saves re-emit it.</summary>
    public DocumentEncoding DetectedEncoding { get; }

    /// <summary>Whether the loaded file carried a BOM. Preserved exactly: re-emitted on save iff present on load, never added.</summary>
    public bool HadBom { get; }

    /// <summary>
    /// The file's dominant line ending — the more frequent of LF/CRLF, a tie going to the file's
    /// <i>first</i> terminator, and a terminator-less (or new) file taking the platform default
    /// (spec §10.1's new-file decision). Used <b>only</b> for the terminator that
    /// ensure-trailing-newline appends; existing endings are preserved per line.
    /// </summary>
    public LineEnding DominantEnding { get; }

    /// <summary>The decoded document text (BOM excluded) — feed this to <see cref="DocumentBuffer"/>.</summary>
    public string Text { get; }

    /// <summary>The platform-native default ending for files with no detectable ending (spec §10.1).</summary>
    internal static LineEnding PlatformDefaultEnding => OperatingSystem.IsWindows() ? LineEnding.CrLf : LineEnding.Lf;

    /// <summary>
    /// Loads and decodes the file at <paramref name="path"/> per the class detection rules.
    /// </summary>
    /// <remarks>
    /// Documented degradation points (each keeps the load succeeding rather than failing the
    /// open): a BOM commits to its encoding, so ill-formed sequences <i>after</i> a UTF-8 BOM
    /// decode with U+FFFD replacement; a UTF-16 payload with an odd trailing byte decodes that
    /// byte as one U+FFFD; unpaired UTF-16 surrogates are preserved as-is (they round-trip
    /// byte-exactly). Only the replacement cases forfeit byte-identical round-trip.
    /// </remarks>
    /// <param name="path">The file to load; must exist.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static DocumentFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string fullPath = IOPath.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);

        DocumentEncoding encoding;
        bool hadBom;
        string text;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = DocumentEncoding.Utf8;
            hadBom = true;
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); // BOM committed: replacement decode
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = DocumentEncoding.Utf16LittleEndian;
            hadBom = true;
            text = DecodeUtf16(bytes.AsSpan(2), bigEndian: false);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = DocumentEncoding.Utf16BigEndian;
            hadBom = true;
            text = DecodeUtf16(bytes.AsSpan(2), bigEndian: true);
        }
        else
        {
            hadBom = false;
            try
            {
                text = StrictUtf8.GetString(bytes);
                encoding = DocumentEncoding.Utf8;
            }
            catch (DecoderFallbackException)
            {
                // Not well-formed UTF-8: the byte-preserving Latin-1 fallback (class remarks).
                text = Encoding.Latin1.GetString(bytes);
                encoding = DocumentEncoding.Latin1;
            }
        }

        return new DocumentFile(fullPath, encoding, hadBom, DetectDominantEnding(text), text);
    }

    /// <summary>
    /// Creates the identity for a document that does not exist on disk yet — the open-as-new path
    /// for a CLI argument naming a missing file. Nothing is written until <see cref="Save"/>.
    /// Defaults per spec §10.1: UTF-8, no BOM, platform-native <see cref="DominantEnding"/>.
    /// </summary>
    /// <param name="path">The path the document will be saved to.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public static DocumentFile CreateNew(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new DocumentFile(IOPath.GetFullPath(path), DocumentEncoding.Utf8, hadBom: false, PlatformDefaultEnding, string.Empty);
    }

    /// <summary>
    /// Atomically saves <paramref name="buffer"/> to <see cref="Path"/>: serializes the line
    /// table (<see cref="IDocumentBuffer.GetText()"/> — the <see cref="Line.Serialize"/>
    /// reassembly rule, per-line endings byte-exact), optionally ensures a trailing newline,
    /// re-encodes in <see cref="DetectedEncoding"/> re-emitting the BOM iff <see cref="HadBom"/>,
    /// and writes temp-then-move in the destination directory (class remarks). Creates the
    /// destination directory on demand (the CLI accepts not-yet-existing paths). UI thread only
    /// (reads the buffer).
    /// </summary>
    /// <param name="buffer">The document to serialize.</param>
    /// <param name="ensureTrailingNewline">
    /// Whether to guarantee the file ends with exactly one newline (spec §10.1, default on): a
    /// non-empty document whose last line is unterminated gets one <see cref="DominantEnding"/>
    /// terminator appended <i>in the file</i> (the buffer is not mutated — the normalization is
    /// save-side). An empty document stays a zero-byte file, and a document whose final character
    /// is a bare <c>'\r'</c> (ordinary content, not a terminator) is left unterminated when the
    /// dominant ending is LF — appending <c>'\n'</c> there would fuse into a CRLF and change the
    /// content. Never doubles an existing trailing newline.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The write or rename failed; the previous file (if any) is intact.</exception>
    /// <exception cref="UnauthorizedAccessException">The destination is not writable; the previous file (if any) is intact.</exception>
    public void Save(IDocumentBuffer buffer, bool ensureTrailingNewline = true)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        string text = buffer.GetText();

        if (ensureTrailingNewline
            && text.Length > 0
            && !text.EndsWith('\n')
            && !(text.EndsWith('\r') && DominantEnding != LineEnding.CrLf))
        {
            text += DominantEnding == LineEnding.CrLf ? "\r\n" : "\n";
        }

        byte[] payload = Encode(text);

        string directory = IOPath.GetDirectoryName(Path) is { Length: > 0 } parent
            ? parent
            : throw new IOException($"Cannot resolve the containing directory of '{Path}'.");
        Directory.CreateDirectory(directory);

        // Unique temp name per save, in the destination directory: concurrent writers (or crash
        // residue) never collide, and the rename is the single visible mutation.
        string temp = IOPath.Combine(directory, $"{IOPath.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true); // the temp is durable before it becomes the document
            }

            // The temp inherits the process umask, so a plain rename would silently strip the
            // destination's mode (execute bit, group-write, setgid). Carry the existing file's
            // POSIX mode onto the temp before it becomes the document. Windows has no unix mode
            // (GetUnixFileMode throws) — skipped there. NOTE: an atomic temp-then-rename replaces
            // the inode, so it inherently breaks hard links to the destination (the other names
            // keep the pre-save content) — the standard trade-off for a crash-safe save; a
            // link-preserving in-place mode is deferred.
            if (!OperatingSystem.IsWindows() && File.Exists(Path))
            {
                try { File.SetUnixFileMode(temp, File.GetUnixFileMode(Path)); }
                catch (UnauthorizedAccessException) { } // best-effort — never fail a save over mode
                catch (IOException) { }
            }

            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp); // best-effort: no residue after a failed save
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            throw;
        }
    }

    // ---- Encoding ---------------------------------------------------------------------------

    private byte[] Encode(string text)
    {
        switch (DetectedEncoding)
        {
            case DocumentEncoding.Utf16LittleEndian:
                return EncodeUtf16(text, bigEndian: false, HadBom);

            case DocumentEncoding.Utf16BigEndian:
                return EncodeUtf16(text, bigEndian: true, HadBom);

            case DocumentEncoding.Latin1:
                // Chars ≤ U+00FF re-encode to the exact loaded bytes; anything typed beyond that
                // range replaces with '?' (see DocumentEncoding.Latin1's contract).
                return Encoding.Latin1.GetBytes(text);

            default:
                if (!HadBom)
                    return Encoding.UTF8.GetBytes(text);

                byte[] encoded = Encoding.UTF8.GetBytes(text);
                byte[] withBom = new byte[Utf8Bom.Length + encoded.Length];
                Utf8Bom.CopyTo(withBom, 0);
                encoded.CopyTo(withBom, Utf8Bom.Length);
                return withBom;
        }
    }

    /// <summary>
    /// Pairwise UTF-16 decode that — unlike <see cref="Encoding.Unicode"/> — preserves unpaired
    /// surrogates instead of replacing them, so a valid-on-disk-but-odd file still round-trips
    /// byte-exactly. An odd trailing byte (a truncated final code unit) decodes as one U+FFFD.
    /// </summary>
    private static string DecodeUtf16(ReadOnlySpan<byte> payload, bool bigEndian)
    {
        int units = payload.Length / 2;
        bool truncated = (payload.Length & 1) != 0;

        var chars = new char[units + (truncated ? 1 : 0)];
        for (var i = 0; i < units; i++)
        {
            int first = payload[2 * i];
            int second = payload[2 * i + 1];
            chars[i] = bigEndian ? (char)((first << 8) | second) : (char)(first | (second << 8));
        }

        if (truncated)
            chars[units] = '�';

        return new string(chars);
    }

    /// <summary>The pairwise inverse of <see cref="DecodeUtf16"/>, with the BOM re-emitted first when requested.</summary>
    private static byte[] EncodeUtf16(string text, bool bigEndian, bool withBom)
    {
        var bytes = new byte[(withBom ? 2 : 0) + text.Length * 2];
        var offset = 0;

        if (withBom)
        {
            bytes[0] = bigEndian ? (byte)0xFE : (byte)0xFF;
            bytes[1] = bigEndian ? (byte)0xFF : (byte)0xFE;
            offset = 2;
        }

        foreach (char c in text)
        {
            if (bigEndian)
            {
                bytes[offset++] = (byte)(c >> 8);
                bytes[offset++] = (byte)c;
            }
            else
            {
                bytes[offset++] = (byte)c;
                bytes[offset++] = (byte)(c >> 8);
            }
        }

        return bytes;
    }

    // ---- Ending detection -------------------------------------------------------------------

    /// <summary>
    /// Counts LF vs CRLF terminators (a lone <c>'\r'</c> is content, per <see cref="LineEnding"/>):
    /// majority wins, a tie goes to the file's first terminator, no terminators at all takes
    /// <see cref="PlatformDefaultEnding"/>.
    /// </summary>
    private static LineEnding DetectDominantEnding(string text)
    {
        int lf = 0, crlf = 0;
        var first = LineEnding.None;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            bool isCrLf = i > 0 && text[i - 1] == '\r';
            if (isCrLf)
                crlf++;
            else
                lf++;

            if (first == LineEnding.None)
                first = isCrLf ? LineEnding.CrLf : LineEnding.Lf;
        }

        if (lf == 0 && crlf == 0)
            return PlatformDefaultEnding;

        if (crlf != lf)
            return crlf > lf ? LineEnding.CrLf : LineEnding.Lf;

        return first;
    }
}
