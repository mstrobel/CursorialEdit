using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Terminal;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.Layout;

using static CursorialEdit.Tests.Layout.NavigationFixtures;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// M1.WP6 — the R4 probe (implementation-plan §6 WP6; architecture risk R4 / FB-1): drives a REAL
/// multi-line <see cref="TextBox"/> (<c>AcceptsReturn</c>, <c>TextWrapping</c> on) under
/// <see cref="UITestHost"/> and the promoted framework text primitives (<see cref="GraphemeLayout"/>,
/// <see cref="TextNavigation"/>, <see cref="TextLayout"/> — the editor now consumes these directly,
/// FB-1 retired) over IDENTICAL grapheme fixtures, comparing caret landings for Left/Right cluster
/// steps, Ctrl+Left/Right word motion,
/// Up/Down goal-column over soft-wrapped rows, and Home/End end-of-line affinity. Logical landings are
/// read from the public <see cref="TextBox.CaretIndex"/>; the <b>visual</b> position (the affinity
/// observable — which visual row a wrap-boundary caret renders on) is readable only via rendering, so
/// it is asserted through the terminal cursor (<c>FrameBuffer.CursorColumn/CursorRow</c>) the
/// <c>TextPresenter</c> publishes. Findings are catalogued in <c>text-navigation-probe-report.md</c>.
/// </summary>
public sealed class TextNavigationProbeTests
{
    /// <summary>Both §5.1 wire presets — the shared registry (<see cref="TestSupport.CapabilityPresets"/>).</summary>
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // Fixtures live in NavigationFixtures (hand-computed cluster inventory + display-cell maps).

    // Single-line vertical/Home/End oracles: the sticky-goal + affinity composition the WP8 caret owns
    // (over ICaretMap), expressed here purely through the promoted TextLayout's row/column accessors —
    // no mirrored line-packer, so what the probe compares against TextBox is the framework primitive.

    private static (int Col, int Cell, bool EndAffinity) MoveVertical(
        TextLayout layout, int col, int delta, int desiredCell = -1, bool endAffinity = false)
    {
        var (row, cell) = layout.Locate(col, endAffinity);
        int goal = desiredCell >= 0 ? desiredCell : cell;
        int targetRow = Math.Clamp(row + delta, 0, layout.LineCount - 1);
        int targetCol = layout.OffsetAt(targetRow, goal);
        return (targetCol, goal, layout.IsLineEndBoundary(targetRow, targetCol));
    }

    private static (int Col, bool EndAffinity) EndCol(TextLayout layout, int col, bool endAffinity = false)
    {
        int row = layout.Locate(col, endAffinity).Line;
        int end = layout.LineContentEnd(row);
        return (end, layout.IsLineEndBoundary(row, end));
    }

    private static int HomeCol(TextLayout layout, int col, bool endAffinity = false)
        => layout.LineContentStart(layout.Locate(col, endAffinity).Line);

    // ───────────────────────────── Left / Right cluster steps ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void RightThenLeft_StepsClusterBoundaries_TextBoxAndNavigatorAgree(string preset)
    {
        using var probe = TextBoxProbe.Create(ClusterFixture, preset);
        var glyphs = GraphemeLayout.Build(ClusterFixture);

        // Walk Right to the end: every landing must be the primitive's NextBoundary, and the terminal
        // cursor must sit at the landing's column (ColumnOf) — the wide clusters advance 2 cells.
        var col = 0;
        while (col < ClusterFixture.Length)
        {
            var expected = glyphs.NextBoundary(col);
            probe.Press(Key.RightArrow);
            Assert.Equal(expected, probe.CaretIndex);
            probe.AssertCursorAt(glyphs.ColumnOf(expected), 0, $"Right from col {col}");
            col = expected;
        }

        probe.Press(Key.RightArrow); // at the end: stays
        Assert.Equal(ClusterFixture.Length, probe.CaretIndex);

        // Walk Left back to the start symmetrically.
        while (col > 0)
        {
            var expected = glyphs.PrevBoundary(col);
            probe.Press(Key.LeftArrow);
            Assert.Equal(expected, probe.CaretIndex);
            probe.AssertCursorAt(glyphs.ColumnOf(expected), 0, $"Left from col {col}");
            col = expected;
        }

        probe.Press(Key.LeftArrow); // at the start: stays
        Assert.Equal(0, probe.CaretIndex);
    }

