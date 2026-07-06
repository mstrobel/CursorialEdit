using System.Text;

using Cursorial.Rendering.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Document.Editing;

/// <summary>
/// The table cell-editing controller (M3.WP4, architecture Decision 11 / spec §5.3–§5.4): interprets an
/// editing or navigation intent that arrives while the caret is inside a <see cref="BlockKind.Table"/>
/// block into an ordinary buffer <see cref="Edit"/> (or a pure caret move), so cell edits funnel through
/// the one mutation splice like everything else. It is a <b>planner</b> — it reads the buffer and the
/// live <see cref="TableModel"/> and returns a <see cref="TableCommand"/> the caret owner applies through
/// <see cref="EditController"/>; it never mutates the buffer itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cell focus is derived, never stored (Decision 4).</b> Every operation locates the caret's cell by
/// mapping its block-relative source offset through <see cref="TableModel.CellOfOffset"/>. Because a
/// re-adopted table keeps its <see cref="BlockId"/>, the caret's source anchor survives the reparse and
/// re-lands in the corresponding (row, col) through the freshly built model — so undo, which restores the
/// pre-edit caret offset, restores the cell focus for free.
/// </para>
/// <para>
/// <b>Pipe safety (spec §5.3, risk d).</b> A typed or pasted <c>|</c> is escaped to <c>\|</c> so it never
/// splits the cell; a pasted newline becomes a space so a multi-line payload never breaks the row. GFM
/// cells are single-line, so <see cref="Enter"/> commits downward rather than inserting a break — a
/// literal line break is the explicit <see cref="InsertCellBreak"/> (<c>&lt;br&gt;</c>) command.
/// </para>
/// <para>
/// <b>Undo grouping.</b> Intra-cell typing/backspace/delete are ordinary <see cref="EditKind.Typing"/>
/// splices (they coalesce like prose); paste is one <see cref="EditKind.Paste"/> group; the structural-ish
/// cell ops (Tab-appends-row, clear cell, cell break) are one sealed <see cref="EditKind.Structural"/>
/// group each. Undo restores the recorded pre-op caret, hence the cell focus.
/// </para>
/// </remarks>
public sealed class TableEditingController
{
    private readonly IDocumentBuffer _buffer;

    /// <summary>Creates the planner over <paramref name="buffer"/> (the same buffer <see cref="EditController"/> mutates).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public TableEditingController(IDocumentBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    // ───────────────────────────── intra-cell editing ─────────────────────────────

    /// <summary>Types <paramref name="text"/> into the caret's cell (pipes escaped; an empty cell is padded to <c>│ text │</c>).</summary>
    public TableCommand Type(TableModel model, int blockStart, TextPosition caret, string text)
        => Insert(model, blockStart, caret, text, EditKind.Typing, collapseBreaks: false);

    /// <summary>Pastes <paramref name="text"/> into the caret's cell as one undo unit: newlines → spaces, pipes escaped (spec §3.4 / §5.3).</summary>
    public TableCommand Paste(TableModel model, int blockStart, TextPosition caret, string text)
        => Insert(model, blockStart, caret, text, EditKind.Paste, collapseBreaks: true);

    private TableCommand Insert(TableModel model, int blockStart, TextPosition caret, string text, EditKind kind, bool collapseBreaks)
    {
        if (string.IsNullOrEmpty(text))
            return TableCommand.NoOperation;

        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        if (model.IsCellEmpty(cell.Row, cell.Column))
        {
            // Pad the empty inter-pipe whitespace region to `│ text │` (the region is whitespace-only —
            // Markdig classified the cell empty — so scanning it is escape-safe). The caret lands after the
            // typed content, before the trailing pad, ready for more typing. The leading pad resets the
            // backslash parity, so the escaping starts from a clean (even) run.
            var pos = _buffer.GetPosition(caretOffset);
            string line = _buffer.GetLine(pos.Line).Text;
            string sanitized = Sanitize(text, collapseBreaks, precedingBackslashes: 0);
            var (from, to) = WhitespaceRegion(line, pos.Col);
            string removed = line.Substring(from, to - from);
            var editStart = new TextPosition(pos.Line, from);
            var target = new TextPosition(pos.Line, from + 1 + sanitized.Length); // after "␠text"
            return TableCommand.Splice(new Edit(editStart, removed, " " + sanitized + " "), kind, target);
        }

        // Escape a typed/pasted '|' against the backslashes ALREADY before the caret (bug 8): after a cell
        // ending in an odd '\' run (e.g. `C:\`) a bare '|' is already escaped, so adding another '\' would
        // make `\\|` — an escaped backslash + a bare separator pipe that splits the cell.
        string cellLine = _buffer.GetLine(caret.Line).Text;
        string sane = Sanitize(text, collapseBreaks, BackslashRunBefore(cellLine, caret.Col));

        // Sanitised text carries no line break, so the landing stays on the caret's line (computed
        // arithmetically — caretOffset + length may lie beyond the pre-edit buffer for a large paste).
        var end = new TextPosition(caret.Line, caret.Col + sane.Length);
        return TableCommand.Splice(new Edit(caret, string.Empty, sane), kind, end);
    }

