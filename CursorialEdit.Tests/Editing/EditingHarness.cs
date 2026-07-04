using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// The M1.WP8 test harness: a headless host showing an <see cref="EditorControl"/> wired
/// <b>directly</b> to a real document pipeline (buffer → controller → producer → bridge —
/// the <c>ReviewRegressionTests.HeightSourceSwap…</c> construction) through
/// <see cref="EditorControl.AttachDocument"/>, focused so the terminal caret publishes.
/// Input goes through <see cref="UITestHost"/>'s deterministic queue; assertions read the
/// composited <see cref="UITestHost.FrameBuffer"/> (cursor position, cell styles) and the
/// live buffer/caret state.
/// </summary>
internal sealed class EditingHarness : IDisposable
{
    private EditingHarness(
        UITestHost host, EditorControl editor, DocumentBuffer buffer,
        EditController controller, PlainTextBlockProducer producer, BlockViewBridge bridge)
    {
        Host = host;
        Editor = editor;
        Buffer = buffer;
        Controller = controller;
        Producer = producer;
        Bridge = bridge;
    }

    public UITestHost Host { get; }

    public EditorControl Editor { get; }

    public DocumentBuffer Buffer { get; }

    public EditController Controller { get; }

    public PlainTextBlockProducer Producer { get; }

    public BlockViewBridge Bridge { get; }

    public DocumentCaret Caret => Editor.DocumentCaretPart!;

    public Cursorial.UI.Controls.ScrollViewer ScrollViewer => Editor.ScrollViewerPart!;

    /// <summary>
    /// The editor's app-internal clipboard store — always a <b>fresh instance per harness</b>
    /// (never <see cref="InternalClipboard.Shared"/>: xunit runs test classes in parallel, and
    /// the process-wide store would race across hosts).
    /// </summary>
    public InternalClipboard Clipboard => Editor.Clipboard;

    /// <summary>The composited terminal cursor cell (the caret's rendered position).</summary>
    public (int Column, int Row) Cursor => (Host.FrameBuffer.CursorColumn, Host.FrameBuffer.CursorRow);

