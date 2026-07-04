using CursorialEdit.Dialogs;

namespace CursorialEdit.Tests.Files;

/// <summary>
/// A unique real temp directory for document files (the PersistenceFixtures pattern): tests write
/// byte fixtures into it and the tree is deleted on dispose, restoring write permission first so
/// the read-only fault-injection tests never leave residue behind.
/// </summary>
internal sealed class TempDocumentDirectory : IDisposable
{
    /// <summary>Unique per instance, so parallel test classes never share state.</summary>
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "CursorialEdit.Tests.Files", Guid.NewGuid().ToString("N"));

    public TempDocumentDirectory() => Directory.CreateDirectory(Root);

    /// <summary>An absolute path for <paramref name="name"/> inside the directory (not created).</summary>
    public string PathFor(string name) => Path.Combine(Root, name);

    /// <summary>Writes <paramref name="bytes"/> as <paramref name="name"/> and returns the absolute path.</summary>
    public string WriteBytes(string name, byte[] bytes)
    {
        string path = PathFor(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The <c>*.tmp</c> residue files currently in the directory (atomic-save cleanliness asserts).</summary>
    public string[] TempResidue() => Directory.GetFiles(Root, "*.tmp");

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(Root))
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A canned-answer <see cref="ITaskDialogService"/>: records every request and completes
/// synchronously with <see cref="Answer"/> (null = dismissed). Synchronous completion keeps the
/// shell's post-prompt continuation on the calling test thread — no host pumping needed for the
/// triad-outcome tests.
/// </summary>
internal sealed class FakeTaskDialogService(TaskDialogButton? answer) : ITaskDialogService
{
    /// <summary>Every request shown, in order.</summary>
    public List<TaskDialogRequest> Requests { get; } = [];

    /// <summary>The button every prompt completes with; null completes as dismissed.</summary>
    public TaskDialogButton? Answer { get; set; } = answer;

    public Task<TaskDialogResult> ShowAsync(TaskDialogRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(new TaskDialogResult(Answer, request.VerificationChecked));
    }
}
