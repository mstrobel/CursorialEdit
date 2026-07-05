using Cursorial.Output.Capabilities;
using Cursorial.Terminal;
using Cursorial.UI.Testing;

namespace CursorialEdit.Tests.TestSupport;

/// <summary>
/// The shared §5.1 wire-preset plumbing for theory-driven suites: preset names travel through xunit
/// theory data (serializable), resolved to <see cref="TestCapabilities"/> here — one registry instead
/// of per-suite copies (review wave1-7).
/// </summary>
internal static class CapabilityPresets
{
    /// <summary>
    /// Composed preset names (§5.1's <c>with</c>-composition style): the stock presets leave
    /// <c>Output.Protocol</c> at <c>None</c>, but the real negotiator claims OSC 52 write for
    /// both the Kitty and Xterm families (<c>VtTerminalNegotiator.TerminalSupportsClipboardWrite</c>),
    /// so composing <c>ClipboardWrite</c> onto either preset is the honest picture of that wire —
    /// the WP9 suites that assert emitted OSC 52 bytes run under these.
    /// </summary>
    public const string KittyTruecolorClipboard = nameof(KittyTruecolorClipboard);

    /// <inheritdoc cref="KittyTruecolorClipboard"/>
    public const string Ansi16LegacyClipboard = nameof(Ansi16LegacyClipboard);

    /// <summary>
    /// The NoColor tier composed onto the Kitty wire (§5.1's <c>with (ColorDepth)</c>): full input and
    /// styling, but color collapsed to <c>Default</c> (<see cref="ColorCapabilities.None"/>, whose
    /// <c>Depth</c> is <see cref="ColorDepth.NoColor"/>). Render-affecting suites add it to assert the
    /// redundant non-color channel (§18.3) — e.g. selection degrading to <c>TextAttributes.Inverse</c>.
    /// </summary>
    public const string NoColor = nameof(NoColor);

    /// <summary>Both §5.1 wire presets — rendering/caret/input suites run under each.</summary>
    public static TheoryData<string> Both =>
        new() { nameof(TestCapabilities.KittyTruecolor), nameof(TestCapabilities.Ansi16Legacy) };

    /// <summary>Both wire presets with negotiated OSC 52 write composed on (see <see cref="KittyTruecolorClipboard"/>).</summary>
    public static TheoryData<string> BothWithClipboardWrite =>
        new() { KittyTruecolorClipboard, Ansi16LegacyClipboard };

    public static TerminalCapabilities Resolve(string preset) => preset switch
    {
        nameof(TestCapabilities.KittyTruecolor) => TestCapabilities.KittyTruecolor,
        nameof(TestCapabilities.Ansi16Legacy) => TestCapabilities.Ansi16Legacy,
        KittyTruecolorClipboard => WithClipboardWrite(TestCapabilities.KittyTruecolor),
        Ansi16LegacyClipboard => WithClipboardWrite(TestCapabilities.Ansi16Legacy),
        NoColor => WithNoColor(TestCapabilities.KittyTruecolor),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    private static TerminalCapabilities WithNoColor(TerminalCapabilities capabilities) =>
        capabilities with
        {
            Output = capabilities.Output with { Color = ColorCapabilities.None },
        };

    private static TerminalCapabilities WithClipboardWrite(TerminalCapabilities capabilities) =>
        capabilities with
        {
            Output = capabilities.Output with
            {
                Protocol = capabilities.Output.Protocol with { ClipboardWrite = true },
            },
        };
}
