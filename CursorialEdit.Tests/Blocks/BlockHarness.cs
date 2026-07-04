using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;

using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Blocks;

/// <summary>
/// Drives <see cref="MarkdigBlockProducer"/> over M1's real <see cref="DocumentBuffer"/>/
/// <see cref="EditController"/> for the M2.WP2 block-model tests: applies edits through the one
/// mutation funnel and captures the resulting <see cref="SpliceResult"/> and
/// <see cref="BlockListChange"/> per edit.
/// </summary>
internal sealed class BlockHarness
{
    private BlockHarness(DocumentBuffer buffer, EditController controller, MarkdigBlockProducer producer)
    {
        Buffer = buffer;
        Controller = controller;
        Producer = producer;
        controller.Changed += r => LastSplice = r;
        producer.Changed += c => LastChange = c;
    }

    public DocumentBuffer Buffer { get; }

    public EditController Controller { get; }

    public MarkdigBlockProducer Producer { get; }

    public BlockList Blocks => Producer.Blocks;

    public SpliceResult LastSplice { get; private set; }

    public BlockListChange? LastChange { get; private set; }

    public static BlockHarness Create(string text)
    {
        var buffer = new DocumentBuffer(text);
        var controller = new EditController(buffer, new FakeTimeProvider());
        return new BlockHarness(buffer, controller, new MarkdigBlockProducer(controller));
    }

    /// <summary>Applies one edit and returns the emitted <see cref="BlockListChange"/>.</summary>
    public BlockListChange Apply(TextPosition start, string removed, string inserted, EditKind kind = EditKind.Typing)
    {
        var caret = new CaretState(start);
        Controller.Apply(new Edit(start, removed, inserted), kind, caret, caret);
        return LastChange!;
    }

    /// <summary>Inserts <paramref name="text"/> at <paramref name="start"/> (pure insertion).</summary>
    public BlockListChange Insert(TextPosition start, string text, EditKind kind = EditKind.Typing)
        => Apply(start, "", text, kind);

    /// <summary>The (start line, line count) span of each block, in order.</summary>
    public (int Start, int Count)[] Spans()
        => [.. Enumerable.Range(0, Blocks.Count).Select(i => (Blocks.GetStartLine(i), Blocks[i].LineCount))];

    /// <summary>The kind of each block, in order.</summary>
    public BlockKind[] Kinds() => [.. Blocks.Select(b => b.Kind)];

    /// <summary>The identity of each block, in order.</summary>
    public BlockId[] Ids() => [.. Blocks.Select(b => b.Id)];

    /// <summary>The exact serialized source text of block <paramref name="index"/> (terminators included).</summary>
    public string TextOf(int index)
    {
        int start = Blocks.GetStartLine(index);
        int count = Blocks[index].LineCount;
        var sb = new System.Text.StringBuilder();
        for (int line = start; line < start + count; line++)
        {
            var value = Buffer.GetLine(line);
            sb.Append(value.Text).Append(value.EndingText);
        }

        return sb.ToString();
    }

    /// <summary>A snapshot of the current blocks for the diff oracle.</summary>
    public IReadOnlyList<BlockSnapshot> Snapshot()
        => [.. Enumerable.Range(0, Blocks.Count).Select(i =>
            new BlockSnapshot(Blocks[i].Id, Blocks[i].Kind, TextOf(i), Blocks.GetStartLine(i), Blocks[i].LineCount))];
}

/// <summary>A captured block for the naive full-diff oracle.</summary>
internal readonly record struct BlockSnapshot(BlockId Id, BlockKind Kind, string Text, int StartLine, int LineCount);
