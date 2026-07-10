using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Themes;

using CursorialEdit.Document.Model;
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

        // A format TOGGLE: checkable, reflecting the caret's live inline-format state on every re-query (checked =
        // the caret sits strictly inside the construct — source-strict, like the alignment radio-set), and gated on
        // CanFormatInline (greys in tables, where the raw-mark splice is guarded off). Execute is the caret toggle:
        // unwrap the covering construct when active, wrap the selection when not. The parameter is per-control (a
        // ribbon toggle carries a CheckableCommandParameter; the MiniToolbar's plain buttons carry none) — the
        // pattern-match lets one command serve both.
        BarCommand Format(string text, IconCarrier icon, Action op, InlineRunKind kind, string description) =>
            new(_ => { op(); editor.Focus(); },
                p =>
                {
                    if (p is CheckableCommandParameter checkable)
                        checkable.IsChecked = editor.IsCaretFormatActive(kind);
                    return editor.CanFormatInline;
                })
            {
                Text = text,
                Icon = icon,
                IsCheckable = true,
                Description = description,
            };

        Cut = Command("_Cut", EditorRibbon.IconCut(), () => editor.Cut(), "Ctrl+X", "Cut the selection to the clipboard.");
        Copy = Command("C_opy", EditorRibbon.IconCopy(), () => editor.Copy(), "Ctrl+C", "Copy the selection to the clipboard.");
        Paste = Command("_Paste", EditorRibbon.IconPaste(), () => editor.Paste(), "Ctrl+V", "Paste the clipboard contents at the cursor.");

        // Bold/Italic/Inline Code have no keybinding yet (M4), so no gesture is advertised in the SuperTip.
        Bold = Format("_Bold", EditorRibbon.IconBold(), editor.Bold, InlineRunKind.Strong, "Toggle bold on the selection.");
        Italic = Format("_Italic", EditorRibbon.IconItalic(), editor.Italic, InlineRunKind.Emphasis, "Toggle italic on the selection.");
        InlineCode = Format("Inline _Code", EditorRibbon.IconInlineCode(), editor.InlineCode, InlineRunKind.Code, "Toggle inline code on the selection.");

        CaretGated = [Bold, Italic, InlineCode];
    }

    /// <summary>Cut the selection to the clipboard (ribbon Home + context bar).</summary>
    public BarCommand Cut { get; }

    /// <summary>Copy the selection to the clipboard (ribbon Home + context bar).</summary>
    public BarCommand Copy { get; }

    /// <summary>Paste the clipboard contents at the cursor (ribbon Home + context bar).</summary>
    public BarCommand Paste { get; }

    /// <summary>Toggle bold at the caret/selection (ribbon Home Format group + context bar) — checkable, reflecting.</summary>
    public BarCommand Bold { get; }

    /// <summary>Toggle italic at the caret/selection (ribbon Home Format group + context bar) — checkable, reflecting.</summary>
    public BarCommand Italic { get; }

    /// <summary>Toggle inline code at the caret/selection (ribbon Home Format group + context bar) — checkable, reflecting.</summary>
    public BarCommand InlineCode { get; }

    /// <summary>
    /// The shared commands whose <c>canExecute</c> reads live caret state (the format toggles) — the ribbon
    /// enlists these into its re-query batch (<c>EditorControl.CaretUpdated</c> → <c>RaiseCanExecuteChanged</c>),
    /// so every bound control (ribbon toggle, MiniToolbar button) re-reads enabled + checked per caret publish.
    /// </summary>
    public IReadOnlyList<BarCommand> CaretGated { get; }
}