    /// <summary>
    /// Replaces a selection whose caret cell is in a table (M3.WP4 bug 3): typing / backspace / delete /
    /// paste over a selection route here. The replaced range is <b>clamped to the caret cell's content</b>
    /// so a selection spanning a cell boundary never deletes the separating <c>|</c> (whole-cell / cell-rect
    /// selection is WP8); the inserted text is pipe-escaped like an ordinary in-cell edit.
    /// </summary>
    public TableCommand Replace(TableModel model, int blockStart, TextPosition selStart, TextPosition selEnd, string text, EditKind kind, bool collapseBreaks)
    {
        int startOffset = _buffer.GetOffset(selStart);
        int endOffset = _buffer.GetOffset(selEnd);
        if (endOffset < startOffset)
            (startOffset, endOffset) = (endOffset, startOffset);

        if (model.CellOfOffset(startOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        // An empty cell has nothing selectable to replace — fall back to the padding insert at the anchor.
        if (model.IsCellEmpty(cell.Row, cell.Column))
            return Insert(model, blockStart, _buffer.GetPosition(startOffset), text, kind, collapseBreaks);

        var (contentStart, contentEnd) = model.CellContentRange(cell.Row, cell.Column);
        int clampStart = blockStart + Math.Clamp(startOffset - blockStart, contentStart, contentEnd);
        int clampEnd = blockStart + Math.Clamp(endOffset - blockStart, contentStart, contentEnd);

        var startPos = _buffer.GetPosition(clampStart);
        string line = _buffer.GetLine(startPos.Line).Text;
        string sanitized = Sanitize(text ?? string.Empty, collapseBreaks, BackslashRunBefore(line, startPos.Col));
        string removed = clampEnd > clampStart ? _buffer.GetTextAtOffset(clampStart, clampEnd - clampStart) : string.Empty;
        if (removed.Length == 0 && sanitized.Length == 0)
            return TableCommand.NoOperation;

        var target = new TextPosition(startPos.Line, startPos.Col + sanitized.Length);
        return TableCommand.Splice(new Edit(startPos, removed, sanitized), kind, target);
    }

    /// <summary>Backspace within the caret's cell — deletes the previous grapheme cluster, bounded to the cell (never merges cells or deletes a pipe).</summary>
    public TableCommand Backspace(TableModel model, int blockStart, TextPosition caret)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        var (contentStart, _) = model.CellContentRange(cell.Row, cell.Column);
        int caretRel = caretOffset - blockStart;
        if (caretRel <= contentStart)
            return TableCommand.NoOperation; // at (or before) the cell's content start — nothing to delete in-cell

        string line = _buffer.GetLine(caret.Line).Text;
        int prev = GraphemeLayout.Build(line).PrevBoundary(caret.Col);
        if (prev >= caret.Col)
            return TableCommand.NoOperation;

        // Escape-aware (bug 7): an escaped `\|` is ONE atomic unit — deleting the '|' alone would expose a
        // bare separator pipe. When the deleted cluster is a '|' with an odd backslash run before it (so it
        // IS escaped), take the escaping '\' too, bounded to the cell content.
        int contentStartCol = contentStart - caretRel + caret.Col;
        if (caret.Col - prev == 1 && line[prev] == '|'
            && BackslashRunBefore(line, prev) % 2 == 1 && prev - 1 >= contentStartCol)
            prev -= 1;

        var start = new TextPosition(caret.Line, prev);
        return TableCommand.Splice(new Edit(start, line[prev..caret.Col], string.Empty), EditKind.Typing, start);
    }

    /// <summary>Forward Delete within the caret's cell — deletes the next grapheme cluster, bounded to the cell.</summary>
    public TableCommand DeleteForward(TableModel model, int blockStart, TextPosition caret)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        var (_, contentEnd) = model.CellContentRange(cell.Row, cell.Column);
        int caretRel = caretOffset - blockStart;
        if (caretRel >= contentEnd)
            return TableCommand.NoOperation; // at (or past) the cell's content end — nothing to delete in-cell

        string line = _buffer.GetLine(caret.Line).Text;
        int next = GraphemeLayout.Build(line).NextBoundary(caret.Col);
        if (next <= caret.Col)
            return TableCommand.NoOperation;

        // Escape-aware (bug 7): forward-deleting the '\' of a `\|` escape would expose a bare separator pipe.
        // When the deleted cluster is a '\' with an even backslash run before it (so it escapes the following
        // '|'), take the '|' too, bounded to the cell content.
        int contentEndCol = contentEnd - caretRel + caret.Col;
        if (next - caret.Col == 1 && line[caret.Col] == '\\'
            && caret.Col + 1 < line.Length && line[caret.Col + 1] == '|'
            && BackslashRunBefore(line, caret.Col) % 2 == 0 && next + 1 <= contentEndCol)
            next += 1;

        return TableCommand.Splice(new Edit(caret, line[caret.Col..next], string.Empty), EditKind.Typing, caret);
    }

    // ───────────────────────────── navigation ─────────────────────────────

    /// <summary>Tab / Shift+Tab: next / previous cell, wrapping across rows; Tab in the last cell of the last row appends a new empty row and enters its first cell (spec §5.3 [EDGE]).</summary>
    public TableCommand Tab(TableModel model, int blockStart, TextPosition caret, bool shift)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        int columns = model.ColumnCount;
        int rows = model.RowCount;

        if (!shift)
        {
            if (cell.Column + 1 < columns)
                return EnterCell(model, blockStart, cell.Row, cell.Column + 1);
            if (cell.Row + 1 < rows)
                return EnterCell(model, blockStart, cell.Row + 1, 0);
            return AppendRow(model, blockStart); // last cell of last row → grow the table
        }

