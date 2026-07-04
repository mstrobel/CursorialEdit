using System.Text;

namespace CursorialEdit.Tests.Clipboard;

/// <summary>
/// OSC 52 byte-level assertions for the WP9 suites: the framework's <c>ClipboardWriter</c> emits
/// <c>ESC ] 52 ; c ; base64(UTF-8 text) ESC \</c> (system-clipboard target, standard padded
/// base64), so the expected envelope is reconstructible exactly and searched for in the wire
/// bytes a settle emitted (<c>EditingHarness.ChordCollectingBytes</c>).
/// </summary>
internal static class Osc52
{
    /// <summary>The <c>ESC ] 52 ;</c> envelope opening — presence anywhere means "an OSC 52 write happened".</summary>
    public static ReadOnlySpan<byte> Prefix => "\x1b]52;"u8;

    /// <summary>The exact system-clipboard set envelope for <paramref name="text"/>.</summary>
    public static byte[] Envelope(string text) =>
        Encoding.ASCII.GetBytes("\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\x1b\\");

    /// <summary>Asserts <paramref name="emitted"/> carries the exact OSC 52 set of <paramref name="text"/>.</summary>
    public static void AssertWritten(byte[] emitted, string text) =>
        Assert.True(
            emitted.AsSpan().IndexOf(Envelope(text)) >= 0,
            $"the emitted wire bytes carry no OSC 52 set of {Show(text)} " +
            $"(looked for base64 payload \"{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\")");

    /// <summary>Asserts <paramref name="emitted"/> carries no OSC 52 write at all.</summary>
    public static void AssertNothingWritten(byte[] emitted) =>
        Assert.True(
            emitted.AsSpan().IndexOf(Prefix) < 0,
            "the emitted wire bytes unexpectedly carry an OSC 52 envelope");

    private static string Show(string text) =>
        "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
