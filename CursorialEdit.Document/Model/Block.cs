namespace CursorialEdit.Document.Model;

/// <summary>
/// One document block: an identity, a kind, and a <b>line count</b> — deliberately <i>not</i> a
/// start line (architecture Decision 8's prefix-sum discipline). Start lines are prefix sums over
/// the owning <see cref="BlockList"/>, recomputed from the edit point, so an edit inside block
/// <i>i</i> re-forms only block <i>i</i> while everything after it shifts implicitly with zero
/// per-block rewriting. A block's source lines are reached through the list:
/// <c>buffer.GetLine(list.GetStartLine(index) + k)</c> for <c>k</c> in <c>[0, LineCount)</c>.
/// </summary>
/// <remarks>
/// Blocks are immutable; a re-formed block is a <b>new instance carrying the same
/// <see cref="Id"/></b> (identity lives in the id, never in the object reference). M2 hangs the
/// Markdig AST reference and the re-adoption <c>ContentStamp</c> off this class; M1 carries only
/// what plain-text rendering needs.
/// </remarks>
public sealed class Block
{
    /// <summary>Creates a block.</summary>
    /// <param name="id">The producer-allocated identity.</param>
    /// <param name="kind">The structural kind.</param>
    /// <param name="lineCount">The number of source lines this block owns (≥ 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lineCount"/> &lt; 1.</exception>
    public Block(BlockId id, BlockKind kind, int lineCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lineCount, 1);

        Id = id;
        Kind = kind;
        LineCount = lineCount;
    }

    /// <summary>The block's stable identity (see <see cref="BlockId"/>).</summary>
    public BlockId Id { get; }

    /// <summary>The block's structural kind.</summary>
    public BlockKind Kind { get; }

    /// <summary>
    /// The number of consecutive source lines this block owns. Every source line belongs to
    /// exactly one block — the <see cref="BlockList"/> tiles the document.
    /// </summary>
    public int LineCount { get; }

    /// <summary>Compact diagnostic form (<c>#7 Paragraph ×3</c>).</summary>
    public override string ToString() => $"{Id} {Kind} ×{LineCount}";
}
