using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// Regressions for the wave-4/5 review's caret/selection findings.
/// </summary>
public sealed class CaretReviewFixTests
{
    /// <summary>
    /// <c>PredictEnd</c> feeds the recorded redo caret. A bare <c>\r</c> in pasted text is ordinary
    /// content to the buffer (only <c>"\n"</c>/<c>"\r\n"</c> break lines), so counting it as a break
    /// mispredicts the redo landing — the live paste lands correctly (it uses the splice receipt),
    /// but redo restores the mispredicted position. Redo must land where the paste did.
    /// </summary>
    [Fact]
    public void Redo_AfterPastingBareCarriageReturn_RestoresTheActualLanding()
    {
        using var h = EditingHarness.Create("", columns: 40);

        h.Caret.Paste("a\rb"); // bare CR — one line "a\rb", caret lands at (0, 3)
        h.Settle();
        var landing = h.Caret.Position;
        Assert.Equal(new TextPosition(0, 3), landing); // sanity: the authoritative receipt landing

        h.Caret.Undo();
        h.Settle();
        h.Caret.Redo();
        h.Settle();

        Assert.Equal(landing, h.Caret.Position); // pre-fix: redo landed at the mispredicted (0, 2)
    }

    /// <summary>
    /// A block's selection overlay must clear when an undo in an <b>earlier</b> block shifts it and
    /// restores a no-selection state. The old repaint diffed the previously-painted <i>absolute</i>
    /// offsets against the post-splice block offsets, so a large shift made them miss the overlap and
    /// skip the invalidation — leaving a stale highlight. The id-keyed diff is immune to the shift.
    /// </summary>
    [Fact]
    public void Undo_ShiftingASelectedLaterBlock_ClearsTheStaleSelectionOverlay()
    {
        using var h = EditingHarness.Create("AAA\n\nBBBBB", columns: 40);

        // A long insert in the first block — shifts the last block's absolute offset by more than the
        // selection's own length (the condition that made the stale-offset compare miss).
        h.Caret.MoveDocumentStart(extend: false);
        h.Caret.MoveEnd(extend: false); // end of "AAA" — (0, 3)
        h.Caret.InsertText("ZZZZZZZZZZ");
        h.Settle();

        // Select the last block ("BBBBB", content row 2) — its overlay paints.
        h.Caret.SelectBlockAt(new TextPosition(2, 0));
        h.Settle();
        var plain = h.BackgroundAt(0, 0);         // block 0's first cell — never selected
        Assert.NotEqual(plain, h.BackgroundAt(0, 2)); // the selection fill is live on the last block

        // Undo the first-block insert: the last block shifts back AND the restored state has no
        // selection — its overlay must clear.
        h.Caret.Undo();
        h.Settle();

        Assert.False(h.Caret.HasSelection);
        Assert.Equal(plain, h.BackgroundAt(0, 2)); // pre-fix: the stale "selected" fill persisted
    }
}
