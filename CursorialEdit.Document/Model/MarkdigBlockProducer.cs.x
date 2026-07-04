using System.Diagnostics;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Parsing;

using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;

using MdBlock = Markdig.Syntax.Block;

namespace CursorialEdit.Document.Model;

/// <summary>
/// M2's real, Markdig-backed block producer (implementation-plan §7 WP2): parses the document
/// through the pinned <see cref="MarkdownPipelineFactory.Shared"/> pipeline, maps the top-level
/// Markdig blocks to <see cref="Block"/>s that tile the buffer, and — the crux — re-adopts block
/// identities across edits by Decision 4's rule so an unchanged block keeps its <see cref="BlockId"/>
/// (and thus its presenter / raster / reveal state downstream). It plugs into the exact
/// <see cref="BlockList"/>/<see cref="BlockListChange"/> seam the throwaway
/// <see cref="PlainTextBlockProducer"/> established, so WP7 swaps producers without touching the view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Full reparse per edit (WP2 scope).</b> Every splice triggers a whole-document
/// <see cref="Markdown.Parse(string, MarkdownPipeline)"/> — correctness first. WP3 adds the reparse
/// window; because segmentation is line-cheap and inlines are lazy (Decision 5), even the full path
/// is sub-frame for typical documents. Full reparse makes every nasty structural case (setext
/// disambiguation, lazy continuation, list tightness, fence/front-matter parity) trivially correct:
/// Markdig sees the whole document, so the <i>new</i> tiling is always right. The only remaining job
/// is aligning it to the old tiling to preserve identity.
/// </para>
/// <para>
/// <b>Tiling.</b> Markdig's top-level blocks do not by themselves cover blank separators, and it
/// relocates footnote and link-reference definitions into synthetic groups at the document tail with
/// aggregate (overlapping) spans. The producer therefore flattens
/// <see cref="FootnoteGroup"/>/<see cref="LinkReferenceDefinitionGroup"/> back to their real source
/// lines, orders every primary block by the line its precise <see cref="MarkdownObject.Span"/> starts
/// on (never <see cref="MarkdownObject.Line"/> — that points at a setext underline, not the heading
/// text), and tiles by "own every line up to the next primary's start line": trailing blanks attach
/// to the block above, leading blanks to the first block, so the blocks partition the buffer exactly.
/// An empty or all-blank document is one synthetic <see cref="BlockKind.Paragraph"/> block.
/// </para>
/// <para>
/// <b>Re-adoption (Decision 4).</b> After reparse, the new tiling is aligned to the old one:
/// <list type="number">
/// <item><b>Common prefix / suffix trimming.</b> Blocks entirely before the edit's dirty line range
/// (identical kind, line count, and start line) re-adopt unshifted; blocks entirely after it
/// (identical kind and line count, start line shifted by the line delta) re-adopt shifted. This alone
/// keeps every sibling of an edit stable and isolates the edit to a small window. The equality guard
/// is what makes structural poisoning safe: an unclosed fence or a setext underline that reinterprets
/// the tail changes those blocks' kind/line-count, so they fail the guard and drop into the window
/// rather than being falsely re-adopted.</item>
/// <item><b>Window matching by (kind, first unmodified line).</b> Within the window, each new segment
/// is matched to an old block by its <i>first unmodified line</i> — the first line whose
/// <see cref="Buffer.Line.Version"/> is below the just-applied edit's version, i.e. a line the edit
/// did not touch. That line's identity is stable across the edit, so mapping it back to the old block
/// that owned it re-adopts precisely, disambiguating merges/splits. A content hash is a
/// <i>secondary</i> check for a moved-but-unchanged block (every line rewritten, so no unmodified
/// line anchors it); it is never the primary key, because the block being typed in has its hash
/// change every keystroke. Anything still unmatched pairs in order by kind, then becomes Added/Removed.</item>
/// </list>
/// The block being edited keeps its id every keystroke: its siblings are the common prefix/suffix, so
/// it is the sole same-kind block in the window and re-adopts by the in-order pass even when every one
/// of its lines was just retyped (no unmodified line to anchor).
/// </para>
/// <para>
/// <b>Kind flips.</b> When an edit flips a block's kind (a paragraph gaining a <c>===</c> underline
/// becomes a setext heading), kind is half the re-adoption key, so no match is found and the new
/// block receives a fresh id (the old one is Removed). This is the deliberate choice: a kind change
/// means a different presenter downstream, so a fresh identity is correct — the surrounding blocks
/// still keep theirs via prefix/suffix trimming.
/// </para>
/// <para>
/// <b>Known WP3 follow-up.</b> A block whose <i>source</i> is unchanged but whose rendering depends on
/// a definition edited elsewhere (a paragraph referencing a link/footnote label whose definition
/// changed) is re-adopted as unchanged here — correct by the "source lines unchanged" contract, but
/// its inlines would want re-realizing. WP3's <c>LinkRefTable</c>/<c>FootnoteTable</c> label→block
/// invalidation closes that gap; WP2 does not model it.
/// </para>
/// <para>All members are UI-thread-only, like the controller and buffer they observe.</para>
/// </remarks>
public sealed class MarkdigBlockProducer : IDisposable
{
    private readonly EditController _controller;
    private readonly IDocumentBuffer _buffer;
    private readonly MarkdownPipeline _pipeline;
    private long _nextId = 1;
    private bool _disposed;

