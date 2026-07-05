using Cursorial.Input;

using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M2.WP8 — caret/selection/word motion over the run map (architecture §2.4). Horizontal motion walks
/// grapheme clusters within <c>Text</c>/<c>RevealedMark</c> runs and structurally skips zero-width
/// <c>HiddenMark</c> runs (never landing "inside" a hidden mark) treating a synthetic marker atomically;
/// word motion segments the concatenated <b>visible</b> text (hidden marks absent); the goal column is
/// preserved in cells across wrapped rows and block kinds; and copy emits the exact source range,
/// marks included. Driven through the real markdown surface (<see cref="MarkdownEditingHarness"/>).
/// </summary>
public sealed class MarkdownCaretTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── horizontal motion over runs ─────────────────────────────

    [Fact]
    public void ArrowRight_OnActiveLine_LandsOnEachRevealedMark()
    {
        // The caret's own line is active, so its emphasis marks are RevealedMark — visible AND landable:
        // Right walks `*`, `*`, `b`, … one cell at a time (unlike an inactive line, where they hide).
        using var harness = MarkdownEditingHarness.Create("**bold**");

        foreach (var col in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            harness.Key(Key.RightArrow);
            harness.AssertCaret(0, col); // col 1 and col 7 sit ON the revealed `*` marks
        }

        harness.Key(Key.RightArrow); // clamps at the block/document end
        harness.AssertCaret(0, 8);
    }

    [Fact]
    public void LandingOnAnInactiveMarkedLine_SkipsHiddenMarks_NeverInside()
    {
        // "x\n**bold**" is one paragraph; with the caret on line 0, line 1's `**bold**` is formatted (the
        // `**` are zero-width HiddenMark runs). Clicking line 1's first cell lands on the VISIBLE 'b'
        // (col 2), never inside the hidden `**` (cols 0–1) — the run map collapses the marks onto the
        // content's cell, so no click/motion can land "inside" a hidden mark.
        using var harness = MarkdownEditingHarness.Create("x\n**bold**");

        harness.Click(0, 1); // window cell 0 of line 1's row
        harness.AssertCaret(1, 2);
    }

    [Fact]
    public void InactiveListMarker_IsOneAtomicStop()
    {
        // "- one\n- two" is one list block. With the caret on line 0, line 1's "- " marker renders as a
        // single • synthetic mapping ATOMICALLY to the marker source — clicking anywhere on the marker
        // (cells 0 or 1) lands at the marker start (col 0), while the item text starts at col 2. The whole
        // marker is one caret stop.
        using var harness = MarkdownEditingHarness.Create("- one\n- two");

        harness.Click(1, 1); // the marker's second cell (the padding) of the inactive line 1
        harness.AssertCaret(1, 0); // still the marker's single atomic stop, never "inside" it
    }

    // ───────────────────────────── word motion over visible text ─────────────────────────────

    [Fact]
    public void CtrlLeft_WordMotion_SkipsHiddenMarks_LandingOnVisibleWordStart()
    {
        // "**bold**\nx" is one paragraph. With the caret on line 1 (active), line 0's `**bold**` is
        // formatted (marks hidden) — Ctrl+Left crosses the terminator and lands at the START of the
        // VISIBLE word "bold" (col 2), skipping the leading `**`; the raw source rule would land at col 0.
        using var harness = MarkdownEditingHarness.Create("**bold**\nx");

        harness.Key(Key.DownArrow);      // → (1,0)
        harness.AssertCaret(1, 0);

        harness.Key(Key.LeftArrow, KeyModifiers.Control);
        harness.AssertCaret(0, 2);       // start of the visible "bold", NOT the raw col 0
    }

    [Fact]
    public void CtrlRight_WordMotion_NavigatesVisibleWords()
    {
        // Ctrl+Right over the visible words of an (inactive, from the caret's vantage) second line: it
        // lands at each whitespace boundary of the RENDERED text "my link end", not inside the hidden
        // link scaffolding.
        using var harness = MarkdownEditingHarness.Create("x\n[my link](/u) end");

        harness.Key(Key.DownArrow); // → (1,0)

        harness.Key(Key.RightArrow, KeyModifiers.Control);
        harness.AssertCaret(1, 3); // end of visible "my" (`[my`)

        harness.Key(Key.RightArrow, KeyModifiers.Control);
        harness.AssertCaret(1, 13); // end of visible "link" — past the hidden `](/u)` to the next boundary
    }

    // ───────────────────────────── goal column across kinds/widths ─────────────────────────────

    [Fact]
    public void GoalColumn_PreservedInCells_AcrossBlocksOfDifferentWidths()
    {
        // Two paragraph blocks of different widths. A goal cell captured on the first survives the trip
        // down into the second and back up — the sticky cell goal is re-applied per landing row across the
        // block boundary and the different widths.
        using var harness = MarkdownEditingHarness.Create("first para\n\nsecond paragraph is much wider");

        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow); // (0,3) — a stable goal cell
        var (cell0, _) = harness.Cursor;

        harness.Key(Key.DownArrow); // into the second block at the goal cell
        harness.Key(Key.DownArrow);
        var descended = harness.Caret.Position;
        Assert.True(descended.Line > 0, "the caret descended into the second block");

        harness.Key(Key.UpArrow);
        harness.Key(Key.UpArrow);
        harness.AssertCaret(0, 3);          // the run ends where it began — goal cell restored
        Assert.Equal((cell0, 0), harness.Cursor);
    }

    [Fact]
    public void GoalColumn_PreservedInCells_AcrossWrappedRows()
    {
        // A single logical line wide enough to wrap into several visual rows at width 12. A goal cell
        // survives crossing the wrap boundaries (Down/Up over the same block's visual rows).
        using var harness = MarkdownEditingHarness.Create("alpha bravo charlie delta echo", columns: 12);

        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow); // goal cell 3 on the first wrap row
        var start = harness.Caret.Position;

        harness.Key(Key.DownArrow);
        int afterDownLine = harness.Caret.Position.Line;
        harness.Key(Key.UpArrow);
        harness.AssertCaret(start.Line, start.Col); // returns to the start after a Down/Up over wrapped rows
        Assert.Equal(0, afterDownLine); // still the same logical source line (wrap, not a new block)
    }

    // ───────────────────────────── copy emits exact source (marks included) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Copy_EmitsExactSourceRange_MarksIncluded(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("**bold** text", preset);

        // Select the source range `**bold**` (cols 0..8) — the selection is a SOURCE range, the reveal
        // rendering is incidental. Copy must serialize the raw source, marks and all.
        for (var i = 0; i < 8; i++)
            harness.Key(Key.RightArrow, KeyModifiers.Shift);

        harness.AssertCaret(0, 8, anchor: new TextPosition(0, 0));
        Assert.Equal("**bold**", harness.Caret.SelectedText());

        harness.Chord('c', KeyModifiers.Control); // Ctrl+C → internal store gets the exact source
        Assert.Equal("**bold**", harness.Editor.Clipboard.Text);
    }
}
