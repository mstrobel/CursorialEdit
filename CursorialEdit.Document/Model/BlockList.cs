using System.Collections;

namespace CursorialEdit.Document.Model;

/// <summary>
/// The ordered list of document blocks, owned and mutated by exactly one producer
/// (<see cref="PlainTextBlockProducer"/> in M1; the Markdig-backed producer in M2). Blocks store
/// line <i>counts</i>; this list derives start lines as prefix sums recomputed lazily from the
/// edit point (architecture Decision 8), and offers lookup by source line and by
/// <see cref="BlockId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tiling invariant.</b> The blocks partition the buffer's lines: block <c>i</c> owns lines
/// <c>[GetStartLine(i), GetStartLine(i) + this[i].LineCount)</c>, consecutively, with
/// <see cref="TotalLineCount"/> equal to the buffer's line count. The producer maintains this
/// after every splice; <see cref="IndexOfLine"/> is total over <c>[0, TotalLineCount)</c> by
/// construction.
/// </para>
/// <para>
/// All members are UI-thread-only, like the buffer the blocks describe. Consumers other than the
/// owning producer treat the list as read-only; they learn about mutations through the producer's
/// <see cref="BlockListChange"/> notification, which fires <i>after</i> the list is consistent.
/// </para>
/// </remarks>
public sealed class BlockList : IReadOnlyList<Block>
{
    private readonly List<Block> _blocks = [];

    /// <summary>Prefix sums: entry <c>i</c> is the start line of block <c>i</c>; entry <c>Count</c> is the total line count.</summary>
    private int[] _startLines = new int[1];

    /// <summary>Count of valid leading entries in <see cref="_startLines"/>; entry 0 (== 0) is always valid.</summary>
    private int _validStarts = 1;

    private readonly Dictionary<BlockId, int> _indexById = [];
    private bool _indexDirty;

    /// <summary>Number of full id-index rebuilds performed by <see cref="IndexOf"/> (test seam: the typing path must not rebuild).</summary>
    internal int IndexRebuildCount { get; private set; }

    /// <summary>The number of blocks.</summary>
    public int Count => _blocks.Count;

    /// <summary>The total number of source lines the blocks tile (the buffer's line count, by invariant).</summary>
    public int TotalLineCount => GetStartLine(Count);

    /// <summary>The block at <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public Block this[int index] => _blocks[index];

    /// <summary>
    /// The start line of block <paramref name="index"/> — a prefix sum over the preceding blocks'
    /// line counts. <paramref name="index"/> may equal <see cref="Count"/>, addressing the
    /// one-past-the-end boundary (== <see cref="TotalLineCount"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or exceeds <see cref="Count"/>.</exception>
    public int GetStartLine(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _blocks.Count);

        EnsureStarts(index);
        return _startLines[index];
    }

    /// <summary>
    /// The index of the block owning source <paramref name="line"/> — the largest <c>i</c> with
    /// <c>GetStartLine(i) &lt;= line</c>. Total over the tiled range by the tiling invariant.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="line"/> is negative or ≥ <see cref="TotalLineCount"/>.</exception>
    public int IndexOfLine(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        if (line >= TotalLineCount) // materializes every start — the binary search below reads warm entries
            throw new ArgumentOutOfRangeException(nameof(line), line, $"The blocks tile {TotalLineCount} lines.");

        int lo = 0, hi = _blocks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_startLines[mid] <= line)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>The index of the block with identity <paramref name="id"/>, or −1 when no such block exists.</summary>
    public int IndexOf(BlockId id)
    {
        if (_indexDirty)
        {
            _indexById.Clear();
            for (var i = 0; i < _blocks.Count; i++)
                _indexById.Add(_blocks[i].Id, i); // Add throws on a duplicate id — an invariant violation, failed loudly

            _indexDirty = false;
            IndexRebuildCount++;
        }

        return _indexById.TryGetValue(id, out var index) ? index : -1;
    }

    /// <summary>
    /// The producer's single mutation primitive: replaces <paramref name="removedCount"/> blocks
    /// at <paramref name="index"/> with <paramref name="inserted"/>. Start-line prefix sums are
    /// invalidated from the edit point and rebuilt lazily. The id index is maintained
    /// incrementally when the block count is unchanged (the typing path: tail indices are
    /// untouched, so only the replaced window's entries move); a count-changing replacement
    /// shifts every tail index and falls back to the lazy full rebuild — the same cost class as
    /// the list splice itself.
    /// </summary>
    internal void ReplaceRange(int index, int removedCount, IReadOnlyList<Block> inserted)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(removedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + removedCount, _blocks.Count);

        // Incremental id-index maintenance (review wave3-7): pointless on a dirty index (it is
        // rebuilt wholesale on the next IndexOf anyway), sound only when tail indices stay put.
        bool sameCount = !_indexDirty && inserted.Count == removedCount;
        if (sameCount)
        {
            for (var i = 0; i < removedCount; i++)
                _indexById.Remove(_blocks[index + i].Id);
        }

        _blocks.RemoveRange(index, removedCount);
        _blocks.InsertRange(index, inserted);

        // Entries 0..index stay valid (they sum blocks before the edit point); everything after is stale.
        _validStarts = Math.Min(_validStarts, index + 1);

        if (sameCount)
        {
            for (var i = 0; i < inserted.Count; i++)
                _indexById.Add(inserted[i].Id, index + i); // Add stays loud on duplicate ids, like the rebuild
        }
        else
        {
            _indexDirty = true;
        }
    }

    private void EnsureStarts(int through)
    {
        // Doubling growth, like DocumentBuffer's offset cache — amortized O(1) per appended block
        // instead of an O(N) reallocation per block-count increase (review wave3-8).
        if (_startLines.Length < _blocks.Count + 1)
            Array.Resize(ref _startLines, Math.Max(_blocks.Count + 1, _startLines.Length * 2));

        while (_validStarts <= through)
        {
            _startLines[_validStarts] = _startLines[_validStarts - 1] + _blocks[_validStarts - 1].LineCount;
            _validStarts++;
        }
    }

    /// <inheritdoc/>
    public IEnumerator<Block> GetEnumerator() => _blocks.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
