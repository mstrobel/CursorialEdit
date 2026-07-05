using CursorialEdit.App;

namespace CursorialEdit.Tests.App;

/// <summary>
/// M1.WP2 — the spec §10.4 CLI contract: no argument = empty editor; one file path = open it
/// (stored for WP11); directories and multiple paths are spec-deferred usage errors (Program
/// prints <see cref="AppStartupOptions.UsageText"/> and exits 2). Wave 2 added flag handling:
/// dash-prefixed arguments are options — help/version outcomes, <c>--</c> end-of-flags, and
/// unknown options as usage errors — never file paths.
/// </summary>
public sealed class AppStartupOptionsTests
{
    [Fact]
    public void Parse_NoArguments_YieldsEmptyEditor()
    {
        Assert.True(AppStartupOptions.TryParse([], out var options, out var error));
        Assert.Null(options!.FilePath);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_SingleFilePath_IsCaptured()
    {
        Assert.True(AppStartupOptions.TryParse(["notes.md"], out var options, out _));
        Assert.Equal("notes.md", options!.FilePath);
    }

    [Fact]
    public void Parse_SingleFilePath_NeedNotExist()
    {
        // WP11 decides create-vs-prompt semantics; parsing must not reject a missing file.
        var path = Path.Combine(Path.GetTempPath(), $"cursorialedit-does-not-exist-{Guid.NewGuid():N}.md");

        Assert.True(AppStartupOptions.TryParse([path], out var options, out _));
        Assert.Equal(path, options!.FilePath);
    }

    [Fact]
    public void Parse_MultipleArguments_IsUsageError()
    {
        Assert.False(AppStartupOptions.TryParse(["a.md", "b.md"], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("multiple files", error);
    }

    [Fact]
    public void Parse_DirectoryArgument_IsUsageError()
    {
        var directory = Directory.CreateTempSubdirectory("cursorialedit-test-").FullName;
        try
        {
            Assert.False(AppStartupOptions.TryParse([directory], out var options, out var error));
            Assert.Null(options);
            Assert.Contains("directory", error);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankArgument_IsUsageError(string argument)
    {
        Assert.False(AppStartupOptions.TryParse([argument], out var options, out var error));
        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void UsageText_NamesTheExecutableAndTheSingleFileForm()
    {
        Assert.Contains("cursorialedit", AppStartupOptions.UsageText);
        Assert.Contains("[FILE]", AppStartupOptions.UsageText);
    }

    // ---- Flags (wave 2): dash-prefixed arguments are options, not paths ----------------------

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void Parse_HelpFlag_YieldsShowHelp(string flag)
    {
        Assert.True(AppStartupOptions.TryParse([flag], out var options, out var error));
        Assert.True(options!.ShowHelp);
        Assert.False(options.ShowVersion);
        Assert.Null(options.FilePath);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_VersionFlag_YieldsShowVersion()
    {
        Assert.True(AppStartupOptions.TryParse(["--version"], out var options, out var error));
        Assert.True(options!.ShowVersion);
        Assert.False(options.ShowHelp);
        Assert.Null(options.FilePath);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_HelpWinsOverOtherArguments()
    {
        // The conventional CLI behavior: help short-circuits path handling entirely.
        Assert.True(AppStartupOptions.TryParse(["a.md", "--help", "b.md"], out var options, out _));
        Assert.True(options!.ShowHelp);
        Assert.Null(options.FilePath);
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("-")]
    [InlineData("--weird-name.md")]
    public void Parse_UnknownOrLoneDashOption_IsUsageError_NeverAFilePath(string flag)
    {
        // "-v" is deliberately NOT a version alias (unclaimed for v1); "-" and any other
        // dash-token are option-looking, so they can never silently open an editor.
        Assert.False(AppStartupOptions.TryParse([flag], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("unknown option", error);
        Assert.Contains(flag, error);
    }

    [Fact]
    public void Parse_DoubleDash_EndsFlagParsing_SoDashPrefixedFileOpens()
    {
        Assert.True(AppStartupOptions.TryParse(["--", "--weird-name.md"], out var options, out var error));
        Assert.Equal("--weird-name.md", options!.FilePath);
        Assert.False(options.ShowHelp);
        Assert.False(options.ShowVersion);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_DoubleDashAlone_YieldsEmptyEditor()
    {
        Assert.True(AppStartupOptions.TryParse(["--"], out var options, out _));
        Assert.Null(options!.FilePath);
        Assert.False(options.ShowHelp);
        Assert.False(options.ShowVersion);
    }

    [Fact]
    public void Parse_DoubleDash_MultiplePathsStillAUsageError()
    {
        Assert.False(AppStartupOptions.TryParse(["--", "-a.md", "-b.md"], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("multiple files", error);
    }

    [Fact]
    public void UsageText_NamesTheFlags()
    {
        Assert.Contains("--help", AppStartupOptions.UsageText);
        Assert.Contains("--version", AppStartupOptions.UsageText);
        Assert.Contains("--", AppStartupOptions.UsageText);
    }

    // ---- Diagnostic journal/replay flags -----------------------------------------------------

    [Fact]
    public void Parse_JournalFlag_NoPath_RequestsDefaultJournal()
    {
        Assert.True(AppStartupOptions.TryParse(["--journal"], out var options, out var error));
        Assert.True(options!.JournalRequested);
        Assert.Null(options.JournalPath); // Program derives + prints the default
        Assert.Null(options.FilePath);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_JournalFlag_WithInlinePath_IsCaptured()
    {
        Assert.True(AppStartupOptions.TryParse(["--journal=/tmp/session.jsonl"], out var options, out _));
        Assert.True(options!.JournalRequested);
        Assert.Equal("/tmp/session.jsonl", options.JournalPath);
    }

    [Fact]
    public void Parse_JournalFlag_SpaceSeparatedPath_ThenFile()
    {
        // The documented sanity form: `--journal PATH FILE` — PATH is consumed, FILE follows.
        Assert.True(AppStartupOptions.TryParse(["--journal", "/tmp/j.jsonl", "notes.md"], out var options, out _));
        Assert.True(options!.JournalRequested);
        Assert.Equal("/tmp/j.jsonl", options.JournalPath);
        Assert.Equal("notes.md", options.FilePath);
    }

    [Fact]
    public void Parse_JournalFlag_SpaceSeparatedPath_NoFile()
    {
        Assert.True(AppStartupOptions.TryParse(["--journal", "/tmp/j.jsonl"], out var options, out _));
        Assert.True(options!.JournalRequested);
        Assert.Equal("/tmp/j.jsonl", options.JournalPath);
        Assert.Null(options.FilePath);
    }

    [Fact]
    public void Parse_JournalFlag_BareBeforeAnotherFlag_UsesDefault()
    {
        // A following flag is not swallowed as the journal path — bare --journal keeps the default.
        Assert.True(AppStartupOptions.TryParse(["--journal", "--version"], out var options, out _));
        Assert.True(options!.ShowVersion);
    }

    [Fact]
    public void Parse_JournalEmptyValue_IsUsageError()
    {
        Assert.False(AppStartupOptions.TryParse(["--journal="], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("--journal", error);
    }

    [Fact]
    public void Parse_ReplayFlag_SpaceSeparatedValue_IsCaptured()
    {
        Assert.True(AppStartupOptions.TryParse(["--replay", "/tmp/session.jsonl"], out var options, out var error));
        Assert.Equal("/tmp/session.jsonl", options!.ReplayPath);
        Assert.Null(options.FilePath);
        Assert.False(options.JournalRequested);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_ReplayFlag_InlineValue_IsCaptured()
    {
        Assert.True(AppStartupOptions.TryParse(["--replay=/tmp/session.jsonl"], out var options, out _));
        Assert.Equal("/tmp/session.jsonl", options!.ReplayPath);
    }

    [Fact]
    public void Parse_ReplayFlag_MissingValue_IsUsageError()
    {
        Assert.False(AppStartupOptions.TryParse(["--replay"], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("--replay", error);
    }

    [Fact]
    public void Parse_ReplayWithFileArgument_IsUsageError()
    {
        // Replay reconstructs the journal's own document; a FILE alongside it is contradictory.
        Assert.False(AppStartupOptions.TryParse(["--replay", "/tmp/session.jsonl", "notes.md"], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("--replay", error);
    }

    [Fact]
    public void Parse_ReplayWithJournal_IsUsageError()
    {
        Assert.False(AppStartupOptions.TryParse(["--replay", "/tmp/s.jsonl", "--journal"], out var options, out var error));
        Assert.Null(options);
        Assert.Contains("--replay", error);
    }

    [Fact]
    public void UsageText_NamesTheJournalAndReplayFlags()
    {
        Assert.Contains("--journal", AppStartupOptions.UsageText);
        Assert.Contains("--replay", AppStartupOptions.UsageText);
    }
}
