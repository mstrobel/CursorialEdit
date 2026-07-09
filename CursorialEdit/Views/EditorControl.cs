using Cursorial.Input;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Views;

/// <summary>
/// The editor's document surface control (architecture Decision 6): a <see cref="Control"/> that
/// templates its <b>own</b> <see cref="ScrollViewer"/> whose scroll-content-presenter content is a
/// <see cref="DocumentPanel"/>. The control is the sole focusable element of the surface; block
/// presenters are inert visuals — all keyboard, text, and mouse input routes here.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HandlesScrolling"/> is <see langword="true"/> so the templated
/// <see cref="ScrollViewer"/> leaves keyboard scroll navigation to this control — the framework
/// gate checks <c>TemplatedParent</c> (<c>ScrollViewer.cs:478</c>, <c>Control.cs:118-121</c>),
/// which is exactly why the ScrollViewer must live in this control's template rather than wrap it
/// loosely.
/// </para>
/// <para>
/// <b>Two input modes.</b> Unattached (the WP3 spike/test path: <see cref="HeightSource"/>/<see
/// cref="BlockFactory"/> set directly), the control keeps the spike keyboard scrolling and the
/// <see cref="SetStubCaret"/> stub. Once <see cref="AttachDocument"/> installs the M1.WP8 caret,
/// keys move the <b>caret</b> (scrolling follows via <see cref="ScrollViewer.EnsureVisible"/> —
/// the SCP coerces), printable input funnels into <see cref="EditController.Apply"/>, and the
/// mouse positions/extends the document selection.
/// </para>
/// <para>
/// <b>Keymap (WP8 scope; FB-14 notes).</b> Arrows/Home/End/PageUp/PageDown (+Shift extends,
/// +Ctrl word/document variants), Ctrl+A, Enter, Backspace, Delete, Tab (spec §6.3 indent —
/// two spaces), Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo. On the legacy wire Ctrl+Shift+letter
/// arrives with Shift dropped (FB-14: Ctrl+Shift+Z fires as Ctrl+Z = undo), so Ctrl+Y is the
/// legacy-safe redo chord; the Ctrl+Shift+Z arm serves Kitty-capable wires. IME composition is
/// out of M1.
/// </para>
/// <para>
/// <b>Clipboard (M1.WP9; FB-3).</b> Ctrl+C / Ctrl+X copy/cut the selection's exact source range
/// (interior terminators byte-exact) to <b>both</b> sinks: the terminal clipboard via
/// <see cref="IClipboardService"/> (an OSC 52 write — fire-and-forget, gated internally on the
/// negotiated <c>ClipboardWrite</c>) and the app-internal <see cref="Clipboard"/> store. Ctrl+V
/// prefers the <b>system</b> clipboard when the terminal negotiated OSC 52 <b>read</b>
/// (<see cref="IClipboardService.CanRead"/>) — an async query/response round-trip that finally lets
/// Ctrl+V see content copied <b>outside</b> the app — and falls back to the internal store when read is
/// unsupported/denied/times out (the FB-3 read-side closure; see <see cref="Paste"/>). External content
/// also still arrives as the terminal's own paste keybinding → bracketed paste →
/// <see cref="TextInputEventArgs.FromPaste"/>, which <see cref="OnTextInput"/> applies as one literal
/// splice. TextBox-parity aliases:
/// Ctrl+Insert copy, Shift+Insert paste, Shift+Delete cut. Only safe-everywhere chords are bound
/// (Ctrl+letter normalizes to <c>(Character, letter, Control)</c> on every wire — integration
/// notes §4); plain-paste/alternate chords are M4/M5 work.
/// </para>
/// <para>
/// <b>Ctrl+C is not SIGINT here (verified).</b> The framework session applies
/// <c>stty … -isig …</c> (<c>PosixStdioTransports.Open</c>), so the TTY's ISIG processing is off
/// for the session's lifetime: pressing Ctrl+C delivers the raw byte 0x03, which the VT
/// interpreter's C0 map (0x01–0x1A → Ctrl+letter) decodes as <c>(Character, "c", Control)</c> —
/// exactly the copy chord. The app's <c>SignalRestore</c> SIGINT path fires only for an outside
/// <c>kill -INT</c>, never from this keystroke. Confirmed empirically by the wire-truth test
/// (<c>Tests/Clipboard/SelectionTests</c>: raw 0x03 through the real <c>VtInputDevice</c> lands
/// in the copy handler).
/// </para>
/// </remarks>
[TemplatePart(PartScrollViewer, typeof(ScrollViewer), IsRequired = true)]
[TemplatePart(PartDocumentPanel, typeof(DocumentPanel), IsRequired = true)]
public class EditorControl : Control, IContentRowMap
{
    private const string PartScrollViewer = "PART_ScrollViewer";
    private const string PartDocumentPanel = "PART_DocumentPanel";

    // The control's own template (code-first; sealed on first instantiation and shared): a
    // ScrollViewer around the DocumentPanel as the SCP's DIRECT content (host discovery requires it).
    private static readonly ControlTemplate EditorTemplate = new(static ctx =>
    {
        var panel = new DocumentPanel();
        ctx.RegisterName(PartDocumentPanel, panel);

        var scrollViewer = new ScrollViewer
        {
            Content = panel,
            Focusable = false,
            IsTabStop = false,
        };
        ctx.RegisterName(PartScrollViewer, scrollViewer);

        return scrollViewer;
    })
    {
        TargetType = typeof(EditorControl),
    };