    /// <summary>
    /// Creates the producer over <paramref name="controller"/>'s buffer, segments the current
    /// document through the pinned pipeline, and subscribes to <see cref="EditController.Changed"/>
    /// for live re-adoption.
    /// </summary>
    /// <param name="controller">The edit controller whose buffer is parsed and observed.</param>
    /// <param name="pipeline">The pipeline to parse with; defaults to <see cref="MarkdownPipelineFactory.Shared"/> (the one pinned configuration).</param>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is <see langword="null"/>.</exception>
    public MarkdigBlockProducer(EditController controller, MarkdownPipeline? pipeline = null)
    {
        ArgumentNullException.ThrowIfNull(controller);

        _controller = controller;
        _buffer = controller.Buffer;
        _pipeline = pipeline ?? MarkdownPipelineFactory.Shared;
        Blocks = new BlockList();

        var segments = Segment();
        var initial = new List<Block>(segments.Count);
        foreach (var seg in segments)
            initial.Add(CreateBlock(new BlockId(_nextId++), seg));

        Blocks.ReplaceRange(0, 0, initial);
        AssertTiling();

        controller.Changed += OnSpliced;
    }

    /// <summary>The live block list this producer owns. Read-only to every other consumer.</summary>
    public BlockList Blocks { get; }

    /// <summary>
    /// Raised once per applied splice, after <see cref="Blocks"/> is consistent — the reconciliation
    /// feed WP7's view applies presenter reuse/invalidation from.
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

    // ───────────────────────────── re-adoption ─────────────────────────────

