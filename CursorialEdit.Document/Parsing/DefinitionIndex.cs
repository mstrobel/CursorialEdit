using System.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Document.Parsing;

/// <summary>
/// The result of reconciling a definition table against a new block list: whether the definition
/// signature set changed at all, and — when it did — the blocks that reference a changed label and
/// therefore need their inlines re-realized (architecture §2.2 step 4).
/// </summary>
/// <param name="SetChanged">Whether any definition label was added, removed, or retargeted since the previous reconcile.</param>
/// <param name="InvalidatedBlocks">
/// The ids of blocks that reference a changed label (a conservative superset found by a cheap source
/// scan — over-invalidation only costs an extra re-realization, never correctness). Empty when
/// <paramref name="SetChanged"/> is <see langword="false"/>.
/// </param>
public readonly record struct DefinitionDelta(bool SetChanged, IReadOnlyList<BlockId> InvalidatedBlocks)
{
    /// <summary>A no-change delta.</summary>
    public static DefinitionDelta None { get; } = new(false, []);
}

/// <summary>
/// A label→referencing-blocks index for one family of document-global markdown definition
/// (architecture §2.2 step 4 / Decision 3). Because a definition (link reference, footnote) can change
/// the rendering of a block anywhere in the document without changing any block's <i>structure</i>, an
/// edit that changes the definition set must invalidate exactly the blocks that reference the changed
/// labels — their cached inline runs are stale. Definition-bearing documents take the producer's
/// synchronous full-parse path, so the fresh ASTs are installed the same frame; this index computes
/// which referencing blocks to re-realize.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cheap by design.</b> Both the definition signatures and the reference candidates are read from
/// block <i>source</i> — never by realizing inlines (which would break Decision 5's laziness and cost a
/// full inline pass per keystroke). Reference matching is a conservative superset: any bracketed label
/// shape in a block's source that normalizes to a changed label invalidates the block. Over-matching
/// re-realizes a few extra blocks; it never misses one.
/// </para>
/// </remarks>
public abstract class DefinitionTable
{
    private Dictionary<string, string> _signatures = new(StringComparer.Ordinal);

    /// <summary>The current normalized definition labels (test/status seam).</summary>
    public IReadOnlyCollection<string> Labels => _signatures.Keys;

    /// <summary>
    /// Reconciles the table against <paramref name="blocks"/> and reports what changed. Rebuilds the
    /// signature map from the definition blocks, diffs it against the previous map, and — when the set
    /// changed — scans every referencing block's source for the changed labels.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> or <paramref name="buffer"/> is <see langword="null"/>.</exception>
    public DefinitionDelta Update(BlockList blocks, IDocumentBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(buffer);

        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!IsDefinitionKind(blocks[i].Kind))
                continue;

