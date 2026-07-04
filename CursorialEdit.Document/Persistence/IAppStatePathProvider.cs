namespace CursorialEdit.Document.Persistence;

/// <summary>
/// The journal-directory seam (implementation-plan §6 M1 risk 6): where autosave journals live.
/// M1 code depends on this interface only; <see cref="AppStatePathProvider"/> is the default
/// platform resolution, and M6.WP3 finalizes the decision behind the same seam.
/// </summary>
/// <remarks>
/// <b>The §3.2 resolution-8 split.</b> <i>User settings</i> are configuration and live in the
/// framework options store (FW-A, <c>~/.cursorial/</c> — global + per-app tri-state overlay);
/// <i>journals and recents are app state, not config</i>, and live in a per-platform application
/// state directory (macOS <c>~/Library/Application Support/CursorialEdit</c>, Linux
/// <c>$XDG_STATE_HOME</c> ?? <c>~/.local/state</c> + <c>/CursorialEdit</c>, Windows
/// <c>%LOCALAPPDATA%\CursorialEdit</c>). This interface hands out the <c>journals</c>
/// subdirectory of that state directory — never anything under <c>~/.cursorial</c>.
/// </remarks>
public interface IAppStatePathProvider
{
    /// <summary>
    /// Resolves the absolute directory that holds autosave journals. The directory is not
    /// required to exist — writers create it on demand — and resolution is re-queried per
    /// operation rather than cached, so a provider may legitimately fail one call and serve
    /// the next (the failure surface the autosave tests exercise).
    /// </summary>
    /// <returns>An absolute directory path.</returns>
    string GetJournalDirectory();
}