    /// <param name="document">The initial document text.</param>
    /// <param name="preset">A <see cref="TestSupport.CapabilityPresets"/> name.</param>
    /// <param name="columns">Terminal columns.</param>
    /// <param name="rows">Terminal rows.</param>
    /// <param name="captureFrameBytes">
    /// Capture each frame's emitted wire bytes (<see cref="UITestHost.LastFrameBytes"/>) — the
    /// WP9 OSC 52 observable, harvested via <see cref="SettleCollectingBytes"/>. Off by default
    /// so benchmark allocation profiles stay framework-only.
    /// </param>
    public static EditingHarness Create(
        string document,
        string preset = nameof(TestCapabilities.KittyTruecolor),
        int columns = 30,
        int rows = 10,
        bool captureFrameBytes = false)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
            CaptureFrameBytes = captureFrameBytes,
        });

        var buffer = new DocumentBuffer(document);
        var controller = new EditController(buffer, host.Time);
        var producer = new PlainTextBlockProducer(controller);
        var bridge = new BlockViewBridge(buffer, producer);

        var editor = new EditorControl
        {
            Clipboard = new InternalClipboard(), // per-harness isolation (see Clipboard remarks)
        };
        editor.AttachDocument(controller, bridge);

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");

        editor.Focus();
        Assert.True(host.RunUntilIdle(), "focusing the editor did not settle");

        return new EditingHarness(host, editor, buffer, controller, producer, bridge);
    }

    public void Settle() => Assert.True(Host.RunUntilIdle(), "the frame loop did not settle");

    /// <summary>Sends a key chord and settles.</summary>
    public void Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Host.SendKey(key, modifiers);
        Settle();
    }

    /// <summary>Sends a Ctrl(-Shift)-letter chord the way the wire delivers it: a Character key event carrying the letter.</summary>
    public void Chord(char letter, KeyModifiers modifiers)
    {
        Host.SendKey(Cursorial.Input.Key.Character, modifiers, letter.ToString());
        Settle();
    }

    /// <summary>Types text (one character key event per rune → TextInput) and settles.</summary>
    public void Type(string text)
    {
        Host.SendText(text);
        Settle();
    }

    /// <summary>
    /// Injects raw wire bytes through the real <c>VtInputDevice</c> (bracketed paste envelopes,
    /// bare C0 bytes), blocks for pump catch-up (the blocked work is thread-pool-side — the
    /// framework's own <c>SendBytes</c> test idiom), and settles.
    /// </summary>
    public void Bytes(ReadOnlySpan<byte> rawBytes)
    {
        Host.SendBytes(rawBytes);
        Host.DrainParsedInputAsync().GetAwaiter().GetResult(); // blocking — stays on the UI thread
        Settle();
    }

    /// <summary>
    /// Sends a Ctrl(-Shift)-letter chord and returns every wire byte emitted while settling —
    /// the WP9 OSC 52 observable (requires <c>captureFrameBytes</c> at <see cref="Create"/>).
    /// </summary>
    public byte[] ChordCollectingBytes(char letter, KeyModifiers modifiers)
    {
        Host.SendKey(Cursorial.Input.Key.Character, modifiers, letter.ToString());
        return SettleCollectingBytes();
    }

    /// <summary>
    /// Steps frames one at a time until the loop settles, concatenating each frame's captured
    /// wire bytes. <see cref="UITestHost.LastFrameBytes"/> holds only the <i>last</i> frame's
    /// emission and clipboard writes ride Phase 6 of whichever frame handled the key, so a
    /// plain <c>RunUntilIdle</c> would overwrite the interesting frame — this harvests per frame.
    /// </summary>
    public byte[] SettleCollectingBytes(int maxFrames = 100)
    {
        using var collected = new MemoryStream();
        for (var i = 0; i < maxFrames; i++)
        {
            Host.RunFrame();
            collected.Write(Host.LastFrameBytes.Span);
            if (Host.RunUntilIdle(maxFrames: 0)) // zero-frame probe: is the loop idle now?
                return collected.ToArray();
        }

        Assert.Fail("the frame loop did not settle while collecting frame bytes");
        return [];
    }

    /// <summary>Clicks (press + release) at a window cell; <paramref name="clickCount"/> 2 = double, 3 = triple.</summary>
    public void Click(int column, int row, int clickCount = 1)
    {
        Host.SendClick(column, row, MouseButton.Left, clickCount);
        Settle();
    }

    /// <summary>A press-move-release drag between two window cells (the capture-extend gesture).</summary>
    public void Drag(int fromColumn, int fromRow, int toColumn, int toRow)
    {
        Host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(fromColumn, fromRow),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            ClickCount = 1,
            Timestamp = Host.Time.GetUtcNow(),
        });
        Host.SendMouseMove(toColumn, toRow);
        Host.SendInput(new MouseEvent
        {
            Kind = MouseEventKind.ButtonUp,
            Position = new CellPosition(toColumn, toRow),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = Host.Time.GetUtcNow(),
        });
        Settle();
    }

    /// <summary>The composited background color of one cell — the selection-painting observable.</summary>
    public Color BackgroundAt(int column, int row) => Host.GetCell(column, row).Style.Background;

    /// <summary>
    /// Asserts cells [<paramref name="fromColumn"/>, <paramref name="toColumn"/>) of
    /// <paramref name="row"/> carry the selection fill: a uniform background that differs from
    /// <paramref name="plainColumn"/>'s (an unselected reference cell on a color tier).
    /// </summary>
    public void AssertSelectionPainted(int row, int fromColumn, int toColumn, int plainColumn)
    {
        var fill = BackgroundAt(fromColumn, row);
        for (int column = fromColumn + 1; column < toColumn; column++)
        {
            Assert.Equal(fill, BackgroundAt(column, row));
        }

        Assert.NotEqual(fill, BackgroundAt(plainColumn, row));
    }

    /// <summary>Asserts the caret's source position (and that no selection exists when <paramref name="anchor"/> is null).</summary>
    public void AssertCaret(int line, int col, TextPosition? anchor = null)
    {
        Assert.Equal(new TextPosition(line, col), Caret.Position);
        Assert.Equal(anchor, Caret.SelectionAnchor);
    }

    public void Dispose() => Host.Dispose();
}
