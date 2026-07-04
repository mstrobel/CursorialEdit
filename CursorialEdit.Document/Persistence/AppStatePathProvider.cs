namespace CursorialEdit.Document.Persistence;

/// <summary>
/// Default <see cref="IAppStatePathProvider"/>: resolves the per-platform application state
/// directory and hands out its <c>journals</c> subdirectory. See the interface remarks for the
/// §3.2 resolution-8 split between app state (here) and user settings (the FW-A framework
/// options store under <c>~/.cursorial</c> — never this class's concern).
/// </summary>
/// <remarks>
/// Resolution per platform:
/// <list type="bullet">
///   <item><description>macOS — <c>~/Library/Application Support/CursorialEdit/journals</c>.</description></item>
///   <item><description>Linux (and other Unix) — <c>$XDG_STATE_HOME/CursorialEdit/journals</c>
///     when <c>XDG_STATE_HOME</c> is set to an absolute path (relative values are ignored per
///     the XDG base-directory spec), else <c>~/.local/state/CursorialEdit/journals</c>.</description></item>
///   <item><description>Windows — <c>%LOCALAPPDATA%\CursorialEdit\journals</c>.</description></item>
/// </list>
/// The directory is resolved fresh on every call and never created here — writers create it on
/// demand. M6.WP3 (<c>AppStatePaths</c> finalization) owns any final adjustment; consumers only
/// see the <see cref="IAppStatePathProvider"/> seam.
/// </remarks>
public sealed class AppStatePathProvider : IAppStatePathProvider
{
    private const string AppDirectoryName = "CursorialEdit";
    private const string JournalsDirectoryName = "journals";

    /// <inheritdoc/>
    public string GetJournalDirectory() => Path.Combine(GetAppStateDirectory(), JournalsDirectoryName);

    /// <summary>The platform application state root for CursorialEdit (no trailing subdirectory).</summary>
    internal static string GetAppStateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppDirectoryName);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", AppDirectoryName);

        string? xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        string stateRoot = !string.IsNullOrEmpty(xdgStateHome) && Path.IsPathRooted(xdgStateHome)
            ? xdgStateHome
            : Path.Combine(home, ".local", "state");

        return Path.Combine(stateRoot, AppDirectoryName);
    }
}
