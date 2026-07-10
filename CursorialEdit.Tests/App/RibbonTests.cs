using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Bars.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;

using CursorialEdit.App;
using CursorialEdit.Document.Model;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.App;

/// <summary>
/// M5 — the Bars <see cref="EditorRibbon"/> docked at the top of the <see cref="EditorShell"/>. Verifies the
/// Home/Table/View tab + group + button structure, that a representative button per tab executes the editor's
/// REAL operation through its command (routed to the persistent <see cref="EditorControl"/>), that the View
/// toggles reflect live state, and — the critical additive invariant — that the ribbon does NOT steal startup
/// focus (the editor auto-focuses and typing works with the ribbon present).
/// </summary>
public sealed class RibbonTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private static (UITestHost Host, EditorShell Shell) Shell(
        string markdown, string preset = nameof(TestCapabilities.KittyTruecolor), int columns = 48, int rows = 24)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
        });

        var shell = new EditorShell();
        shell.WireDocument(markdown, host.Time);
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");
        return (host, shell);
    }

    // ───────────────────────────── structure ─────────────────────────────

    [Fact]
    public void Shell_HostsRibbon_AtTop_WithHomeTableViewTabsAndGroups()
    {
        var (host, shell) = Shell("hi");
        using var _ = host;

        var ribbon = shell.Ribbon;

        // The ribbon is docked ABOVE the editor — the document content begins on a lower frame row.
        Assert.True(TestSupport.ShellLayout.EditorTopRow(shell) > 0, "the ribbon docks above the editor content");

        Assert.Equal(new[] { "Home", "Table", "View" }, Tabs(ribbon).Select(t => (string?)t.Header));

        Assert.Equal(new[] { "Clipboard", "Undo", "Edit" }, GroupHeaders(ribbon, "Home"));
        Assert.Equal(new[] { "Insert", "Delete", "Move", "Cells" }, GroupHeaders(ribbon, "Table"));
        Assert.Equal(new[] { "Mode", "Wrap", "Overflow" }, GroupHeaders(ribbon, "View"));

        // The Clipboard group carries Cut / Copy / Paste (the large button), Undo carries Undo / Redo.
        Assert.Equal(new[] { "Cut", "Copy", "Paste" }, ButtonLabels(ribbon, "Home", "Clipboard"));
        Assert.Equal(new[] { "Undo", "Redo" }, ButtonLabels(ribbon, "Home", "Undo"));
    }

    // ───────────────────────────── Home: a representative command executes ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Home_Undo_UndoesThroughTheCommand_AndRefocusesTheEditor(string preset)
    {
        var (host, shell) = Shell("hello", preset);
        using var _ = host;

        Assert.True(shell.Editor.IsKeyboardFocusWithin); // auto-focused
        host.SendText("X");                              // "Xhello"
        Assert.True(host.RunUntilIdle());
        Assert.Equal("Xhello", shell.Document!.GetText());

        Invoke(shell, "Home", "Undo");
        Assert.True(host.RunUntilIdle());

        Assert.Equal("hello", shell.Document!.GetText());       // the ribbon's Undo command reverted the edit
        Assert.True(shell.Editor.IsKeyboardFocusWithin, "the editor is refocused after a ribbon command");
    }

    // ───────────────────────────── Table: a representative command executes ─────────────────────────────

    [Fact]
    public void Table_InsertRowBelow_InsertsARow_ThroughTheCommand()
    {
        var (host, shell) = Shell("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 48, rows: 20);
        using var _ = host;

        // Land the caret in the body row's first cell (content-space: row 1 is the header, one Down → body).
        var caret = shell.Editor.DocumentCaretPart!;
        caret.ClickAt(2, 1);
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.DownArrow);
        Assert.True(host.RunUntilIdle());
        Assert.True(caret.IsInTable, "the caret is in the table body row");

        int before = shell.ViewBridge!.GetTableModel(0)!.RowCount;

        Invoke(shell, "Table", "Row Below");
        Assert.True(host.RunUntilIdle());

        var model = shell.ViewBridge!.GetTableModel(0)!;
        Assert.Equal(before + 1, model.RowCount);                  // a row was inserted
        Assert.Contains("|  |  |", shell.Document!.GetText());      // the fresh empty GFM row is in the source
    }

    // ───────────────────────────── View: the Raw toggle reflects live ViewMode ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void View_RawToggle_FlipsViewMode_AndIsCheckedReflectsIt(string preset)
    {
        var (host, shell) = Shell("intro\n\n## Section", preset);
        using var _ = host;

        SelectTab(shell.Ribbon, "View"); // realize the View band so the toggle attaches + reflects
        Assert.True(host.RunUntilIdle());

        var toggle = (BarToggleButton)FindButton(shell.Ribbon, "View", "Raw");
        Assert.Equal(ViewMode.Formatted, shell.Editor.ViewMode);
        Assert.NotEqual(true, toggle.IsChecked); // starts unchecked (formatted)

        Invoke(shell, "View", "Raw"); // → Raw
        Assert.True(host.RunUntilIdle());
        Assert.Equal(ViewMode.Raw, shell.Editor.ViewMode);
        Assert.Equal(true, toggle.IsChecked);     // the toggle reflects the real mode

        Invoke(shell, "View", "Raw"); // → Formatted
        Assert.True(host.RunUntilIdle());
        Assert.Equal(ViewMode.Formatted, shell.Editor.ViewMode);
        Assert.NotEqual(true, toggle.IsChecked);
    }

    [Fact] // the keyboard route (Ctrl+/) flips the mode AND the ribbon Raw toggle reflects it (ViewModeChanged sync)
    public void View_RawToggle_ReflectsAKeyboardViewModeFlip()
    {
        var (host, shell) = Shell("intro\n\n## Section");
        using var _ = host;

        SelectTab(shell.Ribbon, "View");
        Assert.True(host.RunUntilIdle());
        var toggle = (BarToggleButton)FindButton(shell.Ribbon, "View", "Raw");
        Assert.NotEqual(true, toggle.IsChecked);

        // Re-focus the editor (selecting a tab moved focus to the strip) and toggle the mode by keyboard.
        shell.Editor.Focus();
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Character, KeyModifiers.Control, text: "/");
        Assert.True(host.RunUntilIdle());

        Assert.Equal(ViewMode.Raw, shell.Editor.ViewMode);
        Assert.Equal(true, toggle.IsChecked); // the ribbon toggle re-synced from the keyboard-driven flip
    }

    [Fact] // the Wrap toggle drives EditWrapEnabled and reflects it
    public void View_WrapToggle_TogglesEditWrap()
    {
        var (host, shell) = Shell("a paragraph");
        using var _ = host;

        SelectTab(shell.Ribbon, "View");
        Assert.True(host.RunUntilIdle());
        var wrap = (BarToggleButton)FindButton(shell.Ribbon, "View", "Wrap");

        Assert.True(shell.Editor.EditWrapEnabled);  // on by default
        Assert.Equal(true, wrap.IsChecked);

        Invoke(shell, "View", "Wrap");
        Assert.True(host.RunUntilIdle());
        Assert.False(shell.Editor.EditWrapEnabled);
        Assert.NotEqual(true, wrap.IsChecked);
    }

    // ───────────────────────────── icons: every button carries a tiered Nerd Font icon over a width-1 floor ─────────────────────────────

    [Fact] // Every ribbon button carries a tiered Icon: a single Nerd-Font-PUA Glyph codepoint (the nf-md-* icon)
           // AND a width-1, no-VS16 text-presentation Text floor — the guaranteed single-cell fallback, never a
           // 2-wide color-emoji sprite. (The opt-in Emoji tier may be a 2-wide sprite; the floor is the disciplined one.)
    public void EveryRibbonButton_HasATieredNerdFontIconWithAWidthOneTextFloor()
    {
        var (host, shell) = Shell("hi");
        using var _ = host;

        int buttons = 0;
        foreach (var button in AllButtons(shell.Ribbon))
        {
            buttons++;
            object? icon = button.GetValue(BarButton.IconProperty);
            var tiered = Assert.IsType<IconCarrier>(icon); // the icon tier is a shareable IconCarrier the theme templates into an Icon; the image tier stays null
            TestSupport.IconAssert.NerdFontOverWidthOneFloor(tiered);
        }

        Assert.True(buttons >= 24, $"expected the full button set to carry icons, saw {buttons}");
    }

    // ───────────────────────────── access keys: assigned + collision-free per drill scope ─────────────────────────────

    [Fact] // Every tab, group, and button has a KeyTip letter, and the letters don't collide within a drill scope
           // (tabs among themselves; groups within a tab; controls within a group) — so Alt+letter never bonks.
    public void EveryTabGroupAndButton_HasANonCollidingAccessKey()
    {
        var (host, shell) = Shell("hi");
        using var _ = host;
        var ribbon = shell.Ribbon;

        // Tabs.
        var tabKeys = Tabs(ribbon).Select(KeyTipLetter).ToList();
        Assert.All(tabKeys, k => Assert.NotNull(k));
        AssertNoDuplicates(tabKeys, "tab");

        foreach (var tab in Tabs(ribbon))
        {
            // Groups within this tab.
            var groupKeys = Groups(tab).Select(KeyTipLetter).ToList();
            Assert.All(groupKeys, k => Assert.NotNull(k));
            AssertNoDuplicates(groupKeys, $"group in tab '{tab.Header}'");

            foreach (var group in Groups(tab))
            {
                // Controls within this group.
                var controlKeys = group.Items.OfType<ButtonBase>().Select(KeyTipLetter).ToList();
                Assert.All(controlKeys, k => Assert.NotNull(k));
                AssertNoDuplicates(controlKeys, $"control in group '{group.Header}' of tab '{tab.Header}'");
            }
        }
    }

    // ───────────────────────────── View: the Overflow segmented control reflects + sets OverflowMode ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void View_OverflowSegmentedControl_ReflectsAndSetsOverflowMode(string preset)
    {
        var (host, shell) = Shell("| A | B |\n|---|---|\n| 1 | 2 |\n", preset, columns: 48, rows: 20);
        using var _ = host;

        SelectTab(shell.Ribbon, "View"); // realize the View band so the toggles attach + reflect
        Assert.True(host.RunUntilIdle());

        var wrap = (BarToggleButton)FindButtonInGroup(shell.Ribbon, "View", "Overflow", "Wrap");
        var truncate = (BarToggleButton)FindButtonInGroup(shell.Ribbon, "View", "Overflow", "Truncate");

        // Default: Wrap — exactly the Wrap toggle is checked.
        Assert.Equal(TableOverflow.Wrap, shell.Editor.OverflowMode);
        Assert.Equal(true, wrap.IsChecked);
        Assert.NotEqual(true, truncate.IsChecked);

        // Select Truncate → the editor's mode flips and mutual exclusion holds (Wrap unchecks).
        InvokeInGroup(shell, "View", "Overflow", "Truncate");
        Assert.True(host.RunUntilIdle());
        Assert.Equal(TableOverflow.Truncate, shell.Editor.OverflowMode);
        Assert.NotEqual(true, wrap.IsChecked);
        Assert.Equal(true, truncate.IsChecked);
        Assert.True(shell.Editor.IsKeyboardFocusWithin, "the editor is refocused after the ribbon command");

        // Select Wrap → back to Wrap.
        InvokeInGroup(shell, "View", "Overflow", "Wrap");
        Assert.True(host.RunUntilIdle());
        Assert.Equal(TableOverflow.Wrap, shell.Editor.OverflowMode);
        Assert.Equal(true, wrap.IsChecked);
        Assert.NotEqual(true, truncate.IsChecked);

        // Re-selecting the ACTIVE choice is a no-op (radio semantics — it never toggles Wrap off).
        InvokeInGroup(shell, "View", "Overflow", "Wrap");
        Assert.True(host.RunUntilIdle());
        Assert.Equal(TableOverflow.Wrap, shell.Editor.OverflowMode);
        Assert.Equal(true, wrap.IsChecked);
        Assert.NotEqual(true, truncate.IsChecked);
    }

    // ───────────────────────────── no focus regression ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Editor_AutoFocuses_AndAcceptsTyping_WithRibbonPresent(string preset)
    {
        var (host, shell) = Shell("hello", preset);
        using var _ = host;

        // No explicit Editor.Focus(): the ribbon is the first tab stop, but the shell keeps the editor the
        // startup focus owner (the ribbon is reached by click/access-key, it must not steal focus).
        Assert.True(shell.Editor.IsKeyboardFocusWithin, "the editor auto-focused despite the ribbon");

        host.SendText("Z");
        Assert.True(host.RunUntilIdle());
        Assert.Equal("Zhello", shell.Document!.GetText()); // typing reached the editor, not the ribbon
        Assert.True(shell.Editor.IsKeyboardFocusWithin);
    }

    // ───────────────────────────── FB-27 gating: table ops grey off-table, alignment reflects ─────────────────────────────

    [Fact] // every Table command's canExecute reads IsCaretInTable: greyed off-table, enabled inside, greyed again on exit
    public void TableOps_GreyOffTable_AndEnableInsideIt()
    {
        var (host, shell) = Shell("hello\n\n| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 48, rows: 20);
        using var _ = host;
        var caret = shell.Editor.DocumentCaretPart!;

        SelectTab(shell.Ribbon, "Table"); // realize the Table band so the buttons attach + subscribe to the re-query
        Assert.True(host.RunUntilIdle());

        var rowBelow = FindButton(shell.Ribbon, "Table", "Row Below");
        Assert.False(caret.IsInTable);
        Assert.False(rowBelow.IsEffectivelyEnabled); // greyed off-table (seeded at attach + first caret publish)

        // Into the table body: the table's grid starts at visual row 2 ("hello", blank, top border), so the body
        // content row sits at 5 (border 2, header 3, separator 4). ClickAt lands the caret; IsInTable self-checks.
        caret.ClickAt(2, 5);
        Assert.True(host.RunUntilIdle());
        Assert.True(caret.IsInTable, "the caret is in the table body row");
        Assert.True(rowBelow.IsEffectivelyEnabled); // the caret publish re-queried the gate

        host.SendKey(Key.Home, KeyModifiers.Control); // back to "hello" — off-table again
        Assert.True(host.RunUntilIdle());
        Assert.False(caret.IsInTable);
        Assert.False(rowBelow.IsEffectivelyEnabled);
    }

    [Fact] // ONE shared command + three ValueCommandParameter<ColumnAlignment>: checked = the caret column's live alignment
    public void AlignmentRadioSet_ReflectsCaretColumn_AndSetsOnInvoke()
    {
        var (host, shell) = Shell("| A | B |\n|:--|--:|\n| a | b |\n", columns: 48, rows: 20);
        using var _ = host;
        var caret = shell.Editor.DocumentCaretPart!;

        SelectTab(shell.Ribbon, "Table"); // realize the Table band so the toggles attach + reflect
        Assert.True(host.RunUntilIdle());

        var left = AlignmentToggle(shell.Ribbon, "Align Left");
        var center = AlignmentToggle(shell.Ribbon, "Align Center");
        var right = AlignmentToggle(shell.Ribbon, "Align Right");

        // Body row, first cell — a LEFT-aligned column (":--").
        caret.ClickAt(2, 1);
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.DownArrow);
        Assert.True(host.RunUntilIdle());
        Assert.True(caret.IsInTable, "the caret is in the table body row");
        Assert.Equal(true, left.IsChecked);      // reflects :--
        Assert.NotEqual(true, center.IsChecked);
        Assert.NotEqual(true, right.IsChecked);

        // Tab to the second cell — a RIGHT-aligned column ("--:"): the radio set re-syncs to the new column.
        host.SendKey(Key.Tab);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(true, right.IsChecked);
        Assert.NotEqual(true, left.IsChecked);

        // Invoke Center on this column: Execute applies the clicked parameter's Value; the courtesy re-query
        // re-syncs the whole set, and the delimiter row now carries the GFM center marker for column B.
        center.Command!.Execute(center.CommandParameter);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(true, center.IsChecked);
        Assert.NotEqual(true, right.IsChecked);
        Assert.Contains(":---:", shell.Document!.GetText()); // the rewritten delimiter (center) round-trips to source

        // Toggle-off: invoking the CHECKED alignment clears the column back to unspecified (`---`) — the strict
        // reflection then checks NOTHING for this column (None carries no toggle; GFM renders it left by default).
        center.Command!.Execute(center.CommandParameter);
        Assert.True(host.RunUntilIdle());
        Assert.NotEqual(true, center.IsChecked);
        Assert.NotEqual(true, left.IsChecked);
        Assert.NotEqual(true, right.IsChecked);
        Assert.DoesNotContain(":---:", shell.Document!.GetText()); // column B's delimiter is back to `---`
        Assert.Contains(":---", shell.Document!.GetText());        // column A's explicit LEFT marker is untouched
    }

    [Fact] // Raw mode: no TableModel is served and there is no reveal — table ops AND the Wrap toggle lock; Formatted restores
    public void RawMode_GreysTableOps_AndLocksTheWrapToggle()
    {
        var (host, shell) = Shell("| A | B |\n|---|---|\n| 1 | 2 |\n", columns: 48, rows: 20);
        using var _ = host;
        var caret = shell.Editor.DocumentCaretPart!;

        // Caret into the table body — everything enabled in Formatted mode. Each button is asserted while ITS
        // tab is selected: a detached band's buttons are unsubscribed and their enabled cache is stale by design.
        caret.ClickAt(2, 1);
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.DownArrow);
        Assert.True(host.RunUntilIdle());
        Assert.True(caret.IsInTable);

        SelectTab(shell.Ribbon, "Table");
        Assert.True(host.RunUntilIdle());
        var rowBelow = FindButton(shell.Ribbon, "Table", "Row Below");
        Assert.True(rowBelow.IsEffectivelyEnabled);

        shell.Editor.ToggleViewMode(); // → Raw (ViewModeChanged re-queries the gates)
        Assert.True(host.RunUntilIdle());
        Assert.False(rowBelow.IsEffectivelyEnabled); // no TableModel in raw mode ⇒ IsCaretInTable is false

        SelectTab(shell.Ribbon, "View");
        Assert.True(host.RunUntilIdle());
        var wrap = FindButtonInGroup(shell.Ribbon, "View", "Wrap", "Wrap");
        Assert.False(wrap.IsEffectivelyEnabled);     // no reveal in raw mode ⇒ the wrap toggle locks

        shell.Editor.ToggleViewMode(); // → Formatted: the wrap toggle unlocks…
        Assert.True(host.RunUntilIdle());
        Assert.True(wrap.IsEffectivelyEnabled);

        SelectTab(shell.Ribbon, "Table");           // …and the re-attached table band re-reads the gate
        Assert.True(host.RunUntilIdle());
        Assert.True(rowBelow.IsEffectivelyEnabled);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    // The alignment toggles share ONE command (Text "Align"), so they are found by their LOCAL Content face.
    private static BarToggleButton AlignmentToggle(EditorRibbon ribbon, string content)
        => Groups(Tab(ribbon, "Table")).Single(g => (string?)g.Header == "Cells")
            .Items.OfType<BarToggleButton>()
            .Single(t => t.Content is string s && AccessText.Parse(s).Text == content);

    private static IEnumerable<RibbonTab> Tabs(EditorRibbon ribbon) => ribbon.Items.Cast<RibbonTab>();

    private static RibbonTab Tab(EditorRibbon ribbon, string header)
        => Tabs(ribbon).Single(t => (string?)t.Header == header);

    private static IEnumerable<RibbonGroup> Groups(RibbonTab tab) => tab.Groups.Cast<RibbonGroup>();

    private static string?[] GroupHeaders(EditorRibbon ribbon, string tab)
        => Groups(Tab(ribbon, tab)).Select(g => (string?)g.Header).ToArray();

    private static string[] ButtonLabels(EditorRibbon ribbon, string tab, string group)
        => Groups(Tab(ribbon, tab)).Single(g => (string?)g.Header == group)
            .Items.OfType<ButtonBase>().Select(CommandLabel).ToArray();

    // The button's DISPLAY label — the command's Text with its access-key literal stripped ("_Paste" ⇒ "Paste").
    // The self-describing BarButton sources Content/Icon/SuperTip from the command, so its label IS the command's Text.
    private static string CommandLabel(ButtonBase button) => AccessText.Parse(((BarCommand)button.Command!).Text ?? "").Text;

    private static IEnumerable<ButtonBase> AllButtons(EditorRibbon ribbon)
        => Tabs(ribbon).SelectMany(Groups).SelectMany(g => g.Items.OfType<ButtonBase>());

    // The KeyTip badge letter the Alt overlay would derive for a control (explicit KeyTip.Key on tabs/groups; the
    // folded access-key mnemonic on buttons). Null when nothing is derivable.
    private static string? KeyTipLetter(UIElement element) => KeyTipModel.Resolve(element).KeyTip;

    private static void AssertNoDuplicates(IReadOnlyCollection<string?> keys, string scope)
    {
        var dupes = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"colliding {scope} KeyTip letter(s): {string.Join(", ", dupes)}");
    }

    private static ButtonBase FindButton(EditorRibbon ribbon, string tab, string label)
    {
        foreach (var group in Groups(Tab(ribbon, tab)))
            foreach (var item in group.Items)
                if (item is ButtonBase b && b.Command is BarCommand && CommandLabel(b) == label)
                    return b;

        throw new Xunit.Sdk.XunitException($"no button '{label}' on tab '{tab}'");
    }

    // Group-scoped find — disambiguates a label that appears in more than one group of a tab (e.g. the View tab's
    // "Wrap" is BOTH the prose-wrap toggle and the Overflow segmented control's wrap choice).
    private static ButtonBase FindButtonInGroup(EditorRibbon ribbon, string tab, string group, string label)
    {
        var g = Groups(Tab(ribbon, tab)).Single(gr => (string?)gr.Header == group);
        foreach (var item in g.Items)
            if (item is ButtonBase b && b.Command is BarCommand && CommandLabel(b) == label)
                return b;

        throw new Xunit.Sdk.XunitException($"no button '{label}' in group '{group}' on tab '{tab}'");
    }

    private static void Invoke(EditorShell shell, string tab, string label)
    {
        var button = FindButton(shell.Ribbon, tab, label);
        ICommand command = button.Command!;
        command.Execute(button.CommandParameter); // exactly what a click/keyboard-activate routes to
    }

    private static void InvokeInGroup(EditorShell shell, string tab, string group, string label)
    {
        var button = FindButtonInGroup(shell.Ribbon, tab, group, label);
        ICommand command = button.Command!;
        command.Execute(button.CommandParameter);
    }

    private static void SelectTab(EditorRibbon ribbon, string header)
        => ribbon.SelectedIndex = Tabs(ribbon).ToList().FindIndex(t => (string?)t.Header == header);
}
