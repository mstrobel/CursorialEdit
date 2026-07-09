using System.Text;

using Cursorial.Input;

using CursorialEdit.Tests.Editing;
using CursorialEdit.Tests.TestSupport;

namespace CursorialEdit.Tests.Clipboard;

/// <summary>
/// The FB-3 read-side closure: when the terminal negotiated OSC 52 <b>read</b>, Ctrl+V pulls the SYSTEM
/// clipboard via the query/response round-trip (<c>EditorControl.Paste</c> → <c>IClipboardService.TryGetTextAsync</c>),
/// <b>preferring</b> it over the app-internal store and falling back to the store when the read yields nothing
/// (denied / unsupported / timeout / empty). The read is async, so the chord is consumed immediately and the
/// paste lands when the terminal's reply arrives — injected here as raw wire bytes (the <c>ClipboardReadTests</c>
/// reply pattern), which the real <c>VtInputDevice</c> surfaces as a clipboard device response.
/// </summary>
public sealed class Osc52ReadPasteTests
{
    // An OSC 52 clipboard response carrying <paramref name="text"/> — "ESC ] 52 ; c ; <base64> ST"; base64
    // encoded here so the payload can't drift out of sync with the asserted text.
    private static byte[] ClipboardReply(string text) =>
        Encoding.ASCII.GetBytes($"\x1b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\x1b\\");

    // Ctrl+V that fires the read but does NOT settle: TryGetTextAsync starts the timeout UITimer, so the loop
    // is not idle until a reply cancels it — a single RunFrame processes the key (registering the response sink
    // and emitting the query) and leaves the read in flight for the caller to answer with Bytes(reply).
    private static void PressCtrlV_ReadInFlight(EditingHarness h)
    {
        h.Host.SendKey(Key.Character, KeyModifiers.Control, "v");
        h.Host.RunFrame();
    }

    [Fact] // read succeeds ⇒ the SYSTEM clipboard wins over the in-app store (system-preferred precedence)
    public void CtrlV_WithClipboardRead_PastesSystemClipboard_OverTheInternalStore()
    {
        using var h = EditingHarness.Create("", preset: CapabilityPresets.KittyTruecolorClipboardRead, columns: 40);
        h.Clipboard.SetText("INTERNAL"); // an in-app copy already sits in the store

        PressCtrlV_ReadInFlight(h);                 // fires the OSC 52 read; the chord is consumed now
        Assert.Equal("", h.Buffer.GetText());       // nothing pasted yet — the read is in flight

        h.Bytes(ClipboardReply("SYSTEM"));          // the terminal answers with its clipboard
        Assert.Equal("SYSTEM", h.Buffer.GetText()); // system clipboard won, NOT the "INTERNAL" store
    }

    [Fact] // read denied / unsupported (the terminal echoes '?') ⇒ fall back to the in-app store
    public void CtrlV_WithClipboardRead_DeniedRead_FallsBackToTheInternalStore()
    {
        using var h = EditingHarness.Create("", preset: CapabilityPresets.KittyTruecolorClipboardRead, columns: 40);
        h.Clipboard.SetText("INTERNAL");

        PressCtrlV_ReadInFlight(h);
        h.Bytes("\x1b]52;c;?\x1b\\"u8);             // a '?' data field ⇒ the read completes null

        Assert.Equal("INTERNAL", h.Buffer.GetText()); // fell back to the store
    }

    [Fact] // OSC 52 carries no request id, so a reply completes EVERY pending read — the in-flight guard must
           // keep a held/double-tapped Ctrl+V from fanning one clipboard value into duplicate pastes.
    public void CtrlV_WithClipboardRead_SecondChordWhileInFlight_PastesOnce()
    {
        using var h = EditingHarness.Create("", preset: CapabilityPresets.KittyTruecolorClipboardRead, columns: 40);

        PressCtrlV_ReadInFlight(h); // starts the read (_pasteReadInFlight = true)
        PressCtrlV_ReadInFlight(h); // coalesced onto the in-flight read — no second read started
        h.Bytes(ClipboardReply("X"));       // one reply completes the single pending read

        Assert.Equal("X", h.Buffer.GetText()); // pasted ONCE, not "XX"
    }
}
