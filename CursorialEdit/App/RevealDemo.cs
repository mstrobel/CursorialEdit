using Cursorial.Input;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;
using CursorialEdit.Presenters;

namespace CursorialEdit.App;

/// <summary>
/// A minimal, self-contained <b>reveal-on-edit demo</b> (the M2 checkpoint deliverable): renders a
/// markdown document through the real <see cref="MarkdigBlockProducer"/> and <see cref="ParagraphPresenter"/>,
/// reveals the raw markdown of the line the cursor sits on (every other line stays formatted), and
/// re-parses live as you type. It deliberately does <b>not</b> touch the reviewed production
/// <c>EditorShell</c>/<c>BlockViewBridge</c>/<c>DocumentPanel</c> pipeline — that full presenter fan-out
/// and app wiring is WP7. For hands-on feel only: presenters are rebuilt per edit (fine for a demo doc).
/// </summary>
/// <remarks>
/// Keys: ↑/↓ move the cursor between lines (the line reveals its marks); ←/→ move within the line
/// (the active line slides to keep the cursor visible); printable keys / Backspace edit the active line
/// (the document re-parses and re-renders); Esc or Ctrl+Q quits.
/// </remarks>
public sealed class RevealDemoView : Control
{
    private const string PartScroll = "PART_Scroll";
    private const string PartStack = "PART_Stack";