    private ScrollViewer? _scrollViewer;
    private DocumentPanel? _panel;
    private IBlockHeightSource? _heightSource;
    private Func<int, UIElement>? _blockFactory;
    private InternalClipboard _clipboard = InternalClipboard.Shared;

    // At most one OSC 52 read is in flight (Ctrl+V system-clipboard read): OSC 52 carries no request id, so a
    // device response completes EVERY pending read with the same text — a held/double-tapped Ctrl+V during the
    // terminal's per-read permission prompt would otherwise fan one clipboard value into duplicate pastes.
    private bool _pasteReadInFlight;

    // The OSC 52 read round-trip budget — generous because terminals often interpose a per-read permission
    // prompt (Kitty asks each time); a denied/unsupported/slow read completes null and we fall back to the store.
    private static readonly TimeSpan PasteReadTimeout = TimeSpan.FromSeconds(2);

    private bool _hasFocus;
    private int _caretColumn;
    private int _caretDocumentRow;

    // ── WP8 document wiring (null until AttachDocument) ──
    private DocumentCaret? _caret;
    private EditController? _controller;
    private IEditorViewSource? _bridge;
    private bool _dragging;
    private bool _publishing; // re-entrancy guard: publishing may refine heights, which re-publishes

    /// <summary>Creates the control: focusable, with the self-templated ScrollViewer surface.</summary>
    public EditorControl()
    {
        Focusable = true;
        Template = EditorTemplate;
    }

    /// <summary>
    /// The editor owns keyboard scroll navigation — its templated <see cref="ScrollViewer"/> must
    /// not consume the arrow/page/home/end keys (the WPF-parity gate keyed on <c>TemplatedParent</c>).
    /// </summary>
    /// <remarks>
    /// Declared <c>protected</c>, not <c>protected internal</c>: C# narrows a cross-assembly
    /// <c>protected internal</c> override to its <c>protected</c> half (CS0507) — the framework's
    /// gate reads the virtual slot either way.
    /// </remarks>
    protected override bool HandlesScrolling => true;

    /// <summary>The block-height provider handed to the templated <see cref="DocumentPanel"/>.</summary>
    public IBlockHeightSource? HeightSource
    {
        get => _heightSource;
        set
        {
            VerifyAccess(); // fail fast pre-template too — the panel setter only guards post-expansion
            _heightSource = value;
            if (_panel is { } panel)
                panel.HeightSource = value;
        }
    }

    /// <summary>The block-element factory handed to the templated <see cref="DocumentPanel"/>.</summary>
    public Func<int, UIElement>? BlockFactory
    {
        get => _blockFactory;
        set
        {
            VerifyAccess();
            _blockFactory = value;
            if (_panel is { } panel)
                panel.BlockFactory = value;
        }
    }

