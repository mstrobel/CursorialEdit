using System.Text;

using Cursorial.Text;

namespace CursorialEdit.Document.Model;

/// <summary>
/// The <b>formatted</b> (marks-hidden) projection of one table cell's content (Decision 9 — per-cell
/// reveal): the Document-project analogue of the prose <c>RunMapBuilder.ClassifyMarks</c>/<c>ClassifyLine</c>
/// pipeline, adapted to a single cell. Given the cell's trimmed content and its cell-relative inline runs
/// it classifies every content position as a hidden syntax <b>mark</b> (emphasis/strong/strikethrough/link
/// delimiters, code fences) or visible <b>content</b>, accumulates the per-position inline
/// <see cref="CellInlineStyle"/>, and renders the marks-hidden <see cref="Display"/> string with the
/// display↔source maps that let a caret/click land on the right source offset even though the marks are
/// gone (§2.4).
/// </summary>
/// <remarks>
/// <para>
/// A cell with no formatting marks and no styling is <see cref="IsPlain"/> — its display equals its content
/// and it renders byte-identically to the pre-formatting path (the common case; no display string / maps are
/// built, so plain tables pay only the cheap inline projection). Only a cell that actually carries an inline
/// construct builds the full projection.
/// </para>
/// <para>
/// The class lives in the Document project (with Markdig-derived <see cref="InlineRun"/>s) and exposes only
/// plain data (a string, cell offsets, styles) — no Markdig type crosses into the view.
/// </para>
/// </remarks>
internal sealed class CellFormat
{
    private readonly CellInlineStyle[] _styleAt;   // per display char (Display.Length)
    private readonly int[] _displayToSrc;          // display index → block-relative source ([Display.Length + 1])

    private CellFormat(string display, bool isPlain, int displayWidth, int contentStart, CellInlineStyle[] styleAt, int[] displayToSrc)
    {
        Display = display;
        IsPlain = isPlain;
        DisplayWidth = displayWidth;
        ContentStart = contentStart;
        _styleAt = styleAt;
        _displayToSrc = displayToSrc;
    }

    /// <summary>The marks-hidden formatted display text (equals the content when <see cref="IsPlain"/>).</summary>
    public string Display { get; }

    /// <summary>Whether the cell has no formatting marks and no styling — it renders raw, byte-identical to the pre-formatting path.</summary>
    public bool IsPlain { get; }

    /// <summary>The display's rendered width in cells (marks hidden → ≤ the raw content width).</summary>
    public int DisplayWidth { get; }

    /// <summary>The block-relative source offset the cell's content begins at (display index 0's source, before any leading mark).</summary>
    public int ContentStart { get; }

    /// <summary>The inline style of display char <paramref name="d"/>.</summary>
    public CellInlineStyle StyleAt(int d) => _styleAt[d];

    /// <summary>The block-relative source offset of display index <paramref name="d"/> (clamped; the display end maps to the content end).</summary>
    public int SourceOf(int d) => _displayToSrc[Math.Clamp(d, 0, Display.Length)];

    /// <summary>Whether display chars <paramref name="d"/> and <paramref name="d"/>+1 are source-contiguous (no hidden mark collapsed between them) — the run-grouping test for 1:1 styled runs.</summary>
    public bool ContiguousAfter(int d) => _displayToSrc[d + 1] - _displayToSrc[d] == 1;

