using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Document;

/// <summary>
/// M1.WP5 gate — undo group semantics: adjacent Typing edits coalesce by splice adjacency
/// (insert runs rightward, Backspace runs leftward, forward-Delete runs at a fixed start,
/// direction locked per run); groups seal on independent caret moves, non-Typing kinds
/// (each its own unit), explicit <see cref="EditController.SealGroup"/>, undo/redo, and the
/// 750 ms idle window (driven deterministically on <see cref="FakeTimeProvider"/>); depth cap
/// discards the oldest group; a new edit clears redo; undo restores the caret and selection
/// captured before the group's first edit.
/// </summary>
public class UndoGroupTests
{
    private static readonly TimeSpan IdleTimeout = EditController.DefaultIdleSealTimeout;

    private static (DocumentBuffer Buffer, EditController Controller, FakeTimeProvider Time) Create(
        string text = "", int depthLimit = EditController.DefaultUndoDepthLimit)
    {
        var time = new FakeTimeProvider();
        var buffer = new DocumentBuffer(text);
        return (buffer, new EditController(buffer, time, depthLimit), time);
    }

    private static CaretState Caret(int line, int col) => new(new TextPosition(line, col));

    /// <summary>Types <paramref name="text"/> one character at a time as Typing edits from <paramref name="at"/> (single-line text only).</summary>
    private static TextPosition Type(EditController controller, TextPosition at, string text)
    {
        foreach (char ch in text)
        {
            var next = new TextPosition(at.Line, at.Col + 1);
            controller.Apply(new Edit(at, "", ch.ToString()), EditKind.Typing, new CaretState(at), new CaretState(next));
            at = next;
        }

        return at;
    }

    // ---- The named gate tests -----------------------------------------------------------------

    [Fact]
    public void TypedSentence_CoalescesToOneGroup_SecondUndoRemovesPriorGroup()
    {
        var (buffer, controller, time) = Create();

        var at = Type(controller, TextPosition.Zero, "Hello.");
        time.Advance(IdleTimeout); // idle seals the first group
        Type(controller, at, " The quick brown fox!");

        Assert.Equal(2, controller.UndoDepth); // the whole sentence — spaces included — is one group

        var afterFirstUndo = controller.Undo();
        Assert.Equal("Hello.", buffer.GetText());          // first undo removes the entire sentence
        Assert.Equal(Caret(0, 6), afterFirstUndo);         // caret before the sentence's first keystroke

        var afterSecondUndo = controller.Undo();
        Assert.Equal("", buffer.GetText());                // second undo removes the prior group
        Assert.Equal(Caret(0, 0), afterSecondUndo);
        Assert.False(controller.CanUndo);
    }

    [Fact]
    public void Undo_RestoresCaretAndSelection()
    {
        var (buffer, controller, _) = Create("The quick fox");

        // "quick" is selected: anchor at its start, caret (active end) at its end.
        var selection = new CaretState(new TextPosition(0, 9), new TextPosition(0, 4));
        Assert.True(selection.HasSelection);

        controller.Apply(
            new Edit(new TextPosition(0, 4), "quick", "X"),
            EditKind.Typing, selection, Caret(0, 5));
        Assert.Equal("The X fox", buffer.GetText());

        var restored = controller.Undo();
        Assert.Equal("The quick fox", buffer.GetText());
        Assert.Equal(selection, restored);                            // position AND selection anchor
        Assert.Equal(new TextPosition(0, 4), restored!.Value.SelectionAnchor);

        var redone = controller.Redo();
        Assert.Equal("The X fox", buffer.GetText());
        Assert.Equal(Caret(0, 5), redone);                            // redo restores the collapsed caret
        Assert.False(redone!.Value.HasSelection);
    }

    // ---- Idle seal (deterministic on the fake clock) ------------------------------------------

