using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

using Cursorial.UI.Dialogs;
using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Document.Persistence;
using CursorialEdit.Views;

namespace CursorialEdit.App;

/// <summary>
/// The application's single-root shell element (M1.WP2/WP11) — no <c>Window</c>, per the bootstrap
/// recipe (integration notes §10): a <see cref="DockPanel"/> with a minimal one-row status line
/// docked bottom (dirty dot + document name, spec §10.1) and the <see cref="EditorControl"/>
/// filling the remainder. Owns the M1 file lifecycle: open (<see cref="OpenFileAsync"/> /
/// <see cref="OpenStartupDocument"/>), save (<see cref="SaveAsync"/>, bound to Ctrl+S), dirty
/// tracking, the save prompt on replace/close, and per-document autosave wiring.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> All members are UI-thread-only. The async lifecycle methods
/// (<see cref="OpenFileAsync"/>, <see cref="RequestCloseAsync"/>) must run with the frame-coherent
/// synchronization context ambient — i.e. from input handlers or dispatcher jobs, which is where
/// commands live — so their post-dialog continuations land back on the UI thread. Under
/// <c>UITestHost</c>, start them inside <c>Dispatcher.InvokeAsync</c> for the same reason.
/// </para>
/// <para>
/// <b>Dirty tracking.</b> Every <see cref="EditController.Changed"/> splice marks the document
/// dirty (undo included — undoing back to the last-saved content still shows dirty, an accepted
/// M1 simplification); a successful save clears it. The dot occupies a reserved two-cell slot so
/// toggling never reflows the status line.
/// </para>
/// <para>
/// <b>M1 scope choices (documented).</b> No file-browse dialogs until M6: the CLI argument and
/// Ctrl+S to the known path are the only file routes, and saving an untitled document reports a
/// status-line message (Save As is M6). A CLI path that does not exist opens as a new document
/// <i>at that path</i> with a status note — first save creates it. <see cref="RequestCloseAsync"/>
/// is the close-path seam M5's Exit command will call before shutting the application down; M1
/// itself has no Exit command, so tests drive the seam directly.
/// </para>
/// </remarks>
public sealed class EditorShell : DockPanel
{
    /// <summary>The application display name shown in the status line.</summary>
    public const string AppName = "CursorialEdit";

    /// <summary>The display name of a document with no path yet.</summary>
    public const string UntitledName = "Untitled";

    /// <summary>Status note when a CLI path did not exist and opened as a new document (spec §10.4).</summary>
    internal const string NewFileNote = "new file (created on first save)";

    /// <summary>Status note when saving an untitled document (Save As is M6 — plan §6 WP11).</summary>
    internal const string UntitledSaveNote = "no file path — Save As arrives in M6";

    private readonly EditorView _view = new();
    private readonly EditorCommands _commands;
    private readonly EditorRibbon _ribbon;
    private readonly EditorContextBar _contextBar;

    private bool _startupFocusRequested;

    private DocumentFile? _file;
    private DocumentKey? _documentKey;
    private AutosaveJournal? _journal;
    private AutosaveService? _autosave;
    private EditController? _dirtySource;
    private IAppStatePathProvider _appStatePaths = new AppStatePathProvider();
    private bool _isDirty;
    private bool _focusTriggerWired;

    /// <summary>Creates the shell with no startup file (empty editor).</summary>
    public EditorShell()
        : this(new AppStartupOptions())
    {
    }

