using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Bars;

using CursorialEdit.App;
using CursorialEdit.Diagnostics;

// M1.WP2 — app bootstrap (implementation-plan §6 WP2; integration notes §10): parse the CLI,
// build the UIApplication, run the single-root EditorShell (no Window), and arm the FB-4
// emergency-restore workaround around the session's lifetime.

// M2 checkpoint deliverable: `--reveal-demo [file.md]` runs the self-contained reveal-on-edit demo
// (RevealDemoView) instead of the production shell — a hands-on preview of markdown rendering +
// reveal-on-edit before the WP7 presenter fan-out wires it into the real editor.
if (args.Length > 0 && args[0] == "--reveal-demo")
{
    string markdown = args.Length > 1 && File.Exists(args[1]) ? await File.ReadAllTextAsync(args[1]) : string.Empty;

    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("cursorialedit: the reveal demo needs an interactive terminal (TTY).");
        return 1;
    }

    UIApplication demoApp = UIApplication.CreateBuilder().WithFrameRate(60).UseAlternateScreen().Build();
    IDisposable? demoRestore = null;
    demoApp.Started += (_, _) => demoRestore = SignalRestore.Register();
    try
    {
        return await demoApp.RunAsync(() =>
        {
            CursorialEdit.Themes.MdTheme.EnsureInstalled(UIApplication.Current); // WP11: Md.* theme for the demo too
            return new RevealDemoView(markdown);
        });
    }
    finally
    {
        demoRestore?.Dispose();
        await demoApp.DisposeAsync();
    }
}

if (!AppStartupOptions.TryParse(args, out var startupOptions, out var startupError))
{
    Console.Error.WriteLine(startupError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(AppStartupOptions.UsageText);
    return 2;
}

if (startupOptions.ShowHelp)
{
    Console.WriteLine(AppStartupOptions.UsageText);
    return 0;
}

if (startupOptions.ShowVersion)
{
    var assembly = typeof(AppStartupOptions).Assembly;
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? assembly.GetName().Version?.ToString()
                  ?? "unknown";
    Console.WriteLine($"cursorialedit {version}");
    return 0;
}

// `--replay <path>`: a diagnostic deliverable — reconstruct a recorded session's initial document and
// re-inject its recorded input stream live in the real editor, so the maintainer watches the exact
// sequence reproduce (retiring a bug's reproduction to a single file). Mirrors the --reveal-demo
// branch: a dedicated app instance, the same FB-4 emergency-restore, its own factory.
if (startupOptions.ReplayPath is { } replayPath)
{
    if (!File.Exists(replayPath))
    {
        Console.Error.WriteLine($"cursorialedit: replay journal not found: {replayPath}");
        return 1;
    }

    JournalReadResult replayJournal;
    try
    {
        replayJournal = JournalReader.ReadFile(replayPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"cursorialedit: cannot read replay journal '{replayPath}': {ex.Message}");
        return 1;
    }

    if (!replayJournal.HasHeader)
    {
        Console.Error.WriteLine($"cursorialedit: '{replayPath}' is not a usable journal — {replayJournal.StoppedReason}.");
        return 1;
    }

    if (replayJournal.Truncated)
    {
        // A truncated capture (crash mid-session) is the interesting case — replay the valid prefix.
        Console.Error.WriteLine(
            $"cursorialedit: replay journal is truncated — replaying {replayJournal.Events.Count} event(s) up to line {replayJournal.LineNumber} ({replayJournal.StoppedReason}).");
    }

    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("cursorialedit: replay needs an interactive terminal (TTY).");
        return 1;
    }

    UIApplication replayApp = UIApplication.CreateBuilder().WithFrameRate(60).UseAlternateScreen().Build();
    IDisposable? replayRestore = null;
    replayApp.Started += (_, _) => replayRestore = SignalRestore.Register();
    replayApp.Started += (_, _) => _ = RunReplayEventsAsync(replayApp, replayJournal);
    try
    {
        return await replayApp.RunAsync(() =>
        {
            CursorialEdit.Themes.MdTheme.EnsureInstalled(UIApplication.Current); // WP11: Md.* theme for the replay view too
            var shell = new EditorShell();
            shell.WireDocument(replayJournal.Header!.Document.Content); // reconstruct the recorded starting buffer
            return shell;
        });
    }
    finally
    {
        replayRestore?.Dispose();
        await replayApp.DisposeAsync();
    }
}

// Pre-flight TTY check, BEFORE building the app: a pipe or redirection can never host the
// full-screen session, so fail with a friendly message instead of a framework stack trace.
// Exotic non-TTY cases this redirection check misses (e.g. a closed descriptor that is not
// redirected) surface as the framework's own InvalidOperationException below — a deliberate
// loud failure, never silently misdiagnosed as something else.
if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("cursorialedit: an interactive terminal (TTY) is required — it cannot run under a pipe or redirection.");
    return 1;
}

// `--journal`: open the diagnostic operation journal now (I/O only; the header is written on the UI
// thread once the app is running and capabilities are negotiated). A bad path fails fast — capturing
// IS the point of the invocation. Zero overhead when the flag is absent: nothing is created and
// nothing subscribes to the input dispatcher.
SessionJournal? journal = null;
DateTimeOffset journalStart = TimeProvider.System.GetUtcNow(); // wall-clock capture stamp via the standard app time source
if (startupOptions.JournalRequested)
{
    string journalPath = ResolveJournalPath(startupOptions.JournalPath);
    try
    {
        journal = SessionJournal.CreateForFile(journalPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"cursorialedit: cannot open journal '{journalPath}': {ex.Message}");
        return 1;
    }

    Console.Error.WriteLine($"cursorialedit: journaling this session to {journalPath}");
}

