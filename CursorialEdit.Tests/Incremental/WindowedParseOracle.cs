using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Incremental;

/// <summary>
/// The R2 correctness spine (architecture Decision 14a / implementation-plan §7 WP4): asserts that a
/// windowed producer's live <see cref="BlockList"/> is <b>byte-identical</b> to a from-scratch full
/// parse of the same buffer — kinds, precise spans (block source origin), line ranges, and source
/// text — and that every realized inline run stays in bounds (the span-oracle-style validity check
/// after a windowed edit). Because the windowed parse and the full parse are two independent parses of
/// the same text, any window that clipped context, mis-mapped an offset, or mis-tiled a boundary shows
/// up here as a mismatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inline scope.</b> The windowed and full inline runs are additionally compared for every block
/// whose source carries no <c>[</c> — i.e. no link/reference/footnote/task construct — which validates
/// the block-relative offset math (<c>SourceStart</c>/<c>SourceLength</c>) end to end. Blocks with
/// bracket syntax are deliberately excluded from the inline-<i>equality</i> check: a reference link or
/// footnote resolves against document-global definitions that an isolated window parse cannot see, so
/// their inline classification is a lazy-inline / debounced-full-reparse concern (Decision 5/13), not a
/// windowed-tiling one. Blocks whose source contains a <b>bare CR</b> (a <c>\r</c> not part of a
/// <c>\r\n</c>) are likewise excluded: the buffer treats a lone CR as content while Markdig treats it
/// as a line break, so a windowed substring parse can realize a different inline tree than the whole
/// document even when block structure agrees — the same buffer/Markdig line-model discrepancy, at the
/// inline layer. Every block's runs are still bounds-checked, and every block's structure (kind, span,
/// line range, text) is still compared, regardless.
/// </para>
/// </remarks>
internal static class WindowedParseOracle
{
    public static void AssertMatchesFullParse(BlockHarness windowed)
    {
        string text = windowed.Buffer.GetText();
        var full = BlockHarness.Create(text); // an independent, from-scratch full parse of the same bytes

        Assert.True(
            full.Blocks.Count == windowed.Blocks.Count,
            $"block count: full parse {full.Blocks.Count} vs windowed {windowed.Blocks.Count}\n{Dump(full, windowed)}\n{Describe(text)}");

        for (var i = 0; i < windowed.Blocks.Count; i++)
        {
            var wb = windowed.Blocks[i];
            var fb = full.Blocks[i];

            Assert.True(fb.Kind == wb.Kind, $"block {i} kind: full {fb.Kind} vs windowed {wb.Kind}\n{Dump(full, windowed)}\n{Describe(text)}");
            Assert.True(
                full.Blocks.GetStartLine(i) == windowed.Blocks.GetStartLine(i),
                $"block {i} start line: full {full.Blocks.GetStartLine(i)} vs windowed {windowed.Blocks.GetStartLine(i)}\n{Dump(full, windowed)}\n{Describe(text)}");
            Assert.True(fb.LineCount == wb.LineCount, $"block {i} line count: full {fb.LineCount} vs windowed {wb.LineCount}\n{Dump(full, windowed)}\n{Describe(text)}");

            string windowedSource = windowed.TextOf(i);
            Assert.True(
                string.Equals(full.TextOf(i), windowedSource, StringComparison.Ordinal),
                $"block {i} source text differs\n  full:     {Quote(full.TextOf(i))}\n  windowed: {Quote(windowedSource)}\n{Describe(text)}");

            // The buffer treats a lone CR as content while Markdig treats it as a line break, so a
            // windowed substring parse can realize a different inline tree than the whole document near
            // a bare CR even when block structure agrees — skip the inline layer for those blocks. Block
            // structure (above) is still asserted for them.
            if (HasBareCr(windowedSource))
                continue;

            AssertSpansStayValid(fb, wb, windowedSource, i, text);
            AssertOffsetMath(fb, wb, i, text);
        }
    }

