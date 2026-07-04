using System.Diagnostics;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;

namespace CursorialEdit.Document.Model;

/// <summary>
/// M1's <b>degenerate, deliberately throwaway</b> block producer (implementation-plan §6 WP7):
/// segments the buffer into blank-line-separated paragraph runs with no parser, maintains the
/// <see cref="BlockList"/> across every <see cref="EditController.Changed"/> splice, and emits a
/// <see cref="BlockListChange"/> per splice. Only this <i>producer</i> is throwaway — the
/// <see cref="BlockList"/>/<see cref="BlockListChange"/> seam it feeds is exactly where M2's
/// Markdig-window producer drops in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Segmentation policy (documented per WP7).</b> A line is <i>blank</i> when its text is empty
/// or all whitespace (the markdown blank-line notion). Blocks tile the document: each block is a
/// maximal run of non-blank lines <b>plus all immediately following blank lines</b> (trailing
/// attachment — the blank separator belongs to the paragraph above it, so typing at a paragraph's
/// end never re-forms its successor). A blank run at the very start of the document has no
/// paragraph to attach to and forms a block of its own (a degenerate <see cref="BlockKind.Paragraph"/>
/// with no content lines). An empty document — one empty line — is one such block; the list is
/// never empty.
/// </para>
/// <para>
/// <b>Identity across edits (the simplest honest rule for plain text).</b> The dirty line range
/// is derived from the splice receipt; the buffer is re-segmented (a cheap full line scan — the
/// throwaway part) and reconciled against the previous list: blocks whose spans lie entirely
/// outside the dirty range and match the fresh segmentation keep their <see cref="BlockId"/>
/// (reported <see cref="BlockListChange.Reused"/>, shifting implicitly via prefix sums); the
/// blocks covering the edit window re-form, pairing old to new <b>in order</b> — the k-th old
/// window block hands its id to the k-th new window segment (<see cref="BlockListChange.Changed"/>),
/// surplus new segments are <see cref="BlockListChange.Added"/>, surplus old blocks are
/// <see cref="BlockListChange.Removed"/>. This positional pairing is honest but coarse (an edit
/// touching a block boundary can classify a merely shifted neighbor as Changed); M2's re-adoption
/// rule — match by (kind, first unmodified line) over per-line <c>Version</c> stamps — replaces it.
/// </para>
/// <para>All members are UI-thread-only, like the controller and buffer they observe.</para>
/// </remarks>
public sealed class PlainTextBlockProducer : IDisposable
{
    private readonly EditController _controller;
    private readonly IDocumentBuffer _buffer;
    private long _nextId = 1;
    private bool _disposed;

    /// <summary>
    /// Creates the producer over <paramref name="controller"/>'s buffer, segments the current
    /// document, and subscribes to <see cref="EditController.Changed"/> for live reconciliation.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is <see langword="null"/>.</exception>
    public PlainTextBlockProducer(EditController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        _controller = controller;
        _buffer = controller.Buffer;
        Blocks = new BlockList();

        var segments = Segment();
        var initial = new List<Block>(segments.Count);
        foreach (var (_, lineCount) in segments)
            initial.Add(new Block(new BlockId(_nextId++), BlockKind.Paragraph, lineCount));

        Blocks.ReplaceRange(0, 0, initial);
        AssertTiling();

        controller.Changed += OnSpliced;
    }

    /// <summary>The live block list this producer owns. Read-only to every other consumer.</summary>
    public BlockList Blocks { get; }

    /// <summary>
    /// Raised once per applied splice, after <see cref="Blocks"/> is consistent — the
    /// reconciliation feed the view applies presenter reuse/invalidation from.
    /// </summary>
    public event Action<BlockListChange>? Changed;

