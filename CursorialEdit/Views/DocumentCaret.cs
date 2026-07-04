using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

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
/// grapheme-cluster-snapped (<see cref="CaretNavigator"/> produces all landings — the WP4 fuzz
/// invariant). The goal column is stored in <b>cells</b> and re-applied per landing row
/// (§3.1 [EDGE]); it survives a vertical run and is forgotten by any non-vertical operation,
/// mirroring the framework <c>TextBox</c>'s sticky <c>_desiredColumn</c>. End-of-row
/// <b>affinity</b> disambiguates a caret col sitting exactly on a soft-wrap boundary (one source
/// position, two visual positions): End and vertical landings keep the earlier row's visual end,
/// Right/Home/typing take the next row's start — <see cref="WrappedLine"/>'s probed contract.
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
    private readonly BlockViewBridge _bridge;
    private readonly IContentRowMap _rows;

    private TextPosition _position;
    private TextPosition? _anchor;

    /// <summary>The sticky goal column in cells; −1 when no vertical run is in progress.</summary>
    private int _goalCell = -1;

    /// <summary>Whether the caret col, when it sits exactly on a soft-wrap boundary, renders at the earlier row's end.</summary>
    private bool _endAffinity;

    /// <summary>The absolute selection range painted by the last repaint pass; (−1, −1) when none.</summary>
    private int _paintedSelStart = -1;
    private int _paintedSelEnd = -1;

    /// <summary>Creates the caret at the document origin.</summary>
    public DocumentCaret(EditController controller, BlockViewBridge bridge, IContentRowMap rows)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(rows);

        _controller = controller;
        _buffer = controller.Buffer;
        _bridge = bridge;
        _rows = rows;
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

    private BlockList Blocks => _bridge.Blocks;

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
        return (_rows.BlockTopRow(blockIndex) + row, cell);
    }

    // ───────────────────────────── horizontal motion ─────────────────────────────

    /// <summary>Caret Left: the previous cluster boundary, crossing to the previous line's end at col 0.</summary>
    public void MoveLeft(bool extend)
    {
        var pos = _position;
        TextPosition target;
        if (pos.Col > 0)
            target = new(pos.Line, CaretNavigator.PrevCluster(_buffer.GetLine(pos.Line).Text, pos.Col));
        else if (pos.Line > 0)
            target = new(pos.Line - 1, _buffer.GetLine(pos.Line - 1).Text.Length);
        else
            target = pos;

        MoveTo(target, extend, endAffinity: false);
    }

    /// <summary>Caret Right: the next cluster boundary, crossing to the next line's start at the line end.</summary>
    public void MoveRight(bool extend)
    {
        var pos = _position;
        string text = _buffer.GetLine(pos.Line).Text;
        TextPosition target;
        if (pos.Col < text.Length)
            target = new(pos.Line, CaretNavigator.NextCluster(text, pos.Col));
        else if (pos.Line < _buffer.LineCount - 1)
            target = new(pos.Line + 1, 0);
        else
            target = pos;

        MoveTo(target, extend, endAffinity: false);
    }

    /// <summary>
    /// Ctrl+Right — the document-scoped composition of the probed <c>TextBox</c> word rule
    /// (whitespace-delimited; land at the <b>end</b> of the word run): skip a whitespace run
    /// (terminators are whitespace, so the skip crosses lines), then a word run (which never
    /// crosses a terminator), pinned to a cluster boundary.
    /// </summary>
    public void MoveWordRight(bool extend)
    {
        int line = _position.Line;
        int col = _position.Col;

        while (true)
        {
            string text = _buffer.GetLine(line).Text;
            while (col < text.Length && char.IsWhiteSpace(text[col]))
                col++;

            if (col >= text.Length && line < _buffer.LineCount - 1)
            {
                line++;
                col = 0;
                continue;
            }

            break;
        }

        string landingText = _buffer.GetLine(line).Text;
        while (col < landingText.Length && !char.IsWhiteSpace(landingText[col]))
            col++;

        MoveTo(new(line, CaretNavigator.SnapToCluster(landingText, col)), extend, endAffinity: false);
    }

    /// <summary>Ctrl+Left — the mirror walk: skip whitespace backward (crossing line starts), then the word run backward.</summary>
    public void MoveWordLeft(bool extend)
    {
        int line = _position.Line;
        int col = _position.Col;

        while (true)
        {
            string text = _buffer.GetLine(line).Text;
            while (col > 0 && char.IsWhiteSpace(text[col - 1]))
                col--;

            if (col == 0 && line > 0)
            {
                line--;
                col = _buffer.GetLine(line).Text.Length;
                continue;
            }

            break;
        }

        string landingText = _buffer.GetLine(line).Text;
        while (col > 0 && !char.IsWhiteSpace(landingText[col - 1]))
            col--;

        MoveTo(new(line, CaretNavigator.SnapToCluster(landingText, col)), extend, endAffinity: false);
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
        int goal = _goalCell >= 0 ? _goalCell : cell;
        int docRow = _rows.BlockTopRow(blockIndex) + rowInBlock;
        int targetRow = Math.Clamp(docRow + deltaRows, 0, totalRows - 1);

        int targetBlock = _rows.BlockIndexOfRow(targetRow);
        var targetMap = GetMap(targetBlock);
        int targetRowInBlock = Math.Clamp(targetRow - _rows.BlockTopRow(targetBlock), 0, targetMap.RowCount - 1);
        int landingRel = targetMap.OffsetAt(targetRowInBlock, goal);
        bool affinity = targetMap.Locate(landingRel).Row != targetRowInBlock;

        MoveTo(PositionOfBlockRel(targetBlock, landingRel), extend, affinity, goal);
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
        var run = map.RunsForRow(row)[0];
        int endRel = run.SrcStart + run.SrcLen;
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

        SetState(
            new(pos.Line, CaretNavigator.SnapToCluster(text, end)),
            new TextPosition(pos.Line, CaretNavigator.SnapToCluster(text, start)),
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

        var text = map.RowText(row);
        int before = CaretNavigator.ColAtOrBeforeCell(text, cell);
        int after = CaretNavigator.NextCluster(text, before);
        int chosen = after == before || cell - CaretNavigator.CellOfCol(text, before) <= CaretNavigator.CellOfCol(text, after) - cell
            ? before
            : after;

        int rel = map.RunsForRow(row)[0].SrcStart + chosen;
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

    /// <summary>Printable input: inserts at the caret, or replaces the selection (<see cref="EditKind.Typing"/>).</summary>
    public void InsertText(string text) => ReplaceSelectionOrInsert(text, EditKind.Typing);

    /// <summary>Enter: a line break — always its own undo group (<see cref="EditKind.Newline"/>, §3.3).</summary>
    public void InsertNewline() => ReplaceSelectionOrInsert("\n", EditKind.Newline);

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
        if (HasSelection)
        {
            ReplaceSelectionOrInsert(string.Empty, EditKind.Typing);
            return;
        }

        var pos = _position;
        if (pos.Col > 0)
        {
            string text = _buffer.GetLine(pos.Line).Text;
            int prev = CaretNavigator.PrevCluster(text, pos.Col);
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
        if (HasSelection)
        {
            ReplaceSelectionOrInsert(string.Empty, EditKind.Typing);
            return;
        }

        var pos = _position;
        var line = _buffer.GetLine(pos.Line);
        if (pos.Col < line.Text.Length)
        {
            int next = CaretNavigator.NextCluster(line.Text, pos.Col);
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

        if (text.Length > 0)
            ReplaceSelectionOrInsert(text, EditKind.Paste);
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
            else if (c is not ('\r' or '\n'))
                continue;

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
        var (newStart, newEnd) = CurrentAbsoluteSelection();

        foreach (var (id, presenter) in _bridge.RealizedPresenters)
        {
            int index = Blocks.IndexOf(id);
            if (index < 0)
                continue; // a just-removed block awaiting the panel's teardown sweep

            int blockStart = BlockStartOffset(index);
            int blockEnd = BlockEndOffset(index);
            if (Intersect(_paintedSelStart, _paintedSelEnd, blockStart, blockEnd)
                != Intersect(newStart, newEnd, blockStart, blockEnd))
            {
                presenter.InvalidateVisual();
            }
        }

        _paintedSelStart = newStart;
        _paintedSelEnd = newEnd;
        Updated?.Invoke();
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

    private (int BlockIndex, BlockRunMap Map, int Rel) LocateCaret()
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

    private BlockRunMap GetMap(int blockIndex) => _bridge.GetRunMap(Blocks[blockIndex].Id, _bridge.WrapWidth);

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
        return new(line, CaretNavigator.SnapToCluster(text, Math.Clamp(pos.Col, 0, text.Length)));
    }
}