            string source = BlockSource(blocks, buffer, i);
            if (TryReadSignature(source, out string? label, out string? signature) && !next.ContainsKey(label))
                next[label] = signature; // FIRST definition of a label wins, matching Markdig/CommonMark
                                         // — so editing the effective (first) duplicate is detected as a change
        }

        var changedLabels = DiffLabels(_signatures, next);
        _signatures = next;

        if (changedLabels.Count == 0)
            return DefinitionDelta.None;

        var invalidated = new List<BlockId>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (IsDefinitionKind(blocks[i].Kind))
                continue;

            string source = BlockSource(blocks, buffer, i);
            if (ReferencesAny(source, changedLabels))
                invalidated.Add(blocks[i].Id);
        }

        return new DefinitionDelta(SetChanged: true, invalidated);
    }

    /// <summary>
    /// The ids of every block that references any <b>currently defined</b> label — the blocks whose
    /// inline runs a full reparse must refresh so their references resolve against the live definition
    /// set. A conservative source-scan superset, like <see cref="Update"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> or <paramref name="buffer"/> is <see langword="null"/>.</exception>
    public void CollectReferencingBlocks(BlockList blocks, IDocumentBuffer buffer, ISet<BlockId> into)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(into);

        if (_signatures.Count == 0)
            return;

        var labels = new HashSet<string>(_signatures.Keys, StringComparer.Ordinal);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (IsDefinitionKind(blocks[i].Kind))
                continue;

            if (ReferencesAny(BlockSource(blocks, buffer, i), labels))
                into.Add(blocks[i].Id);
        }
    }

    /// <summary>Discards all recorded signatures (e.g. on loading a different document).</summary>
    public void Clear() => _signatures = new Dictionary<string, string>(StringComparer.Ordinal);

    // ── family-specific hooks ──

    /// <summary>Whether <paramref name="kind"/> is a definition block of this family.</summary>
    protected abstract bool IsDefinitionKind(BlockKind kind);

    /// <summary>Extracts the normalized label and a rendering-affecting signature from a definition block's source.</summary>
    protected abstract bool TryReadSignature(string source, out string label, out string signature);

    /// <summary>Whether <paramref name="source"/> references any of <paramref name="labels"/> (normalized).</summary>
    protected abstract bool ReferencesAny(string source, IReadOnlySet<string> labels);

    // ── shared helpers ──

    /// <summary>CommonMark label normalization: trim, collapse internal whitespace, case-fold.</summary>
    protected static string NormalizeLabel(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        bool pendingSpace = false;
        foreach (char c in raw.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');

            pendingSpace = false;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static string BlockSource(BlockList blocks, IDocumentBuffer buffer, int index)
    {
        int start = blocks.GetStartLine(index);
        int count = blocks[index].LineCount;
        var sb = new StringBuilder();
        for (int line = start; line < start + count; line++)
            sb.Append(buffer.GetLine(line).Text).Append('\n');

        return sb.ToString();
    }

    private static HashSet<string> DiffLabels(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, sig) in after)
        {
            if (!before.TryGetValue(label, out var old) || !string.Equals(old, sig, StringComparison.Ordinal))
                changed.Add(label);
        }

        foreach (var label in before.Keys)
        {
            if (!after.ContainsKey(label))
                changed.Add(label);
        }

        return changed;
    }
}

/// <summary>
/// The link-reference-definition table (<c>[label]: url "title"</c>): label → referencing blocks, so an
/// edit to a definition invalidates the blocks whose reference links resolve against it.
/// </summary>
public sealed class LinkRefTable : DefinitionTable
{
    /// <inheritdoc/>
    protected override bool IsDefinitionKind(BlockKind kind) => kind == BlockKind.LinkReferenceDefinition;

    /// <inheritdoc/>
    protected override bool TryReadSignature(string source, out string label, out string signature)
    {
        label = string.Empty;
        signature = string.Empty;

        // Shape: optional spaces, '[' label ']' ':' destination [ title ]. A footnote definition
        // ('[^…]:') is not a link reference — its '[' is followed by '^'.
        int open = source.IndexOf('[');
        if (open < 0 || open + 1 >= source.Length || source[open + 1] == '^')
            return false;

        int close = source.IndexOf(']', open + 1);
        if (close < 0 || close + 1 >= source.Length || source[close + 1] != ':')
            return false;

        label = NormalizeLabel(source[(open + 1)..close]);
        if (label.Length == 0)
            return false;

        // The destination+title is the rendering-affecting part; whitespace-normalize it for stability.
        signature = NormalizeLabel(source[(close + 2)..]);
        return true;
    }

    /// <inheritdoc/>
    protected override bool ReferencesAny(string source, IReadOnlySet<string> labels)
    {
        // Any bracketed run that is not a footnote reference is a candidate reference label (full,
        // collapsed, or shortcut form) — a conservative superset.
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '[' || (i + 1 < source.Length && source[i + 1] == '^'))
                continue;

            int close = source.IndexOf(']', i + 1);
            if (close < 0)
                break;

            if (labels.Contains(NormalizeLabel(source[(i + 1)..close])))
                return true;

