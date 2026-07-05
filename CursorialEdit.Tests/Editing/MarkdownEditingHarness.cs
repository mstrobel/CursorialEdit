using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Presenters;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.Editing;

/// <summary>
/// The M2.WP8/WP9 test harness: a headless host showing an <see cref="EditorControl"/> wired to the
/// <b>markdown</b> pipeline (buffer → controller → <see cref="MarkdigBlockProducer"/> →
/// <see cref="MarkdownViewBridge"/> → the <see cref="LeafBlockPresenter"/> suite) through
/// <see cref="EditorControl.AttachDocument"/>, focused so reveal-on-edit and the terminal caret are
/// live. It is the markdown counterpart of <see cref="EditingHarness"/>, used to drive caret/selection/
/// word motion over run maps and to observe the active-block well and the two-zone re-raster gate.
/// </summary>
internal sealed class MarkdownEditingHarness : IDisposable
{
    private MarkdownEditingHarness(
        UITestHost host, EditorControl editor, DocumentBuffer buffer,
        EditController controller, MarkdigBlockProducer producer, MarkdownViewBridge bridge)
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

    public MarkdigBlockProducer Producer { get; }

    public MarkdownViewBridge Bridge { get; }

    public DocumentCaret Caret => Editor.DocumentCaretPart!;

    public BlockList Blocks => Producer.Blocks;

    /// <summary>The composited terminal cursor cell (the caret's rendered position).</summary>
    public (int Column, int Row) Cursor => (Host.FrameBuffer.CursorColumn, Host.FrameBuffer.CursorRow);

    public static MarkdownEditingHarness Create(
        string document,
        string preset = nameof(TestCapabilities.KittyTruecolor),
        int columns = 40,
        int rows = 12)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(columns, rows),
            Capabilities = TestSupport.CapabilityPresets.Resolve(preset),
        });

        var buffer = new DocumentBuffer(document);
        var controller = new EditController(buffer, host.Time);
        var producer = new MarkdigBlockProducer(controller);
        var bridge = new MarkdownViewBridge(buffer, producer);

        var editor = new EditorControl { Clipboard = new InternalClipboard() };
        editor.AttachDocument(controller, bridge);

        host.ShowRoot(editor);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");

        editor.Focus();
        Assert.True(host.RunUntilIdle(), "focusing the editor did not settle");

        return new MarkdownEditingHarness(host, editor, buffer, controller, producer, bridge);
    }

    public void Settle() => Assert.True(Host.RunUntilIdle(), "the frame loop did not settle");

    /// <summary>Sends a key chord and settles.</summary>
    public void Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Host.SendKey(key, modifiers);
        Settle();
    }

    /// <summary>Sends a Ctrl(-Shift)-letter chord the way the wire delivers it: a Character key carrying the letter.</summary>
    public void Chord(char letter, KeyModifiers modifiers)
    {
        Host.SendKey(Cursorial.Input.Key.Character, modifiers, letter.ToString());
        Settle();
    }

    /// <summary>Types text and settles.</summary>
    public void Type(string text)
    {
        Host.SendText(text);
        Settle();
    }

    /// <summary>Clicks (press + release) at a window cell; <paramref name="clickCount"/> 2 = double, 3 = triple.</summary>
    public void Click(int column, int row, int clickCount = 1)
    {
        Host.SendClick(column, row, MouseButton.Left, clickCount);
        Settle();
    }

    /// <summary>The composited background color of one cell — the selection / active-well observable.</summary>
    public Color BackgroundAt(int column, int row) => Host.GetCell(column, row).Style.Background;

    /// <summary>The composited text of a frame row (trimmed).</summary>
    public string RowTrimmed(int row) => Host.GetRowText(row).TrimEnd();

    /// <summary>The live presenter for the block at <paramref name="index"/> (test observability).</summary>
    public LeafBlockPresenter Presenter(int index) => Bridge.GetPresenter(Blocks[index].Id)!;

    /// <summary>Asserts the caret's source position (and that no selection exists when <paramref name="anchor"/> is null).</summary>
    public void AssertCaret(int line, int col, TextPosition? anchor = null)
    {
        Assert.Equal(new TextPosition(line, col), Caret.Position);
        Assert.Equal(anchor, Caret.SelectionAnchor);
    }

    public void Dispose() => Host.Dispose();
}
