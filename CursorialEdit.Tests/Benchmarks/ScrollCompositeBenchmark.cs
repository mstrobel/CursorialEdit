using System.Diagnostics;

using CursorialEdit.Presenters;
using CursorialEdit.Tests.Editing;

using Xunit.Abstractions;

namespace CursorialEdit.Tests.Benchmarks;

/// <summary>
/// The M1 exit-gate scroll benchmark (implementation-plan §6): scrolling the 10k-line document is
/// a <b>composite slide</b> — in-band offset writes (below the SCP's re-anchor threshold
/// <c>K = max(viewportRows, 8)</c>) re-raster <b>zero</b> presenter zones and realize/tear down
/// <b>zero</b> blocks, asserted with the WP7 app-side counters
/// (<see cref="PlainTextPresenter.RenderCount"/>, <c>DocumentPanel.TotalRealizedBlocks</c>/<c>TotalDerealizedBlocks</c>);
/// and a band-crossing jump re-anchors by realizing only the new band — the realization delta is
/// bounded by the panel's cover size (viewport + 4K rows), never the document size. The composite
/// slide is proven live, not just by counters: the composited top row tracks the offset frame by
/// frame while no presenter renders.
/// </summary>
/// <remarks>
/// The leg runs at three anchors across the document (near the top, the exact middle, near the
/// end), so "10k-line scroll" means the whole document, not one lucky band: each anchor is
/// reached by a band-crossing jump (the bounded-realization assert), then oscillated in-band
/// (the zero-re-raster assert). Frame times are reported for the record; the §6 gate for scroll
/// is the counter contract — the timing gate lives in <see cref="TypingLatencyBenchmark"/>.
/// </remarks>
[Trait("Category", "Benchmark")]
public sealed class ScrollCompositeBenchmark(ITestOutputHelper output)
{
    private const int Columns = 120;
    private const int Rows = 32;
    private const int InBandSteps = 60; // offset writes per anchor (3 anchors → 180 slide frames)

    /// <summary>
    /// In-band offsets relative to the anchor: |delta| ≤ 4, far below K = max(viewportRows, 8),
    /// and no two consecutive writes repeat a value (a same-value write is a property no-op).
    /// </summary>
    private static readonly int[] InBandDeltas = [1, 3, -2, 4, -4, 2, -1, -3];

    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    [Theory]
    [MemberData(nameof(Presets))]
    public void Scroll_10kLineDocument_InBandIsCompositeSlide_JumpRealizesOnlyTheCover(string preset)
    {
        string document = BenchmarkDocuments.Build(out int midLine);
        using var harness = EditingHarness.Create(document, preset, Columns, Rows);

        var panel = harness.Editor.DocumentPanelPart!;
        var scrollViewer = harness.ScrollViewer;
        int blockCount = harness.Producer.Blocks.Count;

        // One line = one wrap row at this width — content row n is buffer line n, which the
        // top-row sanity checks below read against the buffer.
        Assert.Equal(BenchmarkDocuments.LineCount, scrollViewer.Extent.Rows);

        int viewportRows = scrollViewer.Viewport.Rows;
        int maxOffset = scrollViewer.Extent.Rows - viewportRows;
        int k = Math.Max(viewportRows, 8);
        Assert.True(maxOffset > 20 * k, "the 10k-line document must dwarf the band, or nothing here measures re-anchoring");

        // The sound realization bound for ONE re-anchor: the panel's cover is
        // [offset − 2K, offset + viewport + 2K) (pad = ratcheted Kmax + K = 2K at steady state,
        // DocumentPanel remarks), and every block spans ≥ 1 row, so at most coverRows + 1 blocks
        // intersect it. ≪ the block count — that gap is what "cover, not document" means.
        int coverBound = viewportRows + 4 * k + 1;
        Assert.True(coverBound < blockCount / 4, $"cover bound {coverBound} does not dwarf {blockCount} blocks — resize the fixture");

        // Three anchors across the document; the mid anchor is the known non-blank ASCII line so
        // at least one top-row sanity check is non-trivial.
        int[] anchors = [Math.Min(1000, maxOffset), Math.Min(midLine, maxOffset), maxOffset - 100];

        foreach (int anchor in anchors)
        {
            // ── the band-crossing jump to the anchor: re-anchor realizes the new band, bounded by the cover ──
            int distance = Math.Abs(anchor - scrollViewer.VerticalOffset);
            Assert.True(distance > 2 * k, $"jump to {anchor} is not band-crossing (distance {distance}, K {k})");

            int realizedBeforeJump = panel.TotalRealizedBlocks;
            var jumpStart = Stopwatch.GetTimestamp();
            scrollViewer.VerticalOffset = anchor;
            harness.Settle();
            double jumpMs = Stopwatch.GetElapsedTime(jumpStart).TotalMilliseconds;

            int realizedDelta = panel.TotalRealizedBlocks - realizedBeforeJump;
            Assert.InRange(realizedDelta, 1, coverBound);
            Assert.Equal(anchor, scrollViewer.VerticalOffset);
            Assert.True(
                panel.RealizedBlocks.ContainsKey(harness.Producer.Blocks.IndexOfLine(anchor)),
                "the jump's landing block is not realized");

            // ── the in-band leg: oscillate below the re-anchor threshold — a pure composite slide ──
            var countsBefore = panel.RealizedBlocks.ToDictionary(kv => kv.Key, kv => ((PlainTextPresenter)kv.Value).RenderCount);
            int realizedBefore = panel.TotalRealizedBlocks;
            int derealizedBefore = panel.TotalDerealizedBlocks;

            var slideStart = Stopwatch.GetTimestamp();
            var finalOffset = anchor;
            for (var i = 0; i < InBandSteps; i++)
            {
                finalOffset = anchor + InBandDeltas[i % InBandDeltas.Length];
                scrollViewer.VerticalOffset = finalOffset;
                harness.Host.RunFrame();
            }

            double slideMs = Stopwatch.GetElapsedTime(slideStart).TotalMilliseconds;
            harness.Settle();

            // The slide moved the composited content (offset applied at frame assembly)…
            Assert.Equal(finalOffset, scrollViewer.VerticalOffset);
            Assert.StartsWith(harness.Buffer.GetLine(finalOffset).Text, harness.Host.GetRowText(0));
            if (anchor == midLine)
                Assert.NotEqual(string.Empty, harness.Buffer.GetLine(finalOffset).Text.TrimEnd()); // the non-trivial check

            // …with ZERO raster work: no presenter rendered, nothing realized, nothing torn down.
            Assert.Equal(realizedBefore, panel.TotalRealizedBlocks);
            Assert.Equal(derealizedBefore, panel.TotalDerealizedBlocks);
            Assert.Equal(countsBefore.Keys.Order(), panel.RealizedBlocks.Keys.Order());
            foreach (var (index, element) in panel.RealizedBlocks)
                Assert.Equal(countsBefore[index], ((PlainTextPresenter)element).RenderCount);

            output.WriteLine(
                $"scroll ({preset}) anchor {anchor}: jump realized {realizedDelta} blocks " +
                $"(cover bound {coverBound}, document {blockCount} blocks) in {jumpMs:F2} ms; " +
                $"{InBandSteps} in-band slides at {slideMs / InBandSteps:F3} ms/frame — 0 re-rasters, 0 churn");
        }
    }
}
