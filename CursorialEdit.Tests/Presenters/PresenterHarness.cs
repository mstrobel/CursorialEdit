using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// The M2.WP7 presenter-suite harness (extends the WP6 <c>RevealHarness</c> pattern): a headless
/// <see cref="UITestHost"/> showing a vertical <see cref="StackPanel"/> of
/// <see cref="LeafBlockPresenter"/>s — one per document block — driven through the real frame loop.
/// It parses real markdown through a <see cref="DocumentBuffer"/> + <see cref="MarkdigBlockProducer"/>
/// (via <see cref="BlockHarness"/>) so every presenter renders the genuine block-kind + inline-run
/// projection, then reads back composited cells to prove §2.1 formatted rendering, per-kind reveal
/// with no reflow, and height-invariance under reveal. <see cref="SelectPresenter"/> is the
/// presenter-per-<see cref="BlockKind"/> selection the WP7b bridge will use in production.
/// </summary>
internal sealed class PresenterHarness : IDisposable
{
    private readonly LeafBlockPresenter[] _presenters;

    private PresenterHarness(UITestHost host, StackPanel root, LeafBlockPresenter[] presenters, int columns, int rows)
    {
        Host = host;
        Root = root;
        _presenters = presenters;
        Columns = columns;
        Rows = rows;
    }

    public UITestHost Host { get; }

    public StackPanel Root { get; }

    public int Columns { get; }

    public int Rows { get; }

    public IReadOnlyList<LeafBlockPresenter> Presenters => _presenters;

    /// <summary>Parses <paramref name="markdown"/>, builds one presenter per block, stacks them, and settles.</summary>
    public static PresenterHarness FromMarkdown(
        string markdown,
        string preset = nameof(TestCapabilities.KittyTruecolor),
        int columns = 40,
        int rows = 12,
        WrapMode wrapMode = WrapMode.WordWrap)
    {
        var block = BlockHarness.Create(markdown);
        var presenters = new List<LeafBlockPresenter>();
        for (var i = 0; i < block.Blocks.Count; i++)
            presenters.Add(SelectPresenter(block, i, wrapMode));

        return Show(presenters, preset, columns, rows);
    }

    /// <summary>Shows a vertical stack of the given presenters and settles the initial layout/render.</summary>
    public static PresenterHarness Show(
        IReadOnlyList<LeafBlockPresenter> presenters,
        string preset = nameof(TestCapabilities.KittyTruecolor),
        int columns = 40,
        int rows = 12)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
        });

        var root = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var presenter in presenters)
            root.Children.Add(presenter);

        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");
        return new PresenterHarness(host, root, [.. presenters], columns, rows);
    }

    /// <summary>
    /// The presenter-per-<see cref="BlockKind"/> selection (the WP7b <c>BlockViewBridge</c> preview):
    /// each rendered CommonMark-core kind gets its dedicated presenter; front matter is owned here;
    /// HTML, tables (M3), and the extension constructs (M4) fall to <see cref="FallbackSourcePresenter"/>.
    /// </summary>
    public static LeafBlockPresenter SelectPresenter(BlockHarness harness, int index, WrapMode wrapMode = WrapMode.WordWrap)
    {
        var block = harness.Blocks[index];
        int start = harness.Blocks.GetStartLine(index);
        var lines = Enumerable.Range(0, block.LineCount).Select(k => harness.Buffer.GetLine(start + k)).ToList();

        return block.Kind switch
        {
            BlockKind.Heading or BlockKind.Paragraph =>
                new ParagraphPresenter(lines, block.InlineRuns, block.Kind, block.HeadingLevel, wrapMode),
            BlockKind.FencedCode or BlockKind.IndentedCode =>
                new CodeBlockPresenter(lines, block.Kind, block.FenceInfo),
            BlockKind.Quote => new QuotePresenter(lines, block.InlineRuns, wrapMode),
            BlockKind.List => new ListItemPresenter(lines, block.InlineRuns, wrapMode),
            BlockKind.ThematicBreak => new RulePresenter(lines),
            BlockKind.FrontMatter => new FrontMatterPresenter(lines),
            _ => new FallbackSourcePresenter(lines, block.Kind),
        };
    }

    /// <summary>The block-0 lines for a markdown snippet (endings preserved).</summary>
    public static IReadOnlyList<Line> Lines(string text)
    {
        var buffer = new DocumentBuffer(text);
        return [.. Enumerable.Range(0, buffer.LineCount).Select(buffer.GetLine)];
    }

    // ───────────────────────────── reveal control ─────────────────────────────

    /// <summary>Activates <paramref name="block"/>'s <paramref name="activeLine"/> at <paramref name="slide"/> and settles.</summary>
    public void SetActive(int block, int? activeLine, int slide = 0)
    {
        _presenters[block].SetReveal(activeLine, slide);
        Settle();
    }

    /// <summary>Deactivates every block (all marks hidden) and settles.</summary>
    public void ClearActive()
    {
        foreach (var presenter in _presenters)
            presenter.SetReveal(null);
        Settle();
    }

    public void Settle() => Assert.True(Host.RunUntilIdle(), "the frame loop did not settle");

    // ───────────────────────────── geometry + readback ─────────────────────────────

    /// <summary>The top frame row of <paramref name="block"/> — the prefix sum of the earlier blocks' measured heights.</summary>
    public int TopRow(int block)
    {
        int top = 0;
        for (var i = 0; i < block; i++)
            top += Height(i);
        return top;
    }

    /// <summary>The measured (rendered) height of <paramref name="block"/> — the inactive layout's row count.</summary>
    public int Height(int block) => _presenters[block].DesiredSize.Rows;

    /// <summary>One composited cell.</summary>
    public Cell Cell(int column, int row) => Host.GetCell(column, row);

    /// <summary>The composited text of a frame row.</summary>
    public string Row(int row) => Host.GetRowText(row);

    /// <summary>The trimmed composited text of a frame row.</summary>
    public string RowTrimmed(int row) => Host.GetRowText(row).TrimEnd();

    /// <summary>A full snapshot of every composited cell (columns × rows) for the no-reflow cell diff.</summary>
    public Cell[,] SnapshotCells()
    {
        var cells = new Cell[Columns, Rows];
        for (var row = 0; row < Rows; row++)
            for (var column = 0; column < Columns; column++)
                cells[column, row] = Host.GetCell(column, row);
        return cells;
    }

    public void Dispose() => Host.Dispose();
}
