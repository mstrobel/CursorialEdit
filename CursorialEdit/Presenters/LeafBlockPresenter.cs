using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

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
    private readonly int? _headingLevel;
    private readonly WrapMode _wrapMode;

    private IReadOnlyList<Line> _lines;
    private IReadOnlyList<InlineRun> _inlineRuns;
    private string? _blockText;

    private int? _activeLine;
    private int _slideOffset;

    private RunMap? _activeMap;
    private int _activeWidth = -1;
    private int? _activeMapActive = -2; // sentinel distinct from every real int? and from null

    private RunMap? _inactiveMap;
    private int _inactiveWidth = -1;

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
    }

    /// <summary>The revealed active source line, or <see langword="null"/> when the block is inactive (all marks hidden).</summary>
    public int? ActiveLine => _activeLine;

    /// <summary>The horizontal slide offset applied to the active line's one un-wrapped row (cells).</summary>
    public int SlideOffset => _slideOffset;

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
        _activeLine = activeLine;
        _slideOffset = Math.Max(0, slideOffset);
        InvalidateVisual();
    }

    /// <summary>
    /// Replaces the block's source lines and inline runs (the typing/edit path): the map caches and
    /// memoized block text drop, and the zone re-measures and re-rasters.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="inlineRuns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    public void SetContent(IReadOnlyList<Line> lines, IReadOnlyList<InlineRun> inlineRuns)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(inlineRuns);
        if (lines.Count == 0)
            throw new ArgumentException("A block owns at least one line.", nameof(lines));

        _lines = lines;
        _inlineRuns = inlineRuns;
        _blockText = null;
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
        if (_activeMap is { } cached && _activeWidth == width && _activeMapActive == _activeLine)
            return cached;

        var map = BuildMap(width, _activeLine);
        _activeMap = map;
        _activeWidth = width;
        _activeMapActive = _activeLine;
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

    /// <summary>Builds a run map for this block at <paramref name="width"/> with the given active line.</summary>
    protected RunMap BuildMap(int width, int? activeLine) =>
        RunMapBuilder.Build(_lines, _inlineRuns, _kind, _headingLevel, width, _wrapMode, activeLine);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        int width = Math.Max(1, availableSize.Columns);
        int rows = MeasuredRowCount(width);
        MeasuredCallback?.Invoke(this, rows);
        return new Size(width, rows);
    }

    /// <summary>The block's rendered height in rows — the inactive layout's row count (height-invariant).</summary>
    protected virtual int MeasuredRowCount(int width) => InactiveMapForWidth(width).RowCount;

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        RenderCount++;

        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        int width = Math.Max(1, context.Size.Columns);
        int rows = Math.Min(MeasuredRowCount(width), bounds.Rows);

        PaintBackground(context, width, rows);
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
        var inactive = InactiveMapForWidth(width);
        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        string blockText = BlockText();

        if (_activeLine is not { } line || line < 0 || line >= _lines.Count)
        {
            for (var row = 0; row < rows; row++)
                DrawInactiveRow(context, inactive, row, blockText, foreground);
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
                    DrawActiveRow(context, active, activeRow, drawAtRow: row, blockText, foreground, width);
                // the reserved wrapped rows stay blank — height preserved, no sibling moves
            }
            else
            {
                DrawInactiveRow(context, inactive, row, blockText, foreground);
            }
        }
    }

    /// <summary>
    /// Draws a non-active visual row plainly: each visible run's source slice (or synthetic glyph) at
    /// its natural cell, formatted (bold/italic/code/link/heading); zero-width hidden marks draw nothing.
    /// </summary>
    protected virtual void DrawInactiveRow(RenderContext context, RunMap map, int row, string blockText, IBrush foreground)
    {
        foreach (var run in map.RunsForRow(row))
        {
            if (run.Kind == RunKind.Synthetic)
            {
                if (run.Glyph is { Length: > 0 } glyph)
                {
                    var (fg, style) = StyleForSynthetic(run, foreground);
                    context.DrawText(run.Col, row, glyph, fg, null, style);
                }

                continue;
            }

            if (run.Width == 0 || run.SrcLen == 0)
                continue; // a hidden mark has no cells to draw

            var slice = SliceInBounds(blockText, run.SrcStart, run.SrcLen);
            if (slice.IsEmpty)
                continue;

            var (foregroundBrush, background, cellStyle) = StyleForContent(run, foreground);
            context.DrawText(run.Col, row, slice, foregroundBrush, background, cellStyle);
        }
    }

    /// <summary>
    /// Draws the active (revealed) row through the clip pipeline at <paramref name="drawAtRow"/>:
    /// <see cref="RunMap.ClipRow(int, int, int)"/> resolves the grapheme-snapped visible window at the
    /// slide, and each column draws its whole cluster, a dim continuation indicator, or blank padding.
    /// </summary>
    protected virtual void DrawActiveRow(
        RenderContext context, RunMap activeMap, int activeRow, int drawAtRow, string blockText, IBrush foreground, int width)
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
                    // text/revealed-mark cell draws its whole source grapheme.
                    var grapheme = cell.Glyph is { Length: > 0 } g ? g.AsSpan() : FirstGrapheme(blockText, cell.SrcOffset);
                    if (!grapheme.IsEmpty)
                    {
                        var (fg, style) = ActiveCellStyle(cell.Run, foreground);
                        context.DrawText(column, drawAtRow, grapheme, fg, null, style);
                    }

                    break;

                case ClipCellKind.LeftIndicator:
                    context.DrawText(column, drawAtRow, LeftIndicatorGlyph, foreground, null, MarkdownStyles.Dim);
                    break;

                case ClipCellKind.RightIndicator:
                    context.DrawText(column, drawAtRow, RightIndicatorGlyph, foreground, null, MarkdownStyles.Dim);
                    break;

                case ClipCellKind.Tail:   // covered by the preceding wide Head glyph
                case ClipCellKind.Blank:  // padding, or a straddling wide cluster's suppressed half
                default:
                    break;
            }
        }
    }

    // ───────────────────────────── styling hooks ─────────────────────────────

    /// <summary>The foreground, background, and attributes for an inactive content run (Text/RevealedMark).</summary>
    protected virtual (IBrush Foreground, IBrush? Background, CellStyle Style) StyleForContent(in Run run, IBrush inherited)
    {
        if (run.Kind == RunKind.RevealedMark)
            return (inherited, null, MarkdownStyles.Dim);

        var attributes = MarkdownStyles.AttributesFor(run.Style);
        var foreground = inherited;

        if (_kind == BlockKind.Heading)
        {
            foreground = MarkdownStyles.HeadingBrush(_headingLevel ?? 1);
            attributes |= MarkdownStyles.HeadingAttributes(_headingLevel ?? 1);
        }

        var background = (run.Style & RunStyle.Code) != 0 ? MarkdownStyles.CodeFillBrush : null;
        return (foreground, background, CellStyle.Default.WithAttributes(attributes));
    }

    /// <summary>The foreground and attributes for a synthetic glyph (bullet, quote bar, ↵). Default: the marker accent color.</summary>
    protected virtual (IBrush Foreground, CellStyle Style) StyleForSynthetic(in Run run, IBrush inherited)
        => (MarkdownStyles.MarkerBrush, CellStyle.Default);

    /// <summary>The foreground and attributes for one revealed active-row cell.</summary>
    protected virtual (IBrush Foreground, CellStyle Style) ActiveCellStyle(RunKind cellKind, IBrush inherited)
    {
        if (cellKind is RunKind.RevealedMark or RunKind.Synthetic)
            return (inherited, MarkdownStyles.Dim); // revealed marks and the ↵ affordance render faint

        if (_kind == BlockKind.Heading)
            return (MarkdownStyles.HeadingBrush(_headingLevel ?? 1),
                CellStyle.Default.WithAttributes(MarkdownStyles.HeadingAttributes(_headingLevel ?? 1)));

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
