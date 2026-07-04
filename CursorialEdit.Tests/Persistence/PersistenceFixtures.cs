using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Persistence;

/// <summary>
/// An <see cref="IAppStatePathProvider"/> over a unique temp directory, with a fault switch:
/// setting <see cref="FailWith"/> makes every resolution throw — the plan's "throwing path
/// provider" failure simulation. Deleting the tree on dispose keeps test runs residue-free.
/// </summary>
internal sealed class TempStatePathProvider : IAppStatePathProvider, IDisposable
{
    /// <summary>Unique per instance, so parallel test classes never share state.</summary>
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "CursorialEdit.Tests.Persistence", Guid.NewGuid().ToString("N"));

    /// <summary>The directory this provider serves (without going through the fault switch).</summary>
    public string JournalDirectory => Path.Combine(Root, "journals");

    /// <summary>When set, every <see cref="GetJournalDirectory"/> call throws this.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// Invoked at the top of every <see cref="GetJournalDirectory"/> call, on the caller's
    /// thread — a blocking hook here holds the autosave drainer mid-operation, which is how the
    /// queue-displacement tests order events deterministically.
    /// </summary>
    public Action? OnResolve { get; set; }

    /// <summary>Total <see cref="GetJournalDirectory"/> calls — one per journal write attempt, so retry policies are countable.</summary>
    public int ResolveCount => _resolveCount;

    private int _resolveCount;

    public string GetJournalDirectory()
    {
        Interlocked.Increment(ref _resolveCount);
        OnResolve?.Invoke();

        if (FailWith is { } error)
            throw error;

        return JournalDirectory;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// One assembled autosave stack on a fake clock: buffer → controller → service → journal over a
/// <see cref="TempStatePathProvider"/>. <see cref="Edit"/> appends at the document end through
/// the controller funnel, which is all the service observes.
/// </summary>
internal sealed class AutosaveHarness : IDisposable
{
    public AutosaveHarness(string text = "", DocumentKey? key = null, TimeSpan? debounceInterval = null)
    {
        Paths = new TempStatePathProvider();
        Buffer = new DocumentBuffer(text);
        Controller = new EditController(Buffer, Time);
        Key = key ?? DocumentKey.NewUntitled();
        Journal = new AutosaveJournal(Paths, Key);
        Service = new AutosaveService(Controller, Journal, Time, debounceInterval);
    }

    public TempStatePathProvider Paths { get; }
    public DocumentBuffer Buffer { get; }
    public EditController Controller { get; }
    public FakeTimeProvider Time { get; } = new();
    public DocumentKey Key { get; }
    public AutosaveJournal Journal { get; }
    public AutosaveService Service { get; }

    /// <summary>Inserts <paramref name="text"/> at the end of the document as a Typing edit.</summary>
    public void Edit(string text)
    {
        int lastLine = Buffer.LineCount - 1;
        var end = new TextPosition(lastLine, Buffer.GetLine(lastLine).Text.Length);
        Controller.Apply(new Edit(end, "", text), EditKind.Typing, new CaretState(end), new CaretState(end));
    }

    public void Dispose()
    {
        Service.Dispose();
        Paths.Dispose();
    }
}
