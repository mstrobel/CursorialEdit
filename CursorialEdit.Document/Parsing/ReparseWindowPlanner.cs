using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Document.Parsing;

/// <summary>
/// Plans the source range an edit must re-run through Markdig (architecture Decision 3 step 1 / §2.2).
/// From the pre-splice <see cref="BlockList"/>, the edit's dirty line range, and the line-count delta,
/// it computes a contiguous window of old blocks whose new text — parsed <b>in isolation</b> — tiles
/// identically to a full-document parse of the same region, so the producer can reuse every block
/// outside the window unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an old-block window is isolation-safe.</b> A top-level block boundary is a context-free cut:
/// once parsing reaches one, the tiling of the lines after it does not depend on the lines before it
/// (the sole exception, YAML front matter, is recognised only at absolute document start and is
/// suppressed by the producer's leading-blank prepend for any non-zero window start). The window's
/// edges are boundaries taken from the <i>previous</i> parse, which are therefore genuine cuts; the
/// interior is exactly the lines the edit could have restructured.
/// </para>
/// <para>
/// <b>The seed and its expansion.</b>
/// <list type="bullet">
/// <item><b>Fast path.</b> When the edit provably cannot change structure (see
/// <see cref="FastPathGate"/>), the window is the single edited block — the §13 "no full reparse per
/// keystroke" case.</item>
/// <item><b>Window path.</b> The seed is the blocks overlapping the edited lines, expanded outward
/// across every <b>non-blank-separated</b> old-block boundary: a window edge is safe only at a
/// blank-line gap (or a document edge), because a blank line is CommonMark's one unambiguous top-level
/// block separator — setext underlines, lazy/list continuation, paragraph merges, and the bare-CR line
/// discrepancy between the buffer's line model and Markdig's all reach across <i>non-blank</i>
/// adjacencies but never across a blank line. Expansion stops at the first blank-separated boundary on
/// each side, so it is bounded by the surrounding blank lines and never lands inside a list or
/// blockquote (those are single top-level blocks, so their internal blanks are never block
/// boundaries).</item>
/// <item><b>Fence parity.</b> If the window's trailing boundary falls inside a fenced region of the
/// <i>new</i> document — the structural keystroke opened a fence that reinterprets the tail — the
/// window extends to end of document (<see cref="FenceIntervalSet"/>); a boundary found strictly inside
/// a region on the leading side (defensive; old boundaries are never fence interiors) walks left out of
/// it.</item>
/// </list>
/// </para>
/// </remarks>
public static class ReparseWindowPlanner
{
    /// <summary>A planned reparse window, in both old-block-index and new-source-line coordinates.</summary>
    /// <param name="OldStartBlock">First old block in the window (inclusive).</param>
    /// <param name="OldEndBlock">One past the last old block in the window (exclusive).</param>
    /// <param name="NewStartLine">First new-document line the window covers (inclusive) — the start line of <paramref name="OldStartBlock"/>, unchanged by the edit.</param>
    /// <param name="NewEndLine">One past the last new-document line the window covers (exclusive).</param>
    /// <param name="IsFullDocument">Whether the window spans the entire document (a full reparse — load, degraded mode, or a tail-reinterpreting structural keystroke).</param>
    public readonly record struct WindowPlan(
        int OldStartBlock, int OldEndBlock, int NewStartLine, int NewEndLine, bool IsFullDocument);

    /// <summary>
    /// Computes the reparse window.
    /// </summary>
    /// <param name="old">The block list as it stood <b>before</b> the splice (its start lines are old-document coordinates).</param>
    /// <param name="dirtyStartLine">The first old-document line the edit touched.</param>
    /// <param name="oldDirtyEndLine">The last old-document line the edit touched (≥ <paramref name="dirtyStartLine"/>).</param>
    /// <param name="delta">The document line-count change of the splice (new − old).</param>
    /// <param name="fastPath">Whether <see cref="FastPathGate"/> admitted this edit to the single-block path.</param>
    /// <param name="newFences">The fenced-region interval set of the <b>new</b> (post-splice) document.</param>
    /// <param name="newBuffer">The <b>new</b> (post-splice) buffer, read only for blank-line boundary tests.</param>
    /// <exception cref="ArgumentNullException"><paramref name="old"/>, <paramref name="newFences"/>, or <paramref name="newBuffer"/> is <see langword="null"/>.</exception>
    public static WindowPlan Plan(
        BlockList old,
        int dirtyStartLine,
        int oldDirtyEndLine,
        int delta,
        bool fastPath,
        FenceIntervalSet newFences,
        IDocumentBuffer newBuffer)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(newFences);
        ArgumentNullException.ThrowIfNull(newBuffer);

        int oldCount = old.Count;
        int oldTotal = old.TotalLineCount;
        int newLineCount = newBuffer.LineCount;

        // Degenerate: no old blocks (never happens for a live document — there is always ≥ 1) ⇒ full.
        if (oldCount == 0)
            return new WindowPlan(0, 0, 0, newLineCount, IsFullDocument: true);

        int clampedDirtyStart = Math.Clamp(dirtyStartLine, 0, oldTotal - 1);
        int clampedDirtyEnd = Math.Clamp(oldDirtyEndLine, clampedDirtyStart, oldTotal - 1);

        int firstDirty = old.IndexOfLine(clampedDirtyStart);
        int lastDirty = old.IndexOfLine(clampedDirtyEnd);

        int oldWs = firstDirty;
        int oldWe = lastDirty + 1;