    /// <summary>
    /// Projects the cell whose trimmed <paramref name="content"/> begins at block-relative
    /// <paramref name="contentStart"/> from its cell-relative <paramref name="runs"/> (offsets measured from
    /// <paramref name="contentStart"/>). A cell with no container/code/link inline is <see cref="IsPlain"/>.
    /// </summary>
    public static CellFormat Build(string content, int contentStart, IReadOnlyList<InlineRun> runs)
    {
        int n = content.Length;

        bool anyMark = false;
        bool anyStyle = false;
        foreach (var run in runs)
        {
            if (IsContainer(run.Kind) || run.Kind == InlineRunKind.Code)
                anyMark = true;
            if (StyleOf(run.Kind) != CellInlineStyle.None)
                anyStyle = true;
        }

        if ((!anyMark && !anyStyle) || n == 0)
            return Plain(content, contentStart);

        var mark = new bool[n];
        var isContent = new bool[n];
        var style = new CellInlineStyle[n];

        // Pass A: flag every formatting container / code span as a candidate mark, and accumulate style.
        foreach (var run in runs)
        {
            int s = run.SourceStart;
            int e = run.SourceStart + run.SourceLength;
            if (s < 0 || e > n || e <= s)
                continue;

            if (IsContainer(run.Kind) || run.Kind == InlineRunKind.Code)
                for (var i = s; i < e; i++)
                    mark[i] = true;

            var flag = StyleOf(run.Kind);
            if (flag != CellInlineStyle.None)
                for (var i = s; i < e; i++)
                    style[i] |= flag;
        }

        // Pass B: flag visible content (which wins over a mark) — a code span's interior between its backtick
        // fences, and every non-container leaf. So a container's delimiters (span minus content) stay marks.
        foreach (var run in runs)
        {
            int s = run.SourceStart;
            int e = run.SourceStart + run.SourceLength;
            if (s < 0 || e > n || e <= s)
                continue;

            if (run.Kind == InlineRunKind.Code)
            {
                int b1 = 0;
                while (s + b1 < e && content[s + b1] == '`')
                    b1++;
                int b2 = 0;
                while (e - 1 - b2 >= s + b1 && content[e - 1 - b2] == '`')
                    b2++;
                for (var i = s + b1; i < e - b2; i++)
                    isContent[i] = true;
            }
            else if (!IsContainer(run.Kind))
            {
                for (var i = s; i < e; i++)
                    isContent[i] = true;
            }
        }

        var sb = new StringBuilder(n);
        var styleAt = new List<CellInlineStyle>(n);
        var displayToSrc = new List<int>(n + 1);
        for (var i = 0; i < n; i++)
        {
            if (mark[i] && !isContent[i])
                continue; // a hidden mark: no display cell (it collapses onto the following content position)

            styleAt.Add(style[i]);
            displayToSrc.Add(contentStart + i);
            sb.Append(content[i]);
        }

        displayToSrc.Add(contentStart + n); // the display end maps to the content end (past any trailing mark)

        string display = sb.ToString();
        if (display.Length == 0)
            // The content is ALL mark with no visible child — an empty-alt image ![](url) or empty-text link
            // [](url). A marks-hidden display would be invisible AND, being span-non-empty but fragment-empty,
            // leave the cell with no caret stop. Fall back to the raw path: render the source verbatim with a
            // clickable stop, exactly as the pre-formatting behavior did.
            return Plain(content, contentStart);

        int width = GraphemeWidth.StringWidth(display);
        return new CellFormat(display, isPlain: false, width, contentStart, [.. styleAt], [.. displayToSrc]);
    }

    private static CellFormat Plain(string content, int contentStart)
    {
        int n = content.Length;
        int width = GraphemeWidth.StringWidth(content);
        // A plain cell renders raw (no map is consulted), but a defensive identity map keeps every accessor total.
        var styleAt = new CellInlineStyle[n];
        var displayToSrc = new int[n + 1];
        for (var i = 0; i <= n; i++)
            displayToSrc[i] = contentStart + i;
        return new CellFormat(content, isPlain: true, width, contentStart, styleAt, displayToSrc);
    }

    /// <summary>The inline formatting a run kind contributes to the content it encloses (mirrors the prose <c>RunMapBuilder.StyleOf</c>).</summary>
    private static CellInlineStyle StyleOf(InlineRunKind kind) => kind switch
    {
        InlineRunKind.Strong => CellInlineStyle.Bold,
        InlineRunKind.Emphasis => CellInlineStyle.Italic,
        InlineRunKind.Strikethrough => CellInlineStyle.Strikethrough,
        InlineRunKind.Code => CellInlineStyle.Code,
        InlineRunKind.Link or InlineRunKind.AutoLink or InlineRunKind.Image => CellInlineStyle.Link,
        _ => CellInlineStyle.None,
    };

    private static bool IsContainer(InlineRunKind kind) => kind is
        InlineRunKind.Emphasis or InlineRunKind.Strong or InlineRunKind.Strikethrough
        or InlineRunKind.Link or InlineRunKind.Image;
}
