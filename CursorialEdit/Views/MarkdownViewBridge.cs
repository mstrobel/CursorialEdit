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

    /// <summary>The render mode (Formatted = WYSIWYG, Raw = verbatim source); drives presenter selection, caret maps, and reveal gating.</summary>
    private ViewMode _viewMode = ViewMode.Formatted;

    /// <summary>Whether the active prose line wraps in place while edited (reveal-wrap) vs. slides (Decision 9 revised). Default on.</summary>
    private bool _editWrapEnabled = true;

    /// <summary>The table cell-overflow mode (§5.6) every realized table renders under. Default <see cref="TableOverflow.Wrap"/>.</summary>
    private TableOverflow _overflowMode = TableOverflow.Wrap;

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
    /// <remarks>
    /// Switching mode drops the cached exact heights (formatted wrapped-row counts and raw line counts are
    /// unrelated) and clears the active block (raw mode has none), then raises
    /// <see cref="HeightsChanged"/> so the panel re-derives its prefix sums and extent from the line-count
    /// estimate — the realized presenters refine to exact as they re-measure. The presenter <i>swap</i>
    /// itself (formatted suite ⇄ <see cref="RawSourcePresenter"/>) is driven by the control re-realizing the
    /// panel, since the presenter type — not just its content — changes.
    /// </remarks>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value)
                return;

            _viewMode = value;
            _heights.Clear();      // formatted heights are meaningless in raw mode and vice versa — re-estimate by line count
            _activeBlockId = null; // raw mode has no active block; formatted re-reveals on the next caret publish
            HeightsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Whether the active prose line wraps in place while edited (reveal-wrap, Decision 9 revised) or slides
    /// (the pre-revision behavior). Default <see langword="true"/>. The "wrap while editing" View command
    /// (M5) drives this; toggling it re-applies the policy to every realized prose presenter and reflows.
    /// </summary>
    public bool EditWrapEnabled
    {
        get => _editWrapEnabled;
        set
        {
            if (_editWrapEnabled == value)
                return;

            _editWrapEnabled = value;
            foreach (var (id, presenter) in _presenters)
            {
                int idx = Blocks.IndexOf(id);
                if (idx >= 0)
                    presenter.RevealWraps = WrapReveals(Blocks[idx].Kind); // the setter re-measures → heights reflow
            }

            // Re-establish the active block's reveal under the new policy: toggling to SLIDE must recompute the
            // horizontal slide (else the active line renders slid-to-0 with the caret possibly off-screen);
            // toggling to WRAP drops the slide. The height reflow + scroll-follow then flow through HeightsChanged.
            RevealActive();
        }
    }

    /// <summary>
    /// The table cell-overflow mode (§5.6, M3.WP6): <see cref="TableOverflow.Wrap"/> (default) or
    /// <see cref="TableOverflow.Truncate"/>. Settable, mirroring <see cref="EditWrapEnabled"/> — assigning it
    /// re-lays-out every realized table (heights change) and re-reveals the caret's focused cell under the new
    /// mode. The user-facing "table overflow" command lands with M5 (do not build it here).
    /// </summary>
    public TableOverflow OverflowMode
    {
        get => _overflowMode;
        set
        {
            if (_overflowMode == value)
                return;

            _overflowMode = value;
            foreach (var (_, presenter) in _presenters)
                if (presenter is TablePresenter table)
                    table.OverflowMode = value; // re-derives the table's rows + heights (MeasuredCallback refines the extent)

            // Re-establish the focused-cell reveal / column-window under the new mode (Wrap has no focused-cell reveal;
            // Truncate un-truncates the caret's cell). The height reflow flows through the re-measure.
            RevealActive();
        }
    }

    /// <summary>Whether a block of <paramref name="kind"/> wrap-reveals its active line: prose, edit-wrap on, formatted, AND the block actually wraps.</summary>
    private bool WrapReveals(BlockKind kind) =>
        _editWrapEnabled && _viewMode == ViewMode.Formatted && IsProseKind(kind) && WrapModeFor(kind) == WrapMode.WordWrap;

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
        // A realized presenter reports the exact height it drew; an unrealized block estimates (refined the
        // instant its presenter measures — MeasuredCallback → RefreshHeight).
        var block = Blocks[index];
        if (_heights.TryGetValue(block.Id, out var height))
            return height;

        // A table renders as a taller box-drawing GRID, not one row per source line: ~1 top border +
        // (content + separator) per logical row. Estimate 2·LineCount − 1 so the scroll extent is close
        // before the table realizes (a plain LineCount estimate is systematically short → a visible scroll
        // jump when it scrolls into view); wrapping is corrected exactly on realize.
        return block.Kind == BlockKind.Table ? Math.Max(1, 2 * block.LineCount - 1) : block.LineCount;
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

        // Reveal-wrap (Decision 9, revised): a prose block wraps its active line in place while edited (so the
        // paragraph context stays visible) when edit-wrap is on; code/raw/rule/front-matter/table keep the
        // horizontal slide. The presenter primitive defaults to slide, so this is the one opt-in point.
        presenter.RevealWraps = WrapReveals(block.Kind);

        presenter.MeasuredCallback = (_, rows) => RefreshHeight(id, rows);

        // The selection is a document-level source range the caret installs on this bridge; the presenter
        // intersects it with ITS block at draw time (WP8). The closure reads the live SelectionSource, so
        // it works whether the caret attaches before or after the block realizes.
        presenter.SelectionProvider = () => SelectionSource?.GetSelection(id);

        // A table additionally reads the rectangular whole-cell selection (M3.WP8, spec §5.4): when the multi-cell
        // selection lies wholly within this table, its cells highlight as whole cells instead of the covered span.
        if (presenter is TablePresenter tablePresenter)
            tablePresenter.CellRectProvider = () => SelectionSource?.GetCellRect(id);

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

        // A block realizing under an active selection paints the highlight from its live SelectionProvider, but
        // the caret's overlay-tracking has no entry for a block realized since the last selection change — so a
        // later clear would strand the fill. Record it now so the clear re-rasters it (see OnPresenterRealized).
        SelectionSource?.OnPresenterRealized(id);

        // The caret seed above lets a later clear REACH this block, but a table keeps its OWN per-row highlight
        // tracking that the ctor built empty (SelectionProvider was null then) — seed it now from the live
        // provider so the forwarded clear re-rasters the right rows (base presenters no-op; see SeedSelectionOverlay).
        presenter.SeedSelectionOverlay();

        // Reveal the caret line the instant its block scrolls into view (the presenter did not exist when
        // the caret last moved) — otherwise the active block would render formatted until the next move.
        if (_activeBlockId == id)
            RevealActive();

        return presenter;
    }

    /// <summary>The prose block kinds whose active line wraps in place under reveal-wrap (Decision 9 revised); everything else keeps the slide.</summary>
    private static bool IsProseKind(BlockKind kind) =>
        kind is BlockKind.Paragraph or BlockKind.Heading or BlockKind.Quote or BlockKind.List;

    private LeafBlockPresenter SelectPresenter(int index, Block block)
    {
        var lines = BuildLines(index, block);

        // Raw mode: every block renders verbatim through the one raw presenter regardless of kind (§4.2).
        if (_viewMode == ViewMode.Raw)
            return new RawSourcePresenter(lines, block.Kind);

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
            BlockKind.Table => SelectTablePresenter(lines, block),
            _ => new FallbackSourcePresenter(lines, block.Kind),
        };
    }

    /// <summary>
    /// The M3.WP2 table presenter, or the dimmed-literal fallback when the block carries no Markdig table
    /// (a malformed/synthetic table block — <see cref="TableModel.Build"/> returns <see langword="null"/>).
    /// The <see cref="TableModel"/> is built here (in the app project) via the Document-project factory, so
    /// no Markdig type crosses into the view (the quarantine holds — <c>ArchitectureTests</c>).
    /// </summary>
    private LeafBlockPresenter SelectTablePresenter(IReadOnlyList<Line> lines, Block block)
    {
        string source = BlockSource(lines);
        var model = TableModel.Build(block, source);
        return model is null
            ? new FallbackSourcePresenter(lines, block.Kind)
            : new TablePresenter(lines, model, source, Math.Max(0, _wrapWidth), _overflowMode); // viewport-aware + current overflow mode from the first frame
    }

    /// <summary>A block's serialized source (lines + terminators) — the same string <c>LeafBlockPresenter.BlockText()</c> produces, so table cell spans index it.</summary>
    private static string BlockSource(IReadOnlyList<Line> lines)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
            builder.Append(line.Text).Append(line.EndingText);
        return builder.ToString();
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
        {
            // A table maps the caret INTO a cell through its composite grid map (M3.WP4), never through the
            // raw source-line run map — so click/arrow land in the right cell (and in raw mode the whole
            // surface is verbatim, so the table is a plain source block there).
            if (presenter is TablePresenter table && _viewMode == ViewMode.Formatted)
                return table.CaretMap();
            return presenter.MapForWidth(Math.Max(1, _wrapWidth));
        }

        // The caret jumped to a block outside the realized band (Ctrl+End). Build its map directly —
        // the one place a caret query realizes a block off the render band. In raw mode that is the
        // identity map (source verbatim, 1:1); in formatted mode the block's inactive map.
        if (_viewMode == ViewMode.Raw)
            return RunMapBuilder.BuildRaw(BuildLines(blockIndex, block), Math.Max(1, _wrapWidth));

        if (block.Kind == BlockKind.Table)
        {
            string tableSource = BlockSource(BuildLines(blockIndex, block));
            if (TableModel.Build(block, tableSource) is { } offBandModel)
                // Floor at 0 (not 1) to match the realized presenter's ctor: BuildForViewport(<=0) takes the
                // viewport-unaware [3,40] fallback, so a cold-start off-band caret query (before the first
                // OnViewportChanged, _wrapWidth still 0) resolves the SAME widths the presenter will, not an
                // all-MinWidth grid from a negative budget — the caret lands in the right cell.
                return TableCaretMap.Build(offBandModel, TableGridMetrics.BuildForViewport(offBandModel, Math.Max(0, _wrapWidth)), tableSource, _overflowMode);
        }

        return RunMapBuilder.Build(
            BuildLines(blockIndex, block), block.InlineRuns, block.Kind, block.HeadingLevel,
            Math.Max(1, _wrapWidth), WrapModeFor(block.Kind), activeLine: null);
    }

    /// <inheritdoc/>
    public TableModel? GetTableModel(int blockIndex)
    {
        // Raw mode renders (and edits) the table's source verbatim — no cell routing, no pipe escaping, no
        // cell-bounded delete (bug 2). GetCaretMap gates the same way, so the two stay consistent.
        if (_viewMode != ViewMode.Formatted || blockIndex < 0 || blockIndex >= Blocks.Count)
            return null;

        var block = Blocks[blockIndex];
        if (block.Kind != BlockKind.Table)
            return null;

        // Prefer the realized presenter's live overlay (it already reflects the current reconcile); build one
        // directly only for a table off the render band.
        if (_presenters.TryGetValue(block.Id, out var presenter) && presenter is TablePresenter table)
            return table.Model;

        return TableModel.Build(block, BlockSource(BuildLines(blockIndex, block)));
    }

    /// <inheritdoc/>
    public int ActiveSlide(int blockIndex, int row)
    {
        var id = Blocks[blockIndex].Id;
        if (!_presenters.TryGetValue(id, out var presenter))
            return 0;

        // A table never slides a line — but when it overflows the viewport it draws the whole grid through a
        // presenter-internal COLUMN-WINDOW (M3.WP6, FB-6 sidestep). That offset applies to every row, and the
        // caret publish subtracts it / a click adds it back here, so both stay consistent with the drawn grid —
        // returned whenever the table overflows, even before it becomes the active block (a click into an
        // off-window column needs it). A table that fits returns 0 (its map reports published cells directly).
        // Gated on Formatted (like GetCaretMap) so the two can never disagree — raw mode renders the table as
        // verbatim source with no window (and its presenter is a RawSourcePresenter there anyway).
        if (presenter is TablePresenter table)
            return _viewMode == ViewMode.Formatted ? table.WindowOffset : 0;

        if (_activeBlockId != id || presenter.ActiveLine is null)
            return 0;

        // Only the active LINE's row is slid; a hit-test on any other row of the same block gets no slide
        // (else clicking a short earlier line would offset the caret by the active line's full slide). In
        // raw mode the identity map has no "active row" flag, but raw is one row per source line, so the
        // active row is simply the active line index.
        var map = presenter.MapForWidth(Math.Max(1, _wrapWidth));
        bool activeRow = _viewMode == ViewMode.Raw ? row == presenter.ActiveLine : map.IsActiveRow(row);
        return activeRow ? presenter.SlideOffset : 0;
    }

    /// <inheritdoc/>
    public int VisibleWidth(int blockIndex)
    {
        // A windowed table publishes into its visible sub-grid, so its drawn width is the caret-publish clip bound;
        // every other block (and raw mode) uses the viewport. Gated on Formatted like ActiveSlide so they agree.
        if (_viewMode == ViewMode.Formatted && blockIndex >= 0 && blockIndex < Blocks.Count
            && _presenters.TryGetValue(Blocks[blockIndex].Id, out var presenter) && presenter is TablePresenter table)
            return table.RenderedWidth;

        return Math.Max(1, _wrapWidth);
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
        // Raw mode shows every mark literally (BuildRaw ignores the active line) and paints no well
        // (RawSourcePresenter overrides it away) — but it STILL honors the horizontal SLIDE below, so the
        // caret's line scrolls to stay visible when a raw source line is wider than the viewport (a raw
        // line does not wrap). So the active-line + slide computation runs in both modes.
        if (_caret is not { } caret || Blocks.Count == 0)
            return;

        int line = Math.Clamp(caret.Line, 0, Math.Max(0, _buffer.LineCount - 1));
        int blockIndex = Blocks.IndexOfLine(line);
        var activeId = Blocks[blockIndex].Id;
        int startLine = Blocks.GetStartLine(blockIndex);
        int lineInBlock = line - startLine;

        // Hide the marks of the block that WAS active (only when it changed — re-clearing an already
        // inactive presenter would needlessly re-raster its zone).
        if (_activeBlockId is { } previous && previous != activeId)
        {
            if (_presenters.TryGetValue(previous, out var prior))
            {
                prior.SetReveal(null); // realized → re-measures to its inactive height (drops any wrap-reveal inflation)
                if (prior is TablePresenter priorTable)
                    priorTable.ClearActiveCell(); // re-truncate the cell we were editing (Truncate reveal-on-focus)
            }
            else
                // De-realized while active: its presenter is gone, so it can't re-measure — but its cached
                // height may be the INFLATED wrap-reveal (revealed-marks) height. Drop it so the block
                // re-estimates and refines to its inactive height when it re-realizes (else the extent stays
                // too tall below it — the wrap-reveal analogue of the slide path's height-invariance).
                _heights.Remove(previous);
        }

        _activeBlockId = activeId;

        if (!_presenters.TryGetValue(activeId, out var active))
            return; // active block is off the realized band — CreatePresenter reveals it when it realizes

        // A table draws its own grid (no revealed source line, no line slide): mark it active for the record, then
        // follow the caret INSIDE the grid — reveal the focused cell (Truncate) and scroll the presenter-internal
        // column-window so the caret's column is on-screen (M3.WP6). The caret map already lands the caret in the cell.
        if (active is TablePresenter tablePresenter)
        {
            active.SetReveal(lineInBlock, 0);
            tablePresenter.EnsureColumnVisible(BlockRelativeCaretOffset(caret, line, startLine));
            return;
        }

        // Wrap-reveal: the active line wraps within the viewport, so there is no horizontal slide — the caret
        // stays visible by ordinary vertical scroll-follow. Just reveal the line (height reflows via measure).
        if (active.RevealWraps)
        {
            active.SetReveal(lineInBlock, 0);
            return;
        }

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
        var lines = BuildLines(index, block);

        // A table re-derives its whole overlay (fresh Markdig cell spans + widths) and reconciles its per-row
        // children in place; every other kind re-derives its inline runs. A block that flipped INTO a table
        // (or out of one) changed kind → new BlockId → Added/Removed, so it never reaches this same-id path.
        if (presenter is TablePresenter table)
        {
            string source = BlockSource(lines);
            if (TableModel.Build(block, source) is { } model)
            {
                table.SetTable(lines, model, source);
                return;
            }
        }

        presenter.SetContent(lines, block.InlineRuns, block.HeadingLevel);
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
