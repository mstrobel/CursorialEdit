using System.Diagnostics;
using System.Text;

using CursorialEdit.Tests.Editing;

namespace CursorialEdit.Tests.Clipboard;

/// <summary>
/// M1.WP9 — the 1 MB paste benchmark (plan §6 "tests beyond the gate"; risk 4): a 1 MiB
/// bracketed paste must apply as <b>one splice / one undo unit</b> and complete end-to-end
/// within a generous-but-asserted budget. The measured window is deliberately the whole harness
/// path — VT-parsing 1 MiB of wire bytes off-thread, the pump hop, the single buffer splice,
/// block production over ~19k lines, and the band-limited layout/raster settle — because the
/// spec §13 budget this protects is <i>frame latency</i> (&lt;16 ms typical / 50 ms hard), and
/// a paste that stalls anywhere in that path stalls the frame. The 500 ms ceiling is therefore
/// a <b>regression tripwire</b>, not the product budget: it catches an accidental O(n²) or a
/// chunked-apply regression (the single-splice memmove is the cheap part) without flaking on
/// runner variance.
/// </summary>
public sealed class PasteBenchmarkTests
{
    /// <summary>The tripwire ceiling (see the class remarks — generous by design, asserted always).</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    [Fact]
    [Trait("Category", "Benchmark")]
    public void BracketedPaste_1MiB_AppliesAsOneSplice_WithinBudget()
    {
        string payload = BuildPayload();
        Assert.Equal(1 << 20, payload.Length); // 1 MiB of ASCII == 1 MiB of wire bytes
        byte[] envelope = Encoding.ASCII.GetBytes("\x1b[200~" + payload + "\x1b[201~");

        // Warm-up (the framework benchmark pattern): JIT the whole path on a small paste so the
        // measured iterations reflect steady state, then take the best of three fresh runs.
        RunOnce(Encoding.ASCII.GetBytes("\x1b[200~warm\nup\x1b[201~"), "warm\nup");

        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var elapsed = RunOnce(envelope, payload);
            if (elapsed < best)
                best = elapsed;
        }

        Assert.True(
            best < Budget,
            $"1 MiB bracketed paste took {best.TotalMilliseconds:F0} ms end-to-end (tripwire {Budget.TotalMilliseconds:F0} ms)");
    }

    /// <summary>One fresh-harness iteration: paste into an empty document, assert the single-splice contract, return the elapsed wall time.</summary>
    private static TimeSpan RunOnce(byte[] envelope, string expected)
    {
        using var harness = EditingHarness.Create(string.Empty);

        var splices = 0;
        harness.Controller.Changed += _ => splices++;

        var clock = Stopwatch.StartNew();
        harness.Bytes(envelope);
        clock.Stop();

        Assert.Equal(1, splices); // ONE splice — never chunked
        Assert.Equal(1, harness.Controller.UndoDepth); // ONE undo unit
        Assert.Equal(expected, harness.Buffer.GetText());
        return clock.Elapsed;
    }

    /// <summary>
    /// Exactly 1 MiB of text: 63-char lines with a blank line every eighth, so the M1
    /// paragraph producer yields many small blocks (the realistic shape) rather than one
    /// 16k-line block.
    /// </summary>
    private static string BuildPayload()
    {
        const int target = 1 << 20;
        var builder = new StringBuilder(target + 128);
        var line = new string('x', 63);

        var i = 0;
        while (builder.Length < target)
        {
            builder.Append(line).Append('\n');
            if (++i % 8 == 0)
                builder.Append('\n');
        }

        return builder.ToString(0, target);
    }
}
