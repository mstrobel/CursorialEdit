using System.Text;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// One logical table row rendered as its own <b>render boundary</b> (architecture §2.5 / Decision 7 —
/// the committed M3 deliverable): a keystroke that edits a cell re-rasters exactly this row's zone, not
/// the whole table. A row draws its content visual rows (<c>│ cell │ cell │</c>, one per wrapped visual
/// row from <see cref="TableModel.LayoutRow"/>) and its trailing border — the <c>├─┼─┤</c> separator
/// between rows or the <c>└─┴─┘</c> bottom border after the last — and logical row 0 additionally owns
/// the <c>┌─┬─┐</c> top border. Its visual lines are built as per-visual-row run maps
/// (<see cref="TableVisualLine"/>): synthetic glyph runs for the box borders and dividers
/// (<c>SrcLen == 0</c>, no caret stop), and a <see cref="RunKind.Text"/> run per cell fragment mapping
/// to its block-relative cell source — so drawing is driven straight from the map WP9 will navigate.
/// </summary>
internal sealed class TableRowPresenter : UIElement
{
    private TableModel _model;
    private TableGridMetrics _metrics;
    private string _blockText;
    private readonly int _logicalRow;
    private TableOverflow _overflow;

    /// <summary>The column of the focused cell when the caret is on THIS row (its content reveals full, un-truncated, under <see cref="TableOverflow.Truncate"/>); <c>-1</c> when no cell on this row is focused.</summary>
    private int _activeColumn = -1;

    private TableVisualLine[] _lines;
    private string _signature;

    /// <summary>
    /// The presenter-internal <b>column-window</b> the whole grid is drawn through (M3.WP6, FB-6 sidestep):
    /// <c>Offset</c> is the cells clipped off the left (the first-visible column's <see cref="TableGridMetrics.BorderX"/>),
    /// <c>DrawWidth</c> the visible grid width (≤ viewport). The owning <see cref="TablePresenter"/> supplies it so
    /// every row translates by the same offset; <see langword="null"/> (or a non-positive width) = draw the whole grid.
    /// </summary>
    internal Func<(int Offset, int DrawWidth)>? WindowProvider { get; set; }

    /// <summary>
    /// The document selection range this row highlights (M3.WP4-deferred #2): a delegate the owning
    /// <see cref="TablePresenter"/> forwards from its own <c>SelectionProvider</c>, returning the live
    /// block-relative source range. Read at draw time and intersected with each cell fragment's source
    /// span so a range selection inside a cell paints — like every other presenter — instead of being
    /// invisible. Full cell-rect (rectangular) selection is still WP8.
    /// </summary>
    internal Func<(int Start, int End)?>? SelectionProvider { get; set; }

    /// <summary>
    /// The rectangular whole-cell selection (M3.WP8, spec §5.4) forwarded from the owning
    /// <see cref="TablePresenter"/>. When it covers this row (<see cref="CellRect.ContainsRow"/>), every selected
    /// cell highlights as a FULL cell (the whole column width incl. padding), overriding the WP5 per-cell text
    /// highlight for that row; otherwise the <see cref="SelectionProvider"/> text highlight is used. The interior
    /// dividers between selected cells are deliberately <b>left as normal border glyphs</b> (the rect reads as
    /// highlighted cell-boxes with gridlines between — the spreadsheet-block convention), consistent on both wire
    /// presets. <see langword="null"/> ⇒ no cell-rect active.
    /// </summary>
    internal Func<CellRect?>? CellRectProvider { get; set; }

    public TableRowPresenter(TableModel model, TableGridMetrics metrics, string blockText, int logicalRow, TableOverflow overflow = TableOverflow.Wrap)
    {
        _model = model;
        _metrics = metrics;
        _blockText = blockText;
        _logicalRow = logicalRow;
        _overflow = overflow;
        IsRenderBoundary = true; // Decision 7 — a cell edit re-rasters one row zone

        _lines = BuildLines(out _signature);
    }

