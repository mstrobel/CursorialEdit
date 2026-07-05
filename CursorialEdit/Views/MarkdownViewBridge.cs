using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;

using Block = CursorialEdit.Document.Model.Block;

namespace CursorialEdit.Views;

/// <summary>
/// The M2.WP7b production bridge (implementation-plan §7 WP7): binds the real
/// <see cref="MarkdigBlockProducer"/> to the view surface with the full <see cref="LeafBlockPresenter"/>
/// suite — one presenter per leaf block, selected by <see cref="BlockKind"/> and reconciled by
/// <see cref="BlockId"/> on every <see cref="BlockListChange"/>. It is the markdown counterpart of the
/// M1 plain-text <see cref="BlockViewBridge"/>: it serves the panel's block heights (from what the
/// presenters actually draw), owns <b>reveal-on-edit</b> coordination for the caret's active line
/// (Decision 9), and forwards a front-matter fold's height change to the scroll extent
/// (§3.2 resolution 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Presenter selection (the production switch, lifted from <c>PresenterHarness.SelectPresenter</c>).</b>
/// Heading/Paragraph → <see cref="ParagraphPresenter"/>; Fenced/Indented code → <see cref="CodeBlockPresenter"/>
/// (with the fence info); Quote → <see cref="QuotePresenter"/>; List → <see cref="ListItemPresenter"/>;
/// ThematicBreak → <see cref="RulePresenter"/>; FrontMatter → <see cref="FrontMatterPresenter"/>; everything
/// else (HTML §2.4, tables until M3, the extension constructs until M4) → <see cref="FallbackSourcePresenter"/>.
/// </para>
/// <para>
/// <b>Reconciliation by <see cref="BlockId"/>.</b> <c>Reused</c> blocks keep their presenter untouched;
/// <c>Changed</c> blocks re-derive their source lines + inline runs through
/// <see cref="LeafBlockPresenter.SetContent"/> (same element — no tear-down per keystroke); <c>Invalidated</c>
/// blocks (unchanged source, stale inlines from a definition change) likewise re-derive their inlines;
/// <c>Added</c> blocks realize through the factory on the next measure; <c>Removed</c> blocks' presenters are
/// torn down by the panel and dropped here via the torn-down callback. A same-height edit re-rasters exactly
/// the touched presenter's zone (Decision 7); a structural change re-derives the panel's prefix sums.
/// </para>
/// <para>
/// <b>Heights (estimate-then-refine, lazy per Decision 5).</b> An unrealized block's height is estimated as
/// its line count; a realized presenter reports its exact rendered height through
/// <see cref="LeafBlockPresenter.MeasuredCallback"/> during measure, and the bridge refines the panel's
/// prefix sums (and thus the published extent) when it moves — so opening a document never realizes every
/// block's inlines just to lay it out.
/// </para>
/// <para>
/// <b>Reveal (the <c>RevealDemoView</c> pattern, productionized).</b> <see cref="OnCaretPositioned"/> marks
/// the caret's block active — its caret line renders un-wrapped and slid (<see cref="HorizontalSlide"/>) to
/// keep the caret visible — and hides the previously-active block's marks, so a caret move re-rasters at most
/// two zones and never reflows a sibling (height-invariance, §4.1).
/// </para>
/// <para>All members are UI-thread-only, like the producer and buffer they observe.</para>
/// </remarks>
public sealed class MarkdownViewBridge : IEditorViewSource
{
    private readonly IDocumentBuffer _buffer;
    private readonly MarkdigBlockProducer _producer;
    private readonly Dictionary<BlockId, LeafBlockPresenter> _presenters = [];

    /// <summary>Exact heights (rows) of blocks whose presenters have measured, keyed by id; absent = estimate by line count.</summary>
    private readonly Dictionary<BlockId, int> _heights = [];

    /// <summary>The wrap width in cells; 0 until the first viewport arrives (heights are line-count estimates).</summary>
    private int _wrapWidth;

    /// <summary>The caret's current source position (drives reveal); <see langword="null"/> until the caret first moves/publishes.</summary>
    private TextPosition? _caret;