    /// <summary>Creates the shell; <see cref="OpenStartupDocument"/> consumes <paramref name="startupOptions"/>.</summary>
    public EditorShell(AppStartupOptions startupOptions)
    {
        ArgumentNullException.ThrowIfNull(startupOptions);
        StartupOptions = startupOptions;

        // The dirty-dot slot: a reserved two-cell TextBlock ("● " when dirty) so toggling the
        // dirty state never reflows the status line.
        DirtyDotPart = new TextBlock { Text = string.Empty, Width = 2 };
        StatusTextPart = new TextBlock { Text = AppName };

        // Height-clamped to exactly one row: a status text longer than the terminal is clipped,
        // never allowed to hard-wrap the status line into a second row (WP11 — long note strings).
        StatusLinePart = new DockPanel { Height = 1 };
        SetDock(StatusLinePart, Dock.Bottom);
        SetDock(DirtyDotPart, Dock.Left);
        StatusLinePart.Children.Add(DirtyDotPart);
        StatusLinePart.Children.Add(StatusTextPart); // last child fills the rest of the row

        // The Bars ribbon (M5), docked at the TOP: Home/Table/View tabs whose buttons run the editor's real
        // operations against the persistent EditorControl. Additive — it does not steal startup focus (the
        // editor auto-focuses via RequestStartupEditorFocus below), and the editor view stays the LAST child so
        // LastChildFill keeps the document surface filling the space between the ribbon and the status line.
        // The editor operations shown on MORE THAN ONE bar surface (Cut/Copy/Paste on both the ribbon and the
        // right-click strip; the format ops the strip carries) are built ONCE as shared BarCommand instances so both
        // surfaces reflect one command state.
        _commands = new EditorCommands(Editor);

        _ribbon = new EditorRibbon(Editor, _commands);
        SetDock(_ribbon, Dock.Top);

        Children.Add(_ribbon);        // top
        Children.Add(StatusLinePart); // bottom
        Children.Add(_view);          // fill (LastChildFill (default)) — must remain the last child

        // The right-click Mini Toolbar (a horizontal light-dismiss strip of icon-only Cut/Copy/Paste + Bold/Italic/
        // InlineCode buttons) attached to the editor surface: a right-click over the document opens it at the pointer.
        // It runs the SAME EditorControl operations as the ribbon/keyboard and re-focuses the editor after an action.
        _contextBar = new EditorContextBar(_commands);
        MiniToolbar.SetBar(Editor, _contextBar.Bar);
    }

    /// <summary>The instantiable document surface (EditorControl + panel + presenter feed) — §3.2 resolution 12.</summary>
    public EditorView View => _view;

    /// <summary>The document surface control (focus owner, caret, input) hosted by <see cref="View"/>.</summary>
    public EditorControl Editor => _view.Editor;

    /// <summary>The Bars ribbon docked at the top (Home/Table/View tabs) — the M5 command surface over <see cref="Editor"/>.</summary>
    public EditorRibbon Ribbon => _ribbon;

    /// <summary>The editor's right-click Mini Toolbar (Cut/Copy/Paste + Bold/Italic/InlineCode) — the pointer command surface over <see cref="Editor"/>.</summary>
    public MiniToolbar ContextBar => _contextBar.Bar;

    /// <summary>The parsed CLI options this shell was launched with (<see cref="OpenStartupDocument"/> consumes <see cref="AppStartupOptions.FilePath"/>).</summary>
    public AppStartupOptions StartupOptions { get; }

    /// <summary>The open document's buffer; <see langword="null"/> until a document is wired.</summary>
    public DocumentBuffer? Document { get; private set; }

    /// <summary>The document's single mutation funnel; <see langword="null"/> until a document is wired.</summary>
    public EditController? Controller { get; private set; }

    /// <summary>The open document's path, or <see langword="null"/> for an untitled document (or before any document is wired).</summary>
    public string? FilePath => _file?.Path;

    /// <summary>Whether the document has unsaved changes (spec §10.1's modified indicator).</summary>
    public bool IsDirty => _isDirty;

    /// <summary>
    /// Whether saves guarantee a single trailing newline (spec §10.1's configurable decision,
    /// default on). Forwarded to <see cref="DocumentFile.Save"/>; the M7 configuration surface
    /// owns exposing it to users.
    /// </summary>
    public bool EnsureTrailingNewlineOnSave { get; set; } = true;

    /// <summary>
    /// The dialog seam for the unsaved-changes save triad (architecture §3.2 resolution 3).
    /// Injectable for tests; when unset, the first prompt resolves a
    /// <see cref="MessageBoxTaskDialogService"/> over <see cref="UIApplication.Current"/>.
    /// </summary>
    public ITaskDialogService? DialogService { get; set; }

