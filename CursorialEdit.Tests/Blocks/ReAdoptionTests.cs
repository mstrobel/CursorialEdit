using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Tests.Blocks;

/// <summary>
/// M2.WP2 gate — the re-adoption rule (architecture Decision 4). The block being edited keeps its
/// <see cref="BlockId"/> across every keystroke while its siblings keep theirs; blocks re-adopt
/// correctly under insert, delete, split, merge, and paste; and the nasty structural cases the plan's
/// Risks name (setext flips, fence-parity boundaries) classify the way the chosen policy documents.
/// </summary>
public sealed class ReAdoptionTests
{
    // ───────────────────────────── the headline gate ─────────────────────────────

    [Fact]
    public void EditedBlockKeepsBlockIdEveryKeystroke()
    {
        var h = BlockHarness.Create("alpha\n\nbeta\n\ngamma");
        var ids = h.Ids();
        Assert.Equal(3, ids.Length);
        BlockId edited = ids[1];

        // Type "X!?#" into the middle paragraph, one character at a time.
        int col = 4; // end of "beta"
        foreach (char ch in "X!?#")
        {
            var change = h.Insert(new TextPosition(2, col), ch.ToString());
            col++;

            // The edited block keeps its id every keystroke; the siblings keep theirs untouched.
            Assert.Equal(edited, h.Blocks[1].Id);
            Assert.Equal(ids[0], h.Blocks[0].Id);
            Assert.Equal(ids[2], h.Blocks[2].Id);

            // The change reports exactly the middle block as Changed, the siblings as Reused.
            Assert.Equal([edited], change.Changed);
            Assert.Equal([ids[0], ids[2]], change.Reused);
            Assert.Empty(change.Added);
            Assert.Empty(change.Removed);
            Assert.Equal(0, change.LineShift);
        }

        Assert.Equal("betaX!?#", h.Buffer.GetLine(2).Text);
    }

