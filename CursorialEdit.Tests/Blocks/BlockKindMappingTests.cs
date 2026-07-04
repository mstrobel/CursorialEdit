using CursorialEdit.Document.Model;
using CursorialEdit.Document.Parsing;

using Markdig;
using Markdig.Syntax;

namespace CursorialEdit.Tests.Blocks;

/// <summary>
/// M2.WP2 — mapping every pinned Markdig top-level construct to the right <see cref="BlockKind"/>
/// with the correct tiling line range, and confirming the block's precise span is usable
/// (<c>document.Substring(span.Start, span.Length)</c> reproduces the construct — the WP1 oracle
/// result the producer relies on). Front matter, headings (ATX and setext), fenced/indented code,
/// quotes, lists, rules, tables, HTML, alerts, definition lists, math, footnotes, and link-reference
/// definitions are each exercised.
/// </summary>
public sealed class BlockKindMappingTests
{
    // ───────────────────────────── the kind mapper (unit) ─────────────────────────────

    [Theory]
    [InlineData("# H", BlockKind.Heading)]
    [InlineData("Title\n===", BlockKind.Heading)]
    [InlineData("```\ncode\n```", BlockKind.FencedCode)]
    [InlineData("    indented", BlockKind.IndentedCode)]
    [InlineData("> quote", BlockKind.Quote)]
    [InlineData("- a\n- b", BlockKind.List)]
    [InlineData("---", BlockKind.ThematicBreak)]
    [InlineData("| a | b |\n| - | - |\n| 1 | 2 |", BlockKind.Table)]
    [InlineData("<div>x</div>", BlockKind.Html)]
    [InlineData("> [!NOTE]\n> body", BlockKind.Alert)]
    [InlineData("Term\n:   def", BlockKind.DefinitionList)]
    [InlineData("$$\nx^2\n$$", BlockKind.Math)]
    [InlineData("plain paragraph", BlockKind.Paragraph)]
    public void Mapper_MapsFirstTopLevelBlock(string markdown, BlockKind expected)
    {
        var doc = Markdown.Parse(markdown, MarkdownPipelineFactory.Shared);
        var first = doc.First(b => b is not (LinkReferenceDefinitionGroup or Markdig.Extensions.Footnotes.FootnoteGroup));
        Assert.Equal(expected, MarkdigBlockKindMap.Map(first));
    }

    [Fact]
    public void Mapper_MapsListItem_ForFutureDecomposition()
    {
        var doc = Markdown.Parse("- one\n- two", MarkdownPipelineFactory.Shared);
        var list = (ListBlock) doc.Single(b => b is ListBlock);
        Assert.Equal(BlockKind.ListItem, MarkdigBlockKindMap.Map((ListItemBlock) list[0]));
    }

    [Fact]
    public void Mapper_NullThrows() => Assert.Throws<ArgumentNullException>(() => MarkdigBlockKindMap.Map(null!));

    // ───────────────────────────── kind + line-range tiling (via the producer) ─────────────────────────────

