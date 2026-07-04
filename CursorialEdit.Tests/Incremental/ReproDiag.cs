using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Blocks;
using Xunit.Abstractions;

namespace CursorialEdit.Tests.Incremental;

public sealed class ReproDiag
{
    private readonly ITestOutputHelper _out;
    public ReproDiag(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Diagnose()
    {
        // Find the first diverging run in [5000, 5400) and print the minimal repro.
        for (int run = 0; run < 400; run++)
        {
            var rng = new Random(5000 + run);
            var h = BlockHarness.Create(DifferentialFuzzTests.StartingDocForRun(run));
            for (int step = 0; step < 250; step++)
            {
                string beforeBuf = h.Buffer.GetText();
                var edit = DifferentialFuzzTests.ApplyRandomEditPublic(h, rng);
                string afterBuf = h.Buffer.GetText();

                var full = BlockHarness.Create(afterBuf);
                bool diverged = full.Blocks.Count != h.Blocks.Count;
                if (!diverged)
                {
                    for (int i = 0; i < h.Blocks.Count; i++)
                    {
                        if (full.Blocks[i].Kind != h.Blocks[i].Kind ||
                            full.Blocks[i].LineCount != h.Blocks[i].LineCount ||
                            full.Blocks.GetStartLine(i) != h.Blocks.GetStartLine(i))
                        {
                            diverged = true;
                            break;
                        }
                    }
                }

                if (diverged)
                {
                    _out.WriteLine($"DIVERGE run={run} seed={5000 + run} step={step}");
                    _out.WriteLine($"BEFORE buffer:\n{Q(beforeBuf)}");
                    _out.WriteLine($"EDIT: {edit}");
                    _out.WriteLine($"AFTER buffer:\n{Q(afterBuf)}");
                    _out.WriteLine("FULL:");
                    for (int i = 0; i < full.Blocks.Count; i++)
                        _out.WriteLine($"  [{i}] {full.Blocks[i].Kind} @{full.Blocks.GetStartLine(i)}x{full.Blocks[i].LineCount} {Q(full.TextOf(i))}");
                    _out.WriteLine("WINDOWED:");
                    for (int i = 0; i < h.Blocks.Count; i++)
                        _out.WriteLine($"  [{i}] {h.Blocks[i].Kind} @{h.Blocks.GetStartLine(i)}x{h.Blocks[i].LineCount} {Q(h.TextOf(i))}");
                    return;
                }
            }
        }

        _out.WriteLine("NO DIVERGENCE FOUND in runs 0..399");
    }

    private static string Q(string t) => "\"" + t.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
