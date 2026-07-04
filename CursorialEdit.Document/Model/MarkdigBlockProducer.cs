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
/// M2's real, Markdig-backed block producer (implementation-plan §7 WP2/WP3): parses the document
/// through the pinned <see cref="MarkdownPipelineFactory.Shared"/> pipeline, maps the top-level
/// Markdig blocks to <see cref="Block"/>s that tile the buffer, and — the crux — re-adopts block
/// identities across edits by Decision 4's rule so an unchanged block keeps its <see cref="BlockId"/>
/// (and thus its presenter / raster / reveal state downstream). It plugs into the exact
/// <see cref="BlockList"/>/<see cref="BlockListChange"/> seam the throwaway
/// <see cref="PlainTextBlockProducer"/> established, so WP7 swaps producers without touching the view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Incremental reparse (WP3, Decision 3).</b> The initial parse and the pre-authorized degraded
/// mode reparse the whole document; every edit instead reparses only a <b>window</b> —
/// <see cref="ReparseWindowPlanner"/> computes the source range the edit could have restructured (the
/// edited block plus, off the fast path, one top-level neighbour on each side; extended to EOF on a
/// fence-parity flip via <see cref="FenceIntervalSet"/>), and <see cref="FastPathGate"/> narrows a
/// provably-non-structural keystroke to the single edited block. The window text is parsed in isolation
/// (with a leading blank prepended for any non-zero start to suppress spurious front matter — recognised
/// only at absolute document start), and the reused blocks outside it are spliced back unchanged. This
/// is the §13 "no full reparse per keystroke" guarantee; the differential fuzzer (WP4) proves the
/// windowed tiling is byte-identical to a full parse.
/// </para>
/// <para>
/// <b>Re-adoption is unchanged (Decision 4).</b> Narrowing the <i>parse scope</i> does not touch the
/// diff: the windowed parse is spliced with the reused prefix/suffix into a full new tiling, and the
/// same common-prefix/suffix trim + three-pass (hash-tier, first-unmodified-line anchor, in-order
/// residue) matching aligns it to the old tiling. The trim naturally re-discovers the true minimal
/// window inside the planned one, so context blocks the planner pulled in for correctness are re-adopted
/// as Reused (their old instances, and inline caches, preserved). Kind flips retire the old id and mint
/// a fresh one; the surrounding blocks keep theirs via the trim.
/// </para>
/// <para>
/// <b>Tiling.</b> Markdig's top-level blocks do not by themselves cover blank separators, and it
/// relocates footnote and link-reference definitions into synthetic groups at the (parse) tail with
/// aggregate (overlapping) spans. The producer therefore flattens
/// <see cref="FootnoteGroup"/>/<see cref="LinkReferenceDefinitionGroup"/> back to their real source
/// lines, orders every primary block by the line its precise <see cref="MarkdownObject.Span"/> starts
/// on (never <see cref="MarkdownObject.Line"/> — that points at a setext underline, not the heading
/// text), and tiles by "own every line up to the next primary's start line": trailing blanks attach
/// to the block above, leading blanks to the first block, so the blocks partition the buffer exactly.
/// An empty or all-blank window is one synthetic <see cref="BlockKind.Paragraph"/> block.
/// </para>
/// <para>
/// <b>Document-global definitions (WP3, §2.2 step 4).</b> Footnote and link-reference definitions
/// resolve against the whole document, so a windowed sub-parse tiles and resolves them differently than
/// a full parse. The producer therefore takes the synchronous full-parse path for any document that
/// contains (or any edit that introduces) such a definition — <see cref="SegmentForEdit"/>'s escalation
/// — which keeps references correct <i>immediately</i>, this frame. A definition-set change additionally
/// reports the referencing (Reused) blocks as <see cref="BlockListChange.Invalidated"/> so the view
/// re-derives their inlines against the fresh ASTs the same-frame full parse already installed. This
/// supersedes the debounced off-thread scheduler the earlier design carried: definition-free documents
/// stay windowed (§13); definition-bearing ones full-parse per keystroke (the plan's sanctioned degraded
/// class, within the §13 hard ceiling for realistic documents) with no async path or off-thread state.
/// </para>
/// <para>All members are UI-thread-only, like the controller and buffer they observe.</para>
/// </remarks>
public sealed class MarkdigBlockProducer : IDisposable
{
    private readonly EditController _controller;
    private readonly IDocumentBuffer _buffer;
    private readonly MarkdownPipeline _pipeline;
    private readonly DefinitionIndex _definitions = new();
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
    public MarkdigBlockProducer(
        EditController controller,
        MarkdownPipeline? pipeline = null)
    {
        ArgumentNullException.ThrowIfNull(controller);

        _controller = controller;
        _buffer = controller.Buffer;
        _pipeline = pipeline ?? MarkdownPipelineFactory.Shared;
        Blocks = new BlockList();

        var segments = SegmentFull();
        var initial = new List<Block>(segments.Count);
        foreach (var seg in segments)
            initial.Add(CreateBlock(new BlockId(_nextId++), seg));

        Blocks.ReplaceRange(0, 0, initial);
        AssertTiling();

        _definitions.Update(Blocks, _buffer); // seed the definition signatures from the initial parse

        controller.Changed += OnSpliced;
    }

