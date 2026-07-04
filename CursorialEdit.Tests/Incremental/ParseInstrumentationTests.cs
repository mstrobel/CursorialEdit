using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Incremental;

/// <summary>
/// M2.WP3 / §13 gate — <c>ParseInstrumentation</c>: a fast-path keystroke must not trigger a
/// full-document Markdig parse (architecture Decision 3, "no full reparse per keystroke"). Drives the
/// windowed producer's parse counters (<see cref="Document.Model.MarkdigBlockProducer.FullDocumentParseCount"/>
/// etc.) to prove the incremental path stays off the full-parse budget.
/// </summary>
public sealed class ParseInstrumentationTests
{
    private const string MultiBlockDoc =
        "# Title\n\nfirst paragraph here\n\nsecond paragraph here\n\n> a quote block\n\nthird paragraph text";

    [Fact]
    public void FastPathKeystroke_DoesNotFullReparse()
    {
        var h = BlockHarness.Create(MultiBlockDoc);
        int fullBefore = h.Producer.FullDocumentParseCount;
        Assert.Equal(1, fullBefore); // exactly the construction-time full parse

        // A word-interior letter insertion ("para|graph") — the fast path.
        h.Insert(new TextPosition(2, 10), "x");

        Assert.False(h.Producer.LastParseWasFullDocument);
        Assert.Equal(fullBefore, h.Producer.FullDocumentParseCount); // no new full parse
        Assert.True(h.Producer.LastParsedLineCount < h.Buffer.LineCount,
            $"fast path parsed {h.Producer.LastParsedLineCount} lines of {h.Buffer.LineCount} — expected a sub-document window");
        Assert.Equal(1, h.Producer.WindowParseCount); // exactly one windowed parse
    }

    [Fact]
    public void FastPathKeystroke_ParsesOnlyTheEditedBlock()
    {
        var h = BlockHarness.Create(MultiBlockDoc);

        // The edited block is the "second paragraph here" paragraph (line 4) plus its trailing blank.
        int editedBlock = h.Blocks.IndexOfLine(4);
        int blockLines = h.Blocks[editedBlock].LineCount;

        h.Insert(new TextPosition(4, 8), "y"); // "second p|aragraph"

        Assert.False(h.Producer.LastParseWasFullDocument);
        Assert.Equal(blockLines, h.Producer.LastParsedLineCount); // exactly the one block, no neighbours
    }

    [Fact]
    public void ManyFastPathKeystrokes_NeverFullReparse()
    {
        var h = BlockHarness.Create(MultiBlockDoc);
        int fullBefore = h.Producer.FullDocumentParseCount;

        // Type a run of letters into the middle of a word — every keystroke is fast-path eligible.
        int col = 6; // inside "third" on the last line
        int line = h.Buffer.LineCount - 1;
        for (var i = 0; i < 20; i++)
        {
            h.Insert(new TextPosition(line, col), "z");
            col++;
            Assert.False(h.Producer.LastParseWasFullDocument);
        }

        Assert.Equal(fullBefore, h.Producer.FullDocumentParseCount);
    }

    [Fact]
    public void StructuralKeystroke_Windows_ButStillNotFullOnASmallLocalEdit()
    {
        var h = BlockHarness.Create(MultiBlockDoc);
        int fullBefore = h.Producer.FullDocumentParseCount;

        // A '#' (heading marker) is boundary-significant — the fast path rejects it — but a local
        // structural edit still windows rather than reparsing the whole document.
        h.Insert(new TextPosition(8, 0), "#");

        Assert.False(h.Producer.LastParseWasFullDocument);
        Assert.Equal(fullBefore, h.Producer.FullDocumentParseCount);
        Assert.True(h.Producer.LastParsedLineCount < h.Buffer.LineCount);
    }
}
