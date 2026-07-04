namespace CursorialEdit.Document.Buffer;

/// <summary>
/// Which way an <see cref="Anchor"/> leans when text is inserted exactly at it, and where it
/// collapses to when the text containing it is removed.
/// </summary>
public enum AnchorGravity
{
    /// <summary>
    /// The anchor sticks to the text before it: insertion at the anchor leaves it in place
    /// (before the inserted text); removal of a range containing it collapses it to the start
    /// of the splice.
    /// </summary>
    Left = 0,

    /// <summary>
    /// The anchor sticks to the text after it: insertion at the anchor pushes it after the
    /// inserted text; removal of a range containing it collapses it to the end of the splice's
    /// inserted text.
    /// </summary>
    Right = 1,
}

/// <summary>
/// A long-lived position registered with an <see cref="AnchorTable"/>, kept current across
/// buffer splices by the standard gravity rules (see <see cref="AnchorTable"/> for the exact
/// mapping). Covers non-block positions such as find-match highlights and footnote
/// back-references (architecture §2.1).
/// </summary>
public sealed class Anchor
{
    internal Anchor(TextPosition position, AnchorGravity gravity)
    {
        Position = position;
        Gravity = gravity;
    }

    /// <summary>The anchor's current position — always valid for the owning buffer.</summary>
    public TextPosition Position { get; internal set; }

    /// <summary>How the anchor behaves at splice boundaries.</summary>
    public AnchorGravity Gravity { get; }

    /// <summary>Scratch used by the owning table during a splice: the anchor's absolute offset captured pre-mutation.</summary>
    internal int CapturedOffset;
}
