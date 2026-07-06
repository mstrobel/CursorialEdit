using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// The M2.WP7 leaf-block presenter base (architecture Decision 7 / 8 / 9): a render-only
/// <see cref="UIElement"/> that drives the whole <b>RunMap → slide → clip → draw</b> path for one
/// leaf block and every construct kind shares. Given a block's source lines, its lazily-realized
/// inline runs, and a reveal state (the caret's active source line + the horizontal slide for that
/// line's one un-wrapped row), it draws each visual row: inactive rows render <b>formatted</b>
/// (syntax marks zero-width, emphasis/links/code styled, structural markers as synthetic glyphs) and
/// the active row renders <b>revealed, slid, and grapheme-snapped clipped</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Height-invariance under reveal (§4.1 / WP6 finding 3).</b> A block's rendered height is the
/// <i>inactive</i> layout's row count — the height it has when no line is being edited. When a line is
/// active it renders un-wrapped as one slid row, but a line that wrapped to N rows while hidden keeps
/// its N-row footprint: the presenter draws the revealed row at the line's first inactive row and
/// leaves the freed rows blank. So revealing never shrinks the block and shifts a sibling
/// (<see cref="MeasuredRowCount"/> is independent of <see cref="ActiveLine"/>).
/// </para>
/// <para>
/// <b>Render boundary.</b> <see cref="UIElement.IsRenderBoundary"/> is <see langword="true"/>, so
/// toggling reveal re-rasters exactly this zone. <see cref="RenderCount"/> is the raster observable
/// the reveal gate diffs against. Subclasses specialize by kind: <see cref="ParagraphPresenter"/>
/// (paragraphs + headings), <see cref="QuotePresenter"/>, <see cref="ListItemPresenter"/>,
/// <see cref="CodeBlockPresenter"/>, <see cref="RulePresenter"/>, <see cref="FrontMatterPresenter"/>,
/// and <see cref="FallbackSourcePresenter"/> — sharing this reveal/clip/height machinery and
/// overriding only their decoration (fills, glyph colors, fold, dimmed-literal).
/// </para>
/// </remarks>
public abstract class LeafBlockPresenter : UIElement
{
    private static readonly string LeftIndicatorGlyph = ClipCell.LeftGlyph.ToString();
    private static readonly string RightIndicatorGlyph = ClipCell.RightGlyph.ToString();

    private readonly BlockKind _kind;
    private int? _headingLevel; // refreshed by SetContent — a same-kind edit can change the heading level
    private readonly WrapMode _wrapMode;

    private IReadOnlyList<Line> _lines;
    private IReadOnlyList<InlineRun> _inlineRuns;
    private string? _blockText;
    private int[]? _lineStarts; // cached block-relative start offset of each source line (prefix sums)

    private int? _activeLine;
    private int _slideOffset;
    private bool _revealWraps; // false = slide reveal (default); true = wrap-in-place reveal (prose, edit-wrap)

    private RunMap? _activeMap;
    private int _activeWidth = -1;
    private int? _activeMapActive = -2; // sentinel distinct from every real int? and from null
    private bool _activeMapWraps;       // the reveal policy the cached active map was built with

    private RunMap? _inactiveMap;
    private int _inactiveWidth = -1;

    // ── selection paint state, resolved once per Render (WP11b) ──
    // The selection is composed INTO the per-run/per-cell DrawText (the M1 PlainTextPresenter model), never
    // a separate background scrim: an opaque run background (inline/code-block fill) can no longer punch a
    // hole through the highlight, and the NoColor tier degrades to TextAttributes.Inverse in the cell style
    // (a scrim's Inverse is overwritten by the glyph draw, so it must ride the glyph's own cells).
    private bool _selectionActive;   // a non-empty selection is painting this pass
    private bool _selectionNoColor;  // NoColor tier → Inverse instead of a fill brush
    private IBrush? _selectionBrush;  // the selection fill (color tiers); null on NoColor
    private int _selectionStart;     // block-relative source range the SelectionProvider …
    private int _selectionEnd;       // … reported this pass ([_selectionStart, _selectionEnd))

