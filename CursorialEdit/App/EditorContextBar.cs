using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

using CursorialEdit.Views;

namespace CursorialEdit.App;

/// <summary>
/// The editor's right-click <see cref="MiniToolbar"/> (the bars guide's Mini Toolbar): a horizontal, light-dismiss
/// strip of small icon-only bar buttons — <b>Cut · Copy · Paste │ Bold · Italic · Inline Code</b> — opened at the
/// pointer by a right-click over the <see cref="EditorControl"/> surface. Attach it with
/// <see cref="MiniToolbar.SetBar"/> (the shell does so on the persistent editor); the strip wins the right-click
/// over any co-present <c>ContextMenu</c> (it marks the event handled) and, being a separate pointer gesture,
/// never disturbs the caret or the live selection the format commands operate on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same commands, same icons — self-describing.</b> Every button binds a <see cref="BarCommand"/> that carries the
/// action (a REAL operation on the persistent <see cref="EditorControl"/> — the same <see cref="EditorControl.Cut"/>/
/// <see cref="EditorControl.Copy"/>/<see cref="EditorControl.Paste"/> the ribbon and keyboard use, plus
/// <see cref="EditorControl.Bold"/>/<see cref="EditorControl.Italic"/>/<see cref="EditorControl.InlineCode"/>), a
/// label, and a tiered <see cref="IconCarrier"/> from the shared <see cref="EditorRibbon"/> <c>Icon*</c> factories
/// (one icon vocabulary — a Nerd Font <see cref="Icon"/> over a width-1 Unicode floor). The button auto-fills its
/// Content/Icon and the hover SuperTip from the command. Verified by <c>ContextBarTests</c>.
/// </para>
/// <para>
/// <b>Icon-only via Compact density.</b> The strip shows icons only because a <see cref="MiniToolbar"/> is
/// automatically Compact (<see cref="Ribbon.IsDensityCompact"/>), which <b>hides</b> the auto-filled label rather than
/// discarding it — so the command keeps its label for identity, the SuperTip, and a future labeled layout. (No empty
/// <see cref="ContentControl.Content"/> is pinned; that would throw the label away.)
/// </para>
/// <para>
/// <b>Keep typing.</b> After a command runs the editor is re-focused (<see cref="Run"/>), so an action taken from
/// the strip leaves focus back on the document surface — "right-click, pick a command, keep typing."
/// </para>
/// </remarks>
public sealed class EditorContextBar
{
    private readonly EditorControl _editor;

    /// <summary>Builds the right-click strip over <paramref name="editor"/> (the shell's persistent editor surface).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="editor"/> is <see langword="null"/>.</exception>
    public EditorContextBar(EditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        Bar = new MiniToolbar();
        Bar.Items.Add(IconButton(EditorRibbon.IconCut(), "Cut", () => _editor.Cut()));
        Bar.Items.Add(IconButton(EditorRibbon.IconCopy(), "Copy", () => _editor.Copy()));
        Bar.Items.Add(IconButton(EditorRibbon.IconPaste(), "Paste", () => _editor.Paste()));
        Bar.Items.Add(new BarSeparator());
        Bar.Items.Add(IconButton(EditorRibbon.IconBold(), "Bold", _editor.Bold));
        Bar.Items.Add(IconButton(EditorRibbon.IconItalic(), "Italic", _editor.Italic));
        Bar.Items.Add(IconButton(EditorRibbon.IconInlineCode(), "Inline Code", _editor.InlineCode));
    }

    /// <summary>The strip itself — attach it with <see cref="MiniToolbar.SetBar"/> on the right-click target.</summary>
    public MiniToolbar Bar { get; }

    // A bar button, self-describing through its BarCommand: the command carries the tiered IconCarrier (the button
    // auto-fills its Icon, which the theme templates into an Icon per host) and the display label (auto-filled into
    // Content and the hover SuperTip). The strip renders icon-only because a MiniToolbar is automatically Compact
    // (Ribbon.IsDensityCompact) — the density HIDES the label rather than discarding it, so a future labeled layout
    // still has it. Do NOT pin an empty Content, which would throw the label away.
    private BarButton IconButton(IconCarrier icon, string text, Action op)
    {
        // Bool-returning Cut/Copy/Paste are wrapped by the caller as `() => _editor.Xxx()` (the result is discarded —
        // the strip runs the op unconditionally, like the ribbon does, unlike the keybind which bubbles on no-op).
        var command = new BarCommand(() => Run(op)) { Text = text, Icon = icon };
        return new BarButton { Command = command };
    }

    // Run the op, then return focus to the editor so typing continues immediately (the "keep typing" bar model);
    // focusing the editor also light-dismisses the strip (its popup closes when focus leaves it).
    private void Run(Action op)
    {
        op();
        _editor.Focus();
    }
}
