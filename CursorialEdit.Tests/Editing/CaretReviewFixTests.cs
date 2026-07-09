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

    /// <summary>
    /// Select All puts the active end at the document end, so the viewport follows it to the bottom and
    /// <b>realizes paragraphs that were never realized at the top</b>. Those blocks paint the selection fill
    /// from their live provider, but the caret's overlay-tracking set was seeded at the Select-All state
    /// change — <i>before</i> the scroll realized them — so it holds no entry for them. A later click that
    /// clears the selection then computes <c>now == (0,0)</c> with <c>was == null</c>, and the
    /// <c>else if (was is not null)</c> guard <b>skips the invalidate</b>, stranding the highlight until an
    /// unrelated re-raster scrolls it out of view. Seeding the overlay at realize time
    /// (<c>DocumentCaret.OnPresenterRealized</c>, the whole-document analog of
    /// <c>TablePresenter.RetrackHighlightedRows</c>) closes the gap.
    /// </summary>
    [Fact]
    public void SelectAll_ThenClickToPlaceCaret_ClearsHighlightOnScrollRealizedBlocks()
    {
        const int rows = 12;

        // 30 paragraphs — far taller than the viewport, so Select All scrolls the top blocks off and
        // realizes a wholly different band at the bottom.
        var doc = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Para {i:D2}"));
        using var h = MarkdownEditingHarness.Create(doc, columns: 40, rows: rows);

        var topBefore = VisibleParagraphRows(h, rows);
        Assert.True(topBefore.Count >= 2, "the top of the document must show at least two paragraphs");

        // A default (unselected, non-active) content-cell background to compare against: the caret sits at
        // (0,0), so the SECOND visible paragraph is neither active nor selected — its fill is the theme default.
        var plainBg = h.BackgroundAt(2, topBefore[1]);
        var topParagraphText = h.RowTrimmed(topBefore[0]); // "Para 01" — must scroll out of view below

        // Select All → the viewport follows the active end to the document bottom.
        h.Caret.SelectAll();
        h.Settle();
        Assert.True(h.Caret.HasSelection);

        // The scroll moved past the initial realized band — the top paragraph is gone, so every visible
        // paragraph now is a block realized UNDER the active selection (the untracked ones the bug strands).
        Assert.DoesNotContain(topParagraphText, VisibleRowTexts(h, rows));
        var visibleAfter = VisibleParagraphRows(h, rows);
        Assert.True(visibleAfter.Count >= 2, "the bottom of the document must show at least two paragraphs");

        int assertRow = visibleAfter[0];  // a scroll-realized paragraph we do NOT click
        int clickRow = visibleAfter[^1];  // the paragraph we click into (its block goes active — re-rasters anyway)
        Assert.NotEqual(assertRow, clickRow);

        // Pre-condition: the asserted paragraph is highlighted (its fill differs from the default).
        Assert.NotEqual(plainBg, h.BackgroundAt(2, assertRow));

        // Click into the LAST paragraph to place a caret — this clears the selection.
        h.Click(2, clickRow);
        Assert.False(h.Caret.HasSelection);

        // The earlier, scroll-realized paragraph (NOT the click target) must lose its highlight.
        // Pre-fix: never tracked in _selectionPainted, so the clear skipped it and the fill persisted.
        Assert.Equal(plainBg, h.BackgroundAt(2, assertRow));
    }

    /// <summary>The frame rows (0..<paramref name="rows"/>) whose trimmed text is a "Para NN" paragraph line.</summary>
    private static List<int> VisibleParagraphRows(MarkdownEditingHarness h, int rows)
    {
        var result = new List<int>();
        for (int r = 0; r < rows; r++)
            if (h.RowTrimmed(r).StartsWith("Para", StringComparison.Ordinal))
                result.Add(r);
        return result;
    }

    /// <summary>The trimmed text of every visible frame row (0..<paramref name="rows"/>).</summary>
    private static List<string> VisibleRowTexts(MarkdownEditingHarness h, int rows)
    {
        var result = new List<string>();
        for (int r = 0; r < rows; r++)
            result.Add(h.RowTrimmed(r));
        return result;
    }
}
