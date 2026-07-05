using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;

using CursorialEdit.Layout;
using CursorialEdit.Themes;

using CellStyle = Cursorial.Output.Style;

namespace CursorialEdit.Presenters;

/// <summary>
/// The M2.WP7/WP11 presenter styling vocabulary (§2.1 / §18.3): the color + weight/attribute each
/// rendered construct carries. WP11 moved the values out of hardcoded literals and into the
/// <b><c>Md.*</c> theme tokens</b> (<see cref="MdThemeKeys"/>, authored per color tier in
/// <see cref="MdTheme"/>): every getter resolves its token through the framework lookup chain
/// (<see cref="ResourceExtensions.TryFindResource(UIElement,object,out object?)"/> — the same mechanism
/// <c>ThemeKeys.SelectionBrush</c> uses in <c>LeafBlockPresenter.ResolveSelection</c>) against the calling
/// presenter element, so the value follows <c>UIApplication.ActualThemeVariant</c> and honors any
/// nearer-scope override (the FW-A seam).
/// </summary>
/// <remarks>
/// <para>
/// The token <b>defaults are byte-identical</b> to WP7's pre-token hardcoded values: the constants below
/// are the single source of truth, consumed BOTH as the per-token fallback (when no theme is wired) AND
/// by <see cref="MdTheme"/> when it authors the Base/Ansi16 tiers — so default rendering is unchanged
/// (the regression contract; proven by the existing <c>PresenterRenderTests</c>/<c>MarkdownRenderTests</c>).
/// </para>
/// <para>
/// Values are emitted as palette colors + <see cref="TextAttributes"/> so
/// <see cref="Cursorial.Output.StyleQuantizer"/> (inside <c>FrameRenderer</c>) degrades them per color
/// tier with zero app branching — a heading keeps its weight/underline on <c>caps-nocolor</c> even as
/// its color collapses to Default (the NoColor tier authors the color as Default and keeps the
/// attribute token, so the collapse is explicit as well as quantizer-driven).
/// </para>
/// </remarks>
internal static class MarkdownStyles
{
    // ───────────────────────────── default token values (the shared source of truth) ─────────────────────────────

    /// <summary>SGR 2 (faint) — the revealed-syntax-mark / continuation-indicator attribute (<c>Md.Mark</c>).</summary>
    public static readonly TextAttributes MarkAttributes = TextAttributes.Faint;

    /// <summary>The <c>:active-block</c> well tint color (§4.3): a translucent scrim (low alpha) blended over prose.</summary>
    public static readonly Color ActiveWellColor = Color.FromRgba(94, 118, 171, 42);

    /// <summary>The code-fill background color (a muted grey that survives the 16-color wire; collapses on NoColor).</summary>
    public static readonly Color CodeFillColor = Colors.LightBlack;

    /// <summary>The opening-fence language-label color — readable on the fill (never the fill color, which would hide it).</summary>
    public static readonly Color CodeLabelColor = Colors.White;

    /// <summary>The blockquote <c>▌</c> bar color.</summary>
    public static readonly Color QuoteBarColor = Colors.LightBlack;

    /// <summary>The list bullet / ordered-number marker color.</summary>
    public static readonly Color ListMarkerColor = Colors.LightYellow;

    /// <summary>A horizontal rule's box-drawing color.</summary>
    public static readonly Color RuleColor = Colors.LightBlack;

    /// <summary>The dim "front matter" foreground.</summary>
    public static readonly Color FrontMatterColor = Colors.LightBlack;

    /// <summary>The <see cref="MiniHighlighter"/> keyword token color.</summary>
    public static readonly Color CodeKeywordColor = Colors.LightBlue;

    /// <summary>The <see cref="MiniHighlighter"/> string token color.</summary>
    public static readonly Color CodeStringColor = Colors.LightGreen;

