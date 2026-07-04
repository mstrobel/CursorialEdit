using System.Text;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.App;

namespace CursorialEdit.Tests.App;

/// <summary>
/// Smoke tests for the M2 checkpoint reveal demo (<see cref="RevealDemoView"/>), driven headlessly
/// through the real frame loop: markdown renders formatted, the cursor's line reveals its raw marks,
/// and typing re-parses live. Proves the demo works end-to-end before a hands-on terminal run.
/// </summary>
public sealed class RevealDemoTests
{
    // line 0 paragraph, line 2 an ATX heading, line 4 a paragraph with emphasis.
    private const string Doc = "para start\n\n# Heading Two\n\nplain **word** end";

    private static string Screen(UITestHost host)
    {
        var sb = new StringBuilder();
        for (var row = 0; row < host.FrameBuffer.Rows; row++)
            sb.AppendLine(host.GetRowText(row));
        return sb.ToString();
    }

    [Fact]
    public void InactiveHeading_RendersFormatted_ActiveHeading_RevealsTheHashMark()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        var demo = new RevealDemoView(Doc);
        host.ShowRoot(demo);
        demo.Focus();
        Assert.True(host.RunUntilIdle());

        // The caret starts on line 0 (a paragraph), so the heading on line 2 is INACTIVE — its "#"
        // mark is hidden and it renders as formatted heading text.
        var formatted = Screen(host);
        Assert.Contains("Heading Two", formatted);
        Assert.DoesNotContain("# Heading Two", formatted); // the mark is hidden while inactive

        // Move the cursor down to the heading line (0 → 1 → 2). It becomes active and reveals its "#".
        host.SendKey(Key.DownArrow);
        host.SendKey(Key.DownArrow);
        Assert.True(host.RunUntilIdle());

        var revealed = Screen(host);
        Assert.Contains("# Heading Two", revealed); // the raw mark surfaces on the active line
    }

    [Fact]
    public void Typing_OnTheActiveLine_ReparsesAndReRenders()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        var demo = new RevealDemoView("hello world\n\nsecond paragraph");
        host.ShowRoot(demo);
        demo.Focus();
        Assert.True(host.RunUntilIdle());

        Assert.Contains("hello world", Screen(host));

        // Type at the caret (line 0, col 0) — the buffer re-parses through the real Markdig producer
        // and the block re-renders with the inserted text.
        host.SendText("X");
        Assert.True(host.RunUntilIdle());

        Assert.Contains("Xhello world", Screen(host));
    }
}