    private static readonly ControlTemplate DemoTemplate = new(static ctx =>
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        ctx.RegisterName(PartStack, stack);
        var scroll = new ScrollViewer { Content = stack, Focusable = false, IsTabStop = false };
        ctx.RegisterName(PartScroll, scroll);
        return scroll;
    })
    {
        TargetType = typeof(RevealDemoView),
    };

    private readonly DocumentBuffer _buffer;
    private readonly EditController _controller;
    private readonly MarkdigBlockProducer _producer;

    private ScrollViewer? _scroll;
    private StackPanel? _stack;
    private ParagraphPresenter? _activePresenter;

    private int _caretLine;
    private int _caretCol;
    private int _slide;
    private bool _hasFocus;
    private bool _focusRequested;

    /// <summary>Creates the demo over an initial markdown document.</summary>
    /// <param name="markdown">The document text; a built-in sample when empty.</param>
    public RevealDemoView(string markdown)
    {
        _buffer = new DocumentBuffer(markdown.Length == 0 ? SampleDocument : markdown);
        _controller = new EditController(_buffer);
        _producer = new MarkdigBlockProducer(_controller);

        Focusable = true;
        Template = DemoTemplate;
    }

    /// <inheritdoc/>
    protected override bool HandlesScrolling => true;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scroll = GetTemplatePart<ScrollViewer>(PartScroll);
        _stack = GetTemplatePart<StackPanel>(PartStack);
        RebuildPresenters();

        // Activation auto-focus only walks DESCENDANTS, so a focusable ROOT like this view is never
        // auto-focused (a framework gap — see docs/framework-feedback.md). Self-focus once the layout
        // has settled: posted to a later frame (OnApplyTemplate runs at measure, before arrange), so
        // Focus() lands on a laid-out element. That fires OnGotFocus → the caret publishes and shows.
        if (!_focusRequested)
        {
            _focusRequested = true;
            UIApplication.Current?.Dispatcher.Post(() =>
            {
                if (!IsFocused)
                    Focus();
            });
        }
    }

    // ───────────────────────────── rendering ─────────────────────────────

    /// <summary>Rebuilds the presenter list from the current block list and re-applies the reveal state.</summary>
    private void RebuildPresenters()
    {
        if (_stack is not { } stack)
            return;

        foreach (var child in stack.Children.ToArray())
        {
            stack.Children.Remove(child);
            child.TearDown();
        }

        var blocks = _producer.Blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            int startLine = blocks.GetStartLine(i);
            var lines = new List<Line>(block.LineCount);
            for (var k = 0; k < block.LineCount; k++)
                lines.Add(_buffer.GetLine(startLine + k));

            var presenter = new ParagraphPresenter(lines, block.InlineRuns, block.Kind, block.HeadingLevel);
            stack.Children.Add(presenter);
        }

        ApplyReveal();
    }

    /// <summary>Reveals the active block's caret line (slid to keep the cursor visible) and deactivates the rest.</summary>
    private void ApplyReveal()
    {
        if (_stack is not { } stack)
            return;

        var blocks = _producer.Blocks;
        if (blocks.Count == 0)
            return;

        int activeIndex = blocks.IndexOfLine(Math.Clamp(_caretLine, 0, Math.Max(0, _buffer.LineCount - 1)));
        _activePresenter = null;

        for (var i = 0; i < stack.Children.Count && i < blocks.Count; i++)
        {
            if (stack.Children[i] is not ParagraphPresenter presenter)
                continue;

            if (i == activeIndex)
            {
                int lineInBlock = _caretLine - blocks.GetStartLine(i);
                presenter.SetReveal(lineInBlock, _slide); // reveal so the active-line map exists
                var map = presenter.MapForWidth(ViewportColumns);
                var (_, caretCell) = map.Locate(BlockRelativeCaretOffset(blocks.GetStartLine(i)));
                var (row, _) = map.Locate(BlockRelativeCaretOffset(blocks.GetStartLine(i)));
                _slide = HorizontalSlide.Compute(_slide, caretCell, map.RowWidth(row), ViewportColumns);
                presenter.SetReveal(lineInBlock, _slide);
                _activePresenter = presenter;
            }
            else
            {
                presenter.SetReveal(null);
            }
        }

        PublishCaret(activeIndex);
    }

    /// <summary>The caret's block-relative UTF-16 source offset (the origin the run map is measured from).</summary>
    private int BlockRelativeCaretOffset(int blockStartLine)
    {
        int offset = 0;
        for (var line = blockStartLine; line < _caretLine; line++)
        {
            var l = _buffer.GetLine(line);
            offset += l.Text.Length + l.EndingText.Length;
        }

        return offset + Math.Clamp(_caretCol, 0, _buffer.GetLine(_caretLine).Text.Length);
    }

    private int ViewportColumns => Math.Max(1, _scroll?.Viewport.Columns ?? 80);

    // ───────────────────────────── caret publication ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        ApplyReveal();
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        if (_stack is { } stack && stack.Children.FirstOrDefault() is { } first)
            (UIApplication.Current?.CaretService)?.Clear(first);
    }

    private void PublishCaret(int activeIndex)
    {
        // Not gated on focus: keys route to this root element regardless of the framework focus state,
        // and the demo is the sole full-screen surface, so the caret should always be visible and
        // follow the active line. (The production surface gates on focus — this is a demo shortcut.)
        if (_activePresenter is null || UIApplication.Current is not { } app)
            return;

        // The active line is one slid row; the caret sits at (caretCell − slide) on the block's own
        // active row. Published on the presenter so the frame folds its offset and clips at its zone.
        var map = _activePresenter.MapForWidth(ViewportColumns);
        var (row, caretCell) = map.Locate(BlockRelativeCaretOffset(_producer.Blocks.GetStartLine(activeIndex)));
        app.CaretService.Publish(_activePresenter, Math.Max(0, caretCell - _slide), row, CursorShape.BlinkingBar);
    }

    // ───────────────────────────── input ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        switch (e.Key)
        {
            case Key.UpArrow when e.Modifiers == KeyModifiers.None:
                MoveLine(-1); e.Handled = true; break;
            case Key.DownArrow when e.Modifiers == KeyModifiers.None:
                MoveLine(+1); e.Handled = true; break;
            case Key.LeftArrow when e.Modifiers == KeyModifiers.None:
                _caretCol = Math.Max(0, _caretCol - 1); ApplyReveal(); e.Handled = true; break;
            case Key.RightArrow when e.Modifiers == KeyModifiers.None:
                _caretCol = Math.Min(_buffer.GetLine(_caretLine).Text.Length, _caretCol + 1); ApplyReveal(); e.Handled = true; break;
            case Key.Home when e.Modifiers == KeyModifiers.None:
                _caretCol = 0; _slide = 0; ApplyReveal(); e.Handled = true; break;
            case Key.End when e.Modifiers == KeyModifiers.None:
                _caretCol = _buffer.GetLine(_caretLine).Text.Length; ApplyReveal(); e.Handled = true; break;
            case Key.Backspace when e.Modifiers == KeyModifiers.None:
                Backspace(); e.Handled = true; break;
            case Key.Character when e.Modifiers is KeyModifiers.Control && e.Text.Span.Equals("q", StringComparison.OrdinalIgnoreCase):
                UIApplication.Current?.Shutdown(); e.Handled = true; break;
            case Key.Escape:
                UIApplication.Current?.Shutdown(); e.Handled = true; break;
        }
    }

    /// <inheritdoc/>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        string text = e.Text.ToString();
        if (e.Handled || text.Length == 0 || text[0] < ' ')
            return;

        var at = new TextPosition(_caretLine, Math.Clamp(_caretCol, 0, _buffer.GetLine(_caretLine).Text.Length));
        _controller.Apply(new Edit(at, string.Empty, text), EditKind.Typing, new CaretState(at), new CaretState(at));
        _caretCol += text.Length;
        RebuildPresenters();
        e.Handled = true;
    }

    private void MoveLine(int delta)
    {
        int target = Math.Clamp(_caretLine + delta, 0, Math.Max(0, _buffer.LineCount - 1));
        if (target == _caretLine)
            return;

        _caretLine = target;
        _caretCol = Math.Min(_caretCol, _buffer.GetLine(_caretLine).Text.Length);
        _slide = 0;
        ApplyReveal();
        // (Scroll-follow is omitted in this minimal demo — the sample fits on screen; WP7's real
        // caret drives ScrollViewer.EnsureVisible in the production surface.)
    }

    private void Backspace()
    {
        if (_caretCol == 0)
            return;

        var start = new TextPosition(_caretLine, _caretCol - 1);
        var end = new TextPosition(_caretLine, _caretCol);
        string removed = _buffer.GetText(start, end);
        _controller.Apply(new Edit(start, removed, string.Empty), EditKind.Typing, new CaretState(end), new CaretState(start));
        _caretCol--;
        RebuildPresenters();
    }

    /// <summary>The built-in sample rendered when no file is given — exercises the reveal-capable constructs.</summary>
    private const string SampleDocument =
        "# Reveal-on-edit demo\n\n" +
        "Move the cursor with the arrow keys. The line you are **on** shows its raw\n" +
        "markdown; every *other* line stays formatted. Type to edit and watch it re-parse.\n\n" +
        "## A second heading\n\n" +
        "Here is some `inline code`, a bit of **bold**, and some _italics_ mixed in.\n\n" +
        "This is the third paragraph — try landing on it and editing a word.\n";
}
