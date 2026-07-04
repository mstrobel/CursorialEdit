using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Blocks;
using CursorialEdit.Tests.Layout;

namespace CursorialEdit.Tests.Reveal;

/// <summary>
/// The M2.WP6 reveal spike harness (risk R1): a headless <see cref="UITestHost"/> showing a vertical
/// <see cref="StackPanel"/> of <see cref="ParagraphPresenter"/>s — one per block — driven through the
/// <b>real frame loop</b>. Tests toggle each block's reveal state (active source line + horizontal
/// slide) and read back the composited cells to prove that reveal changes no cell outside the active
/// block, that a clip edge never splits a wide cluster, and that the caret stays on a grapheme within
/// the visible span. Presenters are built either from plain text (no marks) or from real Markdig
/// inline runs (via <see cref="BlockHarness"/>), so the RunMap → slide → clip → draw path renders the
/// genuine projection.
/// </summary>
internal sealed class RevealHarness : IDisposable
{
    private readonly ParagraphPresenter[] _presenters;

    private RevealHarness(UITestHost host, StackPanel root, ParagraphPresenter[] presenters, int columns, int rows)
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

    public IReadOnlyList<ParagraphPresenter> Presenters => _presenters;

    /// <summary>Shows a vertical stack of the given presenters and settles the initial layout/render.</summary>
    public static RevealHarness Show(
        IReadOnlyList<ParagraphPresenter> presenters,
        string preset = nameof(TestCapabilities.KittyTruecolor),
        int columns = 40,
        int rows = 8)
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
        return new RevealHarness(host, root, [.. presenters], columns, rows);
    }

    // ───────────────────────────── presenter builders ─────────────────────────────

    /// <summary>A plain-text paragraph presenter (no marks — only <see cref="RunKind.Text"/> runs).</summary>
    public static ParagraphPresenter Paragraph(string text, WrapMode mode = WrapMode.WordWrap)
        => new(RunMapHarness.Lines(text), [], BlockKind.Paragraph, wrapMode: mode);

    /// <summary>
    /// A presenter over a single parsed markdown block (block 0) — its real
    /// <see cref="Block.InlineRuns"/> drive the mark classification, so emphasis/heading/code marks
    /// hide when inactive and reveal when active.
    /// </summary>
    public static ParagraphPresenter Markdown(string markdown, WrapMode mode = WrapMode.WordWrap)
    {
        var (lines, runs, kind, heading) = ParseBlock(markdown);
        return new ParagraphPresenter(lines, runs, kind, heading, mode);
    }

    /// <summary>The parsed block-0 inputs for a markdown snippet.</summary>
    public static (IReadOnlyList<Line> Lines, IReadOnlyList<InlineRun> Runs, BlockKind Kind, int? Heading) ParseBlock(string markdown)
    {
        var harness = BlockHarness.Create(markdown);
        int start = harness.Blocks.GetStartLine(0);
        var block = harness.Blocks[0];
        var lines = Enumerable.Range(0, block.LineCount).Select(k => harness.Buffer.GetLine(start + k)).ToList();
        return (lines, block.InlineRuns, block.Kind, block.HeadingLevel);
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

    /// <summary>Replaces a block's plain text (the typing path) and settles.</summary>
    public void SetText(int block, string text)
    {
        _presenters[block].SetContent(RunMapHarness.Lines(text), []);
        Settle();
    }

    public void Settle() => Assert.True(Host.RunUntilIdle(), "the frame loop did not settle");

    // ───────────────────────────── geometry + readback ─────────────────────────────

    /// <summary>The top frame row of <paramref name="block"/> — the prefix sum of the earlier blocks' rendered row counts.</summary>
    public int TopRow(int block)
    {
        int top = 0;
        for (var i = 0; i < block; i++)
            top += _presenters[i].MapForWidth(Columns).RowCount;
        return top;
    }

    /// <summary>One composited cell.</summary>
    public Cell Cell(int column, int row) => Host.GetCell(column, row);

    /// <summary>The composited text of a frame row.</summary>
    public string Row(int row) => Host.GetRowText(row);

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
