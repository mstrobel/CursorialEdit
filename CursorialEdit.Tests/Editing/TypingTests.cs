using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Layout;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M1.WP8 — the typing input path (spec §3.1/§3.3 plain-text subset): printable
/// <c>TextInput</c> → <c>EditController.Apply</c>, Enter as its own <c>Newline</c> group,
/// cluster-aware Backspace/Delete over the shared grapheme fixtures, selection-replacing edits,
/// the §6.3 Tab-as-spaces rule, and the caret-echo/undo-group interplay end to end through the
/// real key routing.
/// </summary>
public sealed class TypingTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    [Theory]
    [MemberData(nameof(Presets))]
    public void Typing_InsertsAtCaret_RendersAndMovesCaret(string preset)
    {
        using var harness = EditingHarness.Create(string.Empty, preset);

        harness.Type("hi👍");

        Assert.Equal("hi👍", harness.Buffer.GetText());
        Assert.Equal("hi👍", harness.Host.GetRowText(0).TrimEnd());
        harness.AssertCaret(0, 4); // 'h' + 'i' + the surrogate-pair emoji (2 UTF-16 units)
        Assert.Equal((4, 0), harness.Cursor); // …which is 2 cells wide
    }

    [Fact]
    public void Typing_ReplacesSelection_AsOneAtomicEdit()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Click(6, 0);
        harness.Key(Key.End, KeyModifiers.Shift); // select "world"
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6));

        harness.Type("there");

        Assert.Equal("hello there", harness.Buffer.GetText());
        harness.AssertCaret(0, 11);
        Assert.Equal("hello there", harness.Host.GetRowText(0).TrimEnd());
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Enter_SplitsTheLine_CaretLandsOnTheNextLineStart(string preset)
    {
        using var harness = EditingHarness.Create("abcd", preset);

        harness.Click(2, 0);
        harness.Key(Key.Enter);

        Assert.Equal("ab\ncd", harness.Buffer.GetText());
        harness.AssertCaret(1, 0);
        Assert.Equal((0, 1), harness.Cursor);
        Assert.Equal("ab", harness.Host.GetRowText(0).TrimEnd());
        Assert.Equal("cd", harness.Host.GetRowText(1).TrimEnd());
    }

    [Fact]
    public void Enter_IsItsOwnUndoGroup_BetweenTypedRuns()
    {
        using var harness = EditingHarness.Create(string.Empty);

        harness.Type("ab");
        harness.Key(Key.Enter);
        harness.Type("cd");
        Assert.Equal("ab\ncd", harness.Buffer.GetText());

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("ab\n", harness.Buffer.GetText()); // the "cd" run
        harness.AssertCaret(1, 0);

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("ab", harness.Buffer.GetText()); // the newline, alone (§3.3)
        harness.AssertCaret(0, 2);

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("", harness.Buffer.GetText()); // the "ab" run
        harness.AssertCaret(0, 0);
    }

    [Fact]
    public void Backspace_RemovesPreviousCluster_EmojiAndZwjWhole()
    {
        using var harness = EditingHarness.Create(NavigationFixtures.ClusterFixture);
        harness.Key(Key.End, KeyModifiers.Control);

        harness.Key(Key.Backspace); // "x"
        Assert.Equal(NavigationFixtures.ClusterFixture[..19], harness.Buffer.GetText());
        Assert.Equal((10, 0), harness.Cursor);

        harness.Key(Key.Backspace); // the ZWJ family — 11 UTF-16 units, one cluster, one keypress
        Assert.Equal(NavigationFixtures.ClusterFixture[..8], harness.Buffer.GetText());
        Assert.Equal((8, 0), harness.Cursor);

        harness.Key(Key.Backspace); // "❤️" (VS16 sequence)
        Assert.Equal(NavigationFixtures.ClusterFixture[..6], harness.Buffer.GetText());
        Assert.Equal((6, 0), harness.Cursor);
    }

    [Fact]
    public void Backspace_AtLineStart_JoinsLines_RemovingCrLfWhole()
    {
        using var harness = EditingHarness.Create("ab\r\ncd\nef");

        harness.Click(0, 1);
        harness.AssertCaret(1, 0);

        harness.Key(Key.Backspace);
        Assert.Equal("abcd\nef", harness.Buffer.GetText()); // the CRLF terminator went as one unit
        harness.AssertCaret(0, 2);
        Assert.Equal((2, 0), harness.Cursor);
    }

    [Fact]
    public void Delete_RemovesNextCluster_AndJoinsAtLineEnd()
    {
        using var harness = EditingHarness.Create("a\nb");

        harness.Key(Key.RightArrow); // (0,1) — the line's text end
        harness.Key(Key.Delete);     // forward delete of the terminator joins the lines
        Assert.Equal("ab", harness.Buffer.GetText());
        harness.AssertCaret(0, 1);

        harness.Key(Key.Delete); // forward delete of the next cluster
        Assert.Equal("a", harness.Buffer.GetText());
        harness.AssertCaret(0, 1);
    }

    [Fact]
    public void Backspace_DeletesTheSelection()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Click(8, 0, clickCount: 2); // select "world"
        harness.Key(Key.Backspace);

        Assert.Equal("hello ", harness.Buffer.GetText());
        harness.AssertCaret(0, 6);
    }

    [Fact]
    public void Tab_InsertsTwoSpaces_AsItsOwnUndoUnit()
    {
        // Spec §6.3 [DECISION]: stray tabs become spaces at the configured indent width (2) —
        // a literal '\t' never enters the document from the keyboard.
        using var harness = EditingHarness.Create("x");

        harness.Key(Key.End);
        harness.Key(Key.Tab);
        Assert.Equal("x  ", harness.Buffer.GetText());
        harness.AssertCaret(0, 3);

        harness.Type("y");
        Assert.Equal("x  y", harness.Buffer.GetText());

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("x  ", harness.Buffer.GetText()); // "y" did not coalesce into the sealed Tab unit

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("x", harness.Buffer.GetText()); // the indent, alone
        Assert.False(harness.Controller.CanUndo);
    }

    [Fact]
    public void TypedRun_CoalescesUntilACaretMoveSealsIt()
    {
        // The end-to-end echo-license path: each keystroke's own landing is consumed as the
        // edit's echo (the run keeps coalescing); the explicit Left seals it.
        using var harness = EditingHarness.Create(string.Empty);

        harness.Type("abc");
        harness.Key(Key.LeftArrow);
        harness.Type("d");
        Assert.Equal("abdc", harness.Buffer.GetText());

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("abc", harness.Buffer.GetText()); // "d" alone — the move sealed the first run
        harness.AssertCaret(0, 2);

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("", harness.Buffer.GetText()); // "abc" as ONE group
        harness.AssertCaret(0, 0);
    }
}
