using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Incremental;

/// <summary>
/// M2.WP4 — the differential fuzzer, the R2 correctness spine (architecture Decision 14a). Random
/// documents × seeded edit scripts drive the <b>windowed</b> incremental producer; after <i>every</i>
/// edit it asserts (a) the windowed <see cref="BlockList"/> is byte-identical to a from-scratch full
/// parse of the same buffer — kinds, precise spans, line ranges, source text
/// (<see cref="WindowedParseOracle"/>) — and (b) block identity is stable for untouched blocks and the
/// Reused/Changed/Added/Removed partition is exactly right (<see cref="BlockDiffOracle"/>). Every
/// realized inline run is bounds-checked (the span-oracle-style validity assertion after a windowed
/// edit). A divergence prints the seed, step, and document to replay from.
/// </summary>
/// <remarks>
/// Fast-path-eligible edits and deletion edits are mandatory generators, biased toward the classes the
/// plan's Risks name: container joins, setext flips, fence toggles, definition edits, and CRLF seams.
/// If the fuzzer ever finds a divergence, the fix is to widen the window rule
/// (<c>ReparseWindowPlanner</c>/<c>FastPathGate</c>) — never to weaken this oracle.
/// </remarks>
public sealed class DifferentialFuzzTests
{
    private static readonly string[] StartingDocuments =
    [
        "alpha\n\nbeta\n\ngamma",
        "# Title\n\nA paragraph with *emphasis* and `code`.\n\n## Section\n\n- one\n- two\n\n> a quote",
        "```\ncode block\nmore code\n```\n\nafter the fence",
        "| a | b |\n| - | - |\n| 1 | 2 |\n\ntrailing text",
        "---\ntitle: doc\n---\n\nbody paragraph\n\nsecond paragraph",
        "line\n\n---\n\nrule above and below\n\n---\n\nend",
        "text with a [ref][label] and a [^fn].\n\n[label]: https://example.com\n\n[^fn]: the footnote body.",
        "> quote one\n> still quote\n\n> quote two\n\n1. first\n2. second\n3. third",
        "para with CRLF\r\nsecond line\r\n\r\nthird paragraph\r\nwith a line\r\n",
        "$$\nx = y + z\n$$\n\ninline $a+b$ math\n\n> [!NOTE]\n> a callout",
    ];

    // Structural fragments — boundary characters over-represented on purpose (window-context poisoning).
    private static readonly string[] StructuralFragments =
    [
        "\n", "\n\n", "#", "# ", "## ", "```", "```\n", ">", "> ", "- ", "* ", "+ ", "1. ",
        "|", " | ", "---", "===", "$$", "[^2]", ": def", "\n---\n", "\n```\n", "\r\n", "\r",
        "    ", "\t", "[x]", "[label2]: /url", "[^2]: note", "\n> ", "\n    indented",
    ];

    [Fact]
    public void WindowedEqualsFull()
    {
        // Per-PR lane: 10k seeded steps by default (plan §5.2/§5.7); the nightly 100k path is the
        // Integration-tagged sibling below.
        RunFuzz(OpsFor(defaultOps: 10_000), label: "per-PR");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void WindowedEqualsFull_Nightly()
    {
        // Nightly-integration lane: 100k steps by default, overridable via the environment.
        RunFuzz(OpsFor(defaultOps: 100_000), label: "nightly");
    }

    private static int OpsFor(int defaultOps) =>
        int.TryParse(Environment.GetEnvironmentVariable("CURSORIALEDIT_FUZZ_OPS"), out int ops) && ops > 0
            ? ops
            : defaultOps;

    private static void RunFuzz(int totalOps, string label)
    {
        const int editsPerRun = 250;
        int runs = Math.Max(1, (totalOps + editsPerRun - 1) / editsPerRun);
        int maxVerified = 0;

        for (var run = 0; run < runs; run++)
        {
            int seed = 5000 + run;
            var rng = new Random(seed);
            var h = BlockHarness.Create(StartingDocuments[run % StartingDocuments.Length]);

            for (var step = 0; step < editsPerRun; step++)
            {
                var before = h.Snapshot();
                BlockListChange change;
                try
                {
                    change = ApplyRandomEdit(h, rng);
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException($"[{label}] edit threw at seed {seed}, step {step}: {ex}");
                }

                try
                {
                    BlockDiffOracle.Verify(h, before, change);
                    WindowedParseOracle.AssertMatchesFullParse(h);
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"[{label}] divergence at seed {seed}, step {step}:\n{ex.Message}");
                }

                maxVerified++;
            }
        }

        Assert.True(maxVerified >= totalOps || maxVerified >= runs * editsPerRun);
    }

