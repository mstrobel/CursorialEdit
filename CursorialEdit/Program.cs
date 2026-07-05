using System.Reflection;

using Cursorial.UI;

using CursorialEdit.App;

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

UIApplication app = UIApplication.CreateBuilder()
                                 .WithFrameRate(60) // §13 targets keystroke→frame < 16 ms; 60 fps pacing to match (Gallery's choice)
                                 .UseAlternateScreen()
                                 .Build();

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
        return shell;
    });
}
finally
{
    emergencyRestore?.Dispose();
    await app.DisposeAsync();
}
