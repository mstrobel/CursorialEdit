using System.Security.Cryptography;
using System.Text;

namespace CursorialEdit.Document.Persistence;

/// <summary>
/// The identity a journal is keyed to (spec §12): a saved document keys by its canonicalized
/// path, an untitled document by a generated id, and the journal filename derives from that key
/// so relaunch/reopen can find the journal without any registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Path keys.</b> The path is canonicalized (<see cref="Path.GetFullPath(string)"/>, plus
/// final-symlink-target resolution when the file exists, plus casing canonicalization — see
/// below) and the journal filename is the lowercase SHA-256 hex of the canonical path's UTF-8
/// bytes — collision-proof against exotic filenames, never leaks path characters into the
/// journal directory, and identical for every spelling of the same file. Full realpath
/// canonicalization of parent-directory symlink chains is finalized by M6.WP3 behind this same
/// type; the filename derivation carries forward.
/// </para>
/// <para>
/// <b>Casing.</b> On the case-insensitive-by-default platforms (Windows, macOS) two casings of
/// one file must yield one key, so each path segment resolves to its true on-disk casing while
/// the on-disk entry exists; once a segment does not resolve (not created yet, unreadable
/// directory), the remainder falls back to an ordinal-ignore-case lower fold — every spelling
/// still converges, at the cost of merging names that a case-<i>sensitive</i> volume mounted on
/// those platforms would keep distinct. Linux keys stay exact: the filesystem is case-sensitive,
/// so distinct casings ARE distinct files. Residual, documented limitations: per-directory
/// case-sensitivity exotica (ext4 casefold dirs, Windows per-dir flags) are out of scope, and a
/// key minted for a <i>not-yet-existing</i> file uses the folded spelling, which matches the
/// later true-cased key only when the file is created with fold-equal casing (M6.WP3's realpath
/// finalization owns closing that seam).
/// </para>
/// <para>
/// <b>Untitled keys.</b> A fresh GUID per untitled buffer; the filename keeps a literal
/// <c>untitled-</c> prefix so M6's launch-time recovery scan can enumerate untitled journals
/// without reading headers.
/// </para>
/// <para>Immutable and equatable by journal filename (which encodes the whole identity).</para>
/// </remarks>
public sealed class DocumentKey : IEquatable<DocumentKey>
{
    private const string JournalExtension = ".journal";

    private DocumentKey(string? sourcePath, Guid? untitledId, string journalFileName, string descriptor)
    {
        SourcePath = sourcePath;
        UntitledId = untitledId;
        JournalFileName = journalFileName;
        Descriptor = descriptor;
    }

    /// <summary>The canonicalized source path, or <see langword="null"/> for an untitled key.</summary>
    public string? SourcePath { get; }

    /// <summary>The generated untitled id, or <see langword="null"/> for a path key.</summary>
    public Guid? UntitledId { get; }

    /// <summary>The journal's filename (no directory): SHA-256 hex + <c>.journal</c> for paths, <c>untitled-&lt;id&gt;.journal</c> for untitled.</summary>
    public string JournalFileName { get; }

    /// <summary>
    /// The human-readable identity written into the journal header's <c>source</c> field:
    /// <c>path:&lt;canonical path&gt;</c> or <c>untitled:&lt;id&gt;</c>.
    /// </summary>
    public string Descriptor { get; }

    /// <summary>Whether this key identifies an untitled (never-saved) document.</summary>
    public bool IsUntitled => UntitledId is not null;

    /// <summary>Creates the key for a document at <paramref name="path"/> (need not exist yet).</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static DocumentKey ForPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string canonical = Canonicalize(path);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new DocumentKey(canonical, null, hash + JournalExtension, "path:" + canonical);
    }

    /// <summary>Creates a fresh key for an untitled document.</summary>
    public static DocumentKey NewUntitled() => ForUntitled(Guid.NewGuid());

    /// <summary>Recreates the key for a known untitled id (M6 recovery uses this to re-adopt a journal).</summary>
    public static DocumentKey ForUntitled(Guid id)
    {
        string idText = id.ToString("N");
        return new DocumentKey(null, id, "untitled-" + idText + JournalExtension, "untitled:" + idText);
    }

    private static string Canonicalize(string path)
    {
        string full = Path.GetFullPath(path);

        try
        {
            if (File.Exists(full) && File.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
                full = target.FullName;
        }
        catch (IOException)
        {
            // Unresolvable link chain: key by the full path — stable, just less aggressive.
        }
        catch (UnauthorizedAccessException)
        {
            // Same fallback: the full path is still a stable key.
        }

        return CanonicalizeCasing(full);
    }

    /// <summary>
    /// Casing canonicalization for case-insensitive-by-default platforms (see the class remarks):
    /// true on-disk casing per existing segment, ordinal lower fold for the rest. Exact on Linux.
    /// </summary>
    private static string CanonicalizeCasing(string full)
    {
        // Linux: case-sensitive — every casing is a distinct file, so the exact path IS the key.
        // (Per-directory case-insensitivity exotica — ext4 casefold etc. — are out of scope.)
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return full;

        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full.ToLowerInvariant(); // no resolvable root (defensive): the fold alone still converges spellings

        // The root itself folds (drive letters and UNC server/share names are case-insensitive;
        // "/" is caseless), then each segment true-cases against its parent while it exists.
        var canonical = root.ToLowerInvariant();
        var resolving = true;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (resolving && TryResolveTrueCasing(canonical, segment, out var trueCased))
            {
                canonical = Path.Join(canonical, trueCased);
            }
            else
            {
                // Not on disk (yet) or unreadable: fold this and everything after it — a fixed
                // rule keeps the key identical for every spelling of the same remainder.
                resolving = false;
                canonical = Path.Join(canonical, segment.ToLowerInvariant());
            }
        }

        return canonical;
    }

    /// <summary>
    /// Finds the on-disk entry of <paramref name="directory"/> whose name equals
    /// <paramref name="segment"/> ordinal-ignore-case, returning its true casing. False when the
    /// entry does not exist, the directory is unreadable, or the filesystem's own equivalence
    /// (Unicode normalization, culture folds) matched a name ordinal comparison cannot confirm —
    /// callers fall back to the fold, which is stable either way.
    /// </summary>
    private static bool TryResolveTrueCasing(string directory, string segment, out string trueCased)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    trueCased = name;
                    return true;
                }
            }
        }
        catch (IOException)
        {
            // Fall through to the fold fallback.
        }
        catch (UnauthorizedAccessException)
        {
            // Same fallback.
        }

        trueCased = string.Empty;
        return false;
    }

    /// <inheritdoc/>
    public bool Equals(DocumentKey? other) =>
        other is not null && string.Equals(JournalFileName, other.JournalFileName, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DocumentKey);

    /// <inheritdoc/>
    public override int GetHashCode() => JournalFileName.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Descriptor;
}