    internal static string StartingDocForRun(int run) => StartingDocuments[run % StartingDocuments.Length];

    internal static string ApplyRandomEditPublic(BlockHarness h, Random rng)
    {
        int lenBefore = h.Buffer.Length;
        ApplyRandomEdit(h, rng);
        return $"len {lenBefore} -> {h.Buffer.Length}";
    }

    internal static BlockHarness Replay(int run, int steps)
    {
        var rng = new Random(5000 + run);
        var h = BlockHarness.Create(StartingDocuments[run % StartingDocuments.Length]);
        for (var s = 0; s < steps; s++)
            ApplyRandomEdit(h, rng);
        return h;
    }

    private static BlockListChange ApplyRandomEdit(BlockHarness h, Random rng)
    {
        // Keep documents bounded: bias hard toward deletion once the buffer grows large.
        bool large = h.Buffer.Length > 4_000;
        int roll = rng.Next(100);

        if (large ? roll < 55 : roll < 30)
        {
            var deletion = TryDelete(h, rng);
            if (deletion is { } d)
                return d;
        }

        // Fast-path-eligible mid-word letter edit (mandatory generator).
        if (roll is >= 30 and < 50)
        {
            var fast = TryFastPathLetterEdit(h, rng);
            if (fast is { } f)
                return f;
        }

        // Structural fragment insertion — the window-poisoning classes.
        int line = rng.Next(h.Buffer.LineCount);
        int textLength = h.Buffer.GetLine(line).Text.Length;
        int col = textLength == 0 ? 0 : rng.Next(textLength + 1);
        var start = new TextPosition(line, col);

        string fragment = StructuralFragments[rng.Next(StructuralFragments.Length)];
        var kind = fragment.Contains('\n') && fragment.Length > 2 ? EditKind.Paste : EditKind.Typing;
        return h.Insert(start, fragment, kind);
    }

    private static BlockListChange? TryDelete(BlockHarness h, Random rng)
    {
        if (h.Buffer.Length == 0)
            return null;

        int startOffset = rng.Next(h.Buffer.Length);
        var startPos = h.Buffer.GetPosition(startOffset);
        startOffset = h.Buffer.GetOffset(startPos); // snap off any CRLF interior
        int maxRemove = Math.Min(1 + rng.Next(8), h.Buffer.Length - startOffset);
        if (maxRemove <= 0)
            return null;

        string removed = h.Buffer.GetTextAtOffset(startOffset, maxRemove);
        return h.Apply(startPos, removed, "", rng.Next(2) == 0 ? EditKind.Typing : EditKind.Structural);
    }

    private static BlockListChange? TryFastPathLetterEdit(BlockHarness h, Random rng)
    {
        string text = h.Buffer.GetText();

        // Insert a letter strictly between two letters (before/after both letters ⇒ the fast-path gate
        // admits it), or delete such an interior letter.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            int i = 1 + rng.Next(Math.Max(1, text.Length - 1));
            if (i <= 0 || i >= text.Length)
                continue;

            if (char.IsLetter(text[i - 1]) && char.IsLetter(text[i]))
            {
                var pos = h.Buffer.GetPosition(i);
                if (h.Buffer.GetOffset(pos) != i)
                    continue; // landed in a CRLF interior — skip

                if (rng.Next(2) == 0)
                    return h.Insert(pos, "q");

                // Delete this interior letter (before is a letter, after is text[i+1]).
                if (i + 1 < text.Length && char.IsLetter(text[i + 1]))
                    return h.Apply(pos, text[i].ToString(), "", EditKind.Typing);
            }
        }

        return null;
    }
}
