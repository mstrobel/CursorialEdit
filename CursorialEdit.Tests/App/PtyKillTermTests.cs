using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CursorialEdit.Tests.App;

/// <summary>
/// Dynamic-skip gate for the PTY kill-TERM test: xunit v2 has no <c>Assert.Skip</c>, so the
/// Cursorial house pattern applies (constructor-computed <see cref="FactAttribute.Skip"/> — see
/// <c>Cursorial.Core.Tests/PlatformAttributes.cs</c>). Skips gracefully when a PTY cannot be
/// allocated: non-POSIX platform, no <c>script(1)</c>, or the app binary missing beside the
/// test assembly.
/// </summary>
public sealed class PtyFactAttribute : FactAttribute
{
    /// <summary>The BSD/util-linux <c>script(1)</c> utility — the portable no-code way to run a child on a fresh PTY.</summary>
    internal const string ScriptPath = "/usr/bin/script";

    /// <summary>The app's apphost, copied beside the test assembly by the project reference.</summary>
    internal static string AppHostPath => Path.Combine(AppContext.BaseDirectory, "CursorialEdit");

    public PtyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            Skip = "PTY kill-TERM test requires a POSIX platform with script(1).";
        else if (!File.Exists(ScriptPath))
            Skip = $"script(1) not found at {ScriptPath} — cannot allocate a PTY here.";
        else if (!File.Exists(AppHostPath))
            Skip = $"CursorialEdit apphost not found at {AppHostPath} — build the app project first.";
    }
}

/// <summary>
/// M1.WP2 / M1 exit gate — the scripted FB-4 signal test (implementation-plan §6: "scripted PTY
/// kill-TERM test"; §5.7 nightly-integration lane, hence <c>Category=Integration</c>): launches
/// the real app on a pseudo-TTY via <c>script(1)</c>, sends SIGTERM, and asserts the emitted
/// byte stream ends sane — the <see cref="CursorialEdit.App.EmergencyRestore"/> sequence appears
/// after the last alt-screen entry, and nothing after it re-enters the alt screen or re-hides
/// the cursor. (The framework's own negotiator disables legitimately follow our bytes — the
/// framework handler runs after ours by design; see <c>SignalRestore</c>'s ordering contract —
/// so the assertion is "restore-then-nothing-that-unsanitizes", not a literal end-of-stream
/// byte match.)
/// </summary>
public sealed class PtyKillTermTests
{
    private const string EnterAltScreen = "\u001b[?1049h";
    private const string HideCursor = "\u001b[?25l";
    private const string RestoreSequence = "\u001b[?7h\u001b]112\u001b\\\u001b[?1049l\u001b[?25h\u001b[0m"; // EmergencyRestore's exact bytes

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Thread-safe accumulator for the PTY byte stream (pump task writes, assertions poll).</summary>
    private sealed class OutputAccumulator
    {
        private readonly MemoryStream _bytes = new();

        public void Append(ReadOnlySpan<byte> chunk)
        {
            lock (_bytes)
            {
                _bytes.Write(chunk);
            }
        }

        /// <summary>Latin-1 decode: a 1:1 byte↔char mapping, so escape-sequence string search is exact.</summary>
        public string SnapshotText()
        {
            lock (_bytes)
            {
                return Encoding.Latin1.GetString(_bytes.GetBuffer(), 0, (int) _bytes.Length);
            }
        }
    }

    [PtyFact]
    [Trait("Category", "Integration")]
    public async Task SigTerm_OnPty_EmitsEmergencyRestore_AndNothingUnsanitizesAfterIt()
    {
        // `sh -c 'echo __PID=$$; exec app'` keeps the app on the shell's PID so we can target it.
        var inner = $"echo __PID=$$; exec '{PtyFactAttribute.AppHostPath}'";

        var psi = new ProcessStartInfo
        {
            FileName = PtyFactAttribute.ScriptPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (OperatingSystem.IsMacOS())
        {
            // BSD script: script [-q] [file [command ...]]
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(inner);
        }
        else
        {
            // util-linux script: script [-q] [-e] [-c command] [file]
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(inner);
            psi.ArgumentList.Add("/dev/null");
        }

        // A known terminal family ⇒ the AlternateScreenBuffer capability stamps ⇒ the app
        // actually enters the alt screen (the state FB-4 is about stranding).
        psi.Environment["TERM"] = "xterm-256color";

        using var process = Process.Start(psi)!;
        var output = new OutputAccumulator();
        var pump = PumpAsync(process.StandardOutput.BaseStream, output);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null); // drain so a full pipe can never wedge script(1)

        try
        {
            // 1. The app is up: PID line seen, then the alt-screen entry bytes on the wire.
            var pidText = await WaitForAsync(output, static text => Regex.Match(text, @"__PID=(\d+)") is { Success: true } m ? m.Groups[1].Value : null,
                                             "the child PID line");
            var pid = int.Parse(pidText);

            await WaitForAsync(output, static text => text.Contains(EnterAltScreen, StringComparison.Ordinal) ? "" : null,
                               "the alt-screen entry (CSI ? 1049 h)");

            // Let a couple of frames land so the kill hits a running, mid-session app.
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            // 2. kill -TERM the app.
            using var kill = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-TERM", pid.ToString() } })!;
            await kill.WaitForExitAsync().WaitAsync(StepTimeout);
            Assert.Equal(0, kill.ExitCode);

            // 3. The session ends (script exits when its child dies).
            await process.WaitForExitAsync().WaitAsync(StepTimeout);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        await pump.WaitAsync(StepTimeout);

        // 4. The stream ends sane: the emergency-restore bytes came after the final alt-screen
        //    entry, and nothing after them undid the restore.
        var text = output.SnapshotText();

        Assert.Contains(RestoreSequence, text);
        var lastRestore = text.LastIndexOf(RestoreSequence, StringComparison.Ordinal);
        var lastEnter = text.LastIndexOf(EnterAltScreen, StringComparison.Ordinal);
        Assert.True(lastRestore > lastEnter,
                    "the emergency-restore sequence must follow the last alt-screen entry");

        var tail = text[(lastRestore + RestoreSequence.Length)..];
        Assert.DoesNotContain(EnterAltScreen, tail); // never re-enters the alt screen
        Assert.DoesNotContain(HideCursor, tail);     // nothing re-hides the cursor after the restore
    }

    private static async Task PumpAsync(Stream stream, OutputAccumulator output)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read <= 0)
                return;

            output.Append(buffer.AsSpan(0, read));
        }
    }

    /// <summary>Polls the accumulated PTY stream until <paramref name="probe"/> yields a value; fails after <see cref="StepTimeout"/>.</summary>
    private static async Task<string> WaitForAsync(OutputAccumulator output, Func<string, string?> probe, string what)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe(output.SnapshotText()) is { } value)
                return value;

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"timed out waiting for {what}; PTY stream so far:\n{output.SnapshotText()}");
        return null!; // unreachable
    }
}