    /// <summary>
    /// Where autosave journals live (implementation-plan §3.2 resolution 8). Injectable for
    /// tests (temp directories); takes effect when the next document is wired.
    /// </summary>
    public IAppStatePathProvider AppStatePaths
    {
        get => _appStatePaths;
        set => _appStatePaths = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The Markdig block producer feeding the surface (test observability); its parse output drives the formatted, reveal-on-edit render.</summary>
    internal MarkdigBlockProducer? BlockProducer { get; private set; }

    /// <summary>The markdown pipeline↔surface bridge serving heights, presenters, and reveal (test observability).</summary>
    internal MarkdownViewBridge? ViewBridge { get; private set; }

    /// <summary>The open document's file identity, or <see langword="null"/> for untitled (test observability).</summary>
    internal DocumentFile? FilePart => _file;

    /// <summary>The active autosave stub, or <see langword="null"/> before a document is wired (test observability).</summary>
    internal AutosaveService? AutosavePart => _autosave;

    /// <summary>The active document's journal handle (test observability).</summary>
    internal AutosaveJournal? JournalPart => _journal;

    // ───────────────────────────── file lifecycle ─────────────────────────────

    /// <summary>
    /// Opens the document the CLI named, or an untitled document when it named none — the
    /// Program startup path, called on the UI thread before the first frame. Never prompts
    /// (nothing can be dirty yet). A missing file opens as a new document at that path with a
    /// status note; an unreadable/undecodable file falls back to an untitled document with the
    /// failure on the status line (the session still starts — spec §10.4's launch contract).
    /// </summary>
    /// <param name="timeProvider">
    /// The application clock for the undo idle-seal and the autosave debounce
    /// (<c>UITestHost.Time</c> in host tests); defaults to the system clock.
    /// </param>
    public void OpenStartupDocument(TimeProvider? timeProvider = null)
    {
        VerifyAccess();

        if (StartupOptions.FilePath is { } path)
        {
            if (OpenFileCore(path, timeProvider, out string? error))
                return;

            NewDocument(timeProvider);
            UpdateStatus(error);
            return;
        }

        NewDocument(timeProvider);
    }

    /// <summary>
    /// Opens <paramref name="path"/>, replacing the current document. A dirty document routes
    /// through the save triad first (spec §10.1's prompt-on-replace): <b>Save</b> saves then
    /// opens (an unsavable untitled document aborts with its status message), <b>Don't Save</b>
    /// discards and opens (the discarded document's autosave journal is deleted — the user chose
    /// to drop that content), <b>Cancel</b>/dismissal aborts. A missing file opens as a new
    /// document at that path (see the class remarks). See the class threading remarks for the
    /// calling-context requirement.
    /// </summary>
    /// <param name="path">The file to open (need not exist).</param>
    /// <param name="timeProvider">The application clock (see <see cref="OpenStartupDocument"/>).</param>
    /// <returns><see langword="true"/> when the document was replaced; <see langword="false"/> on cancel or open failure.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public async Task<bool> OpenFileAsync(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        VerifyAccess();

        if (!await ConfirmDiscardOrSaveAsync())
            return false;

        if (OpenFileCore(path, timeProvider, out string? error))
            return true;

        UpdateStatus(error); // current document untouched; the failure rides its status line
        return false;
    }

    /// <summary>
    /// Wires a fresh untitled document (a new autosave identity via
    /// <see cref="DocumentKey.NewUntitled"/>), replacing the current pipeline. Never prompts: M1
    /// has no New command — Program calls this at startup only, and M5's command surface owns
    /// prompting when it exposes New (route it through <see cref="RequestCloseAsync"/> then here).
    /// </summary>
    /// <param name="timeProvider">The application clock (see <see cref="OpenStartupDocument"/>).</param>
    public void NewDocument(TimeProvider? timeProvider = null)
    {
        VerifyAccess();

        TearDownDocumentServices(deleteJournal: true);
        WireDocument(string.Empty, timeProvider);

        _file = null;
        _documentKey = DocumentKey.NewUntitled();
        AttachDocumentServices(timeProvider);
        SetDirty(false);
        UpdateStatus(null);
    }

