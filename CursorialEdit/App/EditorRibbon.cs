using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;

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
/// <b>Glyph icons.</b> Every button carries a monochrome, <i>text-presentation</i> glyph on its
/// <see cref="BarButton.Icon"/> tier (the image tier is left null, per the design). Each glyph is width-1 with
/// no emoji-presentation selector (no VS16, Emoji=No) — see the <c>Icon*</c> constants — so it renders as a
/// predictable single terminal cell and never as a 2-wide color-emoji sprite. Verified by
/// <c>RibbonTests.EveryRibbonButton_HasAWidthOneTextGlyphIcon</c>.
/// </para>
/// <para>
/// <b>KeyTips / access keys.</b> Every button folds an access-key literal into its <see cref="ContentControl.Content"/>
/// (e.g. <c>"_Paste"</c>), which both underlines the mnemonic and registers the Alt+letter accelerator with the
/// <c>AccessKeyManager</c>; every tab and group carries an explicit <see cref="KeyTip.Key"/>. The keys are assigned
/// collision-free within each drill scope (tabs; groups within a tab; controls within a group). The app enables the
/// Bars KeyTip overlay (<c>UIApplication.EnableKeyTips</c> in <c>Program</c>), so Alt reveals the badges and the
/// tab → group → control drill activates each command.
/// </para>
/// <para>
/// <b>Checkable View toggles.</b> The Raw / Wrap toggles carry a <see cref="CheckableCommandParameter"/> the
/// <see cref="BarToggleButton"/> reads on every command re-query, so their checked state reflects the editor's live
/// state. The Raw toggle additionally re-syncs from <see cref="EditorControl.ViewModeChanged"/>, so a keyboard Ctrl+/
/// that flips the mode is reflected on the ribbon too.
/// </para>
/// <para>
/// <b>Overflow segmented control.</b> The View tab's Overflow group is a two-state segmented control — two
/// mutually-exclusive toggles, <c>Wrap</c> and <c>Truncate</c>, reflecting and setting
/// <see cref="EditorControl.OverflowMode"/> (the automatic column-window horizontal scroll is orthogonal, not a
/// choice here). Selecting one sets the mode and re-syncs both toggles, so exactly one is checked; re-selecting the
/// active choice is a no-op (radio semantics, never a toggle-off).
/// </para>
/// <para>
/// <b>FB-27 (deferred).</b> Context-gating the Table ops (greying them out when the caret is not in a table) is
/// the separate framework work item FB-27; the buttons are left always-enabled here — the underlying ops
/// already no-op safely outside a table.
/// </para>
/// <para>
/// <b>Toggle re-sync on document reload (deferred).</b> A fresh document resets the bridge to its defaults
/// (Formatted / wrap-on / overflow-wrap). The Raw toggle re-syncs from <see cref="EditorControl.ViewModeChanged"/>,
/// but the Wrap toggle and the Overflow segmented control are seeded once at construction; their seeds match the
/// bridge defaults, and M5 has no UI that reloads the document after the ribbon is live (file-open lands in M6), so
/// no stale checked state is reachable yet. When M6 wires a ribbon-driven Open/New, re-sync them on the document swap.
/// </para>
/// </remarks>
public sealed class EditorRibbon : Ribbon
{
    // ───────────────────────────── icon glyphs ─────────────────────────────
    // Monochrome, text-presentation glyphs — each is a SINGLE width-1 grapheme with NO emoji-presentation
    // (no VS16, Emoji=No, GraphemeWidth.CodepointWidth == 1), so it renders as predictable 1-cell text and never
    // as a 2-wide color-emoji sprite (which we diagnosed bleeding over popups). The codepoint is spelled out in
    // each trailing comment; RibbonTests re-verifies width-1/no-VS16 for every one so a bad glyph fails the suite.
    // Cut/Copy/Paste are `internal` so the right-click MiniToolbar (EditorContextBar) reuses the SAME glyph
    // vocabulary — one source of truth for the shared clipboard icons across the ribbon and the mini toolbar.
    internal const string IconCut = "✁";           // U+2701 ✁ upper-blade scissors
    internal const string IconCopy = "⧉";          // U+29C9 ⧉ two joined squares (duplicate)
    internal const string IconPaste = "▤";         // U+25A4 ▤ square with horizontal fill (clipboard)

