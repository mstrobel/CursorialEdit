using Cursorial.Drawing.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;
using CursorialEdit.Layout;

namespace CursorialEdit.Presenters;

/// <summary>
/// The code-block presenter (§2.1): fenced (<c>```</c>/<c>~~~</c>) and indented (four-space) code
/// render as a filled block — a code-fill background across the block width — with the captured
/// language (<see cref="Document.Model.Block.FenceInfo"/>) syntax-highlighted by
/// <see cref="MiniHighlighter"/> for the framework's own languages (C#, XAML, JSON, Markdown, shell)
/// and drawn <b>monochrome</b> for any other language. The <c>```</c>/<c>~~~</c> fences are hidden
/// marks: an inactive opening fence shows a dim language label instead, and a fence line reveals its
/// literal source when it is the active line.
/// </summary>
public sealed class CodeBlockPresenter : LeafBlockPresenter
{
    private readonly CodeLanguage _language;
    private readonly string? _fenceInfo;
    private readonly char _fenceChar; // the opening fence character (` or ~), or '\0' for indented code

    /// <summary>Creates the presenter for a fenced or indented code block.</summary>
    /// <param name="lines">The block's source lines (fence-open, body…, fence-close for fenced code).</param>
    /// <param name="kind"><see cref="BlockKind.FencedCode"/> or <see cref="BlockKind.IndentedCode"/>.</param>
    /// <param name="fenceInfo">The fence info string (<see cref="Document.Model.Block.FenceInfo"/>) — the language tag.</param>
    public CodeBlockPresenter(IReadOnlyList<Line> lines, BlockKind kind, string? fenceInfo = null)
        : base(lines, [], kind, headingLevel: null, WrapMode.NoWrap)
    {
        _fenceInfo = fenceInfo;
        _language = MiniHighlighter.Detect(fenceInfo);
        _fenceChar = kind == BlockKind.FencedCode && lines.Count > 0 ? OpeningFenceChar(lines[0].Text) : '\0';
    }

    private static char OpeningFenceChar(string text)
    {
        var span = text.AsSpan().TrimStart(' ');
        return span.Length >= 3 && (span[0] == '`' || span[0] == '~') ? span[0] : '\0';
    }

    /// <inheritdoc/>
    protected override void PaintBackground(RenderContext context, int width, int rows)
    {
        if (width > 0 && rows > 0)
            context.FillRectangle(new Rect(0, 0, width, rows), MarkdownStyles.CodeFillColor);
    }

    /// <inheritdoc/>
    protected override void DrawInactiveRow(RenderContext context, RunMap map, int row, string blockText, IBrush foreground)
    {
        int line = map.LineOfRow(row);

        if (IsFenceRow(line))
        {
            // The opening fence carries a dim language label; a closing fence is blank fill. Both hide
            // their `````/`~~~` source until the line is active (then the base reveal path shows it).
            if (line == 0 && !string.IsNullOrWhiteSpace(_fenceInfo))
                context.DrawText(0, row, _fenceInfo!.Trim(), MarkdownStyles.CodeLabelBrush, MarkdownStyles.CodeFillBrush, MarkdownStyles.Dim);
            return;
        }

        string display = DisplayLine(line);
        if (display.Length == 0)
            return;

        // Draw the whole line monochrome over the fill, then overdraw the highlighted token spans.
        context.DrawText(0, row, display, foreground, MarkdownStyles.CodeFillBrush);

        // Tokens are in ascending Start order, so carry a running (char, cell) cursor instead of
        // re-measuring StringWidth([0, Start)) per token — O(n) overdraw, not O(n·tokenCount).
        int prevChar = 0;
        int prevCell = 0;
        foreach (var token in MiniHighlighter.Tokenize(_language, display))
        {
            if (token.Start < prevChar || token.Start >= display.Length || token.Length <= 0)
                continue;

            int length = Math.Min(token.Length, display.Length - token.Start);
            prevCell += GraphemeWidth.StringWidth(display.AsSpan(prevChar, token.Start - prevChar));
            prevChar = token.Start;
            context.DrawText(prevCell, row, display.AsSpan(token.Start, length), MiniHighlighter.BrushFor(token.Class), MarkdownStyles.CodeFillBrush);
        }
    }

    /// <summary>
    /// The display text of source <paramref name="line"/>. The literal source is drawn verbatim (indented
    /// code keeps its leading indent — it is real source the user edits, and rendering it faithfully keeps
    /// the inactive row aligned with the active/revealed row so the caret never jumps between them).
    /// </summary>
    private string DisplayLine(int line) => Lines[line].Text;

    /// <summary>Whether source <paramref name="line"/> is a fenced-code delimiter row (opening or closing).</summary>
    private bool IsFenceRow(int line)
    {
        if (Kind != BlockKind.FencedCode)
            return false;
        if (line == 0)
            return true; // the opening fence
        return line == Lines.Count - 1 && Lines.Count >= 2 && IsFenceDelimiter(Lines[line].Text);
    }

    /// <summary>
    /// Whether <paramref name="text"/> closes the block's opening fence: same fence character (a `````
    /// block is not closed by a `~~~` content line, and vice versa) and at least three of it.
    /// </summary>
    private bool IsFenceDelimiter(string text)
    {
        var span = text.AsSpan().TrimStart(' ');
        if (span.Length < 3 || span[0] != _fenceChar)
            return false;

        int count = 0;
        while (count < span.Length && span[count] == _fenceChar)
            count++;
        return count >= 3;
    }
}