    /// <summary>The block whose line is currently revealed (the active block), or <see langword="null"/> when none.</summary>
    private BlockId? _activeBlockId;

    /// <summary>
    /// Creates the bridge over <paramref name="producer"/>'s block list, reading source lines from
    /// <paramref name="buffer"/>, and subscribes to the producer's change feed.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public MarkdownViewBridge(IDocumentBuffer buffer, MarkdigBlockProducer producer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(producer);

        _buffer = buffer;
        _producer = producer;
        producer.Changed += OnBlocksChanged;
    }

    /// <inheritdoc/>
    public BlockList Blocks => _producer.Blocks;

    /// <inheritdoc/>
    public int WrapWidth => _wrapWidth;

    /// <inheritdoc/>
    public ISelectionSource? SelectionSource { get; set; }

    /// <summary>Cumulative count of presenters created (test observability): reconciliation reuses, never recreates, an unchanged block.</summary>
    internal int PresenterCreateCount { get; private set; }

    /// <summary>The live presenter for <paramref name="block"/>, when one is realized (test observability).</summary>
    internal LeafBlockPresenter? GetPresenter(BlockId block) => _presenters.GetValueOrDefault(block);

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<BlockId, UIElement>> RealizedPresenters =>
        _presenters.Select(kv => new KeyValuePair<BlockId, UIElement>(kv.Key, kv.Value));

    // ───────────────────────────── IBlockHeightSource ─────────────────────────────

    /// <inheritdoc/>
    public int BlockCount => Blocks.Count;

    /// <inheritdoc/>
    public int GetBlockHeight(int index)
    {
        // A realized presenter reports the exact height it drew; an unrealized block estimates by line
        // count (refined the instant its presenter measures — MeasuredCallback → RefreshHeight).
        var block = Blocks[index];
        return _heights.TryGetValue(block.Id, out var height) ? height : block.LineCount;
    }

    /// <inheritdoc/>
    public event Action? HeightsChanged;

    // ───────────────────────────── IBlockViewSource ─────────────────────────────

    /// <inheritdoc/>
    public long GetBlockIdentity(int index) => Blocks[index].Id.Value;

    /// <inheritdoc/>
    public int IndexOfBlock(long identity) => Blocks.IndexOf(new BlockId(identity));

    /// <inheritdoc/>
    public void OnViewportChanged(Size viewport)
    {
        int width = Math.Max(1, viewport.Columns);
        if (width == _wrapWidth)
            return;

        _wrapWidth = width;

        // The realized presenters re-measure at the new width through the ordinary layout pass (the SCP
        // re-measures the host on resize), and their MeasuredCallback refines heights. Nothing to do here
        // but record the width — an unmapped block keeps its line-count estimate until it realizes.
    }

    // ───────────────────────────── presenter factory + registry ─────────────────────────────

    /// <inheritdoc/>
    public UIElement CreatePresenter(int index)
    {
        var block = Blocks[index];
        var id = block.Id;
        var presenter = SelectPresenter(index, block);

        presenter.MeasuredCallback = (_, rows) => RefreshHeight(id, rows);

        if (presenter is FrontMatterPresenter frontMatter)
        {
            void OnFold() => OnBlockHeightChanged(id);
            frontMatter.HeightChanged += OnFold;
            presenter.TornDownCallback = p =>
            {
                frontMatter.HeightChanged -= OnFold;
                Deregister(id, p);
            };
        }
        else
        {
            presenter.TornDownCallback = p => Deregister(id, p);
        }

        _presenters[id] = presenter;
        PresenterCreateCount++;

        // Reveal the caret line the instant its block scrolls into view (the presenter did not exist when
        // the caret last moved) — otherwise the active block would render formatted until the next move.
        if (_activeBlockId == id)
            RevealActive();

        return presenter;
    }

    private LeafBlockPresenter SelectPresenter(int index, Block block)
    {
        var lines = BuildLines(index, block);
        return block.Kind switch
        {
            BlockKind.Heading or BlockKind.Paragraph =>
                new ParagraphPresenter(lines, block.InlineRuns, block.Kind, block.HeadingLevel),
            BlockKind.FencedCode or BlockKind.IndentedCode =>
                new CodeBlockPresenter(lines, block.Kind, block.FenceInfo),
            BlockKind.Quote => new QuotePresenter(lines, block.InlineRuns),
            BlockKind.List => new ListItemPresenter(lines, block.InlineRuns),
            BlockKind.ThematicBreak => new RulePresenter(lines),
            BlockKind.FrontMatter => new FrontMatterPresenter(lines),
            _ => new FallbackSourcePresenter(lines, block.Kind),
        };
    }

    private void Deregister(BlockId id, LeafBlockPresenter presenter)
    {
        // Remove only the current registration — a re-realized block may already own a newer element.
        if (_presenters.TryGetValue(id, out var registered) && ReferenceEquals(registered, presenter))
            _presenters.Remove(id);
    }

    // ───────────────────────────── caret map + reveal (IEditorViewSource) ─────────────────────────────

    /// <inheritdoc/>
    public ICaretMap GetCaretMap(int blockIndex)
    {
        var block = Blocks[blockIndex];
        if (_presenters.TryGetValue(block.Id, out var presenter))
            return presenter.MapForWidth(Math.Max(1, _wrapWidth));

        // The caret jumped to a block outside the realized band (Ctrl+End). Build its inactive map
        // directly — the one place a caret query realizes a block's inlines off the render band.
        return RunMapBuilder.Build(
            BuildLines(blockIndex, block), block.InlineRuns, block.Kind, block.HeadingLevel,
            Math.Max(1, _wrapWidth), WrapModeFor(block.Kind), activeLine: null);
    }

    /// <inheritdoc/>
    public int ActiveSlide(int blockIndex, int row)
    {
        var id = Blocks[blockIndex].Id;
        if (_activeBlockId != id || !_presenters.TryGetValue(id, out var presenter) || presenter.ActiveLine is null)
            return 0;

        // Only the active LINE's row is slid; a hit-test on any other row of the same block gets no slide
        // (else clicking a short earlier line would offset the caret by the active line's full slide).
        var map = presenter.MapForWidth(Math.Max(1, _wrapWidth));
        return map.IsActiveRow(row) ? presenter.SlideOffset : 0;
    }

    /// <inheritdoc/>
    public void OnCaretPositioned(TextPosition caret)
    {
        _caret = caret;
        RevealActive();
    }

    /// <summary>
    /// Reveals the caret's block line (slid to keep the caret visible) and hides the previously-active
    /// block's marks — the <c>RevealDemoView</c> reveal loop, productionized. Touches at most the previous
    /// and current active presenters, so a caret move re-rasters at most two zones and never reflows a
    /// sibling (height-invariance — <see cref="LeafBlockPresenter.SetReveal"/> keeps the block's height).
    /// </summary>
    private void RevealActive()
    {
        if (_caret is not { } caret || Blocks.Count == 0)
            return;

        int line = Math.Clamp(caret.Line, 0, Math.Max(0, _buffer.LineCount - 1));
        int blockIndex = Blocks.IndexOfLine(line);
        var activeId = Blocks[blockIndex].Id;
        int startLine = Blocks.GetStartLine(blockIndex);
        int lineInBlock = line - startLine;

        // Hide the marks of the block that WAS active (only when it changed — re-clearing an already
        // inactive presenter would needlessly re-raster its zone).
        if (_activeBlockId is { } previous && previous != activeId
            && _presenters.TryGetValue(previous, out var prior))
        {
            prior.SetReveal(null);
        }

        _activeBlockId = activeId;

        if (!_presenters.TryGetValue(activeId, out var active))
            return; // active block is off the realized band — CreatePresenter reveals it when it realizes

        int viewport = Math.Max(1, _wrapWidth);
        int prevSlide = active.SlideOffset;

        active.SetReveal(lineInBlock, prevSlide); // establish the revealed (un-wrapped) active-line map
        var map = active.MapForWidth(viewport);
        int rel = BlockRelativeCaretOffset(caret, line, startLine);
        var (row, caretCell) = map.Locate(rel);
        int slide = HorizontalSlide.Compute(prevSlide, caretCell, map.RowWidth(row), viewport);
        active.SetReveal(lineInBlock, slide);
    }

    /// <summary>The caret's block-relative UTF-16 source offset (the origin the block's run map is measured from).</summary>
    private int BlockRelativeCaretOffset(TextPosition caret, int line, int startLine)
    {
        int col = Math.Clamp(caret.Col, 0, _buffer.GetLine(line).Text.Length);
        int blockStart = _buffer.GetOffset(new TextPosition(startLine, 0));
        return _buffer.GetOffset(new TextPosition(line, col)) - blockStart;
    }

    // ───────────────────────────── reconciliation ─────────────────────────────

    private void OnBlocksChanged(BlockListChange change)
    {
        bool structureMoved = change.Added.Count > 0 || change.Removed.Count > 0;

        foreach (var id in change.Removed)
            _heights.Remove(id); // the presenter (if realized) is torn down by the panel → Deregister

        foreach (var id in change.Changed)
            ReDeriveContent(id);

        foreach (var id in change.Invalidated)
            ReDeriveContent(id); // unchanged source, fresh inlines from a definition change (§2.2 step 4)

        // Re-derive the panel's prefix sums when the row layout moved: a structural change (Added/Removed),
        // OR any splice with a non-zero line delta — a Changed block whose LineCount grew/shrank shifts
        // every block below it, and if that block is UNREALIZED its estimate never refines through the
        // MeasuredCallback, so the extent would go stale without this. A same-line-count content edit
        // (LineShift == 0) still refines the touched realized zone through MeasuredCallback alone.
        if (structureMoved || change.LineShift != 0)
            HeightsChanged?.Invoke();
    }

    private void ReDeriveContent(BlockId id)
    {
        if (!_presenters.TryGetValue(id, out var presenter))
            return; // not realized — it re-derives from the fresh block when it next realizes

        int index = Blocks.IndexOf(id);
        if (index < 0)
            return; // left the list between the change and this walk (defensive)

        var block = Blocks[index];
        presenter.SetContent(BuildLines(index, block), block.InlineRuns, block.HeadingLevel);
    }

    // ───────────────────────────── heights ─────────────────────────────

    private void RefreshHeight(BlockId id, int rows)
    {
        int served;
        if (_heights.TryGetValue(id, out var existing))
        {
            served = existing;
        }
        else
        {
            int index = Blocks.IndexOf(id); // the pre-refine estimate GetBlockHeight served the panel
            served = index >= 0 ? Blocks[index].LineCount : rows;
        }

        _heights[id] = rows;
        if (rows != served)
            HeightsChanged?.Invoke(); // realize-time / rewrap / fold refine — re-derive prefix sums + extent
    }

    private void OnBlockHeightChanged(BlockId id)
    {
        // A front-matter fold toggled (§3.2 resolution 5): re-measure gives the new height through
        // RefreshHeight, and this forwards the extent invalidation the moment the fold changes.
        if (_presenters.TryGetValue(id, out var presenter))
        {
            _heights[id] = presenter.MeasuredHeight(Math.Max(1, _wrapWidth));
            HeightsChanged?.Invoke();
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private List<Line> BuildLines(int index, Block block)
    {
        int startLine = Blocks.GetStartLine(index);
        var lines = new List<Line>(block.LineCount);
        for (var k = 0; k < block.LineCount; k++)
            lines.Add(_buffer.GetLine(startLine + k));
        return lines;
    }

    /// <summary>The wrap mode each kind's presenter uses (WordWrap for prose, NoWrap for code/rule/front-matter/fallback) — kept in step with <see cref="SelectPresenter"/>.</summary>
    private static WrapMode WrapModeFor(BlockKind kind) => kind switch
    {
        BlockKind.Heading or BlockKind.Paragraph or BlockKind.Quote or BlockKind.List => WrapMode.WordWrap,
        _ => WrapMode.NoWrap,
    };
}