    /// <summary>The row's rendered height in cells (visual rows + owned borders) — the height the table stacks with.</summary>
    public int RowHeight => _lines.Length;

    /// <summary>
    /// The focused-cell column when the caret is on this row (<see cref="TableOverflow.Truncate"/> reveals it in
    /// full), or <c>-1</c>. Set by the owning <see cref="TablePresenter"/> on reveal; a change re-rasters this one
    /// row zone (no re-derive — the reveal is a draw-time overlay, not a layout change).
    /// </summary>
    internal int ActiveColumn
    {
        get => _activeColumn;
        set
        {
            if (_activeColumn == value)
                return;
            _activeColumn = value;

            // Per-cell reveal (Decision 9): the active cell switches formatted↔raw, which re-derives this row's
            // layout — under Wrap the raw (wider) cell can wrap to more/fewer visual rows, so the row REFLOWS and
            // its height changes; under Truncate the height is invariant (one visual row) but its drawn content
            // and caret stops change. Re-derive here (not just re-raster) so the row's fragments/run map match.
            int oldHeight = _lines.Length;
            _lines = BuildLines(out _signature);
            if (_lines.Length != oldHeight)
                InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The cell-overflow mode this row lays out under (<see cref="TablePresenter.OverflowMode"/>); the owner sets it before <see cref="Refresh"/> so a mode toggle re-derives the row.</summary>
    internal TableOverflow Overflow
    {
        get => _overflow;
        set => _overflow = value;
    }

    /// <summary>Number of <see cref="Render"/> calls — the per-row raster observable the R3 benchmark diffs against.</summary>
    public int RenderCount { get; private set; }

    /// <summary>
    /// Number of <see cref="Refresh"/> re-derivations (LayoutRow + run-map + signature rebuild) this row has
    /// run — the WP5 "skip unchanged rows" observable (spike-review deferred #7). Before WP5 every reconcile
    /// re-derived every row; now only a row whose source or column geometry moved re-derives, so a
    /// stable-geometry keystroke advances this on exactly the edited row.
    /// </summary>
    public int DeriveCount { get; private set; }

    /// <summary>The row's visual lines (run maps per visual row) — test observability for the border/cell assertions.</summary>
    internal IReadOnlyList<TableVisualLine> VisualLines => _lines;

    /// <summary>
    /// Re-derives the row from a fresh model/metrics/source (the reconcile path) and reports what changed —
    /// the owning <see cref="TablePresenter"/> decides invalidation (a height change re-measures, a
    /// content-only change re-rasters just this one zone, an unchanged row is left untouched, giving the
    /// R3 "one row zone" economy). No invalidation happens here.
    /// </summary>
    /// <returns>Whether the row's height and/or drawn content changed.</returns>
    public (bool HeightChanged, bool ContentChanged) Refresh(TableModel model, TableGridMetrics metrics, string blockText)
    {
        _model = model;
        _metrics = metrics;
        _blockText = blockText;
        DeriveCount++;

        int oldHeight = _lines.Length;
        _lines = BuildLines(out string signature);
        bool heightChanged = _lines.Length != oldHeight;
        bool contentChanged = heightChanged || !string.Equals(signature, _signature, StringComparison.Ordinal);
        _signature = signature;

        return (heightChanged, contentChanged);
    }

    /// <summary>
    /// Re-binds an <b>unchanged</b> row to the post-reconcile model/source without re-deriving it (the WP5
    /// skip path). The row's rendered content is identical, so its wrapped layout, run map and signature are
    /// reused as-is; only its block-relative cell offsets are shifted by <paramref name="offsetDelta"/> — an
    /// intra-cell edit on an <i>earlier</i> row moves every later row's source offsets by the splice delta,
    /// and those offsets must stay current so a selection (resolved against the live document range) lands on
    /// the right cells. O(runs), no LayoutRow / wrap / signature rebuild, and not counted as a re-derive.
    /// </summary>
    public void Rebind(TableModel model, TableGridMetrics metrics, string blockText, int offsetDelta)
    {
        _model = model;
        _metrics = metrics;
        _blockText = blockText;

        if (offsetDelta == 0)
            return;

        foreach (var line in _lines)
        {
            for (var i = 0; i < line.Runs.Length; i++)
            {
                var run = line.Runs[i];
                if (run.SrcLen > 0) // only source-mapped cell runs; synthetic borders carry no source
                    line.Runs[i] = run with { SrcStart = run.SrcStart + offsetDelta };
            }
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => new(_metrics.Width, _lines.Length);

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        RenderCount++;

        if (context.Bounds.IsEmpty)
            return;

        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        var borderBrush = MarkdownStyles.RuleBrush(this);
        var headerFill = MarkdownStyles.CodeFillBrush(this);
        var headerStyle = CellStyle.Default.WithAttributes(TextAttributes.Bold);
        var selection = ResolveSelection(context);

        // The cell-rect (M3.WP8): when the multi-cell selection covers THIS row, its cells highlight as WHOLE
        // cells (below), overriding the WP5 per-cell text highlight; otherwise the text highlight is used.
        var rect = CellRectProvider?.Invoke();
        bool rowInRect = rect is { } rc && rc.ContainsRow(_logicalRow)
            && rc.Col0 <= rc.Col1 && rc.Col0 < _metrics.ColumnCount && rc.Col1 >= 0;

        // The column-window (M3.WP6): the whole grid draws translated left by `offset` cells and clipped to
        // `drawWidth` (≤ viewport). Non-overflowing tables get (0, gridWidth) — the pre-WP6 behaviour verbatim.
        var (offset, drawWidth) = ResolveWindow();

        for (var row = 0; row < _lines.Length; row++)
        {
            var line = _lines[row];

            if (line is { Kind: TableLineKind.Content, IsHeader: true } && drawWidth > 0)
                context.FillRectangle(new Rect(0, row, drawWidth, 1), headerFill); // header fill under the glyphs

            // Whole-cell rect fill (M3.WP8): before the glyphs, paint the full box (padding incl.) of each selected
            // cell on this content row — the SelectionBrush fill (colour) or Inverse spaces (NoColor) — so an empty
            // cell and the cell padding carry the highlight too. Interior dividers stay normal border glyphs.
            if (rowInRect && line.Kind == TableLineKind.Content && selection.Active)
                FillRectCells(context, row, rect!.Value, offset, drawWidth, foreground, selection);

            foreach (var run in line.Runs)
            {
                // Translate into the window; a run whose first cell is off-window is not drawn (the window is
                // column-granular, so a drawn column sits wholly inside [0, drawWidth) — no half-cell clip).
                int col = run.Col - offset;
                if (col < 0 || col >= drawWidth)
                    continue;

                if (run.Kind == RunKind.Synthetic)
                {
                    if (run.Glyph is { Length: > 0 } glyph)
                    {
                        // The truncation … rides the content colour; box-drawing glyphs (│ ┼ ─) are structure.
                        bool isEllipsis = string.Equals(glyph, TableBox.Ellipsis, StringComparison.Ordinal);
                        var brush = isEllipsis ? foreground : borderBrush;
                        // The … sits inside a cell, so under a rect on NoColor it rides the whole-cell Inverse like
                        // the cell text (else it re-draws plain over the Inverse box and reads unselected). Dividers
                        // stay plain — they are never highlighted.
                        var glyphStyle = isEllipsis && rowInRect && selection.NoColor && InRectColumns(run.Col, rect!.Value)
                            ? CellStyle.Default.AddAttributes(TextAttributes.Inverse)
                            : CellStyle.Default;
                        context.DrawText(col, row, glyph, brush, null, glyphStyle);
                    }

                    continue;
                }

                if (run.SrcLen <= 0)
                    continue;

                var slice = Slice(run.SrcStart, run.SrcLen);
                if (slice.IsEmpty)
                    continue;

                // A FORMATTED cell's Text run carries its inline formatting (bold/italic/strike/link → attributes,
                // code → the code fill), composed with the header's bold exactly as the prose ParagraphPresenter
                // styles the same inline kinds; a RAW cell (plain / active) carries RunStyle.None → the base style.
                var (style, background) = ResolveTextStyle(run, line.IsHeader, headerFill);

                // The rect (M3.WP8) owns the whole selection: a run in a rect column draws as a WHOLE-cell selection
                // — routed through DrawCellText so the colour/NoColor decision lives in ONE place (fill-background on
                // colour, Inverse on NoColor), covering the run's full source span; a non-rect column on a rect row
                // is unselected (default). Outside a rect the WP5 range highlight is passed straight through.
                var cellSelection = rowInRect
                    ? InRectColumns(run.Col, rect!.Value) ? CoverRun(run, selection) : default
                    : selection;
                DrawCellText(context, col, row, slice, run.SrcStart, foreground, background, style, cellSelection);
            }

            // Truncate reveal-on-focus (§5.6): the focused cell re-draws its FULL content on top of the truncated
            // base, overflowing into the cells to its right on this content row (spreadsheet-style) but clipped
            // before the grid's right border. Non-focused cells stay truncated; a caret move re-rasters this zone.
            if (_overflow == TableOverflow.Truncate && _activeColumn >= 0 && line.Kind == TableLineKind.Content)
            {
                // Under a rect the reveal of a SELECTED cell carries the whole-cell highlight, but CLIPPED to the
                // cell's own box: its rightward overflow into neighbours draws plain, so a neighbour outside the rect
                // is never painted selected (on NoColor the overflow would otherwise render Inverse across it; on
                // colour it stays plain either way, the box fill showing through). A focused cell outside the rect
                // columns is unselected. Outside a rect the WP5 range highlight is passed as before.
                bool cellInRect = rowInRect && rect!.Value.Contains(_logicalRow, _activeColumn);
                CellSelection revealSel = cellInRect ? FullCellSelection(_activeColumn, selection) : rowInRect ? default : selection;
                DrawActiveReveal(context, row, offset, drawWidth, line.IsHeader ? headerStyle : CellStyle.Default, foreground, revealSel, clipToBox: cellInRect);
            }
        }
    }

    /// <summary>A <see cref="CellSelection"/> covering the whole content of cell (<see cref="_logicalRow"/>, <paramref name="column"/>) — so the Truncate reveal of a rect cell highlights its revealed content (Inverse on NoColor), clipped to the cell's box by the caller.</summary>
    private CellSelection FullCellSelection(int column, in CellSelection selection)
    {
        var (start, end) = _model.CellContentRange(_logicalRow, column);
        return new CellSelection(start, end, selection.NoColor, selection.Brush);
    }

    /// <summary>A <see cref="CellSelection"/> covering the whole of <paramref name="run"/>'s source span (M3.WP8) — a rect cell's content is drawn wholly selected via <see cref="DrawCellText"/>, so the colour/NoColor decoration is decided in one place.</summary>
    private static CellSelection CoverRun(Run run, in CellSelection selection)
        => new(run.SrcStart, run.SrcStart + run.SrcLen, selection.NoColor, selection.Brush);

    /// <summary>
    /// Paints the whole-cell highlight (M3.WP8) for each selected cell on content grid row <paramref name="drawRow"/>:
    /// the full box <c>[BorderX(c)+1, BorderX(c+1))</c> — the left padding, content, and right padding of columns
    /// <c>[Col0..Col1]</c> — translated into the column-window and clipped to <paramref name="drawWidth"/>. On the
    /// colour tier it fills with the <see cref="ThemeKeys.SelectionBrush"/>; on NoColor it draws Inverse spaces so
    /// the padding carries the highlight (the cell text is then re-drawn Inverse in the run loop). Interior dividers
    /// are left untouched (normal border glyphs), so the rect reads as highlighted boxes with gridlines between.
    /// </summary>
    private void FillRectCells(RenderContext context, int drawRow, CellRect rect, int offset, int drawWidth, IBrush foreground, in CellSelection selection)
    {
        int first = Math.Max(0, rect.Col0);
        int last = Math.Min(_metrics.ColumnCount - 1, rect.Col1);
        for (var c = first; c <= last; c++)
        {
            int x0 = Math.Max(0, _metrics.BorderX(c) + 1 - offset);       // just past the left divider
            int x1 = Math.Min(drawWidth, _metrics.BorderX(c + 1) - offset); // up to (excl.) the right divider
            int w = x1 - x0;
            if (w <= 0)
                continue;

            if (selection.NoColor)
                context.DrawText(x0, drawRow, Repeat(" ", w), foreground, null, CellStyle.Default.AddAttributes(TextAttributes.Inverse));
            else if (selection.Brush is { } brush)
                context.FillRectangle(new Rect(x0, drawRow, w, 1), brush);
        }
    }

    /// <summary>Whether a text run at grid column <paramref name="runCol"/> falls inside the cell-rect's columns <c>[Col0..Col1]</c> (its content region, alignment-agnostic).</summary>
    private bool InRectColumns(int runCol, CellRect rect)
    {
        int first = Math.Max(0, rect.Col0);
        int last = Math.Min(_metrics.ColumnCount - 1, rect.Col1);
        if (first > last)
            return false;
        return runCol >= _metrics.ContentX(first) && runCol < _metrics.ContentX(last) + _metrics.ColumnWidth(last);
    }

    /// <summary>The window (<c>Offset</c>, <c>DrawWidth</c>) this row draws through, or the whole grid when no column-window is active.</summary>
    private (int Offset, int DrawWidth) ResolveWindow()
    {
        if (WindowProvider?.Invoke() is { DrawWidth: > 0 } window)
            return window;
        return (0, _metrics.Width);
    }

    /// <summary>
    /// Draws the focused cell's full content (un-truncated) at its natural column, translated into the window and
    /// clipped just short of the grid's right border so the outer box survives. The caret map reports this same
    /// natural geometry, so the caret lands on the revealed grapheme; the selection composes in as everywhere else.
    /// </summary>
    private void DrawActiveReveal(RenderContext context, int drawRow, int offset, int drawWidth, CellStyle style, IBrush foreground, in CellSelection selection, bool clipToBox = false)
    {
        var (start, end) = _model.CellContentRange(_logicalRow, _activeColumn);
        if (end <= start)
            return; // an empty focused cell has nothing to reveal

        var content = Slice(start, end - start);
        if (content.IsEmpty)
            return;

        // A cell that already fits its column was drawn IN FULL (and aligned) by the base pass — nothing to
        // reveal, and re-drawing it left-anchored would shove a right/center-aligned cell out of place. Only an
        // over-wide (truncated) cell reveals, and it is left-anchored (matching the truncated prefix + the map).
        if (GraphemeWidth.StringWidth(content) <= _metrics.ColumnWidth(_activeColumn))
            return;

        int startCol = _metrics.ContentX(_activeColumn) - offset;
        // Clip strictly left of the grid's right border cell AND the window edge — whichever is nearer.
        int rightLimit = Math.Min(drawWidth, _metrics.BorderX(_metrics.ColumnCount) - offset);
        if (startCol < 0 || startCol >= rightLimit)
            return;

        // Keep the whole-cluster prefix that fits [startCol, rightLimit) — never a half wide cluster over the edge.
        int drawnAt = startCol, visLen = 0;
        var clusters = content.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            int w = GraphemeWidth.StringWidth(clusters.Current);
            if (drawnAt + w > rightLimit)
                break;
            drawnAt += w;
            visLen += clusters.Current.Length;
        }

        if (visLen <= 0)
            return;

        // Under a rect (clipToBox), scope the SELECTED decoration to the focused cell's own column box: only the
        // first ColumnWidth cells of content sit in the box, so the reveal's overflow past it draws plain and never
        // paints a neighbour cell selected (M3.WP8 — the NoColor Inverse would otherwise bleed across neighbours).
        var revealSelection = selection;
        if (clipToBox && selection.Active)
        {
            int boxPrefix = PrefixLengthFitting(content, _metrics.ColumnWidth(_activeColumn));
            revealSelection = selection with { End = Math.Min(selection.End, start + boxPrefix) };
        }

        DrawCellText(context, startCol, drawRow, content[..visLen], start, foreground, null, style, revealSelection);
    }

    /// <summary>The UTF-16 length of the longest whole-grapheme-cluster prefix of <paramref name="text"/> whose display width fits <paramref name="width"/> cells (never splitting a wide cluster).</summary>
    private static int PrefixLengthFitting(ReadOnlySpan<char> text, int width)
    {
        int used = 0, len = 0;
        var clusters = text.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            int w = GraphemeWidth.StringWidth(clusters.Current);
            if (used + w > width)
                break;
            used += w;
            len += clusters.Current.Length;
        }

        return len;
    }

    // ───────────────────────────── selection highlight (WP4-deferred #2) ─────────────────────────────

    /// <summary>
    /// The document selection resolved for this Render pass: the block-relative range (from
    /// <see cref="SelectionProvider"/>), the NoColor tier flag (highlight rides <see cref="TextAttributes.Inverse"/>
    /// there, a background scrim degrading to nothing), and the color-tier fill brush
    /// (<see cref="ThemeKeys.SelectionBrush"/>). Resolved once so the per-fragment path is field reads, mirroring
    /// <see cref="LeafBlockPresenter"/>'s seam.
    /// </summary>
    private CellSelection ResolveSelection(RenderContext context)
    {
        if (SelectionProvider?.Invoke() is not { } range || range.End <= range.Start)
            return default;

        if (context.Capabilities.Color.Depth == ColorDepth.NoColor)
            return new CellSelection(range.Start, range.End, NoColor: true, Brush: null);

        if (this.TryFindResource(ThemeKeys.SelectionBrush, out var value) && value is IBrush brush)
            return new CellSelection(range.Start, range.End, NoColor: false, brush);

        return default;
    }

    /// <summary>
    /// Draws one cell fragment's <paramref name="text"/> at cell (<paramref name="x"/>, <paramref name="row"/>)
    /// with the document <paramref name="selection"/> composed into the sub-span it covers — the block-relative
    /// selection is intersected with the fragment's source span <c>[srcStart, srcStart + len)</c> and the covered
    /// sub-span is drawn with the selection fill (or <see cref="TextAttributes.Inverse"/> on NoColor). Splits land
    /// on grapheme boundaries (a wide cluster is highlighted whole), so a selected CJK/emoji cell paints cleanly.
    /// When nothing on the fragment is selected this is one ordinary <c>DrawText</c>.
    /// </summary>
    private void DrawCellText(
        RenderContext context, int x, int row, ReadOnlySpan<char> text, int srcStart,
        IBrush foreground, IBrush? background, CellStyle style, in CellSelection selection)
    {
        int relFrom = selection.Start - srcStart;
        int relTo = selection.End - srcStart;
        if (!selection.Active || relTo <= 0 || relFrom >= text.Length)
        {
            context.DrawText(x, row, text, foreground, background, style);
            return;
        }

        relFrom = Math.Max(0, relFrom);
        relTo = Math.Min(text.Length, relTo);

        // Walk grapheme clusters, marking the drawn cell where the selected UTF-16 sub-range begins/ends —
        // boundaries are cluster-aligned, so a wide cluster straddling an edge is taken whole into the selection.
        int cell = x, index = 0;
        int prefixEnd = -1, selEnd = -1, selStartCell = x, selEndCell = x;
        var clusters = text.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            if (prefixEnd < 0 && index >= relFrom) { prefixEnd = index; selStartCell = cell; }
            if (selEnd < 0 && index >= relTo) { selEnd = index; selEndCell = cell; }
            cell += GraphemeWidth.StringWidth(clusters.Current);
            index += clusters.Current.Length;
        }

        if (prefixEnd < 0) { prefixEnd = text.Length; selStartCell = cell; }
        if (selEnd < 0) { selEnd = text.Length; selEndCell = cell; }

        if (prefixEnd > 0)
            context.DrawText(x, row, text[..prefixEnd], foreground, background, style);

        if (selEnd > prefixEnd)
        {
            var selected = text[prefixEnd..selEnd];
            if (selection.NoColor)
                context.DrawText(selStartCell, row, selected, foreground, null, style.AddAttributes(TextAttributes.Inverse));
            else
                context.DrawText(selStartCell, row, selected, foreground, selection.Brush, style); // selection fill replaces any code fill — no hole
        }

        if (selEnd < text.Length)
            context.DrawText(selEndCell, row, text[selEnd..], foreground, background, style);
    }

