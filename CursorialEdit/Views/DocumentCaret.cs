using Cursorial.Rendering.Text;
using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;

namespace CursorialEdit.Views;

/// <summary>
/// The real document caret and selection (M1.WP8, architecture §2.4): the caret is a
/// <b>source anchor</b> — a <see cref="TextPosition"/> into the buffer — and every visual
/// question (which terminal cell, which visual row) is answered by mapping it through the
/// <see cref="BlockList"/> prefix sums and the block's <see cref="BlockRunMap"/>
/// (block → visual row → cell). The selection is a document-level source range
/// (anchor + active end — the <see cref="CaretState"/> shape), intersected per presenter at
/// draw time via <see cref="ISelectionSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordinates and invariants.</b> Every position this class produces is buffer-valid and
/// grapheme-cluster-snapped (<see cref="GraphemeLayout"/> produces all landings — the WP4 fuzz
/// invariant). The goal column is stored in <b>cells</b> and re-applied per landing row
/// (§3.1 [EDGE]); it survives a vertical run and is forgotten by any non-vertical operation,
/// mirroring the framework <c>TextBox</c>'s sticky <c>_desiredColumn</c>. End-of-row
/// <b>affinity</b> disambiguates a caret col sitting exactly on a soft-wrap boundary (one source
/// position, two visual positions): End and vertical landings keep the earlier row's visual end,
/// Right/Home/typing take the next row's start — <see cref="TextLayout"/>'s probed contract.
/// </para>
/// <para>
/// <b>Mutations.</b> Editing operations build <see cref="Edit"/>s and funnel them through
/// <see cref="EditController.Apply"/>; the live caret lands on the splice receipt's
/// <see cref="SpliceResult.End"/> (authoritative — it resolves the bare-CR seam corners), while
/// the recorded redo caret is the cheap prediction from the inserted text, which agrees with the
/// receipt for everything the M1 input path can produce (typing inserts no bare <c>'\r'</c>;
/// paste payloads may, and <see cref="PredictEnd"/> counts every terminator shape — only the
/// splice-seam merges diverge, and those merely seal the group and snap on restore).
/// Every caret change — including each edit's own landing — is routed through
/// <see cref="EditController.NotifyCaretMoved"/> in order, per the controller's one-shot
/// echo-license contract: the edit's landing is consumed as its echo, everything else seals the
/// open coalescing group.
/// </para>
/// <para>
/// <b>Restore snapping.</b> <see cref="CaretState"/>s returned by undo/redo are opaque payloads;
/// this class re-validates them on arrival (clamp to the buffer, pin to a cluster boundary), so
/// a state recorded against a document the bare-CR seam later reshaped can never install an
/// invalid caret.
/// </para>
/// <para>
/// <b>Selection repaint economics.</b> After every state change the painted selection range
/// (absolute offsets, recorded at paint time) is compared per <i>realized</i> presenter against
/// the new range, and only presenters whose block intersection changed are invalidated
/// (architecture §2.3). After an edit the recorded offsets are stale relative to the new buffer;
/// that is sound because the reconciliation pass has already invalidated every re-formed block —
/// the comparison here only adds invalidations, never suppresses one for a block that changed.
/// </para>
/// <para>All members are UI-thread-only, like the controller and buffer they drive.</para>
/// </remarks>
internal sealed class DocumentCaret : ISelectionSource
{
    /// <summary>Spec §6.3 [DECISION]: Tab in a paragraph inserts the configured indent — default spaces, width 2 (stray tabs become spaces).</summary>
    internal const string IndentText = "  ";

    private readonly IDocumentBuffer _buffer;
    private readonly EditController _controller;
    private readonly IEditorViewSource _host;
    private readonly IContentRowMap _rows;
    private readonly TableEditingController _table;

    private TextPosition _position;
    private TextPosition? _anchor;

    /// <summary>The sticky goal column in cells; −1 when no vertical run is in progress.</summary>
    private int _goalCell = -1;

    /// <summary>Whether the caret col, when it sits exactly on a soft-wrap boundary, renders at the earlier row's end.</summary>
    private bool _endAffinity;

    /// <summary>
    /// The block-relative selection range each block (by stable id) painted in the last repaint pass.
    /// Keyed by identity and stored <i>relative</i> to the block, so the diff is immune to a splice in
    /// an earlier block shifting later blocks' absolute offsets (a stale absolute compare missed a
    /// block whose overlay had to change — the bug this replaced) while still catching a selection
    /// that grew or shrank <i>within</i> a block (which a membership-only set would miss).
    /// </summary>
    private readonly Dictionary<BlockId, (int Start, int End)> _selectionPainted = [];

    /// <summary>Creates the caret at the document origin.</summary>
    public DocumentCaret(EditController controller, IEditorViewSource host, IContentRowMap rows)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(rows);

