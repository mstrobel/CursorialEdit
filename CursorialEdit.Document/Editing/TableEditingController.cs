using System.Text;

using Cursorial.Text;

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

        string sanitized = Sanitize(text, collapseBreaks);
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        if (model.IsCellEmpty(cell.Row, cell.Column))
        {
            // Pad the empty inter-pipe whitespace region to `│ text │` (the region is whitespace-only —
            // Markdig classified the cell empty — so scanning it is escape-safe). The caret lands after the
            // typed content, before the trailing pad, ready for more typing.
            var pos = _buffer.GetPosition(caretOffset);
            string line = _buffer.GetLine(pos.Line).Text;
            var (from, to) = WhitespaceRegion(line, pos.Col);
            string removed = line.Substring(from, to - from);
            var editStart = new TextPosition(pos.Line, from);
            var target = new TextPosition(pos.Line, from + 1 + sanitized.Length); // after "␠text"
            return TableCommand.Splice(new Edit(editStart, removed, " " + sanitized + " "), kind, target);
        }

        // Sanitised text carries no line break, so the landing stays on the caret's line (computed
        // arithmetically — caretOffset + length may lie beyond the pre-edit buffer for a large paste).
        var end = new TextPosition(caret.Line, caret.Col + sanitized.Length);
        return TableCommand.Splice(new Edit(caret, string.Empty, sanitized), kind, end);
    }

    /// <summary>Backspace within the caret's cell — deletes the previous grapheme cluster, bounded to the cell (never merges cells or deletes a pipe).</summary>
    public TableCommand Backspace(TableModel model, int blockStart, TextPosition caret)
    {
        int caretOffset = _buffer.GetOffset(caret);
        if (model.CellOfOffset(caretOffset - blockStart) is not { } cell)
            return TableCommand.NoOperation;

        var (contentStart, _) = model.CellContentRange(cell.Row, cell.Column);
        if (caretOffset - blockStart <= contentStart)
            return TableCommand.NoOperation; // at (or before) the cell's content start — nothing to delete in-cell

        string line = _buffer.GetLine(caret.Line).Text;
        int prev = PrevCluster(line, caret.Col);
        if (prev >= caret.Col)
            return TableCommand.NoOperation;

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
        if (caretOffset - blockStart >= contentEnd)
            return TableCommand.NoOperation; // at (or past) the cell's content end — nothing to delete in-cell

        string line = _buffer.GetLine(caret.Line).Text;
        int next = NextCluster(line, caret.Col);
        if (next <= caret.Col)
            return TableCommand.NoOperation;

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
    {
        int end = blockStart + model.RowTextEndOffset(model.RowCount - 1);
        var at = _buffer.GetPosition(end); // valid: the end of the last row's text (before its terminator)

        var row = new StringBuilder("\n|");
        for (var c = 0; c < model.ColumnCount; c++)
            row.Append("   |");

        // The appended row becomes the next line; its first cell's opening pipe is at col 0, so the caret
        // lands just inside it (col 1). Computed arithmetically — the inserted text is not in the buffer yet.
        var target = new TextPosition(at.Line + 1, 1);
        return TableCommand.Splice(new Edit(at, string.Empty, row.ToString()), EditKind.Structural, target, seal: true);
    }

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

    // ───────────────────────────── text hygiene ─────────────────────────────

    /// <summary>Escapes a value for a single GFM cell: <c>|</c> → <c>\|</c> always, and (for paste) every line break → a single space so a multi-line payload stays one row.</summary>
    private static string Sanitize(string text, bool collapseBreaks)
    {
        bool hasPipe = text.Contains('|');
        bool hasBreak = collapseBreaks && (text.Contains('\n') || text.Contains('\r'));
        if (!hasPipe && !hasBreak)
            return text;

        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '|')
            {
                sb.Append("\\|");
            }
            else if (collapseBreaks && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++; // CRLF → one space
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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

    // ───────────────────────────── cluster boundaries (single-line) ─────────────────────────────

    private static int PrevCluster(ReadOnlySpan<char> line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);
        int best = 0;
        int boundary = 0;
        var e = line.GetGraphemeEnumerator();
        while (e.MoveNext())
        {
            boundary += e.Current.Length;
            if (boundary >= col)
                break;
            best = boundary;
        }

        return best;
    }

    private static int NextCluster(ReadOnlySpan<char> line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);
        int boundary = 0;
        var e = line.GetGraphemeEnumerator();
        while (e.MoveNext())
        {
            int next = boundary + e.Current.Length;
            if (next > col)
                return next;
            boundary = next;
        }

        return line.Length;
    }
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
