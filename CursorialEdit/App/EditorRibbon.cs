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
/// <b>Tiered icons.</b> Every button carries a capability-tiered <see cref="Icon"/> on its
/// <see cref="BarButton.Icon"/> tier (only the image tier is left null — PNGs procured in M5+): a Nerd Font
/// Material-Design (<c>nf-md-*</c>) <see cref="Icon.Glyph"/> preferred when <see cref="UIApplication.NerdFontAvailable"/>,
/// an opt-in color-<see cref="Icon.Emoji"/> tier, and the guaranteed <see cref="Icon.Text"/> floor — the same width-1,
/// text-presentation Unicode glyph the toolbar carried before the Nerd Font tier landed (no VS16, never a 2-wide
/// color-emoji sprite). Codepoints are pinned in <c>docs/icon-ledger.md</c> against Nerd Fonts glyphnames.json;
/// see the <c>Icon*</c> factories (each returns a fresh instance — an <see cref="Icon"/> is a Control and cannot be
/// double-parented). Verified by <c>RibbonTests.EveryRibbonButton_HasATieredNerdFontIconWithAWidthOneTextFloor</c>.
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
    // ───────────────────────────── icon tiers ─────────────────────────────
    // Each command's icon is a capability-tiered Cursorial.UI.Controls.Icon (design doc §12 / the icon ledger):
    // a Nerd Font Material-Design (nf-md-*) codepoint (Glyph — shown when UIApplication.NerdFontAvailable), an
    // opt-in color-emoji tier where the ledger gives one, and the guaranteed single-width Unicode floor (Text) —
    // the SAME width-1, text-presentation glyph the toolbar carried before the Nerd Font tier landed (no VS16,
    // never a 2-wide color-emoji sprite). The Glyph codepoints are pinned in docs/icon-ledger.md against Nerd
    // Fonts glyphnames.json v3.4.0; every one sits in the plane-15 PUA-A (U+F0000…) range, so they need the
    // \U000Fxxxx 8-digit escape (NOT \uXXXX, which stops at U+FFFF). RibbonTests re-verifies every Glyph is one
    // PUA codepoint and every Text floor is a width-1/no-VS16 grapheme, so a bad codepoint fails the suite.
    //
    // An Icon is a Control and cannot be double-parented, so each of these is a FACTORY returning a FRESH instance
    // per button (some icons are placed on more than one live button — e.g. Wrap on both the View toggle and the
    // Overflow choice). The clipboard/format factories are `internal` so the right-click MiniToolbar
    // (EditorContextBar) shares the SAME icon vocabulary as the ribbon without sharing an instance.
    internal static Icon IconCut() => Nf("\U000F0190", "✁", "✂️");    // nf-md-content_cut U+F0190 · floor U+2701 ✁
    internal static Icon IconCopy() => Nf("\U000F018F", "⧉", "📋");    // nf-md-content_copy U+F018F · floor U+29C9 ⧉
    internal static Icon IconPaste() => Nf("\U000F0192", "▤", "📋");   // nf-md-content_paste U+F0192 · floor U+25A4 ▤
    internal static Icon IconBold() => Nf("\U000F0264", "✱", "🅱");    // nf-md-format_bold U+F0264 · floor U+2731 ✱
    internal static Icon IconItalic() => Nf("\U000F0277", "⟋", "✍️");  // nf-md-format_italic U+F0277 · floor U+27CB ⟋
    internal static Icon IconInlineCode() => Nf("\U000F0174", "`", "💻"); // nf-md-code_tags U+F0174 · floor U+0060 `
    private static Icon IconUndo() => Nf("\U000F054C", "↶", "↩️");     // nf-md-undo U+F054C · floor U+21B6 ↶
    private static Icon IconRedo() => Nf("\U000F044E", "↷", "↪️");     // nf-md-redo U+F044E · floor U+21B7 ↷
    private static Icon IconSelectAll() => Nf("\U000F0486", "⬚", "🔲"); // nf-md-select_all U+F0486 · floor U+2B1A ⬚
    private static Icon IconInsertRowAbove() => Nf("\U000F04F4", "↥", "⬆️"); // nf-md-table_row_plus_before U+F04F4 · floor U+21A5 ↥
    private static Icon IconInsertRowBelow() => Nf("\U000F04F3", "↧", "⬇️"); // nf-md-table_row_plus_after U+F04F3 · floor U+21A7 ↧
    private static Icon IconInsertColLeft() => Nf("\U000F04ED", "↤", "⬅️");  // nf-md-table_column_plus_before U+F04ED · floor U+21A4 ↤
    private static Icon IconInsertColRight() => Nf("\U000F04EC", "↦", "➡️"); // nf-md-table_column_plus_after U+F04EC · floor U+21A6 ↦
    private static Icon IconDeleteRow() => Nf("\U000F04F5", "⊖", "❌");  // nf-md-table_row_remove U+F04F5 · floor U+2296 ⊖
    private static Icon IconDeleteCol() => Nf("\U000F04EE", "⊘", "❌");  // nf-md-table_column_remove U+F04EE · floor U+2298 ⊘
    private static Icon IconDeleteTable() => Nf("\U000F0A76", "⊗", "🗑️"); // nf-md-table_remove U+F0A76 · floor U+2297 ⊗
    private static Icon IconMoveRowUp() => Nf("\U000F0739", "↑", "🔼");  // nf-md-arrow_up_bold_box_outline U+F0739 · floor U+2191 ↑
    private static Icon IconMoveRowDown() => Nf("\U000F0730", "↓", "🔽"); // nf-md-arrow_down_bold_box_outline U+F0730 · floor U+2193 ↓
    private static Icon IconMoveColLeft() => Nf("\U000F0733", "←", "◀️"); // nf-md-arrow_left_bold_box_outline U+F0733 · floor U+2190 ←
    private static Icon IconMoveColRight() => Nf("\U000F0736", "→", "▶️"); // nf-md-arrow_right_bold_box_outline U+F0736 · floor U+2192 →
    private static Icon IconAlignLeft() => Nf("\U000F0262", "⇤", "⬅️");  // nf-md-format_align_left U+F0262 · floor U+21E4 ⇤
    private static Icon IconAlignCenter() => Nf("\U000F0260", "↹", "↔️"); // nf-md-format_align_center U+F0260 · floor U+21B9 ↹
    private static Icon IconAlignRight() => Nf("\U000F0263", "⇥", "➡️"); // nf-md-format_align_right U+F0263 · floor U+21E5 ⇥
    private static Icon IconClearCell() => Nf("\U000F01FE", "∅", "🧹");  // nf-md-eraser U+F01FE · floor U+2205 ∅ (ledger row added)
    private static Icon IconRaw() => Nf("\U000F0694", "⌗", "⌨️");       // nf-md-code_tags_check U+F0694 · floor U+2317 ⌗
    private static Icon IconWrap() => Nf("\U000F05B6", "↵", "↩️");      // nf-md-wrap U+F05B6 · floor U+21B5 ↵
    private static Icon IconTruncate() => Nf("\U000F0D0E", "…", "✂️");  // nf-md-format_text_wrapping_clip U+F0D0E · floor U+2026 … (ledger row added)

    // Builds a fresh tiered Icon: the Nerd Font Glyph (width-1), the color-Emoji tier (opt-in caps-emoji — a 2-wide
    // emoji is fine here; the width-1 discipline is the Text tier's), and the width-1 Unicode Text floor. Image tier
    // stays null (PNGs procured in M5+). GlyphWidth is 1 for every nf-md icon.
    private static Icon Nf(string glyph, string text, string emoji)
        => new() { Glyph = glyph, GlyphWidth = 1, Text = text, Emoji = emoji };

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
        var paste = Button("_Paste", IconPaste(), () => _editor.Paste(), "Ctrl+V");
        SetButtonSize(paste, RibbonButtonSize.Large); // the signature large glyph-over-label button

        return Tab("Home", "H",
            Group("Clipboard", "C",
                Button("_Cut", IconCut(), () => _editor.Cut(), "Ctrl+X"),
                Button("C_opy", IconCopy(), () => _editor.Copy(), "Ctrl+C"),
                paste),
            Group("Undo", "U",
                Button("_Undo", IconUndo(), () => _editor.Undo(), "Ctrl+Z"),
                Button("_Redo", IconRedo(), () => _editor.Redo(), "Ctrl+Y")),
            Group("Edit", "E",
                Button("_Select All", IconSelectAll(), _editor.SelectAll, "Ctrl+A")));
    }

    // ───────────────────────────── Table ─────────────────────────────

    private RibbonTab BuildTableTab()
    {
        // TODO(FB-27): gate every Table command on caret-in-table (pass a canExecute reading a
        // coerced "caret in a table" state) once that framework coercion lands. Until then the buttons
        // stay always-enabled; the ops themselves no-op safely off a table.
        return Tab("Table", "T",
            Group("Insert", "I",
                Button("Row _Above", IconInsertRowAbove(), _editor.TableInsertRowAbove, "Alt+↑"),
                Button("Row _Below", IconInsertRowBelow(), _editor.TableInsertRowBelow, "Alt+↓"),
                Button("Column _Left", IconInsertColLeft(), _editor.TableInsertColumnLeft),
                Button("Column _Right", IconInsertColRight(), _editor.TableInsertColumnRight)),
            Group("Delete", "D",
                Button("Delete _Row", IconDeleteRow(), _editor.TableDeleteRow),
                Button("Delete _Column", IconDeleteCol(), _editor.TableDeleteColumn),
                Button("Delete _Table", IconDeleteTable(), _editor.TableDelete)),
            Group("Move", "M",
                Button("Row _Up", IconMoveRowUp(), _editor.TableMoveRowUp),
                Button("Row _Down", IconMoveRowDown(), _editor.TableMoveRowDown),
                Button("Column _Left", IconMoveColLeft(), _editor.TableMoveColumnLeft),
                Button("Column _Right", IconMoveColRight(), _editor.TableMoveColumnRight)),
            Group("Cells", "C",
                // Alignment: three actions calling TableSetColumnAlignment. A reflecting toggle-set (checked =
                // the caret column's current alignment) is a follow-up — it reads caret-in-table state, FB-27's job.
                Button("Align _Left", IconAlignLeft(), () => _editor.TableSetColumnAlignment(ColumnAlignment.Left)),
                Button("Align C_enter", IconAlignCenter(), () => _editor.TableSetColumnAlignment(ColumnAlignment.Center)),
                Button("Align _Right", IconAlignRight(), () => _editor.TableSetColumnAlignment(ColumnAlignment.Right)),
                new BarSeparator(),
                Button("_Clear Cell", IconClearCell(), _editor.TableClearCell)));
    }

    // ───────────────────────────── View ─────────────────────────────

    private RibbonTab BuildViewTab()
    {
        var raw = new BarToggleButton { Content = "_Raw", Icon = IconRaw(), Command = _rawCommand, CommandParameter = _rawChecked };

        var wrapChecked = new CheckableCommandParameter(_editor.EditWrapEnabled);
        var wrap = Toggle("_Wrap", IconWrap(), wrapChecked, () =>
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
            Content = "_Wrap", Icon = IconWrap(), Command = _overflowWrapCommand, CommandParameter = _overflowWrapChecked,
        };
        var overflowTruncate = new BarToggleButton
        {
            Content = "_Truncate", Icon = IconTruncate(), Command = _overflowTruncateCommand, CommandParameter = _overflowTruncateChecked,
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
    // used for tooltips and command identity; `icon` is a tiered Icon (set directly on the control, so the
    // BarCommand auto-fill preserves it).
    private BarButton Button(string content, Icon icon, Action op, string? gesture = null)
    {
        // Bool-returning ops (Copy/Cut/Paste/Undo/Redo) are wrapped by the caller as `() => _editor.Xxx()`
        // — the result is discarded (the ribbon runs the op unconditionally, unlike the keybind which bubbles).
        var command = new BarCommand(() => Run(op)) { Text = AccessText.Parse(content).Text, InputGestureText = gesture };
        return new BarButton { Content = content, Icon = icon, Command = command };
    }

    private BarToggleButton Toggle(string content, Icon icon, CheckableCommandParameter checkedState, Action toggle, string? gesture = null)
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
