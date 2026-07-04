using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Clipboard;

/// <summary>
/// M1.WP9 — paste: two inbound sources, one funnel (<c>DocumentCaret.Paste</c> →
/// <c>EditController.Apply</c> with <c>EditKind.Paste</c>, one literal splice, its own undo
/// unit, selection replaced, caret at the end). (a) Bracketed paste — the real
/// <c>ESC [ 200~ … ESC [ 201~</c> envelope through the <c>VtInputDevice</c> →
/// <c>TextInput.FromPaste</c> — is the terminal's own paste keybinding and the <b>only</b> way
/// external clipboard content reaches the app (FB-3). (b) Ctrl+V pastes from the app-internal
/// store — it round-trips in-app copy/cut on any wire but <b>cannot see content copied outside
/// the app</b>, because OSC 52 reads don't exist at 0.3.1. All paste is literal in M1 — no
/// reparse (M4 owns smart paste).
/// </summary>
public sealed class PasteTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── Ctrl+V from the internal store ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CtrlV_PastesTheStore_MultiLine_OneUndoUnit_CaretAtEnd(string preset)
    {
        using var harness = EditingHarness.Create("abcd", preset);

        harness.Click(2, 0);
        harness.Clipboard.SetText("one\r\ntwo\nthree"); // as a cut/copy stored it: endings literal

        harness.Chord('v', KeyModifiers.Control);

        Assert.Equal("abone\r\ntwo\nthreecd", harness.Buffer.GetText()); // literal, endings preserved
        harness.AssertCaret(2, 5); // the end of the inserted text
        Assert.Equal(1, harness.Controller.UndoDepth); // ONE unit

        harness.Chord('z', KeyModifiers.Control); // one undo removes the whole paste
        Assert.Equal("abcd", harness.Buffer.GetText());
        harness.AssertCaret(0, 2);
    }

    [Fact]
    public void CtrlV_ReplacesTheSelection_AsOneUnit()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Drag(6, 0, 11, 0); // "world"
        harness.Clipboard.SetText("there");

        harness.Chord('v', KeyModifiers.Control);

        Assert.Equal("hello there", harness.Buffer.GetText());
        harness.AssertCaret(0, 11);
        Assert.Equal(1, harness.Controller.UndoDepth);

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("hello world", harness.Buffer.GetText());
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6)); // the replaced selection came back
    }

    [Fact]
    public void CtrlV_EmptyStore_IsANoOp_ExternalClipboardIsInvisible()
    {
        // The FB-3 boundary made explicit: nothing was copied IN-APP, so Ctrl+V has nothing —
        // whatever sits on the system clipboard is unreadable (no OSC 52 read query exists at
        // 0.3.1); the user's path for external content is the terminal's own paste keybinding,
        // which arrives as the bracketed path below. The chord bubbles unconsumed.
        using var harness = EditingHarness.Create("hello");

        harness.Click(2, 0);
        harness.Chord('v', KeyModifiers.Control);

        Assert.Equal("hello", harness.Buffer.GetText());
        Assert.Equal(0, harness.Controller.UndoDepth);
    }

    [Fact]
    public void CopyThenPaste_RoundTripsThroughTheStore_ByteExact()
    {
        // FB-3's whole point: in-app copy → paste round-trips regardless of the terminal,
        // because the store is the read side of the dual write.
        using var harness = EditingHarness.Create("alpha\r\nbravo\ncharlie");

        harness.Drag(2, 0, 3, 1); // "pha\r\nbra"
        harness.Chord('c', KeyModifiers.Control);
        harness.Key(Key.End, KeyModifiers.Control); // document end — collapses the selection

        harness.Chord('v', KeyModifiers.Control);

        Assert.Equal("alpha\r\nbravo\ncharliepha\r\nbra", harness.Buffer.GetText());
        harness.AssertCaret(3, 3);
    }

    [Fact]
    public void ShiftInsert_IsTheTextBoxParityAlias()
    {
        using var harness = EditingHarness.Create("ab");

        harness.Key(Key.End); // caret to (0,2)
        harness.Clipboard.SetText("!");
        harness.Key(Key.Insert, KeyModifiers.Shift);

        Assert.Equal("ab!", harness.Buffer.GetText());
    }

    // ───────────────────────────── bracketed paste (the external path) ─────────────────────────────

    [Fact]
    public void BracketedPaste_OneSplice_OneUndoUnit_LiteralPayload_CaretAtEnd()
    {
        using var harness = EditingHarness.Create("start\nend");
        harness.Click(5, 0);

        var splices = 0;
        harness.Controller.Changed += _ => splices++;

        // The real envelope through the VtInputDevice; the payload carries a CRLF and markdown
        // markers that must stay inert (literal paste — M1 never reparses).
        harness.Bytes("\x1b[200~ pasted\r\nliteral **not** reparsed\x1b[201~"u8);

        Assert.Equal(1, splices); // the whole payload applied as ONE splice
        Assert.Equal(1, harness.Controller.UndoDepth); // …recorded as ONE undo unit
        Assert.Equal("start pasted\r\nliteral **not** reparsed\nend", harness.Buffer.GetText());
        harness.AssertCaret(1, "literal **not** reparsed".Length);

        // The store holds copy/cut text only — a terminal paste does not mirror into it (its
        // content already lives on the system clipboard the terminal pasted from).
        Assert.Null(harness.Clipboard.Text);

        harness.Chord('z', KeyModifiers.Control); // one undo removes the whole paste
        Assert.Equal("start\nend", harness.Buffer.GetText());
        harness.AssertCaret(0, 5);
    }

    [Fact]
    public void BracketedPaste_ReplacesTheSelection_AsOneUnit()
    {
        using var harness = EditingHarness.Create("hello world");

        harness.Drag(6, 0, 11, 0); // "world"
        harness.Bytes("\x1b[200~mars\x1b[201~"u8);

        Assert.Equal("hello mars", harness.Buffer.GetText());
        Assert.Equal(1, harness.Controller.UndoDepth);

        harness.Chord('z', KeyModifiers.Control);
        Assert.Equal("hello world", harness.Buffer.GetText());
        harness.AssertCaret(0, 11, anchor: new TextPosition(0, 6));
    }
}