    /// <summary>
    /// The cell style + background a Text run draws with (§2.1): the run's inline <see cref="RunStyle"/> mapped to
    /// <see cref="TextAttributes"/> (composed with the header's bold), and the code fill for a code run — the same
    /// styling the prose <see cref="LeafBlockPresenter.StyleForContent"/> applies to the same inline kinds. A raw
    /// cell's run carries <see cref="RunStyle.None"/>, so this reduces to the base header/plain style.
    /// </summary>
    private static (CellStyle Style, IBrush? Background) ResolveTextStyle(in Run run, bool isHeader, IBrush codeFill)
    {
        var attributes = MarkdownStyles.AttributesFor(run.Style);
        if (isHeader)
            attributes |= TextAttributes.Bold;
        var background = (run.Style & RunStyle.Code) != 0 ? codeFill : null;
        return (CellStyle.Default.WithAttributes(attributes), background);
    }

    /// <summary>Maps a Document <see cref="CellInlineStyle"/> to the app's <see cref="RunStyle"/> (identical flag layout; mapped explicitly so the two can evolve independently).</summary>
    private static RunStyle ToRunStyle(CellInlineStyle style)
    {
        var result = RunStyle.None;
        if ((style & CellInlineStyle.Bold) != 0) result |= RunStyle.Bold;
        if ((style & CellInlineStyle.Italic) != 0) result |= RunStyle.Italic;
        if ((style & CellInlineStyle.Strikethrough) != 0) result |= RunStyle.Strikethrough;
        if ((style & CellInlineStyle.Code) != 0) result |= RunStyle.Code;
        if ((style & CellInlineStyle.Link) != 0) result |= RunStyle.Link;
        return result;
    }

