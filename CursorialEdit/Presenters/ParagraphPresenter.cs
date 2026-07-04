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
/// The M2.WP6 reveal-on-edit spike presenter (architecture Decision 9 / §2.4 / §4.1; risk R1): a
/// render-only leaf-block element that proves the full <b>RunMap → slide → clip → draw</b> path for
/// reveal-without-reflow. Given a block's source lines, its lazily-realized inline runs, and a
/// reveal state (the active source line the caret sits on plus the horizontal slide offset for that
/// line's one un-wrapped row), it builds a <see cref="RunMap"/> through
/// <see cref="RunMapBuilder.Build"/> and draws each visual row:
/// </summary>
/// <remarks>
/// <para>
/// <b>Inactive rows render plain.</b> Every non-active row (and every row of an inactive block) draws
/// its <see cref="RunKind.Text"/>/<see cref="RunKind.Synthetic"/> runs at natural cell positions;
/// syntax marks are <see cref="RunKind.HiddenMark"/> runs with zero visible cells, so they simply do
/// not draw — the wrapped, formatted view.
/// </para>
/// <para>
/// <b>The active row reveals, slides, and clips.</b> The active line renders at natural width with
/// its marks shown (<see cref="RunKind.RevealedMark"/>), horizontally slid by <see cref="SlideOffset"/>
/// and grapheme-snapped <b>clipped</b> to the block box through
/// <see cref="RunMap.ClipRow(int, int, int)"/> → <see cref="ClippedRow"/> → per-<see cref="ClipCell"/>
/// draw. A 2-cell cluster straddling either clip edge becomes blank padding (never half-rendered,
/// §2.4 whole-cell discipline), and dim <c>❮</c>/<c>❯</c> continuation indicators occupy the edge
/// cells whenever content extends past the visible span (the less/vim idiom). The active line is one
/// visual row (<see cref="WrapMode.NoWrap"/>, forced inside the builder), so its line-count is
/// invariant under reveal — revealing marks moves no other line or row (§4.1 [EDGE] satisfied
/// structurally): a sibling never reflows.
/// </para>
/// <para>
/// <b>Render boundary.</b> <see cref="UIElement.IsRenderBoundary"/> is <see langword="true"/>
/// (Decision 7), so toggling reveal on this block re-rasters exactly this zone — no cell outside the
/// active block changes. <see cref="RenderCount"/> is the raster observable the R1 gate diffs
/// against. This is the WP6 <b>prototype</b>: it renders paragraphs/headings (Text/HiddenMark/
/// RevealedMark) end to end; the full presenter fan-out to every block kind (synthetic list/quote
/// glyphs, code, callouts, tables) is WP7.
/// </para>
/// </remarks>
public sealed class ParagraphPresenter : UIElement
{
    private static readonly string LeftIndicatorGlyph = ClipCell.LeftGlyph.ToString();
    private static readonly string RightIndicatorGlyph = ClipCell.RightGlyph.ToString();

    // The dim style shared by revealed marks and the continuation indicators — SGR 2 (faint), which
    // StyleQuantizer degrades to nothing on tiers that lack it while the glyph still occupies its cell.
    private static readonly CellStyle DimStyle = CellStyle.Default.WithAttributes(TextAttributes.Faint);

    private readonly BlockKind _kind;
    private readonly int? _headingLevel;
    private readonly WrapMode _wrapMode;

    private IReadOnlyList<Line> _lines;
    private IReadOnlyList<InlineRun> _inlineRuns;
    private string? _blockText; // memoized source concatenation (invalidated on SetContent)

    private int? _activeLine;
    private int _slideOffset;

    // Map cache keyed by (width, activeLine) — the slide is a render-time clip parameter, not a map input.
    private RunMap? _map;
    private int _mapWidth = -1;
    private int? _mapActive = -1; // sentinel distinct from every real int? active line and from null

    /// <summary>Creates the presenter for a block's <paramref name="lines"/>.</summary>
    /// <param name="lines">The block's source lines (≥ 1), buffer-split with endings preserved.</param>
    /// <param name="inlineRuns">The block's block-relative inline runs (<see cref="Block.InlineRuns"/>); empty for plain text/code.</param>
    /// <param name="kind">The block's structural kind (drives heading/list/quote mark derivation).</param>
    /// <param name="headingLevel">The heading level when <paramref name="kind"/> is <see cref="BlockKind.Heading"/>; otherwise ignored.</param>
    /// <param name="wrapMode">Wrap-on (<see cref="WrapMode.WordWrap"/>, default) or wrap-off (<see cref="WrapMode.NoWrap"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="inlineRuns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    public ParagraphPresenter(
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

    /// <summary>Number of <see cref="Render"/> calls — the R1 raster observable (toggling reveal re-rasters exactly this zone).</summary>
    public int RenderCount { get; private set; }

    /// <summary>
    /// Sets the reveal state: <paramref name="activeLine"/> is the caret's source line (revealed,
    /// un-wrapped, slidable) or <see langword="null"/> to deactivate the block; <paramref name="slideOffset"/>
    /// is the caret-visibility slide for the active row (<see cref="HorizontalSlide.Compute"/>).
    /// Re-measures only when the active line moved (row count may change); always re-rasters this zone.
    /// </summary>
    public void SetReveal(int? activeLine, int slideOffset = 0)
    {
        bool activeChanged = activeLine != _activeLine;
        _activeLine = activeLine;
        _slideOffset = Math.Max(0, slideOffset);

        if (activeChanged)
            InvalidateMeasure(); // the active line un-wraps to one row — its row count can change
        InvalidateVisual();
    }

