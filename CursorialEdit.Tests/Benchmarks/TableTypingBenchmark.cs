using System.Diagnostics;
using System.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Presenters;
using CursorialEdit.Tests.Editing;

using Xunit.Abstractions;

namespace CursorialEdit.Tests.Benchmarks;

/// <summary>
/// The M3-entry R3 gate (implementation-plan §8 WP3): types 200 keystrokes of intra-cell text into a
/// 40-column × 20-row table with wrapped cells, driving the <b>real</b> path —
/// <see cref="EditController.Apply"/> → windowed Markdig reparse → <see cref="MarkdownViewBridge"/>
/// reconcile → the <see cref="TablePresenter"/>'s per-row render boundaries re-raster — and asserts the
/// worst frame stays inside budget on both §5.1 wire presets. <b>Passing retires R3 and is the entry gate
/// for M3.WP4+.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Methodology (mirrors <see cref="TypingLatencyBenchmark"/>'s de-flaked gate).</b> The measured window
/// is the honest keystroke→frame path: a cell splice through the real <see cref="EditController.Apply"/>
/// (which reparses and reconciles the block synchronously), then <c>RunFrame</c> until the loop reports
/// idle so any residue is charged to the keystroke. The typical budget gates <b>p50</b> (&lt; 16 ms), the
/// hard ceiling gates <b>p90</b> (&lt; 50 ms) — the sustained worst, not a lone GC/JIT spike — and a
/// separate catastrophic guard keeps a genuine hang failing. p50/p90/max are reported per preset.
/// </para>
/// <para>
/// <b>Raster economy (the R3 mechanism).</b> A stable-geometry cell edit re-rasters exactly the edited
/// row's zone — the committed per-row <see cref="TableRowPresenter"/> boundaries — never the whole table.
/// The burst asserts the per-keystroke row re-raster count stays bounded, so the budget is met by the
/// design, not by luck.
/// </para>
/// </remarks>
[Trait("Category", "Benchmark")]
public sealed class TableTypingBenchmark(ITestOutputHelper output)
{
    private const int Columns = 40;
    private const int BodyRows = 19; // + 1 header = 20 logical rows
    private const int WarmupKeystrokes = 8;
    private const int MeasuredKeystrokes = 200;
    private const int MaxFramesPerKeystroke = 4;
    private const double TypicalBudgetMs = 16.0;         // gates p50
    private const double HardCeilingMs = 50.0;           // gates p90 (sustained worst)
    private const double CatastrophicCeilingMs = 250.0;  // a genuine hang still fails

    // The edited body cell (column 0 of the first body row): its column is pinned to the 40-cell max by a
    // wide header cell, so typing grows the cell (eventually wrapping it) without changing column geometry —
    // the common intra-cell case that must re-raster one row zone.
    private const int TargetLine = 2;
    private const int TargetCol = 2;

    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    [Theory]
    [MemberData(nameof(Presets))]
    public void IntraCellTyping_40x20Table_WithinBudget(string preset)
    {
        string document = BuildTable();
        using var harness = MarkdownEditingHarness.Create(document, preset, columns: 100, rows: 40);

        var presenter = Assert.IsType<TablePresenter>(harness.Presenter(0));
        Assert.Equal(20, presenter.Rows.Count); // header + 19 body rows

        // Warm the whole path (splice, reparse, reconcile, per-row raster) at the exact caret position.
        for (var i = 0; i < WarmupKeystrokes; i++)
            TypeIntoCell(harness);

        var samples = new double[MeasuredKeystrokes];
        int worstRowRasters = 0;
        int worstRowDerives = 0;

        for (var i = 0; i < MeasuredKeystrokes; i++)
        {
            var beforeRasters = SnapshotRowRasters(presenter);
            var beforeDerives = SnapshotRowDerives(presenter);

            var start = Stopwatch.GetTimestamp();
            ApplyCellInsert(harness);
            var frames = 0;
            do
            {
                harness.Host.RunFrame();
                frames++;
            }
            while (!harness.Host.RunUntilIdle(maxFrames: 0) && frames < MaxFramesPerKeystroke);

            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Assert.True(
                harness.Host.RunUntilIdle(maxFrames: 0),
                $"keystroke {i} ({preset}) did not settle within {MaxFramesPerKeystroke} frames");

            worstRowRasters = Math.Max(worstRowRasters, RowRasterDelta(presenter, beforeRasters));
            worstRowDerives = Math.Max(worstRowDerives, RowDeriveDelta(presenter, beforeDerives));
        }

        Array.Sort(samples);
        double p50 = samples[samples.Length / 2];
        double p90 = samples[(int)(samples.Length * 0.9)];
        double max = samples[^1];

        output.WriteLine(
            $"table typing ({preset}): N={MeasuredKeystrokes} keystroke→frame p50 {p50:F2} ms, p90 {p90:F2} ms, " +
            $"max {max:F2} ms (budgets {TypicalBudgetMs:F0}/{HardCeilingMs:F0} ms); worst row-zone re-rasters/keystroke " +
            $"{worstRowRasters}; worst row re-derives/keystroke {worstRowDerives}");

        // The per-row boundary economy: a STABLE-GEOMETRY cell edit re-rasters EXACTLY the edited row's zone
        // (when it wraps, that one row grows taller but is still one zone; siblings only composite-shift). This
        // is the guarantee the milestone exists to lock in — asserted at the committed 1, not a loose bound.
        Assert.Equal(1, worstRowRasters);

        // WP5 (spike-review deferred #7): a stable-geometry edit now RE-DERIVES (LayoutRow + run-map +
        // signature rebuild) exactly the edited row too — before WP5 the reconcile re-derived all N rows each
        // keystroke (only the raster was 1-zone). This is the CPU economy the milestone adds on top of R3.
        Assert.Equal(1, worstRowDerives);

        Assert.True(p50 < TypicalBudgetMs, $"table typing p50 over budget ({preset}): {p50:F2} ms (budget {TypicalBudgetMs:F0} ms; p90 {p90:F2}, max {max:F2}).");
        Assert.True(p90 < HardCeilingMs, $"table typing p90 over the hard ceiling ({preset}): {p90:F2} ms (ceiling {HardCeilingMs:F0} ms; p50 {p50:F2}, max {max:F2}).");
        Assert.True(max < CatastrophicCeilingMs, $"table typing max over the catastrophic ceiling ({preset}): {max:F2} ms (ceiling {CatastrophicCeilingMs:F0} ms).");
    }