    /// <summary>The resolved per-pass selection state (block-relative range + tier decoration) the fragment draw reads.</summary>
    private readonly record struct CellSelection(int Start, int End, bool NoColor, IBrush? Brush)
    {
        /// <summary>Whether a selection is active this pass (the default is "nothing selected").</summary>
        public bool Active => End > Start;
    }

    // ───────────────────────────── visual-line (run-map) construction ─────────────────────────────

    private TableVisualLine[] BuildLines(out string signature)
    {
        // Per-cell reveal (Decision 9): the active cell renders RAW in both overflow modes — under Wrap it wraps
        // raw (the row reflows); under Truncate its raw prefix is the base the DrawActiveReveal overlay reveals full.
        var layout = _model.LayoutRow(_logicalRow, _metrics.ColumnWidths, _overflow, _activeColumn);
        bool isLast = _logicalRow == _model.RowCount - 1;

        var lines = new List<TableVisualLine>();
        if (_logicalRow == 0)
            lines.Add(BorderLine(TableLineKind.TopBorder));

        foreach (var visualRow in layout.VisualRows)
            lines.Add(ContentLine(visualRow, layout.IsHeader));

        lines.Add(BorderLine(isLast ? TableLineKind.BottomBorder : TableLineKind.Separator));

        signature = Signature(lines);
        return [.. lines];
    }

