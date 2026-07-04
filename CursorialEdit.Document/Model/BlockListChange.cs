namespace CursorialEdit.Document.Model;

/// <summary>
/// What one applied splice did to the <see cref="BlockList"/> — the reconciliation contract
/// between the block producer and the view (architecture §2.2 step 5). The M2 parser emits the
/// same shape; the view-side consumer written against this record needs no changes when the
/// degenerate M1 producer is replaced.
/// </summary>
/// <remarks>
/// The notification fires <i>after</i> the list is consistent: indices, start lines, and id
/// lookups queried from the handler observe the post-splice world. The view reconciles presenters
/// by <see cref="BlockId"/> — reuse elements for <see cref="Reused"/> blocks, re-layout/re-draw
/// for <see cref="Changed"/> ones, create for <see cref="Added"/>, tear down for
/// <see cref="Removed"/> — and re-derives height prefix sums only when structure or heights moved.
/// </remarks>
/// <param name="Reused">
/// Blocks that kept identity <b>and content</b>: none of their source lines changed. Their start
/// lines may have shifted implicitly (by <paramref name="LineShift"/> when they sit after the
/// edit window) — prefix sums absorb that; per-block state (presenter, run map) stays valid.
/// </param>
/// <param name="Changed">
/// Blocks that kept identity but were re-formed: source content and/or line count changed.
/// Derived per-block state (run maps, heights, rasters) is stale and must be re-derived.
/// </param>
/// <param name="Added">Newly created blocks (fresh identities).</param>
/// <param name="Removed">Blocks whose identities left the list; their per-block state is dead.</param>
/// <param name="LineShift">
/// The document line-count delta of the splice — the implicit start-line shift of every block
/// positioned after the edit window (blocks before it did not move).
/// </param>
/// <param name="Epoch">The buffer epoch this change was computed at (Decision 13's staleness stamp).</param>
public sealed record BlockListChange(
    IReadOnlyList<BlockId> Reused,
    IReadOnlyList<BlockId> Changed,
    IReadOnlyList<BlockId> Added,
    IReadOnlyList<BlockId> Removed,
    int LineShift,
    long Epoch);