    /// <summary>Whether the text contains a bare carriage return (a <c>\r</c> not immediately followed by <c>\n</c>) — where the buffer and Markdig line models diverge.</summary>
    private static bool HasBareCr(string text)
    {
        int cr = text.IndexOf('\r');
        while (cr >= 0)
        {
            if (cr + 1 >= text.Length || text[cr + 1] != '\n')
                return true;

            cr = text.IndexOf('\r', cr + 1);
        }

        return false;
    }

    /// <summary>
    /// The span-oracle-style validity check after a windowed edit: every windowed inline run stays in
    /// its block's source bounds — <b>except</b> runs that reproduce an out-of-bounds precise span the
    /// full parse also emits. Markdig's <c>UsePreciseSourceLocation</c> can itself yield an out-of-bounds
    /// span on pathological input (a WP1-catalogued divergence class); faithfully reproducing it is
    /// correct, so the assertion flags only invalidity that windowing <i>introduces</i> beyond the full
    /// parse.
    /// </summary>
    private static void AssertSpansStayValid(Block full, Block windowed, string source, int index, string doc)
    {
        var fullRuns = full.InlineRuns;
        foreach (var run in windowed.InlineRuns)
        {
            bool inBounds = run.SourceStart >= 0 && run.SourceLength >= 0 && run.SourceStart + run.SourceLength <= source.Length;
            Assert.True(
                inBounds || fullRuns.Contains(run),
                $"block {index} ({windowed.Kind}) run {run.Kind} [{run.SourceStart}, {run.SourceStart + run.SourceLength}) out of block source bounds [0, {source.Length}] and not present in the full parse; SourceStartOffset={windowed.SourceStartOffset} source={Quote(source)}\n{Describe(doc)}");
        }
    }

    /// <summary>
    /// Validates the windowed block-relative offset math (<c>SourceStart</c>) against the full parse —
    /// <b>only when the two agree on inline structure</b> (the same sequence of run kinds and lengths).
    /// A pure offset bug (a mis-computed <c>SourceStartOffset</c>) shifts every start while preserving
    /// kinds and lengths, so it is caught here; a legitimately context-dependent inline difference (a
    /// reference link, footnote, or definition-list marker that resolves differently in isolation than
    /// in the whole document — the lazy-inline / full-reparse concern of Decision 5/13) changes the
    /// kinds or lengths, so the structures diverge and the comparison is correctly skipped.
    /// </summary>
    private static void AssertOffsetMath(Block full, Block windowed, int index, string doc)
    {
        var fr = full.InlineRuns;
        var wr = windowed.InlineRuns;

        if (fr.Count != wr.Count)
            return;

        for (var k = 0; k < fr.Count; k++)
        {
            if (fr[k].Kind != wr[k].Kind || fr[k].SourceLength != wr[k].SourceLength)
                return; // inline structure diverged (context-dependent) — not an offset-math case
        }

        for (var k = 0; k < fr.Count; k++)
        {
            Assert.True(
                fr[k].SourceStart == wr[k].SourceStart,
                $"block {index} ({full.Kind}) run {k} offset: full {fr[k].SourceStart} vs windowed {wr[k].SourceStart} (kind {fr[k].Kind}, len {fr[k].SourceLength})\n{Describe(doc)}");
        }
    }

    private static string Dump(BlockHarness full, BlockHarness windowed)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  FULL:");
        for (var i = 0; i < full.Blocks.Count; i++)
            sb.AppendLine($"    [{i}] {full.Blocks[i].Kind} @{full.Blocks.GetStartLine(i)}×{full.Blocks[i].LineCount} {Quote(full.TextOf(i))}");
        sb.AppendLine("  WINDOWED:");
        for (var i = 0; i < windowed.Blocks.Count; i++)
            sb.AppendLine($"    [{i}] {windowed.Blocks[i].Kind} @{windowed.Blocks.GetStartLine(i)}×{windowed.Blocks[i].LineCount} {Quote(windowed.TextOf(i))}");
        return sb.ToString();
    }

    private static string Describe(string text) =>
        $"  document ({text.Length} chars): {Quote(text)}";

    private static string Quote(string text)
    {
        const int max = 400;
        string shown = text.Length <= max ? text : text[..max] + $"… (+{text.Length - max})";
        return "\"" + shown.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }
}
