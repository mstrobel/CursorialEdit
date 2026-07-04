using System.Text;

namespace CursorialEdit.Document.Buffer;

/// <summary>
/// The canonical document artifact (architecture Decision 1): a <c>List&lt;Line&gt;</c> where each
/// line carries its text, its own terminator, and a mutation stamp. Saves serialize this table —
/// never an AST — so round-trip fidelity holds by construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical structure invariant.</b> After construction <i>and after every splice</i>, the
/// line structure is exactly what re-splitting <see cref="GetText()"/> from scratch would
/// produce: all <c>"\r\n"</c> pairs in the byte stream are CRLF terminators (never a text-final
/// <c>'\r'</c> abutting an LF terminator), a lone <c>'\r'</c> is ordinary text, and exactly the
/// last line has <see cref="LineEnding.None"/>. Splices maintain this by rebuilding the affected
/// lines from their literal local text, so an inserted <c>'\r'</c> that lands in front of an LF
/// terminator merges into a CRLF terminator rather than leaving a non-canonical seam. The fuzz
/// suite asserts structure equality against a naive fresh split after every scripted splice.
/// </para>
/// <para>
/// <b>Offset cache.</b> Absolute offsets are derived from a prefix-sum array over line lengths,
/// invalidated from the edited line and extended lazily on demand — O(log n) lookups once warm,
/// amortized recompute from the edit point after a splice.
/// </para>
/// <para>
/// <b>Stamps.</b> <see cref="Epoch"/> (long) and <see cref="CurrentVersion"/> (int) both bump
/// exactly once per applied splice, degenerate splices included; every line in the spliced line
/// range is rewritten with the new version, lines outside it are untouched.
/// </para>
/// </remarks>
public sealed class DocumentBuffer : IDocumentBuffer
{
    private readonly List<Line> _lines;

    /// <summary>Prefix sums: entry <c>i</c> is the absolute offset of line <c>i</c>'s start; entry <c>LineCount</c> is the total length.</summary>
    private int[] _lineStarts;

    /// <summary>Count of valid leading entries in <see cref="_lineStarts"/>; entry 0 (== 0) is always valid.</summary>
    private int _validStarts;

    private long _epoch;
    private int _currentVersion;

    /// <summary>Creates an empty buffer: one empty line with <see cref="LineEnding.None"/>.</summary>
    public DocumentBuffer()
        : this(string.Empty)
    {
    }