    // Inline-format glyphs (M4 slice) — shared with the MiniToolbar today, and the glyphs the ribbon will use when
    // it surfaces Bold/Italic/InlineCode later. Width-1 text-presentation like the rest (no VS16); the deliberate
    // avoidance of the math-alphanumeric 𝐁/𝐼 (which some terminals render 2 cells wide) is why these are symbols.
    internal const string IconBold = "✱";          // U+2731 ✱ heavy asterisk (heavy weight ⇒ bold; the ** marker)
    internal const string IconItalic = "⟋";        // U+27CB ⟋ rising diagonal (a slant ⇒ italic)
    internal const string IconInlineCode = "`";    // U+0060 ` grave accent — the literal inline-code marker
    private const string IconUndo = "↶";           // U+21B6 ↶ anticlockwise top semicircle arrow
    private const string IconRedo = "↷";           // U+21B7 ↷ clockwise top semicircle arrow
    private const string IconSelectAll = "⬚";      // U+2B1A ⬚ dotted square (selection marquee)
    private const string IconInsertRowAbove = "↥"; // U+21A5 ↥ upwards arrow from bar
    private const string IconInsertRowBelow = "↧"; // U+21A7 ↧ downwards arrow from bar
    private const string IconInsertColLeft = "↤";  // U+21A4 ↤ leftwards arrow from bar
    private const string IconInsertColRight = "↦"; // U+21A6 ↦ rightwards arrow from bar
    private const string IconDeleteRow = "⊖";      // U+2296 ⊖ circled minus
    private const string IconDeleteCol = "⊘";      // U+2298 ⊘ circled division slash
    private const string IconDeleteTable = "⊗";    // U+2297 ⊗ circled times
    private const string IconMoveRowUp = "↑";      // U+2191 ↑ upwards arrow
    private const string IconMoveRowDown = "↓";    // U+2193 ↓ downwards arrow
    private const string IconMoveColLeft = "←";    // U+2190 ← leftwards arrow
    private const string IconMoveColRight = "→";   // U+2192 → rightwards arrow
    private const string IconAlignLeft = "⇤";      // U+21E4 ⇤ leftwards arrow to bar
    private const string IconAlignCenter = "↹";    // U+21B9 ↹ opposing arrows to a central bar (centered)
    private const string IconAlignRight = "⇥";     // U+21E5 ⇥ rightwards arrow to bar
    private const string IconClearCell = "∅";      // U+2205 ∅ empty set (clear to empty)
    private const string IconRaw = "⌗";            // U+2317 ⌗ viewdata square (markdown source)
    private const string IconWrap = "↵";           // U+21B5 ↵ downwards arrow with corner leftwards (wrap/return)
    private const string IconTruncate = "…";       // U+2026 … horizontal ellipsis (the truncation marker)

    private readonly EditorControl _editor;

    // The Raw toggle's command + checked-state carrier — held so ViewModeChanged (a keyboard-driven flip) can
    // re-sync the toggle without a ribbon interaction.
    private readonly CheckableCommandParameter _rawChecked;
    private readonly BarCommand _rawCommand;

    // The Overflow segmented control's two commands + checked-state carriers — held so selecting either choice can
    // re-sync BOTH toggles (mutual exclusion) from the single EditorControl.OverflowMode. Assigned in BuildViewTab,
    // which the constructor calls before anything can reach the setters below.
    private CheckableCommandParameter _overflowWrapChecked = null!;
    private CheckableCommandParameter _overflowTruncateChecked = null!;
    private BarCommand _overflowWrapCommand = null!;
    private BarCommand _overflowTruncateCommand = null!;

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
        var paste = Button("_Paste", IconPaste, () => _editor.Paste(), "Ctrl+V");
        SetButtonSize(paste, RibbonButtonSize.Large); // the signature large glyph-over-label button

