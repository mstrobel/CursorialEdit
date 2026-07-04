using Cursorial.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Clipboard;

/// <summary>
/// M1.WP9 — copy (spec §3.4): the M1-gate <c>SelectionTests.Copy_YieldsExactSourceRange</c> plus
/// the FB-3 dual-sink contract. Copy serializes the selection's exact source range — interior
/// CRLF/LF seams byte-exact — and writes <b>both</b> sinks: the OSC 52 set on the wire (asserted
/// against the synthetic terminal's emitted bytes; base64 payload reconstructed exactly) and the
/// app-internal store (the read side — terminal clipboard reads don't exist at 0.3.1, FB-3).
/// Includes the verified Ctrl+C-is-not-SIGINT wire truth (see
/// <c>Copy_RawCtrlCByte_ArrivesAsKeyEvent_NotSigint</c>).
/// </summary>
public sealed class SelectionTests
{
    /// <summary>Both wire presets with OSC 52 write negotiated (the honest picture of both families).</summary>
    public static TheoryData<string> ClipboardPresets => TestSupport.CapabilityPresets.BothWithClipboardWrite;

    /// <summary>Mixed endings: line 0 CRLF, line 1 LF, line 2 CRLF — copies must cross both seam shapes.</summary>
    private const string MixedEndingsDoc = "alpha\r\nbravo\ncharlie\r\ndelta";

    [Theory]
    [MemberData(nameof(ClipboardPresets))]
    public void Copy_YieldsExactSourceRange(string preset)
    {
        using var harness = EditingHarness.Create(MixedEndingsDoc, preset, captureFrameBytes: true);

        // Select (0,2) → (2,4): crosses a CRLF seam AND an LF seam.
        harness.Drag(2, 0, 4, 2);
        harness.AssertCaret(2, 4, anchor: new TextPosition(0, 2));

        var emitted = harness.ChordCollectingBytes('c', KeyModifiers.Control);

        // The exact source range — terminators as their literal per-line characters — reached
        // BOTH sinks: the internal store (the FB-3 read side) and the OSC 52 wire write.
        Assert.Equal("pha\r\nbravo\nchar", harness.Clipboard.Text);
        Osc52.AssertWritten(emitted, "pha\r\nbravo\nchar");

        // Copy never mutates: document, caret, selection, and history are all untouched.
        Assert.Equal(MixedEndingsDoc, harness.Buffer.GetText());
        harness.AssertCaret(2, 4, anchor: new TextPosition(0, 2));
        Assert.Equal(0, harness.Controller.UndoDepth);
    }

    [Fact]
    public void Copy_WithoutSelection_IsANoOp_NothingWrittenAnywhere()
    {
        // M1 copy is selection-only (line-copy conventions are later milestones'); the chord
        // bubbles unconsumed, TextBox parity.
        using var harness = EditingHarness.Create(
            "hello", TestSupport.CapabilityPresets.KittyTruecolorClipboard, captureFrameBytes: true);

        var emitted = harness.ChordCollectingBytes('c', KeyModifiers.Control);

        Assert.Null(harness.Clipboard.Text);
        Osc52.AssertNothingWritten(emitted);
        Assert.Equal("hello", harness.Buffer.GetText());
    }

    [Fact]
    public void Copy_WhenTheWireNeverNegotiatedClipboardWrite_StillFillsTheStore()
    {
        // FB-3 in miniature: the OSC 52 write is gated on the negotiated ClipboardWrite (the
        // service no-ops without it — stock presets leave it off), but the in-app round-trip
        // must work on ANY wire, so the store fills regardless and nothing hits the wire.
        using var harness = EditingHarness.Create("hello world", captureFrameBytes: true);

        harness.Drag(0, 0, 5, 0);
        var emitted = harness.ChordCollectingBytes('c', KeyModifiers.Control);

        Assert.Equal("hello", harness.Clipboard.Text);
        Osc52.AssertNothingWritten(emitted);
    }

    [Fact]
    public void Copy_RawCtrlCByte_ArrivesAsKeyEvent_NotSigint()
    {
        // Wire truth (verified in the framework source, confirmed here empirically): the
        // session applies `stty … -isig …` (PosixStdioTransports.Open), so the TTY's ISIG
        // processing is OFF for the session's lifetime — pressing Ctrl+C puts the raw byte
        // 0x03 on the wire, and the VT interpreter's C0 map (0x01–0x1A → Ctrl+letter) decodes
        // it as (Character, "c", Control): the copy chord, not a signal. The app's
        // SignalRestore SIGINT path can only fire from an outside `kill -INT`. This test sends
        // the bare byte through the real VtInputDevice and lands in the copy handler.
        using var harness = EditingHarness.Create("hello world");

        harness.Chord('a', KeyModifiers.Control); // select all
        harness.Bytes([0x03]);

        Assert.Equal("hello world", harness.Clipboard.Text);
        Assert.Equal("hello world", harness.Buffer.GetText()); // and nothing was disturbed
    }

    [Fact]
    public void Copy_CtrlInsert_IsTheTextBoxParityAlias()
    {
        using var harness = EditingHarness.Create("foo bar");

        harness.Drag(4, 0, 7, 0);
        harness.Key(Key.Insert, KeyModifiers.Control);

        Assert.Equal("bar", harness.Clipboard.Text);
    }
}
