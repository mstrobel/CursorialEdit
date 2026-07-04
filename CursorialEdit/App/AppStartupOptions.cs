using System.Diagnostics.CodeAnalysis;

namespace CursorialEdit.App;

/// <summary>
/// The parsed command-line startup options (spec §10.4): zero arguments start an empty editor; a
/// single file-path argument opens that file (M1.WP11 consumes <see cref="FilePath"/> — this type
/// only carries it). Dash-prefixed arguments are flags, not paths: <c>--help</c>/<c>-h</c>/<c>-?</c>
/// set <see cref="ShowHelp"/> (Program prints <see cref="UsageText"/> to stdout and exits 0),
/// <c>--version</c> sets <see cref="ShowVersion"/> (Program prints the version and exits 0),
/// <c>--</c> ends flag parsing so a file literally named like an option can be opened, and any
/// other dash-argument is a parse failure. Directory arguments and multiple paths are
/// spec-<b>deferred</b> (they tie to multi-document support), so they are parse failures too:
/// <c>Program</c> prints the error plus <see cref="UsageText"/> to stderr and exits with code 2.
/// </summary>
/// <param name="FilePath">The document to open at startup, or <see langword="null"/> for an empty editor.</param>
public sealed record AppStartupOptions(string? FilePath = null)
{
    /// <summary>Whether <c>--help</c>/<c>-h</c>/<c>-?</c> was given: print <see cref="UsageText"/> to stdout and exit 0 instead of starting the editor.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Whether <c>--version</c> was given: print the informational version to stdout and exit 0 instead of starting the editor.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>The usage text printed to stderr when the command line cannot be parsed (and to stdout for <c>--help</c>).</summary>
    public const string UsageText =
        """
        Usage: cursorialedit [OPTIONS] [FILE]

        Opens FILE in the editor, or starts with an empty document when no FILE is
        given. Directories and multiple files are not supported in v1.

        Options:
          -h, -?, --help  Print this usage text and exit.
          --version       Print the version and exit.
          --              End of options; later arguments are file paths even when
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
        var paths = new List<string>();

        foreach (var arg in args)
        {
            if (!endOfFlags && arg.StartsWith('-'))
            {
                switch (arg)
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

        if (paths.Count == 0)
        {
            options = new AppStartupOptions();
            return true;
        }

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
        options = new AppStartupOptions(path);
        return true;
    }
}
