using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI.Testing;

using CursorialEdit.App;

namespace CursorialEdit.Tests.App;

/// <summary>
/// M1.WP2 — the single-root <see cref="EditorShell"/> smoke render under <c>UITestHost</c>: the
/// one-row status line placeholder (dirty-dot slot + app name) is visible on the bottom row, the
/// editor is present/templated and fills the remaining rows, and it renders empty because no
/// height source is wired until WP7. Runs under both §5.1 wire presets.
/// </summary>
public sealed class EditorShellTests
{
    private const int Columns = 60;
    private const int Rows = 20;

    /// <summary>Both §5.1 wire presets — the shared registry (<see cref="TestSupport.CapabilityPresets"/>).</summary>
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private static UITestHost CreateHost(string preset) => UITestHost.Create(new UITestHostOptions
    {
        InitialSize = new Size(Columns, Rows),
        Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
    });

    [Theory]
    [MemberData(nameof(Presets))]
    public void Shell_ShowsStatusLineOnBottomRow_WithDirtyDotSlotAndAppName(string preset)
    {
        using var host = CreateHost(preset);
        var shell = new EditorShell();

        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());

        // Bottom row: the reserved two-cell dirty-dot slot (blank until WP11), then the app name.
        var bottomRow = host.GetRowText(Rows - 1);
        Assert.Equal("  " + EditorShell.AppName, bottomRow[..(2 + EditorShell.AppName.Length)]);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Shell_EditorIsTemplated_AndFillsBetweenTheRibbonAndTheStatusLine(string preset)
    {
        using var host = CreateHost(preset);
        var shell = new EditorShell();

        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());

        // The editor templated its own ScrollViewer/DocumentPanel (the WP3 surface) …
        Assert.NotNull(shell.Editor.ScrollViewerPart);
        Assert.NotNull(shell.Editor.DocumentPanelPart);

        // … and its viewport fills the region BETWEEN the M5 ribbon (docked top) and the one status row: the
        // ribbon occupies EditorTop rows, and editor-top + viewport meets the status line at Rows - 1.
        int editorTop = TestSupport.ShellLayout.EditorTopRow(shell);
        Assert.True(editorTop > 0, "the ribbon occupies rows above the editor");
        Assert.Equal(Rows - 1, editorTop + shell.Editor.ScrollViewerPart!.Viewport.Rows);
        Assert.Equal(Columns, shell.Editor.ScrollViewerPart.Viewport.Columns);

        // No height source is wired until WP7 — the editor region (below the ribbon, above the status) is empty.
        Assert.Null(shell.Editor.HeightSource);
        for (var row = editorTop; row < Rows - 1; row++)
            Assert.True(
                string.IsNullOrWhiteSpace(host.GetRowText(row)),
                $"editor row {row} should be empty before WP7 wires a height source");
    }

    [Fact]
    public void Shell_DirtyDotSlot_RendersTheDotWhenSet()
    {
        using var host = CreateHost(nameof(TestCapabilities.KittyTruecolor));
        var shell = new EditorShell();

        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());

        // WP11 wires the real dirty state; the slot itself must already be live in the layout.
        shell.DirtyDotPart.Text = "●";
        Assert.True(host.RunUntilIdle());

        var bottomRow = host.GetRowText(Rows - 1);
        Assert.Equal("● " + EditorShell.AppName, bottomRow[..(2 + EditorShell.AppName.Length)]);
    }

    [Fact]
    public void Shell_RetainsStartupOptionsForWp11()
    {
        var options = new AppStartupOptions("docs/readme.md");
        Assert.Same(options, new EditorShell(options).StartupOptions);
        Assert.Null(new EditorShell().StartupOptions.FilePath);
    }

    [Fact]
    public void Shell_NullStartupOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(static () => new EditorShell(null!));
    }
}