    [Fact]
    public void IdleSeal_JustUnderTimeout_StillCoalesces()
    {
        var (_, controller, time) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        time.Advance(IdleTimeout - TimeSpan.FromMilliseconds(1));
        Type(controller, at, "c");

        Assert.Equal(1, controller.UndoDepth);
    }

    [Fact]
    public void IdleSeal_ExactTimeout_SealsTheGroup()
    {
        var (buffer, controller, time) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        time.Advance(IdleTimeout);
        Type(controller, at, "cd");

        Assert.Equal(2, controller.UndoDepth);
        Assert.NotNull(controller.Undo());
        Assert.Equal("ab", buffer.GetText());
    }

    [Fact]
    public void IdleSeal_MeasuresGapBetweenEdits_NotRunLength()
    {
        var (_, controller, time) = Create();

        // A long run typed with sub-timeout gaps stays one group no matter its total duration.
        var at = TextPosition.Zero;
        for (int i = 0; i < 5; i++)
        {
            at = Type(controller, at, "x");
            time.Advance(IdleTimeout - TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(1, controller.UndoDepth);
    }

    // ---- Seal triggers --------------------------------------------------------------------------

    [Fact]
    public void CaretMove_SealsTheGroup()
    {
        var (_, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        controller.NotifyCaretMoved(Caret(0, 0)); // user moved the caret away…
        controller.NotifyCaretMoved(new CaretState(at)); // …and back to where typing left off
        Type(controller, at, "cd");               // splice-adjacent, but the excursion sealed the run

        Assert.Equal(2, controller.UndoDepth);
    }

    [Fact]
    public void CaretMove_EchoOfTheEditsOwnLanding_DoesNotSeal()
    {
        var (_, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        controller.NotifyCaretMoved(new CaretState(at)); // the caret landing of the last keystroke
        Type(controller, at, "cd");

        Assert.Equal(1, controller.UndoDepth);
    }

    [Fact]
    public void CaretMove_ClickAtTheRunsEnd_SealsDespiteEqualingThePostEditLanding()
    {
        // Wave-2 regression (echo guard over-matched): the echo license is ONE-SHOT per edit.
        // Type "hello" → the edit's own landing echoes through NotifyCaretMoved (consumed, no
        // seal) → the user clicks EXACTLY at the caret (equal state, but the license is spent —
        // seals, like every explicit caret move in the TextBox reference) → typing " world"
        // starts a second group, so undo removes only " world".
        var (buffer, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "hello");
        controller.NotifyCaretMoved(new CaretState(at)); // the last keystroke's echo — consumed
        controller.NotifyCaretMoved(new CaretState(at)); // the click at the same spot — seals
        Type(controller, at, " world");

        Assert.Equal(2, controller.UndoDepth);

        Assert.NotNull(controller.Undo());
        Assert.Equal("hello", buffer.GetText()); // first undo removes only " world"
    }

    [Fact]
    public void SealGroup_ExplicitlyStartsANewGroup()
    {
        var (_, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        controller.SealGroup();
        Type(controller, at, "cd");

        Assert.Equal(2, controller.UndoDepth);
    }

    [Theory]
    [InlineData(EditKind.Newline)]
    [InlineData(EditKind.Structural)]
    [InlineData(EditKind.Paste)]
    public void NonTypingKind_IsItsOwnUnit_AndSealsTheRun(EditKind kind)
    {
        var (_, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        controller.Apply(new Edit(at, "", "X"), kind, new CaretState(at), Caret(0, 3));
        Type(controller, new TextPosition(0, 3), "cd");

        Assert.Equal(3, controller.UndoDepth); // typing run | kind unit | typing run
    }

    [Fact]
    public void ConsecutivePastes_NeverCoalesce()
    {
        var (_, controller, _) = Create();

        controller.Apply(new Edit(TextPosition.Zero, "", "one "), EditKind.Paste, Caret(0, 0), Caret(0, 4));
        controller.Apply(new Edit(new TextPosition(0, 4), "", "two"), EditKind.Paste, Caret(0, 4), Caret(0, 7));

        Assert.Equal(2, controller.UndoDepth);
    }

    [Fact]
    public void Newline_UndoesAsItsOwnStep()
    {
        var (buffer, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        controller.Apply(new Edit(at, "", "\n"), EditKind.Newline, new CaretState(at), Caret(1, 0));
        Type(controller, new TextPosition(1, 0), "cd");

        Assert.NotNull(controller.Undo());
        Assert.Equal("ab\n", buffer.GetText());
        Assert.NotNull(controller.Undo());
        Assert.Equal("ab", buffer.GetText());
        Assert.NotNull(controller.Undo());
        Assert.Equal("", buffer.GetText());
    }

    // ---- Coalescing shapes ----------------------------------------------------------------------

    [Fact]
    public void BackspaceRun_CoalescesLeftward()
    {
        var (buffer, controller, _) = Create("abcd");

        controller.Apply(new Edit(new TextPosition(0, 3), "d", ""), EditKind.Typing, Caret(0, 4), Caret(0, 3));
        controller.Apply(new Edit(new TextPosition(0, 2), "c", ""), EditKind.Typing, Caret(0, 3), Caret(0, 2));
        controller.Apply(new Edit(new TextPosition(0, 1), "b", ""), EditKind.Typing, Caret(0, 2), Caret(0, 1));

        Assert.Equal("a", buffer.GetText());
        Assert.Equal(1, controller.UndoDepth);

        var restored = controller.Undo();
        Assert.Equal("abcd", buffer.GetText());
        Assert.Equal(Caret(0, 4), restored); // the caret before the run's FIRST backspace
    }

    [Fact]
    public void ForwardDeleteRun_CoalescesAtFixedStart()
    {
        var (buffer, controller, _) = Create("abcd");

        controller.Apply(new Edit(TextPosition.Zero, "a", ""), EditKind.Typing, Caret(0, 0), Caret(0, 0));
        controller.Apply(new Edit(TextPosition.Zero, "b", ""), EditKind.Typing, Caret(0, 0), Caret(0, 0));
        controller.Apply(new Edit(TextPosition.Zero, "c", ""), EditKind.Typing, Caret(0, 0), Caret(0, 0));

        Assert.Equal("d", buffer.GetText());
        Assert.Equal(1, controller.UndoDepth);

        Assert.NotNull(controller.Undo());
        Assert.Equal("abcd", buffer.GetText());
    }

    [Fact]
    public void DeleteDirectionSwitch_SealsTheGroup()
    {
        var (buffer, controller, _) = Create("abcdX");

        // Two backspaces lock the run's direction to Backward…
        controller.Apply(new Edit(new TextPosition(0, 3), "d", ""), EditKind.Typing, Caret(0, 4), Caret(0, 3));
        controller.Apply(new Edit(new TextPosition(0, 2), "c", ""), EditKind.Typing, Caret(0, 3), Caret(0, 2));
        // …so a forward Delete at the run's start begins a new group (the TextBox convention).
        controller.Apply(new Edit(new TextPosition(0, 2), "X", ""), EditKind.Typing, Caret(0, 2), Caret(0, 2));

        Assert.Equal("ab", buffer.GetText());
        Assert.Equal(2, controller.UndoDepth);

        Assert.NotNull(controller.Undo());
        Assert.Equal("abX", buffer.GetText());
        Assert.NotNull(controller.Undo());
        Assert.Equal("abcdX", buffer.GetText());
    }

    [Fact]
    public void InsertThenBackspace_DoesNotMerge()
    {
        var (buffer, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        controller.Apply(new Edit(new TextPosition(0, 1), "b", ""), EditKind.Typing, new CaretState(at), Caret(0, 1));

        Assert.Equal(2, controller.UndoDepth); // deletes never fold into an insert run

        Assert.NotNull(controller.Undo());
        Assert.Equal("ab", buffer.GetText());
        Assert.NotNull(controller.Undo());
        Assert.Equal("", buffer.GetText());
    }

    [Fact]
    public void NonAdjacentTyping_StartsANewGroup()
    {
        var (buffer, controller, _) = Create("hello world");

        controller.Apply(new Edit(TextPosition.Zero, "", "X"), EditKind.Typing, Caret(0, 0), Caret(0, 1));
        controller.Apply(new Edit(new TextPosition(0, 7), "", "Y"), EditKind.Typing, Caret(0, 7), Caret(0, 8));

        Assert.Equal("Xhello Yworld", buffer.GetText());
        Assert.Equal(2, controller.UndoDepth);
    }

    [Fact]
    public void ReplaceTyping_IsAtomic_NeitherJoinsNorAcceptsFollowers()
    {
        var (buffer, controller, _) = Create("abc");

        var at = Type(controller, TextPosition.Zero, "X");                 // insert run
        controller.Apply(new Edit(at, "a", "Y"), EditKind.Typing, new CaretState(at), Caret(0, 2)); // replace-typing
        Type(controller, new TextPosition(0, 2), "Z");                     // following typing

        Assert.Equal("XYZbc", buffer.GetText());
        Assert.Equal(3, controller.UndoDepth);
    }

    // ---- Depth cap and redo ---------------------------------------------------------------------

    [Fact]
    public void DepthCap_DiscardsOldestGroup()
    {
        var (buffer, controller, _) = Create(depthLimit: 4);

        var at = TextPosition.Zero;
        foreach (char ch in "abcdef")
        {
            at = Type(controller, at, ch.ToString());
            controller.SealGroup(); // six one-character groups
        }

        Assert.Equal(4, controller.UndoDepth);

        int undos = 0;
        while (controller.Undo() is not null)
            undos++;

        Assert.Equal(4, undos);
        Assert.Equal("ab", buffer.GetText()); // the two evicted groups' text survives undo-to-bottom
        Assert.False(controller.CanUndo);
    }

    [Fact]
    public void NewEdit_ClearsRedo()
    {
        var (buffer, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "a");
        controller.SealGroup();
        Type(controller, at, "b");
        Assert.NotNull(controller.Undo());
        Assert.True(controller.CanRedo);

        Type(controller, at, "c");

        Assert.False(controller.CanRedo);
        Assert.Null(controller.Redo());
        Assert.Equal("ac", buffer.GetText());
    }

    [Fact]
    public void UndoThenTyping_StartsANewGroup()
    {
        var (buffer, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        Assert.NotNull(controller.Undo());
        Assert.Equal("", buffer.GetText());

        Type(controller, TextPosition.Zero, "cd");
        Assert.Equal(1, controller.UndoDepth);
        Assert.NotNull(controller.Undo());
        Assert.Equal("", buffer.GetText());
    }

    [Fact]
    public void RedoneGroup_DoesNotAcceptCoalescing()
    {
        var (buffer, controller, _) = Create();

        var at = Type(controller, TextPosition.Zero, "ab");
        Assert.NotNull(controller.Undo());
        Assert.NotNull(controller.Redo());
        Assert.Equal("ab", buffer.GetText());

        Type(controller, at, "cd"); // splice-adjacent to the redone group, but it is closed

        Assert.Equal(2, controller.UndoDepth);
        Assert.NotNull(controller.Undo());
        Assert.Equal("ab", buffer.GetText());
    }

    [Fact]
    public void CoalescedRun_RedoRestoresTheLastEditsCaret()
    {
        var (buffer, controller, _) = Create();

        Type(controller, TextPosition.Zero, "abc");
        Assert.NotNull(controller.Undo());

        var redone = controller.Redo();
        Assert.Equal("abc", buffer.GetText());
        Assert.Equal(Caret(0, 3), redone); // the caret after the run's LAST keystroke
    }
}