    /// <summary>
    /// The app-internal clipboard store this surface's copy/cut write and Ctrl+V reads — the
    /// FB-3 read side (see <see cref="InternalClipboard"/> for the write-only-terminal split).
    /// Defaults to <see cref="InternalClipboard.Shared"/> so every surface in the process shares
    /// one clipboard; injectable so tests isolate with a fresh instance.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public InternalClipboard Clipboard
    {
        get => _clipboard;
        set
        {
            VerifyAccess();
            _clipboard = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>The templated ScrollViewer part (null before the first template expansion).</summary>
    internal ScrollViewer? ScrollViewerPart => _scrollViewer;

    /// <summary>The templated DocumentPanel part (null before the first template expansion).</summary>
    internal DocumentPanel? DocumentPanelPart => _panel;

    /// <summary>The installed document caret (test observability); null until <see cref="AttachDocument"/>.</summary>
    internal DocumentCaret? DocumentCaretPart => _caret;

    // ───────────────────────────── the WP8 attachment seam ─────────────────────────────

    /// <summary>
    /// Attaches a wired document pipeline to this surface — <b>the</b> seam the application shell
    /// calls (after M1.WP8 lands, <c>EditorShell.WireDocument</c> hands its freshly built
    /// controller + bridge here instead of setting <see cref="HeightSource"/>/<see cref="BlockFactory"/>
    /// itself; the orchestrator wires that call in after this wave). It installs the bridge as the
    /// panel's height/presenter source and replaces the WP3 stub caret with the real
    /// source-anchored caret + selection + typing input path.
    /// </summary>
    /// <remarks>
    /// Re-attaching (the open-file path) replaces everything: swapping the factory de-realizes
    /// every presenter of the previous document, the previous caret/selection is discarded, and
    /// the caret starts at the new document's origin. The raw <see cref="HeightSource"/>/<see
    /// cref="BlockFactory"/> properties keep working for the stub/spike path — a control that was
    /// never attached behaves exactly as before WP8.
    /// </remarks>
    /// <param name="controller">The document's single mutation funnel (undo, coalescing, caret-echo contract).</param>
    /// <param name="bridge">The pipeline↔surface bridge serving heights, run maps, and presenters over the same document — the plain-text <see cref="BlockViewBridge"/> or the markdown <see cref="MarkdownViewBridge"/>.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public void AttachDocument(EditController controller, IEditorViewSource bridge)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(bridge);
        VerifyAccess();

        DetachDocument();

        _controller = controller;
        _bridge = bridge;

        // Factory first: swapping it de-realizes every presenter of the previous document, so the
        // height-source swap that follows reconciles from a clean slate (the WireDocument order).
        BlockFactory = bridge.CreatePresenter;
        HeightSource = bridge;

        _caret = new DocumentCaret(controller, bridge, this);
        bridge.SelectionSource = _caret;
        _caret.Updated += OnCaretUpdated;
        bridge.HeightsChanged += OnDocumentHeightsChanged;

        if (_hasFocus)
            PublishCaret();
    }

    private void DetachDocument()
    {
        if (_caret is not { } caret)
            return;

        caret.Updated -= OnCaretUpdated;
        if (_bridge is { } bridge)
        {
            bridge.HeightsChanged -= OnDocumentHeightsChanged;
            bridge.SelectionSource = null;
        }

        _caret = null;
        _controller = null;
        _bridge = null;
        _dragging = false;
    }

    /// <summary>
    /// Places the M1.WP3 <b>stub</b> caret at a document position (content coordinates: column,
    /// document row) — the pre-<see cref="AttachDocument"/> spike/test path only; once a document
    /// is attached the real caret owns the publication. While the control is focused the caret is
    /// published through <see cref="ITerminalCaretService"/> on the document panel, so the SCP's
    /// composite slide and zone clip position and hide the real terminal cursor with zero
    /// re-raster; it publishes as hidden when the position scrolls out of the viewport.
    /// </summary>
    public void SetStubCaret(int column, int documentRow)
    {
        _caretColumn = Math.Max(0, column);
        _caretDocumentRow = Math.Max(0, documentRow);

        if (_hasFocus && _caret is null)
            PublishCaret();
    }

    // ───────────────────────────── template wiring ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _scrollViewer = GetTemplatePart<ScrollViewer>(PartScrollViewer);
        _panel = GetTemplatePart<DocumentPanel>(PartDocumentPanel);

        if (_panel is { } panel)
        {
            panel.HeightSource = _heightSource;
            panel.BlockFactory = _blockFactory;
        }

        if (_hasFocus)
            PublishCaret();
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        ClearCaret();
        _scrollViewer = null;
        _panel = null;
        base.OnTemplateDetaching(old);
    }

    // ───────────────────────────── IContentRowMap (the caret's vertical geometry) ─────────────────────────────

    int IContentRowMap.ContentRows => _panel?.ContentRowCount ?? 0;

    int IContentRowMap.BlockTopRow(int blockIndex) => _panel?.GetBlockTopRow(blockIndex) ?? 0;

    int IContentRowMap.BlockIndexOfRow(int contentRow) => _panel?.BlockIndexOfContentRow(contentRow) ?? 0;

    // ───────────────────────────── keyboard ─────────────────────────────

    /// <summary>
    /// Attached: routes keys to the document caret (motion, selection, editing, undo — see the
    /// class remarks for the keymap). Unattached: spike-level keyboard scrolling (WP3) — cell
    /// steps for arrows, viewport steps for paging, document ends on Ctrl+Home/Ctrl+End; offsets
    /// coerce at the SCP, so paging at EOF pins at the maximum offset instead of overshooting.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        // View-mode toggle (M2.WP10): Ctrl+/ on the Kitty wire, Alt+/ the legacy-safe alternate — the
        // legacy wire can't distinguish Ctrl+/ (it decodes to the ignored 0x1F byte, FB-14.1). Both chords
        // are bound unconditionally, mirroring the dual Ctrl+Shift+Z / Ctrl+Y redo arm: whichever the wire
        // can deliver fires. Handled before the caret branch because Alt chords otherwise bubble there.
        // Only the markdown surface has marks to hide, so the toggle is meaningful only there. On the
        // plain-text surface raw == formatted, so we neither run the (wasteful) presenter re-realization
        // nor swallow the key — it passes through untouched.
        if (_bridge is MarkdownViewBridge && IsViewModeToggle(e))
        {
            ToggleViewMode();
            e.Handled = true;
            return;
        }

        if (_caret is { } caret)
        {
            OnDocumentKeyDown(e, caret);
            return;
        }

        if (_scrollViewer is not { } scrollViewer)
            return;