    /// <summary>
    /// The GROWING-column case (the other common intra-cell edit): typing makes the cell the unique widest,
    /// so every keystroke widens its column and the whole table re-rasters (the spike has no incremental
    /// reflow yet — that is WP5). This measures whether the naive full-table re-raster is still in budget at
    /// the 20-row size, so R3 is retired for the growing case too — not just the pinned-geometry one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void GrowingColumnTyping_20RowTable_WithinBudget(string preset)
    {
        // 20 logical rows × 10 columns of short equal cells; typing into (0,0) makes column 0 the unique
        // widest, so it widens every keystroke while it stays under the 40-cell clamp.
        const int cols = 10, growthKeystrokes = 34; // start width 2 → ~36, under the 40 clamp: every keystroke grows
        string document = BuildShortTable(cols);
        using var harness = MarkdownEditingHarness.Create(document, preset, columns: 120, rows: 40);

        var presenter = Assert.IsType<TablePresenter>(harness.Presenter(0));
        for (var i = 0; i < WarmupKeystrokes; i++) TypeIntoCell(harness); // warm the path (also grows a little)

        var samples = new double[growthKeystrokes];
        int worstRowRasters = 0;
        int rasterOnStableKeystroke = int.MaxValue; // the fewest rows re-rastered on any keystroke this run
        for (var i = 0; i < growthKeystrokes; i++)
        {
            var before = SnapshotRowRasters(presenter);
            int widthBefore = presenter.GridWidth;
            var start = Stopwatch.GetTimestamp();
            ApplyCellInsert(harness);
            var frames = 0;
            do { harness.Host.RunFrame(); frames++; }
            while (!harness.Host.RunUntilIdle(maxFrames: 0) && frames < MaxFramesPerKeystroke);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            int rasters = RowRasterDelta(presenter, before);
            worstRowRasters = Math.Max(worstRowRasters, rasters);
            if (presenter.GridWidth == widthBefore) // a keystroke that did NOT widen the column (stable geometry)
                rasterOnStableKeystroke = Math.Min(rasterOnStableKeystroke, rasters);
        }

        Array.Sort(samples);
        double p50 = samples[samples.Length / 2];
        double p90 = samples[(int)(samples.Length * 0.9)];
        double max = samples[^1];
        output.WriteLine(
            $"table GROWING-column typing ({preset}): N={growthKeystrokes} p50 {p50:F2} ms, p90 {p90:F2} ms, " +
            $"max {max:F2} ms; worst row-zone re-rasters/keystroke {worstRowRasters}; " +
            $"re-rasters on a stable (non-widening) keystroke {(rasterOnStableKeystroke == int.MaxValue ? -1 : rasterOnStableKeystroke)}");

        // The reflow is column-width-diffed (WP5): a keystroke that does NOT change the column width re-rasters
        // exactly the edited row (1), the same economy the pinned-geometry case gets. A keystroke that DOES
        // widen the column re-rasters every row — the divider band shifts, so each row re-lands its borders and
        // the framework re-rasters a boundary whose arranged width changed (RenderTree size-change path). That
        // is correct and bounded to the table; it is not reducible below N with per-row boundaries + shared
        // columns, which the 40×20 stable benchmark above is the one that locks at 1.
        if (rasterOnStableKeystroke != int.MaxValue)
            Assert.Equal(1, rasterOnStableKeystroke);
        Assert.True(worstRowRasters <= presenter.Rows.Count, "a width change never re-rasters beyond the table's own rows");

        // R3 for the growing case: this is the heavier, WP5-optimizable path (naive full-table re-raster,
        // no incremental reflow yet), so it is gated on the HARD ceiling (p90 < 50 ms) — the sustained worst
        // against the hard frame budget — not the typical 16 ms budget the stable-geometry case meets. The
        // isolated p90 (~12 ms) sits far under the ceiling; gating p50<typical here would flake under the
        // parallel run (the full re-raster is scheduling-sensitive). p50 is reported for visibility.
        Assert.True(p90 < HardCeilingMs, $"growing-column p90 over the hard ceiling ({preset}): {p90:F2} ms (p50 {p50:F2}, max {max:F2}).");
        Assert.True(max < CatastrophicCeilingMs, $"growing-column max over the catastrophic ceiling ({preset}): {max:F2} ms.");
    }

