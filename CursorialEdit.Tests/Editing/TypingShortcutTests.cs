using CursorialEdit.Document.Model;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// M2.WP12 — the typing-shortcut <b>verification package</b> (implementation-plan §7 WP12 / §3.2
/// resolution 2, spec §6.2). M2 owns block-start recognition as a <b>parse reflex</b>: typing the
/// markdown block-opening syntax at the start of a line (<c>## </c>, <c>- </c>, <c>&gt; </c>, <c>1. </c>,
/// a fence, <c>- [ ] </c>) through the <i>real editor</i> re-parses incrementally and the paragraph
/// becomes the correct block <b>live</b>, with no explicit command. (The structured-edit <i>commands</i> —
/// Ctrl+B, list continuation, auto-pairing — are M4; this package only proves the reflex fires.)
/// </summary>
public sealed class TypingShortcutTests
{
    private static MarkdownEditingHarness Empty() => MarkdownEditingHarness.Create("");

    [Fact]
    public void TypingAtxPrefix_MakesAHeadingLive()
    {
        var h = Empty();
        h.Type("## Section");

        Assert.Equal(BlockKind.Heading, h.Blocks[0].Kind);
        Assert.Equal(2, h.Blocks[0].HeadingLevel);
    }

    [Theory]
    [InlineData("# H", 1)]
    [InlineData("### H", 3)]
    [InlineData("###### H", 6)]
    public void TypingAtxPrefix_AtEachLevel_MakesTheRightHeadingLevel(string typed, int level)
    {
        var h = Empty();
        h.Type(typed);

        Assert.Equal(BlockKind.Heading, h.Blocks[0].Kind);
        Assert.Equal(level, h.Blocks[0].HeadingLevel);
    }

    [Fact]
    public void TypingBulletPrefix_MakesAList()
    {
        var h = Empty();
        h.Type("- item");
        Assert.Equal(BlockKind.List, h.Blocks[0].Kind);
    }

    [Fact]
    public void TypingOrderedPrefix_MakesAList()
    {
        var h = Empty();
        h.Type("1. item");
        Assert.Equal(BlockKind.List, h.Blocks[0].Kind);
    }

    [Fact]
    public void TypingQuotePrefix_MakesAQuote()
    {
        var h = Empty();
        h.Type("> quoted");
        Assert.Equal(BlockKind.Quote, h.Blocks[0].Kind);
    }

    [Fact]
    public void TypingATaskListPrefix_MakesAList()
    {
        // §6.2: `- [ ] ` is recognized as a (task) list block. The checkbox renders via fallback until M4,
        // but the parse reflex must classify the block as a List now.
        var h = Empty();
        h.Type("- [ ] todo");
        Assert.Equal(BlockKind.List, h.Blocks[0].Kind);
    }

    [Fact]
    public void TypingAFence_MakesAFencedCodeBlock()
    {
        var h = Empty();
        h.Type("```\ncode\n```");
        Assert.Equal(BlockKind.FencedCode, h.Blocks[0].Kind);
    }

    [Fact]
    public void TheReflexIsLive_TheBlockKindFlipsMidTyping()
    {
        // Build an ordered-list opener incrementally: a bare "1" is a plain paragraph; only once the
        // "1. " marker + content lands does the same block re-parse to a List — no command, no reload.
        // (A bare "#" would already be a valid empty ATX heading, so ordered-list is the clean flip.)
        var h = Empty();

        h.Type("1");
        Assert.Equal(BlockKind.Paragraph, h.Blocks[0].Kind); // a bare "1" is not yet a list

        h.Type(". item"); // now "1. item"
        Assert.Equal(BlockKind.List, h.Blocks[0].Kind);      // flipped live
    }

    [Fact]
    public void TheReflexRendersFormatted_TheHeadingMarkHidesOnAnInactiveLine()
    {
        // End-to-end through the render pipeline: a typed heading, once it is not the caret's line, hides
        // its "## " mark and renders formatted (the reflex + the presenter suite together).
        var h = Empty();
        h.Type("## Section\ntail"); // heading on line 0, a paragraph line 1; caret ends on line 1
        h.Settle();

        Assert.Equal(BlockKind.Heading, h.Blocks[0].Kind);
        Assert.Equal("Section", h.RowTrimmed(0)); // "## " hidden on the now-inactive heading line
    }
}
