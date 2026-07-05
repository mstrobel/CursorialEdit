using Cursorial.UI;

namespace CursorialEdit.Icons;

/// <summary>
/// The editor's glyph-request seam (spec §18.4) — the imperative-presenter analogue of the framework
/// <c>Icon</c> element: features <b>request a glyph by role</b> and inherit the capability tier ladder
/// (<c>caps-nerdfont</c> Nerd Font PUA → <c>caps-emoji</c> emoji → <c>caps-unicode</c> Unicode floor)
/// rather than hardcoding a literal char. Because the leaf-block presenters draw through
/// <c>RenderContext.DrawText</c> (not a control template), they cannot host a heavyweight <c>Icon</c>
/// <c>Control</c>; this resolves the same ladder to a <b>glyph string</b> against
/// <see cref="UIApplication.Current"/>'s negotiated/overridden capabilities.
/// </summary>
/// <remarks>
/// v1 pins <b>no</b> Nerd Font codepoints for these editor marks (the icon ledger lists the fold chevrons
/// and the <c>↵</c> hard break as Unicode-floor glyphs), so resolution falls to the Unicode floor — the
/// same literal char WP7 drew — and rendering is byte-identical. The seam exists so a later ledger row can
/// pin a PUA glyph without touching the presenters. Emoji resolves only when the app has opted into
/// <c>caps-emoji</c> (FB-15) AND the role supplies one; none do yet.
/// </remarks>
internal readonly record struct EditorGlyph(string Unicode, string? NerdFont = null, string? Emoji = null)
{
    /// <summary>The front-matter <b>folded</b> chevron (▸) — collapsed metadata summary affordance.</summary>
    public static readonly EditorGlyph FrontMatterCollapsed = new("▸"); // ▸

    /// <summary>The front-matter <b>expanded</b> chevron (▾) — the collapse affordance.</summary>
    public static readonly EditorGlyph FrontMatterExpanded = new("▾"); // ▾

    /// <summary>The hard-line-break affordance (↵) shown on the active line (§2.1).</summary>
    public static readonly EditorGlyph HardBreak = new("↵"); // ↵

    /// <summary>
    /// The glyph string resolved against <see cref="UIApplication.Current"/>'s capability ladder:
    /// Nerd Font (when <see cref="UIApplication.NerdFontAvailable"/> and a PUA glyph is pinned), else
    /// emoji (when <see cref="UIApplication.EmojiAvailable"/> and one is supplied), else the Unicode floor.
    /// Falls to the Unicode floor when no application is current (a detached context).
    /// </summary>
    public string Resolve() => Resolve(UIApplication.Current);

    /// <summary>The tier resolution against an explicit <paramref name="application"/> (testable without ambient state).</summary>
    public string Resolve(UIApplication? application)
    {
        if (application is { NerdFontAvailable: true } && NerdFont is { Length: > 0 } nf)
            return nf;
        if (application is { EmojiAvailable: true } && Emoji is { Length: > 0 } emoji)
            return emoji;
        return Unicode;
    }
}
