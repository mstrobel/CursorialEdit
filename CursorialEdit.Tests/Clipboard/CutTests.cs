using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Clipboard;

/// <summary>
/// M1.WP9 — cut: copy (both sinks — OSC 52 wire write + internal store) plus deletion of the
/// selection through <c>EditController.Apply</c> as its <b>own undo unit</b>
/// (<c>EditKind.Structural</c> — atomic, so neighboring typing never folds into it), with one
/// undo restoring the text <i>and</i> the selection from the recorded before-state.
/// </summary>
public sealed class CutTests
{
    public static TheoryData<string> ClipboardPresets => TestSupport.CapabilityPresets.BothWithClipboardWrite;

    private const string MixedEndingsDoc = "alpha\r\nbravo\ncharlie";

    [Theory]
    [MemberData(nameof(ClipboardPresets))]
    public void Cut_WritesBothSinks_AndDeletesTheSelection_AsOneUndoUnit(string preset)
    {
        using var harness = EditingHarness.Create(MixedEndingsDoc, preset, captureFrameBytes: true);

        harness.Drag(2, 0, 3, 1); // (0,2) → (1,3): "pha\r\nbra", across the CRLF seam
        var emitted = harness.ChordCollectingBytes('x', KeyModifiers.Control);

        Assert.Equal("pha\r\nbra", harness.Clipboard.Text);
        Osc52.AssertWritten(emitted, "pha\r\nbra");

        Assert.Equal("alvo\ncharlie", harness.Buffer.GetText());
        harness.AssertCaret(0, 2); // collapsed at the cut point
        Assert.Equal(1, harness.Controller.UndoDepth); // ONE unit: the delete (copy records nothing)
    }

    [Fact]
    public void Cut_SingleUndo_RestoresTextAndSelection()
    {
        using var harness = EditingHarness.Create(MixedEndingsDoc);

        harness.Drag(2, 0, 3, 1);
        harness.Chord('x', KeyModifiers.Control);

        harness.Chord('z', KeyModifiers.Control); // one undo — the whole cut

        Assert.Equal(MixedEndingsDoc, harness.Buffer.GetText()); // byte-exact, CRLF seam included
        harness.AssertCaret(1, 3, anchor: new TextPosition(0, 2)); // the selection came back
        harness.AssertSelectionPainted(row: 0, fromColumn: 2, toColumn: 5, plainColumn: 0);
        Assert.Equal(0, harness.Controller.UndoDepth);
    }

    [Fact]
    public void Cut_IsAtomic_AdjacentTypingNeverCoalescesIntoIt()
    {
        // The EditKind choice made observable: a Typing pure deletion would open a coalescing
        // delete-run (a Backspace right after the cut would fold into its group); Structural
        // keeps the cut its own unit in both directions.
        using var harness = EditingHarness.Create("hello world");

        harness.Drag(5, 0, 11, 0); // " world"
        harness.Chord('x', KeyModifiers.Control);
        harness.Key(Key.Backspace); // deletes 'o'

        Assert.Equal("hell", harness.Buffer.GetText());
        Assert.Equal(2, harness.Controller.UndoDepth); // cut + backspace stayed separate

        harness.Chord('z', KeyModifiers.Control); // undoes ONLY the backspace
        Assert.Equal("hello", harness.Buffer.GetText());

        harness.Chord('z', KeyModifiers.Control); // undoes the cut
        Assert.Equal("hello world", harness.Buffer.GetText());
    }

    [Fact]
    public void Cut_WithoutSelection_IsANoOp()
    {
        using var harness = EditingHarness.Create("hello");

        harness.Click(2, 0);
        harness.Chord('x', KeyModifiers.Control);

        Assert.Null(harness.Clipboard.Text);
        Assert.Equal("hello", harness.Buffer.GetText());
        Assert.Equal(0, harness.Controller.UndoDepth);
    }

    [Fact]
    public void Cut_ShiftDelete_IsTheTextBoxParityAlias()
    {
        using var harness = EditingHarness.Create("foo bar");

        harness.Drag(0, 0, 4, 0); // "foo "
        harness.Key(Key.Delete, KeyModifiers.Shift);

        Assert.Equal("foo ", harness.Clipboard.Text);
        Assert.Equal("bar", harness.Buffer.GetText());
    }
}