    /// <summary>
    /// Saves the document to its known path — <see cref="DocumentFile.Save"/> (atomic,
    /// encoding/BOM/endings preserved), then the autosave journal is deleted (spec §12's clean
    /// save) and the dirty state clears. An untitled document has nowhere to save: reports
    /// <see cref="UntitledSaveNote"/> and returns <see langword="false"/> (Save As is M6). An I/O
    /// failure reports on the status line, keeps the document dirty, and returns
    /// <see langword="false"/> — the previous file on disk is intact (atomicity contract).
    /// </summary>
    /// <remarks>Task-shaped for the M6 Save As/dialog path; the M1 body completes synchronously.</remarks>
    /// <returns><see langword="true"/> when the document reached disk.</returns>
    public Task<bool> SaveAsync()
    {
        VerifyAccess();
        return Task.FromResult(SaveNow());
    }

    /// <summary>
    /// The close-path seam (M5's Exit command calls this before shutdown; the M1 gate drives it
    /// directly): prompts the save triad when dirty — Save/Don't Save proceed, Cancel aborts —
    /// and on proceed retires the document's persistence services, deleting the autosave journal
    /// (spec §12's clean close). The visual pipeline is left in place: the application exits
    /// right after a granted close. See the class threading remarks for the calling-context
    /// requirement.
    /// </summary>
    /// <returns><see langword="true"/> when closing may proceed.</returns>
    public async Task<bool> RequestCloseAsync()
    {
        VerifyAccess();

        if (!await ConfirmDiscardOrSaveAsync())
            return false;

        TearDownDocumentServices(deleteJournal: true);
        return true;
    }

    /// <summary>
    /// Builds the document pipeline over <paramref name="content"/> and wires it to the editor
    /// surface: <see cref="DocumentBuffer"/> → <see cref="EditController"/> →
    /// <see cref="MarkdigBlockProducer"/> → <see cref="MarkdownViewBridge"/> (via <see cref="EditorView"/>) →
    /// the <see cref="Presenters.LeafBlockPresenter"/> suite — the formatted, reveal-on-edit render.
    /// Re-wiring (a subsequent call) replaces the whole pipeline; the previous document's
    /// presenters are de-realized by the factory swap and its producer unsubscribed.
    /// Pipeline-only: the file lifecycle methods above call this and additionally manage file
    /// identity, dirty tracking, and autosave — direct callers (pipeline tests) get none of that.
    /// </summary>
    /// <param name="content">The document's initial text (the file lifecycle supplies decoded file contents here).</param>
    /// <param name="timeProvider">
    /// The clock for the controller's idle seal — pass the application's provider
    /// (<c>UITestHost.Time</c> in host tests) so fake clocks drive it; defaults to the system clock.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public void WireDocument(string content, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        BlockProducer?.Dispose(); // the old pipeline stops observing the old controller

        Document = new DocumentBuffer(content);
        Controller = new EditController(Document, timeProvider);
        BlockProducer = new MarkdigBlockProducer(Controller);

