using Cursorial.UI;
using Cursorial.UI.Bars;

using CursorialEdit.Document.Model;
using CursorialEdit.Views;

namespace CursorialEdit.App;

/// <summary>
/// The editor's Bars <see cref="Ribbon"/> (M5 skeleton): a Home / Table / View tab surface docked at the top
/// of the <see cref="EditorShell"/> that exposes the editor's many implemented-but-invisible operations as
/// buttons. Every button binds a <see cref="BarCommand"/> whose action calls a REAL operation on the
/// persistent <see cref="EditorControl"/> (the shell's <see cref="EditorShell.Editor"/>) — never a captured
/// caret, so the command survives a document reload (the caret is re-resolved inside <see cref="EditorControl"/>
/// at call time). After a command runs the editor is re-focused so typing continues ("click a button, keep
/// typing" — the bar model).
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive, non-focus-stealing.</b> The ribbon is a single focus scope reached by click/access-key; the
/// shell keeps the editor as the startup focus owner (see <see cref="EditorShell"/>), so the ribbon's presence
/// does not disturb typing.
/// </para>
/// <para>
/// <b>Checkable View toggles.</b> The Raw / Wrap / Truncate toggles carry a
/// <see cref="CheckableCommandParameter"/> the <see cref="BarToggleButton"/> reads on every command re-query,
/// so their checked state reflects the editor's live state. The Raw toggle additionally re-syncs from
/// <see cref="EditorControl.ViewModeChanged"/>, so a keyboard Ctrl+/ that flips the mode is reflected on the
/// ribbon too.
/// </para>
/// <para>
/// <b>FB-27 (deferred).</b> Context-gating the Table ops (greying them out when the caret is not in a table) is
/// the separate framework work item FB-27; the buttons are left always-enabled here — the underlying ops
/// already no-op safely outside a table.
/// </para>
/// <para>
/// <b>Toggle re-sync on document reload (deferred).</b> A fresh document resets the bridge to its defaults
/// (Formatted / wrap-on / overflow-wrap). The Raw toggle re-syncs from <see cref="EditorControl.ViewModeChanged"/>,
/// but the Wrap / Truncate toggles are seeded once at construction; their seeds match the bridge defaults, and
/// M5 has no UI that reloads the document after the ribbon is live (file-open lands in M6), so no stale checked
/// state is reachable yet. When M6 wires a ribbon-driven Open/New, re-sync all three toggles on the document swap.
/// </para>
/// </remarks>
public sealed class EditorRibbon : Ribbon
{
    private readonly EditorControl _editor;

    // The Raw toggle's command + checked-state carrier — held so ViewModeChanged (a keyboard-driven flip) can
    // re-sync the toggle without a ribbon interaction.
    private readonly CheckableCommandParameter _rawChecked;
    private readonly BarCommand _rawCommand;

    private bool _viewModeSubscribed;

    /// <summary>Builds the ribbon over <paramref name="editor"/> (the shell's persistent editor surface).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="editor"/> is <see langword="null"/>.</exception>
    public EditorRibbon(EditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        _rawChecked = new CheckableCommandParameter(_editor.ViewMode == ViewMode.Raw);
        _rawCommand = new BarCommand(() => Run(_editor.ToggleViewMode))
        {
            Text = "Raw",
            InputGestureText = "Ctrl+/",
            IsCheckable = true,
        };

        Items.Add(BuildHomeTab());
        Items.Add(BuildTableTab());
        Items.Add(BuildViewTab());
    }

    // Opt into the base Ribbon control theme: control themes resolve exact-key (by the concrete type) through the
    // theme-contribution tier, so without this an EditorRibbon subclass would render unthemed (0×0). The GalleryRibbon
    // precedent — WPF DefaultStyleKey parity.
    protected override object ControlThemeKey => typeof(Ribbon);

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        if (_viewModeSubscribed)
            return;

        _editor.ViewModeChanged += OnEditorViewModeChanged;
        _viewModeSubscribed = true;
        OnEditorViewModeChanged(); // reflect the current mode on attach (a Raw document shows the toggle checked)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_viewModeSubscribed)
        {
            _editor.ViewModeChanged -= OnEditorViewModeChanged;
            _viewModeSubscribed = false;
        }

