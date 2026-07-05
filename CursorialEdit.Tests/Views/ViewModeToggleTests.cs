using Cursorial.Input;

using CursorialEdit.Presenters;
using CursorialEdit.Tests.Editing;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.Views;

/// <summary>
/// M2.WP10 — the raw-source view-mode toggle on the shell surface (architecture Decision 12). Ctrl+/
/// (Kitty wire) and Alt+/ (the legacy-safe alternate — the legacy wire decodes Ctrl+/ to the ignored
/// 0x1F byte, FB-14.1) toggle the whole surface between formatted WYSIWYG and verbatim raw source;
/// toggling preserves the caret's source anchor (mode-independent). Both bindings run under both §5.1
/// wire presets.
/// </summary>
public sealed class ViewModeToggleTests
{
    public static TheoryData<string> Presets => TestSupport.CapabilityPresets.Both;

    [Theory]
    [MemberData(nameof(Presets))]
    public void CtrlSlash_TogglesFormattedAndRaw_AcrossTheWholeSurface(string preset)
    {
        // The caret starts on line 0 ("intro"), so the heading and the emphasis paragraph are INACTIVE
        // blocks — their marks hide in formatted mode (reveal-on-edit only reveals the caret's own line).
        using var harness = MarkdownEditingHarness.Create("intro\n\n## Section\n\n**bold** end", preset);

        // Formatted (default): the inactive heading hides its "## ".
        Assert.Equal(ViewMode.Formatted, harness.Bridge.ViewMode);
        Assert.Equal("Section", harness.RowTrimmed(RowOf(harness, "Section")));

        // Ctrl+/ → Raw: every mark shows literally on every line; every block swaps to the raw presenter.
        harness.Chord('/', KeyModifiers.Control);
        Assert.Equal(ViewMode.Raw, harness.Bridge.ViewMode);
        Assert.Equal("## Section", harness.RowTrimmed(RowOf(harness, "## Section")));
        Assert.Equal("**bold** end", harness.RowTrimmed(RowOf(harness, "**bold**")));
        Assert.IsType<RawSourcePresenter>(harness.Presenter(0));
        Assert.IsType<RawSourcePresenter>(harness.Presenter(1)); // the whole surface switched, not just block 0

        // Ctrl+/ again → back to Formatted (the formatted presenters return, no regression).
        harness.Chord('/', KeyModifiers.Control);
        Assert.Equal(ViewMode.Formatted, harness.Bridge.ViewMode);
        Assert.Equal("Section", harness.RowTrimmed(RowOf(harness, "Section")));
        Assert.IsNotType<RawSourcePresenter>(harness.Presenter(1));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void AltSlash_IsTheLegacySafeAlternate(string preset)
    {
        // Caret on line 0 ("intro") keeps the heading inactive, so formatted hides its "# ".
        using var harness = MarkdownEditingHarness.Create("intro\n\n# Title", preset);

        harness.Chord('/', KeyModifiers.Alt); // the chord the legacy wire can deliver
        Assert.Equal(ViewMode.Raw, harness.Bridge.ViewMode);
        Assert.Equal("# Title", harness.RowTrimmed(RowOf(harness, "# Title"))); // literal "# " shown

        harness.Chord('/', KeyModifiers.Alt);
        Assert.Equal(ViewMode.Formatted, harness.Bridge.ViewMode);
        Assert.Equal("Title", harness.RowTrimmed(RowOf(harness, "Title")));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Toggle_PreservesCaretSourcePosition(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("## Section\n\nplain text", preset);

        var origin = harness.Caret.Position;
        harness.Key(Key.End); // move off the origin to a non-trivial source position
        var anchored = harness.Caret.Position;
        Assert.NotEqual(origin, anchored); // sanity: the caret actually moved

        harness.Chord('/', KeyModifiers.Control); // → Raw
        Assert.Equal(anchored, harness.Caret.Position); // the source anchor is mode-independent

        harness.Chord('/', KeyModifiers.Control); // → Formatted
        Assert.Equal(anchored, harness.Caret.Position);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void RawMode_CaretIsIdentity_NoMarkSkipping(string preset)
    {
        using var harness = MarkdownEditingHarness.Create("## Section", preset);

        harness.Chord('/', KeyModifiers.Control); // → Raw

        // Home lands at source col 0 ('#'); the identity map puts the caret at cell 0 (in formatted mode
        // the caret would sit at the 'S', the "## " being hidden). Then walking three cells reaches 'S'.
        harness.Key(Key.Home);
        harness.AssertCaret(0, 0);
        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow);
        harness.Key(Key.RightArrow);
        harness.AssertCaret(0, 3); // raw walks source 1:1 — three steps from '#' reach 'S'
    }

    /// <summary>The frame row whose composited text starts with <paramref name="prefix"/>.</summary>
    private static int RowOf(MarkdownEditingHarness harness, string prefix)
    {
        for (var row = 0; row < 12; row++)
            if (harness.RowTrimmed(row).StartsWith(prefix, StringComparison.Ordinal))
                return row;

        Assert.Fail($"no row starting with '{prefix}'");
        return -1;
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void RawMode_LongLine_EndKeepsTheCaretVisible(string preset)
    {
        // A source line far wider than the 24-col viewport. In raw mode it does not wrap, so pressing End
        // must slide the caret's line to keep the caret on screen (WP10 review fix) — not clip it away.
        using var harness = MarkdownEditingHarness.Create(new string('x', 60) + "END", preset, columns: 24);

        harness.Editor.ToggleViewMode(); // → Raw
        harness.Settle();
        Assert.Equal(ViewMode.Raw, harness.Editor.ViewMode);

        harness.Key(Key.End);
        harness.Settle();

        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.InRange(harness.Host.FrameBuffer.CursorColumn, 0, 23); // slid into view, not off the right edge
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Toggling_KeepsTheCaretOnScreen(string preset)
    {
        // A document taller than the 6-row viewport; put the caret on the last block, then toggle. The mode
        // switch changes every block's height, so without a caret-follow the caret can be scrolled off; the
        // toggle must EnsureVisible (WP10 review fix).
        using var harness = MarkdownEditingHarness.Create(
            "# One\n\npara two\n\npara three\n\npara four\n\n## LAST", preset, columns: 40, rows: 6);

        harness.Caret.MoveDocumentEnd(extend: false); // caret onto the last block
        harness.Settle();

        harness.Editor.ToggleViewMode(); // → Raw (heights shrink: no wrap/reveal)
        harness.Settle();

        Assert.True(harness.Host.FrameBuffer.CursorVisible);
        Assert.InRange(harness.Host.FrameBuffer.CursorRow, 0, 5); // still within the viewport after the switch
    }

}