        // Factor the whole formatted, reveal-on-edit surface through the instantiable EditorView (§3.2
        // resolution 12): it builds a MarkdownViewBridge over the producer's block list and attaches it
        // — the real source-anchored caret + selection + typing path over the same controller.
        _view.Attach(Controller, BlockProducer);
        ViewBridge = _view.Bridge;
    }

    // ───────────────────────────── startup focus ─────────────────────────────

    /// <summary>
    /// Keeps the <see cref="Editor"/> the startup focus owner so typing works immediately, even though the
    /// ribbon (docked above the editor) is the FIRST tab stop the framework's activation auto-focus would
    /// otherwise land on. The ribbon is reached deliberately (click / access-key); it must not grab focus on
    /// launch. Posted once, after the first arrange (layout settled, the editor focusable), and only when the
    /// editor doesn't already hold focus — so it never overrides a focus the user or app established, and it
    /// wins regardless of the order it races the framework's parked activation focus (either it focuses the
    /// editor first, so the framework's "focus already landed in the root" guard leaves it, or the framework
    /// focused the ribbon first and this corrects it).
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);

        // Consume the one-shot only when there is a live application to post to — so an arrange that somehow ran
        // before an application is current doesn't spend the request and lose the auto-focus.
        if (!_startupFocusRequested && UIApplication.Current is { } application)
        {
            _startupFocusRequested = true;
            application.Dispatcher.Post(() =>
            {
                if (!Editor.IsKeyboardFocusWithin)
                    Editor.Focus();
            });
        }

        return size;
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    /// <summary>
    /// The shell-level chord surface: Ctrl+S saves (the safe-everywhere chord — integration
    /// notes §4: Ctrl+letter normalizes to <c>(Character, letter, Control)</c> on every wire).
    /// Exact-modifier match so Ctrl+Shift+S stays free for M6's Save As.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        if (e.Key == Key.Character
            && e.Modifiers == KeyModifiers.Control
            && e.Text.Length == 1
            && char.ToLowerInvariant(e.Text.Span[0]) == 's')
        {
            e.Handled = true;
            SaveFromKeyboard();
        }
    }

    /// <summary>
    /// <c>async void</c> by design — a key-event tail. <see cref="SaveAsync"/> reports expected
    /// I/O failures on the status line and returns; anything unexpected escaping here surfaces
    /// through the dispatcher's unhandled-exception path (loud), never dies in a discarded task.
    /// </summary>
    private async void SaveFromKeyboard() => await SaveAsync();

    // ───────────────────────────── lifecycle internals ─────────────────────────────

    /// <summary>
    /// Loads (or, for a missing file, creates the identity of) <paramref name="path"/> and swaps
    /// the whole document stack over to it. On failure the current document is untouched and
    /// <paramref name="error"/> carries the status-line text.
    /// </summary>
    private bool OpenFileCore(string path, TimeProvider? timeProvider, out string? error)
    {
        // A directory is not openable and must not become a phantom new document (File.Exists is
        // false for a directory, so without this it would fall into the create-new path and only
        // fail later at save). Spec §10.4 defers directory arguments.
        if (Directory.Exists(path))
        {
            error = $"cannot open a directory: {path}";
            return false;
        }

        DocumentFile file;
        string? note = null;
        try
        {
            if (File.Exists(path))
            {
                file = DocumentFile.Load(path);
            }
            else
            {
                file = DocumentFile.CreateNew(path); // open-as-new-at-that-path (class remarks)
                note = NewFileNote;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"open failed: {ex.Message}";
            return false;
        }

        TearDownDocumentServices(deleteJournal: true);
        WireDocument(file.Text, timeProvider);

        _file = file;
        _documentKey = DocumentKey.ForPath(file.Path);
        AttachDocumentServices(timeProvider);
        SetDirty(false);
        UpdateStatus(note);

        error = null;
        return true;
    }

    private bool SaveNow()
    {
        if (Document is null)
            return false; // no document wired — nothing to save

        if (_file is not { } file)
        {
            UpdateStatus(UntitledSaveNote);
            return false;
        }

        try
        {
            file.Save(Document, EnsureTrailingNewlineOnSave);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateStatus($"save failed: {ex.Message}");
            return false;
        }

        _autosave?.Delete(); // clean save retires the journal (spec §12)
        SetDirty(false);
        UpdateStatus(null);
        return true;
    }

    /// <summary>
    /// The shared dirty gate for replace/close: <see langword="true"/> when proceeding is safe —
    /// clean already, saved on request, or explicitly discarded. Cancel and dismissal refuse.
    /// </summary>
    private async Task<bool> ConfirmDiscardOrSaveAsync()
    {
        if (!_isDirty)
            return true;

        ITaskDialogService service = DialogService ??= new MessageBoxTaskDialogService(
            UIApplication.Current ?? throw new InvalidOperationException(
                "No DialogService was injected and no UIApplication is current — cannot show the save prompt."));

        TaskDialogResult result = await service.ShowAsync(new TaskDialogRequest($"Save changes to {DisplayName}?")
        {
            Title = "Unsaved Changes",
            Content = "Your changes will be lost if you don't save them.",
            Severity = TaskDialogSeverity.Warning,
            Buttons = TaskDialogButton.SaveTriad,
        });

        if (result.Button == TaskDialogButton.Save)
            return await SaveAsync(); // an unsavable document (untitled, I/O failure) aborts the replace/close

        return result.Button == TaskDialogButton.DontSave; // Cancel or dismissal → abort
    }

    /// <summary>
    /// Wires the per-document persistence stack: dirty tracking on the current controller, the
    /// journal keyed to <see cref="_documentKey"/> under <see cref="AppStatePaths"/>, the
    /// autosave stub on the same clock, and (once per shell) the terminal focus-loss trigger.
    /// </summary>
    private void AttachDocumentServices(TimeProvider? timeProvider)
    {
        _journal = new AutosaveJournal(_appStatePaths, _documentKey!);
        _autosave = new AutosaveService(Controller!, _journal, timeProvider);

        _dirtySource = Controller;
        Controller!.Changed += OnDocumentChanged;

        WireFocusLossTrigger();
    }

    /// <summary>
    /// Retires the current document's persistence services: unsubscribes dirty tracking and
    /// disposes the autosave stub, deleting its journal first when the document's story is over
    /// (saved, closed, or explicitly discarded — spec §12's clean save/close).
    /// </summary>
    private void TearDownDocumentServices(bool deleteJournal)
    {
        if (_dirtySource is { } source)
            source.Changed -= OnDocumentChanged;
        _dirtySource = null;

        if (_autosave is { } autosave)
        {
            if (deleteJournal)
                autosave.Delete(); // queued through the drainer; completes after any in-flight write

            autosave.Dispose();
        }

        _autosave = null;
        _journal = null;
    }

    /// <summary>
    /// Hooks the autosave focus-loss accelerator (plan §6 WP12's trigger seam) onto the public
    /// <c>InputDispatcher.TerminalFocusChanged</c> event — once per shell; the handler follows
    /// whatever autosave service is current. Skipped when no application is current (plain unit
    /// tests without a host); the debounce path never depends on focus events (spec §12).
    /// </summary>
    private void WireFocusLossTrigger()
    {
        if (_focusTriggerWired || UIApplication.Current is not { } application)
            return;

        application.InputDispatcher.TerminalFocusChanged += OnTerminalFocusChanged;
        _focusTriggerWired = true;
    }

    private void OnTerminalFocusChanged(bool focused)
    {
        if (!focused)
            _autosave?.TriggerNow(); // raised on the UI thread during input dispatch — TriggerNow's contract
    }

    private void OnDocumentChanged(SpliceResult result) => SetDirty(true);

    private void SetDirty(bool dirty)
    {
        _isDirty = dirty;
        DirtyDotPart.Text = dirty ? "●" : string.Empty;
    }

    /// <summary>The status-line document name: the file name of <see cref="FilePath"/>, or <see cref="UntitledName"/>.</summary>
    internal string DisplayName => _file is { } file ? Path.GetFileName(file.Path) : UntitledName;

    /// <summary>Rewrites the status text: <c>name — app</c>, with an optional trailing note (open-as-new, save failures).</summary>
    private void UpdateStatus(string? note) =>
        StatusTextPart.Text = note is null
            ? $"{DisplayName} — {AppName}"
            : $"{DisplayName} — {AppName} — {note}";

    /// <summary>The one-row status line container docked bottom (M5 owns the full chrome).</summary>
    internal DockPanel StatusLinePart { get; }

    /// <summary>The status text: document name + app name (+ transient note).</summary>
    internal TextBlock StatusTextPart { get; }

    /// <summary>The reserved dirty-dot slot; <c>"●"</c> while the document has unsaved changes.</summary>
    internal TextBlock DirtyDotPart { get; }
}