    /// <summary>The live block list this producer owns. Read-only to every other consumer.</summary>
    public BlockList Blocks { get; }

    /// <summary>
    /// Raised once per applied splice, after <see cref="Blocks"/> is consistent — the reconciliation
    /// feed WP7's view applies presenter reuse/invalidation from.
    /// </summary>
    public event Action<BlockListChange>? Changed;

    /// <summary>The document-global definition index (link-reference + footnote definitions).</summary>
    internal DefinitionIndex Definitions => _definitions;

    /// <summary>
    /// The pre-authorized degraded fallback (R2): when <see langword="true"/>, every edit reparses the
    /// whole document instead of a window. Off by default; a stubborn window-rule class can be parked
    /// here while the rule is fixed, and the differential fuzzer flips it to prove windowing and the
    /// full path agree.
    /// </summary>
    internal bool ForceFullReparse { get; set; }

    /// <summary>Number of lines fed to Markdig on the most recent edit (test seam for the §13 no-full-reparse instrumentation).</summary>
    internal int LastParsedLineCount { get; private set; }

    /// <summary>Whether the most recent edit's parse spanned the whole document (test seam).</summary>
    internal bool LastParseWasFullDocument { get; private set; }

    /// <summary>Cumulative count of full-document Markdig parses — one at construction, plus every degraded/structural full reparse (test seam).</summary>
    internal int FullDocumentParseCount { get; private set; }

    /// <summary>Cumulative count of windowed (sub-document) Markdig parses (test seam).</summary>
    internal int WindowParseCount { get; private set; }

    /// <summary>Unsubscribes from the controller and stops the full-reparse scheduler; the list freezes at its current state.</summary>
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

        int currentVersion = _buffer.CurrentVersion;

        // Dirty line range: nothing before result.StartOffset changed, so its line indexes the same
        // before and after; the removed text's '\n' count spans the old range.
        int dirtyStartLine = _buffer.GetPosition(result.StartOffset).Line;
        int newDirtyEndLine = result.End.Line;
        int oldDirtyEndLine = dirtyStartLine + CountLineBreaks(result.RemovedText);
        int delta = newDirtyEndLine - oldDirtyEndLine;

        // WP3: parse only the planned window (or the whole document on the full/degraded path); the
        // result is a full new tiling (reused prefix/suffix + parsed window) fed to the unchanged diff.
        var segs = SegmentForEdit(result, dirtyStartLine, oldDirtyEndLine, delta);
        int newCount = segs.Count;

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

        // WP3 document-global reconcile: a definition-set change invalidates the referencing blocks
        // (their source is unchanged, but their inlines are stale) and escalates to a debounced full
        // reparse. Reported as Invalidated (a subset of Reused) — the partition stays intact.
        var definitionDelta = _definitions.Update(Blocks, _buffer);

        var change = new BlockListChange(reused, changed, added, removed, lineShift, result.Epoch);
        if (definitionDelta.SetChanged && definitionDelta.InvalidatedBlocks.Count > 0)
        {
            // A definition-set change re-renders every REUSED block that references it (its source is
            // unchanged, so the diff classifies it Reused, but its rendered inlines resolved against
            // the old definition). Filter to Reused so the documented invariant holds — Invalidated ⊆
            // Reused, disjoint from Changed/Added (which the view re-renders anyway). A referencing
            // block that was itself Changed/Added this edit needs no separate signal.
            //
            // NOTE: definition-bearing documents take the synchronous full-parse path (SegmentForEdit's
            // escalation), so the referencing blocks' fresh Markdig ASTs are already in Blocks this
            // frame — Invalidated tells the view which Reused presenters to re-derive, no async reparse.
            var reusedSet = new HashSet<BlockId>(reused);
            var invalidated = definitionDelta.InvalidatedBlocks.Where(reusedSet.Contains).ToList();
            if (invalidated.Count > 0)
                change = change with { Invalidated = invalidated };
        }

