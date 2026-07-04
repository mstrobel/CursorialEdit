using Cursorial.Rendering.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Layout;

/// <summary>
/// Shared builders and mapping invariants for the M2.WP5 run-map suites. Plain-text blocks are built
/// straight from source (empty inline runs — the mark classification then produces only
/// <see cref="RunKind.Text"/>, so the map must equal M1's <see cref="BlockRunMap"/> bit-for-bit);
/// markdown blocks are parsed through the real <see cref="BlockHarness"/> so their
/// <see cref="Block.InlineRuns"/> are the genuine Markdig projection.
/// </summary>
internal static class RunMapHarness
{
    /// <summary>The block's source lines split by the real buffer (endings preserved).</summary>
    public static IReadOnlyList<Line> Lines(string text)
    {
        var buffer = new DocumentBuffer(text);
        return [.. Enumerable.Range(0, buffer.LineCount).Select(buffer.GetLine)];
    }

    /// <summary>A run map for a plain-text paragraph (no marks, no synthetics).</summary>
    public static RunMap Plain(string text, int wrap = 0, WrapMode mode = WrapMode.WordWrap, int? active = null)
        => RunMapBuilder.Build(Lines(text), [], BlockKind.Paragraph, null, wrap, mode, active);

    /// <summary>A run map for the given block of a parsed markdown document (real inline runs).</summary>
    public static RunMap Map(string markdown, int wrap = 0, WrapMode mode = WrapMode.WordWrap, int? active = null, int blockIndex = 0)
    {
        var h = BlockHarness.Create(markdown);
        int start = h.Blocks.GetStartLine(blockIndex);
        var block = h.Blocks[blockIndex];
        var lines = Enumerable.Range(0, block.LineCount).Select(k => h.Buffer.GetLine(start + k)).ToList();
        return RunMapBuilder.Build(lines, block.InlineRuns, block.Kind, block.HeadingLevel, wrap, mode, active);
    }

    /// <summary>The single run of <paramref name="row"/> with the given kind (asserts exactly one exists).</summary>
    public static Run SingleRun(this RunMap map, int row, RunKind kind)
        => map.RunsForRow(row).ToArray().Single(r => r.Kind == kind);

    /// <summary>All runs of <paramref name="row"/> with the given kind, in cell order.</summary>
    public static Run[] Runs(this RunMap map, int row, RunKind kind)
        => [.. map.RunsForRow(row).ToArray().Where(r => r.Kind == kind)];

    /// <summary>
    /// The binding source↔cell totality proof: <see cref="RunMap.Locate"/> is total and valid over
    /// every source offset (both affinities) and <see cref="RunMap.OffsetAt"/> is total, valid,
    /// monotone, and round-trips over every (row, cell).
    /// </summary>
    public static void AssertTotalMapping(RunMap map)
    {
        Assert.True(map.RowCount >= 1);

        // Source → cell: every block-relative offset maps to a valid, in-bounds (row, cell).
        for (var src = 0; src <= map.SourceLength; src++)
        {
            foreach (var affinity in new[] { false, true })
            {
                var (row, cell) = map.Locate(src, affinity);
                Assert.InRange(row, 0, map.RowCount - 1);
                Assert.InRange(cell, 0, map.RowWidth(row));
            }
        }

        // Cell → source: every (row, cell) maps back to a valid offset, non-decreasing in cell, and
        // the landing round-trips (some affinity locates a cell that maps to exactly the same offset).
        for (var row = 0; row < map.RowCount; row++)
        {
            int width = map.RowWidth(row);
            int previous = -1;
            for (var cell = 0; cell <= width; cell++)
            {
                int src = map.OffsetAt(row, cell);
                Assert.InRange(src, 0, map.SourceLength);
                Assert.True(src >= previous, $"OffsetAt not monotone at row {row} cell {cell}");
                previous = src;

                var (r0, c0) = map.Locate(src, endAffinity: false);
                var (r1, c1) = map.Locate(src, endAffinity: true);
                Assert.True(map.OffsetAt(r0, c0) == src || map.OffsetAt(r1, c1) == src,
                    $"round-trip failed at row {row} cell {cell} src {src}");
            }
        }
    }
}
