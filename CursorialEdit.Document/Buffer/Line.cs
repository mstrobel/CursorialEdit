using System.Text;

namespace CursorialEdit.Document.Buffer;

/// <summary>
/// One line of a <see cref="DocumentBuffer"/>: the line's text without any terminator, the
/// terminator that follows it in the byte stream, and the buffer-wide mutation stamp of the
/// edit that last produced it.
/// </summary>
/// <param name="Text">The line's characters, excluding the terminator. Never <see langword="null"/>.</param>
/// <param name="Ending">The terminator following <paramref name="Text"/> (<see cref="LineEnding.None"/> only on the last line).</param>
/// <param name="Version">
/// The <see cref="DocumentBuffer.CurrentVersion"/> value of the edit that created or last
/// rewrote this line (0 for lines untouched since construction). Versions are monotonic per
/// buffer, so a consumer that recorded a stamp <c>S</c> knows the line is unmodified since
/// then iff <c>Version &lt;= S</c> — the input to M2's block re-adoption rule.
/// </param>
public readonly record struct Line(string Text, LineEnding Ending, int Version)
{
    /// <summary>Length of the serialized terminator in UTF-16 code units (0, 1, or 2).</summary>
    public int EndingLength => Ending switch
    {
        LineEnding.Lf   => 1,
        LineEnding.CrLf => 2,
        _               => 0,
    };

    /// <summary>The terminator's literal text: <c>""</c>, <c>"\n"</c>, or <c>"\r\n"</c>.</summary>
    public string EndingText => Ending switch
    {
        LineEnding.Lf   => "\n",
        LineEnding.CrLf => "\r\n",
        _               => string.Empty,
    };

    /// <summary>Total serialized length of the line in UTF-16 code units: <c>Text.Length + EndingLength</c>.</summary>
    public int TotalLength => Text.Length + EndingLength;

    /// <summary>
    /// Serializes a line sequence to document text — each line followed by its own terminator, so
    /// mixed CRLF/LF endings survive byte-exact. THE one reassembly rule:
    /// <see cref="DocumentBuffer.GetText()"/> and the autosave snapshot both call this, so the
    /// journal's text is byte-identical to the buffer's by construction (and M6.WP4's
    /// BOM/encoding extension lands in one place).
    /// </summary>
    internal static string Serialize(IReadOnlyList<Line> lines)
    {
        var capacity = 0;
        for (var i = 0; i < lines.Count; i++)
            capacity += lines[i].TotalLength;

        var builder = new StringBuilder(capacity);
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines[i].Text);
            builder.Append(lines[i].EndingText);
        }

        return builder.ToString();
    }
}