        if (!fastPath)
        {
            // Expand each edge to the nearest *clean cut* — the only place a full parse puts a block
            // boundary. Because trailing blank lines attach to the block above, a clean cut is a
            // document edge or a content line whose preceding line is blank (see IsCleanCut). This one
            // predicate handles every reach-across class at once: a setext underline or lazy/list
            // continuation (previous line non-blank), a boundary splitting a trailing-blank run
            // (boundary line blank), and a block whose content was deleted leaving only blanks (its
            // start is blank, so its blanks re-attach to the block above). A boundary between old blocks
            // i-1 and i sits at new-document line old.GetStartLine(i), shifted by delta on the trailing
            // side (the suffix is unchanged, only shifted) and unshifted on the leading side
            // (≤ dirtyStart ⇒ unchanged).
            // A container (list, blockquote, alert, definition list, footnote) absorbs following
            // content across blank lines in ways a line scan cannot see, so a boundary adjacent to one
            // is never a safe cut: expand while the block just inside the trailing edge, or just
            // outside the leading edge, is a container — it may have grown to swallow its neighbour.
            while (oldWe < oldCount
                && (!IsCleanCut(newBuffer, old.GetStartLine(oldWe) + delta) || IsContainer(old[oldWe - 1].Kind)))
                oldWe++;

            while (oldWs > 0
                && (!IsCleanCut(newBuffer, old.GetStartLine(oldWs)) || IsContainer(old[oldWs - 1].Kind)))
                oldWs--;
        }

        int newStart = old.GetStartLine(oldWs);
        int newEnd = oldWe >= oldCount ? newLineCount : old.GetStartLine(oldWe) + delta;

        // Document-start blanks attach *down* to the first content block (there is no block above to
        // take them, unlike interior trailing blanks). If the window begins at line 0 and holds only
        // blanks, extend right to include that block so the blanks are not tiled as a spurious block.
        while (oldWs == 0 && oldWe < oldCount && AllBlank(newBuffer, newStart, newEnd))
        {
            oldWe++;
            newEnd = oldWe >= oldCount ? newLineCount : old.GetStartLine(oldWe) + delta;
        }

        // Leading fence guard (defensive): an old boundary should never be a fence interior, but if one
        // is, walk left until the window starts at or before the fence's opening line.
        while (oldWs > 0 && newFences.StartsInsideRegion(newStart))
        {
            oldWs--;
            newStart = old.GetStartLine(oldWs);
        }

        // Trailing fence parity: a boundary cutting a (necessarily edit-opened, unclosed) fence region
        // in the new document forces the window to end of document — the tail is reinterpreted.
        if (newEnd < newLineCount && newFences.ExtendExclusiveEndPastRegion(newEnd) != newEnd)
        {
            oldWe = oldCount;
            newEnd = newLineCount;
        }

        bool full = oldWs == 0 && oldWe == oldCount;
        return new WindowPlan(oldWs, oldWe, newStart, newEnd, full);
    }

    /// <summary>
    /// Whether new-document line <paramref name="line"/> is a clean block-boundary cut: a place where a
    /// full parse is guaranteed to end one top-level block and begin another, so a window edge placed
    /// here tiles the same way in isolation as in the whole document. That is a document edge, or a
    /// content line, preceded by a blank line, that <b>cannot continue a container above it</b>.
    /// Trailing blanks attach to the block above (hence the blank-before requirement); a list or
    /// blockquote, however, continues <i>across</i> a blank into indented content, another blockquote
    /// marker, or a same-level list-item marker — so a cut line that is indented, starts with <c>&gt;</c>,
    /// or begins a list-item marker is rejected (conservatively; over-widening the window is always
    /// correct, it only re-parses more). Only a non-indented, non-container block start (paragraph,
    /// heading, fence, table, thematic break, HTML, …) after a blank is a clean cut.
    /// </summary>
    private static bool IsCleanCut(IDocumentBuffer buffer, int line)
    {
        if (line <= 0 || line >= buffer.LineCount)
            return true;

        if (BlankAt(buffer, line) || !BlankAt(buffer, line - 1))
            return false;

        string cur = buffer.GetLine(line).Text;
        if (cur.Length == 0)
            return false;

        char first = cur[0];
        if (first is ' ' or '\t' or '>')
            return false; // indented (container content) or a blockquote start

        return !StartsListMarker(cur);
    }

    /// <summary>Whether <paramref name="text"/> begins with a list-item marker (<c>- </c>/<c>* </c>/<c>+ </c> or an ordered <c>1.</c>/<c>1)</c>) — which continues a loose list across a blank.</summary>
    private static bool StartsListMarker(string text)
    {
        char c = text[0];
        if (c is '-' or '*' or '+')
            return text.Length == 1 || text[1] is ' ' or '\t';

        if (char.IsAsciiDigit(c))
        {
            int i = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
                i++;

            return i < text.Length && text[i] is '.' or ')' && (i + 1 == text.Length || text[i + 1] is ' ' or '\t');
        }

        return false;
    }

    /// <summary>Whether <paramref name="kind"/> is a container block that can absorb a neighbouring block across a blank-line boundary.</summary>
    private static bool IsContainer(BlockKind kind) => kind is
        BlockKind.List or BlockKind.Quote or BlockKind.Alert or BlockKind.DefinitionList or BlockKind.Footnote;

    /// <summary>Whether every new-document line in <c>[start, end)</c> is blank.</summary>
    private static bool AllBlank(IDocumentBuffer buffer, int start, int end)
    {
        for (int line = start; line < end; line++)
        {
            if (!BlankAt(buffer, line))
                return false;
        }

        return true;
    }

    /// <summary>Whether new-document line <paramref name="line"/> is blank (whitespace only). Out-of-range lines are treated as non-blank.</summary>
    private static bool BlankAt(IDocumentBuffer buffer, int line)
    {
        if ((uint) line >= (uint) buffer.LineCount)
            return false;

        return string.IsNullOrWhiteSpace(buffer.GetLine(line).Text);
    }
}
