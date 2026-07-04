using CursorialEdit.Document.Persistence;

namespace CursorialEdit.Tests.Persistence;

/// <summary>
/// M1.WP12 — journal keying: path keys canonicalize (spelling variants and symlinks converge)
/// and hash to a filesystem-safe SHA-256 filename; untitled keys carry a scannable prefix and a
/// unique id; the descriptor is the header-ready identity.
/// </summary>
public class DocumentKeyTests
{
    [Fact]
    public void SamePath_DifferentSpellings_ProduceTheSameKey()
    {
        string path = Path.Combine(Path.GetTempPath(), "cursorialedit-key-tests", "doc.md");
        string dotted = Path.Combine(Path.GetTempPath(), "cursorialedit-key-tests", ".", "doc.md");
        string relativeHop = Path.Combine(Path.GetTempPath(), "cursorialedit-key-tests", "sub", "..", "doc.md");

        var canonical = DocumentKey.ForPath(path);
        Assert.Equal(canonical, DocumentKey.ForPath(dotted));
        Assert.Equal(canonical, DocumentKey.ForPath(relativeHop));
        Assert.Equal(canonical.JournalFileName, DocumentKey.ForPath(dotted).JournalFileName);
    }

    [Fact]
    public void PathKey_FileNameIsSha256Hex_NoPathCharactersLeak()
    {
        var key = DocumentKey.ForPath(Path.Combine(Path.GetTempPath(), "spaced dir", "wüld näme?.md"));

        Assert.EndsWith(".journal", key.JournalFileName, StringComparison.Ordinal);
        string stem = key.JournalFileName[..^".journal".Length];
        Assert.Equal(64, stem.Length); // SHA-256 hex
        Assert.All(stem, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void DifferentPaths_ProduceDifferentFileNames()
    {
        var a = DocumentKey.ForPath(Path.Combine(Path.GetTempPath(), "a.md"));
        var b = DocumentKey.ForPath(Path.Combine(Path.GetTempPath(), "b.md"));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.JournalFileName, b.JournalFileName);
    }

    [Fact]
    public void SymlinkAndTarget_ProduceTheSameKey()
    {
        if (OperatingSystem.IsWindows())
            return; // symlink creation is privileged on Windows; the canonicalization seam is M6's to finalize

        string root = Path.Combine(Path.GetTempPath(), "CursorialEdit.Tests.Persistence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "real.md");
            File.WriteAllText(target, "content");
            string link = Path.Combine(root, "alias.md");
            File.CreateSymbolicLink(link, target);

            Assert.Equal(DocumentKey.ForPath(target), DocumentKey.ForPath(link));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SameFile_DifferentCasings_ProduceTheSameKey_OnCaseInsensitivePlatforms()
    {
        // Review wave3-1: on macOS/Windows a journal written under one casing must be found when
        // the file is reopened under another, so both spellings must canonicalize to the file's
        // TRUE on-disk casing. Linux is exact by design (case-sensitive filesystem) — skip.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return;

        string root = Path.Combine(Path.GetTempPath(), "CursorialEdit.Tests.Persistence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string truePath = Path.Combine(root, "CaseProbe.md");
            File.WriteAllText(truePath, "content"); // a real file, so true-casing resolution is exercised

            var canonical = DocumentKey.ForPath(truePath);
            var lowered = DocumentKey.ForPath(Path.Combine(root, "caseprobe.md"));
            var shouted = DocumentKey.ForPath(Path.Combine(root, "CASEPROBE.MD"));

            Assert.Equal(canonical, lowered);
            Assert.Equal(canonical, shouted);
            Assert.EndsWith("CaseProbe.md", canonical.SourcePath!, StringComparison.Ordinal); // the on-disk casing won
            Assert.EndsWith("CaseProbe.md", lowered.SourcePath!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NotYetExistingFile_DifferentCasings_FoldToTheSameKey_OnCaseInsensitivePlatforms()
    {
        // The fold fallback: a path with no on-disk entry still converges every spelling.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return;

        string root = Path.Combine(Path.GetTempPath(), "cursorialedit-key-tests-nonexistent");

        Assert.Equal(
            DocumentKey.ForPath(Path.Combine(root, "Draft.MD")),
            DocumentKey.ForPath(Path.Combine(root, "draft.md")));
    }

    [Fact]
    public void DifferentCasings_StayDistinct_OnLinux()
    {
        // Case-sensitive filesystems: distinct casings ARE distinct files; folding would merge
        // two real documents onto one journal key.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return;

        Assert.NotEqual(
            DocumentKey.ForPath(Path.Combine(Path.GetTempPath(), "Doc.md")),
            DocumentKey.ForPath(Path.Combine(Path.GetTempPath(), "doc.md")));
    }

    [Fact]
    public void PathKey_Descriptor_CarriesTheCanonicalPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "doc.md");
        var key = DocumentKey.ForPath(path);

        Assert.StartsWith("path:", key.Descriptor, StringComparison.Ordinal);
        Assert.Equal(key.SourcePath, key.Descriptor["path:".Length..]);
        Assert.False(key.IsUntitled);
        Assert.Null(key.UntitledId);
    }

    [Fact]
    public void UntitledKeys_HaveScannablePrefix_AndUniqueIds()
    {
        var first = DocumentKey.NewUntitled();
        var second = DocumentKey.NewUntitled();

        Assert.StartsWith("untitled-", first.JournalFileName, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.True(first.IsUntitled);
        Assert.Null(first.SourcePath);
    }

    [Fact]
    public void UntitledKey_IsReproducibleFromItsId()
    {
        var key = DocumentKey.NewUntitled();
        var readopted = DocumentKey.ForUntitled(key.UntitledId!.Value); // M6 recovery re-adoption path

        Assert.Equal(key, readopted);
        Assert.Equal(key.JournalFileName, readopted.JournalFileName);
        Assert.Equal(key.Descriptor, readopted.Descriptor);
    }
}

/// <summary>
/// M1.WP12 — the default journal-directory resolution (§3.2 resolution 8): journals are app
/// state under the platform state directory, never under the framework's <c>~/.cursorial</c>
/// settings store. Only the current platform's shape is asserted; env-var manipulation is
/// avoided because tests run in parallel.
/// </summary>
public class AppStatePathProviderTests
{
    [Fact]
    public void JournalDirectory_IsRooted_UnderTheAppStateDirectory()
    {
        string directory = new AppStatePathProvider().GetJournalDirectory();

        Assert.True(Path.IsPathRooted(directory));
        Assert.Equal("journals", Path.GetFileName(directory));
        Assert.Equal("CursorialEdit", Path.GetFileName(Path.GetDirectoryName(directory)));
    }

    [Fact]
    public void JournalDirectory_FollowsThePlatformConvention()
    {
        string directory = new AppStatePathProvider().GetJournalDirectory();

        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(
                Path.Combine("Library", "Application Support", "CursorialEdit"),
                directory, StringComparison.Ordinal);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                directory, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            string? xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            string expectedRoot = !string.IsNullOrEmpty(xdgStateHome) && Path.IsPathRooted(xdgStateHome)
                ? xdgStateHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            Assert.Equal(Path.Combine(expectedRoot, "CursorialEdit", "journals"), directory);
        }
    }

    [Fact]
    public void JournalDirectory_IsNeverTheFrameworkSettingsStore()
    {
        // The resolution-8 split: settings live in FW-A's ~/.cursorial; journals must not.
        string directory = new AppStatePathProvider().GetJournalDirectory();
        Assert.DoesNotContain(".cursorial", directory, StringComparison.OrdinalIgnoreCase);
    }
}
