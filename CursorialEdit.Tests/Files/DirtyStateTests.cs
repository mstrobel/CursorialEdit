// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine and
// the joined tasks (autosave drainer work, synchronously-completing lifecycle tasks) finish off the
// UI thread, so a bounded Wait cannot deadlock. Same posture as MessageBoxTests.
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Dialogs;
using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Persistence;
using CursorialEdit.Tests.Persistence;

namespace CursorialEdit.Tests.Files;

/// <summary>
/// M1.WP11 gate — the shell's file lifecycle under <c>UITestHost</c> (spec §10.1/§10.2 criteria):
/// the dirty dot cell-asserted on/off around edit and save, Ctrl+S to the known path, the
/// open/new/save lifecycle including the autosave journal's created-then-deleted arc on a fake
/// clock, the save prompt on the close path driven by real keys with the dialog cell-asserted
/// (<see cref="SavePrompt_ShownOnCloseWhenDirty"/>), and all three save-triad outcomes on
/// replace-when-dirty through a fake <see cref="ITaskDialogService"/>.
/// </summary>
public sealed class DirtyStateTests
{
    private const int Columns = 60;
    private const int Rows = 20;
    private static readonly TimeSpan Debounce = AutosaveService.DefaultDebounceInterval;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public required UITestHost Host { get; init; }
        public required EditorShell Shell { get; init; }
        public required TempDocumentDirectory Dir { get; init; }
        public required TempStatePathProvider StatePaths { get; init; }

        /// <summary>The startup document's path (when the harness opened one).</summary>
        public string? Path { get; init; }

