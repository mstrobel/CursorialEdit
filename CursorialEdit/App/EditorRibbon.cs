using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

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
/// <b>Tiered icons.</b> Every <see cref="BarCommand"/> carries a capability-tiered <see cref="IconCarrier"/> on its
/// <see cref="BarCommand.Icon"/> (the button auto-fills its icon from the command; only the image tier is left null —
/// PNGs procured in M5+): a Nerd Font Material-Design (<c>nf-md-*</c>) <see cref="Icon.Glyph"/> preferred when
/// <see cref="UIApplication.NerdFontAvailable"/>, an opt-in color-<see cref="Icon.Emoji"/> tier, and the guaranteed
/// <see cref="Icon.Text"/> floor — the same width-1, text-presentation Unicode glyph the toolbar carried before the
/// Nerd Font tier landed (no VS16, never a 2-wide color-emoji sprite). Codepoints are pinned in
/// <c>docs/icon-ledger.md</c> against Nerd Fonts glyphnames.json; see the <c>Icon*</c> factories (each returns an
/// <see cref="IconCarrier"/> — an immutable descriptor the theme templates into a fresh <see cref="Icon"/> per host,
/// so it is safe on a command a control may share). Verified by <c>RibbonTests.EveryRibbonButton_HasATieredNerdFontIconWithAWidthOneTextFloor</c>.
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
/// <b>Context gating (FB-27, live).</b> Every Table command's <c>canExecute</c> reads
/// <see cref="EditorControl.IsCaretInTable"/>, so the ops grey off-table (and in Raw mode, where the bridge serves
/// no table model); the alignment buttons are a <b>reflecting radio-set</b> — one shared command whose re-query
/// writes each <see cref="ValueCommandParameter{T}"/>'s checked state from the caret column's live delimiter
/// alignment (<see cref="EditorControl.CaretColumnAlignment"/>); and the Wrap toggle locks in Raw mode (no reveal
/// to wrap). Re-query is the Bars manual model: <see cref="EditorControl.CaretUpdated"/> and
/// <see cref="EditorControl.ViewModeChanged"/> re-raise <c>CanExecuteChanged</c> on the gated set, and each bound
/// control re-reads enabled + checked in one pass (CD25). The ops still no-op safely as a belt-and-suspenders.
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
    // The icons live on each BarCommand (BarCommand.Icon), not the button — a command may bind more than one control,
    // and IconCarrier is the type required there: an immutable value descriptor the theme TEMPLATES into a fresh Icon
    // at each host (Cursorial.UI.Themes.IconCarrier), so it is safe to share where a live Icon (a visual Control that
    // cannot be double-parented) would not be. Each factory still returns its own carrier, so the ledger reads
    // one-icon-per-command. The clipboard/format factories are `internal` so the right-click MiniToolbar
    // (EditorContextBar) shares the SAME icon vocabulary as the ribbon.
    internal static IconCarrier IconCut() => Nf("\U000F0190", "✁", "✂️");    // nf-md-content_cut U+F0190 · floor U+2701 ✁
    internal static IconCarrier IconCopy() => Nf("\U000F018F", "⧉", "📋");    // nf-md-content_copy U+F018F · floor U+29C9 ⧉
    internal static IconCarrier IconPaste() => Nf("\U000F0192", "▤", "📋");   // nf-md-content_paste U+F0192 · floor U+25A4 ▤
    internal static IconCarrier IconBold() => Nf("\U000F0264", "✱", "🅱");    // nf-md-format_bold U+F0264 · floor U+2731 ✱
    internal static IconCarrier IconItalic() => Nf("\U000F0277", "⟋", "✍️");  // nf-md-format_italic U+F0277 · floor U+27CB ⟋
    internal static IconCarrier IconInlineCode() => Nf("\U000F0174", "`", "💻"); // nf-md-code_tags U+F0174 · floor U+0060 `
    private static IconCarrier IconUndo() => Nf("\U000F054C", "↶", "↩️");     // nf-md-undo U+F054C · floor U+21B6 ↶
    private static IconCarrier IconRedo() => Nf("\U000F044E", "↷", "↪️");     // nf-md-redo U+F044E · floor U+21B7 ↷
    private static IconCarrier IconSelectAll() => Nf("\U000F0486", "⬚", "🔲"); // nf-md-select_all U+F0486 · floor U+2B1A ⬚
    private static IconCarrier IconInsertRowAbove() => Nf("\U000F04F4", "↥", "⬆️"); // nf-md-table_row_plus_before U+F04F4 · floor U+21A5 ↥
    private static IconCarrier IconInsertRowBelow() => Nf("\U000F04F3", "↧", "⬇️"); // nf-md-table_row_plus_after U+F04F3 · floor U+21A7 ↧
    private static IconCarrier IconInsertColLeft() => Nf("\U000F04ED", "↤", "⬅️");  // nf-md-table_column_plus_before U+F04ED · floor U+21A4 ↤
    private static IconCarrier IconInsertColRight() => Nf("\U000F04EC", "↦", "➡️"); // nf-md-table_column_plus_after U+F04EC · floor U+21A6 ↦
    private static IconCarrier IconDeleteRow() => Nf("\U000F04F5", "⊖", "❌");  // nf-md-table_row_remove U+F04F5 · floor U+2296 ⊖
    private static IconCarrier IconDeleteCol() => Nf("\U000F04EE", "⊘", "❌");  // nf-md-table_column_remove U+F04EE · floor U+2298 ⊘
    private static IconCarrier IconDeleteTable() => Nf("\U000F0A76", "⊗", "🗑️"); // nf-md-table_remove U+F0A76 · floor U+2297 ⊗
    private static IconCarrier IconMoveRowUp() => Nf("\U000F0739", "↑", "🔼");  // nf-md-arrow_up_bold_box_outline U+F0739 · floor U+2191 ↑
    private static IconCarrier IconMoveRowDown() => Nf("\U000F0730", "↓", "🔽"); // nf-md-arrow_down_bold_box_outline U+F0730 · floor U+2193 ↓
    private static IconCarrier IconMoveColLeft() => Nf("\U000F0733", "←", "◀️"); // nf-md-arrow_left_bold_box_outline U+F0733 · floor U+2190 ←
    private static IconCarrier IconMoveColRight() => Nf("\U000F0736", "→", "▶️"); // nf-md-arrow_right_bold_box_outline U+F0736 · floor U+2192 →
    private static IconCarrier IconAlignLeft() => Nf("\U000F0262", "⇤", "⬅️");  // nf-md-format_align_left U+F0262 · floor U+21E4 ⇤
    private static IconCarrier IconAlignCenter() => Nf("\U000F0260", "↹", "↔️"); // nf-md-format_align_center U+F0260 · floor U+21B9 ↹
    private static IconCarrier IconAlignRight() => Nf("\U000F0263", "⇥", "➡️"); // nf-md-format_align_right U+F0263 · floor U+21E5 ⇥
    private static IconCarrier IconClearCell() => Nf("\U000F01FE", "∅", "🧹");  // nf-md-eraser U+F01FE · floor U+2205 ∅ (ledger row added)
    private static IconCarrier IconRaw() => Nf("\U000F0694", "⌗", "⌨️");       // nf-md-code_tags_check U+F0694 · floor U+2317 ⌗
    private static IconCarrier IconWrap() => Nf("\U000F05B6", "↵", "↩️");      // nf-md-wrap U+F05B6 · floor U+21B5 ↵
    private static IconCarrier IconTruncate() => Nf("\U000F0D0E", "…", "✂️");  // nf-md-format_text_wrapping_clip U+F0D0E · floor U+2026 … (ledger row added)

    // Builds a tiered IconCarrier: the Nerd Font Glyph (width-1), the color-Emoji tier (opt-in caps-emoji — a 2-wide
    // emoji is fine here; the width-1 discipline is the Text tier's), and the width-1 Unicode Text floor. Image tier
    // stays null (PNGs procured in M5+). GlyphWidth is 1 for every nf-md icon. The carrier is a value descriptor, not a
    // visual — layout properties (alignment, margins) have no place here; the Bars icon part owns icon placement.
    private static IconCarrier Nf(string glyph, string text, string emoji)
        => new() { Glyph = glyph, GlyphWidth = 1, Text = text, Emoji = emoji };

    private readonly EditorControl _editor;

    // The editor operations that ALSO appear on the right-click MiniToolbar (Cut/Copy/Paste today) are shared
    // BarCommand instances the shell builds once (see EditorCommands): the ribbon binds the same instances, so both
    // surfaces read one source of truth. Ribbon-only commands (Undo/Redo/Select All, the table + view groups) are
    // built inline by the Button/Toggle helpers below.
    private readonly EditorCommands _commands;

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

    // Every command whose canExecute reads live editor state (caret-in-table, view mode) — re-raised as one batch
    // by RequeryGatedCommands on each caret publish / mode flip (the Bars manual-requery model). Populated by the
    // Button/Toggle helpers (a non-null canExecute enlists the command) and the alignment radio-set.
    private readonly List<BarCommand> _gatedCommands = [];

    private bool _viewModeSubscribed;

    /// <summary>Builds the ribbon over <paramref name="editor"/> (the shell's persistent editor surface).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="editor"/> is <see langword="null"/>.</exception>
    public EditorRibbon(EditorControl editor, EditorCommands commands)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));

        _rawChecked = new CheckableCommandParameter(_editor.ViewMode == ViewMode.Raw);
        _rawCommand = new BarCommand(() => Run(_editor.ToggleViewMode))
        {
            Text = "_Raw",
            Icon = IconRaw(),
            InputGestureText = "Ctrl+/",
            IsCheckable = true,
            Description = "Show the raw Markdown source.",
        };

        Items.Add(BuildHomeTab());
        Items.Add(BuildTableTab());
        Items.Add(BuildViewTab());

        // The shared format toggles read live caret state too — enlist them so the same re-query batch keeps
        // every surface binding them (the Home Format group AND the right-click MiniToolbar) in sync.
        _gatedCommands.AddRange(commands.CaretGated);
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
        _editor.CaretUpdated += RequeryGatedCommands;
        _viewModeSubscribed = true;
        OnEditorViewModeChanged(); // reflect the current mode on attach (a Raw document shows the toggle checked)
        RequeryGatedCommands();    // seed the gates (table ops grey until the caret enters a table)
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_viewModeSubscribed)
        {
            _editor.ViewModeChanged -= OnEditorViewModeChanged;
            _editor.CaretUpdated -= RequeryGatedCommands;
            _viewModeSubscribed = false;
        }

        base.OnDetachedFromTree(in e);
    }

    // The Raw toggle mirrors EditorControl.ViewMode; re-query the command so the bound BarToggleButton re-reads
    // the checked state (whether the flip came from the ribbon or the keyboard). The gates re-query too: Raw mode
    // greys the table ops (no TableModel is served) and locks the Wrap toggle (no reveal to wrap).
    private void OnEditorViewModeChanged()
    {
        _rawChecked.IsChecked = _editor.ViewMode == ViewMode.Raw;
        _rawCommand.RaiseCanExecuteChanged();
        RequeryGatedCommands();
    }

    // The FB-27 gating re-query (the Bars model is manual-requery by design): every caret publish re-raises
    // CanExecuteChanged on the caret-/mode-gated commands, so each bound control re-reads BOTH its enabled state
    // (IsEnabledCore → canExecute — CD25) and its command-owned checked state (the checkable/value parameters the
    // canExecute writes) in one pass. The re-query is cheap (a block lookup per command), so no dirty-tracking.
    private void RequeryGatedCommands()
    {
        foreach (var command in _gatedCommands)
            command.RaiseCanExecuteChanged();
    }

    // ───────────────────────────── Home ─────────────────────────────

    private RibbonTab BuildHomeTab()
    {
        // Cut/Copy/Paste are the SHARED commands (bound identically on the right-click MiniToolbar) — bind the same
        // instances rather than build parallel ones. Paste is the signature large glyph-over-label button.
        return Tab("Home", "H",
            Group("Clipboard", "C",
                Bind(_commands.Cut),
                Bind(_commands.Copy),
                Bind(_commands.Paste)),
            Group("Format", "F",
                BindToggle(_commands.Bold),
                BindToggle(_commands.Italic),
                BindToggle(_commands.InlineCode)),
            Group("Undo", "U",
                Button("_Undo", IconUndo(), () => _editor.Undo(), "Ctrl+Z", "Undo the last change."),
                Button("_Redo", IconRedo(), () => _editor.Redo(), "Ctrl+Y", "Redo the last undone change.")),
            Group("Edit", "E",
                Button("_Select All", IconSelectAll(), _editor.SelectAll, "Ctrl+A", "Select the entire document.")));
    }

    // ───────────────────────────── Table ─────────────────────────────

    private RibbonTab BuildTableTab()
    {
        // FB-27 gating (LIVE): every table command's canExecute reads EditorControl.IsCaretInTable — buttons grey
        // off-table (and in Raw mode, where the bridge serves no TableModel) and re-enable when the caret enters a
        // table (RequeryGatedCommands on every caret publish). The ops still no-op safely as a belt-and-suspenders.
        Func<bool> inTable = () => _editor.IsCaretInTable;

        return Tab("Table", "T",
            Group("Insert", "I",
                Button("Row _Above", IconInsertRowAbove(), _editor.TableInsertRowAbove, "Alt+↑", "Insert a row above the current row.", inTable),
                Button("Row _Below", IconInsertRowBelow(), _editor.TableInsertRowBelow, "Alt+↓", "Insert a row below the current row.", inTable),
                Button("Column _Left", IconInsertColLeft(), _editor.TableInsertColumnLeft, description: "Insert a column to the left.", canExecute: inTable),
                Button("Column _Right", IconInsertColRight(), _editor.TableInsertColumnRight, description: "Insert a column to the right.", canExecute: inTable)),
            Group("Delete", "D",
                Button("Delete _Row", IconDeleteRow(), _editor.TableDeleteRow, description: "Delete the current row.", canExecute: inTable),
                Button("Delete _Column", IconDeleteCol(), _editor.TableDeleteColumn, description: "Delete the current column.", canExecute: inTable),
                Button("Delete _Table", IconDeleteTable(), _editor.TableDelete, description: "Delete the entire table.", canExecute: inTable)),
            Group("Move", "M",
                Button("Row _Up", IconMoveRowUp(), _editor.TableMoveRowUp, description: "Move the current row up.", canExecute: inTable),
                Button("Row _Down", IconMoveRowDown(), _editor.TableMoveRowDown, description: "Move the current row down.", canExecute: inTable),
                Button("Column _Left", IconMoveColLeft(), _editor.TableMoveColumnLeft, description: "Move the current column left.", canExecute: inTable),
                Button("Column _Right", IconMoveColRight(), _editor.TableMoveColumnRight, description: "Move the current column right.", canExecute: inTable)),
            Group("Cells", "C",
                AlignmentToggle("Align _Left", IconAlignLeft(), ColumnAlignment.Left),
                AlignmentToggle("Align C_enter", IconAlignCenter(), ColumnAlignment.Center),
                AlignmentToggle("Align _Right", IconAlignRight(), ColumnAlignment.Right),
                new BarSeparator(),
                Button("_Clear Cell", IconClearCell(), _editor.TableClearCell, description: "Clear the current cell's contents.", canExecute: inTable)));
    }

    // ───────────────────────────── alignment radio-set (ValueCommandParameter) ─────────────────────────────

    // ONE shared command drives the Left/Center/Right toggles; each button contributes its alignment through its
    // own ValueCommandParameter<ColumnAlignment>. On every re-query the canExecute writes each parameter's checked
    // state (checked = the caret column's CURRENT delimiter alignment — a live radio set, no stored UI state) and
    // gates the whole set on caret-in-table; Execute reads the clicked button's Value and applies it, and the
    // courtesy re-query after Execute re-syncs all three. A ColumnAlignment.None column checks nothing.
    private BarCommand _alignCommand = null!;

    private BarToggleButton AlignmentToggle(string content, IconCarrier icon, ColumnAlignment alignment)
    {
        _alignCommand ??= BuildAlignmentCommand();

        // Content + Icon are set locally so each toggle keeps its OWN face — a local value wins over the shared
        // command's auto-fill (which would otherwise stamp all three with the same label/icon).
        return new BarToggleButton
        {
            Content = content,
            Icon = icon,
            Command = _alignCommand,
            CommandParameter = new ValueCommandParameter<ColumnAlignment>(alignment),
        };
    }

    private BarCommand BuildAlignmentCommand()
    {
        var command = new BarCommand(
            execute: p => Run(() =>
            {
                // Toggle-off: invoking the CHECKED alignment clears the column back to unspecified (`---`) —
                // checked state is strict source markup (None checks nothing), so this is the ribbon's only way
                // to REMOVE an alignment. The comparison reads the model (CaretColumnAlignment), never the
                // parameter's IsChecked, so a control pre-flipping its visual state cannot skew the decision.
                var value = ((ValueCommandParameter<ColumnAlignment>)p!).Value;
                _editor.TableSetColumnAlignment(_editor.CaretColumnAlignment == value ? ColumnAlignment.None : value);
            }),
            canExecute: p =>
            {
                // The wiring re-query can arrive before the parameter is installed — pattern-match, don't cast.
                // Strict reflection: checked = the delimiter's EXPLICIT marker; an unspecified column (None,
                // GFM-rendered left) checks nothing — the check mirrors the source, not the rendered default.
                if (p is ValueCommandParameter<ColumnAlignment> vp)
                    vp.IsChecked = _editor.CaretColumnAlignment is { } current && vp.Value == current;
                return _editor.IsCaretInTable;
            })
        {
            Text = "Align",
            IsCheckable = true,
            Description = "Set the current column's alignment; selecting the active alignment clears it.",
        };

        _gatedCommands.Add(command);
        return command;
    }

    // ───────────────────────────── View ─────────────────────────────

    private RibbonTab BuildViewTab()
    {
        var raw = new BarToggleButton { Command = _rawCommand, CommandParameter = _rawChecked };

        var wrapChecked = new CheckableCommandParameter(_editor.EditWrapEnabled);
        var wrap = Toggle("_Wrap", IconWrap(), wrapChecked, () =>
        {
            _editor.EditWrapEnabled = !_editor.EditWrapEnabled;
            wrapChecked.IsChecked = _editor.EditWrapEnabled; // re-read the real state (the command owns the checked bit)
        }, description: "Wrap the edited line in place instead of sliding it horizontally.",
           canExecute: () => _editor.ViewMode == ViewMode.Formatted); // Raw mode has no reveal — nothing to wrap (the toggle locks)

        // Overflow: a two-state segmented control (Wrap ⇄ Truncate). Two mutually-exclusive toggles reflecting +
        // setting EditorControl.OverflowMode; selecting one sets the mode and re-syncs BOTH toggles so exactly one
        // is checked (see SetOverflowMode/SyncOverflowToggles). Re-selecting the active choice is a no-op.
        _overflowWrapChecked = new CheckableCommandParameter(_editor.OverflowMode == TableOverflow.Wrap);
        _overflowTruncateChecked = new CheckableCommandParameter(_editor.OverflowMode == TableOverflow.Truncate);
        _overflowWrapCommand = new BarCommand(() => Run(() => SetOverflowMode(TableOverflow.Wrap)))
            { Text = "_Wrap", Icon = IconWrap(), IsCheckable = true, Description = "Wrap overflowing cell content onto multiple lines." };
        _overflowTruncateCommand = new BarCommand(() => Run(() => SetOverflowMode(TableOverflow.Truncate)))
            { Text = "_Truncate", Icon = IconTruncate(), IsCheckable = true, Description = "Truncate overflowing cell content with an ellipsis." };
        var overflowWrap = new BarToggleButton { Command = _overflowWrapCommand, CommandParameter = _overflowWrapChecked };
        var overflowTruncate = new BarToggleButton { Command = _overflowTruncateCommand, CommandParameter = _overflowTruncateChecked };

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

    // Binds a fresh button to an EXISTING (shared) BarCommand — the button auto-fills its Content/Icon/gesture +
    // SuperTip from the command, so a command shown on more than one surface (Cut/Copy/Paste) is defined once.
    private static BarButton Bind(BarCommand command) => new() { Command = command };

    // The toggle flavour: a fresh CheckableCommandParameter per control — the shared command's canExecute writes the
    // caret-reflected checked state into it on every re-query (FB-27), so each bound toggle shows the live state.
    private static BarToggleButton BindToggle(BarCommand command)
        => new() { Command = command, CommandParameter = new CheckableCommandParameter(false) };

    // Define-once on the BarCommand (the Bars self-describing model): Text carries the access-key literal
    // (e.g. "_Paste" — it underlines the mnemonic, registers the Alt+letter accelerator, and seeds the KeyTip
    // badge), `icon` the tiered IconCarrier, `gesture` the shortcut, and `description` the SuperTip body. The
    // BarButton auto-fills its Content/Icon/InputGestureText from the command and builds the SuperTip (title =
    // Text, shortcut = gesture, body = Description) — no display metadata is set on the button itself.
    private BarButton Button(string content, IconCarrier icon, Action op, string? gesture = null, string? description = null, Func<bool>? canExecute = null)
    {
        // Bool-returning ops (Copy/Cut/Paste/Undo/Redo) are wrapped by the caller as `() => _editor.Xxx()`
        // — the result is discarded (the ribbon runs the op unconditionally, unlike the keybind which bubbles).
        var command = new BarCommand(() => Run(op), canExecute) { Text = content, Icon = icon, InputGestureText = gesture, Description = description };
        if (canExecute is not null)
            _gatedCommands.Add(command); // state-gated: re-queried on every caret publish / mode flip
        return new BarButton { Command = command };
    }

    private BarToggleButton Toggle(string content, IconCarrier icon, CheckableCommandParameter checkedState, Action toggle, string? gesture = null, string? description = null, Func<bool>? canExecute = null)
    {
        var command = new BarCommand(() => Run(toggle), canExecute) { Text = content, Icon = icon, InputGestureText = gesture, IsCheckable = true, Description = description };
        if (canExecute is not null)
            _gatedCommands.Add(command);
        return new BarToggleButton { Command = command, CommandParameter = checkedState };
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