    private void OnSpliced(SpliceResult result)
    {
        var old = Blocks;
        int oldCount = old.Count;
        int oldTotalLines = old.TotalLineCount;

        var segs = Segment();
        int newCount = segs.Count;
        int currentVersion = _buffer.CurrentVersion;

        // Dirty line range: nothing before result.StartOffset changed, so its line indexes the same
        // before and after; the removed text's '\n' count spans the old range.
        int dirtyStartLine = _buffer.GetPosition(result.StartOffset).Line;
        int newDirtyEndLine = result.End.Line;
        int oldDirtyEndLine = dirtyStartLine + CountLineBreaks(result.RemovedText);
        int delta = newDirtyEndLine - oldDirtyEndLine;

        // Common prefix: blocks whose whole span lies before the dirty range and match structurally.
        int prefix = 0;
        while (prefix < oldCount && prefix < newCount)
        {
            var ob = old[prefix];
            var seg = segs[prefix];
            int oStart = old.GetStartLine(prefix);
            if (oStart + ob.LineCount > dirtyStartLine
                || ob.Kind != seg.Kind || ob.LineCount != seg.LineCount || oStart != seg.StartLine)
            {
                break;
            }

            prefix++;
        }

        // Common suffix: blocks whose whole span lies after the dirty range, shifted by delta.
        int suffix = 0;
        int maxSuffix = Math.Min(oldCount, newCount) - prefix;
        while (suffix < maxSuffix)
        {
            int oi = oldCount - 1 - suffix;
            int ni = newCount - 1 - suffix;
            var ob = old[oi];
            var seg = segs[ni];
            int oStart = old.GetStartLine(oi);
            if (oStart <= oldDirtyEndLine
                || ob.Kind != seg.Kind || ob.LineCount != seg.LineCount || seg.StartLine != oStart + delta)
            {
                break;
            }

            suffix++;
        }

        int oldWindowStart = prefix;
        int oldWindowEnd = oldCount - suffix;
        int newWindowStart = prefix;
        int newWindowEnd = newCount - suffix;
        int oldWindow = oldWindowEnd - oldWindowStart;
        int newWindow = newWindowEnd - newWindowStart;

        // Match the window's new segments to old blocks. matchedOldFor[j] is the old window index
        // (absolute) re-adopted by new window segment j, or -1; matchedOld[local] marks an old window
        // block as claimed (only ever set on a genuine match — an unmatched old block is Removed).
        var matchedOldFor = new int[newWindow];
        Array.Fill(matchedOldFor, -1);
        var matchedOld = new bool[oldWindow];

        // Pass 1 — exact content match (same kind, line count, content hash): a block whose whole
        // text survived the edit unchanged, only shifted. Split in two priority tiers so identity is
        // never handed to the wrong byte-identical twin (Decision 4 — the hash is an equality WITNESS,
        // never a positional key):
        //   1a. segments that still contain an UNMODIFIED line (the genuine survivor of an edit)
        //       claim first — so pasting an identical copy above a block leaves the untouched original
        //       (which has unmodified lines) holding the id, not the all-modified pasted copy.
        //   1b. the remaining hash-identical segments (every line rewritten/pasted to identical text)
        //       claim what is left.
        // Running hash before the anchor pass also stops a segment of purely-new content from
        // stealing an id via a merged line that inherited an old line's version stamp during the
        // splice (the "typed a new paragraph whose last line merged with an old terminator" artifact).
        for (var tier = 0; tier < 2; tier++)
        {
            for (var j = 0; j < newWindow; j++)
            {
                if (matchedOldFor[j] >= 0)
                    continue;

                var seg = segs[newWindowStart + j];
                bool hasUnmodified = FirstUnmodifiedLine(seg, currentVersion) >= 0;
                if ((tier == 0) != hasUnmodified)
                    continue; // tier 0 = survivors first; tier 1 = the rest

                ulong hash = HashLines(seg.StartLine, seg.LineCount);
                for (var local = 0; local < oldWindow; local++)
                {
                    int oi = oldWindowStart + local;
                    if (!matchedOld[local] && old[oi].Kind == seg.Kind
                        && old[oi].LineCount == seg.LineCount && old[oi].ContentHash == hash)
                    {
                        matchedOldFor[j] = oi;
                        matchedOld[local] = true;
                        break;
                    }
                }
            }
        }

        // Pass 2 — (kind, first unmodified line): the first line the edit did not touch identifies the
        // old block that owned it, disambiguating merges/splits and the edited multi-line block that
        // pass 1 could not hash-match (its text changed). Only claims old blocks pass 1 left.
        for (var j = 0; j < newWindow; j++)
        {
            if (matchedOldFor[j] >= 0)
                continue;

            var seg = segs[newWindowStart + j];
            int anchorNewLine = FirstUnmodifiedLine(seg, currentVersion);
            if (anchorNewLine < 0)
                continue;

            int anchorOldLine = anchorNewLine < dirtyStartLine ? anchorNewLine : anchorNewLine - delta;
            if (anchorOldLine < 0 || anchorOldLine >= oldTotalLines)
                continue;

            int oi = old.IndexOfLine(anchorOldLine);
            int local = oi - oldWindowStart;
            if (local >= 0 && local < oldWindow && !matchedOld[local] && old[oi].Kind == seg.Kind)
            {
                matchedOldFor[j] = oi;
                matchedOld[local] = true;
            }
        }

        // Pass 3 — in-order same-kind pairing for the residue (a fully-retyped block whose every line
        // was touched, split/merge products). Two pointers over the still-unmatched window blocks; an
        // old block advanced past here stays unmatched and is reported Removed.
        {
            int oiLocal = 0, j = 0;
            while (j < newWindow)
            {
                if (matchedOldFor[j] >= 0) { j++; continue; }
                while (oiLocal < oldWindow && matchedOld[oiLocal]) oiLocal++;
                if (oiLocal >= oldWindow) break;

                var seg = segs[newWindowStart + j];
                int oi = oldWindowStart + oiLocal;
                if (old[oi].Kind == seg.Kind)
                {
                    matchedOldFor[j] = oi;
                    matchedOld[oiLocal] = true;
                    oiLocal++;
                    j++;
                }
                else if (UnmatchedCount(matchedOld, oiLocal) > UnmatchedCount(matchedOldFor, j))
                {
                    oiLocal++; // more old than new remain ⇒ this old is Removed (left unmatched)
                }
                else
                {
                    j++; // this new is Added
                }
            }
        }

        // Build the new window blocks, re-adopting ids, and classify every block.
        var reused = new List<BlockId>();
        var changed = new List<BlockId>();
        var added = new List<BlockId>();
        var removed = new List<BlockId>();

        for (var i = 0; i < prefix; i++)
            reused.Add(old[i].Id);

        var window = new List<Block>(newWindow);
        for (var j = 0; j < newWindow; j++)
        {
            var seg = segs[newWindowStart + j];
            int oi = matchedOldFor[j];
            if (oi >= 0)
            {
                var id = old[oi].Id;
                window.Add(CreateBlock(id, seg));

                // Reused only when this segment's source is byte-identical to the old block whose id
                // it re-adopts AND no line was touched — i.e. a block pulled into the window but left
                // whole. A re-adoption where the anchor line drifted from another block (same id,
                // different surrounding text) is Changed, so the view re-derives it.
                bool unchanged = old[oi].LineCount == seg.LineCount
                    && old[oi].ContentHash == HashLines(seg.StartLine, seg.LineCount)
                    && !HasModifiedLine(seg, currentVersion);
                (unchanged ? reused : changed).Add(id);
            }
            else
            {
                var id = new BlockId(_nextId++);
                window.Add(CreateBlock(id, seg));
                added.Add(id);
            }
        }

        for (var local = 0; local < oldWindow; local++)
        {
            if (!matchedOld[local])
                removed.Add(old[oldWindowStart + local].Id);
        }

        for (var oi = oldCount - suffix; oi < oldCount; oi++)
            reused.Add(old[oi].Id);

        Blocks.ReplaceRange(oldWindowStart, oldWindow, window);
        AssertTiling();

        int lineShift = _buffer.LineCount - oldTotalLines;
        Debug.Assert(lineShift == delta, $"Line shift {lineShift} disagrees with dirty-range delta {delta}.");

        Changed?.Invoke(new BlockListChange(reused, changed, added, removed, lineShift, result.Epoch));
    }