        if (cell.Column > 0)
            return EnterCell(model, blockStart, cell.Row, cell.Column - 1);
        if (cell.Row > 0)
            return EnterCell(model, blockStart, cell.Row - 1, columns - 1);
        return EnterCell(model, blockStart, 0, 0); // first cell — stay put
    }

    /// <summary>Enter: GFM cells are single-line, so this commits and moves to the cell below (same column), or exits below the table on the last row (spec §5.4 [EDGE]).</summary>
    public TableCommand Enter(TableModel model, int blockStart, TextPosition caret)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        if (cell.Row + 1 < model.RowCount)
            return EnterCell(model, blockStart, cell.Row + 1, cell.Column);

        return TableCommand.ExitBelow;
    }

    private TableCommand EnterCell(TableModel model, int blockStart, int row, int column)
    {
        var target = _buffer.GetPosition(blockStart + model.CellEntryOffset(row, column));
        return TableCommand.Navigate(target);
    }

    private TableCommand AppendRow(TableModel model, int blockStart)
        // Grow the table below its last row — the same empty-row insert the structural ops use, so the row
        // shape and the document's line-ending convention are honoured in one place (was a hardcoded "\n").
        => InsertRowAt(AbsLineOfRow(model, blockStart, model.RowCount - 1) + 1, BuildEmptyRow(model.ColumnCount), TableEndingText(model, blockStart));

    // ───────────────────────────── cell commands ─────────────────────────────

    /// <summary>Clears the caret's cell (empties its content, keeps the structure) — spec §5.3 "Clear cell". One sealed undo group.</summary>
    public TableCommand ClearCell(TableModel model, int blockStart, TextPosition caret)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        var (contentStart, contentEnd) = model.CellContentRange(cell.Row, cell.Column);
        if (contentEnd <= contentStart)
            return TableCommand.NoOperation; // already empty

        var start = _buffer.GetPosition(blockStart + contentStart);
        string removed = _buffer.GetTextAtOffset(blockStart + contentStart, contentEnd - contentStart);
        return TableCommand.Splice(new Edit(start, removed, string.Empty), EditKind.Structural, start, seal: true);
    }

    /// <summary>
    /// Clears (or replaces) every cell of a rectangular whole-cell selection (M3.WP8, spec §5.4) as <b>one</b>
    /// sealed structural splice — a rect <c>Delete</c>/Clear passes <c>text = ""</c> to empty all selected cells;
    /// a type/paste over the rect passes the sanitized text, which lands in the rect's <b>top-left</b> cell while
    /// the rest empty. The splice is a single contiguous edit from the top-left cell's content to the bottom-right
    /// cell's content: every byte between the selected cells (pipes, padding, the delimiter line, terminators, and
    /// any non-selected cells) is copied verbatim, and only each selected cell's <see cref="CellContentRange"/> is
    /// blanked — so GFM structure is preserved exactly and the result re-parses to the same table with the selected
    /// cells emptied. Caret → the top-left cell (after the inserted text). Undo restores the pre-op source + caret.
    /// </summary>
    public TableCommand ReplaceCellRect(TableModel model, int blockStart, CellRect rect, string text, bool collapseBreaks)
    {
        string sanitized = Sanitize(text ?? string.Empty, collapseBreaks, precedingBackslashes: 0);

        // The top-left cell takes the inserted text: a non-empty cell replaces its trimmed content in place (its
        // surrounding padding kept), while an EMPTY cell being typed into pads to "│ text │" — replacing its whole
        // inter-pipe whitespace region — exactly like the intra-cell empty-cell insert (WP4 point 0), so a rect
        // whose top-left is empty renders "| X |", not "|X  |". A pure clear (empty text) blanks it either way.
        var (tlStart, tlEnd) = model.CellContentRange(rect.Row0, rect.Col0);
        string tlText = sanitized;
        bool paddedEmpty = false;
        if (sanitized.Length > 0 && model.IsCellEmpty(rect.Row0, rect.Col0))
        {
            var tlPos = _buffer.GetPosition(blockStart + tlStart);
            var (from, to) = WhitespaceRegion(_buffer.GetLine(tlPos.Line).Text, tlPos.Col);
            int lineRel = tlStart - tlPos.Col; // block-relative offset of the top-left cell's line start
            tlStart = lineRel + from;
            tlEnd = lineRel + to;
            tlText = " " + sanitized + " ";
            paddedEmpty = true;
        }

        // The top-left cell is the earliest selected cell in source order and the bottom-right the latest, so
        // [tlStart, bottom-right content end) is the whole contiguous span covering the rectangle (row-major).
        int spanStart = tlStart;
        int spanEnd = model.CellContentRange(rect.Row1, rect.Col1).End;
        if (spanEnd < spanStart)
            return TableCommand.NoOperation; // defensive — a degenerate/empty rectangle

        string removed = _buffer.GetTextAtOffset(blockStart + spanStart, spanEnd - spanStart);

        // Rebuild the span, copying everything verbatim except each selected cell's content region (blanked; the
        // top-left cell takes tlText over its possibly-padded range). Cursor walks block-relative offsets; indices
        // into `removed` are cursor − spanStart. The ranges are disjoint and source-ordered, so the last cell's end
        // is spanEnd — no trailing copy past the loop is ever reached.
        var sb = new StringBuilder(removed.Length + tlText.Length);
        int cursor = spanStart;
        for (var r = rect.Row0; r <= rect.Row1; r++)
        {
            for (var c = rect.Col0; c <= rect.Col1; c++)
            {
                bool topLeft = r == rect.Row0 && c == rect.Col0;
                var (cs, ce) = topLeft ? (tlStart, tlEnd) : model.CellContentRange(r, c);
                if (cs > cursor)
                    sb.Append(removed, cursor - spanStart, cs - cursor);
                if (topLeft)
                    sb.Append(tlText);
                cursor = Math.Max(cursor, ce);
            }
        }

        string inserted = sb.ToString();
        if (string.Equals(inserted, removed, StringComparison.Ordinal))
            return TableCommand.NoOperation; // every selected cell already empty (and no text to insert)

        var editStart = _buffer.GetPosition(blockStart + spanStart);
        // Land after the inserted text (past the leading pad for a padded empty cell), computed ARITHMETICALLY on
        // editStart's line: tlText carries no line break and is spliced in at editStart, so the landing stays on
        // that line. Resolving it via GetPosition on the PRE-edit buffer would walk through `removed` (which spans
        // newlines for a multi-row rect) to the wrong line — and past the buffer end for a large paste, which
        // GetPosition throws on (the same arithmetic-landing reason Insert/Replace already state).
        var target = new TextPosition(editStart.Line, editStart.Col + (paddedEmpty ? 1 : 0) + sanitized.Length);
        return TableCommand.Splice(new Edit(editStart, removed, inserted), EditKind.Structural, target, seal: true);
    }

    /// <summary>
    /// The markdown <b>sub-table</b> a rectangular cell selection copies as (M3.WP8, spec §5.4): the selected
    /// cells emitted as a valid GFM table — the top selected row as the header, then a synthesized delimiter row
    /// carrying the <b>selected columns'</b> alignment, then the remaining selected rows as body. When the rect
    /// does <b>not</b> include the model's header row this still yields a valid GFM table (the top selected body
    /// row becomes the sub-table's header, delimiter synthesized from alignment). Cell text is the trimmed content
    /// (escaped/backtick-guarded pipes preserved verbatim), so the emission re-parses to exactly the selected cells.
    /// Rows are joined with <paramref name="lineEnding"/> and carry no trailing terminator (it pastes inline).
    /// </summary>
    public static string SubTableMarkdown(TableModel model, CellRect rect, string lineEnding = "\n")
    {
        ArgumentNullException.ThrowIfNull(model);

        var lines = new List<string>(rect.RowSpan + 1);
        for (var r = rect.Row0; r <= rect.Row1; r++)
        {
            var cells = new string[rect.ColumnSpan];
            for (var c = rect.Col0; c <= rect.Col1; c++)
                cells[c - rect.Col0] = model.CellContent(r, c);
            lines.Add(BuildDataRow(cells));

            if (r == rect.Row0)
            {
                var align = new ColumnAlignment[rect.ColumnSpan];
                for (var c = rect.Col0; c <= rect.Col1; c++)
                    align[c - rect.Col0] = model.Alignment(c);
                lines.Add(BuildDelimiterRow(align));
            }
        }

        return string.Join(lineEnding, lines);
    }

    /// <summary>Inserts a literal <c>&lt;br&gt;</c> cell break at the caret (spec §5.4 — a command, not Enter). One sealed undo group.</summary>
    public TableCommand InsertCellBreak(TableModel model, int blockStart, TextPosition caret)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is null)
            return TableCommand.NoOperation;

        const string Break = "<br>";
        var target = new TextPosition(caret.Line, caret.Col + Break.Length); // <br> carries no line break
        return TableCommand.Splice(new Edit(caret, string.Empty, Break), EditKind.Structural, target, seal: true);
    }

    // ───────────────────────────── structural operations (M3.WP7, spec §5.3) ─────────────────────────────
    //
    // Each op is ONE GFM-valid source splice through EditController.Apply as ONE sealed EditKind.Structural
    // undo group (so undo restores the exact pre-op source + caret), lands the caret in a sensible cell, and
    // re-parses (Markdig) to the intended table. Positions are derived from the TableModel (row source lines,
    // cell spans, alignment) — never a bespoke pipe scanner — and the delimiter row is kept structurally
    // consistent with the data columns (one marker per column) by rebuilding it from the per-column alignment.

    /// <summary>
    /// Inserts a new empty row above (<paramref name="below"/> = false) or below the caret's row (spec §5.3).
    /// <b>[EDGE]</b> A body row cannot precede the header/delimiter in GFM, so on the header row the new row is
    /// inserted as the first <i>body</i> row (below the delimiter) whether "above" or "below" was requested.
    /// Caret → the new row's first cell.
    /// </summary>
    public TableCommand InsertRow(TableModel model, int blockStart, TextPosition caret, bool below)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        int blockLine = BlockLine(blockStart);
        int insertLineAbs = cell.Row == 0
            ? blockLine + model.RowSourceLine(0) + 2         // header: land as the first body row (after the delimiter)
            : below
                ? blockLine + model.RowSourceLine(cell.Row) + 1
                : blockLine + model.RowSourceLine(cell.Row);

        return InsertRowAt(insertLineAbs, BuildEmptyRow(model.ColumnCount), TableEndingText(model, blockStart));
    }

    /// <summary>Inserts an empty column left (<paramref name="right"/> = false) or right of the caret's column into EVERY row (header, delimiter, body); the new column's delimiter marker defaults to <c>---</c> (renders left). Caret → the new column's cell in the current row.</summary>
    public TableCommand InsertColumn(TableModel model, int blockStart, TextPosition caret, bool right)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        int oldColumns = model.ColumnCount;
        int at = right ? cell.Column + 1 : cell.Column;

        var rows = new List<string[]>(model.RowCount);
        for (var r = 0; r < model.RowCount; r++)
        {
            var arr = new string[oldColumns + 1];
            for (var c = 0; c < oldColumns; c++)
                arr[c < at ? c : c + 1] = model.CellContent(r, c);
            arr[at] = string.Empty;
            rows.Add(arr);
        }

        var align = new ColumnAlignment[oldColumns + 1];
        for (var c = 0; c < oldColumns; c++)
            align[c < at ? c : c + 1] = model.Alignment(c);
        align[at] = ColumnAlignment.None; // "---" — default (left-rendered) alignment

        return RebuildTable(model, blockStart, rows, align, cell.Row, at);
    }

    /// <summary>
    /// Deletes the caret's row (spec §5.3). <b>[EDGE]</b> Deleting the header row promotes the next body row to
    /// header (a GFM table must lead with header + delimiter); deleting the only row deletes the whole table.
    /// Caret → the same column in the adjacent row.
    /// </summary>
    public TableCommand DeleteRow(TableModel model, int blockStart, TextPosition caret)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        if (model.RowCount <= 1)
            return DeleteTable(model, blockStart); // the header is the only row — nothing valid remains

        if (cell.Row == 0)
            return PromoteHeader(model, blockStart, cell.Column); // [EDGE] promote row 1 to header

        int abs = AbsLineOfRow(model, blockStart, cell.Row);
        var line = _buffer.GetLine(abs);

        // Remove the row's whole physical line. When it is the buffer's last line (no terminator of its own) take
        // the PRECEDING terminator instead, so no dangling empty line is left behind.
        int removeStart, removeEnd;
        if (line.EndingLength > 0)
        {
            removeStart = LineStart(abs);
            removeEnd = removeStart + line.TotalLength;
        }
        else
        {
            removeStart = LineStart(abs) - _buffer.GetLine(abs - 1).EndingLength;
            removeEnd = LineStart(abs) + line.Text.Length;
        }

        string removed = _buffer.GetTextAtOffset(removeStart, removeEnd - removeStart);

        // Caret → the same column in the adjacent row: the row below (which shifts up one line after the delete)
        // when one exists, else the row above (unaffected by a delete beneath it).
        int adjacent = cell.Row + 1 < model.RowCount ? cell.Row + 1 : cell.Row - 1;
        var entry = _buffer.GetPosition(blockStart + model.CellEntryOffset(adjacent, cell.Column));
        var target = adjacent > cell.Row ? new TextPosition(entry.Line - 1, entry.Col) : entry;

        return TableCommand.Splice(new Edit(_buffer.GetPosition(removeStart), removed, string.Empty), EditKind.Structural, target, seal: true);
    }

    /// <summary>Deletes the caret's column from EVERY row (incl. the delimiter marker). <b>[EDGE]</b> Deleting the last (only) column deletes the whole table. Caret → the adjacent column (or out of the table when it was deleted).</summary>
    public TableCommand DeleteColumn(TableModel model, int blockStart, TextPosition caret)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        int oldColumns = model.ColumnCount;
        if (oldColumns <= 1)
            return DeleteTable(model, blockStart); // [EDGE] the last column — the whole table goes

        int del = cell.Column;
        var rows = new List<string[]>(model.RowCount);
        for (var r = 0; r < model.RowCount; r++)
        {
            var arr = new string[oldColumns - 1];
            for (var c = 0; c < oldColumns; c++)
            {
                if (c == del)
                    continue;
                arr[c < del ? c : c - 1] = model.CellContent(r, c);
            }

            rows.Add(arr);
        }

        var align = new ColumnAlignment[oldColumns - 1];
        for (var c = 0; c < oldColumns; c++)
        {
            if (c == del)
                continue;
            align[c < del ? c : c - 1] = model.Alignment(c);
        }

        int caretColumn = del < oldColumns - 1 ? del : del - 1; // the column now sitting where del was, else its predecessor
        return RebuildTable(model, blockStart, rows, align, cell.Row, caretColumn);
    }

    /// <summary>Moves the caret's row up (<paramref name="down"/> = false) or down, swapping it with its neighbour. The header does not move and a body row never crosses the delimiter (moving row 1 up is a no-op). Caret follows the moved row.</summary>
    public TableCommand MoveRow(TableModel model, int blockStart, TextPosition caret, bool down)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        int r = cell.Row;
        int other = down ? r + 1 : r - 1;
        if (r == 0 || other < 1 || other >= model.RowCount)
            return TableCommand.NoOperation; // the header stays put; a body row cannot cross the delimiter or the ends

        int topAbs = AbsLineOfRow(model, blockStart, Math.Min(r, other));   // the upper of the two adjacent body lines
        int bottomAbs = topAbs + 1;                                         // body rows are physically consecutive
        var top = _buffer.GetLine(topAbs);
        var bottom = _buffer.GetLine(bottomAbs);

        // Reorder the two full lines, carrying each row's OWN terminator with its text (byte-exact under CRLF /
        // mixed endings — a positional swap would give a row its neighbour's ending). The one exception is when
        // the bottom line is the buffer's last line (no terminator): promoting it above means the two rows share
        // the single interior terminator (the top row's) and the row that ends up last stays unterminated.
        int start = LineStart(topAbs);
        int end = LineStart(bottomAbs) + bottom.TotalLength; // both full lines (the bottom line's terminator included)
        string removed = _buffer.GetTextAtOffset(start, end - start);
        bool bottomTerminated = bottom.EndingLength > 0;
        string firstEnding = bottomTerminated ? bottom.EndingText : top.EndingText;   // separates the two rows after the swap
        string secondEnding = bottomTerminated ? top.EndingText : bottom.EndingText;  // the region's trailing ending ("" if last)
        string inserted = bottom.Text + firstEnding + top.Text + secondEnding;

        // The moved row's text is unchanged (only relocated by one line), so the caret keeps its column and shifts one line.
        var target = new TextPosition(down ? caret.Line + 1 : caret.Line - 1, caret.Col);
        return TableCommand.Splice(new Edit(_buffer.GetPosition(start), removed, inserted), EditKind.Structural, target, seal: true);
    }

    /// <summary>Moves the caret's column left (<paramref name="right"/> = false) or right, swapping it with its neighbour across EVERY row <b>including the delimiter</b> (so the column's alignment travels with it). Caret follows the moved column.</summary>
    public TableCommand MoveColumn(TableModel model, int blockStart, TextPosition caret, bool right)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        int c = cell.Column;
        int other = right ? c + 1 : c - 1;
        if (other < 0 || other >= model.ColumnCount)
            return TableCommand.NoOperation; // already at the edge

        var rows = new List<string[]>(model.RowCount);
        for (var r = 0; r < model.RowCount; r++)
        {
            var arr = new string[model.ColumnCount];
            for (var col = 0; col < model.ColumnCount; col++)
                arr[col] = model.CellContent(r, col);
            (arr[c], arr[other]) = (arr[other], arr[c]);
            rows.Add(arr);
        }

        var align = new ColumnAlignment[model.ColumnCount];
        for (var col = 0; col < model.ColumnCount; col++)
            align[col] = model.Alignment(col);
        (align[c], align[other]) = (align[other], align[c]); // alignment travels with the column (delimiter stays consistent)

        return RebuildTable(model, blockStart, rows, align, cell.Row, other);
    }

    /// <summary>Sets the caret column's alignment (left/center/right/none), rewriting only the delimiter row's markers; the data rows are untouched, and the alignment round-trips (render → save → reopen). Caret unchanged.</summary>
    public TableCommand SetAlignment(TableModel model, int blockStart, TextPosition caret, ColumnAlignment alignment)
    {
        if (model.CellOfOffset(_buffer.GetOffset(caret) - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        var align = new ColumnAlignment[model.ColumnCount];
        for (var c = 0; c < model.ColumnCount; c++)
            align[c] = model.Alignment(c);
        align[cell.Column] = alignment;

        int delimiterAbs = BlockLine(blockStart) + model.RowSourceLine(0) + 1;
        string removed = _buffer.GetLine(delimiterAbs).Text;
        string inserted = BuildDelimiterRow(align);
        if (string.Equals(removed, inserted, StringComparison.Ordinal))
            return TableCommand.NoOperation; // no change

        // Only the delimiter LINE changes; the caret sits in a data cell whose (line, col) is unaffected.
        return TableCommand.Splice(new Edit(new TextPosition(delimiterAbs, 0), removed, inserted), EditKind.Structural, caret, seal: true);
    }

    /// <summary>Deletes the whole table block's structural source (header, delimiter, and every body row). Caret → where the table was (the line that now occupies its top position).</summary>
    public TableCommand DeleteTable(TableModel model, int blockStart)
    {
        int headerAbs = AbsLineOfRow(model, blockStart, 0);
        int lastAbs = LastPhysicalLine(model, blockStart);

        int removeStart = LineStart(headerAbs);
        int removeEnd = LineStart(lastAbs) + _buffer.GetLine(lastAbs).TotalLength; // include the last row's terminator if any
        string removed = _buffer.GetTextAtOffset(removeStart, removeEnd - removeStart);

        // Land where the table was: the line now at the table's old top position, clamped into the shrunken buffer.
        // The removed physical lines are headerAbs..lastAbs; each contributes a terminator except a terminator-less
        // last buffer line, so the surviving line count follows from the span without rescanning `removed`.
        int removedTerminators = (lastAbs - headerAbs) + (_buffer.GetLine(lastAbs).EndingLength > 0 ? 1 : 0);
        int newLineCount = _buffer.LineCount - removedTerminators;
        var target = new TextPosition(Math.Min(headerAbs, Math.Max(0, newLineCount - 1)), 0);
        return TableCommand.Splice(new Edit(_buffer.GetPosition(removeStart), removed, string.Empty), EditKind.Structural, target, seal: true);
    }

    // ───────────────────────────── structural splice helpers ─────────────────────────────

    /// <summary>Promotes body row 1 to the header (the delete-header [EDGE]): removes the old header line and lifts row 1's text above the delimiter as the new header. Caret → the new header, same column.</summary>
    private TableCommand PromoteHeader(TableModel model, int blockStart, int column)
    {
        int headerAbs = AbsLineOfRow(model, blockStart, 0);
        var header = _buffer.GetLine(headerAbs);
        var delimiter = _buffer.GetLine(headerAbs + 1);
        var firstBody = _buffer.GetLine(headerAbs + 2);

        // Remove all three full lines and rebuild as two, carrying each SURVIVING line's own terminator so an
        // untouched delimiter (or the promoted row) is never rewritten to its neighbour's ending (byte-exact
        // under CRLF / mixed endings). The old header's terminator simply vanishes with the header.
        int removeStart = LineStart(headerAbs);
        int removeEnd = LineStart(headerAbs + 2) + firstBody.TotalLength; // all three full lines
        string removed = _buffer.GetTextAtOffset(removeStart, removeEnd - removeStart);

        // The promoted row keeps its own terminator before the delimiter; if it was the buffer's last line (no
        // terminator) a real separator is needed, so the deleted header's ending is reused. The delimiter keeps
        // its own terminator, except it becomes the (unterminated) last line when the promoted row was last.
        bool promotedTerminated = firstBody.EndingLength > 0;
        string headerEnding = promotedTerminated ? firstBody.EndingText : header.EndingText;
        string delimiterEnding = promotedTerminated ? delimiter.EndingText : firstBody.EndingText;
        string inserted = firstBody.Text + headerEnding + delimiter.Text + delimiterEnding;

        // Row 1's text is unchanged but rises by two physical lines (past the removed header and delimiter gap).
        var entry = _buffer.GetPosition(blockStart + model.CellEntryOffset(1, column));
        var target = new TextPosition(entry.Line - 2, entry.Col);
        return TableCommand.Splice(new Edit(_buffer.GetPosition(removeStart), removed, inserted), EditKind.Structural, target, seal: true);
    }

    /// <summary>
    /// Rebuilds the whole table region (header, delimiter, every body row) from <paramref name="rows"/> and
    /// <paramref name="align"/> as one splice — the shared path for the column ops, which change every line.
    /// The delimiter is rebuilt from the per-column alignment so it always has exactly one marker per data
    /// column. Caret → cell (<paramref name="caretRow"/>, <paramref name="caretColumn"/>) in the new layout.
    /// </summary>
    private TableCommand RebuildTable(TableModel model, int blockStart, List<string[]> rows, ColumnAlignment[] align, int caretRow, int caretColumn)
    {
        int headerAbs = AbsLineOfRow(model, blockStart, 0);
        int lastAbs = LastPhysicalLine(model, blockStart);

        // Physical order: header row, delimiter, then the body rows (all contiguous — a table cannot hold a blank line).
        var physical = new List<string>(model.RowCount + 1) { BuildDataRow(rows[0]), BuildDelimiterRow(align) };
        for (var r = 1; r < model.RowCount; r++)
            physical.Add(BuildDataRow(rows[r]));

        int removeStart = LineStart(headerAbs);
        int removeEnd = LineStart(lastAbs) + _buffer.GetLine(lastAbs).Text.Length; // exclude the last line's terminator
        string removed = _buffer.GetTextAtOffset(removeStart, removeEnd - removeStart);

        var sb = new StringBuilder();
        for (var i = 0; i < physical.Count; i++)
        {
            sb.Append(physical[i]);
            if (i < physical.Count - 1)
                sb.Append(_buffer.GetLine(headerAbs + i).EndingText); // keep each original interior terminator
        }

        var target = new TextPosition(AbsLineOfRow(model, blockStart, caretRow), EntryColumn(rows[caretRow], caretColumn));
        return TableCommand.Splice(new Edit(_buffer.GetPosition(removeStart), removed, sb.ToString()), EditKind.Structural, target, seal: true);
    }

    /// <summary>
    /// Splices <paramref name="newRow"/> in as a fresh physical line at absolute line <paramref name="insertLineAbs"/>
    /// (appending past the last line when needed). The new row's terminator honours the document's line-ending
    /// convention — the line just above the insertion (a table row, always terminated) for an interior insert, else
    /// <paramref name="prevailingEnding"/> — so inserting a row never rewrites a CRLF document to a lone LF.
    /// Caret → the new row's first cell.
    /// </summary>
    private TableCommand InsertRowAt(int insertLineAbs, string newRow, string prevailingEnding)
    {
        if (insertLineAbs < _buffer.LineCount)
        {
            // The line above is a table row whose ending is the local convention; it is never the last buffer line
            // here (insertLineAbs < LineCount), so its ending is a real terminator.
            string ending = insertLineAbs > 0 ? _buffer.GetLine(insertLineAbs - 1).EndingText : prevailingEnding;
            var editStart = new TextPosition(insertLineAbs, 0);
            var target = new TextPosition(insertLineAbs, 1); // just inside the new row's leading pipe (its first cell)
            return TableCommand.Splice(new Edit(editStart, string.Empty, newRow + ending), EditKind.Structural, target, seal: true);
        }

        // Appending past the last line: the current last line has no terminator of its own, so the new row is
        // separated with the table's prevailing ending and itself becomes the (unterminated) last line.
        int lastLine = _buffer.LineCount - 1;
        var last = _buffer.GetLine(lastLine);
        var appendAt = new TextPosition(lastLine, last.Text.Length);
        var appendTarget = new TextPosition(lastLine + 1, 1);
        return TableCommand.Splice(new Edit(appendAt, string.Empty, prevailingEnding + newRow), EditKind.Structural, appendTarget, seal: true);
    }

    /// <summary>The table's prevailing line-ending text — taken from the header row, which is always terminated (the delimiter follows it).</summary>
    private string TableEndingText(TableModel model, int blockStart) => _buffer.GetLine(AbsLineOfRow(model, blockStart, 0)).EndingText;

    private int BlockLine(int blockStart) => _buffer.GetPosition(blockStart).Line;

    private int AbsLineOfRow(TableModel model, int blockStart, int row) => BlockLine(blockStart) + model.RowSourceLine(row);

    /// <summary>
    /// The absolute buffer line of the table's last <b>physical</b> line. With body rows that is the last body
    /// row; for a header-only table (<see cref="TableModel.RowCount"/> == 1) the delimiter row sits physically
    /// <i>below</i> the only model row — the <see cref="Math.Max(int,int)"/> picks it up, so a whole-table
    /// removal / rebuild covers the delimiter instead of orphaning it (adversarial-review fix).
    /// </summary>
    private int LastPhysicalLine(TableModel model, int blockStart)
        => Math.Max(AbsLineOfRow(model, blockStart, model.RowCount - 1), AbsLineOfRow(model, blockStart, 0) + 1);

    private int LineStart(int absLine) => _buffer.GetOffset(new TextPosition(absLine, 0));

    /// <summary>Builds a GFM data row <c>| c0 | c1 | … |</c> from the trimmed cell contents (escaped/backtick-guarded pipes preserved verbatim, empty cells rendered as a bare inter-pipe gap).</summary>
    private static string BuildDataRow(IReadOnlyList<string> cells)
    {
        var sb = new StringBuilder("|");
        foreach (var cell in cells)
            sb.Append(' ').Append(cell).Append(" |");
        return sb.ToString();
    }

    /// <summary>Builds an empty GFM row of <paramref name="columns"/> blank cells.</summary>
    private static string BuildEmptyRow(int columns)
    {
        var cells = new string[Math.Max(1, columns)];
        Array.Fill(cells, string.Empty);
        return BuildDataRow(cells);
    }

    /// <summary>Builds the GFM delimiter row from the per-column alignment — one <c>---</c>/<c>:---</c>/<c>:---:</c>/<c>---:</c> marker per column (structurally consistent with the data columns).</summary>
    private static string BuildDelimiterRow(ColumnAlignment[] align)
    {
        var sb = new StringBuilder("|");
        foreach (var a in align)
            sb.Append(' ').Append(Marker(a)).Append(" |");
        return sb.ToString();
    }

    private static string Marker(ColumnAlignment align) => align switch
    {
        ColumnAlignment.Left => ":---",
        ColumnAlignment.Center => ":---:",
        ColumnAlignment.Right => "---:",
        _ => "---",
    };

    /// <summary>The column within a freshly <see cref="BuildDataRow"/>-built line where a caret entering cell <paramref name="targetColumn"/> lands: just inside the opening pipe for an empty cell, or on the first content character otherwise.</summary>
    private static int EntryColumn(IReadOnlyList<string> cells, int targetColumn)
    {
        int column = 1; // past the leading '|', on cell 0's leading pad space
        for (var j = 0; j < targetColumn; j++)
            column += 1 + cells[j].Length + 2; // " " + content + " |"
        return column + (cells[targetColumn].Length > 0 ? 1 : 0);
    }

    // ───────────────────────────── text hygiene ─────────────────────────────

    /// <summary>
    /// Escapes a value for a single GFM cell: a <c>|</c> is escaped to <c>\|</c> <b>only when it is not
    /// already escaped by the backslashes before it</b> (bug 8 — backslash parity, seeded by
    /// <paramref name="precedingBackslashes"/> in the buffer and carried through the value), and — for paste
    /// (<paramref name="collapseBreaks"/>) — every line break becomes a single space so a multi-line payload
    /// stays one row.
    /// </summary>
    private static string Sanitize(string text, bool collapseBreaks, int precedingBackslashes)
    {
        bool hasPipe = text.Contains('|');
        bool hasBreak = collapseBreaks && (text.Contains('\n') || text.Contains('\r'));
        if (!hasPipe && !hasBreak)
            return text; // no pipe ⇒ parity is irrelevant, and no break to collapse

        var sb = new StringBuilder(text.Length + 8);
        int backslashes = precedingBackslashes; // consecutive '\' immediately before the current output point
        for (var i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '|')
            {
                if (backslashes % 2 == 0)
                    sb.Append('\\'); // an even '\' run leaves the '|' unescaped — add the escape
                sb.Append('|');
                backslashes = 0;
            }
            else if (collapseBreaks && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++; // CRLF → one space
                sb.Append(' ');
                backslashes = 0;
            }
            else
            {
                sb.Append(c);
                backslashes = c == '\\' ? backslashes + 1 : 0;
            }
        }

        return sb.ToString();
    }

    /// <summary>The number of consecutive backslashes immediately before <paramref name="col"/> in <paramref name="line"/> — the escape-parity input for pipe escaping and escaped-pipe deletion.</summary>
    private static int BackslashRunBefore(string line, int col)
    {
        int count = 0;
        int i = Math.Clamp(col, 0, line.Length) - 1;
        while (i >= 0 && line[i] == '\\')
        {
            count++;
            i--;
        }

        return count;
    }

    /// <summary>The whitespace run of <paramref name="line"/> around <paramref name="col"/>, bounded by a pipe or non-space on each side — an empty cell's inter-pipe region.</summary>
    private static (int From, int To) WhitespaceRegion(string line, int col)
    {
        int from = Math.Clamp(col, 0, line.Length);
        while (from > 0 && IsPad(line[from - 1]))
            from--;
        int to = Math.Clamp(col, 0, line.Length);
        while (to < line.Length && IsPad(line[to]))
            to++;
        return (from, to);
    }

    private static bool IsPad(char c) => c is ' ' or '\t';
}