    /// <summary>The <see cref="MiniHighlighter"/> comment token color (readable on the fill — not the fill color).</summary>
    public static readonly Color CodeCommentColor = Colors.White;

    /// <summary>The <see cref="MiniHighlighter"/> number token color.</summary>
    public static readonly Color CodeNumberColor = Colors.LightCyan;

    /// <summary>The raw-view structural-mark color (<see cref="RawMarkdownHighlighter"/> keyword class).</summary>
    public static readonly Color RawStructureColor = Colors.LightBlue;

    /// <summary>The raw-view inline-code color (<see cref="RawMarkdownHighlighter"/> string class).</summary>
    public static readonly Color RawCodeColor = Colors.LightGreen;

    /// <summary>The distinct (color, weight/underline) per heading level (§2.1). H1/H2 also underline, mirroring their setext form.</summary>
    internal static readonly (Color Color, TextAttributes Attributes)[] HeadingDefaults =
    [
        (Colors.LightBlue, TextAttributes.Bold | TextAttributes.Underline), // H1
        (Colors.LightCyan, TextAttributes.Bold | TextAttributes.Underline), // H2
        (Colors.LightGreen, TextAttributes.Bold),                            // H3
        (Colors.LightYellow, TextAttributes.Bold),                           // H4
        (Colors.LightMagenta, TextAttributes.Bold),                          // H5
        (Colors.LightRed, TextAttributes.Bold),                              // H6
    ];

    // Cached fallback brushes (used only when no Md theme resolves — e.g. a detached test context): keeps
    // the miss path allocation-free and identical to WP7's static brushes.
    private static readonly IBrush FallbackActiveWell = new SolidColorBrush(ActiveWellColor);
    private static readonly IBrush FallbackCodeFill = new SolidColorBrush(CodeFillColor);
    private static readonly IBrush FallbackCodeLabel = new SolidColorBrush(CodeLabelColor);
    private static readonly IBrush FallbackQuoteBar = new SolidColorBrush(QuoteBarColor);
    private static readonly IBrush FallbackListMarker = new SolidColorBrush(ListMarkerColor);
    private static readonly IBrush FallbackRule = new SolidColorBrush(RuleColor);
    private static readonly IBrush FallbackFrontMatter = new SolidColorBrush(FrontMatterColor);
    private static readonly IBrush FallbackCodeKeyword = new SolidColorBrush(CodeKeywordColor);
    private static readonly IBrush FallbackCodeString = new SolidColorBrush(CodeStringColor);
    private static readonly IBrush FallbackCodeComment = new SolidColorBrush(CodeCommentColor);
    private static readonly IBrush FallbackCodeNumber = new SolidColorBrush(CodeNumberColor);
    private static readonly IBrush FallbackRawStructure = new SolidColorBrush(RawStructureColor);
    private static readonly IBrush FallbackRawCode = new SolidColorBrush(RawCodeColor);
    private static readonly IBrush[] FallbackHeadingBrushes =
        [.. HeadingDefaults.Select(h => (IBrush) new SolidColorBrush(h.Color))];

    // ───────────────────────────── token resolution ─────────────────────────────

    private static IBrush Resolve(UIElement element, string key, IBrush fallback)
        => element.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    private static TextAttributes ResolveAttributes(UIElement element, string key, TextAttributes fallback)
        => element.TryFindResource(key, out var value) && value is TextAttributes attributes ? attributes : fallback;

    /// <summary>The revealed-syntax-mark dim style (<c>Md.Mark</c> attribute) — faint, degrading to nothing on tiers without it while the glyph keeps its cell.</summary>
    public static CellStyle Dim(UIElement element)
        => CellStyle.Default.WithAttributes(ResolveAttributes(element, MdThemeKeys.Mark, MarkAttributes));

    /// <summary>The <c>:active-block</c> well brush (<c>Md.ActiveWell</c>).</summary>
    public static IBrush ActiveWellBrush(UIElement element) => Resolve(element, MdThemeKeys.ActiveWell, FallbackActiveWell);

