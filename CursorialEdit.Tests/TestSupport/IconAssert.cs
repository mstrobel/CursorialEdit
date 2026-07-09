using System.Linq;

using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

using Xunit;

namespace CursorialEdit.Tests.TestSupport;

/// <summary>
/// Shared guard for the editor's tiered command icons (<see cref="Icon"/>) — used by both <c>RibbonTests</c> and
/// <c>ContextBarTests</c> so the ribbon and the right-click MiniToolbar assert the SAME contract. Every button's
/// icon must carry:
/// <list type="bullet">
/// <item>a single Nerd-Font-PUA <see cref="Icon.Glyph"/> codepoint — the <c>nf-md-*</c> icon, preferred when Nerd
/// Font is available — with <see cref="Icon.GlyphWidth"/> 1; and</item>
/// <item>a width-1, no-VS16, text-presentation <see cref="Icon.Text"/> floor — the guaranteed single-cell Unicode
/// fallback the toolbar had before the Nerd Font tier landed. (The opt-in <see cref="Icon.Emoji"/> tier is allowed
/// to be a 2-wide color sprite — the width-1 discipline is the Text floor's alone.)</item>
/// </list>
/// </summary>
internal static class IconAssert
{
    // Nerd Font packs its glyphs into two Private-Use ranges: the BMP PUA (legacy) and the plane-15 PUA-A, where the
    // nf-md-* Material Design set lives in current Nerd Fonts (v3: U+F0000–U+F1AF0).
    private static bool IsNerdFontPua(int codepoint)
        => codepoint is >= 0xE000 and <= 0xF8FF        // BMP Private Use Area
        || codepoint is >= 0xF0000 and <= 0xFFFFD;     // Supplementary Private Use Area-A

    /// <summary>Asserts <paramref name="icon"/> is a tiered Nerd Font icon carrier: one PUA <see cref="Icon.Glyph"/>
    /// codepoint (<see cref="Icon.GlyphWidth"/> 1) over a width-1, no-VS16 <see cref="Icon.Text"/> floor.</summary>
    public static void NerdFontOverWidthOneFloor(IconCarrier icon)
    {
        // Glyph tier: exactly one Nerd-Font-PUA codepoint, budgeted at one cell.
        Assert.False(string.IsNullOrEmpty(icon.Glyph), "the Nerd Font Glyph tier must be set");
        var runes = icon.Glyph!.EnumerateRunes().ToArray();
        Assert.Single(runes);                                                                     // one codepoint
        Assert.True(IsNerdFontPua(runes[0].Value), $"Glyph U+{runes[0].Value:X} is outside the Nerd Font PUA ranges");
        Assert.Equal(1, icon.GlyphWidth);

        // Text floor: the guaranteed single-cell, text-presentation fallback (never a 2-wide color-emoji sprite).
        Assert.False(string.IsNullOrEmpty(icon.Text), "the width-1 Text floor must be set");
        Assert.Equal(1, GraphemeWidth.ClusterCount(icon.Text!));                                  // one grapheme cluster
        Assert.Equal(1, GraphemeWidth.StringWidth(icon.Text!));                                   // …occupying exactly one cell
        foreach (var rune in icon.Text!.EnumerateRunes())
        {
            Assert.NotEqual(0xFE0F, rune.Value);                                                  // no VS16 emoji-presentation selector
            Assert.Equal(1, GraphemeWidth.CodepointWidth(rune));                                  // every codepoint is width-1
        }
    }
}
