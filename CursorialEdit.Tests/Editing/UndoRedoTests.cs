using Cursorial.Input;

using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M1.WP8 — the undo/redo keybindings end to end (spec §3.3): Ctrl+Z applies the returned
/// <c>CaretState</c> (caret <b>and</b> selection restored, selection re-painted), Ctrl+Y and
/// Ctrl+Shift+Z both redo. FB-14 note: on the legacy wire a Ctrl+Shift+letter chord arrives with
/// Shift dropped — Ctrl+Shift+Z fires as Ctrl+Z — so Ctrl+Y is the legacy-safe redo; the harness
/// injects decoded key events (modifiers explicit), so both arms are testable on any preset.
/// </summary>
public sealed class UndoRedoTests
{
    [Fact]
    public void CtrlZ_UndoesSelectionReplace_RestoresAndRepaintsTheSelection()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Click(8, 0, clickCount: 2); // select "world"
        var paintedFill = harness.BackgroundAt(6, 0);
        Assert.NotEqual(paintedFill, harness.BackgroundAt(0, 0));

        harness.Type("X");
        Assert.Equal("hello X", harness.Buffer.GetText());
        harness.AssertCaret(0, 7);

        harness.Chord('z', KeyModifiers.Control);

        Assert.Equal("hello world", harness.Buffer.GetText());
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6));
        Assert.Equal(paintedFill, harness.BackgroundAt(6, 0)); // the restored selection re-painted
        Assert.Equal((11, 0), harness.Cursor);
    }

    [Fact]
    public void CtrlY_Redoes_CaretLandsOnTheRecordedAfterState()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Click(8, 0, clickCount: 2);
        harness.Type("X");
        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("hello world", harness.Buffer.GetText());

        harness.Chord('y', KeyModifiers.Control);

        Assert.Equal("hello X", harness.Buffer.GetText());
        harness.AssertCaret(0, 7); // the edit's landing, no selection
        Assert.Equal((7, 0), harness.Cursor);
    }

    [Fact]
    public void CtrlShiftZ_Redoes_OnWiresThatDeliverTheShift()
    {
        using var harness = EditingHarness.Create(string.Empty);

        harness.Type("abc");
        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("", harness.Buffer.GetText());

        harness.Chord('z', KeyModifiers.Control | KeyModifiers.Shift);

        Assert.Equal("abc", harness.Buffer.GetText());
        harness.AssertCaret(0, 3);
    }

    [Fact]
    public void UndoRedo_MoveTheCaretAcrossLines_AndScrollFollows()
    {
        var document = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line{i:D2}"));
        using var harness = EditingHarness.Create(document, columns: 30, rows: 10);

        harness.Key(Key.End, KeyModifiers.Control);
        harness.Type("!"); // edit at the bottom of the document
        Assert.Equal(20, harness.ScrollViewer.VerticalOffset);

        harness.Key(Key.Home, KeyModifiers.Control); // scroll back to the top (also seals the group)
        Assert.Equal(0, harness.ScrollViewer.VerticalOffset);

        harness.Chord('z', KeyModifiers.Control); // undo lands the caret at the edit site …
        harness.AssertCaret(29, 6);
        Assert.True(harness.Host.FrameBuffer.CursorVisible); // … and the viewport followed it
        Assert.Equal(20, harness.ScrollViewer.VerticalOffset);
        Assert.Equal("line29", harness.Host.GetRowText(9)[..6]);
    }
}
