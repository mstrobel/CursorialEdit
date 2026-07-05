using Cursorial.Drawing.Media;
using Cursorial.Output;

using CursorialEdit.Layout;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// The M2.WP7 presenter styling vocabulary (§2.1 / §18.3): the color + weight/attribute each rendered
/// construct carries, emitted <b>directly</b> as palette colors and <see cref="TextAttributes"/> so
/// <see cref="Cursorial.Rendering.StyleQuantizer"/> (inside <c>FrameRenderer</c>) degrades them per
/// color tier with zero app branching — a heading keeps its weight/underline on
/// <c>caps-nocolor</c> even as its color drops. Colors are palette entries (<see cref="Colors"/>) so
/// they survive the <c>Ansi16Legacy</c> wire; the WP11 <c>Md.*</c> theme layer will later hand these
/// through <c>ThemeDictionaries</c>, but WP7 emits them literally (the plan's "just emit the
/// color+attribute").
/// </summary>
internal static class MarkdownStyles
{
    /// <summary>SGR 2 (faint) — revealed syntax marks and the continuation indicators. Degrades to nothing on tiers without it while the glyph keeps its cell.</summary>
    public static readonly CellStyle Dim = CellStyle.Default.WithAttributes(TextAttributes.Faint);

    /// <summary>The code-fill background (a muted grey that survives the 16-color wire; drops on NoColor).</summary>
    public static readonly Color CodeFillColor = Colors.LightBlack;

    /// <summary>The code-fill background brush.</summary>
    public static readonly IBrush CodeFillBrush = new SolidColorBrush(CodeFillColor);

    /// <summary>The opening-fence language label — a readable colour on the fill (not <see cref="CodeFillColor"/>, which would render it invisible).</summary>
    public static readonly IBrush CodeLabelBrush = new SolidColorBrush(Colors.White);

    /// <summary>The blockquote <c>▌</c> bar color.</summary>
    public static readonly IBrush QuoteBarBrush = new SolidColorBrush(Colors.LightBlack);

    /// <summary>The list bullet / ordered-number marker color.</summary>
    public static readonly IBrush MarkerBrush = new SolidColorBrush(Colors.LightYellow);

    /// <summary>A horizontal rule's box-drawing color.</summary>
    public static readonly IBrush RuleBrush = new SolidColorBrush(Colors.LightBlack);

    /// <summary>The dim "front matter" foreground.</summary>
    public static readonly IBrush FrontMatterBrush = new SolidColorBrush(Colors.LightBlack);

    // Distinct color + weight per heading level (§2.1). H1/H2 also underline, mirroring their setext
    // form, so the six levels stay distinguishable by weight+underline once color degrades.
    private static readonly (Color Color, TextAttributes Attributes)[] Headings =
    [
        (Colors.LightBlue, TextAttributes.Bold | TextAttributes.Underline), // H1
        (Colors.LightCyan, TextAttributes.Bold | TextAttributes.Underline), // H2
        (Colors.LightGreen, TextAttributes.Bold),                            // H3
        (Colors.LightYellow, TextAttributes.Bold),                           // H4
        (Colors.LightMagenta, TextAttributes.Bold),                          // H5
        (Colors.LightRed, TextAttributes.Bold),                              // H6
    ];

    private static readonly IBrush[] HeadingBrushes =
        [.. Headings.Select(h => (IBrush) new SolidColorBrush(h.Color))];

    /// <summary>The distinct foreground brush for heading <paramref name="level"/> (1–6, clamped).</summary>
    public static IBrush HeadingBrush(int level) => HeadingBrushes[ClampLevel(level)];

    /// <summary>The distinct weight/underline for heading <paramref name="level"/> (1–6, clamped).</summary>
    public static TextAttributes HeadingAttributes(int level) => Headings[ClampLevel(level)].Attributes;

    private static int ClampLevel(int level) => Math.Clamp(level, 1, 6) - 1;

    /// <summary>The <see cref="TextAttributes"/> an inline <see cref="RunStyle"/> contributes (§2.1).</summary>
    public static TextAttributes AttributesFor(RunStyle style)
    {
        var attributes = TextAttributes.None;
        if ((style & RunStyle.Bold) != 0)
            attributes |= TextAttributes.Bold;
        if ((style & RunStyle.Italic) != 0)
            attributes |= TextAttributes.Italic;
        if ((style & RunStyle.Strikethrough) != 0)
            attributes |= TextAttributes.Strikethrough;
        if ((style & RunStyle.Link) != 0)
            attributes |= TextAttributes.Underline; // links render underlined (§2.1); StyleQuantizer gates it
        return attributes;
    }
}