    /// <summary>Unsubscribes from the controller; the list freezes at its current state.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _controller.Changed -= OnSpliced;
    }

    // ───────────────────────────── reconciliation ─────────────────────────────

    private void OnSpliced(SpliceResult result)
    {
        // The dirty line range, from the splice receipt (post-splice coordinates for the new
        // range): nothing before result.StartOffset changed, so the line containing it indexes
        // identically pre- and post-splice; the removed text's '\n' count spans the old range
        // (CRLF pairs carry one '\n'; a lone '\r' is buffer text, never a terminator).
        int dirtyStart = _buffer.GetPosition(result.StartOffset).Line;
        int newDirtyEnd = result.End.Line;
        int oldDirtyEnd = dirtyStart + CountLineBreaks(result.RemovedText);
        int delta = newDirtyEnd - oldDirtyEnd;

        var segments = Segment(); // fresh truth — the throwaway full scan
        var old = Blocks;

        // Prefix: blocks strictly before the dirty range whose fresh segments are span-identical.
        // (`end <= dirtyStart` alone is not enough — the dirtyStart line's blank-ness decides
        // whether it attaches to the block above, so span equality against the fresh scan is the
        // authoritative check; the dirty-range check only upgrades the match to content-untouched.)
        int maxPairs = Math.Min(old.Count, segments.Count);
        int prefix = 0;
        while (prefix < maxPairs)
        {
            var (segStart, segCount) = segments[prefix];
            if (old.GetStartLine(prefix) != segStart
                || old[prefix].LineCount != segCount
                || segStart + segCount > dirtyStart)
            {
                break;
            }

            prefix++;
        }

        // Suffix: blocks strictly after the dirty range, matched from the tail, shifted by delta.
        int suffix = 0;
        while (suffix < Math.Min(old.Count, segments.Count) - prefix)
        {
            int oldIndex = old.Count - 1 - suffix;
            var (segStart, segCount) = segments[segments.Count - 1 - suffix];
            int oldStart = old.GetStartLine(oldIndex);
            if (old[oldIndex].LineCount != segCount
                || segStart != oldStart + delta
                || oldStart <= oldDirtyEnd)
            {
                break;
            }

            suffix++;
        }

        // Window: pair old to new in order; hand ids across; surplus is Added/Removed.
        int oldWindow = old.Count - prefix - suffix;
        int newWindow = segments.Count - prefix - suffix;

        var reused = new List<BlockId>(prefix + suffix);
        for (var i = 0; i < prefix; i++)
            reused.Add(old[i].Id);
        for (var i = old.Count - suffix; i < old.Count; i++)
            reused.Add(old[i].Id);

        var changed = new List<BlockId>(Math.Min(oldWindow, newWindow));
        var added = new List<BlockId>(Math.Max(0, newWindow - oldWindow));
        var removed = new List<BlockId>(Math.Max(0, oldWindow - newWindow));

        var window = new List<Block>(newWindow);
        for (var k = 0; k < newWindow; k++)
        {
            var (_, segCount) = segments[prefix + k];
            BlockId id;
            if (k < oldWindow)
            {
                id = old[prefix + k].Id;
                changed.Add(id);
            }
            else
            {
                id = new BlockId(_nextId++);
                added.Add(id);
            }

            window.Add(new Block(id, BlockKind.Paragraph, segCount));
        }

        for (var k = newWindow; k < oldWindow; k++)
            removed.Add(old[prefix + k].Id);

        Blocks.ReplaceRange(prefix, oldWindow, window);
        AssertTiling();

        Changed?.Invoke(new BlockListChange(reused, changed, added, removed, delta, result.Epoch));
    }

    // ───────────────────────────── segmentation ─────────────────────────────

    /// <summary>Segments the whole buffer into block line spans per the class policy.</summary>
    private List<(int Start, int Count)> Segment()
    {
        var segments = new List<(int, int)>();
        int lineCount = _buffer.LineCount;
        var i = 0;

        // A document-leading blank run has no paragraph above it — its own block.
        if (IsBlank(0))
        {
            while (i < lineCount && IsBlank(i))
                i++;
            segments.Add((0, i));
        }

        while (i < lineCount)
        {
            int start = i;
            while (i < lineCount && !IsBlank(i))
                i++;
            while (i < lineCount && IsBlank(i))
                i++;
            segments.Add((start, i - start));
        }

        return segments;
    }

    private bool IsBlank(int line) => string.IsNullOrWhiteSpace(_buffer.GetLine(line).Text);

    private static int CountLineBreaks(string text)
    {
        var breaks = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                breaks++;
        }

        return breaks;
    }

    [Conditional("DEBUG")]
    private void AssertTiling()
        => Debug.Assert(Blocks.TotalLineCount == _buffer.LineCount,
            $"BlockList tiling broke: blocks cover {Blocks.TotalLineCount} lines, buffer has {_buffer.LineCount}.");
}
