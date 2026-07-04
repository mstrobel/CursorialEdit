using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Tests.Blocks;

/// <summary>
/// M2.WP2 — <see cref="Document.Model.BlockListChange"/> correctness against the naive full-diff
/// oracle (<see cref="BlockDiffOracle"/>). A seeded stream of edits — biased toward the structural
/// characters that trigger container joins, setext flips, and fence parity (the plan's window-context
/// poisoning risk) — is applied to a set of starting documents, and after every single edit the
/// producer's emitted change is checked against the independent classifier. Failures replay from the
/// seed printed in the assertion.
/// </summary>
public sealed class BlockListChangeOracleTests
{
    private static readonly string[] StartingDocuments =
    [
        "alpha\n\nbeta\n\ngamma",
        "# Title\n\nA paragraph with *emphasis* and `code`.\n\n## Section\n\n- one\n- two\n\n> a quote",
        "```\ncode block\nmore code\n```\n\nafter the fence",
        "| a | b |\n| - | - |\n| 1 | 2 |\n\ntrailing text",
        "---\ntitle: doc\n---\n\nbody paragraph\n\nsecond paragraph",
        "line\n\n---\n\nrule above and below\n\n---\n\nend",
    ];

    // The palette the fuzzer inserts/removes — structural characters over-represented on purpose.
    private static readonly string[] Fragments =
    [
        "x", " ", "word ", "\n", "\n\n", "#", "# ", "## ", "```", "```\n", ">", "> ", "- ", "1. ",
        "|", "---", "===", "$$", "text", "[^1]", ": def", "\n---\n", "\n```\n",
    ];

    [Fact]
    public void WindowedChange_MatchesNaiveDiffOracle_OverSeededEditStream()
    {
        const int editsPerDocument = 1500;

        for (var docIndex = 0; docIndex < StartingDocuments.Length; docIndex++)
        {
            var rng = new Random(1000 + docIndex);
            var h = BlockHarness.Create(StartingDocuments[docIndex]);

            for (var step = 0; step < editsPerDocument; step++)
            {
                var before = h.Snapshot();
                BlockListChange change;
                try
                {
                    change = ApplyRandomEdit(h, rng);
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"edit threw at doc {docIndex}, step {step}: {ex.Message}");
                }

                try
                {
                    BlockDiffOracle.Verify(h, before, change);
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"oracle divergence at doc {docIndex}, step {step} (seed {1000 + docIndex}):\n{ex.Message}");
                }
            }
        }
    }

    private static BlockListChange ApplyRandomEdit(BlockHarness h, Random rng)
    {
        int lineCount = h.Buffer.LineCount;
        int line = rng.Next(lineCount);
        int textLength = h.Buffer.GetLine(line).Text.Length;
        int col = textLength == 0 ? 0 : rng.Next(textLength + 1);
        var start = new TextPosition(line, col);

        // 60% insert, 40% delete (when there is something to remove).
        if (rng.Next(100) < 60 || h.Buffer.Length == 0)
        {
            string fragment = Fragments[rng.Next(Fragments.Length)];
            var kind = fragment.Contains('\n') && fragment.Length > 2 ? EditKind.Paste : EditKind.Typing;
            return h.Insert(start, fragment, kind);
        }

        int startOffset = h.Buffer.GetOffset(start);
        int maxRemove = Math.Min(6, h.Buffer.Length - startOffset);
        if (maxRemove <= 0)
            return h.Insert(start, "z");

        int removeLength = 1 + rng.Next(maxRemove);
        string removed = h.Buffer.GetTextAtOffset(startOffset, removeLength);
        return h.Apply(start, removed, "", EditKind.Typing);
    }
}
