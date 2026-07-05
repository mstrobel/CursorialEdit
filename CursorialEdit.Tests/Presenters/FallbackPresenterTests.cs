using Cursorial.Output;

using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;

namespace CursorialEdit.Tests.Presenters;

/// <summary>
/// M2.WP7 §2.4 fallback gate: raw HTML and every construct without a dedicated presenter yet — tables
/// (M3) and the extension constructs alerts / definition lists / footnotes / math / link-reference
/// definitions (M4) — route to <see cref="FallbackSourcePresenter"/>, which renders their raw source
/// as dimmed literal text and never crashes.
/// </summary>
public sealed class FallbackPresenterTests
{
    public static TheoryData<string, string> Constructs() => new()
    {
        { "<div class=\"x\">raw</div>", "<div class=\"x\">raw</div>" },     // HTML (§2.4)
        { "| a | b |\n|---|---|\n| 1 | 2 |", "| a | b |" },                  // table (M3)
        { "> [!NOTE]\n> heads up", "> [!NOTE]" },                            // GitHub alert (M4)
        { "$$\nx = y\n$$", "$$" },                                          // block math (M4)
        { "[^1]: a footnote definition", "[^1]: a footnote definition" },   // footnote (M4)
    };

    [Theory]
    [MemberData(nameof(Constructs))]
    public void UnhandledConstruct_RendersDimmedLiteral_ViaFallback(string markdown, string firstRow)
    {
        using var harness = PresenterHarness.FromMarkdown(markdown, columns: 40, rows: 10);

        // The first block routes to the fallback presenter…
        Assert.IsType<FallbackSourcePresenter>(harness.Presenters[0]);

        // …and its raw source shows as dimmed literal text (never interpreted, never crashing).
        Assert.Equal(firstRow, harness.RowTrimmed(0));
        Assert.Equal(TextAttributes.Faint, harness.Cell(0, 0).Style.Attributes & TextAttributes.Faint);
    }

    [Fact]
    public void Fallback_NeverThrows_OnAnyKind()
    {
        // Directly exercise the fallback over an arbitrary kind and a ragged block — it must not throw.
        foreach (var kind in Enum.GetValues<BlockKind>())
        {
            var lines = PresenterHarness.Lines("<unclosed\n\ttabs\tand   spaces\nmore");
            using var harness = PresenterHarness.Show([new FallbackSourcePresenter(lines, kind)], columns: 20, rows: 6);
            Assert.Equal("<unclosed", harness.RowTrimmed(0));
        }
    }
}
