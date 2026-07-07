using Cursorial.Input;
using Cursorial.Output;

using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M4 slice — the minimal wrap-selection inline-format commands (Bold / Italic / InlineCode) on the markdown
/// surface. Each wraps the selection's SOURCE range in its marks as one atomic undo group; with no selection it
/// inserts the empty pair and drops the caret between the marks. Verifies the source splice, the restored
/// selection/caret, the formatted render (marks re-derive → the inner text shows bold/italic/code), single-undo
/// restore, backwards-selection handling, and the deliberate no-op inside a table cell. Rendering theories run
/// under both §5.1 wire presets.
/// </summary>
public sealed class FormatCommandTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    // ───────────────────────────── wrap a selection: source + render ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Bold_WrapsSelection_SourceHasMarks_AndRendersBold(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("hello world\n\nsecond", preset);

        harness.Click(8, 0, clickCount: 2);                 // double-click "world" → selection [(0,6),(0,11))
        harness.AssertCaret(0, 11, new TextPosition(0, 6));

        harness.Editor.Bold();
        harness.Settle();

        Assert.Equal("hello **world**\n\nsecond", harness.Buffer.GetText()); // marks spliced into the source
        harness.AssertCaret(0, 13, new TextPosition(0, 8));                   // selection re-covers the inner text

        harness.Key(Key.End, KeyModifiers.Control);         // deactivate block 0 → its marks re-hide
        Assert.Equal("hello world", harness.RowTrimmed(0)); // formatted: the ** fences are hidden
        Assert.True((harness.AttributesAt(6, 0) & TextAttributes.Bold) != 0);  // "w" of "world" renders bold
        Assert.True((harness.AttributesAt(0, 0) & TextAttributes.Bold) == 0);  // "h" of "hello" is plain
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Italic_WrapsSelection_SourceHasMarks_AndRendersItalic(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("hello world\n\nsecond", preset);

        harness.Click(8, 0, clickCount: 2);
        harness.Editor.Italic();
        harness.Settle();

        Assert.Equal("hello *world*\n\nsecond", harness.Buffer.GetText());
        harness.AssertCaret(0, 12, new TextPosition(0, 7));  // one-char marks: inner shifts by 1

        harness.Key(Key.End, KeyModifiers.Control);
        Assert.Equal("hello world", harness.RowTrimmed(0));
        Assert.True((harness.AttributesAt(6, 0) & TextAttributes.Italic) != 0);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void InlineCode_WrapsSelection_SourceHasMarks_AndRendersCodeFill(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("hello world\n\nsecond", preset);

        harness.Click(8, 0, clickCount: 2);
        harness.Editor.InlineCode();
        harness.Settle();

        Assert.Equal("hello `world`\n\nsecond", harness.Buffer.GetText());
        harness.AssertCaret(0, 12, new TextPosition(0, 7));

        harness.Key(Key.End, KeyModifiers.Control);
        Assert.Equal("hello world", harness.RowTrimmed(0));
        Assert.Equal(Colors.LightBlack, harness.BackgroundAt(6, 0)); // `code` renders the code fill
    }

    // ───────────────────────────── no selection: empty pair, caret between ─────────────────────────────

    [Fact]
    public void Wrap_NoSelection_InsertsEmptyPair_AndDropsCaretBetweenTheMarks()
    {
        using var harness = MarkdownEditingHarness.Create("hello world");

        harness.AssertCaret(0, 0);   // caret at the origin, no selection
        harness.Editor.Bold();
        harness.Settle();

        Assert.Equal("****hello world", harness.Buffer.GetText()); // the empty pair at the caret
        harness.AssertCaret(0, 2);                                 // caret BETWEEN the marks (**|**)
    }

    // ───────────────────────────── one undo restores ─────────────────────────────

    [Fact]
    public void Wrap_IsOneUndoGroup_SingleUndoRestoresTextAndSelection()
    {
        using var harness = MarkdownEditingHarness.Create("hello world\n\nsecond");

        harness.Click(8, 0, clickCount: 2);
        harness.Editor.Bold();
        harness.Settle();
        Assert.Equal("hello **world**\n\nsecond", harness.Buffer.GetText());

        harness.Chord('z', KeyModifiers.Control);                            // one Ctrl+Z
        Assert.Equal("hello world\n\nsecond", harness.Buffer.GetText());     // fully restored
        harness.AssertCaret(0, 11, new TextPosition(0, 6));                  // …including the original selection
    }

    // ───────────────────────────── backwards selection ─────────────────────────────

    [Fact]
    public void Wrap_BackwardsSelection_WrapsTheRangeAndNormalizesTheRestoredSelection()
    {
        using var harness = MarkdownEditingHarness.Create("hello world");

        harness.Key(Key.End, KeyModifiers.Control);           // caret at (0,11)
        for (int i = 0; i < 5; i++)
            harness.Key(Key.LeftArrow, KeyModifiers.Shift);   // extend LEFT → active (0,6), anchor (0,11)
        harness.AssertCaret(0, 6, new TextPosition(0, 11));   // a backwards selection

        harness.Editor.Bold();
        harness.Settle();

        Assert.Equal("hello **world**", harness.Buffer.GetText());  // wrapped the exact range regardless of direction
        harness.AssertCaret(0, 13, new TextPosition(0, 8));         // restored selection is normalized over the inner text
    }

    // ───────────────────────────── table-cell guard (no-op) ─────────────────────────────

    [Fact]
    public void Wrap_InsideATableCell_IsANoOp()
    {
        const string table = "| A | B |\n|---|---|\n| foo | 2 |\n";
        using var harness = MarkdownEditingHarness.Create(table, columns: 40, rows: 14);

        harness.Caret.SelectWordAt(new TextPosition(2, 3)); // select "foo" inside the body cell
        harness.Settle();
        Assert.True(harness.Caret.IsInTable, "the selection is inside a table cell");

        harness.Editor.Bold();
        harness.Settle();

        Assert.Equal(table, harness.Buffer.GetText()); // guarded: raw marks never corrupt the cell/pipe structure
    }
}
