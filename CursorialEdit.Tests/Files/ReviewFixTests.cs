using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Persistence;

namespace CursorialEdit.Tests.Files;

/// <summary>
/// Regressions for the wave-4/5 review's file-lifecycle findings.
/// </summary>
public sealed class ReviewFixTests
{
    /// <summary>
    /// The atomic temp-then-rename save must carry the destination's existing POSIX mode onto the
    /// replacement — a plain rename installs the umask-default mode and silently strips the execute
    /// bit (an opened+saved shell script would stop running). POSIX-only (Windows has no unix mode).
    /// </summary>
    [Fact]
    public void Save_PreservesExistingUnixFileMode()
    {
        if (OperatingSystem.IsWindows())
            return; // no unix mode on Windows — File.GetUnixFileMode would throw

        using var dir = new TempDocumentDirectory();
        string path = dir.WriteBytes("script.sh", "#!/bin/sh\necho hi\n"u8.ToArray());

        var executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                         | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                         | UnixFileMode.OtherRead | UnixFileMode.OtherExecute; // 0755
        File.SetUnixFileMode(path, executable);

        var file = DocumentFile.Load(path);
        var buffer = new DocumentBuffer(file.Text);
        buffer.Apply(new TextPosition(1, 0), new TextPosition(1, 0), "echo edited\n");
        file.Save(buffer);

        Assert.Equal(executable, File.GetUnixFileMode(path)); // execute bits survived the save
    }

    /// <summary>
    /// A directory path is not openable: <c>File.Exists</c> is false for a directory, so without an
    /// explicit guard it would fall into the create-new path and become a phantom document that only
    /// fails at save. Opening a directory must fail cleanly and leave the current document untouched.
    /// </summary>
    [Fact]
    public void OpenFile_DirectoryPath_FailsCleanly_AndLeavesCurrentDocument()
    {
        using var dir = new TempDocumentDirectory();
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 10) });

        var shell = new EditorShell();
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());
        shell.NewDocument(host.Time);
        shell.WireDocument("existing content", host.Time);
        Assert.True(host.RunUntilIdle());

        bool opened = shell.OpenFileAsync(dir.Root, host.Time).Result;

        Assert.False(opened); // the directory was rejected
        Assert.Equal("existing content", shell.Document!.GetText()); // current document untouched
        // The status model (not the width-clipped row) names the failure.
        Assert.Contains("directory", shell.StatusTextPart.Text, StringComparison.OrdinalIgnoreCase);
    }
}