    /// <summary>
    /// Replaces the block's source lines and inline runs (the typing/edit path the harness drives):
    /// the map cache and memoized block text drop, and the zone re-measures and re-rasters.
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
        _map = null;
        _mapWidth = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// The run map for the current reveal state at <paramref name="width"/> cells — the same map the
    /// presenter measures and draws through, exposed so a caret owner (the harness, WP8) computes the
    /// caret cell and the <see cref="HorizontalSlide"/> from exactly the layout being rendered.
    /// </summary>
    public RunMap MapForWidth(int width)
    {
        width = Math.Max(1, width);
        if (_map is { } cached && _mapWidth == width && _mapActive == _activeLine)
            return cached;

        var map = RunMapBuilder.Build(_lines, _inlineRuns, _kind, _headingLevel, width, _wrapMode, _activeLine);
        _map = map;
        _mapWidth = width;
        _mapActive = _activeLine;
        return map;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        int width = Math.Max(1, availableSize.Columns);
        var map = MapForWidth(width);
        return new Size(width, map.RowCount);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        RenderCount++;

        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        int viewport = Math.Max(1, context.Size.Columns);
        var map = MapForWidth(viewport);
        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        string blockText = BlockText();

        int rows = Math.Min(map.RowCount, bounds.Rows);
        for (var row = 0; row < rows; row++)
        {
            if (map.IsActiveRow(row))
                RenderActiveRow(context, map, row, blockText, foreground, viewport);
            else
                RenderInactiveRow(context, map, row, blockText, foreground);
        }
    }

    /// <summary>
    /// Draws a non-active visual row plainly: each visible run's source slice at its natural cell,
    /// syntax marks omitted (zero-width <see cref="RunKind.HiddenMark"/> runs draw nothing). The row
    /// is already wrapped to fit the block box, so no clipping applies.
    /// </summary>
    private void RenderInactiveRow(RenderContext context, RunMap map, int row, string blockText, IBrush foreground)
    {
        foreach (var run in map.RunsForRow(row))
        {
            if (run.Width == 0 || run.SrcLen == 0)
                continue; // a hidden mark (or a zero-source-length decoration) has no cells to draw

            var slice = SliceInBounds(blockText, run.SrcStart, run.SrcLen);
            if (slice.IsEmpty)
                continue;

            context.DrawText(run.Col, row, slice, foreground, null, StyleFor(run.Kind));
        }
    }

    /// <summary>
    /// Draws the active (revealed) row through the clip pipeline: <see cref="RunMap.ClipRow(int, int, int)"/>
    /// resolves the grapheme-snapped visible window at <see cref="SlideOffset"/>, and each published
    /// column draws its whole cluster (from the block source), a dim continuation indicator, or blank
    /// padding — never half a wide cluster.
    /// </summary>
    private void RenderActiveRow(RenderContext context, RunMap map, int row, string blockText, IBrush foreground, int viewport)
    {
        var clip = map.ClipRow(row, _slideOffset, viewport);
        var cells = clip.Cells;

        for (var column = 0; column < cells.Count; column++)
        {
            var cell = cells[column];
            switch (cell.Kind)
            {
                case ClipCellKind.Head:
                    // Draw the whole grapheme (1 or 2 cells) at its head column; the framework marks
                    // the wide half's continuation cell, so the paired Tail column below is a no-op.
                    var grapheme = FirstGrapheme(blockText, cell.SrcOffset);
                    if (!grapheme.IsEmpty)
                        context.DrawText(column, row, grapheme, foreground, null, StyleFor(cell.Run));
                    break;

                case ClipCellKind.LeftIndicator:
                    context.DrawText(column, row, LeftIndicatorGlyph, foreground, null, DimStyle);
                    break;

                case ClipCellKind.RightIndicator:
                    context.DrawText(column, row, RightIndicatorGlyph, foreground, null, DimStyle);
                    break;

                case ClipCellKind.Tail:   // covered by the preceding wide Head glyph
                case ClipCellKind.Blank:  // padding, or a straddling wide cluster's suppressed half
                default:
                    break;
            }
        }
    }

    /// <summary>Revealed marks and continuation indicators render dim (faint); text and synthetics render at the inherited style.</summary>
    private static CellStyle StyleFor(RunKind kind) => kind == RunKind.RevealedMark ? DimStyle : CellStyle.Default;

    /// <summary>The block's serialized source (lines + terminators) — the origin of the block-relative offsets the runs and clip cells carry.</summary>
    private string BlockText()
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
    private static ReadOnlySpan<char> FirstGrapheme(string blockText, int offset)
    {
        if (offset < 0 || offset >= blockText.Length)
            return default;

        var enumerator = blockText.AsSpan(offset).GetGraphemeEnumerator();
        return enumerator.MoveNext() ? blockText.AsSpan(offset, enumerator.Current.Length) : default;
    }

    /// <summary>A source slice clamped into <paramref name="blockText"/> (defensive against a run pointing past the snapshot).</summary>
    private static ReadOnlySpan<char> SliceInBounds(string blockText, int start, int length)
    {
        if (start < 0 || start >= blockText.Length || length <= 0)
            return default;

        return blockText.AsSpan(start, Math.Min(length, blockText.Length - start));
    }
}
