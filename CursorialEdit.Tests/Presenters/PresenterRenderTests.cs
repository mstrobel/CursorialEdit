using Cursorial.Output;
using Cursorial.Rendering;

using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7 §2.1 rendering gate — every rendered CommonMark-core construct displays in <b>formatted</b>
/// form when inactive (syntax marks hidden, structural markers as glyphs, inline emphasis/links/code
/// styled), read back from the composited cells of the real <see cref="PresenterHarness"/> frame loop.
/// The text-content assertions run under both §5.1 wire presets; the color/attribute assertions use
/// palette entries and <see cref="TextAttributes"/> that survive both wires (verified), so they hold
/// on <c>KittyTruecolor</c> and <c>Ansi16Legacy</c> alike.
/// </summary>
public sealed class PresenterRenderTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    private static bool Has(Cell cell, TextAttributes attribute) => (cell.Style.Attributes & attribute) == attribute;

    // ───────────────────────────── headings ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void AtxHeading_ShowsTextWithoutHash_BoldAndColored(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("# Heading one", preset);

        Assert.Equal("Heading one", harness.RowTrimmed(0)); // the "# " prefix is hidden
        var cell = harness.Cell(0, 0);
        Assert.Equal("H", cell.Grapheme);
        Assert.True(Has(cell, TextAttributes.Bold)); // per-level weight (§2.1)
        Assert.Equal(Colors.LightBlue, cell.Style.Foreground); // H1 color
    }

    [Theory]
    [InlineData(1, "LightBlue", true)]
    [InlineData(2, "LightCyan", true)]
    [InlineData(3, "LightGreen", false)]
    [InlineData(4, "LightYellow", false)]
    [InlineData(5, "LightMagenta", false)]
    [InlineData(6, "LightRed", false)]
    public void EveryHeadingLevel_HasDistinctColorAndWeight(int level, string colorName, bool underlined)
    {
        using var harness = PresenterHarness.FromMarkdown($"{new string('#', level)} Title");

        Assert.Equal("Title", harness.RowTrimmed(0));
        var cell = harness.Cell(0, 0);
        Assert.True(Has(cell, TextAttributes.Bold));
        Assert.Equal(underlined, Has(cell, TextAttributes.Underline)); // H1/H2 also underline (setext echo)
        Assert.Equal(ResolveColor(colorName), cell.Style.Foreground);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void SetextHeading_RendersAsHeading_UnderlineHidden(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("Title here\n==========", preset);

        Assert.Equal("Title here", harness.RowTrimmed(0));
        Assert.True(Has(harness.Cell(0, 0), TextAttributes.Bold));
        Assert.Equal(Colors.LightBlue, harness.Cell(0, 0).Style.Foreground); // setext `=` → H1
        Assert.Equal(string.Empty, harness.RowTrimmed(1));                    // the ===== underline is hidden
        Assert.Equal(2, harness.Height(0));                                   // but its row is still reserved
    }

    // ───────────────────────────── inline emphasis ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void InlineEmphasis_StrongItalicCodeStrike_Styled(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("**bold** *em* `code` ~~s~~ x", preset);

        Assert.Equal("bold em code s x", harness.RowTrimmed(0)); // all fences hidden
        Assert.True(Has(harness.Cell(0, 0), TextAttributes.Bold));          // **bold**
        Assert.True(Has(harness.Cell(5, 0), TextAttributes.Italic));        // *em*
        Assert.Equal(Colors.LightBlack, harness.Cell(8, 0).Style.Background); // `code` fill
        Assert.True(Has(harness.Cell(13, 0), TextAttributes.Strikethrough)); // ~~s~~
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Link_RendersUnderlined(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("A [link](http://x.com) here", preset);

        Assert.Equal("A link here", harness.RowTrimmed(0)); // the [](url) scaffolding hides
        Assert.True(Has(harness.Cell(2, 0), TextAttributes.Underline)); // "l" of "link"
        Assert.False(Has(harness.Cell(0, 0), TextAttributes.Underline)); // plain "A" is not
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Autolink_Underlined(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("see https://z.com now", preset);

        Assert.Equal("see https://z.com now", harness.RowTrimmed(0));
        Assert.True(Has(harness.Cell(4, 0), TextAttributes.Underline)); // "h" of the bare autolink
        Assert.False(Has(harness.Cell(0, 0), TextAttributes.Underline)); // "s" of "see" is plain
    }

    // ───────────────────────────── code blocks ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void FencedCode_FilledWithLanguageLabelAndHighlight(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("```csharp\nvar x = 1; // hi\n```", preset);

        Assert.Equal(3, harness.Height(0)); // fence-open, body, fence-close all reserve rows
        Assert.Equal("csharp", harness.RowTrimmed(0)); // opening fence shows the language label, not ```
        Assert.Equal("var x = 1; // hi", harness.RowTrimmed(1));
        Assert.Equal(string.Empty, harness.RowTrimmed(2)); // closing ``` hidden

        // The code fill spans the block; the body highlights keyword / number / comment.
        Assert.Equal(Colors.LightBlack, harness.Cell(0, 1).Style.Background);
        Assert.Equal(Colors.LightBlue, harness.Cell(0, 1).Style.Foreground);  // "var" keyword
        Assert.Equal(Colors.LightCyan, harness.Cell(8, 1).Style.Foreground);  // "1" number
        Assert.Equal(Colors.White, harness.Cell(11, 1).Style.Foreground);     // "//" comment (readable on the fill)
        Assert.NotEqual(harness.Cell(11, 1).Style.Foreground, harness.Cell(11, 1).Style.Background); // never fill-on-fill
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void IndentedCode_RendersFilled_IndentPreserved(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("    indented code\n    line two", preset);

        // The 4-space indent is drawn verbatim (real source the user edits) — this keeps the inactive
        // and revealed rows column-aligned so the caret never jumps landing on a line (WP7a review fix).
        Assert.Equal("    indented code", harness.Row(0).TrimEnd());
        Assert.Equal("    line two", harness.Row(1).TrimEnd());
        Assert.Equal(Colors.LightBlack, harness.Cell(0, 0).Style.Background); // the code fill
    }

    [Fact]
    public void UnknownLanguage_RendersMonochrome()
    {
        using var harness = PresenterHarness.FromMarkdown("```fortran\nkeyword var 42\n```");

        Assert.Equal("keyword var 42", harness.RowTrimmed(1));
        // No token brush is applied — the fill's default foreground carries every cell (monochrome).
        var expected = harness.Cell(0, 1).Style.Foreground;
        for (var c = 0; c < "keyword var 42".Length; c++)
            Assert.Equal(expected, harness.Cell(c, 1).Style.Foreground);
        Assert.Equal(Color.Default, expected);
    }

    // ───────────────────────────── blockquotes ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void Blockquote_RendersBarGlyph(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("> quoted line", preset);

        Assert.Equal("▌ quoted line", harness.RowTrimmed(0)); // ▌ replaces "> "
        Assert.Equal("▌", harness.Cell(0, 0).Grapheme);
        Assert.Equal(Colors.LightBlack, harness.Cell(0, 0).Style.Foreground);
        Assert.Equal("q", harness.Cell(2, 0).Grapheme); // body follows the bar
    }

    [Fact]
    public void NestedBlockquote_RendersOneBarPerLevel()
    {
        using var harness = PresenterHarness.FromMarkdown("> outer\n> > nested");

        Assert.Equal("▌ outer", harness.RowTrimmed(0));
        Assert.Equal("▌", harness.Cell(0, 1).Grapheme);
        Assert.Equal("▌", harness.Cell(1, 1).Grapheme); // two bars for depth 2
        Assert.Contains("nested", harness.RowTrimmed(1));
    }

    // ───────────────────────────── lists ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void UnorderedList_RendersBulletAndText(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("- item one\n- item two", preset);

        Assert.Equal("• item one", harness.RowTrimmed(0)); // "- " normalized to a bullet
        Assert.Equal("• item two", harness.RowTrimmed(1));
        Assert.Equal("•", harness.Cell(0, 0).Grapheme);
        Assert.Equal(Colors.LightYellow, harness.Cell(0, 0).Style.Foreground);
    }

    [Fact]
    public void OrderedList_RendersNumberAndText()
    {
        using var harness = PresenterHarness.FromMarkdown("1. first\n2. second");

        Assert.Equal("1. first", harness.RowTrimmed(0)); // ordered markers keep their numerals
        Assert.Equal("2. second", harness.RowTrimmed(1));
        Assert.Equal("1", harness.Cell(0, 0).Grapheme);
        Assert.Equal(Colors.LightYellow, harness.Cell(0, 0).Style.Foreground);
    }

    [Fact]
    public void NestedList_RendersWithIndent()
    {
        using var harness = PresenterHarness.FromMarkdown("- a\n  - nested b");

        Assert.Equal("• a", harness.RowTrimmed(0));
        Assert.Equal("  • nested b", harness.RowTrimmed(1)); // the two-space nest indent is preserved
    }

    [Fact]
    public void TaskListItem_RendersRawMarker_CheckboxDeferredToM4()
    {
        using var harness = PresenterHarness.FromMarkdown("- [ ] task\n- [x] done");

        // The checkbox is deferred to M4 — the item renders its bullet plus the literal [ ]/[x].
        Assert.Equal("• [ ] task", harness.RowTrimmed(0));
        Assert.Equal("• [x] done", harness.RowTrimmed(1));
    }

    // ───────────────────────────── rules, breaks ─────────────────────────────

    [Theory]
    [MemberData(nameof(Presets))]
    public void ThematicBreak_RendersFullWidthRule(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("---", preset, columns: 20);

        Assert.Equal(new string('─', 20), harness.RowTrimmed(0));
        Assert.Equal(Colors.LightBlack, harness.Cell(0, 0).Style.Foreground);
    }

    [Theory]
    [InlineData("***")]
    [InlineData("___")]
    public void OtherRuleForms_AlsoRenderAsRule(string source)
    {
        using var harness = PresenterHarness.FromMarkdown(source, columns: 12);
        Assert.Equal(new string('─', 12), harness.RowTrimmed(0));
    }

    [Fact]
    public void HardLineBreak_IsHonored_TrailingSpacesHidden()
    {
        using var harness = PresenterHarness.FromMarkdown("line one  \nline two");

        // The two source lines render on separate rows (the break is honored); the trailing hard-break
        // spaces are invisible when inactive (§2.1).
        Assert.Equal("line one", harness.RowTrimmed(0));
        Assert.Equal("line two", harness.RowTrimmed(1));
    }

    // ───────────────────────────── front matter, fallback ─────────────────────────────

    [Fact]
    public void FrontMatter_RendersDimAndFolded()
    {
        using var harness = PresenterHarness.FromMarkdown("---\ntitle: x\nauthor: y\n---\n\nBody");

        Assert.Equal(1, harness.Height(0)); // folded to one summary row by default
        Assert.StartsWith("▸", harness.RowTrimmed(0));
        Assert.True(Has(harness.Cell(0, 0), TextAttributes.Faint)); // dim "front matter" style
        Assert.Equal("Body", harness.RowTrimmed(1)); // the following paragraph sits right under the fold
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void RawHtml_RendersDimmedLiteral_NeverInterpreted(string preset)
    {
        using var harness = PresenterHarness.FromMarkdown("<div class=\"x\">raw</div>", preset);

        Assert.Equal("<div class=\"x\">raw</div>", harness.RowTrimmed(0)); // literal, not interpreted
        Assert.True(Has(harness.Cell(0, 0), TextAttributes.Faint));        // dimmed (§2.4)
    }

    private static Color ResolveColor(string name) => name switch
    {
        "LightBlue" => Colors.LightBlue,
        "LightCyan" => Colors.LightCyan,
        "LightGreen" => Colors.LightGreen,
        "LightYellow" => Colors.LightYellow,
        "LightMagenta" => Colors.LightMagenta,
        "LightRed" => Colors.LightRed,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
