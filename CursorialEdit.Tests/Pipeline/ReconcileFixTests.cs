using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;

namespace CursorialEdit.Tests.Pipeline;

/// <summary>
/// M2.WP7b review fixes on the production reconcile/caret path:
/// (1) a same-id block whose heading LEVEL changed re-renders at the NEW level's color (SetContent now
/// refreshes the level, not just text/inlines); (2) a click on a non-active line of the active block is
/// NOT offset by the active line's horizontal slide; (3) an edit that changes a block's line count
/// refreshes the panel's heights so blocks below reflow.
/// </summary>
public sealed class ReconcileFixTests
{
    private static ShellFixture CreateShell(string markdown, int columns = 40, int rows = 24)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(columns, rows) });
        var shell = new EditorShell();
        shell.WireDocument(markdown, host.Time);
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());
        shell.Editor.Focus();
        Assert.True(host.RunUntilIdle());
        return new ShellFixture(host, shell);
    }

    private sealed record ShellFixture(UITestHost Host, EditorShell Shell) : IDisposable
    {
        // The M5 ribbon docks at the shell's top, so the document content starts EditorTop frame rows down.
        public int EditorTop => TestSupport.ShellLayout.EditorTopRow(Shell);
        public string Row(int row) => Host.GetRowText(row + EditorTop).TrimEnd();
        public void Dispose() => Host.Dispose();
    }

    [Fact]
    public void ChangingHeadingLevel_ReRendersAtTheNewLevelColor()
    {
        // Heading is on line 2 (inactive — caret sits on line 0), so it renders formatted at its level color.
        using var h = CreateShell("intro\n\n## Section");
        Assert.Equal("Section", h.Row(2));
        Assert.Equal(Colors.LightCyan, h.Host.GetCell(0, 2 + h.EditorTop).Style.Foreground); // H2

        // Insert a "#" at the heading's start → "### Section" (same block id, reported Changed), caret stays
        // on line 0 so the heading remains inactive/formatted.
        var at = new TextPosition(2, 0);
        h.Shell.Controller!.Apply(
            new Edit(at, string.Empty, "#"), EditKind.Typing,
            new CaretState(new TextPosition(0, 0)), new CaretState(new TextPosition(0, 0)));
        Assert.True(h.Host.RunUntilIdle());

        Assert.Equal("Section", h.Row(2));
        Assert.Equal(Colors.LightGreen, h.Host.GetCell(0, 2 + h.EditorTop).Style.Foreground); // H3, not the stale H2 LightCyan
    }

    [Fact]
    public void ClickingANonActiveLineOfTheActiveBlock_IsNotOffsetByTheSlide()
    {
        // One code block whose line 1 is far wider than the 40-col viewport, and a short line 2.
        var wide = new string('x', 60) + "END";
        using var h = CreateShell("```\n" + wide + "\nhi\n```", columns: 40);

        // Land the caret on the wide line and press End → the active line slides right (slide >> 2).
        h.Host.SendKey(Key.DownArrow); // line 0 (fence) → line 1 (wide)
        h.Host.SendKey(Key.End);
        Assert.True(h.Host.RunUntilIdle());

        // Click column 1 of the SHORT line (content row 2, "hi"). It is not the slid active row, so the caret
        // must land on 'i' (col 1) — not be pushed by the wide line's slide to the clamped line end (col 2).
        // The click is a FRAME coordinate, so add the ribbon offset to reach the editor's content row 2.
        h.Host.SendClick(1, 2 + h.EditorTop);
        Assert.True(h.Host.RunUntilIdle());

        Assert.True(h.Host.FrameBuffer.CursorVisible);
        Assert.Equal(1, h.Host.FrameBuffer.CursorColumn);
    }

    [Fact]
    public void AddingALineToABlock_ReflowsTheBlocksBelow()
    {
        using var h = CreateShell("one\n\ntwo\n\nMARKER");
        // Rows: B0 "one"(0,1) · B1 "two"(2,3) · B2 "MARKER"(4). Find MARKER's row.
        int before = Enumerable.Range(0, 24).First(r => h.Row(r) == "MARKER");

        // Insert a hard line break into B1's paragraph so it grows by a line — everything below shifts down.
        var at = new TextPosition(2, 3);
        h.Shell.Controller!.Apply(
            new Edit(at, string.Empty, "\nx"), EditKind.Typing,
            new CaretState(new TextPosition(0, 0)), new CaretState(new TextPosition(0, 0)));
        Assert.True(h.Host.RunUntilIdle());

        int after = Enumerable.Range(0, 24).First(r => h.Row(r) == "MARKER");
        Assert.True(after > before, $"MARKER should move down after the block grew (was {before}, now {after})");
    }
}
