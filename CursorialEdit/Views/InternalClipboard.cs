namespace CursorialEdit.Views;

/// <summary>
/// The app-internal clipboard store (M1.WP9) — the read side of the FB-3 split. The terminal
/// clipboard is write-only at Cursorial 0.3.1 (<c>IClipboardService</c> emits OSC 52 sets, but
/// <c>TryGetTextAsync</c> always completes <see langword="null"/> — no terminal family
/// negotiates the OSC 52 read query), so Copy/Cut write <b>both</b> sinks — OSC 52 toward the
/// user's system clipboard, and this store — and Ctrl+V reads back from this store alone.
/// In-app copy→paste therefore round-trips byte-exact regardless of what the terminal does
/// with the OSC 52 write (most gate it behind a prompt; none acknowledge it), while
/// <b>external</b> content necessarily arrives through the terminal's own paste keybinding
/// (bracketed paste → <c>TextInput.FromPaste</c>): this store cannot see the system clipboard,
/// and Ctrl+V cannot paste content copied outside the app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (decided).</b> A process-wide default instance (<see cref="Shared"/>) rather than
/// an app-scoped service: a clipboard is one-per-user-session by nature, every document
/// surface in the process must share it (M7's split view pastes what either pane cut), and M1
/// has no service registry to hang an app-scoped instance on — while the
/// <see cref="EditorControl.Clipboard"/> property that consumes it stays injectable, so tests
/// isolate with a fresh instance and never race on the shared one. This is deliberately app
/// code, not an FB-12-style promotable component: the framework-side fix is FB-3's proposal
/// (a service-level fallback store + the OSC 52 read leg), tracked separately.
/// </para>
/// <para>
/// The store holds the last <i>copy/cut</i> text only — a bracketed paste does not mirror
/// into it (its content already lives on the system clipboard the terminal pasted from).
/// UI-thread-only, like the input paths that use it; process-lifetime, never persisted.
/// </para>
/// </remarks>
public sealed class InternalClipboard
{
    /// <summary>The process-wide store every editor surface shares by default.</summary>
    public static InternalClipboard Shared { get; } = new();

    /// <summary>The last copied/cut text, or <see langword="null"/> when nothing has been copied yet.</summary>
    public string? Text { get; private set; }

    /// <summary>Stores <paramref name="text"/> as the current clipboard content (the copy/cut write side).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }
}