        return Tab("Home", "H",
            Group("Clipboard", "C",
                Button("_Cut", IconCut, () => _editor.Cut(), "Ctrl+X"),
                Button("C_opy", IconCopy, () => _editor.Copy(), "Ctrl+C"),
                paste),
            Group("Undo", "U",
                Button("_Undo", IconUndo, () => _editor.Undo(), "Ctrl+Z"),
                Button("_Redo", IconRedo, () => _editor.Redo(), "Ctrl+Y")),
            Group("Edit", "E",
                Button("_Select All", IconSelectAll, _editor.SelectAll, "Ctrl+A")));
    }

    // ───────────────────────────── Table ─────────────────────────────

    private RibbonTab BuildTableTab()
    {
        // TODO(FB-27): gate every Table command on caret-in-table (pass a canExecute reading a
        // coerced "caret in a table" state) once that framework coercion lands. Until then the buttons
        // stay always-enabled; the ops themselves no-op safely off a table.
        return Tab("Table", "T",
            Group("Insert", "I",
                Button("Row _Above", IconInsertRowAbove, _editor.TableInsertRowAbove, "Alt+↑"),
                Button("Row _Below", IconInsertRowBelow, _editor.TableInsertRowBelow, "Alt+↓"),
                Button("Column _Left", IconInsertColLeft, _editor.TableInsertColumnLeft),
                Button("Column _Right", IconInsertColRight, _editor.TableInsertColumnRight)),
            Group("Delete", "D",
                Button("Delete _Row", IconDeleteRow, _editor.TableDeleteRow),
                Button("Delete _Column", IconDeleteCol, _editor.TableDeleteColumn),
                Button("Delete _Table", IconDeleteTable, _editor.TableDelete)),
            Group("Move", "M",
                Button("Row _Up", IconMoveRowUp, _editor.TableMoveRowUp),
                Button("Row _Down", IconMoveRowDown, _editor.TableMoveRowDown),
                Button("Column _Left", IconMoveColLeft, _editor.TableMoveColumnLeft),
                Button("Column _Right", IconMoveColRight, _editor.TableMoveColumnRight)),
            Group("Cells", "C",
                // Alignment: three actions calling TableSetColumnAlignment. A reflecting toggle-set (checked =
                // the caret column's current alignment) is a follow-up — it reads caret-in-table state, FB-27's job.
                Button("Align _Left", IconAlignLeft, () => _editor.TableSetColumnAlignment(ColumnAlignment.Left)),
                Button("Align C_enter", IconAlignCenter, () => _editor.TableSetColumnAlignment(ColumnAlignment.Center)),
                Button("Align _Right", IconAlignRight, () => _editor.TableSetColumnAlignment(ColumnAlignment.Right)),
                new BarSeparator(),
                Button("_Clear Cell", IconClearCell, _editor.TableClearCell)));
    }

    // ───────────────────────────── View ─────────────────────────────

    private RibbonTab BuildViewTab()
    {
        var raw = new BarToggleButton { Content = "_Raw", Icon = IconRaw, Command = _rawCommand, CommandParameter = _rawChecked };

        var wrapChecked = new CheckableCommandParameter(_editor.EditWrapEnabled);
        var wrap = Toggle("_Wrap", IconWrap, wrapChecked, () =>
        {
            _editor.EditWrapEnabled = !_editor.EditWrapEnabled;
            wrapChecked.IsChecked = _editor.EditWrapEnabled; // re-read the real state (the command owns the checked bit)
        });

        // Overflow: a two-state segmented control (Wrap ⇄ Truncate). Two mutually-exclusive toggles reflecting +
        // setting EditorControl.OverflowMode; selecting one sets the mode and re-syncs BOTH toggles so exactly one
        // is checked (see SetOverflowMode/SyncOverflowToggles). Re-selecting the active choice is a no-op.
        _overflowWrapChecked = new CheckableCommandParameter(_editor.OverflowMode == TableOverflow.Wrap);
        _overflowTruncateChecked = new CheckableCommandParameter(_editor.OverflowMode == TableOverflow.Truncate);
        _overflowWrapCommand = new BarCommand(() => Run(() => SetOverflowMode(TableOverflow.Wrap))) { Text = "Wrap", IsCheckable = true };
        _overflowTruncateCommand = new BarCommand(() => Run(() => SetOverflowMode(TableOverflow.Truncate))) { Text = "Truncate", IsCheckable = true };
        var overflowWrap = new BarToggleButton
        {
            Content = "_Wrap", Icon = IconWrap, Command = _overflowWrapCommand, CommandParameter = _overflowWrapChecked,
        };
        var overflowTruncate = new BarToggleButton
        {
            Content = "_Truncate", Icon = IconTruncate, Command = _overflowTruncateCommand, CommandParameter = _overflowTruncateChecked,
        };

        return Tab("View", "V",
            Group("Mode", "M", raw),
            Group("Wrap", "W", wrap),
            Group("Overflow", "O", overflowWrap, overflowTruncate));
    }

    // Sets the editor's table-cell overflow mode from the segmented control, then re-syncs both toggles so the
    // choice is mutually exclusive (exactly one checked) regardless of which button was pressed.
    private void SetOverflowMode(TableOverflow mode)
    {
        _editor.OverflowMode = mode;
        SyncOverflowToggles();
    }

    // Reflects the single EditorControl.OverflowMode onto both segmented toggles and re-queries their commands so
    // the bound BarToggleButtons re-read the checked bit.
    private void SyncOverflowToggles()
    {
        TableOverflow mode = _editor.OverflowMode;
        _overflowWrapChecked.IsChecked = mode == TableOverflow.Wrap;
        _overflowTruncateChecked.IsChecked = mode == TableOverflow.Truncate;
        _overflowWrapCommand.RaiseCanExecuteChanged();
        _overflowTruncateCommand.RaiseCanExecuteChanged();
    }

    // ───────────────────────────── construction helpers ─────────────────────────────

    // Runs a ribbon command's op, then returns focus to the editor so typing continues immediately (the bar
    // "click a button, keep typing" model; also makes direct command invocation in tests keep the editor focused).
    private void Run(Action op)
    {
        op();
        _editor.Focus();
    }

    // `content` folds an access-key literal (e.g. "_Paste"): it underlines the mnemonic, registers the Alt+letter
    // accelerator, and seeds the KeyTip badge. The command's Text is the clean display label (underscore stripped)
    // used for tooltips and command identity; `icon` is a width-1 text glyph (set directly on the control, so the
    // BarCommand auto-fill preserves it).
    private BarButton Button(string content, string icon, Action op, string? gesture = null)
    {
        // Bool-returning ops (Copy/Cut/Paste/Undo/Redo) are wrapped by the caller as `() => _editor.Xxx()`
        // — the result is discarded (the ribbon runs the op unconditionally, unlike the keybind which bubbles).
        var command = new BarCommand(() => Run(op)) { Text = AccessText.Parse(content).Text, InputGestureText = gesture };
        return new BarButton { Content = content, Icon = icon, Command = command };
    }

    private BarToggleButton Toggle(string content, string icon, CheckableCommandParameter checkedState, Action toggle, string? gesture = null)
    {
        var command = new BarCommand(() => Run(toggle)) { Text = AccessText.Parse(content).Text, InputGestureText = gesture, IsCheckable = true };
        return new BarToggleButton { Content = content, Icon = icon, Command = command, CommandParameter = checkedState };
    }

    private static RibbonGroup Group(string header, string keyTip, params UIElement[] items)
    {
        var group = new RibbonGroup { Header = header };
        KeyTip.SetKey(group, keyTip); // explicit, collision-immune group letter for the Alt drill's second level
        foreach (var item in items)
            group.Items.Add(item);
        return group;
    }

    private static RibbonTab Tab(string header, string keyTip, params RibbonGroup[] groups)
    {
        var tab = new RibbonTab { Header = header };
        KeyTip.SetKey(tab, keyTip); // explicit, collision-immune tab letter for the Alt drill's first level
        foreach (var group in groups)
            tab.Groups.Add(group);
        return tab;
    }
}
