using System.Reflection;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;

using CursorialEdit.Presenters;

namespace CursorialEdit.Themes;

/// <summary>
/// The app-owned <c>Md.*</c> theme dictionary (architecture §2.3 / implementation-plan §7 WP11): a
/// code-first <see cref="ResourceDictionary"/> whose <see cref="ResourceDictionary.ThemeDictionaries"/>
/// carry the markdown-presenter token values, keyed by <see cref="ThemeVariantKey"/> for the three
/// color tiers. It is assigned to <see cref="UIApplication.Theme"/> so it <b>layers over</b> the code-first
/// <see cref="Cursorial.UI.Themes.CursorialTheme.BuiltIn"/> backstop through the framework lookup chain
/// (<c>Resources → Theme → BuiltIn</c>) — the app therefore depends on <b>no</b> unpackaged
/// <c>Cursorial.UI.Themes</c> assembly (FB-7); <c>Palette.xaml</c> there is an authoring reference only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three tiers</b> (base-wildcard keys — the markdown palette is base-agnostic, so <c>(·,Tier)</c>
/// serves both Dark and Light; probe descent per <c>ThemeVariantProbe</c>):
/// </para>
/// <list type="bullet">
/// <item><b>Base — <c>(·,Ansi256)</c></b>: the authored colors, served at Truecolor + Ansi256 by descent.
/// Byte-identical to WP7's hardcoded palette (the regression contract), authored from
/// <see cref="MarkdownStyles"/>'s shared default constants so there is no drift.</item>
/// <item><b><c>(·,Ansi16)</c></b>: the hand-picked nearest 16-color. The WP7 palette is already ANSI-16
/// (<c>LightBlue</c>…), so the nearest-16 pick equals the base value — Ansi16Legacy renders identically;
/// the well scrim keeps its RGBA (the quantizer reduces it, a partial-color background).</item>
/// <item><b><c>(·,NoColor)</c></b>: every color role collapses to <see cref="Colors.Default"/> (no stranded
/// RGB, mirroring BuiltIn), and the <b>attribute</b> tokens (heading weight/underline, the mark faint)
/// live here at the tier floor every descent reaches — so a heading still reads as a heading on
/// <c>caps-nocolor</c> (bold/underline), the <see cref="StyleQuantizer"/> having dropped the color.</item>
/// </list>
/// </remarks>
internal static class MdTheme
{
    /// <summary>The user-option key prefix for the FW-A <c>Md.*</c> token override seam (§4.3).</summary>
    internal const string OverrideKeyPrefix = "theme.md.";

