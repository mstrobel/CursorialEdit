namespace CursorialEdit.Themes;

/// <summary>
/// The <c>Md.*</c> theme-token resource keys (architecture §2.3 / implementation-plan §7 WP11) — the
/// app-owned parallel to the framework's <c>Theme.*</c> spine (<c>Cursorial.UI.Themes.ThemeKeys</c>).
/// Every color/attribute the markdown presenters render is addressed by one of these string keys and
/// resolved through the framework lookup chain (<c>element → … → UIApplication.Resources →
/// UIApplication.Theme → CursorialTheme.BuiltIn</c>), exactly as <c>ThemeKeys.SelectionBrush</c> is —
/// so the value is authored once in <see cref="MdTheme"/> (per color tier) and a user config can
/// override any token at a nearer chain scope (the FW-A seam, <see cref="MdTheme.ApplyUserOverrides"/>).
/// </summary>
/// <remarks>
/// Keys naming the <b>foreground/fill brush</b> resolve to an <see cref="Cursorial.Drawing.Media.IBrush"/>;
/// the <c>*.Attributes</c> and <see cref="Mark"/> keys resolve to a boxed
/// <see cref="Cursorial.Output.TextAttributes"/> (the same pattern the framework uses for
/// <c>ThemeKeys.InteractiveInverseAttributes</c>). Attribute tokens are authored at the NoColor tier
/// floor so every color tier's descent reaches them (see <see cref="MdTheme"/>).
/// </remarks>
internal static class MdThemeKeys
{
    // ── Headings (§2.1): a brush + a TextAttributes per level; the attribute survives caps-nocolor so a
    //    heading still reads as a heading (weight/underline) once color degrades. ──
    public const string Heading1 = "Md.Heading.1";
    public const string Heading2 = "Md.Heading.2";
    public const string Heading3 = "Md.Heading.3";
    public const string Heading4 = "Md.Heading.4";
    public const string Heading5 = "Md.Heading.5";
    public const string Heading6 = "Md.Heading.6";

    public const string Heading1Attributes = "Md.Heading.1.Attributes";
    public const string Heading2Attributes = "Md.Heading.2.Attributes";
    public const string Heading3Attributes = "Md.Heading.3.Attributes";
    public const string Heading4Attributes = "Md.Heading.4.Attributes";
    public const string Heading5Attributes = "Md.Heading.5.Attributes";
    public const string Heading6Attributes = "Md.Heading.6.Attributes";

    // ── Code blocks + the MiniHighlighter token colors (§2.1). ──
    public const string CodeFill = "Md.Code.Fill";
    public const string CodeLabel = "Md.Code.Label";
    public const string CodeKeyword = "Md.Code.Keyword";
    public const string CodeString = "Md.Code.String";
    public const string CodeComment = "Md.Code.Comment";
    public const string CodeNumber = "Md.Code.Number";

    // ── Structural decoration. ──
    public const string QuoteBar = "Md.Quote.Bar";
    public const string ListMarker = "Md.List.Marker";
    public const string Rule = "Md.Rule";
    public const string FrontMatter = "Md.FrontMatter";

    /// <summary>The <c>:active-block</c> well tint (§4.3) — a translucent scrim brush.</summary>
    public const string ActiveWell = "Md.ActiveWell";

    /// <summary>The revealed-syntax-mark dim (SGR faint) — a <see cref="Cursorial.Output.TextAttributes"/> value.</summary>
    public const string Mark = "Md.Mark";

    // ── Raw-source view token colors (M2.WP10): the structural marks and inline-code spans of a literal
    //    markdown line (RawMarkdownHighlighter). Distinct keys from the code-block tokens so raw view and
    //    fenced code can be re-themed independently. ──
    public const string RawMarkStructure = "Md.RawMark.Structure";
    public const string RawMarkCode = "Md.RawMark.Code";

    /// <summary>The <c>Md.Heading.N</c> brush key for level <paramref name="level"/> (1–6, clamped).</summary>
    public static string Heading(int level) => Clamp(level) switch
    {
        1 => Heading1,
        2 => Heading2,
        3 => Heading3,
        4 => Heading4,
        5 => Heading5,
        _ => Heading6,
    };

    /// <summary>The <c>Md.Heading.N.Attributes</c> key for level <paramref name="level"/> (1–6, clamped).</summary>
    public static string HeadingAttributes(int level) => Clamp(level) switch
    {
        1 => Heading1Attributes,
        2 => Heading2Attributes,
        3 => Heading3Attributes,
        4 => Heading4Attributes,
        5 => Heading5Attributes,
        _ => Heading6Attributes,
    };

    private static int Clamp(int level) => Math.Clamp(level, 1, 6);
}

/// <summary>
/// The presenter <b>style classes</b> (architecture §2.3 / spec §18.2): the <c>md-*</c> classes the
/// leaf-block presenters expose on their <see cref="Cursorial.UI.UIElement.Classes"/> so the rendered
/// constructs are addressable by the framework's style-class mechanism — the same selector spine
/// <c>caps-*</c> degradation keys off. The presenters resolve their <c>Md.*</c> tokens by resource key
/// (the idiomatic path for imperative <c>DrawText</c> presenters); the classes carry the §18 selector
/// addressability and name which token a presenter consumes (<c>md-h1 → Md.Heading.1</c>).
/// </summary>
internal static class MdStyleClasses
{
    public const string Heading1 = "md-h1";
    public const string Heading2 = "md-h2";
    public const string Heading3 = "md-h3";
    public const string Heading4 = "md-h4";
    public const string Heading5 = "md-h5";
    public const string Heading6 = "md-h6";
    public const string Code = "md-code";
    public const string Quote = "md-quote";
    public const string FrontMatter = "md-frontmatter";

    // (No md-mark class: a revealed mark is an inline RUN within a presenter, not a whole-presenter kind,
    // so there is no element to carry the class — it is styled via the Md.Mark resource token directly.)

    /// <summary>The <c>md-hN</c> class for heading level <paramref name="level"/> (1–6, clamped).</summary>
    public static string Heading(int level) => Math.Clamp(level, 1, 6) switch
    {
        1 => Heading1,
        2 => Heading2,
        3 => Heading3,
        4 => Heading4,
        5 => Heading5,
        _ => Heading6,
    };
}
