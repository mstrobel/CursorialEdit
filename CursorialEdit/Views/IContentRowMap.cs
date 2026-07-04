namespace CursorialEdit.Views;

/// <summary>
/// The caret's view of the document's vertical geometry (M1.WP8): block heights folded into
/// content rows — the same prefix sums the <see cref="DocumentPanel"/> arranges blocks by, so the
/// caret's (row, cell) math and the panel's block placement agree by construction.
/// <see cref="EditorControl"/> implements it by delegating to its templated panel (zeros before
/// the template expands — caret operations only run on input events, which require layout).
/// </summary>
internal interface IContentRowMap
{
    /// <summary>Total content rows (the scroll extent's row count).</summary>
    int ContentRows { get; }

    /// <summary>The top content row of block <paramref name="blockIndex"/> (clamped to the block range).</summary>
    int BlockTopRow(int blockIndex);

    /// <summary>The index of the block whose rows contain <paramref name="contentRow"/> (clamped to the content range).</summary>
    int BlockIndexOfRow(int contentRow);
}