    /// <summary>Builds a fresh, unsealed <c>Md.*</c> theme dictionary (one per application — acquired by its owner).</summary>
    public static ResourceDictionary Create()
    {
        var dict = new ResourceDictionary();

        // Base tier: authored colors, serving Truecolor + Ansi256 by descent (CD8).
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.Ansi256)] = ColorTier();

        // Ansi16 tier: hand-picked nearest 16-color (the WP7 palette is already 16-safe, so identical).
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.Ansi16)] = ColorTier();

        // NoColor floor: colors → Default; attribute tokens authored here (every tier's descent reaches it).
        dict.ThemeDictionaries[new ThemeVariantKey(null, ColorDepth.NoColor)] = NoColorTier();

        return dict;
    }

    // ── the color-bearing tier (Ansi256 base + Ansi16), authored from the shared defaults ──
    private static ResourceDictionary ColorTier()
    {
        var d = new ResourceDictionary
        {
            [MdThemeKeys.CodeFill] = Brush(MarkdownStyles.CodeFillColor),
            [MdThemeKeys.CodeLabel] = Brush(MarkdownStyles.CodeLabelColor),
            [MdThemeKeys.CodeKeyword] = Brush(MarkdownStyles.CodeKeywordColor),
            [MdThemeKeys.CodeString] = Brush(MarkdownStyles.CodeStringColor),
            [MdThemeKeys.CodeComment] = Brush(MarkdownStyles.CodeCommentColor),
            [MdThemeKeys.CodeNumber] = Brush(MarkdownStyles.CodeNumberColor),
            [MdThemeKeys.QuoteBar] = Brush(MarkdownStyles.QuoteBarColor),
            [MdThemeKeys.ListMarker] = Brush(MarkdownStyles.ListMarkerColor),
            [MdThemeKeys.Rule] = Brush(MarkdownStyles.RuleColor),
            [MdThemeKeys.FrontMatter] = Brush(MarkdownStyles.FrontMatterColor),
            [MdThemeKeys.ActiveWell] = Brush(MarkdownStyles.ActiveWellColor),
            [MdThemeKeys.RawMarkStructure] = Brush(MarkdownStyles.RawStructureColor),
            [MdThemeKeys.RawMarkCode] = Brush(MarkdownStyles.RawCodeColor),
        };

        for (var level = 1; level <= 6; level++)
            d[MdThemeKeys.Heading(level)] = Brush(MarkdownStyles.HeadingDefaults[level - 1].Color);

        return d;
    }

    // ── the NoColor floor: colors → Default; attribute tokens (reachable from every tier's descent) ──
    private static ResourceDictionary NoColorTier()
    {
        var defaultBrush = new SolidColorBrush(Colors.Default);
        var d = new ResourceDictionary
        {
            [MdThemeKeys.CodeFill] = defaultBrush,
            [MdThemeKeys.CodeLabel] = defaultBrush,
            [MdThemeKeys.CodeKeyword] = defaultBrush,
            [MdThemeKeys.CodeString] = defaultBrush,
            [MdThemeKeys.CodeComment] = defaultBrush,
            [MdThemeKeys.CodeNumber] = defaultBrush,
            [MdThemeKeys.QuoteBar] = defaultBrush,
            [MdThemeKeys.ListMarker] = defaultBrush,
            [MdThemeKeys.Rule] = defaultBrush,
            [MdThemeKeys.FrontMatter] = defaultBrush,
            [MdThemeKeys.ActiveWell] = defaultBrush,
            [MdThemeKeys.RawMarkStructure] = defaultBrush,
            [MdThemeKeys.RawMarkCode] = defaultBrush,

            // Attribute tokens — the redundant non-color channel (§18.3). Boxed TextAttributes, resolved
            // by MarkdownStyles via TryFindResource → the collapse-to-attribute contract for caps-nocolor.
            [MdThemeKeys.Mark] = MarkdownStyles.MarkAttributes,
        };

        for (var level = 1; level <= 6; level++)
        {
            d[MdThemeKeys.Heading(level)] = defaultBrush;
            d[MdThemeKeys.HeadingAttributes(level)] = MarkdownStyles.HeadingDefaults[level - 1].Attributes;
        }

        return d;
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    // ───────────────────────────── installation + FW-A override seam ─────────────────────────────

    /// <summary>
    /// Installs the <c>Md.*</c> theme on <paramref name="application"/> (idempotent, dispatcher-thread-safe):
    /// assigns <see cref="UIApplication.Theme"/> if unset, then applies any FW-A user-config token overrides.
    /// A no-op off the dispatcher thread or when <paramref name="application"/> is null (the per-token
    /// fallback in <see cref="MarkdownStyles"/> then covers rendering).
    /// </summary>
    public static void EnsureInstalled(UIApplication? application)
    {
        if (application is null || !application.Dispatcher.CheckAccess())
            return;

        application.Theme ??= Create();
        ApplyUserOverrides(application);
    }

    /// <summary>
    /// The FW-A override seam (§4.3): reads <c>theme.md.*</c> keys from the application's user-configuration
    /// store (<see cref="UIApplication.UserOptions"/>) and writes the parsed brushes into
    /// <see cref="UIApplication.Resources"/> — the chain hop <b>above</b> <see cref="UIApplication.Theme"/>,
    /// so a user override wins over the authored token while BuiltIn still fills every gap. A variant-agnostic
    /// override (a top-level Resources entry) so it applies on every tier the user is on.
    /// </summary>
    public static void ApplyUserOverrides(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (application.UserOptions is not { } store)
            return;

        foreach (var (optionKey, resourceKey) in OverridableTokens)
        {
            if (store.GetString(optionKey) is { Length: > 0 } raw && TryParseColor(raw, out var color))
                application.Resources[resourceKey] = new SolidColorBrush(color);
        }
    }

    // The overridable color tokens: (user-option key → Md.* resource key). Attribute tokens are not
    // user-overridable in v1 (color is the axis a user re-themes).
    private static readonly (string OptionKey, string ResourceKey)[] OverridableTokens = BuildOverrideMap();

    private static (string, string)[] BuildOverrideMap()
    {
        var map = new List<(string, string)>
        {
            (OverrideKeyPrefix + "code.fill", MdThemeKeys.CodeFill),
            (OverrideKeyPrefix + "code.label", MdThemeKeys.CodeLabel),
            (OverrideKeyPrefix + "code.keyword", MdThemeKeys.CodeKeyword),
            (OverrideKeyPrefix + "code.string", MdThemeKeys.CodeString),
            (OverrideKeyPrefix + "code.comment", MdThemeKeys.CodeComment),
            (OverrideKeyPrefix + "code.number", MdThemeKeys.CodeNumber),
            (OverrideKeyPrefix + "quote.bar", MdThemeKeys.QuoteBar),
            (OverrideKeyPrefix + "list.marker", MdThemeKeys.ListMarker),
            (OverrideKeyPrefix + "rule", MdThemeKeys.Rule),
            (OverrideKeyPrefix + "frontmatter", MdThemeKeys.FrontMatter),
            (OverrideKeyPrefix + "activewell", MdThemeKeys.ActiveWell),
            (OverrideKeyPrefix + "rawmark.structure", MdThemeKeys.RawMarkStructure),
            (OverrideKeyPrefix + "rawmark.code", MdThemeKeys.RawMarkCode),
        };

        for (var level = 1; level <= 6; level++)
            map.Add(($"{OverrideKeyPrefix}heading.{level}", MdThemeKeys.Heading(level)));

        return [.. map];
    }

    /// <summary>
    /// Parses a config color value: a <c>#rgb</c>/<c>#rrggbb</c>/<c>#aarrggbb</c> hex, a <c>palette:N</c>
    /// index, or a named <see cref="Colors"/> entry (e.g. <c>LightRed</c>, case-insensitive).
    /// </summary>
    internal static bool TryParseColor(string text, out Color color)
    {
        color = Colors.Default;
        text = text.Trim();
        if (text.Length == 0)
            return false;

        if (text[0] == '#')
        {
            try
            {
                color = Color.FromHex(text);
                return true;
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                // Color.FromHex throws ArgumentException for a malformed/wrong-length hex (e.g. "#12",
                // "#gggggg", an 8-digit value) and FormatException for bad digits — both mean "not a color".
                // A bad user-config override must fall back to the authored token, never crash startup.
                return false;
            }
        }

        if (text.StartsWith("palette:", StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(text.AsSpan("palette:".Length), out var index))
        {
            color = Color.FromPalette(index);
            return true;
        }

        // A named Colors.* entry (the same names the authoring palette and tests use).
        var field = typeof(Colors).GetField(text, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
        if (field is { } && field.FieldType == typeof(Color) && field.GetValue(null) is Color named)
        {
            color = named;
            return true;
        }

        return false;
    }
}
