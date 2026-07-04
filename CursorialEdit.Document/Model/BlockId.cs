namespace CursorialEdit.Document.Model;

/// <summary>
/// The stable identity of a <see cref="Block"/> (architecture §2.2 step 5): allocated once by the
/// block producer and preserved for as long as the block survives reconciliation, across every
/// index shift and line shift. Presenter reuse, layout caches, fold state, and (from M2) table
/// cell focus all key on this identity — never on a block's position in the list.
/// </summary>
/// <param name="Value">The producer-allocated identity value (monotonic per producer, never reused).</param>
public readonly record struct BlockId(long Value)
{
    /// <summary>Compact diagnostic form (<c>#7</c>).</summary>
    public override string ToString() => $"#{Value}";
}
