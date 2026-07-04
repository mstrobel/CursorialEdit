namespace CursorialEdit.Document.Buffer;

/// <summary>
/// The canonical document coordinate (architecture §2.1): a zero-based line index and a
/// zero-based column measured in <b>UTF-16 code units</b> into that line's <see cref="Line.Text"/>.
/// </summary>
/// <remarks>
/// <para>
/// A position is valid for a buffer when <c>0 &lt;= Line &lt; LineCount</c> and
/// <c>0 &lt;= Col &lt;= Text.Length</c> of that line. <c>Col == Text.Length</c> addresses the
/// end of the line's text, immediately <i>before</i> its terminator; the next valid position is
/// the start of the following line — terminators are atomic and have no interior positions.
/// </para>
/// <para>
/// Columns are UTF-16 offsets, not cells and not grapheme clusters: a position may legally
/// split a surrogate pair or a ZWJ sequence. Producing only cluster-snapped caret positions is
/// the caret navigator's job (M1.WP6), not the buffer's.
/// </para>
/// </remarks>
/// <param name="Line">Zero-based line index.</param>
/// <param name="Col">Zero-based UTF-16 column within the line's text.</param>
public readonly record struct TextPosition(int Line, int Col) : IComparable<TextPosition>
{
    /// <summary>The origin position (line 0, column 0).</summary>
    public static readonly TextPosition Zero = new(0, 0);

    /// <summary>Orders positions by line, then by column.</summary>
    public int CompareTo(TextPosition other)
    {
        int byLine = Line.CompareTo(other.Line);
        return byLine != 0 ? byLine : Col.CompareTo(other.Col);
    }

    /// <summary>Returns whether <paramref name="left"/> precedes <paramref name="right"/> in document order.</summary>
    public static bool operator <(TextPosition left, TextPosition right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether <paramref name="left"/> follows <paramref name="right"/> in document order.</summary>
    public static bool operator >(TextPosition left, TextPosition right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether <paramref name="left"/> precedes or equals <paramref name="right"/> in document order.</summary>
    public static bool operator <=(TextPosition left, TextPosition right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether <paramref name="left"/> follows or equals <paramref name="right"/> in document order.</summary>
    public static bool operator >=(TextPosition left, TextPosition right) => left.CompareTo(right) >= 0;
}
