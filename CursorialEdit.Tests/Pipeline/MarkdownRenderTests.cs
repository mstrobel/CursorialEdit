using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Pipeline;

/// <summary>
/// M2.WP7b — the milestone gate: a whole markdown document rendered <b>formatted</b> through the REAL
/// production path (<see cref="EditorShell.WireDocument"/> → <c>MarkdigBlockProducer</c> →
/// <c>MarkdownViewBridge</c> → the <see cref="LeafBlockPresenter"/> suite → <c>DocumentPanel</c>) under
/// <c>UITestHost</c> — headings/emphasis/lists/code (the §2.1 constructs), reveal-on-edit from the real
/// caret, and incremental re-render of only the touched zone on a keystroke. Not the presenter harness:
/// this is the app.
/// </summary>
public sealed class MarkdownRenderTests
{
    private const string Fence = "```";

    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private sealed class ShellHarness(UITestHost host, EditorShell shell) : IDisposable
    {
        public UITestHost Host { get; } = host;

        public EditorShell Shell { get; } = shell;

        public string Row(int row) => Host.GetRowText(row).TrimEnd();

        public Cell Cell(int column, int row) => Host.GetCell(column, row);

        /// <summary>The realized leaf presenters by block index (the raster observable).</summary>
        public IReadOnlyDictionary<int, LeafBlockPresenter> Leaves()
            => Shell.Editor.DocumentPanelPart!.RealizedBlocks.ToDictionary(kv => kv.Key, kv => (LeafBlockPresenter)kv.Value);

        public void Dispose() => Host.Dispose();
    }

    private static ShellHarness CreateShell(
        string markdown, string preset = nameof(TestCapabilities.KittyTruecolor), int columns = 40, int rows = 24)
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

        // Focus the surface so the caret publishes and reveals its active line (the demo's focus lesson).
        shell.Editor.Focus();
        Assert.True(host.RunUntilIdle(), "focusing the editor did not settle");