    private TableVisualLine BorderLine(TableLineKind kind)
    {
        int columns = _metrics.ColumnCount;
        var runs = new List<Run>(columns * 2 + 1);

        for (var c = 0; c <= columns; c++)
        {
            string junction = Junction(kind, c, columns);
            runs.Add(new Run(0, 0, _metrics.BorderX(c), 1, RunKind.Synthetic) { Glyph = junction });

            if (c < columns)
            {
                int fillStart = _metrics.BorderX(c) + 1;
                int fillWidth = _metrics.BorderX(c + 1) - fillStart;
                if (fillWidth > 0)
                    runs.Add(new Run(0, 0, fillStart, fillWidth, RunKind.Synthetic) { Glyph = Repeat(TableBox.Horizontal, fillWidth) });
            }
        }

        return new TableVisualLine(kind, isHeader: false, [.. runs]);
    }

    private TableVisualLine ContentLine(TableVisualRow visualRow, bool isHeader)
    {
        int columns = _metrics.ColumnCount;
        var runs = new List<Run>(columns * 2 + 1);

        for (var c = 0; c <= columns; c++)
            runs.Add(new Run(0, 0, _metrics.BorderX(c), 1, RunKind.Synthetic) { Glyph = TableBox.Vertical });

        for (var c = 0; c < columns; c++)
        {
            var fragment = visualRow.Cell(c);
            if (fragment.IsEmpty)
                continue;

            // A truncated fragment is left-anchored (the … sits flush at the trailing edge); a fitting/wrapped one
            // honours the column's alignment. The … is a synthetic run — no caret stop — one cell past the prefix.
            int x = fragment.Ellipsis ? _metrics.ContentX(c) : _metrics.AlignedX(c, fragment.Width);

            if (fragment.StyledRuns is { Count: > 0 } styled)
            {
                // A FORMATTED (marks-hidden) cell: one styled Text run per content run, each mapping to its
                // block-relative source slice (== its display text, 1:1) and carrying its inline formatting.
                foreach (var sr in styled)
                    runs.Add(new Run(sr.SrcStart, sr.SrcLength, x + sr.CellOffset, sr.Width, RunKind.Text) { Style = ToRunStyle(sr.Style) });
            }
            else
            {
                // A RAW cell (plain, or the active cell the caret is in): draw the source slice verbatim.
                runs.Add(new Run(fragment.SrcStart, fragment.SrcLength, x, fragment.Width, RunKind.Text));
            }

            if (fragment.Ellipsis)
                runs.Add(new Run(0, 0, x + fragment.Width, TableModel.EllipsisWidth, RunKind.Synthetic) { Glyph = TableBox.Ellipsis });
        }

        runs.Sort(static (l, r) => l.Col - r.Col);
        return new TableVisualLine(TableLineKind.Content, isHeader, [.. runs]);
    }