    /// <summary>Creates the presenter for a block's <paramref name="lines"/>.</summary>
    /// <param name="lines">The block's source lines (≥ 1), buffer-split with endings preserved.</param>
    /// <param name="inlineRuns">The block's block-relative inline runs (<see cref="Block.InlineRuns"/>); empty for plain text/code.</param>
    /// <param name="kind">The block's structural kind (drives heading/list/quote mark derivation).</param>
    /// <param name="headingLevel">The heading level when <paramref name="kind"/> is <see cref="BlockKind.Heading"/>; otherwise ignored.</param>
    /// <param name="wrapMode">Wrap-on (<see cref="WrapMode.WordWrap"/>, default) or wrap-off (<see cref="WrapMode.NoWrap"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="inlineRuns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    protected LeafBlockPresenter(
        IReadOnlyList<Line> lines,
        IReadOnlyList<InlineRun> inlineRuns,
        BlockKind kind,
        int? headingLevel = null,
        WrapMode wrapMode = WrapMode.WordWrap)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(inlineRuns);
        if (lines.Count == 0)
            throw new ArgumentException("A block owns at least one line.", nameof(lines));

        _lines = lines;
        _inlineRuns = inlineRuns;
        _kind = kind;
        _headingLevel = headingLevel;
        _wrapMode = wrapMode;
        IsRenderBoundary = true; // Decision 7 — reveal touches one zone, never a band
        AssignStyleClasses(); // §18.2 — md-* selector addressability (assigned before the element enters a tree)
    }

    // The md-* style classes (architecture §2.3 / spec §18.2) that make each construct addressable by the
    // framework's class-selector mechanism — the same spine caps-* degradation keys off. Assigned in the
    // constructor (no root yet, so no style re-match churn); the presenters resolve their Md.* tokens by
    // resource key (the idiomatic path for imperative DrawText presenters), the class naming which token a
    // presenter consumes (md-h1 → Md.Heading.1).
    private void AssignStyleClasses()
    {
        switch (_kind)
        {
            case BlockKind.Heading:
                Classes.Add(Themes.MdStyleClasses.Heading(_headingLevel ?? 1));
                break;
            case BlockKind.FencedCode:
            case BlockKind.IndentedCode:
                Classes.Add(Themes.MdStyleClasses.Code);
                break;
            case BlockKind.Quote:
                Classes.Add(Themes.MdStyleClasses.Quote);
                break;
            case BlockKind.FrontMatter:
                Classes.Add(Themes.MdStyleClasses.FrontMatter);
                break;
        }
    }

    /// <summary>The revealed active source line, or <see langword="null"/> when the block is inactive (all marks hidden).</summary>
    public int? ActiveLine => _activeLine;

    /// <summary>The horizontal slide offset applied to the active line's one un-wrapped row (cells).</summary>
    public int SlideOffset => _slideOffset;

    /// <summary>
    /// The reveal policy (Decision 9, revised): <see langword="false"/> (default) = <b>slide</b> — the active
    /// line un-wraps to one horizontally-slid row, line-count invariant (code/raw/table cells, and prose when
    /// edit-wrap is off). <see langword="true"/> = <b>wrap-reveal</b> — the active prose line wraps in place
    /// with its marks shown, and the block <b>reflows</b> (its height varies while active), so the surrounding
    /// paragraph context stays visible while editing. The production bridge opts prose blocks into this when
    /// edit-wrap is on and the block wraps; the presenter primitive defaults to slide.
    /// </summary>
    public bool RevealWraps
    {
        get => _revealWraps;
        set
        {
            if (_revealWraps == value)
                return;
            _revealWraps = value;
            InvalidateMeasure(); // the height authority changes (inactive vs active map)
            InvalidateVisual();
        }
    }

    /// <summary>Number of <see cref="Render"/> calls — the reveal raster observable (toggling reveal re-rasters exactly this zone).</summary>
    public int RenderCount { get; private set; }

    /// <summary>
    /// De-realization hook for the WP7b <c>MarkdownViewBridge</c>'s presenter registry: invoked once
    /// from <see cref="UIElement.TearDown"/> so the bridge never keeps a dead presenter (mirrors the
    /// M1 <c>PlainTextPresenter</c>). Unset by default (the harness stacks presenters directly).
    /// </summary>
    internal Action<LeafBlockPresenter>? TornDownCallback { get; set; }

    /// <summary>
    /// Height-refine hook for the WP7b bridge (mirrors <c>BlockViewBridge</c>'s realize-time refine):
    /// invoked from <see cref="MeasureOverride"/> with the row count just measured, so the bridge learns
    /// a realized block's exact (possibly re-wrapped or folded) height and reconciles the panel's prefix
    /// sums. Unset by default (the harness measures presenters directly).
    /// </summary>
    internal Action<LeafBlockPresenter, int>? MeasuredCallback { get; set; }

    /// <summary>
    /// The document-selection probe for this block (M2.WP8): the WP7b bridge sets it to
    /// <c>() =&gt; SelectionSource?.GetSelection(id)</c> so the presenter can intersect the live
    /// document selection with its own block <b>at draw time</b> (architecture §2.3/§2.4) and paint the
    /// selection fill across the selected cells. Unset by default (no selection is painted).
    /// </summary>
    internal Func<(int Start, int End)?>? SelectionProvider { get; set; }

    /// <summary>
    /// Re-rasters this presenter's on-screen selection overlay after the document selection changed (the
    /// caret's per-block invalidation route). The default re-rasters the presenter's own zone — correct for
    /// every presenter that paints its own rows. A presenter whose visible content lives in child render
    /// boundaries (the <see cref="TablePresenter"/>, whose rows draw and highlight the cells) overrides this
    /// to forward the invalidation to those children, since its own zone paints nothing.
    /// </summary>
    internal virtual void InvalidateSelectionOverlay() => InvalidateVisual();

    /// <summary>
    /// The block's rendered height in terminal rows at <paramref name="width"/> cells — the height the
    /// WP7b bridge feeds the panel's prefix sums so slot heights match what this presenter draws (the
    /// inactive, reveal-invariant row count, folded for front matter). Public projection of
    /// <see cref="MeasuredRowCount"/>.
    /// </summary>
    public int MeasuredHeight(int width) => MeasuredRowCount(Math.Max(1, width));

    /// <summary>The block's structural kind.</summary>
    protected BlockKind Kind => _kind;

    /// <summary>The heading level (1–6) when <see cref="Kind"/> is <see cref="BlockKind.Heading"/>; otherwise <see langword="null"/>.</summary>
    protected int? HeadingLevel => _headingLevel;

    /// <summary>The block's source lines.</summary>
    protected IReadOnlyList<Line> Lines => _lines;

    /// <summary>The block's block-relative inline runs.</summary>
    protected IReadOnlyList<InlineRun> InlineRuns => _inlineRuns;

    /// <summary>
    /// Sets the reveal state: <paramref name="activeLine"/> is the caret's source line (revealed,
    /// un-wrapped, slidable) or <see langword="null"/> to deactivate the block; <paramref name="slideOffset"/>
    /// is the caret-visibility slide for the active row (<see cref="HorizontalSlide.Compute"/>).
    /// Never changes the block's measured height (Decision 9) — only re-rasters this zone.
    /// </summary>
    public void SetReveal(int? activeLine, int slideOffset = 0)
    {
        bool activeChanged = _activeLine != activeLine;
        _activeLine = activeLine;
        _slideOffset = Math.Max(0, slideOffset);

        // In wrap-reveal the active line wraps in place, so activating/deactivating (or moving to a line that
        // wraps to a different depth) changes the block's height — re-measure so the reflow propagates through
        // the panel's prefix sums. Slide-reveal is height-invariant (Decision 9), so it only re-rasters.
        if (_revealWraps && activeChanged)
            InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Replaces the block's source lines and inline runs (the typing/edit path): the map caches and
    /// memoized block text drop, and the zone re-measures and re-rasters.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="inlineRuns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    public void SetContent(IReadOnlyList<Line> lines, IReadOnlyList<InlineRun> inlineRuns)
        => SetContent(lines, inlineRuns, _headingLevel);

    /// <summary>
    /// As <see cref="SetContent(IReadOnlyList{Line}, IReadOnlyList{InlineRun})"/>, but also refreshes the
    /// heading level — a same-kind edit (<c>##</c>→<c>###</c>) keeps the block's identity and is reconciled
    /// in place, so the level (and the code language, via <see cref="OnContentChanged"/>) must update too or
    /// the block renders with stale color/weight/highlighting until it is torn down and re-realized.
    /// </summary>
    public void SetContent(IReadOnlyList<Line> lines, IReadOnlyList<InlineRun> inlineRuns, int? headingLevel)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(inlineRuns);
        if (lines.Count == 0)
            throw new ArgumentException("A block owns at least one line.", nameof(lines));

        _lines = lines;
        _inlineRuns = inlineRuns;
        _headingLevel = headingLevel;
        _blockText = null;
        _lineStarts = null;
        _activeMap = null;
        _activeWidth = -1;
        _inactiveMap = null;
        _inactiveWidth = -1;
        OnContentChanged();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Hook for subclasses to drop derived caches when the block source changes.</summary>
    protected virtual void OnContentChanged() { }

    /// <summary>
    /// The run map for the current reveal state at <paramref name="width"/> cells — the map a caret
    /// owner (WP8) computes the caret cell and the <see cref="HorizontalSlide"/> from. On the active
    /// line this is the un-wrapped, slidable layout.
    /// </summary>
    public RunMap MapForWidth(int width)
    {
        width = Math.Max(1, width);
        if (_activeMap is { } cached && _activeWidth == width && _activeMapActive == _activeLine && _activeMapWraps == _revealWraps)
            return cached;

        var map = BuildMap(width, _activeLine);
        _activeMap = map;
        _activeWidth = width;
        _activeMapActive = _activeLine;
        _activeMapWraps = _revealWraps;
        return map;
    }

    /// <summary>
    /// The <b>inactive</b> layout at <paramref name="width"/> cells (all marks hidden) — the height
    /// authority and the source of every non-active line's row positions, so reveal is height-invariant.
    /// </summary>
    protected RunMap InactiveMapForWidth(int width)
    {
        width = Math.Max(1, width);
        if (_inactiveMap is { } cached && _inactiveWidth == width)
            return cached;

        var map = BuildMap(width, activeLine: null);
        _inactiveMap = map;
        _inactiveWidth = width;
        return map;
    }

    /// <summary>
    /// Builds a run map for this block at <paramref name="width"/> with the given active line. Virtual so
    /// a mode-specialized presenter (the M2.WP10 <see cref="RawSourcePresenter"/>) can substitute an
    /// identity map (source verbatim, 1:1 source↔cell) without disturbing the formatted layout.
    /// </summary>
    protected virtual RunMap BuildMap(int width, int? activeLine) =>
        RunMapBuilder.Build(_lines, _inlineRuns, _kind, _headingLevel, width, _wrapMode, activeLine, revealSlides: !_revealWraps);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        int width = Math.Max(1, availableSize.Columns);
        int rows = MeasuredRowCount(width);
        MeasuredCallback?.Invoke(this, rows);
        return new Size(width, rows);
    }

    /// <summary>
    /// The block's rendered height in rows. Slide-reveal is height-invariant → the inactive layout's row
    /// count always (the revealed slid row draws within the reserved rows). Wrap-reveal lets the active
    /// block reflow → the ACTIVE layout's row count (the revealed line wrapped in place may occupy more/fewer
    /// rows than hidden), so the block grows/shrinks while active and content below shifts.
    /// </summary>
    protected virtual int MeasuredRowCount(int width) =>
        _revealWraps && _activeLine is not null
            ? MapForWidth(width).RowCount
            : InactiveMapForWidth(width).RowCount;

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        RenderCount++;

        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        int width = Math.Max(1, context.Size.Columns);
        int rows = Math.Min(MeasuredRowCount(width), bounds.Rows);

        // Order: block fill (code) and the subtle active-block well (§4.3) paint BEFORE the content as
        // background pre-passes over the still-empty cells (a wide cluster is then written whole by the row
        // pass, its wide-cell bookkeeping undisturbed). The SELECTION is NOT a pre-pass — it is composed into
        // the row draws themselves (WP11b): each selected sub-span carries the selection fill as its own
        // DrawText background (so an opaque code fill can't hole it), or, on NoColor, Inverse in the cell
        // style. None of the three changes the block's height (WP9).
        ResolveSelection(context);
        PaintBackground(context, width, rows);
        PaintActiveWell(context, width, rows);
        RenderRows(context, width, rows);
    }

    /// <inheritdoc/>
    protected override void OnTearDown()
    {
        var callback = TornDownCallback;
        TornDownCallback = null;
        callback?.Invoke(this);
        base.OnTearDown();
    }

    /// <summary>Paints a background before the rows draw (code fill, active-block tint). Default no-op.</summary>
    protected virtual void PaintBackground(RenderContext context, int width, int rows) { }

    /// <summary>
    /// The height-invariant reveal flow: non-active lines draw from the inactive layout at their inactive
    /// row positions; the active line's revealed row draws at that line's first inactive row and its freed
    /// rows stay blank. Subclasses (code, rule, fallback) may override entirely.
    /// </summary>
    protected virtual void RenderRows(RenderContext context, int width, int rows)
    {
        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        string blockText = BlockText();

        // Wrap-reveal: the ACTIVE map already has the revealed line wrapped in place (marks shown) and every
        // other line wrapped with marks hidden, so every row draws plainly from it — no slide, no clip, no
        // reserved-blank rows, and no inactive map needed. The block's height is the active map's row count
        // (MeasuredRowCount), so nothing is truncated. This is the prose-editing path.
        if (_revealWraps && _activeLine is { } wrapLine && wrapLine >= 0 && wrapLine < _lines.Count)
        {
            var wrapMap = MapForWidth(width);
            for (var row = 0; row < rows; row++)
                DrawInactiveRow(context, wrapMap, row, blockText, foreground, SelectedCells(wrapMap, row, width, slide: 0));
            return;
        }

        var inactive = InactiveMapForWidth(width);

        if (_activeLine is not { } line || line < 0 || line >= _lines.Count)
        {
            for (var row = 0; row < rows; row++)
                DrawInactiveRow(context, inactive, row, blockText, foreground, SelectedCells(inactive, row, width, slide: 0));
            return;
        }

        var active = MapForWidth(width);
        int firstRow = inactive.RowsOfLine(line).FirstRow;
        int activeRow = active.RowsOfLine(line).FirstRow;

        for (var row = 0; row < rows; row++)
        {
            if (inactive.LineOfRow(row) == line)
            {
                if (row == firstRow)
                    DrawActiveRow(context, active, activeRow, drawAtRow: row, blockText, foreground, width,
                        SelectedCells(active, activeRow, width, _slideOffset));
                // the reserved wrapped rows stay blank — height preserved, no sibling moves
            }
            else
            {
                DrawInactiveRow(context, inactive, row, blockText, foreground, SelectedCells(inactive, row, width, slide: 0));
            }
        }
    }

    /// <summary>
    /// Draws a non-active visual row plainly: each visible run's source slice (or synthetic glyph) at
    /// its natural cell, formatted (bold/italic/code/link/heading); zero-width hidden marks draw nothing.
    /// The row's <paramref name="selection"/> (cell interval, from <see cref="SelectedCells"/>) is composed
    /// into each run's draw by <see cref="DrawSelectableText"/> — so a selected inline-code cell carries the
    /// selection fill, not the opaque code fill (WP11b), and NoColor selection rides <c>Inverse</c>.
    /// </summary>
    protected virtual void DrawInactiveRow(RenderContext context, RunMap map, int row, string blockText, IBrush foreground, RowSelection selection)
    {
        foreach (var run in map.RunsForRow(row))
        {
            if (run.Kind == RunKind.Synthetic)
            {
                if (run.Glyph is { Length: > 0 } glyph)
                {
                    var (fg, style) = StyleForSynthetic(run, foreground);
                    DrawSelectableText(context, run.Col, row, glyph, fg, null, style, selection);
                }

                continue;
            }

            if (run.Width == 0 || run.SrcLen == 0)
                continue; // a hidden mark has no cells to draw

            var slice = SliceInBounds(blockText, run.SrcStart, run.SrcLen);
            if (slice.IsEmpty)
                continue;

            var (foregroundBrush, background, cellStyle) = StyleForContent(run, foreground);
            DrawSelectableText(context, run.Col, row, slice, foregroundBrush, background, cellStyle, selection);
        }
    }

    /// <summary>
    /// Draws the active (revealed) row through the clip pipeline at <paramref name="drawAtRow"/>:
    /// <see cref="RunMap.ClipRow(int, int, int)"/> resolves the grapheme-snapped visible window at the
    /// slide, and each column draws its whole cluster, a dim continuation indicator, or blank padding.
    /// </summary>
    protected virtual void DrawActiveRow(
        RenderContext context, RunMap activeMap, int activeRow, int drawAtRow, string blockText, IBrush foreground, int width, RowSelection selection)
    {
        var clip = activeMap.ClipRow(activeRow, _slideOffset, width);
        var cells = clip.Cells;

        for (var column = 0; column < cells.Count; column++)
        {
            var cell = cells[column];
            switch (cell.Kind)
            {
                case ClipCellKind.Head:
                    // A synthetic head cell (a ↵ hard break on the active line) draws its glyph; a
                    // text/revealed-mark cell draws its whole source grapheme. The selection (in
                    // post-slide viewport cells) is composed into this cell's own draw.
                    var grapheme = cell.Glyph is { Length: > 0 } g ? g.AsSpan() : FirstGrapheme(blockText, cell.SrcOffset);
                    if (!grapheme.IsEmpty)
                    {
                        var (fg, style) = ActiveCellStyle(cell.Run, foreground);
                        DrawSelectableText(context, column, drawAtRow, grapheme, fg, null, style, selection);
                    }

                    break;

                case ClipCellKind.LeftIndicator:
                    // The ❮/❯ continuation indicators carry a glyph, so compose the selection into them
                    // (a selected clip edge stays a contiguous highlight, no one-cell gap).
                    DrawSelectableText(context, column, drawAtRow, LeftIndicatorGlyph, foreground, null, MarkdownStyles.Dim(this), selection);
                    break;

                case ClipCellKind.RightIndicator:
                    DrawSelectableText(context, column, drawAtRow, RightIndicatorGlyph, foreground, null, MarkdownStyles.Dim(this), selection);
                    break;

                case ClipCellKind.Blank:  // padding, or a straddling wide cluster's suppressed half — no glyph
                    if (_selectionActive && column >= selection.FromCell && column < selection.ToCell)
                        DrawSelectedSpan(context, column, drawAtRow, " ", foreground, default);
                    break;

                case ClipCellKind.Tail:   // covered by the preceding wide Head glyph
                default:
                    break;
            }
        }
    }

    // ───────────────────────────── active-block well + selection ─────────────────────────────

    /// <summary>
    /// Paints the <c>:active-block</c> well tint behind the block when it is active (§4.3 / WP9): a faint
    /// translucent scrim across the block's box so the user sees which block the caret is in. A no-op for
    /// inactive blocks — so a caret crossing a block boundary re-rasters exactly the block left (well
    /// removed) and the block entered (well added), the two-zone gate.
    /// </summary>
    protected virtual void PaintActiveWell(RenderContext context, int width, int rows)
    {
        if (_activeLine is null || width <= 0 || rows <= 0)
            return;

        context.PaintRectangle(new Rect(0, 0, width, rows), MarkdownStyles.ActiveWellBrush(this));
    }

    /// <summary>
    /// Resolves the document selection for this Render pass (M2.WP11b): the block-relative source range
    /// (<see cref="SelectionProvider"/>), whether the tier is NoColor (→ <see cref="TextAttributes.Inverse"/>
    /// in the cell style, the BuiltIn non-color selection channel — a background scrim degrades to nothing),
    /// and, on a color tier, the resolved selection fill (<see cref="ThemeKeys.SelectionBrush"/>, the
    /// <c>TextPresenter</c> convention). Resolved once per pass so the per-cell draw path is a field read,
    /// not a resource-chain walk per row (the deferred-cleanup note on the M1 path).
    /// </summary>
    private void ResolveSelection(RenderContext context)
    {
        _selectionActive = false;
        _selectionBrush = null;

        if (SelectionProvider?.Invoke() is not { } selection || selection.End <= selection.Start)
            return;

        _selectionStart = selection.Start;
        _selectionEnd = selection.End;
        _selectionNoColor = context.Capabilities.Color.Depth == ColorDepth.NoColor;
        if (_selectionNoColor)
        {
            _selectionActive = true; // the Inverse path needs no fill brush
            return;
        }

        if (this.TryFindResource(ThemeKeys.SelectionBrush, out var value) && value is IBrush brush)
        {
            _selectionBrush = brush;
            _selectionActive = true;
        }
    }

    /// <summary>
    /// The selected cell interval of one visual row (<see cref="RowSelection"/>): the block-relative
    /// selection is clamped to the row's source span and its ends mapped to cells through
    /// <paramref name="map"/> (minus the active line's <paramref name="slide"/>, clamped into the viewport).
    /// A zero-width hidden mark, or no selection this pass, yields <see cref="RowSelection.None"/>. Whole-cell
    /// discipline lives in <see cref="RunMap.Locate"/>, so a partially-selected wide cluster's interval spans
    /// the whole cluster.
    /// </summary>
    protected RowSelection SelectedCells(RunMap map, int mapRow, int width, int slide)
    {
        if (!_selectionActive)
            return RowSelection.None;

        int rowStartSrc = map.OffsetAt(mapRow, 0);
        int rowEndSrc = map.RowEndOffset(mapRow);
        int fromSrc = Math.Max(_selectionStart, rowStartSrc);
        int toSrc = Math.Min(_selectionEnd, rowEndSrc);
        if (toSrc <= fromSrc)
            return RowSelection.None;

        int fromCell = Math.Clamp(map.Locate(fromSrc, endAffinity: false).Cell - slide, 0, width);
        int toCell = Math.Clamp(map.Locate(toSrc, endAffinity: true).Cell - slide, 0, width);
        return new RowSelection(fromCell, toCell);
    }

    /// <summary>
    /// The selected cell interval of a source line drawn <b>verbatim</b> at cell 0 (the
    /// <see cref="FallbackSourcePresenter"/>/<see cref="FrontMatterPresenter"/>/<see cref="RawSourcePresenter"/>
    /// path, which draw <c>Lines[i].Text</c> 1:1 with no run map): the block selection ∩ the line's text,
    /// with the ends measured to cells by <see cref="GraphemeWidth"/> (whole-cell). A subclass that slides
    /// the line subtracts its slide and clamps to the viewport. Returns <see cref="RowSelection.None"/> when
    /// the line is unselected this pass.
    /// </summary>
    protected RowSelection SelectedCellsForVerbatimLine(int lineIndex)
    {
        if (!_selectionActive || lineIndex < 0 || lineIndex >= _lines.Count)
            return RowSelection.None;

        int lineStart = LineSourceStart(lineIndex);
        var text = _lines[lineIndex].Text.AsSpan();
        int fromSrc = Math.Max(_selectionStart, lineStart) - lineStart;   // line-relative
        int toSrc = Math.Min(_selectionEnd, lineStart + text.Length) - lineStart;
        if (toSrc <= fromSrc || fromSrc >= text.Length)
            return RowSelection.None;

        fromSrc = Math.Max(0, fromSrc);
        int fromCell = GraphemeWidth.StringWidth(text[..fromSrc]);
        int toCell = GraphemeWidth.StringWidth(text[..toSrc]);
        return new RowSelection(fromCell, toCell);
    }

    /// <summary>The block-relative source offset where source line <paramref name="lineIndex"/> begins (terminators included).</summary>
    private int LineSourceStart(int lineIndex)
    {
        // Cached prefix sums (invalidated in SetContent): one pass per content, not a re-walk per call —
        // so selection over a tall verbatim block is O(rows), not O(rows²), per frame.
        if (_lineStarts is not { } starts)
        {
            starts = new int[_lines.Count + 1];
            for (var i = 0; i < _lines.Count; i++)
                starts[i + 1] = starts[i] + _lines[i].TotalLength;
            _lineStarts = starts;
        }

        return starts[lineIndex];
    }

    /// <summary>
    /// Slides a verbatim line's cell interval into the viewport for a horizontally-slid row (the
    /// <see cref="RawSourcePresenter"/> active line, drawn at <c>-slide</c>): subtracts the row's
    /// <paramref name="slide"/> and clamps to <c>[0, width]</c>, matching <see cref="SelectedCells"/>'s
    /// viewport convention so <see cref="DrawSelectableText"/> reads one coordinate space everywhere.
    /// </summary>
    protected static RowSelection SlideSelection(RowSelection lineSelection, int slide, int width)
    {
        if (lineSelection.IsEmpty)
            return RowSelection.None;

        int from = Math.Clamp(lineSelection.FromCell - slide, 0, width);
        int to = Math.Clamp(lineSelection.ToCell - slide, 0, width);
        return new RowSelection(from, to);
    }

    /// <summary>
    /// Draws <paramref name="text"/> at cell (<paramref name="col"/>, <paramref name="row"/>) with the
    /// document <paramref name="selection"/> composed INTO the cells it covers (M2.WP11b — the shared seam
    /// every presenter and subclass draws rows through). The span is split at the row's selected cell
    /// interval and the selected sub-span is drawn with the selection fill as its own cell background — so
    /// an opaque per-run background (inline-code or code-block fill) passed as <paramref name="background"/>
    /// cannot punch a hole through the highlight — or, on the NoColor tier, with
    /// <see cref="TextAttributes.Inverse"/> in the cell style (a scrim's Inverse is overwritten by the glyph
    /// draw, so it must ride the glyph's cells). Selection boundaries are cluster-aligned cell positions, so
    /// the split lands on grapheme boundaries and a wide cluster is drawn whole-selected. When nothing on the
    /// row is selected this is one ordinary <c>DrawText</c>.
    /// </summary>
    protected void DrawSelectableText(
        RenderContext context, int col, int row, ReadOnlySpan<char> text,
        IBrush foreground, IBrush? background, CellStyle style, RowSelection selection)
    {
        if (text.IsEmpty)
            return;

        if (!_selectionActive || selection.IsEmpty)
        {
            context.DrawText(col, row, text, foreground, background, style);
            return;
        }

        // Partition the drawn span into an unselected prefix, a selected middle, and an unselected suffix at
        // the [FromCell, ToCell) boundaries, walking grapheme clusters from `col`. Because the boundaries are
        // cluster-aligned cell positions, each split falls on a grapheme boundary (a wide cluster is never
        // halved). selStartCell/selEndCell carry the drawn cell each sub-span begins at.
        int cell = col;
        int index = 0;
        int prefixEnd = -1, selEnd = -1;
        int selStartCell = col, selEndCell = col;
        var clusters = text.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            if (prefixEnd < 0 && cell >= selection.FromCell)
            {
                prefixEnd = index;
                selStartCell = cell;
            }

            if (selEnd < 0 && cell >= selection.ToCell)
            {
                selEnd = index;
                selEndCell = cell;
            }

            cell += GraphemeWidth.StringWidth(clusters.Current);
            index += clusters.Current.Length;
        }

        if (prefixEnd < 0) { prefixEnd = text.Length; selStartCell = cell; }
        if (selEnd < 0) { selEnd = text.Length; selEndCell = cell; }

        if (prefixEnd > 0)
            context.DrawText(col, row, text[..prefixEnd], foreground, background, style);

        if (selEnd > prefixEnd)
            DrawSelectedSpan(context, selStartCell, row, text[prefixEnd..selEnd], foreground, style);

        if (selEnd < text.Length)
            context.DrawText(selEndCell, row, text[selEnd..], foreground, background, style);
    }

    /// <summary>Draws a selected sub-span: the selection fill as its background (color tiers), or <see cref="TextAttributes.Inverse"/> in the cell style (NoColor).</summary>
    private void DrawSelectedSpan(RenderContext context, int col, int row, ReadOnlySpan<char> text, IBrush foreground, CellStyle style)
    {
        if (_selectionNoColor)
            context.DrawText(col, row, text, foreground, null, style.AddAttributes(TextAttributes.Inverse));
        else
            context.DrawText(col, row, text, foreground, _selectionBrush, style); // fill replaces any run background — no hole
    }

    /// <summary>
    /// Composes the selection onto <b>glyph-free</b> cells in <c>[fromCell, selection.ToCell)</c> — the
    /// cells no <see cref="DrawSelectableText"/> covers (a blank/closing code fence row, the freed padding of
    /// a slid active row): there is no glyph to compose into, so the selection is drawn as spaces carrying its
    /// own fill (or Inverse on NoColor). Restores the uniform highlight the old full-width scrim gave those
    /// cells, without reintroducing the hole (a glyph cell still composes through <see cref="DrawSelectableText"/>).
    /// </summary>
    protected void FillSelectedBlank(RenderContext context, int row, int fromCell, IBrush foreground, RowSelection selection)
    {
        if (!_selectionActive || selection.IsEmpty)
            return;

        int from = Math.Max(fromCell, selection.FromCell);
        int to = selection.ToCell;
        if (to <= from)
            return;

        DrawSelectedSpan(context, from, row, new string(' ', to - from), foreground, default);
    }

    /// <summary>
    /// A row's selected cell interval <c>[FromCell, ToCell)</c> (post-slide, viewport-clamped), the
    /// per-row hand-off <see cref="DrawSelectableText"/> consults to compose the selection into each draw.
    /// <see cref="None"/> (the default) is "nothing selected on this row".
    /// </summary>
    protected readonly record struct RowSelection(int FromCell, int ToCell)
    {
        /// <summary>The empty interval — no cell on the row is selected.</summary>
        public static RowSelection None => default;

        /// <summary>Whether the interval covers no cells.</summary>
        public bool IsEmpty => ToCell <= FromCell;
    }

    // ───────────────────────────── styling hooks ─────────────────────────────

    /// <summary>The foreground, background, and attributes for an inactive content run (Text/RevealedMark).</summary>
    protected virtual (IBrush Foreground, IBrush? Background, CellStyle Style) StyleForContent(in Run run, IBrush inherited)
    {
        if (run.Kind == RunKind.RevealedMark)
            return (inherited, null, MarkdownStyles.Dim(this));

        var attributes = MarkdownStyles.AttributesFor(run.Style);
        var foreground = inherited;

        if (_kind == BlockKind.Heading)
        {
            foreground = MarkdownStyles.HeadingBrush(this, _headingLevel ?? 1);
            attributes |= MarkdownStyles.HeadingAttributes(this, _headingLevel ?? 1);
        }

        var background = (run.Style & RunStyle.Code) != 0 ? MarkdownStyles.CodeFillBrush(this) : null;
        return (foreground, background, CellStyle.Default.WithAttributes(attributes));
    }

    /// <summary>The foreground and attributes for a synthetic glyph (bullet, quote bar, ↵). Default: the marker accent color.</summary>
    protected virtual (IBrush Foreground, CellStyle Style) StyleForSynthetic(in Run run, IBrush inherited)
        => (MarkdownStyles.MarkerBrush(this), CellStyle.Default);

    /// <summary>The foreground and attributes for one revealed active-row cell.</summary>
    protected virtual (IBrush Foreground, CellStyle Style) ActiveCellStyle(RunKind cellKind, IBrush inherited)
    {
        if (cellKind is RunKind.RevealedMark or RunKind.Synthetic)
            return (inherited, MarkdownStyles.Dim(this)); // revealed marks and the ↵ affordance render faint

        if (_kind == BlockKind.Heading)
            return (MarkdownStyles.HeadingBrush(this, _headingLevel ?? 1),
                CellStyle.Default.WithAttributes(MarkdownStyles.HeadingAttributes(this, _headingLevel ?? 1)));

        return (inherited, CellStyle.Default);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>The block's serialized source (lines + terminators) — the origin of the block-relative offsets the runs carry.</summary>
    protected string BlockText()
    {
        if (_blockText is { } cached)
            return cached;

        int length = 0;
        foreach (var line in _lines)
            length += line.TotalLength;

        var builder = new System.Text.StringBuilder(length);
        foreach (var line in _lines)
            builder.Append(line.Text).Append(line.EndingText);

        return _blockText = builder.ToString();
    }

    /// <summary>The grapheme cluster of <paramref name="blockText"/> starting at <paramref name="offset"/> (empty when out of range).</summary>
    protected static ReadOnlySpan<char> FirstGrapheme(string blockText, int offset)
    {
        if (offset < 0 || offset >= blockText.Length)
            return default;

        var enumerator = blockText.AsSpan(offset).GetGraphemeEnumerator();
        return enumerator.MoveNext() ? blockText.AsSpan(offset, enumerator.Current.Length) : default;
    }

    /// <summary>A source slice clamped into <paramref name="blockText"/> (defensive against a run pointing past the snapshot).</summary>
    protected static ReadOnlySpan<char> SliceInBounds(string blockText, int start, int length)
    {
        if (start < 0 || start >= blockText.Length || length <= 0)
            return default;

        return blockText.AsSpan(start, Math.Min(length, blockText.Length - start));
    }
}