    /// <summary>
    /// Creates a buffer from <paramref name="text"/>, detecting each line's terminator
    /// (<c>"\n"</c> → <see cref="LineEnding.Lf"/>, <c>"\r\n"</c> → <see cref="LineEnding.CrLf"/>;
    /// a lone <c>'\r'</c> stays in the text). Mixed endings are preserved per line.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public DocumentBuffer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _lines = SplitSegments(text, LineEnding.None, version: 0);
        _lineStarts = new int[_lines.Count + 1];
        _validStarts = 1;
        Anchors = new AnchorTable(this);
    }

    /// <inheritdoc/>
    public int LineCount => _lines.Count;

    /// <inheritdoc/>
    public int Length
    {
        get
        {
            EnsureStarts(_lines.Count);
            return _lineStarts[_lines.Count];
        }
    }

    /// <inheritdoc/>
    public long Epoch => _epoch;

    /// <inheritdoc/>
    public int CurrentVersion => _currentVersion;

    /// <inheritdoc/>
    public AnchorTable Anchors { get; }

    /// <inheritdoc/>
    public Line GetLine(int index)
    {
        if ((uint) index >= (uint) _lines.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Line index must be in [0, {_lines.Count}).");

        return _lines[index];
    }

    /// <inheritdoc/>
    public string GetText() => Line.Serialize(_lines);

    /// <inheritdoc/>
    public string GetText(TextPosition start, TextPosition end)
    {
        ValidatePosition(start, nameof(start));
        ValidatePosition(end, nameof(end));

        if (end < start)
            throw new ArgumentException($"Range end {end} precedes start {start}.", nameof(end));

        return GetTextRaw(start.Line, start.Col, end.Line, end.Col);
    }

    /// <inheritdoc/>
    public string GetTextAtOffset(int offset, int length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be non-negative.");

        var (startLine, startCol) = ResolveRaw(offset, nameof(offset));
        var (endLine, endCol) = ResolveRaw(offset + length, nameof(length));
        return GetTextRaw(startLine, startCol, endLine, endCol);
    }

    /// <inheritdoc/>
    public int GetOffset(TextPosition position)
    {
        ValidatePosition(position, nameof(position));
        EnsureStarts(position.Line);
        return _lineStarts[position.Line] + position.Col;
    }

    /// <inheritdoc/>
    public TextPosition GetPosition(int offset)
    {
        var (line, col) = ResolveRaw(offset, nameof(offset));
        return new TextPosition(line, Math.Min(col, _lines[line].Text.Length));
    }

    /// <inheritdoc/>
    public SpliceResult Apply(TextPosition start, TextPosition end, string inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        ValidatePosition(start, nameof(start));
        ValidatePosition(end, nameof(end));

        if (end < start)
            throw new ArgumentException($"Range end {end} precedes start {start}.", nameof(end));

        return SpliceCore(start.Line, start.Col, end.Line, end.Col, inserted);
    }

    /// <inheritdoc/>
    public SpliceResult Apply(TextPosition start, int removedLength, string inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        ValidatePosition(start, nameof(start));

        if (removedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(removedLength), removedLength, "Removed length must be non-negative.");

        int startOffset = GetOffset(start);
        var (endLine, endCol) = ResolveRaw(startOffset + removedLength, nameof(removedLength));
        return SpliceCore(start.Line, start.Col, endLine, endCol, inserted);
    }

    /// <inheritdoc/>
    public SpliceResult ApplyAtOffset(int startOffset, int removedLength, string inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);

        if (removedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(removedLength), removedLength, "Removed length must be non-negative.");

        var (startLine, startCol) = ResolveRaw(startOffset, nameof(startOffset));
        var (endLine, endCol) = ResolveRaw(startOffset + removedLength, nameof(removedLength));
        return SpliceCore(startLine, startCol, endLine, endCol, inserted);
    }

    /// <summary>
    /// The one mutation path. Columns here are <i>raw</i>: they may reach into a CRLF
    /// terminator (<c>col == Text.Length + 1</c> addresses the gap between <c>'\r'</c> and
    /// <c>'\n'</c>), which only the offset-based entry points can produce.
    /// </summary>
    private SpliceResult SpliceCore(int startLine, int startCol, int endLine, int endCol, string inserted)
    {
        string removed = GetTextRaw(startLine, startCol, endLine, endCol);

        EnsureStarts(startLine);
        int startOffset = _lineStarts[startLine] + startCol;

        // Anchor offsets must be captured against the pre-splice structure.
        Anchors.CaptureOffsets();

        // Rebuild the affected lines from their literal local text. Splitting head+inserted+tail
        // as one string is what keeps the structure canonical: any "\r\n" formed across the
        // splice seams becomes a CRLF terminator exactly as a fresh parse would see it.
        var startLineValue = _lines[startLine];
        var endLineValue = _lines[endLine];

        string head = SerializedPrefix(startLineValue, startCol);

        string tail;
        LineEnding tailEnding;
        if (endCol <= endLineValue.Text.Length)
        {
            tail = endLineValue.Text[endCol..];
            tailEnding = endLineValue.Ending;
        }
        else
        {
            // The removal consumed the '\r' of this line's CRLF terminator; the remaining '\n'
            // is an LF ending for whatever the rebuild leaves in front of it.
            tail = string.Empty;
            tailEnding = LineEnding.Lf;
        }

        _epoch++;
        _currentVersion++;

        var segments = SplitSegments(string.Concat(head, inserted, tail), tailEnding, _currentVersion);

        _lines.RemoveRange(startLine, endLine - startLine + 1);
        _lines.InsertRange(startLine, segments);
        _validStarts = Math.Min(_validStarts, startLine + 1);

        var end = GetPosition(startOffset + inserted.Length);
        Anchors.Remap(startOffset, removed.Length, inserted.Length);

        return new SpliceResult(startOffset, removed, end, _epoch);
    }

    /// <summary>
    /// Splits <paramref name="text"/> into lines: <c>'\n'</c> terminates a line, a directly
    /// preceding <c>'\r'</c> folds into a CRLF terminator, a lone <c>'\r'</c> is content. The
    /// trailing segment takes <paramref name="trailingEnding"/> — and when that is
    /// <see cref="LineEnding.Lf"/> and the segment ends with <c>'\r'</c>, the two merge into a
    /// CRLF terminator (the canonical-structure rule at the tail seam).
    /// </summary>
    private static List<Line> SplitSegments(string text, LineEnding trailingEnding, int version)
    {
        var result = new List<Line>();
        int segmentStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            bool crlf = i > segmentStart && text[i - 1] == '\r';
            result.Add(new Line(text[segmentStart..(crlf ? i - 1 : i)], crlf ? LineEnding.CrLf : LineEnding.Lf, version));
            segmentStart = i + 1;
        }

        if (trailingEnding == LineEnding.Lf && text.Length > segmentStart && text[^1] == '\r')
            result.Add(new Line(text[segmentStart..^1], LineEnding.CrLf, version));
        else
            result.Add(new Line(text[segmentStart..], trailingEnding, version));

        return result;
    }

    /// <summary>Serialized prefix of a line: <c>[0, col)</c> of its text followed by its terminator characters (raw cols may include the terminator's <c>'\r'</c>).</summary>
    private static string SerializedPrefix(Line line, int col)
    {
        return col <= line.Text.Length
            ? line.Text[..col]
            : line.Text + line.EndingText[..(col - line.Text.Length)];
    }

    /// <summary>Reassembles the raw half-open range; raw columns may address terminator interiors.</summary>
    private string GetTextRaw(int startLine, int startCol, int endLine, int endCol)
    {
        if (startLine == endLine)
        {
            var builder = new StringBuilder(endCol - startCol);
            AppendSerializedSlice(builder, _lines[startLine], startCol, endCol);
            return builder.ToString();
        }

        var sb = new StringBuilder();
        AppendSerializedSlice(sb, _lines[startLine], startCol, _lines[startLine].TotalLength);

        for (int i = startLine + 1; i < endLine; i++)
        {
            sb.Append(_lines[i].Text);
            sb.Append(_lines[i].EndingText);
        }

        AppendSerializedSlice(sb, _lines[endLine], 0, endCol);
        return sb.ToString();
    }

    /// <summary>Appends the <c>[from, to)</c> slice of a line's serialized form (text plus terminator).</summary>
    private static void AppendSerializedSlice(StringBuilder builder, Line line, int from, int to)
    {
        int textLength = line.Text.Length;

        if (from < textLength)
            builder.Append(line.Text, from, Math.Min(to, textLength) - from);

        if (to > textLength)
        {
            string ending = line.EndingText;
            int endingFrom = Math.Max(from - textLength, 0);
            builder.Append(ending, endingFrom, to - textLength - endingFrom);
        }
    }

    /// <summary>Throws unless <paramref name="position"/> addresses a line and a column within that line's text (end-of-text inclusive).</summary>
    internal void ValidatePosition(TextPosition position, string paramName)
    {
        if ((uint) position.Line >= (uint) _lines.Count)
            throw new ArgumentOutOfRangeException(paramName, position, $"Position line must be in [0, {_lines.Count}).");

        if ((uint) position.Col > (uint) _lines[position.Line].Text.Length)
            throw new ArgumentOutOfRangeException(paramName, position, $"Position column must be in [0, {_lines[position.Line].Text.Length}] on line {position.Line}.");
    }

    /// <summary>
    /// Resolves an absolute offset to (line, raw column), where the raw column may point inside
    /// a CRLF terminator. Validates 0..<see cref="Length"/> inclusive.
    /// </summary>
    private (int Line, int Col) ResolveRaw(int offset, string paramName)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(paramName, offset, "Offset must be non-negative.");

        EnsureStartsCovering(offset, paramName);

        // Upper-bound binary search over the valid prefix: first entry > offset.
        int lo = 1, hi = _validStarts;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_lineStarts[mid] > offset)
                hi = mid;
            else
                lo = mid + 1;
        }

        int line = Math.Min(lo - 1, _lines.Count - 1);
        return (line, offset - _lineStarts[line]);
    }

    /// <summary>Ensures prefix-sum entries exist through index <paramref name="through"/> (0..LineCount).</summary>
    private void EnsureStarts(int through)
    {
        while (_validStarts <= through)
            ExtendOneStart();
    }

    /// <summary>Extends the prefix sums until some valid entry exceeds <paramref name="offset"/> or the table is complete; throws if the offset is beyond the end.</summary>
    private void EnsureStartsCovering(int offset, string paramName)
    {
        while (_validStarts <= _lines.Count && _lineStarts[_validStarts - 1] <= offset)
            ExtendOneStart();

        if (_validStarts == _lines.Count + 1 && offset > _lineStarts[_lines.Count])
            throw new ArgumentOutOfRangeException(paramName, offset, $"Offset must be in [0, {_lineStarts[_lines.Count]}].");
    }

    private void ExtendOneStart()
    {
        if (_lineStarts.Length < _lines.Count + 1)
            Array.Resize(ref _lineStarts, Math.Max(_lines.Count + 1, _lineStarts.Length * 2));

        _lineStarts[_validStarts] = _lineStarts[_validStarts - 1] + _lines[_validStarts - 1].TotalLength;
        _validStarts++;
    }
}