    [Fact]
    public void EditedBlockKeepsBlockId_AcrossManyBlocks_AndKindsOfNeighbors()
    {
        var h = BlockHarness.Create("# Title\n\nfirst para\n\n> a quote\n\n- item one\n- item two\n\nlast para");
        var ids = h.Ids();
        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph, BlockKind.Quote, BlockKind.List, BlockKind.Paragraph], h.Kinds());

        // Edit the list block (line 6) repeatedly; every other block keeps its identity.
        BlockId listId = ids[3];
        for (var i = 0; i < 6; i++)
        {
            h.Insert(new TextPosition(6, 9), "s");
            Assert.Equal(ids, h.Ids()); // full id vector unchanged
            Assert.Equal(listId, h.Blocks[3].Id);
        }
    }

    // ───────────────────────────── insert / delete / split / merge ─────────────────────────────

    [Fact]
    public void InsertNewParagraph_AddsOneBlock_SiblingsKeepIds()
    {
        var h = BlockHarness.Create("alpha\n\nbeta");
        var ids = h.Ids();
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(2, 4), "\n\ngamma", EditKind.Paste); // beta -> "beta\n\ngamma" ? no: at end of beta

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(ids[0], h.Blocks[0].Id); // alpha untouched
        Assert.Contains(ids[1], h.Ids());      // beta re-adopted
        Assert.Single(change.Added);           // the new gamma paragraph
    }

    [Fact]
    public void DeleteWholeBlock_RemovesIt_SiblingsKeepIds()
    {
        var h = BlockHarness.Create("alpha\n\nbeta\n\ngamma");
        var ids = h.Ids();
        var before = h.Snapshot();

        // Delete "beta" and one blank separator: lines 2..3 (offsets computed via the buffer).
        int from = h.Buffer.GetOffset(new TextPosition(1, 0));
        int to = h.Buffer.GetOffset(new TextPosition(3, 0));
        var change = h.Apply(new TextPosition(1, 0), h.Buffer.GetTextAtOffset(from, to - from), "", EditKind.Structural);

        BlockDiffOracle.Verify(h, before, change);
        Assert.Contains(ids[1], change.Removed);           // beta removed
        Assert.Contains(ids[0], h.Ids());                  // alpha kept
        Assert.Contains(ids[2], h.Ids());                  // gamma kept
    }

    [Fact]
    public void SplitParagraph_KeepsOneHalfId_AddsTheOther()
    {
        var h = BlockHarness.Create("one two three\n\ntail");
        var ids = h.Ids();
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(0, 7), "\n\n"); // "one two\n\n three" — a real split

        BlockDiffOracle.Verify(h, before, change);
        // The paragraph's id survives on the half that keeps its first-unmodified line (the original
        // trailing blank drifts to the second half under trailing-blank attachment, so the second half
        // is the one that re-adopts — either way the id is preserved, not churned, and one half is new.
        Assert.Contains(ids[0], h.Ids());
        Assert.Single(change.Added);           // the other half is new
        Assert.Contains(ids[1], h.Ids());      // tail re-adopted (shifted)
        Assert.Equal(2, change.LineShift);
    }

    [Fact]
    public void MergeParagraphs_KeepsFirstId_RemovesSecond()
    {
        var h = BlockHarness.Create("alpha\n\nbeta\n\ngamma");
        var ids = h.Ids();
        var before = h.Snapshot();

        // Delete the blank separator between alpha and beta -> one paragraph.
        var change = h.Apply(new TextPosition(1, 0), "\n", "", EditKind.Typing);

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(ids[0], h.Blocks[0].Id);   // merged block keeps alpha's id
        Assert.Contains(ids[1], change.Removed); // beta swallowed
        Assert.Contains(ids[2], h.Ids());        // gamma kept
    }

    [Fact]
    public void PasteMultiBlock_KeepsSurroundingIds()
    {
        var h = BlockHarness.Create("head\n\ntail");
        var ids = h.Ids();
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(0, 4), "\n\n# Injected\n\nmiddle", EditKind.Paste);

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(ids[0], h.Blocks[0].Id);        // head keeps its id
        Assert.Contains(ids[1], h.Ids());            // tail re-adopted
        Assert.Contains(BlockKind.Heading, h.Kinds());
    }

    [Fact]
    public void MovedButUnchangedBlock_ReAdoptsViaContentHash()
    {
        // Paste a copy of an existing block above it: the paste rewrites every line (no unmodified
        // line to anchor), so the secondary content-hash check re-adopts the moved-but-identical
        // block rather than churning ids across the whole tail.
        var h = BlockHarness.Create("keep me\n\n> a distinctive quote\n\ntail");
        var ids = h.Ids();
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(0, 7), "\n\nbrand new para", EditKind.Paste);

        BlockDiffOracle.Verify(h, before, change);
        Assert.Contains(ids[1], h.Ids()); // the quote block kept its id despite shifting down
        Assert.Contains(ids[2], h.Ids()); // tail kept its id
    }

    // ───────────────────────────── setext flip (kind change) ─────────────────────────────

    [Fact]
    public void SetextUnderline_FlipsParagraphToHeading_NewId_SiblingsStable()
    {
        var h = BlockHarness.Create("alpha\n\nHeading\n\ngamma");
        var ids = h.Ids();
        var before = h.Snapshot();

        // Type "\n===" after "Heading" so the paragraph becomes a setext H1.
        var change = h.Insert(new TextPosition(2, 7), "\n===");

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(BlockKind.Heading, h.Blocks[1].Kind);
        Assert.Equal(1, h.Blocks[1].HeadingLevel);

        // Kind flip -> the old paragraph id is Removed, the heading gets a fresh id (documented policy).
        Assert.Contains(ids[1], change.Removed);
        Assert.DoesNotContain(ids[1], h.Ids());
        Assert.Single(change.Added);
        Assert.Equal(change.Added[0], h.Blocks[1].Id);

        // Siblings survive the flip.
        Assert.Equal(ids[0], h.Blocks[0].Id);
        Assert.Contains(ids[2], h.Ids());
    }

    [Fact]
    public void RemovingSetextUnderline_FlipsHeadingBackToParagraph()
    {
        var h = BlockHarness.Create("alpha\n\nHeading\n===\n\ngamma");
        Assert.Equal(BlockKind.Heading, h.Blocks[1].Kind);
        var ids = h.Ids();
        var before = h.Snapshot();

        // Delete the "===\n" underline line -> back to a paragraph.
        var change = h.Apply(new TextPosition(3, 0), "===\n", "", EditKind.Typing);

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(BlockKind.Paragraph, h.Blocks[1].Kind);
        Assert.Contains(ids[1], change.Removed); // heading id retired on the kind flip
    }

    // ───────────────────────────── fence-parity boundary ─────────────────────────────

    [Fact]
    public void EditingInsideAFence_KeepsTheFenceBlockId()
    {
        var h = BlockHarness.Create("intro\n\n```js\nline();\n```\n\noutro");
        Assert.Equal([BlockKind.Paragraph, BlockKind.FencedCode, BlockKind.Paragraph], h.Kinds());
        var ids = h.Ids();
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(3, 7), " // note"); // type inside the fenced body

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(ids[1], h.Blocks[1].Id);   // fence keeps its id (still FencedCode)
        Assert.Equal([ids[1]], change.Changed);
        Assert.Equal([ids[0], ids[2]], change.Reused);
    }

    [Fact]
    public void OpeningUnclosedFence_SwallowsFollowingBlocks_TheyBecomeOneFence()
    {
        var h = BlockHarness.Create("first para\n\nsecond para\n\nthird para");
        var ids = h.Ids();
        var before = h.Snapshot();

        // Insert an opening fence at the very top; with no closing fence it runs to EOF, so every
        // paragraph is reinterpreted as one fenced code block.
        var change = h.Insert(new TextPosition(0, 0), "```\n");

        BlockDiffOracle.Verify(h, before, change);
        Assert.Single(h.Blocks);
        Assert.Equal(BlockKind.FencedCode, h.Blocks[0].Kind);
        // The poisoned paragraphs did not get falsely re-adopted as the fence (kinds differ).
        Assert.Contains(ids[1], change.Removed);
        Assert.Contains(ids[2], change.Removed);
    }

    [Fact]
    public void ClosingAFence_SplitsTailBackOut()
    {
        var h = BlockHarness.Create("```\ncode\n\nstill code\n\nmore code");
        Assert.Single(h.Blocks); // one unterminated fence swallowing everything
        var before = h.Snapshot();

        // Close the fence after "code": the tail becomes real paragraphs again.
        var change = h.Insert(new TextPosition(1, 4), "\n```");

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(BlockKind.FencedCode, h.Blocks[0].Kind);
        Assert.Contains(h.Kinds(), k => k == BlockKind.Paragraph);
    }

    // ───────────────────────────── undo replays through the same seam ─────────────────────────────

    [Fact]
    public void Undo_RestoresBlockStructure_ThroughTheSamePipeline()
    {
        var h = BlockHarness.Create("alpha\n\nbeta");
        var spansBefore = h.Spans();
        var kindsBefore = h.Kinds();

        h.Apply(new TextPosition(1, 0), "\n", "", EditKind.Typing); // merge
        Assert.Single(h.Blocks);

        var before = h.Snapshot();
        h.Controller.Undo();

        Assert.Equal(spansBefore, h.Spans());
        Assert.Equal(kindsBefore, h.Kinds());
        BlockDiffOracle.Verify(h, before, h.LastChange!);
    }

    // ───────────────────────────── identity migration (review) ─────────────────────────────

    /// <summary>
    /// Review finding: content-hash must never hand an unchanged block's id to a byte-identical twin.
    /// Pasting an identical copy of a block ABOVE it leaves the untouched ORIGINAL (which still has its
    /// unmodified lines) holding the id; the pasted copy — all lines new — is a fresh Added block.
    /// </summary>
    [Fact]
    public void PastingIdenticalTwin_LeavesTheUntouchedOriginalHoldingItsId()
    {
        var h = BlockHarness.Create("X\n\nZ");
        var ids = h.Ids(); // [X-id, Z-id]
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(0, 0), "X\n\n", EditKind.Paste); // identical copy at top

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal([BlockKind.Paragraph, BlockKind.Paragraph, BlockKind.Paragraph], h.Kinds());
        Assert.Equal(ids[0], h.Blocks[1].Id);   // the ORIGINAL X (shifted to index 1) kept its id
        Assert.Single(change.Added);             // the pasted copy is the only new block
        Assert.Equal(change.Added[0], h.Blocks[0].Id); // ...and it is the top copy, not the original
        Assert.Contains(ids[1], h.Ids());        // Z kept its id
    }

    /// <summary>
    /// Review finding (the mirror artifact): a segment of purely-new content whose last line merged
    /// with an old line's terminator during the splice (so that line inherited the old version stamp)
    /// must not anchor-steal an EARLIER block's id. Inserting new blocks after "head" leaves head's id
    /// on head, never on the trailing new "middle" paragraph.
    /// </summary>
    [Fact]
    public void NewContentMergedWithAnOldTerminator_DoesNotStealAnEarlierBlockId()
    {
        var h = BlockHarness.Create("head\n\ntail");
        var ids = h.Ids(); // [head-id, tail-id]
        var before = h.Snapshot();

        var change = h.Insert(new TextPosition(0, 4), "\n\n# Injected\n\nmiddle", EditKind.Paste);

        BlockDiffOracle.Verify(h, before, change);
        Assert.Equal(ids[0], h.Blocks[0].Id);       // head kept its id (not migrated to "middle")
        Assert.DoesNotContain(ids[0], change.Added); // head's id is not reported as newly added
        Assert.Contains(ids[1], h.Ids());            // tail kept its id
        // The injected heading + middle paragraph are both fresh ids.
        Assert.Equal(2, change.Added.Count);
        Assert.Contains(change.Added, id => id == h.Blocks[1].Id); // heading
        Assert.Contains(change.Added, id => id == h.Blocks[2].Id); // middle
    }
}