UIApplication app = UIApplication.CreateBuilder()
                                 .WithFrameRate(60) // §13 targets keystroke→frame < 16 ms; 60 fps pacing to match (Gallery's choice)
                                 .UseAlternateScreen()
                                 .Build();

// Enable the Bars KeyTip overlay (the Alt-held accelerator badges) for the ribbon: Alt reveals the tab/group/button
// key tips, and the tab → group → control drill activates a command. Idempotent and gated — it no-ops on terminals
// that don't satisfy the AltHeld gate (a legacy terminal simply never arms the overlay).
app.EnableKeyTips();

// FB-4 workaround: register from Started — AFTER TerminalSession.OpenAsync registered its own
// signal handlers — so ours run FIRST (the runtime invokes handlers newest-first) and the restore
// bytes hit the wire before the framework's teardown. See SignalRestore's remarks for the full
// ordering contract.
IDisposable? emergencyRestore = null;
app.Started += (_, _) => emergencyRestore = SignalRestore.Register();

// No catch around RunAsync: any InvalidOperationException from a running app (EditController's
// designed loud-failure throws, framework invariant violations) must propagate loudly — stack
// trace and non-zero exit — never be misreported as a TTY problem.
try
{
    // The factory runs on the UI thread before the first frame (UIApplication.Current is already
    // installed there), so the startup document — the CLI path, or an untitled buffer — is wired
    // with its autosave/dirty tracking before anything renders. A missing CLI file opens as a new
    // document at that path; an unreadable one falls back to untitled with the failure on the
    // status line (EditorShell.OpenStartupDocument's contract).
    return await app.RunAsync(() =>
    {
        CursorialEdit.Themes.MdTheme.EnsureInstalled(UIApplication.Current); // WP11: install the Md.* theme + FW-A overrides
        var shell = new EditorShell(startupOptions);
        shell.OpenStartupDocument();

        // Journaling armed: write the session header (schema, negotiated caps, terminal size, and the
        // initial document) as the first line, then subscribe to the single comprehensive capture point
        // — InputDispatcher.PreProcessInput — for the rest of the session. Runs on the UI thread with
        // capabilities already negotiated (StartupAsync ran before this factory).
        if (journal is { } sessionJournal)
        {
            var (columns, rows) = TryGetTerminalSize();
            sessionJournal.WriteHeader(JournalHeaderLine.Create(
                app.Capabilities,
                columns,
                rows,
                shell.FilePath,
                shell.Document?.GetText() ?? string.Empty,
                journalStart));
            sessionJournal.AttachTo(app.InputDispatcher);
        }

        return shell;
    });
}
finally
{
    journal?.Dispose(); // unsubscribe + flush + close the journal
    emergencyRestore?.Dispose();
    await app.DisposeAsync();
}

// ───────────────────────────── diagnostic-journal helpers ─────────────────────────────

// The default journal path: a timestamped file under the temp directory. Kept out of the app-state
// (`journals/` autosave) and FW-A user-settings (`~/.cursorial`) stores so a diagnostic capture never
// collides with them. The filename timestamp is derived once from the wall clock via the standard app
// time source (Date.Now-free).
static string ResolveJournalPath(string? explicitPath)
{
    if (!string.IsNullOrWhiteSpace(explicitPath))
        return explicitPath;

    var stamp = TimeProvider.System.GetUtcNow()
                            .ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    return Path.Combine(Path.GetTempPath(), "cursorialedit", $"session-{stamp}.jsonl");
}

// Best-effort terminal size for the journal header. The app already guaranteed a TTY, so Console
// reports the real size; the framework's own 80×24 startup default is the fallback if it cannot.
static (int Columns, int Rows) TryGetTerminalSize()
{
    try
    {
        var columns = Console.WindowWidth;
        var rows = Console.WindowHeight;
        if (columns > 0 && rows > 0)
            return (columns, rows);
    }
    catch
    {
        // not a console — fall through
    }

    return (80, 24);
}

// The live replay loop: drive each recorded event through the real input system, pacing loosely by
// the captured inter-event timing (clamped so it stays watchable without dragging). Runs on the UI
// thread — started from `Started`, its awaits resume on the frame-coherent sync context — so the
// frame loop renders between injected events. Best-effort: a replay must never crash the viewer, and
// the app stays open on the final state until the maintainer quits (Ctrl+C).
static async Task RunReplayEventsAsync(UIApplication application, JournalReadResult replayJournal)
{
    try
    {
        DateTimeOffset? previous = null;
        foreach (var inputEvent in replayJournal.Events)
        {
            var delta = previous is { } p ? inputEvent.Timestamp - p : TimeSpan.Zero;
            previous = inputEvent.Timestamp;

            // Clamp to [16 ms, 300 ms]: at least one frame between events (watchable), never a stall.
            var pauseMs = Math.Clamp(delta.TotalMilliseconds, 16, 300);
            await Task.Delay(TimeSpan.FromMilliseconds(pauseMs)); // resumes on the UI thread (captured sync context)

            ReplayDriver.Inject(application, inputEvent);
            application.RequestRender();
        }
    }
    catch
    {
        // A diagnostic replay is best-effort; swallow so the viewer stays up on whatever it reached.
    }
}
