using System.Windows.Input;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

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

    // ───────────────────────────── helpers ─────────────────────────────

    private static IEnumerable<RibbonTab> Tabs(EditorRibbon ribbon) => ribbon.Items.Cast<RibbonTab>();

    private static RibbonTab Tab(EditorRibbon ribbon, string header)
        => Tabs(ribbon).Single(t => (string?)t.Header == header);

    private static IEnumerable<RibbonGroup> Groups(RibbonTab tab) => tab.Groups.Cast<RibbonGroup>();

    private static string?[] GroupHeaders(EditorRibbon ribbon, string tab)
        => Groups(Tab(ribbon, tab)).Select(g => (string?)g.Header).ToArray();

    private static string[] ButtonLabels(EditorRibbon ribbon, string tab, string group)
        => Groups(Tab(ribbon, tab)).Single(g => (string?)g.Header == group)
            .Items.OfType<ButtonBase>().Select(b => ((BarCommand)b.Command!).Text!).ToArray();

    private static ButtonBase FindButton(EditorRibbon ribbon, string tab, string label)
    {
        foreach (var group in Groups(Tab(ribbon, tab)))
            foreach (var item in group.Items)
                if (item is ButtonBase b && b.Command is BarCommand cmd && cmd.Text == label)
                    return b;

        throw new Xunit.Sdk.XunitException($"no button '{label}' on tab '{tab}'");
    }

    private static void Invoke(EditorShell shell, string tab, string label)
    {
        var button = FindButton(shell.Ribbon, tab, label);
        ICommand command = button.Command!;
        command.Execute(button.CommandParameter); // exactly what a click/keyboard-activate routes to
    }

    private static void SelectTab(EditorRibbon ribbon, string header)
        => ribbon.SelectedIndex = Tabs(ribbon).ToList().FindIndex(t => (string?)t.Header == header);
}