    private static string Junction(TableLineKind kind, int column, int columnCount)
    {
        bool first = column == 0;
        bool last = column == columnCount;
        return kind switch
        {
            TableLineKind.TopBorder => first ? TableBox.TopLeft : last ? TableBox.TopRight : TableBox.TopTee,
            TableLineKind.BottomBorder => first ? TableBox.BottomLeft : last ? TableBox.BottomRight : TableBox.BottomTee,
            _ => first ? TableBox.MidLeft : last ? TableBox.MidRight : TableBox.Cross, // Separator
        };
    }

    private static string Repeat(string glyph, int count)
    {
        if (count <= 1)
            return glyph;
        return string.Create(count, glyph, static (span, g) => span.Fill(g[0]));
    }

    // A cheap render signature: the runs' placed glyphs/slices in draw order, so an intra-cell edit that
    // changes what this row draws is detected (→ re-raster) while an untouched row stays byte-identical.
    private string Signature(List<TableVisualLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            foreach (var run in line.Runs)
            {
                sb.Append(run.Col).Append(':');
                if (run.Kind == RunKind.Synthetic)
                    sb.Append(run.Glyph);
                else
                    sb.Append(Slice(run.SrcStart, run.SrcLen));
                sb.Append('#').Append((int)run.Style); // a formatting-only change (an inactive cell restyled, same text) must re-raster too
                sb.Append('|');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private ReadOnlySpan<char> Slice(int start, int length)
    {
        if (start < 0 || start >= _blockText.Length || length <= 0)
            return default;
        return _blockText.AsSpan(start, Math.Min(length, _blockText.Length - start));
    }
}
