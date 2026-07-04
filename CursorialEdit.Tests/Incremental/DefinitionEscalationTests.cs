using CursorialEdit.Document.Buffer;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Incremental;

/// <summary>
/// M2.WP3 — the document-global definition escalation (the seed-5236 fuzzer divergence). Footnote and
/// link-reference definitions resolve against the whole document, so a windowed parse of a sub-range
/// diverges from a full parse; the producer escalates such documents (and edits that introduce a
/// definition) to a full reparse. A definition-free document stays windowed (§13). Correctness is
/// independently enforced by the differential fuzzer; these pin the ESCALATION policy directly.
/// </summary>
public sealed class DefinitionEscalationTests
{
    [Fact]
    public void EditingADocumentWithAFootnoteDefinition_FullReparses_AndMatchesFull()
    {
        var h = BlockHarness.Create("see [^n] here\n\n[^n]: the note\n\ntail");
        int fullBefore = h.Producer.FullDocumentParseCount;

        h.Insert(new TextPosition(0, 3), "X"); // an ordinary in-paragraph keystroke, far from the def

        Assert.True(h.Producer.LastParseWasFullDocument);            // escalated: the doc has a footnote def
        Assert.Equal(fullBefore + 1, h.Producer.FullDocumentParseCount);
        WindowedParseOracle.AssertMatchesFullParse(h);              // ...and the tiling equals a full parse
    }

    [Fact]
    public void EditingADocumentWithALinkReferenceDefinition_FullReparses()
    {
        var h = BlockHarness.Create("a [ref] link\n\n[ref]: https://example.com\n\ntail");

        h.Insert(new TextPosition(0, 1), "Z");

        Assert.True(h.Producer.LastParseWasFullDocument); // link-reference definitions are document-global too
        WindowedParseOracle.AssertMatchesFullParse(h);
    }

    [Fact]
    public void IntroducingAFootnoteDefinition_FullReparses_EvenInAPreviouslyPlainDoc()
    {
        var h = BlockHarness.Create("plain one\n\nplain two\n\nplain three");
        // (The seed parse is itself a full parse; what matters is that the definition-introducing EDIT
        // takes the full path even though the pre-edit document had no definitions.)

        h.Insert(new TextPosition(4, 0), "[^new]: introduced\n\n"); // this edit adds the first definition

        Assert.True(h.Producer.LastParseWasFullDocument); // the edit text carries definition syntax → escalate
        WindowedParseOracle.AssertMatchesFullParse(h);
    }

    [Fact]
    public void DefinitionFreeDocument_StaysWindowed()
    {
        var h = BlockHarness.Create("# Title\n\nfirst para\n\nsecond para\n\nthird para");
        int fullBefore = h.Producer.FullDocumentParseCount;

        h.Insert(new TextPosition(2, 3), "abc"); // ordinary keystrokes, no definition anywhere

        Assert.False(h.Producer.LastParseWasFullDocument);          // §13: stayed on the windowed path
        Assert.Equal(fullBefore, h.Producer.FullDocumentParseCount);
        WindowedParseOracle.AssertMatchesFullParse(h);
    }
}
