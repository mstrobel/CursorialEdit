using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Tests.Blocks;

/// <summary>
/// The naive full-diff oracle for <see cref="BlockListChange"/> (implementation-plan §7 WP2 gate):
/// an independent, obviously-correct classifier that derives the expected Reused/Changed/Added/
/// Removed/LineShift from a plain before/after block comparison and asserts the producer's emitted
/// change agrees. It never consults the producer's matching internals — only block ids, kinds,
/// source texts, and per-line versions — so a re-adoption bug (a churned id, a mis-classified block)
/// shows up as a mismatch here.
/// </summary>
/// <remarks>
/// <b>Reference semantics.</b> A block present in both snapshots is <b>Reused</b> iff its kind and
/// exact source text are unchanged <i>and</i> none of its current lines were touched by the edit
/// (every line's <see cref="Line.Version"/> is below the buffer's current version); otherwise it is
/// <b>Changed</b>. New ids are <b>Added</b>, vanished ids are <b>Removed</b>. Independently, the
/// oracle enforces the non-churn floor: any old block lying entirely outside the edit's dirty line
/// range must keep its id (the prefix/suffix-trimming guarantee), so the producer cannot "solve" the
/// diff by minting fresh ids everywhere.
/// </remarks>
internal static class BlockDiffOracle
{
    public static void Verify(BlockHarness harness, IReadOnlyList<BlockSnapshot> before, BlockListChange change)
    {
        var buffer = harness.Buffer;
        var blocks = harness.Blocks;
        int currentVersion = buffer.CurrentVersion;

        var after = new Dictionary<BlockId, (BlockKind Kind, string Text, int LineCount, bool AllUnmodified)>();
        for (var i = 0; i < blocks.Count; i++)
        {
            int start = blocks.GetStartLine(i);
            int count = blocks[i].LineCount;
            bool allUnmodified = true;
            for (int line = start; line < start + count; line++)
            {
                if (buffer.GetLine(line).Version >= currentVersion)
                {
                    allUnmodified = false;
                    break;
                }
            }

            after[blocks[i].Id] = (blocks[i].Kind, harness.TextOf(i), count, allUnmodified);
        }

        var beforeMap = before.ToDictionary(b => b.Id, b => (b.Kind, b.Text, b.LineCount));
        var beforeIds = beforeMap.Keys.ToHashSet();
        var afterIds = after.Keys.ToHashSet();

        var expectedAdded = afterIds.Except(beforeIds).ToHashSet();
        var expectedRemoved = beforeIds.Except(afterIds).ToHashSet();
        var expectedReused = new HashSet<BlockId>();
        var expectedChanged = new HashSet<BlockId>();
        foreach (var id in beforeIds.Intersect(afterIds))
        {
            var b = beforeMap[id];
            var a = after[id];
            // Line count is part of "unchanged": a block can be byte-identical yet gain/lose a trailing
            // empty line (which serializes to nothing) — a height change the view must re-layout.
            bool unchanged = b.Kind == a.Kind && b.LineCount == a.LineCount
                && string.Equals(b.Text, a.Text, StringComparison.Ordinal) && a.AllUnmodified;
            (unchanged ? expectedReused : expectedChanged).Add(id);
        }

        AssertBucket(expectedAdded, change.Added, nameof(change.Added));
        AssertBucket(expectedRemoved, change.Removed, nameof(change.Removed));
        AssertBucket(expectedReused, change.Reused, nameof(change.Reused));
        AssertBucket(expectedChanged, change.Changed, nameof(change.Changed));

        // The four buckets partition: no id twice, and Reused ∪ Changed ∪ Added == the new id set.
        var live = change.Reused.Concat(change.Changed).Concat(change.Added).ToList();
        Assert.Equal(live.Count, live.Distinct().Count());
        Assert.Equal(afterIds, live.ToHashSet());

        // Line shift and tiling.
        int beforeTotalLines = before.Count == 0 ? 0 : before[^1].StartLine + before[^1].LineCount;
        Assert.Equal(blocks.TotalLineCount - beforeTotalLines, change.LineShift);
        Assert.Equal(buffer.LineCount, blocks.TotalLineCount);
        Assert.Equal(change.Epoch, buffer.Epoch);

        // Non-churn floor. An old block wholly outside the edit's dirty line range whose exact
        // (kind, text) still exists in the new list genuinely survived the edit unchanged, so it MUST
        // keep its id — the producer cannot "solve" the diff by minting fresh ids for unchanged
        // blocks. Blocks whose signature vanished (a fence/setext flip that reinterpreted the tail)
        // are legitimately retired and excluded; a duplicated signature is skipped to avoid the
        // (unchanged-block × identical-twin) ambiguity.
        var afterSignatures = after.Values.Select(v => (v.Kind, v.Text)).ToHashSet();
        var oldSignatureCounts = new Dictionary<(BlockKind, string), int>();
        foreach (var b in before)
            oldSignatureCounts[(b.Kind, b.Text)] = oldSignatureCounts.GetValueOrDefault((b.Kind, b.Text)) + 1;

        var splice = harness.LastSplice;
        int dirtyStart = buffer.GetPosition(splice.StartOffset).Line;
        int oldDirtyEnd = dirtyStart + CountLineBreaks(splice.RemovedText);
        foreach (var b in before)
        {
            bool whollyOutside = b.StartLine + b.LineCount <= dirtyStart || b.StartLine > oldDirtyEnd;
            var signature = (b.Kind, b.Text);
            if (whollyOutside && oldSignatureCounts[signature] == 1 && afterSignatures.Contains(signature))
                Assert.True(afterIds.Contains(b.Id), $"Block {b.Id} ({b.Kind}) survived unchanged but lost its id.");
        }
    }

    private static void AssertBucket(HashSet<BlockId> expected, IReadOnlyList<BlockId> actual, string name)
    {
        Assert.True(expected.SetEquals(actual),
            $"{name}: expected {{{string.Join(", ", expected)}}} but producer reported {{{string.Join(", ", actual)}}}.");
        Assert.Equal(expected.Count, actual.Count); // no duplicates within the bucket
    }

    private static int CountLineBreaks(string text)
    {
        var breaks = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                breaks++;
        }

        return breaks;
    }
}
