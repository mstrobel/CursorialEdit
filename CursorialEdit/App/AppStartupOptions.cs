using System.Diagnostics.CodeAnalysis;

namespace CursorialEdit.App;

/// <summary>
/// The parsed command-line startup options (spec §10.4): zero arguments start an empty editor; a
/// single file-path argument opens that file (M1.WP11 consumes <see cref="FilePath"/> — this type
/// only carries it). Dash-prefixed arguments are flags, not paths: <c>--help</c>/<c>-h</c>/<c>-?</c>
/// set <see cref="ShowHelp"/> (Program prints <see cref="UsageText"/> to stdout and exits 0),
/// <c>--version</c> sets <see cref="ShowVersion"/> (Program prints the version and exits 0),
/// <c>--journal[=&lt;path&gt;]</c> enables the diagnostic operation journal (<see cref="JournalRequested"/>
/// / <see cref="JournalPath"/>), <c>--replay &lt;path&gt;</c> replays a recorded journal
/// (<see cref="ReplayPath"/>), <c>--</c> ends flag parsing so a file literally named like an option
/// can be opened, and any other dash-argument is a parse failure. Directory arguments and multiple
/// paths are spec-<b>deferred</b> (they tie to multi-document support), so they are parse failures too:
/// <c>Program</c> prints the error plus <see cref="UsageText"/> to stderr and exits with code 2.
/// </summary>
/// <param name="FilePath">The document to open at startup, or <see langword="null"/> for an empty editor.</param>
public sealed record AppStartupOptions(string? FilePath = null)
{
    /// <summary>Whether <c>--help</c>/<c>-h</c>/<c>-?</c> was given: print <see cref="UsageText"/> to stdout and exit 0 instead of starting the editor.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Whether <c>--version</c> was given: print the informational version to stdout and exit 0 instead of starting the editor.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>
    /// Whether <c>--journal</c> (with or without a path) was given: Program records the session's
    /// input stream to a JSONL journal (see <see cref="Diagnostics.SessionJournal"/>).
    /// </summary>
    public bool JournalRequested { get; init; }

    /// <summary>
    /// The explicit journal path from <c>--journal=&lt;path&gt;</c> or <c>--journal &lt;path&gt;</c>
    /// (the next non-flag argument), or <see langword="null"/> when <c>--journal</c> was given bare
    /// (Program derives a default under the temp directory and prints it to stderr). Meaningful only
    /// when <see cref="JournalRequested"/> is set.
    /// </summary>
    public string? JournalPath { get; init; }

    /// <summary>
    /// The journal file to replay from <c>--replay &lt;path&gt;</c>, or <see langword="null"/> for a
    /// normal run. When set, Program reconstructs the recorded initial document and re-injects the
    /// recorded input stream live (see <see cref="Diagnostics.ReplayDriver"/>).
    /// </summary>
    public string? ReplayPath { get; init; }

    /// <summary>The usage text printed to stderr when the command line cannot be parsed (and to stdout for <c>--help</c>).</summary>
    public const string UsageText =
        """
        Usage: cursorialedit [OPTIONS] [FILE]

        Opens FILE in the editor, or starts with an empty document when no FILE is
        given. Directories and multiple files are not supported in v1.

        Options:
          -h, -?, --help    Print this usage text and exit.
          --version         Print the version and exit.
          --journal[=PATH]  Record this session's input to a JSONL operation journal
                            (a diagnostic capture for reproducing bugs). PATH may also
                            follow as the next argument (--journal PATH [FILE]); bare
                            --journal uses a default under the temp dir, printed to stderr.
          --replay PATH     Replay a recorded journal: reconstruct its initial document
                            and re-inject the recorded input stream live in the editor.
          --                End of options; later arguments are file paths even when
                            they start with '-'.
        """;