    private int FirstUnmodifiedLine(in Seg seg, int currentVersion)
    {
        int end = seg.StartLine + seg.LineCount;
        for (int line = seg.StartLine; line < end; line++)
        {
            if (_buffer.GetLine(line).Version < currentVersion)
                return line;
        }

        return -1;
    }

    private bool HasModifiedLine(in Seg seg, int currentVersion)
    {
        int end = seg.StartLine + seg.LineCount;
        for (int line = seg.StartLine; line < end; line++)
        {
            if (_buffer.GetLine(line).Version >= currentVersion)
                return true;
        }

        return false;
    }

    /// <summary>Count of still-unmatched old window blocks from local index <paramref name="from"/>.</summary>
    private static int UnmatchedCount(bool[] matchedOld, int from)
    {
        var n = 0;
        for (int i = from; i < matchedOld.Length; i++)
        {
            if (!matchedOld[i])
                n++;
        }

        return n;
    }

    /// <summary>Count of still-unmatched new window segments from window index <paramref name="from"/>.</summary>
    private static int UnmatchedCount(int[] matchedOldFor, int from)
    {
        var n = 0;
        for (int i = from; i < matchedOldFor.Length; i++)
        {
            if (matchedOldFor[i] < 0)
                n++;
        }

        return n;
    }

    // ───────────────────────────── segmentation ─────────────────────────────

    /// <summary>One tiled block span with its mapped kind and Markdig payload.</summary>
    private readonly record struct Seg(
        BlockKind Kind, int StartLine, int LineCount, MdBlock? MarkdigBlock, int? HeadingLevel, string? FenceInfo);

    /// <summary>A candidate top-level block before gap lines are tiled in.</summary>
    private readonly record struct Primary(
        MdBlock Block, BlockKind Kind, int StartLine, int SpanLength, int? HeadingLevel, string? FenceInfo);