        _controller = controller;
        _buffer = controller.Buffer;
        _host = host;
        _rows = rows;
        _table = new TableEditingController(_buffer);
    }

    /// <summary>Raised after every caret/selection state change — the owner re-publishes the terminal caret and scroll-follows.</summary>
    public event Action? Updated;

    /// <summary>The caret position (the active end of the selection, when one exists).</summary>
    public TextPosition Position => _position;

    /// <summary>The fixed end of the selection, or <see langword="null"/> when there is none.</summary>
    public TextPosition? SelectionAnchor => _anchor;

    /// <summary>The caret state in the undo-record shape.</summary>
    public CaretState State => new(_position, _anchor);

    /// <summary>Whether a non-empty selection exists.</summary>
    public bool HasSelection => _anchor is { } anchor && anchor != _position;

    private BlockList Blocks => _host.Blocks;

    // ───────────────────────────── visual mapping ─────────────────────────────

    /// <summary>
    /// The caret's visual position in content coordinates: the document row (block top + wrap row,
    /// resolved per the caret's affinity) and the cell column — what the owner publishes through
    /// <c>ITerminalCaretService</c> and scroll-follows with.
    /// </summary>
    public (int DocumentRow, int Cell) VisualDocumentPosition()
    {
        var (blockIndex, map, rel) = LocateCaret();
        var (row, cell) = map.Locate(rel, _endAffinity);

        // On the markdown surface the active line is drawn horizontally slid to keep the caret visible
        // (Decision 9); the published column is that slide (or a table's column-window offset) subtracted. Clamp
        // to the block's on-screen drawn width so a caret deep in a focused over-wide/truncated cell (whose map
        // cell is the UNCLIPPED natural column, drawn only up to the reveal/window clip) never publishes past the
        // clip edge and off-screen. The plain surface reports slide 0 and the viewport width.
        int visible = Math.Clamp(cell - _host.ActiveSlide(blockIndex, row), 0, Math.Max(0, _host.VisibleWidth(blockIndex)));
        return (_rows.BlockTopRow(blockIndex) + row, visible);
    }

    // ───────────────────────────── horizontal motion ─────────────────────────────

    /// <summary>
    /// Caret Left: the previous <b>visible</b> caret stop (M2.WP8) — the previous grapheme cluster within
    /// the run map's <c>Text</c>/<c>RevealedMark</c> cells, <b>structurally skipping zero-width
    /// <c>HiddenMark</c> runs</c> (you never land inside a hidden mark) and treating a
    /// <c>Synthetic</c> marker (bullet, <c>↵</c>) as one atomic stop, crossing to the previous line's end
    /// at col 0. On the plain surface (only <c>Text</c> runs) this is the ordinary cluster walk.
    /// </summary>
    public void MoveLeft(bool extend) => MoveTo(PrevCaretPosition(_position), extend, endAffinity: false);

    /// <summary>
    /// Caret Right: the next <b>visible</b> caret stop — the mirror of <see cref="MoveLeft"/>. Moving right
    /// from just-before a hidden mark jumps past it to the next visible cluster's source offset; on the
    /// active (revealed) line the marks are visible and landable, on inactive lines they are skipped.
    /// </summary>
    public void MoveRight(bool extend) => MoveTo(NextCaretPosition(_position), extend, endAffinity: false);

    /// <summary>
    /// Ctrl+Right — word motion over the block's <b>rendered</b> text (M2.WP8 / §2.4): the probed
    /// <c>TextBox</c> whitespace rule (skip a whitespace run, then a word run, land at the word's
    /// <b>end</b>) applied to the concatenated <b>visible</b> clusters — <b>hidden marks are skipped</b>
    /// (word boundaries are computed on what's rendered, not the raw source: an inactive <c>**bold**</c>
    /// navigates as <c>bold</c>), a terminator/line cross is whitespace, and every stop is canonicalized to
    /// a visible caret position. On the plain surface (no marks) this reduces to the raw whitespace rule.
    /// </summary>
    public void MoveWordRight(bool extend)
    {
        var pos = Visible(_position);

        pos = StepWordRight(pos, char.IsWhiteSpace); // skip the whitespace run to the next word grapheme
        pos = StepWordRight(pos, c => !char.IsWhiteSpace(c)); // consume the word run to its end

        MoveTo(pos, extend, endAffinity: false);
    }

    /// <summary>Ctrl+Left — the mirror walk over rendered text: skip the whitespace run backward (crossing line starts, skipping hidden marks), then the word run back to its start.</summary>
    public void MoveWordLeft(bool extend)
    {
        var pos = Visible(_position);

        pos = StepWordLeft(pos, char.IsWhiteSpace);
        pos = StepWordLeft(pos, c => !char.IsWhiteSpace(c));

        MoveTo(pos, extend, endAffinity: false);
    }

    /// <summary>Advances forward over the maximal run of visible graphemes satisfying <paramref name="take"/> (a terminator/line cross reads as whitespace; the document end stops the walk).</summary>
    private TextPosition StepWordRight(TextPosition pos, Func<char, bool> take)
    {
        while (VisibleCharAt(pos) is { } c && take(c))
        {
            var next = NextVisibleStop(pos);
            if (next == pos)
                break; // document end
            pos = next;
        }

        return pos;
    }

    /// <summary>Retreats over the maximal run of visible graphemes whose predecessor satisfies <paramref name="take"/> (the mirror of <see cref="StepWordRight"/>).</summary>
    private TextPosition StepWordLeft(TextPosition pos, Func<char, bool> take)
    {
        while (VisibleCharBefore(pos) is { } c && take(c))
        {
            var prev = PrevVisibleStop(pos);
            if (prev == pos)
                break; // document start
            pos = prev;
        }

        return pos;
    }

    // ───────────────────────────── run-aware caret-stop stepping ─────────────────────────────

    /// <summary>
    /// The next visible caret stop after <paramref name="pos"/> (M2.WP8): the next source cluster that
    /// occupies a <b>distinct display cell</b> from <paramref name="pos"/> on its block's run map, so
    /// zero-width hidden marks and the atomic remainder of a synthetic marker are skipped; a source-line
    /// cross is always a visible move. Returns <paramref name="pos"/> unchanged at the document end.
    /// </summary>
    private TextPosition NextCaretPosition(TextPosition pos)
    {
        bool hasCell = TryCell(pos, out int originCell);
        var layout = GraphemeLayout.Build(_buffer.GetLine(pos.Line).Text); // once — the loop never leaves pos.Line
        var cur = pos;
        while (RawNext(cur, layout) is { } next)
        {
            if (next.Line != pos.Line)
                return next;                    // crossed a source line — always a visible move
            if (!hasCell)
                return next;                    // pre-layout — behave as the plain source cluster walk
            if (!TryCell(next, out int cell) || cell != originCell)
                return next;                    // a distinct display cell — a real move
            cur = next;                         // same cell (a zero-width hidden mark) — keep skipping
        }

        return cur; // document end
    }

    /// <summary>The previous visible caret stop before <paramref name="pos"/> — the mirror of <see cref="NextCaretPosition"/>.</summary>
    private TextPosition PrevCaretPosition(TextPosition pos)
    {
        bool hasCell = TryCell(pos, out int originCell);
        var layout = GraphemeLayout.Build(_buffer.GetLine(pos.Line).Text); // once — the loop never leaves pos.Line
        var cur = pos;
        while (RawPrev(cur, layout) is { } prev)
        {
            if (prev.Line != pos.Line)
                return prev;
            if (!hasCell)
                return prev;
            if (!TryCell(prev, out int cell) || cell != originCell)
                return prev;
            cur = prev;
        }

        return cur;
    }

    /// <summary>
    /// The next source cluster boundary (crossing to the next line's start at the line end; null at the
    /// document end), reusing a caller-built <paramref name="lineLayout"/> of <paramref name="pos"/>'s line.
    /// A same-line stepping loop (word / hidden-mark motion) builds it ONCE: the promoted
    /// <see cref="GraphemeLayout"/> precomputes every boundary, so rebuilding it per step would be
    /// O(steps × line) with an allocation each step (FB-1 review).
    /// </summary>
    private TextPosition? RawNext(TextPosition pos, in GraphemeLayout lineLayout)
    {
        if (pos.Col < _buffer.GetLine(pos.Line).Text.Length)
            return new(pos.Line, lineLayout.NextBoundary(pos.Col));
        if (pos.Line < _buffer.LineCount - 1)
            return new(pos.Line + 1, 0);
        return null;
    }

    /// <summary>The previous source cluster boundary (crossing to the previous line's end at col 0; null at the document start), reusing a caller-built <paramref name="lineLayout"/> of <paramref name="pos"/>'s line (see <see cref="RawNext(TextPosition, in GraphemeLayout)"/>).</summary>
    private TextPosition? RawPrev(TextPosition pos, in GraphemeLayout lineLayout)
    {
        if (pos.Col > 0)
            return new(pos.Line, lineLayout.PrevBoundary(pos.Col));
        if (pos.Line > 0)
            return new(pos.Line - 1, _buffer.GetLine(pos.Line - 1).Text.Length);
        return null;
    }

    /// <summary>The display cell <paramref name="pos"/> maps to on its block's run map, or <see langword="false"/> pre-layout (no geometry yet).</summary>
    private bool TryCell(TextPosition pos, out int cell)
    {
        cell = 0;
        if (_rows.ContentRows <= 0)
            return false;

        int blockIndex = Blocks.IndexOfLine(pos.Line);
        var map = GetMap(blockIndex);
        int rel = _buffer.GetOffset(pos) - BlockStartOffset(blockIndex);
        cell = map.Locate(rel).Cell;
        return true;
    }

    // ───────────────────────────── visible-text stepping (word motion) ─────────────────────────────

    /// <summary>
    /// Snaps <paramref name="pos"/> to the <b>canonical visible</b> offset at its rendered cell (M2.WP8):
    /// the block's run map collapses hidden marks onto the following content's cell, and
    /// <c>OffsetAt(cell)</c> returns that content's source offset — so a position sitting on a hidden mark
    /// canonicalizes to the visible grapheme it renders under. On the active (revealed) line, and on the
    /// plain surface, the marks are visible and this is the identity.
    /// </summary>
    private TextPosition Visible(TextPosition pos)
    {
        if (_rows.ContentRows <= 0)
            return pos;

        int blockIndex = Blocks.IndexOfLine(pos.Line);
        var map = GetMap(blockIndex);
        int rel = _buffer.GetOffset(pos) - BlockStartOffset(blockIndex);
        var (row, cell) = map.Locate(rel);
        return PositionOfBlockRel(blockIndex, map.OffsetAt(row, cell));
    }

    /// <summary>The next distinct <b>visible</b> caret stop after <paramref name="pos"/> — hidden marks are skipped (they canonicalize onto their content); a line cross canonicalizes into the new line's visible content.</summary>
    private TextPosition NextVisibleStop(TextPosition pos)
    {
        var origin = Visible(pos);
        var layout = GraphemeLayout.Build(_buffer.GetLine(origin.Line).Text); // once — the loop never leaves origin.Line
        var cur = origin;
        while (RawNext(cur, layout) is { } next)
        {
            if (next.Line != origin.Line)
                return Visible(next);
            var visible = Visible(next);
            if (visible != origin)
                return visible;
            cur = next;
        }

        return origin; // document end
    }

    /// <summary>The previous distinct visible caret stop before <paramref name="pos"/> — the mirror of <see cref="NextVisibleStop"/>.</summary>
    private TextPosition PrevVisibleStop(TextPosition pos)
    {
        var origin = Visible(pos);
        var layout = GraphemeLayout.Build(_buffer.GetLine(origin.Line).Text); // once — the loop never leaves origin.Line
        var cur = origin;
        while (RawPrev(cur, layout) is { } prev)
        {
            if (prev.Line != origin.Line)
                return Visible(prev);
            var visible = Visible(prev);
            if (visible != origin)
                return visible;
            cur = prev;
        }

        return origin; // document start
    }

    /// <summary>The visible grapheme at <paramref name="pos"/> for the word rule: the source char (marks are canonical), a space at a line end (the terminator), or <see langword="null"/> at the document end.</summary>
    private char? VisibleCharAt(TextPosition pos)
    {
        string text = _buffer.GetLine(pos.Line).Text;
        if (pos.Col < text.Length)
            return text[pos.Col];
        return pos.Line < _buffer.LineCount - 1 ? ' ' : null; // line end = terminator whitespace; last line = document end
    }

    /// <summary>The visible grapheme immediately before <paramref name="pos"/>: the char at the previous visible stop, a space across a line boundary, or <see langword="null"/> at the document start.</summary>
    private char? VisibleCharBefore(TextPosition pos)
    {
        var prev = PrevVisibleStop(pos);
        if (prev == pos)
            return null; // document start
        if (prev.Line != pos.Line)
            return ' '; // crossed a terminator — whitespace

        string text = _buffer.GetLine(prev.Line).Text;
        return prev.Col < text.Length ? text[prev.Col] : ' ';
    }

    // ───────────────────────────── vertical motion ─────────────────────────────

    /// <summary>
    /// Up/Down/PageUp/PageDown: moves the caret <paramref name="deltaRows"/> <b>visual</b> rows,
    /// landing at the sticky goal column (recorded in cells on the first vertical step, kept for
    /// the run), clamped to the document's rows — cross-line/cross-block composition over
    /// <see cref="BlockRunMap.OffsetAt"/>'s cluster-pinned goal landing.
    /// </summary>
    public void MoveVertical(int deltaRows, bool extend)
    {
        int totalRows = _rows.ContentRows;
        if (totalRows <= 0)
            return; // pre-layout — there is no visual geometry to move through yet

        var (blockIndex, map, rel) = LocateCaret();
        var (rowInBlock, cell) = map.Locate(rel, _endAffinity);
        // The goal column is a VISIBLE column (what the user sees), so a slid prose line or a windowed/overflowing
        // table records the on-screen column — the active-line slide / table column-window offset subtracted, the
        // same as the caret publishes (VisualDocumentPosition). Otherwise vertical motion out of a windowed table
        // would carry the UNCLIPPED cell (~45) as the goal and land far right in the block below.
        int goal = _goalCell >= 0 ? _goalCell : cell - _host.ActiveSlide(blockIndex, rowInBlock);
        int docRow = _rows.BlockTopRow(blockIndex) + rowInBlock;
        int targetRow = Math.Clamp(docRow + deltaRows, 0, totalRows - 1);

        // Step past non-caret rows (a table's box-drawing border/delimiter rows) in the travel direction so
        // the caret leaves the table into the adjacent block instead of snapping back onto the last cell it
        // was on (bug 1). Falls back to the other direction if the step runs off the document edge.
        targetRow = SkipToCaretRow(targetRow, deltaRows >= 0 ? 1 : -1, totalRows);

        int targetBlock = _rows.BlockIndexOfRow(targetRow);
        var targetMap = GetMap(targetBlock);
        int targetRowInBlock = Math.Clamp(targetRow - _rows.BlockTopRow(targetBlock), 0, targetMap.RowCount - 1);
        // Land at the goal in the target's own UNCLIPPED map space — add its slide/window offset back (the mirror
        // of the capture above, matching how PositionFromContentPoint resolves a click).
        int landingRel = targetMap.OffsetAt(targetRowInBlock, goal + _host.ActiveSlide(targetBlock, targetRowInBlock));
        bool affinity = targetMap.Locate(landingRel).Row != targetRowInBlock;

        MoveTo(PositionOfBlockRel(targetBlock, landingRel), extend, affinity, goal);
    }

    /// <summary>Advances <paramref name="row"/> in the <paramref name="step"/> direction while it has no caret stop (a table border row), reversing if it runs off the document.</summary>
    private int SkipToCaretRow(int row, int step, int totalRows)
    {
        int r = row;
        while (r >= 0 && r < totalRows && !RowHasCaretStop(r))
            r += step;

        if (r < 0 || r >= totalRows)
        {
            r = row;
            while (r >= 0 && r < totalRows && !RowHasCaretStop(r))
                r -= step;
        }

        return Math.Clamp(r, 0, totalRows - 1);
    }

    /// <summary>Whether the block owning content <paramref name="docRow"/> reports a caret stop on that row (false only for a table's border rows).</summary>
    private bool RowHasCaretStop(int docRow)
    {
        int block = _rows.BlockIndexOfRow(docRow);
        var map = GetMap(block);
        int rowInBlock = Math.Clamp(docRow - _rows.BlockTopRow(block), 0, map.RowCount - 1);
        return map.HasCaretStop(rowInBlock);
    }

    // ───────────────────────────── Home / End / document ends ─────────────────────────────

    /// <summary>Home: the start of the caret's <b>visual</b> row (start-affinity) — the probed <c>TextBox</c> per-row semantics.</summary>
    public void MoveHome(bool extend)
    {
        var (blockIndex, map, rel) = LocateCaret();
        int row = map.Locate(rel, _endAffinity).Row;
        MoveTo(PositionOfBlockRel(blockIndex, map.OffsetAt(row, 0)), extend, endAffinity: false);
    }

    /// <summary>
    /// End: the content end of the caret's <b>visual</b> row, keeping end-affinity when that col
    /// aliases the next row's start — the caret renders at this row's end (probe §Home/End).
    /// </summary>
    public void MoveEnd(bool extend)
    {
        var (blockIndex, map, rel) = LocateCaret();
        int row = map.Locate(rel, _endAffinity).Row;
        int endRel = map.RowEndOffset(row);
        bool affinity = map.Locate(endRel).Row != row;
        MoveTo(PositionOfBlockRel(blockIndex, endRel), extend, affinity);
    }

    /// <summary>Ctrl+Home: the document origin.</summary>
    public void MoveDocumentStart(bool extend) => MoveTo(TextPosition.Zero, extend, endAffinity: false);

    /// <summary>Ctrl+End: the end of the last line's text.</summary>
    public void MoveDocumentEnd(bool extend)
    {
        int line = _buffer.LineCount - 1;
        MoveTo(new(line, _buffer.GetLine(line).Text.Length), extend, endAffinity: false);
    }

    // ───────────────────────────── selection commands ─────────────────────────────

    /// <summary>Ctrl+A: anchor at the origin, active end at the document end.</summary>
    public void SelectAll()
    {
        int line = _buffer.LineCount - 1;
        SetState(new(line, _buffer.GetLine(line).Text.Length), TextPosition.Zero, endAffinity: false);
    }

    /// <summary>
    /// Double-click: selects the whitespace-delimited word at <paramref name="pos"/> (a click on
    /// whitespace selects the whitespace run instead) — <c>TextBox.SelectWordAt</c>'s rule,
    /// line-scoped because a word never crosses a terminator.
    /// </summary>
    public void SelectWordAt(TextPosition pos)
    {
        string text = _buffer.GetLine(pos.Line).Text;
        if (text.Length == 0)
        {
            SetState(new(pos.Line, 0), anchor: null, endAffinity: false);
            return;
        }

        int index = Math.Clamp(pos.Col, 0, text.Length);
        int start = index, end = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

        if (start == end) // the click was on whitespace — select the whitespace run instead
        {
            while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
        }

        var glyphs = GraphemeLayout.Build(text);
        SetState(
            new(pos.Line, glyphs.PinToBoundary(end)),
            new TextPosition(pos.Line, glyphs.PinToBoundary(start)),
            endAffinity: false);
    }

    /// <summary>
    /// Triple-click: selects the whole block (paragraph) containing <paramref name="pos"/> — from
    /// the block's first line start to its last line's text end (the M1 producer attaches trailing
    /// blank separator lines to the paragraph above, so they ride along, painting nothing).
    /// </summary>
    public void SelectBlockAt(TextPosition pos)
    {
        int blockIndex = Blocks.IndexOfLine(pos.Line);
        int startLine = Blocks.GetStartLine(blockIndex);
        int lastLine = startLine + Blocks[blockIndex].LineCount - 1;
        SetState(
            new(lastLine, _buffer.GetLine(lastLine).Text.Length),
            new TextPosition(startLine, 0),
            endAffinity: false);
    }

    // ───────────────────────────── mouse hit-testing ─────────────────────────────

    /// <summary>
    /// Maps a panel-local content point to a caret landing: content row → block (prefix sums) →
    /// wrap row → the <b>nearer</b> cluster boundary (a click on a wide glyph's right half rounds
    /// per <c>TextBox.IndexFromPointer</c>'s display-space rule, ties toward the earlier boundary),
    /// with end-affinity when the landing aliases the next row's start so the caret stays on the
    /// clicked row. Coordinates clamp into the content, so drags past the edges land on the
    /// nearest row/col.
    /// </summary>
    public (TextPosition Position, bool EndAffinity) PositionFromContentPoint(int cell, int contentRow)
    {
        int totalRows = _rows.ContentRows;
        if (totalRows <= 0)
            return (TextPosition.Zero, false);

        contentRow = Math.Clamp(contentRow, 0, totalRows - 1);
        cell = Math.Max(0, cell);

        int blockIndex = _rows.BlockIndexOfRow(contentRow);
        var map = GetMap(blockIndex);
        int row = Math.Clamp(contentRow - _rows.BlockTopRow(blockIndex), 0, map.RowCount - 1);

        // On the markdown surface the active line is slid, so a click's panel cell maps back through the
        // unclipped map at cell + slide; the plain surface reports slide 0 (unchanged mapping).
        int rel = map.NearestOffset(row, cell + _host.ActiveSlide(blockIndex, row));
        bool affinity = map.Locate(rel).Row != row;
        return (PositionOfBlockRel(blockIndex, rel), affinity);
    }

    /// <summary>Single click: positions (collapses) the caret at the hit point.</summary>
    public void ClickAt(int cell, int contentRow)
    {
        var (pos, affinity) = PositionFromContentPoint(cell, contentRow);
        MoveTo(pos, extend: false, affinity);
    }

    /// <summary>Drag: extends the selection from the mouse-down position toward the pointer.</summary>
    public void DragTo(int cell, int contentRow)
    {
        var (pos, affinity) = PositionFromContentPoint(cell, contentRow);
        MoveTo(pos, extend: true, affinity);
    }

    // ───────────────────────────── editing ─────────────────────────────

    /// <summary>Printable input: inserts at the caret, or replaces the selection (<see cref="EditKind.Typing"/>). Inside a table cell it routes through <see cref="TableEditingController"/> (pipe escaping, empty-cell padding, cell-clamped over a selection).</summary>
    public void InsertText(string text)
    {
        if (TryTableContext(out var model, out int blockStart))
        {
            if (HasSelection)
            {
                RunTableCommand(TableReplace(model, blockStart, text, EditKind.Typing, collapseBreaks: false));
                return;
            }

            if (CaretOnTableRow(model))
            {
                RunTableCommand(_table.Type(model, blockStart, _position, text));
                return;
            }
        }

        ReplaceSelectionOrInsert(text, EditKind.Typing);
    }

    /// <summary>Enter: a line break — always its own undo group (<see cref="EditKind.Newline"/>, §3.3). Inside a table it commits downward (GFM cells are single-line, spec §5.4).</summary>
    public void InsertNewline()
    {
        if (!HasSelection && TryTableContext(out var model, out int blockStart) && CaretOnTableRow(model))
        {
            RunTableCommand(_table.Enter(model, blockStart, _position));
            return;
        }

        ReplaceSelectionOrInsert("\n", EditKind.Newline);
    }

    /// <summary>
    /// Tab: inserts the spec §6.3 indent (<see cref="IndentText"/> — stray tabs become spaces,
    /// width 2; a literal <c>'\t'</c> never enters the document from the keyboard). Sealed on both
    /// sides so it is its own undo unit, mirroring the <c>TextBox</c> reference's Tab handling.
    /// </summary>
    public void InsertIndent()
    {
        _controller.SealGroup();
        ReplaceSelectionOrInsert(IndentText, EditKind.Typing);
        _controller.SealGroup();
    }

    /// <summary>
    /// Backspace: deletes the selection when one exists; otherwise the previous grapheme cluster,
    /// or — at a line start — the previous line's terminator (joining the lines, CRLF removed
    /// whole).
    /// </summary>
    public void Backspace()
    {
        if (TryTableContext(out var model, out int blockStart))
        {
            if (HasSelection)
            {
                RunTableCommand(TableReplace(model, blockStart, string.Empty, EditKind.Typing, collapseBreaks: false));
                return;
            }

            if (CaretOnTableRow(model))
            {
                RunTableCommand(_table.Backspace(model, blockStart, _position));
                return;
            }
        }

        if (HasSelection)
        {
            ReplaceSelectionOrInsert(string.Empty, EditKind.Typing);
            return;
        }

        var pos = _position;
        if (pos.Col > 0)
        {
            string text = _buffer.GetLine(pos.Line).Text;
            int prev = GraphemeLayout.Build(text).PrevBoundary(pos.Col);
            ApplyEdit(new Edit(new(pos.Line, prev), text[prev..pos.Col], string.Empty), EditKind.Typing);
        }
        else if (pos.Line > 0)
        {
            var previousLine = _buffer.GetLine(pos.Line - 1);
            ApplyEdit(
                new Edit(new(pos.Line - 1, previousLine.Text.Length), previousLine.EndingText, string.Empty),
                EditKind.Typing);
        }
    }

    /// <summary>
    /// Forward Delete: deletes the selection when one exists; otherwise the next grapheme cluster,
    /// or — at a line's text end — the line's terminator (joining with the next line).
    /// </summary>
    public void DeleteForward()
    {
        if (TryTableContext(out var model, out int blockStart))
        {
            if (HasSelection)
            {
                RunTableCommand(TableReplace(model, blockStart, string.Empty, EditKind.Typing, collapseBreaks: false));
                return;
            }

            if (CaretOnTableRow(model))
            {
                RunTableCommand(_table.DeleteForward(model, blockStart, _position));
                return;
            }
        }

        if (HasSelection)
        {
            ReplaceSelectionOrInsert(string.Empty, EditKind.Typing);
            return;
        }

        var pos = _position;
        var line = _buffer.GetLine(pos.Line);
        if (pos.Col < line.Text.Length)
        {
            int next = GraphemeLayout.Build(line.Text).NextBoundary(pos.Col);
            ApplyEdit(new Edit(pos, line.Text[pos.Col..next], string.Empty), EditKind.Typing);
        }
        else if (pos.Line < _buffer.LineCount - 1)
        {
            ApplyEdit(new Edit(pos, line.EndingText, string.Empty), EditKind.Typing);
        }
    }

    /// <summary>Undoes the top group and restores its recorded caret + selection. False when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_controller.Undo() is not { } restored)
            return false;

        RestoreState(restored);
        return true;
    }

    /// <summary>Redoes the most recently undone group and restores its recorded caret. False when there is nothing to redo.</summary>
    public bool Redo()
    {
        if (_controller.Redo() is not { } restored)
            return false;

        RestoreState(restored);
        return true;
    }

    // ───────────────────────────── clipboard (M1.WP9) ─────────────────────────────

    /// <summary>
    /// The selection's exact source text — the serialized half-open range between the
    /// normalized selection ends, interior line terminators included as their literal
    /// characters (<c>"\n"</c> / <c>"\r\n"</c> per line, byte-exact by the buffer's
    /// <see cref="IDocumentBuffer.GetText(TextPosition, TextPosition)"/> contract) — or
    /// <see langword="null"/> when there is no selection (spec §3: M1 copy is
    /// selection-only; line-copy conventions are later milestones').
    /// </summary>
    public string? SelectedText()
    {
        if (!HasSelection)
            return null;

        var (start, end) = NormalizedSelection();
        return _buffer.GetText(start, end);
    }

    /// <summary>
    /// Cut's delete half: removes the selection as its own atomic undo unit. The kind is
    /// <see cref="EditKind.Structural"/>, deliberately not <see cref="EditKind.Typing"/> — a
    /// Typing pure deletion opens a coalescing delete-run, so a Backspace right after the cut
    /// would fold into its group; Structural keeps "one undo restores exactly the cut", with
    /// the recorded before-state bringing the selection back. No-op without a selection.
    /// </summary>
    public void DeleteSelection()
    {
        if (HasSelection)
            ReplaceSelectionOrInsert(string.Empty, EditKind.Structural);
    }

    /// <summary>
    /// Paste — both inbound paths funnel here (bracketed paste and the internal-store Ctrl+V):
    /// inserts <paramref name="text"/> <b>literally</b> at the caret, replacing the selection
    /// when one exists, as <b>one</b> splice recorded as its own undo unit
    /// (<see cref="EditKind.Paste"/>). All paste is literal in M1 — no reparse, no terminator
    /// normalization (M4 owns smart paste); the buffer's splice splits any <c>"\n"</c> /
    /// <c>"\r\n"</c> / bare-<c>'\r'</c> mix into lines honestly. The caret lands on the splice
    /// receipt's end. Empty text no-ops.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public void Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return;

        if (TryTableContext(out var model, out int blockStart))
        {
            if (HasSelection)
            {
                RunTableCommand(TableReplace(model, blockStart, text, EditKind.Paste, collapseBreaks: true));
                return;
            }

            if (CaretOnTableRow(model))
            {
                RunTableCommand(_table.Paste(model, blockStart, _position, text));
                return;
            }
        }

        ReplaceSelectionOrInsert(text, EditKind.Paste);
    }

    // ───────────────────────────── table cell editing (M3.WP4) ─────────────────────────────

    /// <summary>Routes an edit over a table selection to the controller's cell-clamped <see cref="TableEditingController.Replace"/> (bug 3 — a cross-cell selection never deletes the separating pipe).</summary>
    private TableCommand TableReplace(TableModel model, int blockStart, string text, EditKind kind, bool collapseBreaks)
    {
        var (start, end) = NormalizedSelection();
        return _table.Replace(model, blockStart, start, end, text, kind, collapseBreaks);
    }

    /// <summary>Whether the caret sits on a table row line (the input layer routes Tab/Shift+Tab here — never on the delimiter or an absorbed trailing blank line).</summary>
    public bool IsInTable => TryTableContext(out var model, out _) && CaretOnTableRow(model);

    /// <summary>Tab / Shift+Tab inside a table: move to the next / previous cell (wrapping rows; last-cell Tab appends a row) — spec §5.3.</summary>
    public void TableTab(bool shift)
    {
        if (TryTableContext(out var model, out int blockStart))
            RunTableCommand(_table.Tab(model, blockStart, _position, shift));
    }

    /// <summary>Clears the caret's cell (spec §5.3 "Clear cell"); a no-op outside a table.</summary>
    public void TableClearCell()
    {
        if (TryTableContext(out var model, out int blockStart))
            RunTableCommand(_table.ClearCell(model, blockStart, _position));
    }

    /// <summary>Inserts a <c>&lt;br&gt;</c> cell break at the caret (spec §5.4); a no-op outside a table.</summary>
    public void TableInsertCellBreak()
    {
        if (TryTableContext(out var model, out int blockStart))
            RunTableCommand(_table.InsertCellBreak(model, blockStart, _position));
    }

    /// <summary>Resolves the caret's block to a live <see cref="TableModel"/> (Decision-4 cell focus is derived from the caret offset), or <see langword="false"/> when the caret is not in a table.</summary>
    private bool TryTableContext(out TableModel model, out int blockStart)
    {
        model = null!;
        blockStart = 0;
        if (_buffer.LineCount == 0 || Blocks.Count == 0)
            return false;

        int blockIndex = Blocks.IndexOfLine(_position.Line);
        if (_host.GetTableModel(blockIndex) is not { } table)
            return false;

        model = table;
        blockStart = BlockStartOffset(blockIndex);
        return true;
    }

    /// <summary>
    /// Whether the caret sits on an actual table <b>row</b> line — not the delimiter line, nor an absorbed
    /// trailing blank line the table block swallowed. A collapsed-caret cell edit / Tab / Enter routes to the
    /// table only when this holds (bug 5: Enter below a trailing table lands the caret on the swallowed blank
    /// line, which must edit as an ordinary paragraph). A selection edit ignores this — it clamps to the cell
    /// its start falls in, so a selection whose active end drifted onto the delimiter row is still safe (bug 3).
    /// </summary>
    private bool CaretOnTableRow(TableModel model)
    {
        int blockIndex = Blocks.IndexOfLine(_position.Line);
        return model.HasRowOnLine(_position.Line - Blocks.GetStartLine(blockIndex));
    }

    /// <summary>Applies a <see cref="TableCommand"/>: a splice (installing the controller-computed landing), a pure move, or an exit below the table.</summary>
    private void RunTableCommand(TableCommand command)
    {
        if (command.IsNoOp)
            return;

        if (command.ExitsBelow)
        {
            MoveCaretBelowTable();
            return;
        }

        if (command.Edit is not { } edit)
        {
            MoveTo(command.Caret, extend: false, endAffinity: false); // pure navigation — seals the group
            return;
        }

        if (command.Seal)
            _controller.SealGroup();

        var before = State;
        var after = new CaretState(command.Caret);
        _controller.Apply(edit, command.Kind, before, after);

        // The controller computed the intended landing (which may differ from the raw splice end — e.g. the
        // appended row lands in its first cell, not after the inserted text), so install it directly.
        _position = command.Caret;
        _anchor = null;
        _endAffinity = false;
        _goalCell = -1;

        if (command.Seal)
            _controller.SealGroup();

        _controller.NotifyCaretMoved(State);
        AfterStateChange();
    }

    /// <summary>Moves the caret out of the table below it (Enter on the last row): into the next block — landing in its first cell when that block is itself a table (bug 6) — or, when the table is the document's last block, adding an editable line beneath it and landing there (bug 5).</summary>
    private void MoveCaretBelowTable()
    {
        int blockIndex = Blocks.IndexOfLine(_position.Line);
        int nextBlock = blockIndex + 1;

        if (nextBlock < Blocks.Count)
        {
            // Bug 6: column 0 of a table's first line is its leading pipe, which owns no caret stop — a dead
            // caret. Exiting into another table must land in its first cell instead.
            if (_host.GetTableModel(nextBlock) is { } nextTable)
                MoveTo(_buffer.GetPosition(BlockStartOffset(nextBlock) + nextTable.CellEntryOffset(0, 0)), extend: false, endAffinity: false);
            else
                MoveTo(new TextPosition(Blocks.GetStartLine(nextBlock), 0), extend: false, endAffinity: false);

            return;
        }

        // Bug 5: the table is the document's last block — there is nothing below to move into, so add an
        // editable line beneath it (its own undo group) and land the caret there.
        int lastLine = Blocks.GetStartLine(blockIndex) + Blocks[blockIndex].LineCount - 1;
        var end = new TextPosition(lastLine, _buffer.GetLine(lastLine).Text.Length);
        ApplyEdit(new Edit(end, string.Empty, "\n"), EditKind.Newline);
    }

    // ───────────────────────────── ISelectionSource ─────────────────────────────

    /// <inheritdoc/>
    public (int Start, int End)? GetSelection(BlockId block)
    {
        var (selStart, selEnd) = CurrentAbsoluteSelection();
        if (selStart < 0)
            return null;

        int index = Blocks.IndexOf(block);
        if (index < 0)
            return null;

        int blockStart = BlockStartOffset(index);
        int from = Math.Max(selStart, blockStart);
        int to = Math.Min(selEnd, BlockEndOffset(index));
        return to > from ? (from - blockStart, to - blockStart) : null;
    }

    // ───────────────────────────── state transitions ─────────────────────────────

    /// <summary>
    /// The single move funnel: extends (anchoring at the pre-move caret when no selection exists)
    /// or collapses per <paramref name="extend"/>, installs affinity/goal, notifies the undo
    /// controller (sealing per its echo-license contract), repaints the selection delta, and
    /// raises <see cref="Updated"/>.
    /// </summary>
    private void MoveTo(TextPosition target, bool extend, bool endAffinity, int goalCell = -1)
        => SetState(target, extend ? _anchor ?? _position : null, endAffinity, goalCell);

    private void SetState(TextPosition position, TextPosition? anchor, bool endAffinity, int goalCell = -1)
    {
        _position = position;
        _anchor = anchor;
        _endAffinity = endAffinity;
        _goalCell = goalCell;

        _controller.NotifyCaretMoved(State);
        AfterStateChange();
    }

    private void RestoreState(CaretState state)
    {
        // Restored states are opaque payloads (CaretState remarks) — re-validate on arrival.
        SetState(
            Snap(state.Position),
            state.SelectionAnchor is { } anchor ? Snap(anchor) : null,
            endAffinity: false);
    }

    private void ReplaceSelectionOrInsert(string inserted, EditKind kind)
    {
        var (selStart, selEnd) = NormalizedSelection();
        var edit = selStart != selEnd
            ? new Edit(selStart, _buffer.GetText(selStart, selEnd), inserted)
            : new Edit(_position, string.Empty, inserted);

        ApplyEdit(edit, kind);
    }

    private void ApplyEdit(Edit edit, EditKind kind)
    {
        var before = State;
        var predictedAfter = new CaretState(PredictEnd(edit.Start, edit.Inserted));
        var result = _controller.Apply(edit, kind, before, predictedAfter);

        _position = result.End; // authoritative landing (bare-CR seam corners included)
        _anchor = null;
        _endAffinity = false;
        _goalCell = -1;

        // The edit's own landing — consumed by the controller's one-shot echo license (it equals
        // the recorded after-state whenever the prediction matched the receipt; a mismatch merely
        // seals the group, which is the safe direction).
        _controller.NotifyCaretMoved(State);
        AfterStateChange();
    }

    /// <summary>
    /// The post-splice caret prediction for this class's input alphabet. Typing inserts
    /// printable text and <c>"\n"</c>; paste payloads (WP9) may carry any terminator mix —
    /// <c>"\r\n"</c>, or bare <c>'\r'</c> (terminals conventionally transmit pasted line breaks
    /// as CR) — so every break shape is counted, CRLF as one. (The buffer's receipt remains the
    /// live caret's source of truth — it alone resolves the bare-CR seam corners, e.g. a pasted
    /// trailing <c>'\r'</c> merging with a following <c>'\n'</c> into one CRLF; this prediction
    /// only feeds the redo record, a mismatch merely seals the open group — the safe direction —
    /// and restores snap on arrival regardless.)
    /// </summary>
    private static TextPosition PredictEnd(TextPosition start, string inserted)
    {
        int line = start.Line;
        int afterLastBreak = -1; // index just past the most recent terminator; -1 = none seen
        for (int i = 0; i < inserted.Length; i++)
        {
            char c = inserted[i];
            if (c == '\r' && i + 1 < inserted.Length && inserted[i + 1] == '\n')
                i++; // CRLF is one break
            else if (c != '\n')
                continue; // a bare '\r' is ordinary content (LineEnding: only "\n"/"\r\n" break) — match the buffer

            line++;
            afterLastBreak = i + 1;
        }

        return afterLastBreak < 0
            ? new(start.Line, start.Col + inserted.Length)
            : new(line, inserted.Length - afterLastBreak);
    }

    // ───────────────────────────── selection repaint ─────────────────────────────

    private void AfterStateChange()
    {
        // Reveal-on-edit: the surface reveals the caret's active line and re-hides the prior block's
        // marks (markdown); the plain surface no-ops. Done before the owner republishes so the caret
        // publishes through the revealed, slid map.
        _host.OnCaretPositioned(_position);

        var (newStart, newEnd) = CurrentAbsoluteSelection();

        // Diff each block's block-relative selection range against what it last painted, keyed by id.
        // Both ranges are relative to the block's current offsets, so nothing compares across a
        // coordinate shift; the range (not just membership) is compared, so a selection growing or
        // shrinking within a block still invalidates it. A block that left the realized set is not
        // visited — it carries no on-screen overlay — and drops from the map below.
        var previous = _selectionPainted.Count == 0 ? null : new Dictionary<BlockId, (int, int)>(_selectionPainted);
        _selectionPainted.Clear();

        foreach (var (id, presenter) in _host.RealizedPresenters)
        {
            int index = Blocks.IndexOf(id);
            if (index < 0)
                continue; // a just-removed block awaiting the panel's teardown sweep

            var now = Intersect(newStart, newEnd, BlockStartOffset(index), BlockEndOffset(index));
            (int, int)? was = previous is not null && previous.TryGetValue(id, out var prev) ? prev : null;

            if (now != (0, 0))
            {
                if (was != now)
                    InvalidateSelectionOverlay(presenter);
                _selectionPainted[id] = now;
            }
            else if (was is not null)
            {
                InvalidateSelectionOverlay(presenter); // the overlay cleared
            }
        }

        Updated?.Invoke();
    }

    /// <summary>
    /// Re-rasters a block's selection overlay: a <see cref="LeafBlockPresenter"/> routes through its
    /// selection-overlay hook (so a table forwards to its per-row child boundaries, which draw the cells);
    /// any other element re-rasters its own zone. Keeps the two-zone gate — a caret crossing a boundary
    /// still re-rasters exactly the block(s) whose intersection changed.
    /// </summary>
    private static void InvalidateSelectionOverlay(UIElement presenter)
    {
        if (presenter is LeafBlockPresenter leaf)
            leaf.InvalidateSelectionOverlay();
        else
            presenter.InvalidateVisual();
    }

    private static (int Start, int End) Intersect(int selStart, int selEnd, int blockStart, int blockEnd)
    {
        if (selStart < 0)
            return (0, 0);

        int from = Math.Max(selStart, blockStart);
        int to = Math.Min(selEnd, blockEnd);
        return to > from ? (from - blockStart, to - blockStart) : (0, 0);
    }

    // ───────────────────────────── mapping helpers ─────────────────────────────

    private (int BlockIndex, ICaretMap Map, int Rel) LocateCaret()
    {
        // Defensive re-validation: the owner's HeightsChanged republish observes the caret
        // DURING a splice's notification chain — after the buffer moved, before this caret's own
        // epilogue installs the post-edit state — so the live position may name a line the splice
        // just removed. Snap for the read; the mutation's epilogue re-publishes the real state
        // within the same frame. (For positions that are already valid, Snap is the identity —
        // every landing this class produces is cluster-snapped.)
        var position = Snap(_position);
        int blockIndex = Blocks.IndexOfLine(position.Line);
        var map = GetMap(blockIndex);
        int rel = _buffer.GetOffset(position) - BlockStartOffset(blockIndex);
        return (blockIndex, map, rel);
    }

    private ICaretMap GetMap(int blockIndex) => _host.GetCaretMap(blockIndex);

    private TextPosition PositionOfBlockRel(int blockIndex, int rel)
        => _buffer.GetPosition(BlockStartOffset(blockIndex) + rel);

    private int BlockStartOffset(int blockIndex)
        => _buffer.GetOffset(new TextPosition(Blocks.GetStartLine(blockIndex), 0));

    private int BlockEndOffset(int blockIndex)
    {
        int endLine = Blocks.GetStartLine(blockIndex) + Blocks[blockIndex].LineCount;
        return endLine >= _buffer.LineCount ? _buffer.Length : _buffer.GetOffset(new TextPosition(endLine, 0));
    }

    private (int Start, int End) CurrentAbsoluteSelection()
    {
        if (_anchor is not { } anchor || anchor == _position)
            return (-1, -1);

        int a = _buffer.GetOffset(anchor);
        int p = _buffer.GetOffset(_position);
        return a <= p ? (a, p) : (p, a);
    }

    private (TextPosition Start, TextPosition End) NormalizedSelection()
    {
        if (_anchor is not { } anchor)
            return (_position, _position);

        return anchor <= _position ? (anchor, _position) : (_position, anchor);
    }

    private TextPosition Snap(TextPosition pos)
    {
        int line = Math.Clamp(pos.Line, 0, _buffer.LineCount - 1);
        string text = _buffer.GetLine(line).Text;
        return new(line, GraphemeLayout.Build(text).PinToBoundary(Math.Clamp(pos.Col, 0, text.Length)));
    }
}