    /// <summary>
    /// Parses the process argument vector. On success <paramref name="options"/> is set (with
    /// <see cref="FilePath"/> <see langword="null"/> for an empty invocation, or
    /// <see cref="ShowHelp"/>/<see cref="ShowVersion"/> set for the flag outcomes); on failure
    /// <paramref name="error"/> carries a one-line reason suitable for stderr.
    /// </summary>
    /// <param name="args">The raw <c>args</c> array (no executable name).</param>
    /// <param name="options">The parsed options when the method returns <see langword="true"/>.</param>
    /// <param name="error">The failure reason when the method returns <see langword="false"/>.</param>
    public static bool TryParse(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out AppStartupOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;

        bool showHelp = false;
        bool showVersion = false;
        bool endOfFlags = false;
        bool journalRequested = false;
        string? journalPath = null;
        string? replayPath = null;
        var paths = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!endOfFlags && arg.StartsWith('-'))
            {
                // Split "--flag=value" once; a bare flag leaves value null.
                var eq = arg.IndexOf('=');
                var name = eq >= 0 ? arg[..eq] : arg;
                var inlineValue = eq >= 0 ? arg[(eq + 1)..] : null;

                switch (name)
                {
                    case "--":
                        endOfFlags = true; // everything after is a path, even dash-prefixed
                        break;
                    case "--help" or "-h" or "-?":
                        showHelp = true;
                        break;
                    case "--version":
                        showVersion = true;
                        break;
                    case "--journal":
                        journalRequested = true;
                        if (inlineValue is not null)
                        {
                            if (inlineValue.Length == 0)
                            {
                                error = "cursorialedit: '--journal=' needs a path (or use bare '--journal' for the default).";
                                return false;
                            }

                            journalPath = inlineValue;
                        }
                        else if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                        {
                            // Space form: `--journal PATH [FILE]` — the next non-flag token is the
                            // journal path (a positional FILE may still follow it). A bare `--journal`
                            // at the end (or before another flag) uses the default path.
                            journalPath = args[++i];
                        }

                        break;
                    case "--replay":
                        // Value is inline (--replay=PATH) or the next argument (--replay PATH).
                        if (inlineValue is not null)
                        {
                            replayPath = inlineValue;
                        }
                        else if (i + 1 < args.Count)
                        {
                            replayPath = args[++i];
                        }
                        else
                        {
                            error = "cursorialedit: '--replay' requires a journal file path.";
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(replayPath))
                        {
                            error = "cursorialedit: the '--replay' journal path is empty.";
                            return false;
                        }

                        break;
                    default:
                        error = $"cursorialedit: unknown option '{arg}'.";
                        return false;
                }

                continue;
            }

            paths.Add(arg);
        }

        // Help/version win over any path arguments (the conventional CLI behavior).
        if (showHelp)
        {
            options = new AppStartupOptions { ShowHelp = true };
            return true;
        }

        if (showVersion)
        {
            options = new AppStartupOptions { ShowVersion = true };
            return true;
        }

        if (paths.Count > 1)
        {
            error = "cursorialedit: opening multiple files is not supported in v1.";
            return false;
        }

        // Replay reconstructs its own initial document from the journal — a FILE (or --journal) makes
        // no sense alongside it, so reject the combination rather than silently ignore one.
        if (replayPath is not null)
        {
            if (paths.Count > 0)
            {
                error = "cursorialedit: '--replay' opens the journal's recorded document; do not also pass a FILE.";
                return false;
            }

            if (journalRequested)
            {
                error = "cursorialedit: '--replay' cannot be combined with '--journal'.";
                return false;
            }

            options = new AppStartupOptions { ReplayPath = replayPath };
            return true;
        }

        string? filePath = null;

        if (paths.Count == 1)
        {
            var path = paths[0];

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "cursorialedit: the file path argument is empty.";
                return false;
            }

            if (Directory.Exists(path))
            {
                error = $"cursorialedit: '{path}' is a directory; opening directories is not supported in v1.";
                return false;
            }

            // The file itself need not exist — WP11 decides whether a missing path means
            // create-on-save or an error prompt. Parsing only rejects what the spec defers.
            filePath = path;
        }

        options = new AppStartupOptions(filePath)
        {
            JournalRequested = journalRequested,
            JournalPath = journalPath,
        };
        return true;
    }
}
