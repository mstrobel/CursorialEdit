using System.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Persistence;

namespace CursorialEdit.Tests.Files;

/// <summary>
/// M1.WP11 gate — <see cref="DocumentFile"/> round-trip fidelity (spec §10.1): encoding
/// detection (UTF-8, UTF-8 BOM, UTF-16 LE/BE BOM, Latin-1 fallback), per-line mixed LF/CRLF
/// preservation across edits, the ensure-trailing-newline option in both settings, atomic-save
/// fault behavior, and the load→save byte-identity guarantee.
/// </summary>
public sealed class FileRoundTripTests
{
    /// <summary>Load → wrap in a buffer → save with no edits; returns the resulting bytes.</summary>
    private static byte[] RoundTrip(string path, bool ensureTrailingNewline)
    {
        var file = DocumentFile.Load(path);
        file.Save(new DocumentBuffer(file.Text), ensureTrailingNewline);
        return File.ReadAllBytes(path);
    }

    // ── encoding detection + byte fidelity ───────────────────────────────────────────────────────

    [Fact]
    public void Utf8NoBom_Detected_RoundTripsByteIdentical()
    {
        using var dir = new TempDocumentDirectory();
        byte[] original = Encoding.UTF8.GetBytes("héllo 汉字 👍\nsecond line\r\nthird\n");
        string path = dir.WriteBytes("utf8.md", original);

        var file = DocumentFile.Load(path);
        Assert.Equal(DocumentEncoding.Utf8, file.DetectedEncoding);
        Assert.False(file.HadBom);
        Assert.Equal("héllo 汉字 👍\nsecond line\r\nthird\n", file.Text);

        Assert.Equal(original, RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void Utf8Bom_Detected_BomPreservedByteIdentical()
    {
        using var dir = new TempDocumentDirectory();
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("bom content\nsecond\n")];
        string path = dir.WriteBytes("utf8-bom.md", original);

        var file = DocumentFile.Load(path);
        Assert.Equal(DocumentEncoding.Utf8, file.DetectedEncoding);
        Assert.True(file.HadBom);
        Assert.Equal("bom content\nsecond\n", file.Text); // BOM never leaks into the document text

        Assert.Equal(original, RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void Utf16LeBom_Detected_RoundTripsByteIdentical()
    {
        using var dir = new TempDocumentDirectory();
        const string content = "utf-16 content é 汉\r\nsecond line\n";
        byte[] original = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(content)];
        string path = dir.WriteBytes("utf16le.md", original);

        var file = DocumentFile.Load(path);
        Assert.Equal(DocumentEncoding.Utf16LittleEndian, file.DetectedEncoding);
        Assert.True(file.HadBom);
        Assert.Equal(content, file.Text);

        Assert.Equal(original, RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void Utf16BeBom_Detected_RoundTripsByteIdentical()
    {
        using var dir = new TempDocumentDirectory();
        const string content = "big endian é\nsecond\r\n";
        byte[] original = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(content)];
        string path = dir.WriteBytes("utf16be.md", original);

        var file = DocumentFile.Load(path);
        Assert.Equal(DocumentEncoding.Utf16BigEndian, file.DetectedEncoding);
        Assert.True(file.HadBom);
        Assert.Equal(content, file.Text);

        Assert.Equal(original, RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToLatin1_RoundTripsByteIdentical()
    {
        using var dir = new TempDocumentDirectory();
        // 0xE9 ('é' in Latin-1) standing alone is ill-formed UTF-8 → the documented fallback.
        byte[] original = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n', 0xFF, (byte)'\n'];
        string path = dir.WriteBytes("latin1.md", original);

        var file = DocumentFile.Load(path);
        Assert.Equal(DocumentEncoding.Latin1, file.DetectedEncoding);
        Assert.False(file.HadBom);
        Assert.Equal("café\nÿ\n", file.Text);

        Assert.Equal(original, RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        using var dir = new TempDocumentDirectory();
        Assert.Throws<FileNotFoundException>(() => DocumentFile.Load(dir.PathFor("absent.md")));
    }

    [Fact]
    public void CreateNew_DefaultsToUtf8NoBom_PlatformEnding_EmptyText()
    {
        using var dir = new TempDocumentDirectory();
        var file = DocumentFile.CreateNew(dir.PathFor("fresh.md"));

        Assert.Equal(DocumentEncoding.Utf8, file.DetectedEncoding);
        Assert.False(file.HadBom);
        Assert.Equal(OperatingSystem.IsWindows() ? LineEnding.CrLf : LineEnding.Lf, file.DominantEnding);
        Assert.Equal(string.Empty, file.Text);
        Assert.False(File.Exists(file.Path)); // nothing on disk until the first save
    }

    // ── mixed line endings ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void MixedEndings_PreservedPerLine_AcrossAnEdit()
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("mixed.md", Encoding.UTF8.GetBytes("one\r\ntwo\nthree\r\nfour\n"));

        var file = DocumentFile.Load(path);
        var buffer = new DocumentBuffer(file.Text);

        // Edit line 1 ("two") only; every line keeps its own terminator byte-exactly.
        buffer.Apply(new TextPosition(1, 3), new TextPosition(1, 3), "!");
        file.Save(buffer, ensureTrailingNewline: true);

        Assert.Equal("one\r\ntwo!\nthree\r\nfour\n", File.ReadAllText(path));
    }

    // ── ensure-trailing-newline: both settings ───────────────────────────────────────────────────

    [Theory]
    [InlineData("a\r\nb\r\nc", "a\r\nb\r\nc\r\n")] // CRLF-dominant → CRLF appended
    [InlineData("a\nb", "a\nb\n")]                 // LF-dominant → LF appended
    [InlineData("a\r\nb\nc", "a\r\nb\nc\r\n")]     // 1:1 tie → the file's FIRST terminator wins
    public void EnsureOn_AppendsOneDominantEnding_WhenUnterminated(string content, string expected)
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("unterminated.md", Encoding.UTF8.GetBytes(content));

        Assert.Equal(Encoding.UTF8.GetBytes(expected), RoundTrip(path, ensureTrailingNewline: true));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\nb")]
    public void EnsureOff_IsByteIdentical_EvenWhenUnterminated(string content)
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("unterminated.md", Encoding.UTF8.GetBytes(content));

        Assert.Equal(Encoding.UTF8.GetBytes(content), RoundTrip(path, ensureTrailingNewline: false));
    }

    [Fact]
    public void EnsureOn_AlreadyTerminated_NeverDoubles()
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("terminated.md", Encoding.UTF8.GetBytes("done\n"));

        Assert.Equal(Encoding.UTF8.GetBytes("done\n"), RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void EnsureOn_EmptyFile_StaysEmpty()
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("empty.md", []);

        Assert.Empty(RoundTrip(path, ensureTrailingNewline: true));
    }

    [Fact]
    public void EnsureOn_TrailingBareCr_LeftUntouched_WhenDominantIsLf()
    {
        using var dir = new TempDocumentDirectory();
        // A bare '\r' is content, not a terminator; appending '\n' would fuse it into a CRLF and
        // change the content — the documented guard skips the normalization instead.
        string path = dir.WriteBytes("bare-cr.md", Encoding.UTF8.GetBytes("a\nb\r"));

        Assert.Equal(Encoding.UTF8.GetBytes("a\nb\r"), RoundTrip(path, ensureTrailingNewline: true));
    }

    // ── byte-identity sweep across encodings (the round-trip guarantee) ──────────────────────────

    public static TheoryData<string, byte[]> IdentityFixtures => new()
    {
        { "utf8", Encoding.UTF8.GetBytes("plain\nmixed\r\ncontent 汉字\n") },
        { "utf8-bom", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("bom\ncontent\n")] },
        { "utf8-no-trailing", Encoding.UTF8.GetBytes("no\ntrailing newline") },
        { "utf16le", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("le é\r\ncontent\n")] },
        { "utf16be", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("be é\ncontent\r\n")] },
        { "latin1", [0xE9, (byte)'\n', 0x80, 0xFE, (byte)'\r', (byte)'\n'] },
        { "empty", [] },
        { "lone-cr-inside", Encoding.UTF8.GetBytes("alpha\rbeta\n") }, // bare CR is ordinary content
    };

    [Theory]
    [MemberData(nameof(IdentityFixtures))]
    public void LoadThenSave_NoEdits_EnsureOff_IsByteIdentical(string name, byte[] original)
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes(name + ".md", original);

        Assert.Equal(original, RoundTrip(path, ensureTrailingNewline: false));
    }

