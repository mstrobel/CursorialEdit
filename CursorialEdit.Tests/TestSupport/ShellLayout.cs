using CursorialEdit.App;

namespace CursorialEdit.Tests.TestSupport;

/// <summary>
/// Helpers for reading an <see cref="EditorShell"/>-hosted surface now that the M5 ribbon docks at the top:
/// the ribbon occupies the first few frame rows, so a test that reads the editor's document content at
/// absolute frame rows must offset by <see cref="EditorTopRow"/> (the frame row where the document content
/// begins, below the ribbon). Content read <i>dynamically</i> (searching for a row by text) needs no offset —
/// it finds the content wherever it renders.
/// </summary>
internal static class ShellLayout
{
    /// <summary>The frame row where the shell's editor document content begins — below the docked ribbon.</summary>
    public static int EditorTopRow(EditorShell shell)
        => shell.Editor.DocumentPanelPart!.TranslateToScreen(0, 0).Row;
}