        switch (e.Key)
        {
            case Key.UpArrow when e.Modifiers == KeyModifiers.None:
                scrollViewer.ScrollBy(0, -1);
                e.Handled = true;
                break;

            case Key.DownArrow when e.Modifiers == KeyModifiers.None:
                scrollViewer.ScrollBy(0, +1);
                e.Handled = true;
                break;

            case Key.PageUp when e.Modifiers == KeyModifiers.None:
                scrollViewer.ScrollBy(0, -Math.Max(1, scrollViewer.Viewport.Rows));
                e.Handled = true;
                break;

            case Key.PageDown when e.Modifiers == KeyModifiers.None:
                scrollViewer.ScrollBy(0, +Math.Max(1, scrollViewer.Viewport.Rows));
                e.Handled = true;
                break;

            // Exact-modifier matches (consistent with the None cases above): a loose Control mask
            // would swallow Ctrl+Shift+Home — the select-to-start chord WP8 binds (review wave1-4).
            case Key.Home when e.Modifiers == KeyModifiers.Control:
                scrollViewer.VerticalOffset = 0;
                e.Handled = true;
                break;

            case Key.End when e.Modifiers == KeyModifiers.Control:
                scrollViewer.VerticalOffset = Math.Max(0, scrollViewer.Extent.Rows - scrollViewer.Viewport.Rows);
                e.Handled = true;
                break;
        }
    }

    private void OnDocumentKeyDown(KeyEventArgs e, DocumentCaret caret)
    {
        bool ctrl = (e.Modifiers & KeyModifiers.Control) != 0;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;

        // Alt+Enter inserts a literal in-cell line break (<br>) inside a table cell — the Excel/Sheets
        // convention (plain Enter commits the cell downward, so the break needs its own chord). Handled
        // before the Alt-chord bubble below; outside a table it is not ours, so it bubbles like other Alt
        // chords (menu access, future row ops).
        if (e.Key == Key.Enter && e.Modifiers == KeyModifiers.Alt)
        {
            if (caret.IsInTable)
            {
                caret.TableInsertCellBreak();
                e.Handled = true;
            }

            return;
        }

        // Alt+↑ / Alt+↓ insert a table row above / below the caret's row (spec §5.3). Table-only: elsewhere the
        // chord bubbles (Alt motion is unbound), like Alt+Enter above. Handled before the Alt-chord bubble below.
        if (e.Key is Key.UpArrow or Key.DownArrow && e.Modifiers == KeyModifiers.Alt)
        {
            if (caret.IsInTable)
            {
                if (e.Key == Key.UpArrow)
                    caret.TableInsertRowAbove();
                else
                    caret.TableInsertRowBelow();
                e.Handled = true;
            }

            return;
        }

        // Alt/Super chords are not ours (M3 row ops, menu access keys) — let them bubble.
        if ((e.Modifiers & ~(KeyModifiers.Control | KeyModifiers.Shift)) != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.LeftArrow:
                if (ctrl)
                    caret.MoveWordLeft(shift);
                else
                    caret.MoveLeft(shift);
                break;

            case Key.RightArrow:
                if (ctrl)
                    caret.MoveWordRight(shift);
                else
                    caret.MoveRight(shift);
                break;

            case Key.UpArrow when !ctrl:
                caret.MoveVertical(-1, shift);
                break;

            case Key.DownArrow when !ctrl:
                caret.MoveVertical(+1, shift);
                break;

            case Key.PageUp when !ctrl:
                caret.MoveVertical(-ViewportRows(), shift);
                break;

            case Key.PageDown when !ctrl:
                caret.MoveVertical(+ViewportRows(), shift);
                break;

            case Key.Home:
                if (ctrl)
                    caret.MoveDocumentStart(shift);
                else
                    caret.MoveHome(shift);
                break;

            case Key.End:
                if (ctrl)
                    caret.MoveDocumentEnd(shift);
                else
                    caret.MoveEnd(shift);
                break;

            case Key.Enter when !ctrl && !shift:
                caret.InsertNewline();
                break;

            case Key.Backspace when !ctrl:
                caret.Backspace();
                break;

            case Key.Delete when shift && !ctrl: // Shift+Delete — cut (TextBox parity alias)
                if (!Cut())
                    return; // nothing to cut — bubbles, consistent with Ctrl+X
                break;

            case Key.Delete when !ctrl && !shift:
                caret.DeleteForward();
                break;

            case Key.Insert when ctrl && !shift: // Ctrl+Insert — copy (TextBox parity alias)
                if (!Copy())
                    return; // nothing to copy — bubbles
                break;

            case Key.Insert when shift && !ctrl: // Shift+Insert — paste (TextBox parity alias)
                if (!Paste())
                    return; // empty store — bubbles
                break;

            case Key.Tab when !ctrl && !shift:
                // In a table Tab navigates cells (last-cell Tab appends a row); elsewhere it indents (§6.3).
                if (caret.IsInTable)
                    caret.TableTab(shift: false);
                else
                    caret.InsertIndent();
                break;

            case Key.Tab when !ctrl && shift: // Shift+Tab: previous cell in a table; otherwise free for M4 outdent
                if (!caret.IsInTable)
                    return; // bubble — the plain surface has no Shift+Tab binding yet
                caret.TableTab(shift: true);
                break;

            case Key.Character when ctrl && !shift && IsLetter(e, 'a'):
                caret.SelectAll();
                break;

            // WP9 clipboard chords — safe on every wire (integration notes §4). On POSIX the
            // session runs with ISIG off (stty -isig), so Ctrl+C reaches us as the 0x03 byte
            // decoded to this chord, never as SIGINT — see the class remarks for the verified
            // wire truth.
            case Key.Character when ctrl && !shift && IsLetter(e, 'c'):
                if (!Copy())
                    return; // Ctrl+C with no selection is not consumed — bubbles (TextBox parity)
                break;

            case Key.Character when ctrl && !shift && IsLetter(e, 'x'):
                if (!Cut())
                    return;
                break;

            case Key.Character when ctrl && !shift && IsLetter(e, 'v'):
                if (!Paste())
                    return; // no document, or (no-read terminal) the store is empty — bubbles; an OSC 52 read consumes the chord
                break;

            // The Ctrl+Shift+Z redo arm precedes the Ctrl+Z undo arm so a shifted Z matches redo
            // (Kitty wires; on the legacy wire the Shift is dropped — FB-14 — and Ctrl+Y is the
            // safe redo chord, mirroring the framework TextBox's arm ordering).
            case Key.Character when ctrl && shift && IsLetter(e, 'z'):
                if (!caret.Redo())
                    return; // nothing to redo — bubble
                break;

            case Key.Character when ctrl && !shift && IsLetter(e, 'z'):
                if (!caret.Undo())
                    return; // nothing to undo — bubble
                break;

            case Key.Character when ctrl && !shift && IsLetter(e, 'y'):
                if (!caret.Redo())
                    return;
                break;

            default:
                return; // not ours — bubbles (M5 command surface)
        }

        e.Handled = true;
    }

    private int ViewportRows() => Math.Max(1, _scrollViewer?.Viewport.Rows ?? 1);

    private static bool IsLetter(KeyEventArgs e, char lower)
        => e.Text.Length == 1 && char.ToLowerInvariant(e.Text.Span[0]) == lower;

    // ───────────────────────────── view mode (M2.WP10) ─────────────────────────────

    /// <summary>The surface's current render mode; <see cref="ViewMode.Formatted"/> when no document is attached.</summary>
    public ViewMode ViewMode => _bridge?.ViewMode ?? ViewMode.Formatted;

    /// <summary>
    /// Raised after <see cref="ViewMode"/> actually changes (the Ctrl+/ keybind, <see cref="ToggleViewMode"/>,
    /// or the ribbon's Raw command) so a bound ribbon toggle reflects the live mode even when the keyboard —
    /// not the ribbon — drove the switch (M5).
    /// </summary>
    public event Action? ViewModeChanged;

    /// <summary>
    /// Toggles the surface between <see cref="ViewMode.Formatted"/> (WYSIWYG) and <see cref="ViewMode.Raw"/>
    /// (verbatim source) — the M2.WP10 raw-mode toggle (Ctrl+/ / Alt+/). The whole surface switches mode;
    /// the caret's source position is preserved (it is a mode-independent source anchor). A no-op when no
    /// document is attached.
    /// </summary>
    public void ToggleViewMode()
    {
        if (_bridge is { } bridge)
            SetViewMode(bridge.ViewMode == ViewMode.Formatted ? ViewMode.Raw : ViewMode.Formatted);
    }

    /// <summary>Sets the render mode: switches the bridge, re-realizes every block's presenter, and re-anchors the caret.</summary>
    private void SetViewMode(ViewMode mode)
    {
        if (_bridge is not { } bridge || bridge.ViewMode == mode)
            return;

        bridge.ViewMode = mode;         // drops cached heights + active block, raises HeightsChanged (extent re-derives)
        _panel?.RefreshRealizations();  // swap every block's presenter type (formatted suite ⇄ raw) on the next measure

        // Re-anchor AND scroll-follow the caret: the mode switch changes every block's height (raw = line
        // count, formatted = wrapped/revealed rows), so the caret's document row shifts under a preserved
        // scroll offset — without EnsureVisible the caret/edited line can be left off-screen. OnCaretUpdated
        // publishes the terminal caret (when focused) and calls ScrollViewer.EnsureVisible on its new row.
        OnCaretUpdated();

        ViewModeChanged?.Invoke(); // let a bound ribbon toggle re-sync to the new mode (keyboard route included)
    }

    /// <summary>Whether <paramref name="e"/> is the view-mode toggle chord (Ctrl+/ or Alt+/, no other modifier).</summary>
    private static bool IsViewModeToggle(KeyEventArgs e)
    {
        if (e.Key != Key.Character || e.Text.Length != 1 || e.Text.Span[0] != '/')
            return false;

        return e.Modifiers is KeyModifiers.Control or KeyModifiers.Alt;
    }

    // ───────────────────────────── clipboard (M1.WP9) ─────────────────────────────

    /// <summary>
    /// Copy: serializes the selection's exact source range and writes both clipboard sinks — the
    /// single path the Ctrl+C / Ctrl+Insert keybind AND the ribbon's Copy command share (M5). False
    /// (nothing copied, the key bubbles) when no document is attached or the selection is empty — M1
    /// has no line-copy convention (later milestones'), matching the framework <c>TextBox</c>'s
    /// unconsumed no-selection Ctrl+C.
    /// </summary>
    public bool Copy()
    {
        if (_caret is not { } caret || caret.SelectedText() is not { } text)
            return false;

        WriteClipboard(text);
        return true;
    }

    /// <summary>
    /// Cut: copy (both sinks) + delete of the selection as its own undo unit — undo restores
    /// the text <i>and</i> the selection (the recorded before-state carries the anchor). The single
    /// path the Ctrl+X / Shift+Delete keybind AND the ribbon's Cut command share (M5). False (the key
    /// bubbles) when no document is attached or the selection is empty.
    /// </summary>
    public bool Cut()
    {
        if (_caret is not { } caret || caret.SelectedText() is not { } text)
            return false;

        WriteClipboard(text);
        caret.DeleteSelection();
        return true;
    }

    /// <summary>
    /// Ctrl+V / Shift+Insert / the ribbon's Paste command (the single path all three share, M5). When the
    /// terminal negotiated OSC 52 <b>read</b> (<see cref="IClipboardService.CanRead"/>), this pulls the
    /// <b>system</b> clipboard — external copies included — via <see cref="IClipboardService.TryGetTextAsync"/>,
    /// a query/response round-trip the framework pumps on this thread. Because that is async (a blocking wait
    /// would deadlock the pump), the chord is consumed immediately and the paste lands on completion; the
    /// app-internal <see cref="Clipboard"/> store is the fallback when the read is unsupported, denied, times
    /// out, or returns empty (system-preferred, per the FB-3 read-side closure). Without read capability the
    /// internal store is the only source and external content still arrives via the terminal's own bracketed
    /// paste. Returns <see langword="false"/> (the key bubbles) only when no document is attached, or — in the
    /// no-read case — the store is empty.
    /// </summary>
    public bool Paste()
    {
        if (_caret is not { } caret)
            return false; // no document — bubble

        if (UIApplication.Current is { } application && application.Clipboard.CanRead)
        {
            // A device response completes EVERY pending read with the same text (OSC 52 carries no request
            // id), so coalesce a repeat chord onto the in-flight read rather than fanning it into duplicate
            // pastes during the terminal's permission-prompt window (TextBox's _pasteReadInFlight guard).
            if (!_pasteReadInFlight)
            {
                _pasteReadInFlight = true;
                _ = CompletePasteAsync(application.Clipboard.TryGetTextAsync(PasteReadTimeout));
            }

            return true; // chord consumed; the paste (system text, or the store fallback) lands on completion
        }

        return PasteFromStore(caret); // no OSC 52 read — the internal store is the only source (FB-3 write pairing)
    }

    /// <summary>Pastes the app-internal store's content as one literal splice; false (the key bubbles) when it is empty.</summary>
    private bool PasteFromStore(DocumentCaret caret)
    {
        if (_clipboard.Text is not { Length: > 0 } text)
            return false;

        caret.Paste(text);
        return true;
    }

    /// <summary>
    /// The OSC 52 read completion (fire-and-forget from <see cref="Paste"/>): the reply — or the timeout null —
    /// resumes on the UI thread via the dispatcher sync context. System clipboard preferred, the in-app store
    /// as fallback when the read yields nothing (denied, unsupported-but-<c>CanRead</c> race, timeout, or an
    /// empty selection). The caret is re-resolved at completion — a document reload during the read window
    /// swaps it — so a stale caret is never pasted into.
    /// </summary>
    private async Task CompletePasteAsync(ValueTask<string?> read)
    {
        try
        {
            string? text = await read;
            if (string.IsNullOrEmpty(text))
                text = _clipboard.Text; // system clipboard yielded nothing — fall back to the in-app store

            if (!string.IsNullOrEmpty(text) && _caret is { } caret)
                caret.Paste(text);
        }
        catch (Exception)
        {
            // Fire-and-forget: degrade a failed paste to a no-op rather than an unobserved-task exception.
            // TryGetTextAsync completes with null (never faults) and caret.Paste is throw-free for literal
            // text, so this is defensive; the framework's exception funnel (RaiseUnhandled) is internal and
            // not reachable from the app assembly.
        }
        finally
        {
            _pasteReadInFlight = false;
        }
    }

    /// <summary>
    /// The dual write (FB-3): the internal store is the read side for in-app Ctrl+V; the
    /// framework <see cref="IClipboardService"/> emits the OSC 52 set toward the user's system
    /// clipboard (fire-and-forget, queued on the out-of-band OSC channel and emitted in the
    /// frame's Phase 6; a no-op inside the service when the wire never negotiated
    /// <c>ClipboardWrite</c>).
    /// </summary>
    private void WriteClipboard(string text)
    {
        _clipboard.SetText(text);

        if (UIApplication.Current is { } application)
            application.Clipboard.SetText(text);
    }

    // ───────────────────────────── command surface (M5 ribbon) ─────────────────────────────
    //
    // Thin public forwarders the Bars ribbon binds its BarCommands to. Every one routes to the CURRENT
    // document caret (recreated per document by AttachDocument), so a command captured against the
    // persistent EditorControl keeps working across a document reload — the caret is resolved at call
    // time, never captured. The keybinds above hit the very same caret methods, so ribbon and keyboard
    // share one implementation (no duplicated logic).

    /// <summary>Undo the last edit group; the Ctrl+Z keybind and the ribbon's Undo command share this path. False when there is nothing to undo.</summary>
    public bool Undo() => _caret?.Undo() ?? false;

    /// <summary>Redo the most recently undone group; the Ctrl+Y keybind and the ribbon's Redo command share this path. False when there is nothing to redo.</summary>
    public bool Redo() => _caret?.Redo() ?? false;

    /// <summary>Select the whole document (Ctrl+A / the ribbon's Select All). A no-op when no document is attached.</summary>
    public void SelectAll() => _caret?.SelectAll();

    /// <summary>Wrap the selection's source range in <c>**…**</c> (the MiniToolbar's Bold command); with no selection inserts an empty pair with the caret between the marks. A no-op when no document is attached, inside a table cell, or across blocks. See <see cref="DocumentCaret.Bold"/>.</summary>
    public void Bold() => _caret?.Bold();

    /// <summary>Wrap the selection's source range in <c>*…*</c> (the MiniToolbar's Italic command). A no-op when no document is attached, inside a table cell, or across blocks. See <see cref="DocumentCaret.Italic"/>.</summary>
    public void Italic() => _caret?.Italic();

    /// <summary>Wrap the selection's source range in <c>`…`</c> (the MiniToolbar's Inline Code command). A no-op when no document is attached, inside a table cell, or across blocks. See <see cref="DocumentCaret.InlineCode"/>.</summary>
    public void InlineCode() => _caret?.InlineCode();

    /// <summary>Inserts an empty table row above the caret's row (Alt+↑ / ribbon). A no-op outside a table. See <see cref="DocumentCaret.TableInsertRowAbove"/>.</summary>
    public void TableInsertRowAbove() => _caret?.TableInsertRowAbove();

    /// <summary>Inserts an empty table row below the caret's row (Alt+↓ / ribbon). A no-op outside a table.</summary>
    public void TableInsertRowBelow() => _caret?.TableInsertRowBelow();

    /// <summary>Inserts an empty column to the left of the caret's column (ribbon). A no-op outside a table.</summary>
    public void TableInsertColumnLeft() => _caret?.TableInsertColumnLeft();

    /// <summary>Inserts an empty column to the right of the caret's column (ribbon). A no-op outside a table.</summary>
    public void TableInsertColumnRight() => _caret?.TableInsertColumnRight();

    /// <summary>Deletes the caret's table row — promoting the next row when it is the header (ribbon). A no-op outside a table.</summary>
    public void TableDeleteRow() => _caret?.TableDeleteRow();

    /// <summary>Deletes the caret's table column — deleting the whole table when it is the last column (ribbon). A no-op outside a table.</summary>
    public void TableDeleteColumn() => _caret?.TableDeleteColumn();

    /// <summary>Deletes the whole table the caret is in (ribbon). A no-op outside a table.</summary>
    public void TableDelete() => _caret?.TableDelete();

    /// <summary>Moves the caret's table row up (ribbon). A no-op outside a table or when the row cannot move.</summary>
    public void TableMoveRowUp() => _caret?.TableMoveRowUp();

    /// <summary>Moves the caret's table row down (ribbon). A no-op outside a table or when the row cannot move.</summary>
    public void TableMoveRowDown() => _caret?.TableMoveRowDown();

    /// <summary>Moves the caret's table column left, carrying its alignment (ribbon). A no-op outside a table or at the left edge.</summary>
    public void TableMoveColumnLeft() => _caret?.TableMoveColumnLeft();

    /// <summary>Moves the caret's table column right, carrying its alignment (ribbon). A no-op outside a table or at the right edge.</summary>
    public void TableMoveColumnRight() => _caret?.TableMoveColumnRight();

    /// <summary>Sets the caret column's alignment, rewriting only the delimiter row (ribbon). A no-op outside a table.</summary>
    public void TableSetColumnAlignment(ColumnAlignment alignment) => _caret?.TableSetColumnAlignment(alignment);

    /// <summary>Clears the caret's table cell (ribbon). A no-op outside a table.</summary>
    public void TableClearCell() => _caret?.TableClearCell();

    /// <summary>
    /// Whether the active prose line wraps in place while edited (reveal-wrap, Decision 9 revised) vs. slides —
    /// the View tab's "wrap while editing" toggle. Reads/writes the markdown bridge; reports the default
    /// (<see langword="true"/>) and ignores writes when no markdown document is attached.
    /// </summary>
    public bool EditWrapEnabled
    {
        get => _bridge is MarkdownViewBridge bridge ? bridge.EditWrapEnabled : true;
        set { if (_bridge is MarkdownViewBridge bridge) bridge.EditWrapEnabled = value; }
    }

    /// <summary>
    /// The table cell-overflow mode (§5.6) every realized table renders under — the View tab's overflow toggle.
    /// Reads/writes the markdown bridge; reports the default (<see cref="TableOverflow.Wrap"/>) and ignores
    /// writes when no markdown document is attached.
    /// </summary>
    public TableOverflow OverflowMode
    {
        get => _bridge is MarkdownViewBridge bridge ? bridge.OverflowMode : TableOverflow.Wrap;
        set { if (_bridge is MarkdownViewBridge bridge) bridge.OverflowMode = value; }
    }

    // ───────────────────────────── text input (typing) ─────────────────────────────

    /// <summary>
    /// Printable input → <see cref="EditController.Apply"/> (insert at the caret / replace the
    /// selection). Bracketed paste (<see cref="TextInputEventArgs.FromPaste"/> — the terminal's
    /// own paste keybinding, and the only inbound path for <i>external</i> clipboard content,
    /// FB-3) applies the whole payload <b>literally as one splice</b>, its own undo unit
    /// (<c>EditKind.Paste</c>); M1 never reparses pasted text (M4 owns smart paste).
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);

        if (e.Handled || _caret is not { } caret || e.Text.Length == 0)
            return;

        if (e.FromPaste)
            caret.Paste(e.Text.ToString());
        else
            caret.InsertText(e.Text.ToString());

        e.Handled = true;
    }

    // ───────────────────────────── mouse ─────────────────────────────

    /// <summary>
    /// Attached only: click positions the caret (panel-local hit → block → run map → source
    /// position), double-click selects the word, triple-click selects the block/paragraph, and a
    /// single-click press begins a capture drag that extends the selection.
    /// </summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Handled || e.Button != MouseButton.Left || _caret is not { } caret || _panel is not { } panel)
            return;

        Focus(FocusNavigationMethod.Pointer);
        var local = e.GetPosition(panel);

        if (e.ClickCount >= 3)
        {
            caret.SelectBlockAt(caret.PositionFromContentPoint(local.Column, local.Row).Position);
        }
        else if (e.ClickCount == 2)
        {
            caret.SelectWordAt(caret.PositionFromContentPoint(local.Column, local.Row).Position);
        }
        else
        {
            caret.ClickAt(local.Column, local.Row);
            if (CaptureMouse())
                _dragging = true;
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging || _caret is not { } caret || _panel is not { } panel)
            return;

        var local = e.GetPosition(panel);
        caret.DragTo(local.Column, local.Row); // coordinates clamp inside — dragging past an edge keeps extending
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_dragging || e.Button != MouseButton.Left)
            return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        _dragging = false;
        base.OnLostMouseCapture(e);
    }

    // ───────────────────────────── caret publication + scroll-follow ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        PublishCaret();
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        _controller?.SealGroup(); // a focus boundary seals the open typing group (TextBox parity)
        ClearCaret();
    }

    /// <summary>
    /// Caret changed: re-publish and scroll-follow — minimal offset writes through
    /// <see cref="ScrollViewer.EnsureVisible"/> keep the caret's content cell inside the viewport
    /// (the SCP coerces the offset into range).
    /// </summary>
    private void OnCaretUpdated()
    {
        if (_caret is not { } caret)
            return;

        // The position read can refine a lazily wrapped block's height (GetRunMap's
        // estimate-then-refine), which raises HeightsChanged — the guard keeps that from
        // re-entering the publish path mid-read; the values below are post-refine either way.
        int documentRow, cell;
        _publishing = true;
        try
        {
            (documentRow, cell) = caret.VisualDocumentPosition();
        }
        finally
        {
            _publishing = false;
        }

        if (_hasFocus)
            PublishCaret(documentRow, cell);

        _scrollViewer?.EnsureVisible(new Cursorial.Rendering.Rect(cell, documentRow, 1, 1));
    }

    /// <summary>
    /// Heights moved (edit, rewrap, width change): the caret's document row may have shifted with
    /// the prefix sums — re-publish at the recomputed position. No scroll-follow: only a caret
    /// <i>move</i> steers the viewport.
    /// </summary>
    private void OnDocumentHeightsChanged()
    {
        // A height change (a lazily-realized block refining, or a wrap-reveal prose block reflowing as the
        // caret enters/edits it) can push the caret's document row outside the viewport — so scroll-FOLLOW
        // the caret, not just re-publish it. OnCaretUpdated publishes AND calls EnsureVisible; the guard keeps
        // its VisualDocumentPosition read (which can itself refine a height) from re-entering here.
        if (!_publishing)
            OnCaretUpdated();
    }

    private void PublishCaret()
    {
        if (_caret is { } caret)
        {
            // Refresh reveal before publishing (focus gained, attach, height refine — none of which
            // routed through the caret's own move path). No-op on the plain surface.
            _bridge?.OnCaretPositioned(caret.Position);

            _publishing = true;
            try
            {
                var (documentRow, cell) = caret.VisualDocumentPosition();
                PublishCaret(documentRow, cell);
            }
            finally
            {
                _publishing = false;
            }
        }
        else
        {
            PublishCaret(_caretDocumentRow, _caretColumn);
        }
    }

    private void PublishCaret(int documentRow, int cell)
    {
        if (_panel is not { } panel || UIApplication.Current is not { } application)
            return;

        // Published element-local on the PANEL (content coordinates): frame assembly folds the
        // SCP's composite scroll offset live and gates visibility on the zone clip, so a scroll
        // moves/hides the terminal cursor with no re-publication and zero re-raster.
        ITerminalCaretService caretService = application.CaretService;
        caretService.Publish(panel, cell, documentRow, CursorShape.BlinkingBar);
    }

    private void ClearCaret()
    {
        if (_panel is not { } panel || UIApplication.Current is not { } application)
            return;

        ITerminalCaretService caretService = application.CaretService;
        caretService.Clear(panel);
    }
}
