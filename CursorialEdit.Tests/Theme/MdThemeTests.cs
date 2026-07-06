using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Configuration;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Presenters;
using CursorialEdit.Themes;

namespace CursorialEdit.Tests.Theme;

/// <summary>
/// M2.WP11a — the <c>Md.*</c> theme-token layer (implementation-plan §7 WP11 / §18). The markdown
/// presenter colors live in an app-owned <see cref="MdTheme"/> dictionary installed on
/// <see cref="UIApplication.Theme"/>: the authored Base tier is byte-identical to the WP7 hardcoded
/// palette (the regression contract), the NoColor tier collapses color to attributes, and an FW-A
/// user-config <c>theme.md.*</c> override wins over the authored token.
/// </summary>
public sealed class MdThemeTests
{
    /// <summary>A scratch <see cref="IUserConfigurationPathProvider"/> over a unique temp directory (mirrors the framework's test helper).</summary>
    private sealed class TempConfigRoot : IUserConfigurationPathProvider, IDisposable
    {
        public string ConfigurationRoot { get; } =
            Path.Combine(Path.GetTempPath(), "cursorialedit-theme-tests", Guid.NewGuid().ToString("N"));

        public void WriteGlobalFile(string contents)
        {
            Directory.CreateDirectory(ConfigurationRoot);
            File.WriteAllText(Path.Combine(ConfigurationRoot, "options.json"), contents);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(ConfigurationRoot))
                    Directory.Delete(ConfigurationRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup — a leaked temp dir must not fail the test
            }
        }
    }

    private sealed record ShellFixture(UITestHost Host, EditorShell Shell) : IDisposable
    {
        // The M5 ribbon docks at the shell's top, so document content starts EditorTop frame rows down.
        public int EditorTop => TestSupport.ShellLayout.EditorTopRow(Shell);
        public string Row(int row) => Host.GetRowText(row + EditorTop).TrimEnd();
        public Color HeadingForeground(int row) => Host.GetCell(0, row + EditorTop).Style.Foreground;
        public void Dispose() => Host.Dispose();
    }

    /// <summary>Shows an EditorShell over a doc with an inactive H2 on row 2, optionally under a user-config override.</summary>
    private static ShellFixture ShowHeadingDoc(TempConfigRoot? config = null)
    {
        var options = new UITestHostOptions
        {
            InitialSize = new Size(40, 12),
            Capabilities = TestCapabilities.KittyTruecolor,
            ConfigureBuilder = config is null
                ? null
                : builder => builder.WithUserConfiguration(new UserConfigurationOptions { PathProvider = config }),
        };

        var host = UITestHost.Create(options);
        MdTheme.EnsureInstalled(host.Application); // install the Md.* theme (+ apply any FW-A overrides)

        var shell = new EditorShell();
        shell.WireDocument("intro\n\n## Section", host.Time); // caret at origin → the H2 (row 2) stays inactive/formatted
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());
        shell.Editor.Focus();
        Assert.True(host.RunUntilIdle());

        return new ShellFixture(host, shell);
    }

    [Fact]
    public void InstalledTheme_RendersTheAuthoredDefault_ByteIdenticalToWP7()
    {
        // With the Md.* theme installed (no override), the H2 still resolves to the WP7 default color —
        // the authored Base tier equals the old hardcoded palette (the regression contract), proving the
        // token indirection changed nothing.
        using var fixture = ShowHeadingDoc();

        Assert.Equal("Section", fixture.Row(2));
        Assert.Equal(Colors.LightCyan, fixture.HeadingForeground(2)); // H2 default
    }

    [Fact]
    public void UserConfigOverride_OfAnMdToken_WinsOverTheAuthoredDefault()
    {
        using var config = new TempConfigRoot();
        config.WriteGlobalFile("""{ "theme.md.heading.2": "LightRed" }""");

        using var fixture = ShowHeadingDoc(config);

        // The FW-A override (theme.md.heading.2) is applied above UIApplication.Theme, so the H2 renders
        // in the overridden color instead of the authored LightCyan.
        Assert.Equal("Section", fixture.Row(2));
        Assert.Equal(Colors.LightRed, fixture.HeadingForeground(2));
    }

    [Fact]
    public void NoColorTier_CollapsesColorToDefault_ButKeepsTheHeadingAttributes()
    {
        var dict = MdTheme.Create();
        var noColor = dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.NoColor)];

        // Color roles collapse to Default (no stranded RGB), but the heading weight/underline lives here
        // at the tier floor — so a heading still reads as a heading on caps-nocolor (the §18.3 channel).
        Assert.True(noColor[MdThemeKeys.Heading(1)] is SolidColorBrush { Color: var c } && c == Colors.Default);
        Assert.Equal(
            TextAttributes.Bold | TextAttributes.Underline,
            (TextAttributes)noColor[MdThemeKeys.HeadingAttributes(1)]!);
    }

    [Theory]
    [InlineData("#ff0000", true)]
    [InlineData("LightRed", true)]
    [InlineData("palette:9", true)]
    [InlineData("not-a-color", false)]
    [InlineData("", false)]
    // Malformed hex must return false, NOT throw — Color.FromHex throws ArgumentException (not just
    // FormatException) for a wrong-length or bad-digit hex, and a bad user-config override must never
    // crash startup (it falls back to the authored token).
    [InlineData("#80ff0000", false)] // 8-digit — outside the 3/6-digit forms FromHex accepts
    [InlineData("#12", false)]       // too short
    [InlineData("#gggggg", false)]   // bad digits
    public void TryParseColor_HandlesHexNamedPaletteAndRejectsGarbage_WithoutThrowing(string text, bool expected)
    {
        Assert.Equal(expected, MdTheme.TryParseColor(text, out _));
    }

    [Fact]
    public void CodeFillOverride_RecolorsTheWholeCodeBlock_IncludingTheBackgroundFill()
    {
        using var config = new TempConfigRoot();
        config.WriteGlobalFile("""{ "theme.md.code.fill": "LightRed" }""");

        var options = new UITestHostOptions
        {
            InitialSize = new Size(40, 12),
            Capabilities = TestCapabilities.KittyTruecolor,
            ConfigureBuilder = builder => builder.WithUserConfiguration(new UserConfigurationOptions { PathProvider = config }),
        };

        using var host = UITestHost.Create(options);
        MdTheme.EnsureInstalled(host.Application);

        var shell = new EditorShell();
        shell.WireDocument("intro\n\n```\ncode\n```", host.Time); // fenced code as block 1
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());
        shell.Editor.Focus();
        Assert.True(host.RunUntilIdle());

        // The code fill must be the override everywhere in the block — including a BLANK trailing cell
        // (the PaintBackground fill), not just behind the "code" text — so it reads as one uniform color,
        // not two-tone. Find the "code" row and check a text cell and a blank cell share the override.
        int codeRow = Enumerable.Range(0, 12).First(r => host.GetRowText(r).TrimEnd() == "code");
        Assert.Equal(Colors.LightRed, host.GetCell(0, codeRow).Style.Background);   // behind the 'c'
        Assert.Equal(Colors.LightRed, host.GetCell(20, codeRow).Style.Background);  // a blank cell — the fill
    }
}