    /// <summary>The code-fill background brush (<c>Md.Code.Fill</c>).</summary>
    public static IBrush CodeFillBrush(UIElement element) => Resolve(element, MdThemeKeys.CodeFill, FallbackCodeFill);

    /// <summary>The opening-fence language-label brush (<c>Md.Code.Label</c>).</summary>
    public static IBrush CodeLabelBrush(UIElement element) => Resolve(element, MdThemeKeys.CodeLabel, FallbackCodeLabel);

    /// <summary>The blockquote bar brush (<c>Md.Quote.Bar</c>).</summary>
    public static IBrush QuoteBarBrush(UIElement element) => Resolve(element, MdThemeKeys.QuoteBar, FallbackQuoteBar);

    /// <summary>The list marker brush (<c>Md.List.Marker</c>).</summary>
    public static IBrush MarkerBrush(UIElement element) => Resolve(element, MdThemeKeys.ListMarker, FallbackListMarker);

    /// <summary>The horizontal-rule brush (<c>Md.Rule</c>).</summary>
    public static IBrush RuleBrush(UIElement element) => Resolve(element, MdThemeKeys.Rule, FallbackRule);

    /// <summary>The front-matter foreground brush (<c>Md.FrontMatter</c>).</summary>
    public static IBrush FrontMatterBrush(UIElement element) => Resolve(element, MdThemeKeys.FrontMatter, FallbackFrontMatter);

    /// <summary>The distinct foreground brush for heading <paramref name="level"/> (1–6, clamped) — <c>Md.Heading.N</c>.</summary>
    public static IBrush HeadingBrush(UIElement element, int level)
        => Resolve(element, MdThemeKeys.Heading(level), FallbackHeadingBrushes[ClampLevel(level)]);

    /// <summary>The distinct weight/underline for heading <paramref name="level"/> (1–6, clamped) — <c>Md.Heading.N.Attributes</c>.</summary>
    public static TextAttributes HeadingAttributes(UIElement element, int level)
        => ResolveAttributes(element, MdThemeKeys.HeadingAttributes(level), HeadingDefaults[ClampLevel(level)].Attributes);

    /// <summary>The <see cref="MiniHighlighter"/> code-token brush for <paramref name="tokenClass"/> (<c>Md.Code.*</c>).</summary>
    public static IBrush CodeTokenBrush(UIElement element, CodeTokenClass tokenClass) => tokenClass switch
    {
        CodeTokenClass.Keyword => Resolve(element, MdThemeKeys.CodeKeyword, FallbackCodeKeyword),
        CodeTokenClass.String => Resolve(element, MdThemeKeys.CodeString, FallbackCodeString),
        CodeTokenClass.Comment => Resolve(element, MdThemeKeys.CodeComment, FallbackCodeComment),
        CodeTokenClass.Number => Resolve(element, MdThemeKeys.CodeNumber, FallbackCodeNumber),
        _ => Resolve(element, MdThemeKeys.CodeKeyword, FallbackCodeKeyword),
    };

    /// <summary>The raw-view mark brush for <paramref name="tokenClass"/> (<c>Md.RawMark.*</c>): structural marks (keyword) vs inline code (string).</summary>
    public static IBrush RawMarkBrush(UIElement element, CodeTokenClass tokenClass) => tokenClass switch
    {
        CodeTokenClass.String => Resolve(element, MdThemeKeys.RawMarkCode, FallbackRawCode),
        _ => Resolve(element, MdThemeKeys.RawMarkStructure, FallbackRawStructure),
    };

    private static int ClampLevel(int level) => Math.Clamp(level, 1, 6) - 1;

    /// <summary>The <see cref="TextAttributes"/> an inline <see cref="RunStyle"/> contributes (§2.1) — a pure style→attribute mapping (no theme token).</summary>
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
