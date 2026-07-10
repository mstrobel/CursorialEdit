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
/// <b>Shared commands.</b> Every button binds a SHARED <see cref="BarCommand"/> from <see cref="EditorCommands"/> —
/// the SAME instances the ribbon's Home/Clipboard group shows for Cut/Copy/Paste — so both surfaces read one source
/// of truth (icon, label, gesture, and, once gating lands, enabled state). Because a command is bound by more than
/// one control, its icon is a shareable <see cref="IconCarrier"/> (a live <see cref="Icon"/> is a visual and cannot
/// be); the button auto-fills its Content/Icon and the hover SuperTip from the command. Each command runs a REAL
/// <see cref="EditorControl"/> operation and refocuses the editor after, so an action taken here leaves focus back on
/// the document surface — "right-click, pick a command, keep typing."
/// </para>
/// <para>
/// <b>Icon-only via Compact density.</b> The strip shows icons only because a <see cref="MiniToolbar"/> is
/// automatically Compact (<see cref="Ribbon.IsDensityCompact"/>), which <b>hides</b> the auto-filled label rather than
/// discarding it — so the command keeps its label for identity, the SuperTip, and a future labeled layout. (No empty
/// <see cref="ContentControl.Content"/> is pinned; that would throw the label away.)
/// </para>
/// </remarks>
public sealed class EditorContextBar
{
    /// <summary>Builds the right-click strip from the shared <paramref name="commands"/> (the same instances the ribbon binds).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> is <see langword="null"/>.</exception>
    public EditorContextBar(EditorCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        Bar = new MiniToolbar();
        Bar.Items.Add(new BarButton { Command = commands.Cut });
        Bar.Items.Add(new BarButton { Command = commands.Copy });
        Bar.Items.Add(new BarButton { Command = commands.Paste });
        Bar.Items.Add(new BarSeparator()); // splits the clipboard and format clusters
        Bar.Items.Add(new BarButton { Command = commands.Bold });
        Bar.Items.Add(new BarButton { Command = commands.Italic });
        Bar.Items.Add(new BarButton { Command = commands.InlineCode });
    }

    /// <summary>The strip itself — attach it with <see cref="MiniToolbar.SetBar"/> on the right-click target.</summary>
    public MiniToolbar Bar { get; }
}