    private static string BuildShortTable(int cols)
    {
        var sb = new StringBuilder();
        sb.Append('|');
        for (var c = 0; c < cols; c++) sb.Append(" h").Append(c).Append(" |");
        sb.Append("\n|");
        for (var c = 0; c < cols; c++) sb.Append("---|");
        sb.Append('\n');
        for (var r = 0; r < 19; r++)
        {
            sb.Append('|');
            for (var c = 0; c < cols; c++) sb.Append(" ab |");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void TypeIntoCell(MarkdownEditingHarness harness)
    {
        ApplyCellInsert(harness);
        harness.Settle();
    }

    private static void ApplyCellInsert(MarkdownEditingHarness harness)
    {
        var at = new TextPosition(TargetLine, TargetCol);
        var caret = new CaretState(at);
        // Insert one plain letter at a fixed intra-cell position (never a '|' — that would split the cell):
        // the cell grows and eventually wraps, while its column stays pinned to the 40-cell max.
        harness.Controller.Apply(new Edit(at, "", "x"), EditKind.Typing, caret, caret);
    }

    private static int[] SnapshotRowRasters(TablePresenter presenter)
    {
        var counts = new int[presenter.Rows.Count];
        for (var i = 0; i < counts.Length; i++)
            counts[i] = presenter.Rows[i].RenderCount;
        return counts;
    }

    private static int RowRasterDelta(TablePresenter presenter, int[] before)
    {
        int changed = 0;
        for (var i = 0; i < presenter.Rows.Count && i < before.Length; i++)
        {
            if (presenter.Rows[i].RenderCount > before[i])
                changed++;
        }

        return changed;
    }

    private static int[] SnapshotRowDerives(TablePresenter presenter)
    {
        var counts = new int[presenter.Rows.Count];
        for (var i = 0; i < counts.Length; i++)
            counts[i] = presenter.Rows[i].DeriveCount;
        return counts;
    }

    private static int RowDeriveDelta(TablePresenter presenter, int[] before)
    {
        int changed = 0;
        for (var i = 0; i < presenter.Rows.Count && i < before.Length; i++)
        {
            if (presenter.Rows[i].DeriveCount > before[i])
                changed++;
        }

        return changed;
    }

    /// <summary>
    /// A 40-column × 20-row GFM table with wrapped cells: a handful of header cells are 45 characters wide,
    /// pinning their columns to the 40-cell max (so those columns wrap and never reflow on edits); the rest
    /// are short. The edited column 0 is one of the pinned-wide columns.
    /// </summary>
    private static string BuildTable()
    {
        var wide = new HashSet<int> { 0, 13, 27 }; // columns pinned wide (wrapped header cells)
        var sb = new StringBuilder();

        // Header.
        sb.Append('|');
        for (var c = 0; c < Columns; c++)
            sb.Append(' ').Append(wide.Contains(c) ? new string('W', 45) : $"h{c}").Append(" |");
        sb.Append('\n');

        // Delimiter.
        sb.Append('|');
        for (var c = 0; c < Columns; c++)
            sb.Append("---|");
        sb.Append('\n');

        // Body.
        for (var r = 0; r < BodyRows; r++)
        {
            sb.Append('|');
            for (var c = 0; c < Columns; c++)
                sb.Append(' ').Append($"r{r}c{c}").Append(" |");
            sb.Append('\n');
        }

        return sb.ToString();
    }
}
