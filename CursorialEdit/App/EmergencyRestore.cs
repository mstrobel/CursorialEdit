using System.Buffers;

using Cursorial.Output;

namespace CursorialEdit.App;

/// <summary>
/// The FB-4 workaround's byte payload: the terminal-sanity restore sequence a signal-killed
/// full-screen app must emit so the user's shell is not stranded on the alternate screen with a
/// hidden cursor. The framework's own signal path restores termios and the negotiator's opt-ins
/// but <b>not</b> the alt screen, cursor visibility, autowrap (DECAWM), or the cursor color
/// (<c>EmergencyRestoreBytes</c> missing — integration notes §2); this fills exactly that gap
/// until FB-4 lands upstream.
/// </summary>
public static class EmergencyRestore
{
    /// <summary>
    /// Writes the emergency restore sequence, in order: re-enable autowrap (DECSET 7,
    /// <c>CSI ? 7 h</c>), reset the cursor color (OSC 112, <c>OSC 112 ST</c>), leave the
    /// alternate screen (DECRST 1049, <c>CSI ? 1049 l</c>), show the cursor (DECSET 25,
    /// <c>CSI ? 25 h</c>), reset SGR state (<c>CSI 0 m</c>). Pure — the bytes go into
    /// <paramref name="writer"/>; no I/O happens here.
    /// </summary>
    /// <remarks>
    /// The sequences are produced by the framework's own writers (<see cref="ScreenWriter"/>,
    /// <see cref="PaletteWriter"/>, <see cref="CursorWriter"/>, <see cref="SgrEncoder"/>) rather
    /// than hand-rolled literals, so this stays byte-identical to what the framework emits on
    /// its clean-teardown path. The order mirrors that path too: autowrap re-enable and the
    /// cursor-color reset land before the alt-screen leave (as <c>FrameRenderer.Close</c> and
    /// the frame loop's teardown do — the renderer disables DECAWM every frame and the frame
    /// loop recolors the cursor via OSC 12 at startup, and only that clean path restores
    /// either), then leave the alt screen (restoring the saved main-screen cursor position),
    /// make the cursor visible, and clear any live SGR attributes. All five are idempotent and
    /// harmless if the app never entered the alternate screen or recolored the cursor.
    /// </remarks>
    /// <param name="writer">The destination buffer writer.</param>
    public static void WriteRestoreBytes(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ScreenWriter.WriteEnableAutowrap(writer);       // CSI ? 7 h
        PaletteWriter.WriteResetCursor(writer);         // OSC 112 ST
        ScreenWriter.WriteLeaveAlternateScreen(writer); // CSI ? 1049 l
        CursorWriter.WriteShow(writer);                 // CSI ? 25 h
        SgrEncoder.WriteReset(writer);                  // CSI 0 m
    }
}
