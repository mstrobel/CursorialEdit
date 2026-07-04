using System.Collections;

namespace CursorialEdit.Document.Buffer;

/// <summary>
/// The registered <see cref="Anchor"/>s of a <see cref="DocumentBuffer"/>, shifted on every
/// splice by the standard rules. Cost is O(anchors) per edit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mapping rule.</b> A replacement is modeled as delete-then-insert-at-start. For a splice
/// replacing the offset range <c>[s, s + removed)</c> with <c>inserted</c> characters, an anchor
/// at pre-splice offset <c>a</c> maps to:
/// </para>
/// <list type="bullet">
/// <item><description><c>a</c> when <c>a &lt; s</c> (strictly before the splice);</description></item>
/// <item><description><c>s</c> (<see cref="AnchorGravity.Left"/>) or <c>s + inserted</c>
/// (<see cref="AnchorGravity.Right"/>) when <c>s &lt;= a &lt;= s + removed</c> — positions inside
/// the removed range collapse, and the boundary positions take the insertion-gravity rule;</description></item>
/// <item><description><c>a - removed + inserted</c> when <c>a &gt; s + removed</c> (shifted).</description></item>
/// </list>
/// <para>
/// The mapped offset is then converted back to a <see cref="TextPosition"/>; an offset that
/// lands inside a CRLF terminator snaps to the nearest valid position before it, exactly like
/// <see cref="IDocumentBuffer.GetPosition"/>.
/// </para>
/// </remarks>
public sealed class AnchorTable : IReadOnlyCollection<Anchor>
{
    private readonly DocumentBuffer _buffer;
    private readonly List<Anchor> _anchors = [];

    internal AnchorTable(DocumentBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>Number of registered anchors.</summary>
    public int Count => _anchors.Count;

    /// <summary>
    /// Registers an anchor at <paramref name="position"/> with the given gravity.
    /// </summary>
    /// <param name="position">A valid position in the owning buffer.</param>
    /// <param name="gravity">How the anchor behaves at splice boundaries.</param>
    /// <returns>The live anchor; observe <see cref="Anchor.Position"/> after edits.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is not valid for the buffer.</exception>
    public Anchor Register(TextPosition position, AnchorGravity gravity)
    {
        _buffer.ValidatePosition(position, nameof(position));
        var anchor = new Anchor(position, gravity);
        _anchors.Add(anchor);
        return anchor;
    }

    /// <summary>
    /// Removes <paramref name="anchor"/> from the table; it stops tracking edits.
    /// </summary>
    /// <returns><see langword="true"/> if the anchor was registered here.</returns>
    public bool Unregister(Anchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return _anchors.Remove(anchor);
    }

    /// <summary>Captures every anchor's absolute offset against the pre-splice line structure.</summary>
    internal void CaptureOffsets()
    {
        foreach (var anchor in _anchors)
            anchor.CapturedOffset = _buffer.GetOffset(anchor.Position);
    }

    /// <summary>Re-derives every anchor's position from its captured offset via the mapping rule, against the post-splice structure.</summary>
    internal void Remap(int startOffset, int removedLength, int insertedLength)
    {
        int removedEnd = startOffset + removedLength;

        foreach (var anchor in _anchors)
        {
            int offset = anchor.CapturedOffset;

            int mapped;
            if (offset < startOffset)
                mapped = offset;
            else if (offset <= removedEnd)
                mapped = anchor.Gravity == AnchorGravity.Left ? startOffset : startOffset + insertedLength;
            else
                mapped = offset - removedLength + insertedLength;

            anchor.Position = _buffer.GetPosition(mapped);
        }
    }

    /// <inheritdoc/>
    public IEnumerator<Anchor> GetEnumerator() => _anchors.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
