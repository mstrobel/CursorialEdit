using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.App;

/// <summary>
/// M4 — the editor's right-click <see cref="MiniToolbar"/> (<see cref="EditorContextBar"/>): a horizontal
/// light-dismiss strip of icon-only Cut/Copy/Paste + Bold/Italic/InlineCode buttons attached to the editor surface.
/// Verifies the item set + icon-only shape, that every glyph is a width-1 text-presentation grapheme (never a
/// 2-wide emoji sprite), that a right-click over the editor opens the strip at the pointer without disturbing the
/// live selection the format commands act on, that the buttons run the REAL editor operations, and that the editor
/// is re-focused after an action ("right-click, pick a command, keep typing").
/// </summary>
public sealed class ContextBarTests
{
    private static (UITestHost Host, EditorShell Shell) Shell(
        string markdown, int columns = 48, int rows = 24)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(nameof(TestCapabilities.KittyTruecolor)),
        });

        var shell = new EditorShell();
        shell.WireDocument(markdown, host.Time);
        shell.Editor.Clipboard = new InternalClipboard(); // isolate from the process-wide Shared store (parallel tests)
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");
        return (host, shell);
    }

    // ───────────────────────────── structure: the item set, icon-only ─────────────────────────────

    [Fact]
    public void ContextBar_HasCutCopyPaste_Separator_BoldItalicInlineCode_IconOnly()
    {
        var (host, shell) = Shell("hello world");
        using var _ = host;

        var bar = shell.ContextBar;

        // Cut · Copy · Paste │ Bold · Italic · Inline Code — 6 buttons split by one separator.
        Assert.Equal(new[] { "Cut", "Copy", "Paste", "Bold", "Italic", "Inline Code" },
            bar.Items.OfType<BarButton>().Select(b => ((BarCommand)b.Command!).Text!));
        Assert.Single(bar.Items.OfType<BarSeparator>());
        Assert.IsType<BarSeparator>(bar.Items[3]); // the separator sits between the clipboard and format clusters

        // Icon-only: every button carries a tiered Icon on the Icon tier and an explicit EMPTY Content (no label).
        foreach (var button in bar.Items.OfType<BarButton>())
        {
            Assert.IsType<Icon>(button.Icon);
            Assert.Equal(string.Empty, button.Content);
        }
    }

    [Fact] // Every strip icon is a tiered Nerd Font Icon: one PUA Glyph codepoint over a width-1, no-VS16 Text floor
           // (the same rule the ribbon holds — via the shared IconAssert guard).
    public void EveryContextBarButton_HasATieredNerdFontIconWithAWidthOneTextFloor()
    {
        var (host, shell) = Shell("hello world");
        using var _ = host;

        int buttons = 0;
        foreach (var button in shell.ContextBar.Items.OfType<BarButton>())
        {
            buttons++;
            var icon = Assert.IsType<Icon>(button.Icon);
            TestSupport.IconAssert.NerdFontOverWidthOneFloor(icon);
        }

        Assert.Equal(6, buttons);
    }

    // ───────────────────────────── right-click opens; selection survives ─────────────────────────────

    [Fact]
    public void RightClickOverEditor_OpensTheStrip_WithoutClearingTheSelection()
    {
        var (host, shell) = Shell("hello world");
        using var _ = host;

        int top = TestSupport.ShellLayout.EditorTopRow(shell);
        host.SendClick(8, top, MouseButton.Left, clickCount: 2); // double-click "world"
        Assert.True(host.RunUntilIdle());
        var caret = shell.Editor.DocumentCaretPart!;
        Assert.Equal("world", caret.SelectedText());

        host.SendClick(8, top, MouseButton.Right); // right-click over the editor
        Assert.True(host.RunUntilIdle());

        Assert.True(shell.ContextBar.IsOpen);            // the strip opened at the pointer
        Assert.Equal("world", caret.SelectedText());     // …and the live selection is untouched (the commands act on it)
    }

    // ───────────────────────────── the buttons run the real editor ops ─────────────────────────────

    [Fact]
    public void CutCommand_RemovesTheSelection_AndRefocusesTheEditor()
    {
        var (host, shell) = Shell("hello world");
        using var _ = host;

        int top = TestSupport.ShellLayout.EditorTopRow(shell);
        host.SendClick(8, top, MouseButton.Left, clickCount: 2); // select "world"
        Assert.True(host.RunUntilIdle());

        Invoke(shell.ContextBar, "Cut");
        Assert.True(host.RunUntilIdle());

        Assert.Equal("hello ", shell.Document!.GetText());   // Cut ran through the real EditorControl.Cut
        Assert.True(shell.Editor.IsKeyboardFocusWithin, "the editor is re-focused after a strip command");
    }

    [Fact]
    public void BoldCommand_WrapsTheSelection_ThroughTheEditor()
    {
        var (host, shell) = Shell("hello world");
        using var _ = host;

        int top = TestSupport.ShellLayout.EditorTopRow(shell);
        host.SendClick(8, top, MouseButton.Left, clickCount: 2); // select "world"
        Assert.True(host.RunUntilIdle());

        Invoke(shell.ContextBar, "Bold");
        Assert.True(host.RunUntilIdle());

        Assert.Equal("hello **world**", shell.Document!.GetText()); // the new Bold command spliced the marks
        Assert.True(shell.Editor.IsKeyboardFocusWithin);
    }

    // ───────────────────────────── focus returns even when the strip held it ─────────────────────────────

    [Fact] // A strip command re-focuses the editor: driven over a bare, initially-UNfocused editor, so the flip
           // false→true proves EditorContextBar.Run's Focus() call (not merely that focus was never lost).
    public void AfterAnAction_FocusReturnsToTheEditor()
    {
        using var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(40, 12),
            Capabilities = TestSupport.CapabilityPresets.Resolve(nameof(TestCapabilities.KittyTruecolor)),
        });

        var buffer = new DocumentBuffer("hello world");
        var controller = new EditController(buffer, host.Time);
        var producer = new MarkdigBlockProducer(controller);
        var bridge = new MarkdownViewBridge(buffer, producer);
        var editor = new EditorControl { Clipboard = new InternalClipboard() };
        editor.AttachDocument(controller, bridge);

        var contextBar = new EditorContextBar(editor);
        MiniToolbar.SetBar(editor, contextBar.Bar);

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle());
        Assert.False(editor.IsKeyboardFocusWithin, "the editor is not focused yet");

        Invoke(contextBar.Bar, "Copy"); // Copy no-ops with no selection, but Run still re-focuses
        Assert.True(host.RunUntilIdle());

        Assert.True(editor.IsKeyboardFocusWithin, "the strip command focused the editor");
    }

    // ───────────────────────────── typing still works with the strip attached ─────────────────────────────

    [Fact]
    public void EditorStillAutoFocusesAndTypes_WithTheContextBarAttached()
    {
        var (host, shell) = Shell("hello");
        using var _ = host;

        Assert.True(shell.Editor.IsKeyboardFocusWithin, "the editor auto-focused despite the attached strip");
        host.SendText("Z");
        Assert.True(host.RunUntilIdle());
        Assert.Equal("Zhello", shell.Document!.GetText());
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static BarButton FindButton(MiniToolbar bar, string label)
        => bar.Items.OfType<BarButton>().Single(b => b.Command is BarCommand c && c.Text == label);

    private static void Invoke(MiniToolbar bar, string label)
    {
        var button = FindButton(bar, label);
        button.Command!.Execute(button.CommandParameter);
    }
}
