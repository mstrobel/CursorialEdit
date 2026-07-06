using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.App;

namespace CursorialEdit.Tests.App;

/// <summary>
/// The wave-4 cross-seam integration: <see cref="EditorShell.WireDocument"/> now routes through
/// <c>EditorControl.AttachDocument</c>, so the SHELL-hosted surface must carry the full WP8 editing
/// path (typing, caret publication, undo) and the WP11 dirty tracking off the same controller —
/// end-to-end through the real shell, not the test harness wiring.
/// </summary>
public sealed class ShellEditingIntegrationTests
{
    [Fact]
    public void ShellHostedEditor_TypesUndoes_AndTracksDirty()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(50, 10) });

        var shell = new EditorShell();
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());

        // The real lifecycle path (WireDocument alone is the pipeline seam — dirty tracking and
        // autosave attach via NewDocument/OpenFile).
        shell.NewDocument(host.Time);
        Assert.True(host.RunUntilIdle());
        Assert.False(shell.IsDirty);

        // Typing flows through the attached controller into the shell-hosted surface. The M5 ribbon docks at
        // the top, so the document's first content row is EditorTop frame rows down (not frame row 0).
        int editorTop = TestSupport.ShellLayout.EditorTopRow(shell);
        host.SendText("Zed");
        Assert.True(host.RunUntilIdle());
        Assert.StartsWith("Zed", host.GetRowText(editorTop), StringComparison.Ordinal);
        Assert.Equal("Zed", shell.Document!.GetText());

        // Terminal caret follows the typed text (content col 3, row 0 of the editor area).
        Assert.Equal(3, host.FrameBuffer.CursorColumn);

        // Dirty dot appears on edit (WP11 tracking off the same controller instance).
        Assert.True(shell.IsDirty);

        // Undo through the shell-hosted chord path restores text and caret (dirty stays until save).
        host.SendKey(Key.Character, KeyModifiers.Control, text: "z");
        Assert.True(host.RunUntilIdle());
        Assert.Equal(string.Empty, host.GetRowText(editorTop).TrimEnd());
        Assert.Equal(string.Empty, shell.Document!.GetText());
        Assert.Equal(0, host.FrameBuffer.CursorColumn);
        Assert.True(shell.IsDirty);
    }
}
