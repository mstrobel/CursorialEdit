using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;

using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Pipeline;

/// <summary>
/// M1.WP7 — the degenerate <see cref="PlainTextBlockProducer"/>: blank-line-separated paragraph
/// segmentation (trailing-blank attachment, leading-blank-run block), the tiling invariant, and
/// the per-splice <see cref="BlockListChange"/> reconciliation contract — identity stability
/// outside the edit window (implicit shift), in-order re-formation inside it, and the split/merge
/// classifications the M1 gate names (split → Added + Reused-with-shift; merge → Removed).
/// </summary>
public sealed class PlainTextBlockProducerTests
{
    private static (DocumentBuffer Buffer, EditController Controller, PlainTextBlockProducer Producer) Create(string text)
    {
        var buffer = new DocumentBuffer(text);
        var controller = new EditController(buffer, new FakeTimeProvider());
        return (buffer, controller, new PlainTextBlockProducer(controller));
    }

    private static CaretState Caret(int line, int col) => new(new TextPosition(line, col));

    private static void Apply(EditController controller, TextPosition start, string removed, string inserted)
        => controller.Apply(new Edit(start, removed, inserted), EditKind.Typing, Caret(start.Line, start.Col), Caret(start.Line, start.Col));

    private static (int Start, int Count)[] Spans(BlockList blocks)
        => [.. Enumerable.Range(0, blocks.Count).Select(i => (blocks.GetStartLine(i), blocks[i].LineCount))];

    // ───────────────────────────── segmentation policy ─────────────────────────────

    [Fact]
    public void Segmentation_ParagraphOwnsItsTrailingBlankLines()
    {
        var (_, _, producer) = Create("aaa\nbbb\n\n\nccc\n\nddd");

        // [aaa,bbb,blank,blank] [ccc,blank] [ddd] — trailing blanks attach to the paragraph above.
        Assert.Equal([(0, 4), (4, 2), (6, 1)], Spans(producer.Blocks));
        Assert.All(producer.Blocks, block => Assert.Equal(BlockKind.Paragraph, block.Kind));
    }

    [Fact]
    public void Segmentation_LeadingBlankRun_FormsItsOwnBlock()
    {
        var (_, _, producer) = Create("\n\naaa");

        Assert.Equal([(0, 2), (2, 1)], Spans(producer.Blocks));
    }

    [Fact]
    public void Segmentation_WhitespaceOnlyLinesAreBlank()
    {
        var (_, _, producer) = Create("aaa\n \t \nbbb");

        Assert.Equal([(0, 2), (2, 1)], Spans(producer.Blocks));
    }

    [Fact]
    public void Segmentation_EmptyDocument_IsOneBlankBlock()
    {
        var (buffer, _, producer) = Create("");

        Assert.Equal([(0, 1)], Spans(producer.Blocks));
        Assert.Equal(buffer.LineCount, producer.Blocks.TotalLineCount);
    }

    [Fact]
    public void Segmentation_BlankOnlyDocument_IsOneBlock()
    {
        var (_, _, producer) = Create("\n\n\n");

        // Four lines (three blank + the empty unterminated last line), all one leading blank run.
        Assert.Equal([(0, 4)], Spans(producer.Blocks));
    }

