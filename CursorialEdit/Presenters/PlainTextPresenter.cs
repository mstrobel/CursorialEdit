using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// M1's render-only block element: draws its block's soft-wrapped rows from the
/// <see cref="BlockRunMap"/> (architecture Decision 7). Every presenter is its own render
/// boundary, so a keystroke re-rasters exactly one block zone — never the band — and an in-band
/// scroll re-rasters nothing. Presenters are inert visuals: no focus, no input; the
/// <c>EditorControl</c> owns both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live height.</b> Measure asks the run-map source for the block's map at the offered width
/// and returns its wrap-row count — heights are live, so a width change re-measures and re-wraps
/// through the ordinary layout pass. The <c>DocumentPanel</c>'s height source derives block slot
/// heights from the same maps, so the two agree by construction.
/// </para>
/// <para>
/// <b>Raster counter.</b> <see cref="RenderCount"/> increments once per <see cref="Render"/> —
/// the app-side raster observability this milestone's gate asserts against (keystroke re-rasters
/// exactly one block; in-band scroll re-rasters zero). M2.WP13 and M7.WP1 <i>extend</i> this
/// counter surface; they do not re-create it.
/// </para>
/// </remarks>
public sealed class PlainTextPresenter : UIElement
{
    private readonly IBlockRunMapSource _source;

    /// <summary>Creates the presenter for <paramref name="block"/>, drawing from <paramref name="source"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public PlainTextPresenter(IBlockRunMapSource source, BlockId block)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        Block = block;
        IsRenderBoundary = true; // Decision 7 — one small raster zone per block
    }

    /// <summary>The identity of the block this presenter renders. Stable across index and line shifts.</summary>
    public BlockId Block { get; }

    /// <summary>Number of <see cref="Render"/> calls — the raster-economics observable (see the class remarks).</summary>
    public int RenderCount { get; private set; }

    /// <summary>
    /// De-realization hook for the presenter registry (set by the creating bridge): invoked once
    /// from <see cref="UIElement.TearDown"/> so the registry never holds dead elements.
    /// </summary>
    internal Action<PlainTextPresenter>? TornDownCallback { get; set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var map = _source.GetRunMap(Block, Math.Max(1, availableSize.Columns));
        return new Size(availableSize.Columns, map.RowCount);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Selection overlay (M1.WP8).</b> When the run-map source also serves the document
    /// selection (<see cref="ISelectionSource"/> — the <see cref="IBlockViewSource"/> type-test
    /// idiom), each row is intersected with the block's selection slice at draw time
    /// (architecture §2.4) and painted as up to three runs: plain / selected / plain. The
    /// selected run uses the theme's selection fill (<see cref="ThemeKeys.SelectionBrush"/>, the
    /// <c>TextPresenter</c> convention) as its background; on the NoColor tier it degrades to
    /// <see cref="TextAttributes.Inverse"/> — the BuiltIn non-color selection channel. Selection
    /// endpoints and row boundaries are cluster boundaries, so a wide cluster is never split by
    /// the overlay (whole-cell discipline). A blank row inside the selection paints nothing,
    /// matching the framework reference.
    /// </remarks>
    protected override void Render(RenderContext context)
    {
        RenderCount++;

        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        var map = _source.GetRunMap(Block, Math.Max(1, context.Size.Columns));
        var foreground = TextElement.GetForeground(this) ?? Brushes.Default;
        var selection = (_source as ISelectionSource)?.GetSelection(Block);

        int rows = Math.Min(map.RowCount, bounds.Rows);
        for (var row = 0; row < rows; row++)
        {
            var text = map.RowText(row);

            if (selection is { } range)
            {
                var run = map.RunsForRow(row)[0];
                int from = Math.Clamp(range.Start - run.SrcStart, 0, run.SrcLen);
                int to = Math.Clamp(range.End - run.SrcStart, 0, run.SrcLen);
                if (to > from)
                {
                    DrawSelectedRow(context, row, text, from, to, foreground);
                    continue;
                }
            }

            if (!text.IsEmpty)
                context.DrawText(0, row, text, foreground);
        }
    }

    /// <summary>Draws one row split around its selected slice <c>[from, to)</c> (row-text-relative cluster boundaries).</summary>
    private void DrawSelectedRow(RenderContext context, int row, ReadOnlySpan<char> text, int from, int to, IBrush foreground)
    {
        int fromCell = CaretNavigator.CellOfCol(text, from);
        int toCell = CaretNavigator.CellOfCol(text, to);

        if (from > 0)
            context.DrawText(0, row, text[..from], foreground);

        if (context.Capabilities.Color.Depth == ColorDepth.NoColor)
        {
            context.DrawText(fromCell, row, text[from..to], foreground, null,
                CellStyle.Default.WithAttributes(TextAttributes.Inverse));
        }
        else
        {
            var selectionBrush = this.TryFindResource(ThemeKeys.SelectionBrush, out var value) && value is IBrush brush
                ? brush
                : null;
            context.DrawText(fromCell, row, text[from..to], foreground, selectionBrush);
        }

        if (to < text.Length)
            context.DrawText(toCell, row, text[to..], foreground);
    }

    /// <inheritdoc/>
    protected override void OnTearDown()
    {
        var callback = TornDownCallback;
        TornDownCallback = null;
        callback?.Invoke(this);
        base.OnTearDown();
    }
}
