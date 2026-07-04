using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Layout;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M1.WP8 — the gate's caret-motion suite (spec §3.1 plain-text subset): arrows, Home/End,
/// Ctrl+Home/End, word motion, PageUp/Down, and the cell-measured sticky goal column, over the
/// shared CJK/emoji/ZWJ <see cref="NavigationFixtures"/>, asserting the composited
/// <c>FrameBuffer</c> cursor position (the real terminal caret) <b>and</b> the buffer-level caret
/// state. Rendering-affecting theories run under both §5.1 wire presets.
/// </summary>
public sealed class CaretTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── cluster-boundary arrows ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ArrowRight_WalksClusterBoundaries_CjkEmojiZwj(string preset)
    {
        using var harness = EditingHarness.Create(NavigationFixtures.ClusterFixture, preset);

        for (var i = 1; i < NavigationFixtures.ClusterBoundaries.Length; i++)
        {
            harness.Key(Key.RightArrow);
            harness.AssertCaret(0, NavigationFixtures.ClusterBoundaries[i]);
            Assert.True(harness.Host.FrameBuffer.CursorVisible);
            Assert.Equal((NavigationFixtures.ClusterCells[i], 0), harness.Cursor);
        }

        // Right at the end of the last line clamps — the caret never leaves the document.
        harness.Key(Key.RightArrow);
        harness.AssertCaret(0, NavigationFixtures.ClusterFixture.Length);
        Assert.Equal((11, 0), harness.Cursor);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void ArrowLeft_WalksClusterBoundariesBackward(string preset)
    {
        using var harness = EditingHarness.Create(NavigationFixtures.ClusterFixture, preset);
        harness.Key(Key.End, KeyModifiers.Control);
        harness.AssertCaret(0, NavigationFixtures.ClusterFixture.Length);

        for (var i = NavigationFixtures.ClusterBoundaries.Length - 2; i >= 0; i--)
        {
            harness.Key(Key.LeftArrow);
            harness.AssertCaret(0, NavigationFixtures.ClusterBoundaries[i]);
            Assert.Equal((NavigationFixtures.ClusterCells[i], 0), harness.Cursor);
        }

        harness.Key(Key.LeftArrow); // clamp at the document start
        harness.AssertCaret(0, 0);
    }

    [Fact]
    public void Arrows_CrossLineBoundariesAtLineEnds()
    {
        using var harness = EditingHarness.Create("ab\ncd");

        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow);
        harness.AssertCaret(0, 2);
        Assert.Equal((2, 0), harness.Cursor);

        harness.Key(Key.RightArrow); // line end → next line start
        harness.AssertCaret(1, 0);
        Assert.Equal((0, 1), harness.Cursor);

        harness.Key(Key.LeftArrow); // line start → previous line end
        harness.AssertCaret(0, 2);
        Assert.Equal((2, 0), harness.Cursor);
    }

    // ───────────────────────────── word motion ─────────────────────────────

    [Fact]
    public void WordMotion_CtrlRightLeft_MirrorsTextBoxWhitespaceSemantics()
    {
        // The probed TextBox landings over "foo, bar—baz 漢字 end" (report §3.1/§3.2):
        // punctuation adheres, unspaced CJK is one word, Ctrl+Right lands at the run's END.
        using var harness = EditingHarness.Create(NavigationFixtures.WordFixture);

        foreach (var landing in new[] { 4, 12, 15, 19 })
        {
            harness.Key(Key.RightArrow, KeyModifiers.Control);
            harness.AssertCaret(0, landing);
        }

        foreach (var landing in new[] { 16, 13, 5, 0 })
        {
            harness.Key(Key.LeftArrow, KeyModifiers.Control);
            harness.AssertCaret(0, landing);
        }
    }

    [Fact]
    public void WordMotion_CrossesLineBoundaries()
    {
        using var harness = EditingHarness.Create("foo\nbar baz");

        harness.Key(Key.RightArrow, KeyModifiers.Control);
        harness.AssertCaret(0, 3); // end of "foo"

        harness.Key(Key.RightArrow, KeyModifiers.Control);
        harness.AssertCaret(1, 3); // the terminator is whitespace — skipped into "bar"

        harness.Key(Key.RightArrow, KeyModifiers.Control);
        harness.AssertCaret(1, 7);

        harness.Key(Key.LeftArrow, KeyModifiers.Control);
        harness.AssertCaret(1, 4); // start of "baz"

        harness.Key(Key.LeftArrow, KeyModifiers.Control);
        harness.AssertCaret(1, 0);

        harness.Key(Key.LeftArrow, KeyModifiers.Control);
        harness.AssertCaret(0, 0); // back across the terminator into "foo"
    }

    // ───────────────────────────── Home / End over soft wrap ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void HomeEnd_LandOnVisualRows_WithWrapBoundaryAffinity(string preset)
    {
        // Wrap28Fixture at width 28: rows [0,11) "aaaaaaaaaa " and [11,33) 22 b's. Col 11 is the
        // affinity-ambiguous wrap boundary: End keeps it on row 0's end, a fresh landing from
        // Home+Down renders the SAME col at row 1's start (probe §Home/End parity).
        using var harness = EditingHarness.Create(NavigationFixtures.Wrap28Fixture, preset, columns: 28, rows: 6);

        harness.Key(Key.End);
        harness.AssertCaret(0, 11);
        Assert.Equal((11, 0), harness.Cursor); // end-affinity: rendered at row 0's visual end

        harness.Key(Key.Home); // the caret's row is row 0 (affinity), so Home lands its start
        harness.AssertCaret(0, 0);
        Assert.Equal((0, 0), harness.Cursor);

        harness.Key(Key.DownArrow);
        harness.AssertCaret(0, 11); // the same source col …
        Assert.Equal((0, 1), harness.Cursor); // … rendered at row 1's start (start-affinity landing)

        harness.Key(Key.End);
        harness.AssertCaret(0, 33);
        Assert.Equal((22, 1), harness.Cursor);
    }

    // ───────────────────────────── Ctrl+Home / Ctrl+End (document ends, scroll follows) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CtrlHomeEnd_MoveTheCaret_ScrollingFollows(string preset)
    {
        var document = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i:D2}"));
        using var harness = EditingHarness.Create(document, preset);

        harness.Key(Key.End, KeyModifiers.Control);
        harness.AssertCaret(29, 6);
        Assert.Equal(20, harness.ScrollViewer.VerticalOffset); // 30 rows − 10 viewport
        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.Equal((6, 9), harness.Cursor); // document row 29 at viewport row 9

        harness.Key(Key.Home, KeyModifiers.Control);
        harness.AssertCaret(0, 0);
        Assert.Equal(0, harness.ScrollViewer.VerticalOffset);
        Assert.Equal((0, 0), harness.Cursor);
    }

    // ───────────────────────────── goal column ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void GoalColumn_StickyInCells_AcrossShorterRow(string preset)
    {
        // ThreeRowFixture at 28: rows of widths 28 / 3 ("bb ") / 27. The goal cell 20 survives
        // the 3-cell middle row and is re-applied on the far side; Up restores it exactly.
        using var harness = EditingHarness.Create(NavigationFixtures.ThreeRowFixture, preset, columns: 28, rows: 6);

        harness.Click(20, 0);
        harness.AssertCaret(0, 20);
        Assert.Equal((20, 0), harness.Cursor);

        harness.Key(Key.DownArrow);
        harness.AssertCaret(0, 31); // clamped to the middle row's content end (col 28 + 3)
        Assert.Equal((3, 1), harness.Cursor);

        harness.Key(Key.DownArrow);
        harness.AssertCaret(0, 51); // goal cell 20 re-applied on row 2 (col 31 + 20)
        Assert.Equal((20, 2), harness.Cursor);

        harness.Key(Key.UpArrow);
        Assert.Equal((3, 1), harness.Cursor);

        harness.Key(Key.UpArrow);
        harness.AssertCaret(0, 20); // the run ends where it began
        Assert.Equal((20, 0), harness.Cursor);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void GoalColumn_InsideWideCluster_SnapsAtOrBefore(string preset)
    {
        // CjkRowFixture row 1 is all 2-cell clusters; a goal cell of 5 falls inside the third
        // cluster and must snap to the boundary at-or-before (cell 4), never inside (§3.1 [EDGE]).
        using var harness = EditingHarness.Create(NavigationFixtures.CjkRowFixture, preset, columns: 28, rows: 6);

        harness.Click(5, 0);
        harness.AssertCaret(0, 5);

        harness.Key(Key.DownArrow);
        harness.AssertCaret(0, 30); // col 28 (row start) + 2 (one whole cluster)
        Assert.Equal((4, 1), harness.Cursor);

        harness.Key(Key.UpArrow);
        harness.AssertCaret(0, 5); // the goal cell (5) was kept, not the snapped landing
        Assert.Equal((5, 0), harness.Cursor);
    }

    // ───────────────────────────── paging ─────────────────────────────

    [Fact]
    public void PageUpDown_MoveCaretByViewport_KeepGoalColumn_ScrollFollows()
    {
        var document = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i:D2}"));
        using var harness = EditingHarness.Create(document, columns: 30, rows: 10);

        harness.Click(4, 2);
        harness.AssertCaret(2, 4);

        harness.Key(Key.PageDown); // viewport = 10 rows
        harness.AssertCaret(12, 4);
        Assert.Equal(3, harness.ScrollViewer.VerticalOffset); // minimal scroll to reveal row 12
        Assert.Equal((4, 9), harness.Cursor);

        harness.Key(Key.PageDown);
        harness.AssertCaret(22, 4);
        Assert.Equal(13, harness.ScrollViewer.VerticalOffset);

        harness.Key(Key.PageUp);
        harness.AssertCaret(12, 4); // goal column kept across the run
        Assert.Equal(12, harness.ScrollViewer.VerticalOffset);
        Assert.Equal((4, 0), harness.Cursor);

        harness.Key(Key.PageUp);
        harness.AssertCaret(2, 4);
    }

    // ───────────────────────────── scroll-follow at the viewport edges ─────────────────────────────

    [Fact]
    public void CaretScrollFollow_AtViewportEdges()
    {
        var document = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i:D2}"));
        using var harness = EditingHarness.Create(document, columns: 30, rows: 10);

        for (var i = 0; i < 9; i++)
            harness.Key(Key.DownArrow);

        harness.AssertCaret(9, 0);
        Assert.Equal(0, harness.ScrollViewer.VerticalOffset); // row 9 is the last visible row — no scroll yet
        Assert.Equal((0, 9), harness.Cursor);

        harness.Key(Key.DownArrow); // crossing the bottom edge scrolls exactly one row
        harness.AssertCaret(10, 0);
        Assert.Equal(1, harness.ScrollViewer.VerticalOffset);
        Assert.Equal((0, 9), harness.Cursor);
        Assert.True(harness.Host.FrameBuffer.CursorVisible);

        for (var i = 0; i < 3; i++)
            harness.Key(Key.UpArrow);

        harness.AssertCaret(7, 0); // still inside the viewport — the offset holds
        Assert.Equal(1, harness.ScrollViewer.VerticalOffset);
        Assert.Equal((0, 6), harness.Cursor);

        for (var i = 0; i < 7; i++)
            harness.Key(Key.UpArrow);

        harness.AssertCaret(0, 0); // crossing the top edge pulled the viewport back up
        Assert.Equal(0, harness.ScrollViewer.VerticalOffset);
        Assert.Equal((0, 0), harness.Cursor);
    }

    // ───────────────────────────── undo restores caret + selection (gate) ─────────────────────────────

    [Fact]
    public void Undo_RestoresCaretAndSelection()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Click(8, 0, clickCount: 2); // select "world"
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6));

        harness.Type("X"); // replace the selection
        Assert.Equal("hello X", harness.Buffer.GetText());
        harness.AssertCaret(0, 7);

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("hello world", harness.Buffer.GetText());
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6)); // caret AND selection restored
        Assert.Equal((11, 0), harness.Cursor);
    }
}