    [Fact]
    public void BlockList_LookupByLine_AndStartLines()
    {
        var (_, _, producer) = Create("aaa\n\nbbb\nccc\n\nddd");
        var blocks = producer.Blocks;

        Assert.Equal(0, blocks.IndexOfLine(0));
        Assert.Equal(0, blocks.IndexOfLine(1));
        Assert.Equal(1, blocks.IndexOfLine(2));
        Assert.Equal(1, blocks.IndexOfLine(4));
        Assert.Equal(2, blocks.IndexOfLine(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => blocks.IndexOfLine(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => blocks.IndexOfLine(-1));

        Assert.Equal(6, blocks.TotalLineCount);
        Assert.Equal(6, blocks.GetStartLine(blocks.Count));
    }

    [Fact]
    public void BlockList_TypingSplice_MaintainsIdIndexWithoutFullRebuild()
    {
        // Review wave3-7: a same-count ReplaceRange (the typing path) must update the id index
        // incrementally — one warm rebuild total, no rebuild per keystroke.
        var (_, controller, producer) = Create(string.Join("\n\n", Enumerable.Range(0, 1000).Select(i => $"para {i}")));
        var blocks = producer.Blocks;
        Assert.Equal(1000, blocks.Count);

        var ids = blocks.Select(b => b.Id).ToArray();
        Assert.Equal(999, blocks.IndexOf(ids[999])); // warm the index (the one allowed rebuild)
        Assert.Equal(1, blocks.IndexRebuildCount);

        for (var keystroke = 0; keystroke < 5; keystroke++)
            Apply(controller, new TextPosition(1000, 6), "", "!"); // type inside paragraph 500

        Assert.Equal(500, blocks.IndexOf(ids[500]));
        Assert.Equal(999, blocks.IndexOf(ids[999]));
        Assert.Equal(1, blocks.IndexRebuildCount); // still the single warm-up rebuild
    }

    [Fact]
    public void BlockList_SameCountReplaceWithNewIds_KeepsTheIdIndexExact()
    {
        // The incremental path must evict replaced ids and land the new ones, not just re-point.
        var list = new BlockList();
        list.ReplaceRange(0, 0, [
            new Block(new BlockId(1), BlockKind.Paragraph, 1),
            new Block(new BlockId(2), BlockKind.Paragraph, 2),
            new Block(new BlockId(3), BlockKind.Paragraph, 1),
        ]);
        Assert.Equal(1, list.IndexOf(new BlockId(2))); // warm (count-changing init flagged a rebuild)

        list.ReplaceRange(1, 1, [new Block(new BlockId(9), BlockKind.Paragraph, 2)]);

        Assert.Equal(1, list.IndexOf(new BlockId(9)));
        Assert.Equal(-1, list.IndexOf(new BlockId(2)));
        Assert.Equal(2, list.IndexOf(new BlockId(3)));
        Assert.Equal(1, list.IndexRebuildCount);
    }

    // ───────────────────────────── reconciliation: typing inside one block ─────────────────────────────

    [Fact]
    public void TypingInsideOneParagraph_ChangesExactlyThatBlock_SiblingsReusedWithIds()
    {
        var (_, controller, producer) = Create("aaa\n\nbbb\n\nccc");
        var idsBefore = producer.Blocks.Select(b => b.Id).ToArray();

        BlockListChange? change = null;
        producer.Changed += c => change = c;

        Apply(controller, new TextPosition(2, 3), "", "!"); // type into the middle paragraph

        Assert.NotNull(change);
        Assert.Equal([idsBefore[1]], change!.Changed);
        Assert.Equal([idsBefore[0], idsBefore[2]], change.Reused); // prefix ids, then suffix ids
        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Equal(0, change.LineShift);

        // Identities and spans are stable; only the middle block re-formed (same shape here).
        Assert.Equal(idsBefore, producer.Blocks.Select(b => b.Id));
        Assert.Equal([(0, 2), (2, 2), (4, 1)], Spans(producer.Blocks));
    }

    [Fact]
    public void EnterInsideParagraph_GrowsTheBlock_ShiftsFollowersAsReused()
    {
        var (_, controller, producer) = Create("aaabbb\n\nccc");
        var idsBefore = producer.Blocks.Select(b => b.Id).ToArray();

        BlockListChange? change = null;
        producer.Changed += c => change = c;

        Apply(controller, new TextPosition(0, 3), "", "\n"); // split the LINE, not the paragraph

        Assert.NotNull(change);
        Assert.Equal([idsBefore[0]], change!.Changed);
        Assert.Equal([idsBefore[1]], change.Reused);
        Assert.Empty(change.Added);
        Assert.Empty(change.Removed);
        Assert.Equal(1, change.LineShift);

        // Two-line paragraph + trailing blank; the follower shifted implicitly.
        Assert.Equal([(0, 3), (3, 1)], Spans(producer.Blocks));
        Assert.Equal(idsBefore, producer.Blocks.Select(b => b.Id));
    }

    // ───────────────────────────── reconciliation: split / merge (the gate cases) ─────────────────────────────

    [Fact]
    public void BlankLineSplit_AddsOneBlock_ReusesFollowersWithShift()
    {
        var (_, controller, producer) = Create("aaa bbb\n\nccc");
        var idsBefore = producer.Blocks.Select(b => b.Id).ToArray();

        BlockListChange? change = null;
        producer.Changed += c => change = c;

        Apply(controller, new TextPosition(0, 4), "", "\n\n"); // "aaa \n\nbbb" — a real paragraph split

        Assert.NotNull(change);
        Assert.Equal([idsBefore[0]], change!.Changed);       // the split paragraph keeps its id
        Assert.Single(change.Added);                          // the new second paragraph
        Assert.Equal([idsBefore[1]], change.Reused);          // the follower, shifted
        Assert.Empty(change.Removed);
        Assert.Equal(2, change.LineShift);

        // [aaa ,blank] [bbb,blank] [ccc]
        Assert.Equal([(0, 2), (2, 2), (4, 1)], Spans(producer.Blocks));
        Assert.Equal(idsBefore[0], producer.Blocks[0].Id);
        Assert.Equal(change.Added[0], producer.Blocks[1].Id);
        Assert.Equal(idsBefore[1], producer.Blocks[2].Id);
    }

    [Fact]
    public void BlankLineMerge_RemovesTheSwallowedBlock_ReusesFollowersWithShift()
    {
        var (_, controller, producer) = Create("aaa\n\nbbb\n\nccc");
        var idsBefore = producer.Blocks.Select(b => b.Id).ToArray();

        BlockListChange? change = null;
        producer.Changed += c => change = c;

        // Delete the blank separator's terminator: "aaa" and "bbb" become one paragraph.
        Apply(controller, new TextPosition(1, 0), "\n", "");

        Assert.NotNull(change);
        Assert.Equal([idsBefore[0]], change!.Changed);
        Assert.Equal([idsBefore[1]], change.Removed);
        Assert.Equal([idsBefore[2]], change.Reused);
        Assert.Empty(change.Added);
        Assert.Equal(-1, change.LineShift);

        // [aaa,bbb,blank] [ccc]
        Assert.Equal([(0, 3), (3, 1)], Spans(producer.Blocks));
        Assert.Equal(new[] { idsBefore[0], idsBefore[2] }, producer.Blocks.Select(b => b.Id));
    }

    [Fact]
    public void UndoReplaysThroughTheSamePipeline_RestoringTheBlockStructure()
    {
        var (_, controller, producer) = Create("aaa\n\nbbb");
        var spansBefore = Spans(producer.Blocks);

        Apply(controller, new TextPosition(1, 0), "\n", ""); // merge
        Assert.Equal([(0, 2)], Spans(producer.Blocks));

        var changes = new List<BlockListChange>();
        producer.Changed += changes.Add;

        controller.Undo();

        Assert.Single(changes); // the replay funneled through the same reconciliation path
        Assert.Equal(spansBefore, Spans(producer.Blocks));
    }

    [Fact]
    public void CrLfDocuments_ReconcileByLineNotByChar()
    {
        var (buffer, controller, producer) = Create("aaa\r\n\r\nbbb\r\nccc");
        Assert.Equal([(0, 2), (2, 2)], Spans(producer.Blocks));
        var idsBefore = producer.Blocks.Select(b => b.Id).ToArray();

        BlockListChange? change = null;
        producer.Changed += c => change = c;

        Apply(controller, new TextPosition(2, 3), "", "!"); // edit inside the second paragraph

        Assert.Equal("aaa\r\n\r\nbbb!\r\nccc", buffer.GetText());
        Assert.Equal([idsBefore[1]], change!.Changed);
        Assert.Equal([idsBefore[0]], change.Reused);
        Assert.Equal(0, change.LineShift);
    }

    [Fact]
    public void DisposedProducer_StopsObserving()
    {
        var (_, controller, producer) = Create("aaa\n\nbbb");
        producer.Dispose();

        var notified = false;
        producer.Changed += _ => notified = true;

        Apply(controller, new TextPosition(0, 0), "", "x");

        Assert.False(notified);
        Assert.Equal([(0, 2), (2, 1)], Spans(producer.Blocks)); // frozen at pre-dispose state
    }

    [Fact]
    public void TilingInvariant_HoldsAcrossAnEditScript()
    {
        var (buffer, controller, producer) = Create("aaa\n\nbbb\nccc\n\n\nddd");

        Apply(controller, new TextPosition(0, 3), "", "\n\n");     // split
        Apply(controller, new TextPosition(2, 0), "", "zz");       // fill a blank line
        Apply(controller, new TextPosition(0, 0), "aaa\n", "");    // delete a line
        controller.Undo();
        controller.Undo();

        Assert.Equal(buffer.LineCount, producer.Blocks.TotalLineCount);
        var expectedIndex = 0;
        for (var line = 0; line < buffer.LineCount; line++)
        {
            if (line >= producer.Blocks.GetStartLine(expectedIndex) + producer.Blocks[expectedIndex].LineCount)
                expectedIndex++;
            Assert.Equal(expectedIndex, producer.Blocks.IndexOfLine(line));
        }
    }
}