            i = close;
        }

        return false;
    }
}

/// <summary>
/// The footnote-definition table (<c>[^label]: …</c>): label → referencing blocks, so an edit to a
/// footnote definition invalidates the blocks that carry its <c>[^label]</c> reference.
/// </summary>
public sealed class FootnoteTable : DefinitionTable
{
    /// <inheritdoc/>
    protected override bool IsDefinitionKind(BlockKind kind) => kind == BlockKind.Footnote;

    /// <inheritdoc/>
    protected override bool TryReadSignature(string source, out string label, out string signature)
    {
        label = string.Empty;
        signature = string.Empty;

        int open = source.IndexOf("[^", StringComparison.Ordinal);
        if (open < 0)
            return false;

        int close = source.IndexOf(']', open + 2);
        if (close < 0 || close + 1 >= source.Length || source[close + 1] != ':')
            return false;

        label = NormalizeLabel(source[(open + 2)..close]);
        if (label.Length == 0)
            return false;

        // A footnote's presence is its whole signal to referencing blocks (the back-reference resolves
        // or it does not); its body is inline content that reference blocks do not depend on.
        signature = "present";
        return true;
    }

    /// <inheritdoc/>
    protected override bool ReferencesAny(string source, IReadOnlySet<string> labels)
    {
        for (int i = source.IndexOf("[^", StringComparison.Ordinal); i >= 0; i = source.IndexOf("[^", i + 2, StringComparison.Ordinal))
        {
            int close = source.IndexOf(']', i + 2);
            if (close < 0)
                break;

            if (labels.Contains(NormalizeLabel(source[(i + 2)..close])))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Composes the <see cref="LinkRefTable"/> and <see cref="FootnoteTable"/> — the two document-global
/// definition families the pinned pipeline recognises — into one reconcile the block producer runs
/// after every edit (architecture §2.2 step 4). A change in either escalates: the referencing blocks
/// are reported invalidated for the synchronous frame, and a debounced full reparse is scheduled.
/// </summary>
public sealed class DefinitionIndex
{
    private readonly LinkRefTable _links = new();
    private readonly FootnoteTable _footnotes = new();

    /// <summary>The link-reference-definition table.</summary>
    public LinkRefTable Links => _links;

    /// <summary>The footnote-definition table.</summary>
    public FootnoteTable Footnotes => _footnotes;

    /// <summary>
    /// Reconciles both tables against <paramref name="blocks"/> and returns the union delta: the set
    /// changed if either family's did, and the invalidated blocks are the union.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> or <paramref name="buffer"/> is <see langword="null"/>.</exception>
    public DefinitionDelta Update(BlockList blocks, IDocumentBuffer buffer)
    {
        var linkDelta = _links.Update(blocks, buffer);
        var footnoteDelta = _footnotes.Update(blocks, buffer);

        if (!linkDelta.SetChanged && !footnoteDelta.SetChanged)
            return DefinitionDelta.None;

        var seen = new HashSet<BlockId>(linkDelta.InvalidatedBlocks);
        var union = new List<BlockId>(linkDelta.InvalidatedBlocks);
        foreach (var id in footnoteDelta.InvalidatedBlocks)
        {
            if (seen.Add(id))
                union.Add(id);
        }

        return new DefinitionDelta(SetChanged: true, union);
    }

    /// <summary>The ids of blocks referencing any currently defined link-reference or footnote label (union of both tables).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> or <paramref name="buffer"/> is <see langword="null"/>.</exception>
    public IReadOnlyCollection<BlockId> ReferencingBlocks(BlockList blocks, IDocumentBuffer buffer)
    {
        var into = new HashSet<BlockId>();
        _links.CollectReferencingBlocks(blocks, buffer, into);
        _footnotes.CollectReferencingBlocks(blocks, buffer, into);
        return into;
    }

    /// <summary>Discards all recorded signatures in both tables.</summary>
    public void Clear()
    {
        _links.Clear();
        _footnotes.Clear();
    }
}