        base.OnDetachedFromTree(in e);
    }

    // The Raw toggle mirrors EditorControl.ViewMode; re-query the command so the bound BarToggleButton re-reads
    // the checked state (whether the flip came from the ribbon or the keyboard).
    private void OnEditorViewModeChanged()
    {
        _rawChecked.IsChecked = _editor.ViewMode == ViewMode.Raw;
        _rawCommand.RaiseCanExecuteChanged();
    }

    // ───────────────────────────── Home ─────────────────────────────

    private RibbonTab BuildHomeTab()
    {
        var paste = Button("Paste", () => _editor.Paste(), "Ctrl+V");
        SetButtonSize(paste, RibbonButtonSize.Large); // the signature large glyph-over-label button

        return Tab("Home",
            Group("Clipboard",
                Button("Cut", () => _editor.Cut(), "Ctrl+X"),
                Button("Copy", () => _editor.Copy(), "Ctrl+C"),
                paste),
            Group("Undo",
                Button("Undo", () => _editor.Undo(), "Ctrl+Z"),
                Button("Redo", () => _editor.Redo(), "Ctrl+Y")),
            Group("Edit",
                Button("Select All", _editor.SelectAll, "Ctrl+A")));
    }

    // ───────────────────────────── Table ─────────────────────────────

    private RibbonTab BuildTableTab()
    {
        // TODO(FB-27): gate every Table command on caret-in-table (pass a canExecute reading a
        // coerced "caret in a table" state) once that framework coercion lands. Until then the buttons
        // stay always-enabled; the ops themselves no-op safely off a table.
        return Tab("Table",
            Group("Insert",
                Button("Row Above", _editor.TableInsertRowAbove, "Alt+↑"),
                Button("Row Below", _editor.TableInsertRowBelow, "Alt+↓"),
                Button("Column Left", _editor.TableInsertColumnLeft),
                Button("Column Right", _editor.TableInsertColumnRight)),
            Group("Delete",
                Button("Delete Row", _editor.TableDeleteRow),
                Button("Delete Column", _editor.TableDeleteColumn),
                Button("Delete Table", _editor.TableDelete)),
            Group("Move",
                Button("Row Up", _editor.TableMoveRowUp),
                Button("Row Down", _editor.TableMoveRowDown),
                Button("Column Left", _editor.TableMoveColumnLeft),
                Button("Column Right", _editor.TableMoveColumnRight)),
            Group("Cells",
                // Alignment: three actions calling TableSetColumnAlignment. A reflecting toggle-set (checked =
                // the caret column's current alignment) is a follow-up — it reads caret-in-table state, FB-27's job.
                Button("Align Left", () => _editor.TableSetColumnAlignment(ColumnAlignment.Left)),
                Button("Align Center", () => _editor.TableSetColumnAlignment(ColumnAlignment.Center)),
                Button("Align Right", () => _editor.TableSetColumnAlignment(ColumnAlignment.Right)),
                new BarSeparator(),
                Button("Clear Cell", _editor.TableClearCell)));
    }

    // ───────────────────────────── View ─────────────────────────────

    private RibbonTab BuildViewTab()
    {
        var raw = new BarToggleButton { Content = "Raw", Command = _rawCommand, CommandParameter = _rawChecked };

        var wrapChecked = new CheckableCommandParameter(_editor.EditWrapEnabled);
        var wrap = Toggle("Wrap", wrapChecked, () =>
        {
            _editor.EditWrapEnabled = !_editor.EditWrapEnabled;
            wrapChecked.IsChecked = _editor.EditWrapEnabled; // re-read the real state (the command owns the checked bit)
        });

        var truncateChecked = new CheckableCommandParameter(_editor.OverflowMode == TableOverflow.Truncate);
        var truncate = Toggle("Truncate", truncateChecked, () =>
        {
            _editor.OverflowMode = _editor.OverflowMode == TableOverflow.Truncate ? TableOverflow.Wrap : TableOverflow.Truncate;
            truncateChecked.IsChecked = _editor.OverflowMode == TableOverflow.Truncate;
        });

        return Tab("View",
            Group("Mode", raw),
            Group("Wrap", wrap),
            Group("Overflow", truncate));
    }

    // ───────────────────────────── construction helpers ─────────────────────────────

    // Runs a ribbon command's op, then returns focus to the editor so typing continues immediately (the bar
    // "click a button, keep typing" model; also makes direct command invocation in tests keep the editor focused).
    private void Run(Action op)
    {
        op();
        _editor.Focus();
    }

    private BarButton Button(string text, Action op, string? gesture = null)
    {
        // Bool-returning ops (Copy/Cut/Paste/Undo/Redo) are wrapped by the caller as `() => _editor.Xxx()`
        // — the result is discarded (the ribbon runs the op unconditionally, unlike the keybind which bubbles).
        var command = new BarCommand(() => Run(op)) { Text = text, InputGestureText = gesture };
        return new BarButton { Content = text, Command = command };
    }

    private BarToggleButton Toggle(string text, CheckableCommandParameter checkedState, Action toggle, string? gesture = null)
    {
        var command = new BarCommand(() => Run(toggle)) { Text = text, InputGestureText = gesture, IsCheckable = true };
        return new BarToggleButton { Content = text, Command = command, CommandParameter = checkedState };
    }

    private static RibbonGroup Group(string header, params UIElement[] items)
    {
        var group = new RibbonGroup { Header = header };
        foreach (var item in items)
            group.Items.Add(item);
        return group;
    }

    private static RibbonTab Tab(string header, params RibbonGroup[] groups)
    {
        var tab = new RibbonTab { Header = header };
        foreach (var group in groups)
            tab.Groups.Add(group);
        return tab;
    }
}