    // ── atomic save (fault injection) ────────────────────────────────────────────────────────────

    [Fact]
    public void FailedMove_Throws_AndLeavesNoTempResidue()
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("victim.md", Encoding.UTF8.GetBytes("content\n"));
        var file = DocumentFile.Load(path);

        // Fault: the destination becomes a DIRECTORY, so the temp-then-move rename must fail
        // after the temp file was fully written.
        File.Delete(path);
        Directory.CreateDirectory(path);
        try
        {
            Assert.ThrowsAny<IOException>(() => file.Save(new DocumentBuffer(file.Text)));

            Assert.True(Directory.Exists(path)); // the destination was not clobbered
            Assert.Empty(dir.TempResidue());     // the failed save cleaned its temp file up
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public void FailedWrite_LeavesPreviousFileIntact_NoResidue()
    {
        if (OperatingSystem.IsWindows())
            return; // Unix-permission fault injection; the move-fault test covers Windows

        using var dir = new TempDocumentDirectory();
        byte[] original = Encoding.UTF8.GetBytes("precious original\n");
        string path = dir.WriteBytes("victim.md", original);

        var file = DocumentFile.Load(path);
        var buffer = new DocumentBuffer(file.Text);
        buffer.Apply(new TextPosition(0, 0), new TextPosition(0, 0), "edited ");

        // Fault: the directory refuses new files, so creating the temp file itself fails —
        // before the destination could possibly be touched.
        File.SetUnixFileMode(dir.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => file.Save(buffer));
        }
        finally
        {
            File.SetUnixFileMode(
                dir.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(original, File.ReadAllBytes(path)); // atomicity: the old bytes survive a failed save
        Assert.Empty(dir.TempResidue());
    }

    // ── save-side normalization is save-side only ────────────────────────────────────────────────

    [Fact]
    public void EnsureOn_DoesNotMutateTheBuffer()
    {
        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("stays.md", Encoding.UTF8.GetBytes("unterminated"));

        var file = DocumentFile.Load(path);
        var buffer = new DocumentBuffer(file.Text);
        file.Save(buffer, ensureTrailingNewline: true);

        Assert.Equal("unterminated", buffer.GetText());                       // buffer untouched
        Assert.Equal("unterminated\n", File.ReadAllText(path));               // file normalized
    }
}