        return new ShellHarness(host, shell);
    }

    private static bool Has(Cell cell, TextAttributes attribute) => (cell.Style.Attributes & attribute) == attribute;

    // ───────────────────────────── formatted rendering through the production path ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void WholeDocument_RendersFormatted_Headings_Emphasis_Bullets_CodeFill(string preset)
    {
        // Constructs on non-caret lines render formatted; block 0 (the caret line) is a plain paragraph,
        // so its reveal shows the same text.
        var markdown =
            "Intro paragraph.\n\n## Section\n\nBody with **bold** text.\n\n- one\n- two\n\n"
            + Fence + "txt\ncode line\n" + Fence;

        using var harness = CreateShell(markdown, preset);

        // Rows: B0 para (0,1) · B1 heading (2,3) · B2 para (4,5) · B3 list (6,7,8) · B4 code (9,10,11).
        Assert.Equal("Intro paragraph.", harness.Row(0));

        // Heading: "## " hidden, bold + H2 color.
        Assert.Equal("Section", harness.Row(2));
        Assert.True(Has(harness.Cell(0, 2), TextAttributes.Bold));
        Assert.Equal(Colors.LightCyan, harness.Cell(0, 2).Style.Foreground);

        // Inline emphasis: "**" fences hidden, "bold" carries the Bold attribute (col 10 = "b").
        Assert.Equal("Body with bold text.", harness.Row(4));
        Assert.True(Has(harness.Cell(10, 4), TextAttributes.Bold));
        Assert.False(Has(harness.Cell(0, 4), TextAttributes.Bold));

        // List: "- " normalized to a "•" glyph in the marker color.
        Assert.Equal("• one", harness.Row(6));
        Assert.Equal("• two", harness.Row(7));
        Assert.Equal("•", harness.Cell(0, 6).Grapheme);
        Assert.Equal(Colors.LightYellow, harness.Cell(0, 6).Style.Foreground);

        // Code: opening fence shows the language label; the body is drawn over the code fill.
        Assert.Equal("txt", harness.Row(9));
        Assert.Equal("code line", harness.Row(10));
        Assert.Equal(Colors.LightBlack, harness.Cell(0, 10).Style.Background);
    }

    // ───────────────────────────── reveal-on-edit from the real caret ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void CaretMove_RevealsActiveLine_HidesPrior_NoReflowElsewhere(string preset)
    {
        using var harness = CreateShell("# Alpha\n\nMiddle stays.\n\n# Bravo", preset);
        var host = harness.Host;

        // Rows: B0 heading (0,1) · B1 para (2,3) · B2 heading (4). Caret at the origin reveals B0's line.
        Assert.Equal("# Alpha", harness.Row(0));       // active heading shows its raw "# " marks (revealed)
        Assert.Equal("Middle stays.", harness.Row(2)); // inactive paragraph, formatted
        Assert.Equal("Bravo", harness.Row(4));         // inactive heading, "# " hidden

        // Move the caret to the last line (Ctrl+End) — B2 becomes active.
        host.SendKey(Key.End, KeyModifiers.Control);
        Assert.True(host.RunUntilIdle());

        Assert.Equal("Alpha", harness.Row(0));         // B0 re-hidden its marks (reveal moved off it)
        Assert.Equal("Middle stays.", harness.Row(2)); // the untouched middle block did not reflow
        Assert.Equal("# Bravo", harness.Row(4));       // B2 now reveals its raw marks
    }

    // ───────────────────────────── keystroke re-rasters one zone (§13) ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Typing_ReparsesIncrementally_ReRastersOnlyTheTouchedZone(string preset)
    {
        using var harness = CreateShell("Alpha para.\n\n## Section\n\nBravo para.", preset);
        var host = harness.Host;

        var before = harness.Leaves();
        var counts = before.ToDictionary(kv => kv.Key, kv => kv.Value.RenderCount);
        Assert.Equal(3, before.Count);

        host.SendText("!"); // inserts at the caret (block 0), a same-height edit
        Assert.True(host.RunUntilIdle());

        Assert.Equal("!Alpha para.", harness.Row(0)); // block 0 re-parsed and re-rendered

        var after = harness.Leaves();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.True(after[0].RenderCount > counts[0], "the edited block re-rastered");
        Assert.Equal(counts[1], after[1].RenderCount); // the heading zone was untouched
        Assert.Equal(counts[2], after[2].RenderCount); // the trailing paragraph zone was untouched
    }

    // ───────────────────────────── front-matter fold grows the extent ─────────────────────────────

    [Fact]
    public void FrontMatterExpand_GrowsScrollExtent()
    {
        using var harness = CreateShell("---\ntitle: x\nauthor: y\n---\n\nBody", columns: 40, rows: 24);
        var host = harness.Host;

        int foldedExtent = harness.Shell.Editor.ScrollViewerPart!.Extent.Rows;
        Assert.Equal("▸ front matter", harness.Row(0)); // folded to one summary row by default

        var frontMatter = (FrontMatterPresenter)harness.Leaves()[0];
        frontMatter.ToggleFold();
        Assert.True(host.RunUntilIdle());

        int expandedExtent = harness.Shell.Editor.ScrollViewerPart.Extent.Rows;
        Assert.True(expandedExtent > foldedExtent, $"expanding front matter should grow the extent ({foldedExtent} → {expandedExtent})");
        Assert.StartsWith("---", harness.Row(0)); // the raw metadata is now shown
    }

    // ───────────────────────────── the WireDocument seam (markdown) ─────────────────────────────

    [Fact]
    public void WireDocument_ExposesTheMarkdownPipeline()
    {
        using var harness = CreateShell("# Title\n\nBody");
        var shell = harness.Shell;

        Assert.NotNull(shell.Document);
        Assert.NotNull(shell.Controller);
        Assert.Same(shell.Document, shell.Controller!.Buffer);
        Assert.NotNull(shell.BlockProducer); // MarkdigBlockProducer — the real parser drives the render
        Assert.Same(shell.ViewBridge, shell.Editor.HeightSource);
        Assert.Equal("# Title\n\nBody", shell.Document!.GetText());
    }

    [Fact]
    public void WireDocument_Again_ReplacesThePipelineAndReformats()
    {
        using var harness = CreateShell("old body");
        var host = harness.Host;
        var oldProducer = harness.Shell.BlockProducer!;

        harness.Shell.WireDocument("new intro\n\n## New Heading\n\nnew body", host.Time);
        Assert.True(host.RunUntilIdle());
        harness.Shell.Editor.Focus();
        Assert.True(host.RunUntilIdle());

        // Rows: B0 para (0,1) · B1 heading (2,3) · B2 para (4). The heading (non-caret) re-formats.
        Assert.Equal("new intro", harness.Row(0));
        Assert.Equal("New Heading", harness.Row(2)); // re-parsed + re-formatted through the fresh pipeline
        Assert.Equal("new body", harness.Row(4));
        Assert.NotSame(oldProducer, harness.Shell.BlockProducer);
    }
}