/// <summary>
/// The plan a <see cref="TableEditingController"/> operation returns (M3.WP4): either a buffer splice with
/// a target caret, a pure caret move, an exit below the table, or a no-op — applied by the caret owner
/// through <see cref="EditController"/> so the caret state machine stays in one place.
/// </summary>
public readonly record struct TableCommand
{
    /// <summary>The splice to apply, or <see langword="null"/> for a pure navigation / no-op.</summary>
    public Edit? Edit { get; private init; }

    /// <summary>How the splice folds into undo history (ignored when <see cref="Edit"/> is null).</summary>
    public EditKind Kind { get; private init; }

    /// <summary>Whether the undo group is sealed on both sides of the splice (structural cell ops).</summary>
    public bool Seal { get; private init; }

    /// <summary>The caret to install after the command (absolute); meaningful unless <see cref="ExitsBelow"/> or <see cref="IsNoOp"/>.</summary>
    public TextPosition Caret { get; private init; }

    /// <summary>Whether the caret should leave the table below it (Enter on the last row).</summary>
    public bool ExitsBelow { get; private init; }

    /// <summary>Whether the command does nothing (the key bubbles / is swallowed with no effect).</summary>
    public bool IsNoOp { get; private init; }

    /// <summary>A do-nothing command.</summary>
    public static TableCommand NoOperation => new() { IsNoOp = true };

    /// <summary>Leave the table below it.</summary>
    public static TableCommand ExitBelow => new() { ExitsBelow = true };

    /// <summary>A pure caret move to <paramref name="caret"/> (no mutation).</summary>
    public static TableCommand Navigate(TextPosition caret) => new() { Caret = caret };

    /// <summary>A buffer splice landing the caret at <paramref name="caret"/>.</summary>
    public static TableCommand Splice(Edit edit, EditKind kind, TextPosition caret, bool seal = false)
        => new() { Edit = edit, Kind = kind, Caret = caret, Seal = seal };
}