        public void Dispose()
        {
            Host.Dispose();
            StatePaths.Dispose();
            Dir.Dispose();
        }
    }

    /// <summary>
    /// A shell over a real temp directory and a temp journal provider, with the startup document
    /// opened on the host's fake clock: <paramref name="fileName"/> pre-written with
    /// <paramref name="content"/>, or an untitled document when null.
    /// </summary>
    private static Harness Create(string? fileName = null, string content = "hello\n")
    {
        var dir = new TempDocumentDirectory();
        var statePaths = new TempStatePathProvider();
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(Columns, Rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(nameof(TestCapabilities.KittyTruecolor)),
        });

        string? path = null;
        if (fileName is not null)
            path = dir.WriteBytes(fileName, Encoding.UTF8.GetBytes(content));

        var shell = new EditorShell(new AppStartupOptions(path)) { AppStatePaths = statePaths };
        shell.OpenStartupDocument(host.Time);

        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());

        return new Harness { Host = host, Shell = shell, Dir = dir, StatePaths = statePaths, Path = path };
    }

    /// <summary>Appends <paramref name="text"/> at the document end through the controller funnel (a Typing edit).</summary>
    private static void Type(EditorShell shell, string text)
    {
        DocumentBuffer buffer = shell.Document!;
        int lastLine = buffer.LineCount - 1;
        var end = new TextPosition(lastLine, buffer.GetLine(lastLine).Text.Length);
        shell.Controller!.Apply(new Edit(end, "", text), EditKind.Typing, new CaretState(end), new CaretState(end));
    }

    /// <summary>The composited screen as one string, for dialog cell assertions.</summary>
    private static string ScreenText(UITestHost host)
    {
        var text = new StringBuilder();
        for (var row = 0; row < host.FrameBuffer.Rows; row++)
            text.AppendLine(host.GetRowText(row));

        return text.ToString();
    }

    private static string BottomRow(UITestHost host) => host.GetRowText(Rows - 1);

    /// <summary>Joins the autosave drainer so journal asserts observe completed I/O, not a race.</summary>
    private static void JoinAutosave(EditorShell shell) =>
        Assert.True(shell.AutosavePart!.PendingWrite.Wait(JoinTimeout), "autosave I/O did not drain");

    /// <summary>
    /// Pumps frames until <paramref name="condition"/> holds. Dialog completions hop over the
    /// thread pool before posting their continuations back to the dispatcher (the dialog TCS runs
    /// continuations asynchronously), so pumping alternates with short sleeps.
    /// </summary>
    private static void PumpUntil(UITestHost host, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + JoinTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached while pumping");
            host.RunUntilIdle();
            if (!condition())
                Thread.Sleep(10);
        }
    }

    // ── dirty dot: cell-asserted on/off around edit and save ────────────────────────────────────

    [Fact]
    public void OpenFile_ShowsFileName_AndCleanDotSlot()
    {
        using var h = Create("notes.md");

        var bottom = BottomRow(h.Host);
        Assert.StartsWith("  notes.md — " + EditorShell.AppName, bottom);
        Assert.False(h.Shell.IsDirty);
        Assert.Equal("notes.md", Path.GetFileName(h.Shell.FilePath!));
    }

    [Fact]
    public void DirtyDot_AppearsOnEdit_ClearsOnSave_AndTheFileIsWritten()
    {
        using var h = Create("notes.md");

        Type(h.Shell, "typed");
        Assert.True(h.Host.RunUntilIdle());
        Assert.True(h.Shell.IsDirty);
        Assert.StartsWith("● notes.md — " + EditorShell.AppName, BottomRow(h.Host));

        Assert.True(h.Shell.SaveAsync().Result);
        Assert.True(h.Host.RunUntilIdle());

        Assert.False(h.Shell.IsDirty);
        Assert.StartsWith("  notes.md — " + EditorShell.AppName, BottomRow(h.Host));
        Assert.Equal("hello\ntyped\n", File.ReadAllText(h.Path!)); // trailing newline ensured (spec default)
    }

    [Fact]
    public void CtrlS_SavesToTheKnownPath_AndClearsTheDot()
    {
        using var h = Create("notes.md");

        h.Shell.Editor.Focus();
        Assert.True(h.Host.RunUntilIdle());

        Type(h.Shell, "!");
        Assert.True(h.Host.RunUntilIdle());
        Assert.StartsWith("● ", BottomRow(h.Host));

        h.Host.SendKey(Key.Character, KeyModifiers.Control, "s");
        Assert.True(h.Host.RunUntilIdle());

        Assert.False(h.Shell.IsDirty);
        Assert.StartsWith("  notes.md — " + EditorShell.AppName, BottomRow(h.Host));
        Assert.Equal("hello\n!\n", File.ReadAllText(h.Path!));
    }

    [Fact]
    public void CtrlS_OnUntitled_ReportsTheM6StatusMessage_AndStaysDirty()
    {
        using var h = Create(fileName: null);

        h.Shell.Editor.Focus();
        Type(h.Shell, "unsaved words");
        Assert.True(h.Host.RunUntilIdle());

        h.Host.SendKey(Key.Character, KeyModifiers.Control, "s");
        Assert.True(h.Host.RunUntilIdle());

        Assert.True(h.Shell.IsDirty); // nothing was saved — Save As is M6
        var bottom = BottomRow(h.Host);
        Assert.StartsWith("● " + EditorShell.UntitledName, bottom);
        Assert.Contains("Save As", bottom);
    }

    // ── open/new/save lifecycle + the autosave journal arc ──────────────────────────────────────

    [Fact]
    public void Lifecycle_JournalCreatedByDebounce_ThenDeletedByCleanSave()
    {
        using var h = Create("notes.md");
        var journal = h.Shell.JournalPart!;
        Assert.False(journal.Exists());

        Type(h.Shell, " world");
        h.Host.Time.Advance(Debounce); // fake clock: the 5 s debounce fires synchronously
        JoinAutosave(h.Shell);

        Assert.True(journal.Exists());
        Assert.True(journal.TryRead(out var record));
        Assert.Equal("hello\n world", record!.Text);

        Assert.True(h.Shell.SaveAsync().Result); // clean save retires the journal (spec §12)
        JoinAutosave(h.Shell);

        Assert.False(journal.Exists());
        Assert.Equal("hello\n world\n", File.ReadAllText(h.Path!));
    }

    [Fact]
    public void NewDocument_KeysTheJournalAsUntitled_AndShowsUntitled()
    {
        using var h = Create(fileName: null);

        Assert.Null(h.Shell.FilePath);
        Assert.StartsWith("untitled-", h.Shell.JournalPart!.Key.JournalFileName);
        Assert.StartsWith("  " + EditorShell.UntitledName + " — " + EditorShell.AppName, BottomRow(h.Host));
    }

    [Fact]
    public void TerminalFocusLoss_TriggersAnImmediateJournalWrite()
    {
        using var h = Create("notes.md");
        var journal = h.Shell.JournalPart!;

        Type(h.Shell, " quick");
        h.Host.SendInput(new FocusEvent { HasFocus = false, Timestamp = h.Host.Time.GetUtcNow() }); // no clock advance — the accelerator path
        Assert.True(h.Host.RunUntilIdle());
        JoinAutosave(h.Shell);

        Assert.True(journal.Exists());
        Assert.True(journal.TryRead(out var record));
        Assert.Equal("hello\n quick", record!.Text);
    }

    [Fact]
    public void MissingStartupFile_OpensAsNewAtThatPath_SaveCreatesIt()
    {
        using var dir = new TempDocumentDirectory();
        using var statePaths = new TempStatePathProvider();
        using var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(Columns, Rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(nameof(TestCapabilities.KittyTruecolor)),
        });

        string path = dir.PathFor("brand-new.md");
        var shell = new EditorShell(new AppStartupOptions(path)) { AppStatePaths = statePaths };
        shell.OpenStartupDocument(host.Time);
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());

        Assert.Equal(path, shell.FilePath);
        Assert.False(shell.IsDirty);
        Assert.False(File.Exists(path)); // nothing on disk until the first save
        Assert.Contains("new file", BottomRow(host)); // the documented open-as-new status note

        Type(shell, "first words");
        Assert.True(shell.SaveAsync().Result);
        Assert.Equal("first words\n", File.ReadAllText(path));
    }

    // ── the save prompt on the close path (M1 gate: real keys, dialog cells) ────────────────────

    [Fact]
    public void SavePrompt_ShownOnCloseWhenDirty()
    {
        using var h = Create("notes.md");
        h.Shell.DialogService = new MessageBoxTaskDialogService(h.Host.Application);
        var journal = h.Shell.JournalPart!;
        var autosave = h.Shell.AutosavePart!;

        Type(h.Shell, " edited");
        h.Host.Time.Advance(Debounce); // let the journal exist, so the clean close observably retires it
        JoinAutosave(h.Shell);
        Assert.True(journal.Exists());
        Assert.True(h.Host.RunUntilIdle());

        // Start the close inside a dispatcher job so the frame-coherent sync context is ambient —
        // exactly how M5's Exit command will invoke it (the shell's documented calling contract).
        Task<bool>? close = null;
        h.Host.Dispatcher.Post(() => close = h.Shell.RequestCloseAsync());
        Assert.True(h.Host.RunUntilIdle());
        Assert.NotNull(close);
        Assert.False(close!.IsCompleted); // parked on the dialog

        // The save triad is on screen — cell-asserted.
        var screen = ScreenText(h.Host);
        Assert.Contains("Unsaved Changes", screen);
        Assert.Contains("Save changes to notes.md?", screen);
        Assert.Contains("Save", screen);
        Assert.Contains("Don't Save", screen);
        Assert.Contains("Cancel", screen);

        h.Host.SendKey(Key.Enter); // Save is the triad's default
        PumpUntil(h.Host, () => close!.IsCompleted);

        Assert.True(close!.Result);
        Assert.False(h.Shell.IsDirty);
        Assert.Equal("hello\n edited\n", File.ReadAllText(h.Path!)); // Save ran before the close proceeded

        Assert.True(autosave.PendingWrite.Wait(JoinTimeout)); // join the retired service's drainer
        Assert.False(journal.Exists());                       // clean close retired the journal
    }

    [Fact]
    public void SavePrompt_OnClose_Escape_CancelsTheClose()
    {
        using var h = Create("notes.md");
        h.Shell.DialogService = new MessageBoxTaskDialogService(h.Host.Application);

        Type(h.Shell, " edited");
        Assert.True(h.Host.RunUntilIdle());

        Task<bool>? close = null;
        h.Host.Dispatcher.Post(() => close = h.Shell.RequestCloseAsync());
        Assert.True(h.Host.RunUntilIdle());
        Assert.NotNull(close);

        h.Host.SendKey(Key.Escape); // Cancel carries IsCancel in the triad
        PumpUntil(h.Host, () => close!.IsCompleted);

        Assert.False(close!.Result);
        Assert.True(h.Shell.IsDirty);                              // nothing saved, nothing discarded
        Assert.Equal("hello\n", File.ReadAllText(h.Path!));        // disk untouched
        Assert.NotNull(h.Shell.AutosavePart);                      // services still live — close aborted
    }

    // ── the save triad on replace-when-dirty (fake service: all three outcomes) ─────────────────

    [Fact]
    public void OpenReplaceWhenDirty_Save_SavesThenOpens()
    {
        using var h = Create("first.md");
        var dialogs = new FakeTaskDialogService(TaskDialogButton.Save);
        h.Shell.DialogService = dialogs;

        Type(h.Shell, " kept");
        string second = h.Dir.WriteBytes("second.md", Encoding.UTF8.GetBytes("second doc\n"));

        Task<bool> open = h.Shell.OpenFileAsync(second, h.Host.Time);
        Assert.True(open.IsCompleted); // the fake service completes synchronously — no pumping needed
        Assert.True(open.Result);

        var request = Assert.Single(dialogs.Requests);
        Assert.Equal(TaskDialogButton.SaveTriad, request.Buttons);
        Assert.Contains("first.md", request.MainInstruction);

        Assert.Equal("hello\n kept\n", File.ReadAllText(h.Path!)); // Save ran before the replace
        Assert.Equal(second, h.Shell.FilePath);
        Assert.Equal("second doc\n", h.Shell.Document!.GetText());
        Assert.False(h.Shell.IsDirty);
    }

    [Fact]
    public void OpenReplaceWhenDirty_DontSave_DiscardsOpens_AndDeletesTheJournal()
    {
        using var h = Create("first.md");
        h.Shell.DialogService = new FakeTaskDialogService(TaskDialogButton.DontSave);

        Type(h.Shell, " discarded");
        h.Host.Time.Advance(Debounce);
        JoinAutosave(h.Shell);
        var oldJournal = h.Shell.JournalPart!;
        var oldAutosave = h.Shell.AutosavePart!;
        Assert.True(oldJournal.Exists());

        string second = h.Dir.WriteBytes("second.md", Encoding.UTF8.GetBytes("second doc\n"));
        Assert.True(h.Shell.OpenFileAsync(second, h.Host.Time).Result);

        Assert.Equal("hello\n", File.ReadAllText(h.Path!)); // the dirty content was NOT saved
        Assert.Equal(second, h.Shell.FilePath);
        Assert.False(h.Shell.IsDirty);

        Assert.True(oldAutosave.PendingWrite.Wait(JoinTimeout));
        Assert.False(oldJournal.Exists()); // explicit discard retired the abandoned journal
    }

    [Theory]
    [InlineData(true)]  // Cancel button
    [InlineData(false)] // dismissed without a choice — treated as cancel
    public void OpenReplaceWhenDirty_CancelOrDismiss_KeepsTheCurrentDocument(bool viaCancelButton)
    {
        using var h = Create("first.md");
        h.Shell.DialogService = new FakeTaskDialogService(viaCancelButton ? TaskDialogButton.Cancel : null);

        Type(h.Shell, " kept in buffer");
        string second = h.Dir.WriteBytes("second.md", Encoding.UTF8.GetBytes("second doc\n"));

        Assert.False(h.Shell.OpenFileAsync(second, h.Host.Time).Result);

        Assert.Equal(h.Path, h.Shell.FilePath);                      // still the first document
        Assert.True(h.Shell.IsDirty);                                // edits intact
        Assert.Equal("hello\n kept in buffer", h.Shell.Document!.GetText());
        Assert.Equal("hello\n", File.ReadAllText(h.Path!));          // and nothing was written
    }

    [Fact]
    public void OpenReplaceWhenClean_NeverPrompts()
    {
        using var h = Create("first.md");
        var dialogs = new FakeTaskDialogService(TaskDialogButton.Cancel); // would abort if consulted
        h.Shell.DialogService = dialogs;

        string second = h.Dir.WriteBytes("second.md", Encoding.UTF8.GetBytes("second doc\n"));
        Assert.True(h.Shell.OpenFileAsync(second, h.Host.Time).Result);

        Assert.Empty(dialogs.Requests);
        Assert.Equal(second, h.Shell.FilePath);
    }
}
