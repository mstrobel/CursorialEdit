using Cursorial.UI.Bars;
using Cursorial.UI.Themes;

using CursorialEdit.Views;

namespace CursorialEdit.App;

/// <summary>
/// The editor operations that appear on MORE THAN ONE bar surface — the ribbon's Home/Clipboard group and the
/// right-click <see cref="EditorContextBar"/> both show Cut/Copy/Paste, and a future ribbon Format group will share
/// the Bold/Italic/Inline&#160;Code the context bar already carries. Each is created ONCE here as a shared
/// <see cref="BarCommand"/> instance so every control binding it reflects a single source of truth (icon, label,
/// gesture, and — once gating lands — enabled/checked state). Because one instance is bound by more than one control,
/// the icon MUST be an <see cref="IconCarrier"/> (a live <see cref="Controls.Icon"/> is a visual and can't be shared);
/// the shared <see cref="EditorRibbon"/> <c>Icon*</c> factories return carriers, which the theme templates into a
/// fresh Icon per host. Each command carries its full display metadata (<see cref="BarCommand.Text"/> with the
/// ribbon access-key literal, <see cref="BarCommand.Icon"/>, an optional <see cref="BarCommand.InputGestureText"/>,
/// and a <see cref="BarCommand.Description"/> for the hover SuperTip) and refocuses the editor after running, so a
/// command taken from any surface leaves focus on the document ("click a command, keep typing").
/// </summary>
public sealed class EditorCommands
{
    /// <summary>Builds the shared commands against <paramref name="editor"/> (the shell's persistent surface).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="editor"/> is <see langword="null"/>.</exception>
    public EditorCommands(EditorControl editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        // Bool-returning ops (Cut/Copy/Paste) are wrapped as `() => editor.Xxx()` — the result is discarded (a bar
        // command runs the op unconditionally, unlike the keybind which bubbles on a no-op). Every action refocuses.
        BarCommand Command(string text, IconCarrier icon, Action op, string? gesture, string description) =>
            new(() => { op(); editor.Focus(); })
            {
                Text = text,
                Icon = icon,
                InputGestureText = gesture,
                Description = description,
            };

        Cut = Command("_Cut", EditorRibbon.IconCut(), () => editor.Cut(), "Ctrl+X", "Cut the selection to the clipboard.");
        Copy = Command("C_opy", EditorRibbon.IconCopy(), () => editor.Copy(), "Ctrl+C", "Copy the selection to the clipboard.");
        Paste = Command("_Paste", EditorRibbon.IconPaste(), () => editor.Paste(), "Ctrl+V", "Paste the clipboard contents at the cursor.");

        // Bold/Italic/Inline Code have no keybinding yet (M4), so no gesture is advertised in the SuperTip.
        Bold = Command("_Bold", EditorRibbon.IconBold(), editor.Bold, gesture: null, "Bold the selected text.");
        Italic = Command("_Italic", EditorRibbon.IconItalic(), editor.Italic, gesture: null, "Italicize the selected text.");
        InlineCode = Command("Inline _Code", EditorRibbon.IconInlineCode(), editor.InlineCode, gesture: null, "Format the selection as inline code.");
    }

    /// <summary>Cut the selection to the clipboard (ribbon Home + context bar).</summary>
    public BarCommand Cut { get; }

    /// <summary>Copy the selection to the clipboard (ribbon Home + context bar).</summary>
    public BarCommand Copy { get; }

    /// <summary>Paste the clipboard contents at the cursor (ribbon Home + context bar).</summary>
    public BarCommand Paste { get; }

    /// <summary>Bold the selection (context bar; a future ribbon Format group).</summary>
    public BarCommand Bold { get; }

    /// <summary>Italicize the selection (context bar; a future ribbon Format group).</summary>
    public BarCommand Italic { get; }

    /// <summary>Format the selection as inline code (context bar; a future ribbon Format group).</summary>
    public BarCommand InlineCode { get; }
}