    [Fact]
    public void Paragraph_And_Heading_Level()
    {
        var h = BlockHarness.Create("# Title\n\nA paragraph.\n\n## Sub");

        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph, BlockKind.Heading], h.Kinds());
        Assert.Equal([(0, 2), (2, 2), (4, 1)], h.Spans());
        Assert.Equal(1, h.Blocks[0].HeadingLevel);
        Assert.Null(h.Blocks[1].HeadingLevel);
        Assert.Equal(2, h.Blocks[2].HeadingLevel);
    }

    [Fact]
    public void SetextHeading_StartsAtTheTextLine_NotTheUnderline()
    {
        // Markdig reports HeadingBlock.Line at the '===' underline; the producer must anchor the
        // block at the TEXT line via the precise span so tiling stays correct.
        var h = BlockHarness.Create("Title\n===\n\nbody");

        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph], h.Kinds());
        Assert.Equal([(0, 3), (3, 1)], h.Spans()); // heading owns the text, underline, and trailing blank
        Assert.Equal(1, h.Blocks[0].HeadingLevel);
    }

    [Fact]
    public void FencedCode_CarriesInfoString_AndTilesWholeFence()
    {
        var h = BlockHarness.Create("```csharp\nvar x = 1;\n```\n\nafter");

        Assert.Equal([BlockKind.FencedCode, BlockKind.Paragraph], h.Kinds());
        Assert.Equal([(0, 4), (4, 1)], h.Spans());
        Assert.Equal("csharp", h.Blocks[0].FenceInfo);
    }

    [Fact]
    public void IndentedCode_MapsWithoutInfo()
    {
        var h = BlockHarness.Create("    line one\n    line two\n\ntext");

        Assert.Equal(BlockKind.IndentedCode, h.Blocks[0].Kind);
        Assert.Null(h.Blocks[0].FenceInfo);
        Assert.Equal([(0, 3), (3, 1)], h.Spans());
    }

    [Fact]
    public void Quote_And_Alert_AreDistinct()
    {
        var quote = BlockHarness.Create("> just a quote\n> more");
        Assert.Equal(BlockKind.Quote, quote.Blocks[0].Kind);

        var alert = BlockHarness.Create("> [!WARNING]\n> heed this");
        Assert.Equal(BlockKind.Alert, alert.Blocks[0].Kind);
    }

    [Fact]
    public void List_ThematicBreak_Table()
    {
        var h = BlockHarness.Create("- a\n- b\n\n---\n\n| x | y |\n| - | - |\n| 1 | 2 |");

        Assert.Equal([BlockKind.List, BlockKind.ThematicBreak, BlockKind.Table], h.Kinds());
        Assert.Equal(h.Buffer.LineCount, h.Blocks.TotalLineCount);
    }

    [Fact]
    public void FrontMatter_IsTheDocumentHead()
    {
        var h = BlockHarness.Create("---\ntitle: x\ntags: [a, b]\n---\n\nBody.");

        Assert.Equal([BlockKind.FrontMatter, BlockKind.Paragraph], h.Kinds());
        Assert.Equal([(0, 5), (5, 1)], h.Spans());
    }

    [Fact]
    public void DefinitionList_And_Math()
    {
        var deflist = BlockHarness.Create("Apple\n:   A fruit.\n\ntext");
        Assert.Equal(BlockKind.DefinitionList, deflist.Blocks[0].Kind);

        var math = BlockHarness.Create("$$\n\\int x\\,dx\n$$\n\ntext");
        Assert.Equal(BlockKind.Math, math.Blocks[0].Kind);
    }

    [Fact]
    public void Footnote_And_LinkReference_Definitions_ReanchoredToSourceLines()
    {
        var footnote = BlockHarness.Create("A claim.[^1]\n\n[^1]: The evidence.\n\nAfter.");
        // Markdig relocates the definition to a footnote group at the tail; the producer re-anchors
        // it to its source line so the tiling stays in document order.
        Assert.Equal([BlockKind.Paragraph, BlockKind.Footnote, BlockKind.Paragraph], footnote.Kinds());
        Assert.Equal(footnote.Buffer.LineCount, footnote.Blocks.TotalLineCount);

        var linkref = BlockHarness.Create("See [it][r].\n\n[r]: https://example.com\n\nAfter.");
        Assert.Equal([BlockKind.Paragraph, BlockKind.LinkReferenceDefinition, BlockKind.Paragraph], linkref.Kinds());
        Assert.Equal(linkref.Buffer.LineCount, linkref.Blocks.TotalLineCount);
    }

    [Fact]
    public void Html_MapsToHtmlKind()
    {
        var h = BlockHarness.Create("<div class=\"x\">\nraw\n</div>\n\ntext");
        Assert.Equal([BlockKind.Html, BlockKind.Paragraph], h.Kinds());
    }

    // ───────────────────────────── empty / blank documents ─────────────────────────────

    [Fact]
    public void EmptyDocument_IsOneParagraphBlock()
    {
        var h = BlockHarness.Create("");
        Assert.Equal([BlockKind.Paragraph], h.Kinds());
        Assert.Equal([(0, 1)], h.Spans());
        Assert.Null(h.Blocks[0].MarkdigBlock);
    }

    [Fact]
    public void AllBlankDocument_IsOneParagraphBlock()
    {
        var h = BlockHarness.Create("\n\n\n");
        Assert.Equal([BlockKind.Paragraph], h.Kinds());
        Assert.Equal([(0, 4)], h.Spans());
    }

    [Fact]
    public void LeadingBlankLines_AttachToTheFirstBlock()
    {
        var h = BlockHarness.Create("\n\n# Heading");
        Assert.Equal([BlockKind.Heading], h.Kinds());
        Assert.Equal([(0, 3)], h.Spans());
    }

    // ───────────────────────────── spans are trustworthy ─────────────────────────────

    [Fact]
    public void EveryBlockSpan_ReproducesItsConstructSource()
    {
        const string markdown = "# Heading\n\nA *para* with `code`.\n\n```js\nx();\n```\n\n> quote\n\n- one\n- two";
        var h = BlockHarness.Create(markdown);
        string document = h.Buffer.GetText();

        foreach (var block in h.Blocks)
        {
            var span = block.MarkdigBlock!.Span;
            string slice = document.Substring(span.Start, span.Length);
            Assert.False(string.IsNullOrWhiteSpace(slice));
            // The block's first source line is contained within its precise span slice.
            int startLine = h.Blocks.GetStartLine(h.Blocks.IndexOf(block.Id));
            string firstLineText = h.Buffer.GetLine(FirstNonBlank(h, startLine, block.LineCount)).Text.Trim();
            if (firstLineText.Length > 0)
                Assert.Contains(firstLineText.TrimStart('#', ' ', '>', '-', '`'), slice, StringComparison.Ordinal);
        }
    }

    private static int FirstNonBlank(BlockHarness h, int start, int count)
    {
        for (int line = start; line < start + count; line++)
        {
            if (!string.IsNullOrWhiteSpace(h.Buffer.GetLine(line).Text))
                return line;
        }

        return start;
    }
}
