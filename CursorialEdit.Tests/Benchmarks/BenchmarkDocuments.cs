using CursorialEdit.Tests.Layout;

namespace CursorialEdit.Tests.Benchmarks;

/// <summary>
/// The pinned 10k-line mixed-content document the M1 exit-gate benchmarks run against
/// (implementation-plan §6: typing latency + composite-slide scroll are measured "in a 10k-line
/// doc"). Deterministic by construction — no randomness — so every run measures the same
/// document: blank-line-separated paragraphs of varied sizes, mostly ASCII prose of varied
/// lengths, with the shared <see cref="NavigationFixtures"/> CJK/emoji/ZWJ lines interleaved so
/// the wrap/measure path pays real grapheme costs. Every line stays well under
/// <see cref="MaxLineCells"/> cells, so at any benchmark width of at least that many content
/// columns <b>one line is exactly one wrap row</b> — content row <i>n</i> is buffer line
/// <i>n</i>, which the benchmarks lean on for caret placement and scroll-position sanity checks.
/// </summary>
internal static class BenchmarkDocuments
{
    /// <summary>Total line count of the generated document (the plan's 10k-line reference shape).</summary>
    public const int LineCount = 10_000;

    /// <summary>
    /// The widest line the generator emits, in display cells (the CJK row fixture: 27 + space +
    /// six wide clusters = 40). Benchmarks size their terminal so content columns comfortably
    /// exceed this <i>plus</i> the cells a typing burst appends — nothing ever soft-wraps.
    /// </summary>
    public const int MaxLineCells = 40;

    /// <summary>Line lengths a typing burst may extend must start below this (see <see cref="Build"/>).</summary>
    private const int MaxTypingTargetLength = 40;

    /// <summary>The document's final line — short ASCII, so the document-end typing burst never wraps.</summary>
    private const string EndLine = "End of the benchmark document.";

    /// <summary>Content lines per paragraph, cycled — paragraphs of 1–6 lines, blank-separated.</summary>
    private static readonly int[] ParagraphSizes = [1, 4, 2, 6, 3, 2, 5, 1, 3, 4];

    /// <summary>ASCII prose of varied lengths (13–58 chars — all far below the wrap width).</summary>
    private static readonly string[] ProseLines =
    [
        "The quick brown fox jumps over the lazy dog.",
        "Sphinx of black quartz, judge my vow.",
        "A short line.",
        "Pack my box with five dozen liquor jugs, then rest well.",
        "How vexingly quick daft zebras jump!",
        "Editing ten thousand lines should feel instant, always.",
        "Another perfectly ordinary paragraph line follows here.",
        "Brisk keystrokes, one raster zone, sixteen milliseconds.",
    ];

    /// <summary>The shared grapheme fixtures, interleaved as whole lines (CJK, emoji, ZWJ, VS16).</summary>
    private static readonly string[] FixtureLines =
    [
        NavigationFixtures.ClusterFixture,
        NavigationFixtures.WordFixture,
        NavigationFixtures.EmojiWordFixture,
        NavigationFixtures.CjkRowFixture,
    ];

    /// <summary>
    /// Builds the document. <paramref name="midLine"/> is the first line at or past the midpoint
    /// that is short pure-ASCII prose (&lt; 40 chars) — the mid-document typing target, chosen so
    /// a 58-keystroke burst cannot push the line to the wrap width.
    /// </summary>
    public static string Build(out int midLine)
    {
        var lines = new List<string>(LineCount);
        var paragraph = 0;
        var prose = 0;

        while (lines.Count < LineCount - 2)
        {
            int size = ParagraphSizes[paragraph % ParagraphSizes.Length];
            for (var i = 0; i < size && lines.Count < LineCount - 2; i++)
                lines.Add(NextContentLine(paragraph, i, ref prose));

            if (lines.Count < LineCount - 2)
                lines.Add(string.Empty); // the blank paragraph separator (trailing attachment)

            paragraph++;
        }

        lines.Add(string.Empty);
        lines.Add(EndLine); // no trailing newline — the last line is real content

        midLine = -1;
        for (int i = LineCount / 2; i < LineCount; i++)
        {
            string line = lines[i];
            if (line.Length is > 0 and < MaxTypingTargetLength && line.All(char.IsAscii))
            {
                midLine = i;
                break;
            }
        }

        if (midLine < 0)
            throw new InvalidOperationException("no short ASCII typing target found past the midpoint — the generator changed shape");

        return string.Join('\n', lines);
    }

    /// <summary>Every 7th paragraph opens with a grapheme fixture line; everything else cycles the prose pool.</summary>
    private static string NextContentLine(int paragraph, int lineInParagraph, ref int prose)
        => paragraph % 7 == 3 && lineInParagraph == 0
            ? FixtureLines[paragraph / 7 % FixtureLines.Length]
            : ProseLines[prose++ % ProseLines.Length];
}