    // ───────────────────────────── Ctrl+Left / Ctrl+Right word motion ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CtrlRightThenCtrlLeft_WordLandings_TextBoxAndNavigatorAgree(string preset)
    {
        using var probe = TextBoxProbe.Create(WordFixture, preset);

        // TextBox semantics (mirrored, documented in the report): whitespace-delimited runs —
        // punctuation adheres to its word ("foo," is one landing unit), unspaced CJK is one word, and
        // Ctrl+Right lands at the END of the word run (col 4, 12, 15, 19), not the next word's start.
        var col = 0;
        while (col < WordFixture.Length)
        {
            var expected = TextNavigation.NextWord(WordFixture, col);
            probe.Press(Key.RightArrow, KeyModifiers.Control);
            Assert.Equal(expected, probe.CaretIndex);
            col = expected;
        }

        Assert.Equal(19, col); // pinned: the walk really ended at the last run boundary

        while (col > 0)
        {
            var expected = TextNavigation.PrevWord(WordFixture, col);
            probe.Press(Key.LeftArrow, KeyModifiers.Control);
            Assert.Equal(expected, probe.CaretIndex);
            col = expected;
        }
    }

    /// <summary>
    /// DIVERGENCE RECORD (verdict: deliberate — see text-navigation-probe-report.md §3.1). The
    /// implementation plan's WP6 gloss describes words as "letters/digits runs vs space/punctuation",
    /// but <c>TextBox</c>'s actual classifier (<c>TextNavigation</c>) is whitespace-delimited: from
    /// col 0 of <c>"foo, bar"</c> a letters/digits classifier would stop at 3 (end of <c>foo</c>) or
    /// 5 (start of <c>bar</c>); TextBox lands at 4 (after <c>foo,</c> — punctuation adheres). The
    /// editor consumes the framework <c>TextNavigation</c> classifier verbatim, so this parity holds by
    /// construction; a markdown-aware classifier is an M2 run-map decision, recorded under FB-1.
    /// </summary>
    [Fact]
    public void Divergence_WordClassifier_IsWhitespaceDelimited_NotLettersDigits()
    {
        Assert.Equal(4, TextNavigation.NextWord("foo, bar", 0));     // NOT 3, NOT 5
        Assert.Equal(5, TextNavigation.PrevWord("foo, bar!?", 10));  // "bar!?" is one word
        Assert.Equal(15, TextNavigation.NextWord(WordFixture, 12));  // unspaced CJK is one word, not per-ideograph
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void CtrlRight_OverEmojiRun_LandsOnClusterBoundary(string preset)
    {
        var text = EmojiWordFixture; // two 👍 (4 UTF-16 units) + space + "ok"
        using var probe = TextBoxProbe.Create(text, preset);

        probe.Press(Key.RightArrow, KeyModifiers.Control);
        Assert.Equal(TextNavigation.NextWord(text, 0), probe.CaretIndex);
        Assert.Equal(4, probe.CaretIndex); // after the emoji run — a cluster boundary
        probe.AssertCursorAt(4, 0, "Ctrl+Right over the emoji run"); // 2 wide cells + … = cell 4
    }

    // ───────────────────────────── soft-wrap segmentation parity ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void WrapSegmentation_TextBoxRowsMatchNavigatorRows(string preset)
    {
        using var probe = TextBoxProbe.Create(Wrap28Fixture, preset);

        var wrapped = TextLayout.Build(Wrap28Fixture, probe.WrapWidth, WrapMode.WordWrap);
        Assert.Equal(2, wrapped.LineCount); // fixture sanity
        Assert.Equal(11, wrapped.LineContentStart(1));

        // The rendered rows must break exactly where the navigator says: 10 a's (+ trailing space) on
        // row 0, all 22 b's on row 1 — no 'b' leaks onto row 0 and no 'a' onto row 1.
        var row0 = probe.RowText(0);
        var row1 = probe.RowText(1);
        Assert.Contains(new string('a', 10), row0);
        Assert.DoesNotContain('b', row0);
        Assert.Contains(new string('b', 22), row1);
        Assert.DoesNotContain('a', row1);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void WrapSegmentation_WideClusterNeverStraddlesTheEdge(string preset)
    {
        using var probe = TextBoxProbe.Create(StraddleFixture, preset);

        var wrapped = TextLayout.Build(StraddleFixture, probe.WrapWidth, WrapMode.WordWrap);
        Assert.Equal(2, wrapped.LineCount);
        Assert.Equal(27, wrapped.LineContentStart(1)); // 27 a's + 漢 = 29 cells > 28 → the CJK moves whole

        var row0 = probe.RowText(0);
        var row1 = probe.RowText(1);
        Assert.Contains(new string('a', 27), row0);
        Assert.DoesNotContain('漢', row0); // never half a wide cluster at the edge
        Assert.Contains("漢漢漢", row1);
    }

    // ───────────────────────────── Up / Down goal column ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void UpDown_StickyGoalColumn_SurvivesAShortMiddleRow(string preset)
    {
        using var probe = TextBoxProbe.Create(ThreeRowFixture, preset);

        var wrapped = TextLayout.Build(ThreeRowFixture, probe.WrapWidth, WrapMode.WordWrap);
        Assert.Equal(3, wrapped.LineCount); // "aaa…a " (28 cells) / "bb " (3) / 27 c's

        probe.SetCaret(10); // row 0, cell 10

        // Down onto the 3-cell middle row: clamps to its end — which IS the next wrap col, so
        // end-affinity must keep the caret rendered on row 1 (col 31 aliases to row 2's start).
        var (col1, goal, affinity1) = MoveVertical(wrapped, 10, +1);
        Assert.Equal((31, true), (col1, affinity1)); // fixture sanity (pinned)
        probe.Press(Key.DownArrow);
        Assert.Equal(col1, probe.CaretIndex);
        probe.AssertCursorAt(3, 1, "Down onto the short row (end-affinity)");

        // Down again: the goal column is sticky — row 2 lands back at cell 10, not cell 3.
        var (col2, _, _) = MoveVertical(wrapped, col1, +1, goal, affinity1);
        Assert.Equal(41, col2); // pinned
        probe.Press(Key.DownArrow);
        Assert.Equal(col2, probe.CaretIndex);
        probe.AssertCursorAt(10, 2, "Down onto row 2 (sticky goal column)");

        // Up twice returns to row 0 cell 10 through the same aliased middle row.
        probe.Press(Key.UpArrow);
        Assert.Equal(31, probe.CaretIndex);
        probe.AssertCursorAt(3, 1, "Up onto the short row (end-affinity)");
        probe.Press(Key.UpArrow);
        Assert.Equal(10, probe.CaretIndex);
        probe.AssertCursorAt(10, 0, "Up back to row 0");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void UpDown_GoalInsideWideCluster_SnapsBeforeIt_AndGoalStaysExact(string preset)
    {
        using var probe = TextBoxProbe.Create(CjkRowFixture, preset);

        var wrapped = TextLayout.Build(CjkRowFixture, probe.WrapWidth, WrapMode.WordWrap);
        Assert.Equal(2, wrapped.LineCount);

        probe.SetCaret(5); // row 0, cell 5 (odd — falls between CJK boundaries on row 1)

        // Down: goal cell 5 falls inside the third 汉 (cells 4..6) → land at cell 4 (col 30), never
        // inside the cluster.
        var (down, goal, _) = MoveVertical(wrapped, 5, +1);
        Assert.Equal((30, 5), (down, goal)); // pinned
        probe.Press(Key.DownArrow);
        Assert.Equal(down, probe.CaretIndex);
        probe.AssertCursorAt(4, 1, "Down into the CJK row (snap before the wide cluster)");

        // Up: the sticky goal is still 5 — the caret returns to cell 5 exactly, not the snapped 4.
        probe.Press(Key.UpArrow);
        Assert.Equal(5, probe.CaretIndex);
        probe.AssertCursorAt(5, 0, "Up restores the exact goal cell");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void UpDown_AtDocumentEdges_ClampsToTheSameRow(string preset)
    {
        // TextBox (and therefore the navigator) clamps the target row: Up on the first visual row and
        // Down on the last are NO-OPs at the goal column — the caret does not jump to line start/end
        // (a deliberate divergence from some editors; see the divergence report).
        using var probe = TextBoxProbe.Create(Wrap28Fixture, preset);

        probe.SetCaret(5);
        probe.Press(Key.UpArrow);
        Assert.Equal(5, probe.CaretIndex);
        probe.AssertCursorAt(5, 0, "Up at the first row");

        probe.SetCaret(16); // row 1, cell 5
        probe.Press(Key.DownArrow);
        Assert.Equal(16, probe.CaretIndex);
        probe.AssertCursorAt(5, 1, "Down at the last row");
    }

    // ───────────────────────────── Home / End affinity ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void End_OnWrappedRow_LandsOnWrapColWithEndAffinity(string preset)
    {
        using var probe = TextBoxProbe.Create(Wrap28Fixture, preset);
        var wrapped = TextLayout.Build(Wrap28Fixture, probe.WrapWidth, WrapMode.WordWrap);

        probe.SetCaret(3); // row 0
        var (endCol, endAffinity) = EndCol(wrapped, 3);
        Assert.Equal((11, true), (endCol, endAffinity)); // pinned: End = the wrap col, end-affinity

        probe.Press(Key.End);
        Assert.Equal(endCol, probe.CaretIndex);
        // The affinity observable: col 11 is also row 1's start, but End renders at ROW 0's visual end.
        probe.AssertCursorAt(11, 0, "End keeps the caret on the wrapped row");

        // Right from the boundary drops the affinity and steps into row 1.
        probe.Press(Key.RightArrow);
        Assert.Equal(GraphemeLayout.Build(Wrap28Fixture).NextBoundary(11), probe.CaretIndex);
        probe.AssertCursorAt(1, 1, "Right after End steps into the next row");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Home_OnWrapCol_RendersAtNextRowStart_SameColAsEndDifferentRow(string preset)
    {
        using var probe = TextBoxProbe.Create(Wrap28Fixture, preset);
        var wrapped = TextLayout.Build(Wrap28Fixture, probe.WrapWidth, WrapMode.WordWrap);

        // Caret mid row 1; Home lands on col 11 with START affinity → renders at row 1 cell 0, even
        // though the SAME col rendered at row 0 cell 11 after End (the aliasing observable).
        probe.SetCaret(20);
        Assert.Equal(11, HomeCol(wrapped, 20)); // pinned
        probe.Press(Key.Home);
        Assert.Equal(11, probe.CaretIndex);
        probe.AssertCursorAt(0, 1, "Home renders at the next row's start");

        // End from there: row 1's End is the true line end — no aliasing, no affinity.
        var (endCol, endAffinity) = EndCol(wrapped, 11);
        Assert.Equal((33, false), (endCol, endAffinity)); // pinned
        probe.Press(Key.End);
        Assert.Equal(endCol, probe.CaretIndex);
        probe.AssertCursorAt(22, 1, "End on the last row lands at the line end");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Home_AfterEnd_BelongsToTheWrappedRow(string preset)
    {
        using var probe = TextBoxProbe.Create(Wrap28Fixture, preset);
        var wrapped = TextLayout.Build(Wrap28Fixture, probe.WrapWidth, WrapMode.WordWrap);

        probe.SetCaret(3);
        probe.Press(Key.End); // col 11, end-affinity (proven above)

        // Home consumes the affinity: the caret's row is row 0, so Home lands at col 0 — not row 1's
        // start (which is the same col the caret already sits on).
        Assert.Equal(0, HomeCol(wrapped, 11, endAffinity: true)); // pinned
        probe.Press(Key.Home);
        Assert.Equal(0, probe.CaretIndex);
        probe.AssertCursorAt(0, 0, "Home after End belongs to the wrapped row");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void DownAfterEnd_UsesTheWrappedRowAsOrigin(string preset)
    {
        using var probe = TextBoxProbe.Create(Wrap28Fixture, preset);
        var wrapped = TextLayout.Build(Wrap28Fixture, probe.WrapWidth, WrapMode.WordWrap);

        probe.SetCaret(3);
        probe.Press(Key.End); // col 11, end-affinity, cell 11 on row 0

        // Down resolves the caret's row via the affinity (row 0) and carries cell 11 down to row 1.
        var (down, _, _) = MoveVertical(wrapped, 11, +1, -1, endAffinity: true);
        Assert.Equal(22, down); // pinned: row 1 start (11) + 11 cols
        probe.Press(Key.DownArrow);
        Assert.Equal(down, probe.CaretIndex);
        probe.AssertCursorAt(11, 1, "Down after End starts from the wrapped row");
    }
}

/// <summary>
/// The probe rig: a real themed <see cref="TextBox"/> (AcceptsReturn, WordWrap) as the root of a
/// 30×10 headless terminal. The default TextBox template pads one column each side (Border Padding
/// (1,0)), so the wrap budget is <c>columns − 2</c>; the cursor origin (caret col 0 ⇒ presenter-local
/// (0,0)) is captured empirically at creation so cursor assertions are chrome-independent deltas.
/// </summary>
internal sealed class TextBoxProbe : IDisposable
{
    private readonly int _originColumn;
    private readonly int _originRow;

    private TextBoxProbe(UITestHost host, TextBox box, int wrapWidth, int originColumn, int originRow)
    {
        Host = host;
        Box = box;
        WrapWidth = wrapWidth;
        _originColumn = originColumn;
        _originRow = originRow;
    }

    public UITestHost Host { get; }

    public TextBox Box { get; }

    /// <summary>The presenter's soft-wrap cell budget (terminal columns − 2 template padding columns).</summary>
    public int WrapWidth { get; }

    public int CaretIndex => Box.CaretIndex;

    public static TextBoxProbe Create(string text, string preset, int columns = 30, int rows = 10)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
        });

        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = WrapMode.WordWrap,
        };

        host.ShowRoot(box);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");
        Assert.True(box.Focus(), "the TextBox did not take focus");
        Assert.True(host.RunUntilIdle(), "the focus frame did not settle");

        // Caret at col 0 renders at presenter-local (0,0) — its screen position is the chrome origin.
        Assert.Equal(0, box.CaretIndex);
        Assert.True(host.FrameBuffer.CursorVisible, "the terminal caret was not published");
        return new TextBoxProbe(host, box, columns - 2, host.FrameBuffer.CursorColumn, host.FrameBuffer.CursorRow);
    }

    /// <summary>Sets the caret directly (collapses selection, resets goal column/affinity) and settles.</summary>
    public void SetCaret(int col)
    {
        Box.CaretIndex = col;
        Settle();
    }

    /// <summary>Sends one key and settles the frame loop.</summary>
    public void Press(Key key, KeyModifiers modifiers = default)
    {
        Host.SendKey(key, modifiers);
        Settle();
    }

    /// <summary>The composited text of a presenter row (0 = the text's first visual row).</summary>
    public string RowText(int row) => Host.GetRowText(_originRow + row);

    /// <summary>
    /// Asserts the terminal cursor sits at presenter-local (<paramref name="cell"/>, <paramref name="row"/>)
    /// — the rendered caret position, the only observable that exposes soft-wrap affinity.
    /// </summary>
    public void AssertCursorAt(int cell, int row, string context)
    {
        Assert.True(Host.FrameBuffer.CursorVisible, $"{context}: the terminal caret is not visible");
        var actual = (Host.FrameBuffer.CursorColumn - _originColumn, Host.FrameBuffer.CursorRow - _originRow);
        Assert.True((cell, row) == actual, $"{context}: expected cursor at (cell {cell}, row {row}), got {actual}");
    }

    private void Settle() => Assert.True(Host.RunUntilIdle(), "the frame loop did not settle");

    public void Dispose() => Host.Dispose();
}