    /// <summary>Parses the whole buffer and tiles it into block segments (the full-reparse path).</summary>
    private List<Seg> Segment()
    {
        var doc = Markdown.Parse(_buffer.GetText(), _pipeline);
        int lineCount = _buffer.LineCount;

        var primaries = new List<Primary>();
        foreach (var child in doc)
        {
            switch (child)
            {
                case FootnoteGroup group:
                    foreach (var footnote in group)
                        AddPrimary(primaries, footnote);
                    break;
                case LinkReferenceDefinitionGroup group:
                    foreach (var definition in group)
                        AddPrimary(primaries, definition);
                    break;
                default:
                    AddPrimary(primaries, child);
                    break;
            }
        }

        // Empty or all-blank document: Markdig produces no blocks; emit one synthetic paragraph.
        if (primaries.Count == 0)
            return [new Seg(BlockKind.Paragraph, 0, lineCount, null, null, null)];

        primaries.Sort(static (a, b) => a.StartLine.CompareTo(b.StartLine));
        DedupSameStartLine(primaries);

        var segs = new List<Seg>(primaries.Count);
        for (var i = 0; i < primaries.Count; i++)
        {
            int start = i == 0 ? 0 : primaries[i].StartLine;
            int end = i == primaries.Count - 1 ? lineCount : primaries[i + 1].StartLine;
            var p = primaries[i];
            segs.Add(new Seg(p.Kind, start, end - start, p.Block, p.HeadingLevel, p.FenceInfo));
        }

        return segs;
    }

    private void AddPrimary(List<Primary> primaries, MdBlock block)
    {
        int startOffset = Math.Max(0, block.Span.Start);
        int startLine = _buffer.GetPosition(startOffset).Line;
        var kind = MarkdigBlockKindMap.Map(block);
        int? level = block is HeadingBlock heading ? heading.Level : null;
        string? info = kind == BlockKind.FencedCode && block is FencedCodeBlock fenced
            ? fenced.Info ?? string.Empty
            : null;
        primaries.Add(new Primary(block, kind, startLine, block.Span.Length, level, info));
    }

    /// <summary>
    /// Collapses primaries that share a start line — the footnote definition line is represented
    /// twice (a label registration in the link-ref group and the definition body in the footnote
    /// group). Keeps the larger-span representative (the body, which carries the inline AST).
    /// </summary>
    private static void DedupSameStartLine(List<Primary> primaries)
    {
        var write = 0;
        for (var read = 0; read < primaries.Count; read++)
        {
            if (write > 0 && primaries[write - 1].StartLine == primaries[read].StartLine)
            {
                if (primaries[read].SpanLength > primaries[write - 1].SpanLength)
                    primaries[write - 1] = primaries[read];
                continue;
            }

            primaries[write++] = primaries[read];
        }

        primaries.RemoveRange(write, primaries.Count - write);
    }

    private Block CreateBlock(BlockId id, in Seg seg)
    {
        if (seg.MarkdigBlock is null)
            return new Block(id, seg.Kind, seg.LineCount); // synthetic empty/blank-document block

        int stamp = MaxVersion(seg.StartLine, seg.LineCount);
        int startOffset = _buffer.GetOffset(new TextPosition(seg.StartLine, 0));
        ulong hash = HashLines(seg.StartLine, seg.LineCount);
        return new Block(id, seg.Kind, seg.LineCount, seg.MarkdigBlock, startOffset, stamp, hash, seg.HeadingLevel, seg.FenceInfo);
    }

    private int MaxVersion(int startLine, int lineCount)
    {
        var max = 0;
        for (int line = startLine; line < startLine + lineCount; line++)
            max = Math.Max(max, _buffer.GetLine(line).Version);

        return max;
    }

    /// <summary>Order-sensitive FNV-1a over the block's serialized source (endings folded in so CRLF ≠ LF).</summary>
    private ulong HashLines(int startLine, int lineCount)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;
        for (int line = startLine; line < startLine + lineCount; line++)
        {
            var value = _buffer.GetLine(line);
            foreach (char ch in value.Text)
            {
                hash = (hash ^ ch) * prime;
            }

            hash = (hash ^ (byte) value.Ending) * prime;
        }

        return hash;
    }

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