        Changed?.Invoke(change);
    }

    // ───────────────────────────── window planning + parse ─────────────────────────────

    /// <summary>
    /// Parses the edit's reparse window (or the whole document on the full/degraded path) and returns
    /// the resulting <b>full</b> new tiling: reused prefix segments, the parsed window, and reused
    /// suffix segments — the input the unchanged diff aligns to the old tiling.
    /// </summary>
    /// <summary>
    /// Whether the current (pre-edit) tiling contains a document-global definition — a footnote or
    /// link-reference definition. Keyed off the block <b>kind</b> (which Markdig assigned), not the
    /// definition-signature parser, so a malformed-but-recognized definition still forces the full path.
    /// </summary>
    private bool DocumentHasGlobalDefinitions()
    {
        var b = Blocks;
        for (var i = 0; i < b.Count; i++)
        {
            if (b[i].Kind is BlockKind.Footnote or BlockKind.LinkReferenceDefinition)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the changed text could add or remove a document-global definition — a cheap syntactic
    /// superset (a footnote marker <c>[^</c> or a definition/reference colon <c>]:</c> in either the
    /// inserted or the removed text). Over-inclusive by design; the false positives merely take the
    /// (correct) full path.
    /// </summary>
    private bool EditTouchesDefinitionSyntax(SpliceResult result)
    {
        int insertedLength = _buffer.GetOffset(result.End) - result.StartOffset;
        string inserted = insertedLength > 0
            ? _buffer.GetTextAtOffset(result.StartOffset, insertedLength)
            : string.Empty;

        return HasDefinitionMarker(inserted) || HasDefinitionMarker(result.RemovedText);

        static bool HasDefinitionMarker(string text)
            => text.Contains("[^", StringComparison.Ordinal) || text.Contains("]:", StringComparison.Ordinal);
    }

    private List<Seg> SegmentForEdit(SpliceResult result, int dirtyStartLine, int oldDirtyEndLine, int delta)
    {
        var old = Blocks;
        int firstDirty = old.IndexOfLine(Math.Clamp(dirtyStartLine, 0, old.TotalLineCount - 1));

        var fences = FenceIntervalSet.Build(_buffer);

        // Degraded-mode fallback (sanctioned by the plan's Risks): a lone CR is content to the buffer
        // but a line break to Markdig, so the buffer's line-based window analysis cannot be trusted for
        // such a document. Reparse it whole — always correct, and only for the rare stray-CR document.
        if (ForceFullReparse || fences.HasBareCarriageReturn)
            return SegmentFull();

        // Document-global definitions (footnote + link-reference) resolve against the WHOLE document —
        // a footnote reference binds to a definition ANYWHERE, and Markdig relocates the definitions
        // into synthetic tail groups. So a windowed parse of a sub-range resolves references and tiles
        // definitions differently than a full parse, and no line-based window can reconcile that.
        // Escalate to a full parse whenever the document already HAS such a definition (old blocks) or
        // this edit could INTRODUCE/REMOVE one (definition-shaped syntax in the changed text). Correct
        // always; the common definition-free document stays windowed (§13). (Plan Risks: definitions
        // are the sanctioned full-reparse class.)
        if (DocumentHasGlobalDefinitions() || EditTouchesDefinitionSyntax(result))
            return SegmentFull();

        bool fastPath = IsFastPath(result);
        var plan = ReparseWindowPlanner.Plan(old, dirtyStartLine, oldDirtyEndLine, delta, fastPath, fences, _buffer);

        if (plan.IsFullDocument)
            return SegmentFull();

        var (segs, windowSegCount) = ParseWindow(plan, delta);

        // Fast-path safety net: a single-block parse is valid only if the block's kind is unchanged. A
        // word-interior edit cannot itself restructure blocks, so a flipped kind means the block's kind
        // depended on a line outside its own tile — the bare-CR setext pathology, where the underline
        // lands in the next block's tile. Fall back to the context window, which the blank-separation
        // rule widens to include the dependency.
        if (fastPath && (windowSegCount != 1 || segs[plan.OldStartBlock].Kind != old[firstDirty].Kind))
        {
            plan = ReparseWindowPlanner.Plan(old, dirtyStartLine, oldDirtyEndLine, delta, fastPath: false, fences, _buffer);
            if (plan.IsFullDocument)
                return SegmentFull();

            (segs, _) = ParseWindow(plan, delta);
        }

        return segs;
    }

    /// <summary>Parses the plan's window in isolation and assembles the full new tiling (reused prefix/suffix + parsed window). Returns the assembled segments and the count contributed by the parsed window.</summary>
    private (List<Seg> Segs, int WindowSegCount) ParseWindow(ReparseWindowPlanner.WindowPlan plan, int delta)
    {
        var old = Blocks;
        int oldCount = old.Count;
        int lineCount = _buffer.LineCount;
        int newWs = plan.NewStartLine;
        int newWe = plan.NewEndLine;

        int windowStartOffset = _buffer.GetOffset(new TextPosition(newWs, 0));
        int windowEndOffset = newWe >= lineCount ? _buffer.Length : _buffer.GetOffset(new TextPosition(newWe, 0));
        string windowText = _buffer.GetTextAtOffset(windowStartOffset, windowEndOffset - windowStartOffset);

        // A non-zero window start prepends a blank line: YAML front matter is recognised only at
        // absolute document start, so the blank both suppresses a spurious front-matter read and is
        // otherwise inert to block structure. parseBaseOffset maps a parse-text index to its document
        // offset (index 0 is the prepended blank, one before windowStartOffset).
        int prependLen = newWs == 0 ? 0 : 1;
        string parseText = prependLen == 0 ? windowText : "\n" + windowText;
        int parseBaseOffset = windowStartOffset - prependLen;

        var doc = Markdown.Parse(parseText, _pipeline);
        LastParsedLineCount = newWe - newWs;
        LastParseWasFullDocument = false;
        WindowParseCount++;

        var windowSegs = TileWindow(doc, parseBaseOffset, newWs, newWe);

        var segs = new List<Seg>(plan.OldStartBlock + windowSegs.Count + (oldCount - plan.OldEndBlock));
        for (var i = 0; i < plan.OldStartBlock; i++)
            segs.Add(SynthSeg(old, i, shift: 0)); // reused prefix — never CreateBlock'd (trimmed)
        segs.AddRange(windowSegs);
        for (var i = plan.OldEndBlock; i < oldCount; i++)
            segs.Add(SynthSeg(old, i, shift: delta)); // reused suffix, shifted — never CreateBlock'd

        return (segs, windowSegs.Count);
    }

    /// <summary>Whether the edit provably cannot change block structure (single-block fast path — <see cref="FastPathGate"/>).</summary>
    private bool IsFastPath(SpliceResult result)
    {
        if (result.RemovedText.Contains('\n'))
            return false;

        int startOffset = result.StartOffset;
        int endOffset = _buffer.GetOffset(result.End);
        int insertedLen = endOffset - startOffset;
        if (insertedLen < 0)
            return false;

        string inserted = insertedLen == 0 ? string.Empty : _buffer.GetTextAtOffset(startOffset, insertedLen);
        char? before = startOffset > 0 ? _buffer.GetTextAtOffset(startOffset - 1, 1)[0] : null;
        char? after = endOffset < _buffer.Length ? _buffer.GetTextAtOffset(endOffset, 1)[0] : null;

        return FastPathGate.IsEligible(before, after, result.RemovedText, inserted);
    }

    /// <summary>A reused prefix/suffix segment mirroring old block <paramref name="index"/>, shifted by <paramref name="shift"/>. Carries no Markdig backing — the diff trims it before any block is built from it.</summary>
    private static Seg SynthSeg(BlockList old, int index, int shift)
    {
        var block = old[index];
        return new Seg(block.Kind, old.GetStartLine(index) + shift, block.LineCount, MarkdigBlock: null, HeadingLevel: null, FenceInfo: null, SpanOrigin: 0);
    }

    // ───────────────────────────── segmentation ─────────────────────────────

    /// <summary>One tiled block span with its mapped kind and Markdig payload.</summary>
    private readonly record struct Seg(
        BlockKind Kind, int StartLine, int LineCount, MdBlock? MarkdigBlock, int? HeadingLevel, string? FenceInfo, int SpanOrigin);

    /// <summary>A candidate top-level block before gap lines are tiled in.</summary>
    private readonly record struct Primary(
        MdBlock Block, BlockKind Kind, int StartLine, int SpanLength, int? HeadingLevel, string? FenceInfo);

    /// <summary>Parses the whole buffer and tiles it into block segments (the full-reparse path: load, degraded mode, tail-reinterpreting structural keystroke).</summary>
    private List<Seg> SegmentFull()
    {
        int lineCount = _buffer.LineCount;
        var doc = Markdown.Parse(_buffer.GetText(), _pipeline);

        LastParsedLineCount = lineCount;
        LastParseWasFullDocument = true;
        FullDocumentParseCount++;

        return TileWindow(doc, parseBaseOffset: 0, firstLine: 0, lastLine: lineCount);
    }

    /// <summary>
    /// Tiles the primaries of a parsed <paramref name="doc"/> across the source lines
    /// <c>[<paramref name="firstLine"/>, <paramref name="lastLine"/>)</c>. <paramref name="parseBaseOffset"/>
    /// is the document offset the parse text's index 0 maps to (0 for a full parse; the window start
    /// minus any prepended blank for a windowed parse), so a Markdig span at parse-text index
    /// <c>s</c> is document offset <c>s + parseBaseOffset</c>.
    /// </summary>
    private List<Seg> TileWindow(MarkdownDocument doc, int parseBaseOffset, int firstLine, int lastLine)
    {
        var primaries = new List<Primary>();
        foreach (var child in doc)
        {
            switch (child)
            {
                case FootnoteGroup group:
                    foreach (var footnote in group)
                        AddPrimary(primaries, footnote, parseBaseOffset, firstLine, lastLine);
                    break;
                case LinkReferenceDefinitionGroup group:
                    foreach (var definition in group)
                        AddPrimary(primaries, definition, parseBaseOffset, firstLine, lastLine);
                    break;
                default:
                    AddPrimary(primaries, child, parseBaseOffset, firstLine, lastLine);
                    break;
            }
        }

        // Empty or all-blank window: Markdig produces no blocks; emit one synthetic paragraph.
        if (primaries.Count == 0)
            return [new Seg(BlockKind.Paragraph, firstLine, lastLine - firstLine, null, null, null, 0)];

        primaries.Sort(static (a, b) => a.StartLine.CompareTo(b.StartLine));
        DedupSameStartLine(primaries);

        var segs = new List<Seg>(primaries.Count);
        for (var i = 0; i < primaries.Count; i++)
        {
            int start = i == 0 ? firstLine : primaries[i].StartLine;
            int end = i == primaries.Count - 1 ? lastLine : primaries[i + 1].StartLine;
            var p = primaries[i];
            int spanOrigin = _buffer.GetOffset(new TextPosition(start, 0)) - parseBaseOffset;
            segs.Add(new Seg(p.Kind, start, end - start, p.Block, p.HeadingLevel, p.FenceInfo, spanOrigin));
        }

        return segs;
    }

    private void AddPrimary(List<Primary> primaries, MdBlock block, int parseBaseOffset, int firstLine, int lastLine)
    {
        int startOffset = Math.Clamp(Math.Max(0, block.Span.Start) + parseBaseOffset, 0, _buffer.Length);
        int startLine = Math.Clamp(_buffer.GetPosition(startOffset).Line, firstLine, Math.Max(firstLine, lastLine - 1));
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
            return new Block(id, seg.Kind, seg.LineCount); // synthetic empty/blank-window block

        int stamp = MaxVersion(seg.StartLine, seg.LineCount);
        ulong hash = HashLines(seg.StartLine, seg.LineCount);
        int startOffset = _buffer.GetOffset(new TextPosition(seg.StartLine, 0));
        int endOffset = seg.StartLine + seg.LineCount >= _buffer.LineCount
            ? _buffer.Length
            : _buffer.GetOffset(new TextPosition(seg.StartLine + seg.LineCount, 0));
        return new Block(id, seg.Kind, seg.LineCount, seg.MarkdigBlock, seg.SpanOrigin, endOffset - startOffset, stamp, hash, seg.HeadingLevel, seg.FenceInfo);
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
