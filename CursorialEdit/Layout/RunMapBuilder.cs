using System.Text;

using Cursorial.Rendering.Text;
using Cursorial.Text;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Layout;

/// <summary>
/// Builds the M2.WP5 <see cref="RunMap"/> for a block from its source lines and its lazily-realized
/// inline runs (architecture Decision 8 / §2.4). The builder is the sole place that decides, per
/// source line, which spans are visible text, which are syntax marks (hidden or — on the active line
/// — revealed), and which structural prefixes become synthetic glyphs; from that classification it
/// renders each line to a display string, wraps it through the framework
/// <see cref="TextLayout.Build"/>, and assembles the per-visual-row runs plus the clip clusters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mark derivation.</b> Formatting containers (emphasis, strong, strikethrough, links, images)
/// contribute their delimiters as marks — a container's span minus the visible-text leaves it
/// encloses, which handles nesting for free (<c>[**b** x](/u)</c> → <c>[**</c>, <c>**</c>,
/// <c>](/u)</c>). Inline code contributes its backtick fences. An ATX heading contributes its
/// <c>#</c> prefix. Blockquote (<c>&gt; </c>) and list (<c>- </c>/<c>1. </c>) markers become
/// synthetic glyphs mapping to their marker source (a single caret stop). Anything unrecognized
/// stays visible text — the mapping is total regardless of classification.
/// </para>
/// <para>
/// <b>Reveal.</b> When <c>activeLine</c> names a line, that line renders un-wrapped at natural width
/// with its marks shown (<see cref="RunKind.RevealedMark"/>) so the caret-visibility slide can keep
/// the caret in view without re-wrapping (Decision 9); every other line hides its marks
/// (<see cref="RunKind.HiddenMark"/>, zero cells). An inactive block hides all marks.
/// </para>
/// </remarks>
public static class RunMapBuilder
{
    private enum SegKind
    {
        Content,
        Mark,
        ListMarker,
        QuoteMarker,
    }

    /// <summary>One classified span of a line rendered into the display string (or, for a hidden mark, into nothing).</summary>
    private readonly record struct PieceRun(
        RunKind Kind, int SrcStart, int SrcLen, int DisplayStart, int DisplayLen,
        RunStyle Style = RunStyle.None, string? Glyph = null);

    /// <summary>
    /// Builds the run map for <paramref name="lines"/> (a block's source lines), classifying marks
    /// from <paramref name="inlineRuns"/> (block-relative — from <see cref="Block.InlineRuns"/>) and
    /// <paramref name="kind"/>/<paramref name="headingLevel"/>, wrapped at <paramref name="wrapWidth"/>
    /// cells under <paramref name="wrapMode"/>.
    /// </summary>
    /// <param name="lines">The block's source lines (≥ 1).</param>
    /// <param name="inlineRuns">The block's lazily-realized inline runs (block-relative offsets); empty for plain text or code.</param>
    /// <param name="kind">The block's structural kind (drives heading/list/quote prefixes).</param>
    /// <param name="headingLevel">The heading level when <paramref name="kind"/> is <see cref="BlockKind.Heading"/>; otherwise ignored.</param>
    /// <param name="wrapWidth">The soft-wrap cell budget (≤ 0 disables wrapping).</param>
    /// <param name="wrapMode">Wrap-on (<see cref="WrapMode.WordWrap"/>, default) or wrap-off (<see cref="WrapMode.NoWrap"/>, one row per logical line).</param>
    /// <param name="activeLine">The revealed active line, or <see langword="null"/> for an inactive block.</param>
    /// <param name="revealSlides">
    /// The reveal policy for the active line (Decision 9). <see langword="true"/> (default) = <b>slide</b>:
    /// the revealed line is force-unwrapped to one row (the caller slides it horizontally) — line-count
    /// invariant. <see langword="false"/> = <b>wrap</b>: the revealed line wraps in place under
    /// <paramref name="wrapMode"/> with its marks shown, so a prose paragraph keeps its surrounding context
    /// while edited (the block reflows). Ignored when <paramref name="wrapMode"/> is
    /// <see cref="WrapMode.NoWrap"/> (nothing to wrap, so the line is one row either way).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="inlineRuns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    public static RunMap Build(
        IReadOnlyList<Line> lines,
        IReadOnlyList<InlineRun> inlineRuns,
        BlockKind kind,
        int? headingLevel = null,
        int wrapWidth = 0,
        WrapMode wrapMode = WrapMode.WordWrap,
        int? activeLine = null,
        bool revealSlides = true)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(inlineRuns);
        if (lines.Count == 0)
            throw new ArgumentException("A block owns at least one line.", nameof(lines));

        int lineCount = lines.Count;
        var lineSrcStart = new int[lineCount + 1];
        var lineTextLen = new int[lineCount];
        for (var i = 0; i < lineCount; i++)
        {
            lineTextLen[i] = lines[i].Text.Length;
            lineSrcStart[i + 1] = lineSrcStart[i] + lines[i].TotalLength;
        }

        int sourceLength = lineSrcStart[lineCount];
        string blockText = BuildBlockText(lines, sourceLength);
        var (mark, content, style) = ClassifyMarks(blockText, inlineRuns, kind, lineSrcStart, lineTextLen);

        var wrapped = new TextLayout[lineCount];
        var srcToDisplay = new int[lineCount][];
        var displayToSrc = new int[lineCount][];
        var lineFirstRow = new int[lineCount + 1];

        var rowRuns = new List<Run[]>();
        var rowCells = new List<RowCluster[]>();
        var rowWidth = new List<int>();
        var rowLine = new List<int>();

        for (var i = 0; i < lineCount; i++)
        {
            bool revealed = activeLine == i;
            var (marker, markerStart, markerLen) = ScanLeadingMarker(lines[i].Text, kind);
            int hardBreakLen = kind == BlockKind.Paragraph && i < lineCount - 1
                ? HardBreakMarkerLength(lines[i].Text)
                : 0;

            var pieces = ClassifyLine(
                lines[i].Text, lineSrcStart[i], mark, content, style,
                marker, markerStart, markerLen, hardBreakLen, revealed,
                out string display, out int[] toDisplay, out int[] toSrc);

            // A lone '\r' surviving from the source (in-line content, not a terminator) would hard-break the
            // display in TextLayout.Build — splitting one source line into phantom rows and mismapping offsets.
            // Sanitize it to its control picture (1:1, so toDisplay/toSrc stay valid) before wrap AND run-build.
            display = DisplayText.SanitizeControls(display);

            // A revealed line is force-unwrapped only in SLIDE mode; in wrap-reveal it wraps in place under
            // the block's wrapMode (marks shown), so a prose paragraph keeps its context while edited.
            var lineWrap = revealed && revealSlides ? WrapMode.NoWrap : wrapMode;
            wrapped[i] = TextLayout.Build(display, wrapWidth, lineWrap);
            srcToDisplay[i] = toDisplay;
            displayToSrc[i] = toSrc;
            lineFirstRow[i] = rowRuns.Count;

            for (var r = 0; r < wrapped[i].LineCount; r++)
            {
                var (runs, cells) = BuildRow(display, wrapped[i], r, pieces);
                rowRuns.Add(runs);
                rowCells.Add(cells);
                rowWidth.Add(wrapped[i].LineWidth(r));
                rowLine.Add(i);
            }
        }

        lineFirstRow[lineCount] = rowRuns.Count;

        return new RunMap(
            [.. rowRuns], [.. rowCells], [.. rowWidth], [.. rowLine],
            wrapped, srcToDisplay, displayToSrc,
            lineFirstRow, lineSrcStart, lineTextLen,
            sourceLength, wrapWidth, wrapMode, activeLine);
    }

    /// <summary>
    /// Builds an <b>identity</b> run map for raw-source view mode (architecture Decision 12 / M2.WP10):
    /// every source line renders verbatim as one visual row (wrap-off, so the block's height is its raw
    /// line count), with every character a <see cref="RunKind.Text"/> cell — nothing hidden, nothing
    /// revealed, no synthetic markers, no active-line reveal. The source↔cell mapping is 1:1 (source
    /// offset <c>N</c> sits at cell <c>N</c>), so the caret walks raw source directly and every existing
    /// caret/selection/find operation works unchanged; syntax-token coloring is a presenter concern layered
    /// on top (<see cref="Presenters.RawSourcePresenter"/>), never a mapping change.
    /// </summary>
    /// <param name="lines">The block's source lines (≥ 1).</param>
    /// <param name="wrapWidth">The horizontal cell budget (retained for row-width/clip queries; wrap-off never collapses height).</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    public static RunMap BuildRaw(IReadOnlyList<Line> lines, int wrapWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
            throw new ArgumentException("A block owns at least one line.", nameof(lines));

        int lineCount = lines.Count;
        var lineSrcStart = new int[lineCount + 1];
        var lineTextLen = new int[lineCount];
        for (var i = 0; i < lineCount; i++)
        {
            lineTextLen[i] = lines[i].Text.Length;
            lineSrcStart[i + 1] = lineSrcStart[i] + lines[i].TotalLength;
        }

        int sourceLength = lineSrcStart[lineCount];

        // Identity classification: no marks, no content wins/loses, no inline style — every character
        // classifies as visible Content (a Text run), so the display string equals the source verbatim.
        var mark = new bool[sourceLength];
        var content = new bool[sourceLength];
        var style = new RunStyle[sourceLength];

        var wrapped = new TextLayout[lineCount];
        var srcToDisplay = new int[lineCount][];
        var displayToSrc = new int[lineCount][];
        var lineFirstRow = new int[lineCount + 1];

        var rowRuns = new List<Run[]>();
        var rowCells = new List<RowCluster[]>();
        var rowWidth = new List<int>();
        var rowLine = new List<int>();

        for (var i = 0; i < lineCount; i++)
        {
            var pieces = ClassifyLine(
                lines[i].Text, lineSrcStart[i], mark, content, style,
                marker: SegKind.Content, markerStart: 0, markerLen: 0, hardBreakLen: 0, revealed: false,
                out string display, out int[] toDisplay, out int[] toSrc);

            // NoWrap disables SOFT wrap, not TextLayout.Build's hard break on '\r' — a lone CR in raw source
            // would still split the "verbatim one row" contract. Sanitize it (1:1) to its control picture.
            display = DisplayText.SanitizeControls(display);

            wrapped[i] = TextLayout.Build(display, wrapWidth, WrapMode.NoWrap);
            srcToDisplay[i] = toDisplay;
            displayToSrc[i] = toSrc;
            lineFirstRow[i] = rowRuns.Count;

            for (var r = 0; r < wrapped[i].LineCount; r++)
            {
                var (runs, cells) = BuildRow(display, wrapped[i], r, pieces);
                rowRuns.Add(runs);
                rowCells.Add(cells);
                rowWidth.Add(wrapped[i].LineWidth(r));
                rowLine.Add(i);
            }
        }

        lineFirstRow[lineCount] = rowRuns.Count;

        return new RunMap(
            [.. rowRuns], [.. rowCells], [.. rowWidth], [.. rowLine],
            wrapped, srcToDisplay, displayToSrc,
            lineFirstRow, lineSrcStart, lineTextLen,
            sourceLength, wrapWidth, WrapMode.NoWrap, activeLine: null);
    }

    private static string BuildBlockText(IReadOnlyList<Line> lines, int sourceLength)
    {
        var sb = new StringBuilder(sourceLength);
        foreach (var line in lines)
            sb.Append(line.Text).Append(line.EndingText);
        return sb.ToString();
    }

    // ───────────────────────────── mark classification ─────────────────────────────

    /// <summary>
    /// Marks vs. content (plus per-position inline <see cref="RunStyle"/>) over the whole block source.
    /// Pass A flags every formatting-container and code span as a candidate mark; pass B flags the
    /// visible-text leaves (and code interiors) as content, which wins — so a container's delimiters
    /// (span minus children) and code's backtick fences remain marks while nested content shows. The
    /// same two passes accumulate the style flags (<see cref="StyleOf"/>) over each inline span, so a
    /// content position knows its enclosing emphasis/strong/code/strike/link. The ATX heading prefix
    /// and a setext underline line are folded in as marks (hidden when inactive, revealed when active).
    /// </summary>
    private static (bool[] Mark, bool[] Content, RunStyle[] Style) ClassifyMarks(
        string blockText, IReadOnlyList<InlineRun> inlineRuns, BlockKind kind, int[] lineSrcStart, int[] lineTextLen)
    {
        int n = blockText.Length;
        var mark = new bool[n];
        var content = new bool[n];
        var style = new RunStyle[n];

        foreach (var run in inlineRuns)
        {
            int s = run.SourceStart;
            int e = run.SourceStart + run.SourceLength;
            if (s < 0 || e > n || e <= s)
                continue;

            if (IsContainer(run.Kind) || run.Kind == InlineRunKind.Code)
                for (var i = s; i < e; i++)
                    mark[i] = true;

            var flag = StyleOf(run.Kind);
            if (flag != RunStyle.None)
                for (var i = s; i < e; i++)
                    style[i] |= flag;
        }

        foreach (var run in inlineRuns)
        {
            int s = run.SourceStart;
            int e = run.SourceStart + run.SourceLength;
            if (s < 0 || e > n || e <= s)
                continue;

            if (run.Kind == InlineRunKind.Code)
            {
                int b1 = 0;
                while (s + b1 < e && blockText[s + b1] == '`')
                    b1++;
                int b2 = 0;
                while (e - 1 - b2 >= s + b1 && blockText[e - 1 - b2] == '`')
                    b2++;
                for (var i = s + b1; i < e - b2; i++)
                    content[i] = true;
            }
            else if (!IsContainer(run.Kind))
            {
                for (var i = s; i < e; i++)
                    content[i] = true;
            }
        }

        if (kind == BlockKind.Heading)
        {
            MarkAtxPrefix(blockText, lineSrcStart[0], lineTextLen[0], mark, content);
            MarkSetextUnderline(blockText, lineSrcStart, lineTextLen, mark, content);
        }

        return (mark, content, style);
    }

    /// <summary>The inline formatting a run kind contributes to the content it encloses (§2.1).</summary>
    private static RunStyle StyleOf(InlineRunKind kind) => kind switch
    {
        InlineRunKind.Strong => RunStyle.Bold,
        InlineRunKind.Emphasis => RunStyle.Italic,
        InlineRunKind.Strikethrough => RunStyle.Strikethrough,
        InlineRunKind.Code => RunStyle.Code,
        InlineRunKind.Link or InlineRunKind.AutoLink or InlineRunKind.Image => RunStyle.Link,
        _ => RunStyle.None,
    };

    private static bool IsContainer(InlineRunKind kind) => kind is
        InlineRunKind.Emphasis or InlineRunKind.Strong or InlineRunKind.Strikethrough
        or InlineRunKind.Link or InlineRunKind.Image;

    /// <summary>
    /// Marks a setext heading's underline line (the trailing all-<c>=</c> or all-<c>-</c> line of a
    /// multi-line <see cref="BlockKind.Heading"/>) as a mark — hidden when inactive, revealed when
    /// active — so the heading renders as H1/H2 text with the underline suppressed (§2.1 [DECISION]).
    /// </summary>
    private static void MarkSetextUnderline(string blockText, int[] lineSrcStart, int[] lineTextLen, bool[] mark, bool[] content)
    {
        for (var line = 1; line < lineTextLen.Length; line++)
        {
            int start = lineSrcStart[line];
            int end = start + lineTextLen[line];
            if (!IsSetextUnderline(blockText, start, end))
                continue;

            for (var i = start; i < end; i++)
            {
                mark[i] = true;
                content[i] = false;
            }
        }
    }

    private static bool IsSetextUnderline(string blockText, int start, int end)
    {
        int p = start;
        while (p < end && blockText[p] == ' ')
            p++;
        if (p >= end)
            return false;

        char c = blockText[p];
        if (c != '=' && c != '-')
            return false;

        while (p < end && blockText[p] == c)
            p++;
        while (p < end && (blockText[p] == ' ' || blockText[p] == '\t'))
            p++;
        return p == end; // a run of one underline char, optional surrounding whitespace, nothing else
    }

    /// <summary>
    /// The length of a line's trailing hard-line-break marker (§2.1): two-or-more trailing spaces, or a
    /// single trailing backslash. <c>0</c> when the line has no hard break. Those marker cells hide when
    /// inactive (spec: "rendered invisibly") and a <c>↵</c> affordance shows on the active line.
    /// </summary>
    private static int HardBreakMarkerLength(string text)
    {
        // A trailing backslash is a hard break only when UNESCAPED — i.e. an odd number of trailing
        // backslashes (the last one stands alone). An even count is escaped pairs (a literal `\`), no break.
        int backslashes = 0;
        for (int i = text.Length - 1; i >= 0 && text[i] == '\\'; i--)
            backslashes++;
        if (backslashes > 0)
            return backslashes % 2 == 1 ? 1 : 0;

        int spaces = 0;
        for (int i = text.Length - 1; i >= 0 && text[i] == ' '; i--)
            spaces++;
        return spaces >= 2 ? spaces : 0;
    }

    private static void MarkAtxPrefix(string blockText, int lineStart, int textLen, bool[] mark, bool[] content)
    {
        int end = lineStart + textLen;
        int p = lineStart;
        int hashes = 0;
        while (p < end && blockText[p] == '#' && hashes < 6)
        {
            p++;
            hashes++;
        }

        if (hashes == 0 || (p < end && blockText[p] != ' ' && blockText[p] != '\t'))
            return; // no ATX marker (a bare "#word" is not a heading marker; setext has no prefix)

        while (p < end && (blockText[p] == ' ' || blockText[p] == '\t'))
            p++;

        for (var i = lineStart; i < p; i++)
        {
            mark[i] = true;
            content[i] = false;
        }
    }

    // ───────────────────────────── leading structural markers ─────────────────────────────

    /// <summary>Scans a line's leading blockquote/list marker (canonical form); returns <see cref="SegKind.Content"/> when there is none.</summary>
    private static (SegKind Marker, int Start, int Length) ScanLeadingMarker(string text, BlockKind kind)
    {
        if (kind == BlockKind.Quote)
            return ScanQuoteMarker(text);
        if (kind == BlockKind.List)
            return ScanListMarker(text);
        return (SegKind.Content, 0, 0);
    }

    private static (SegKind, int, int) ScanQuoteMarker(string text)
    {
        int j = 0;
        while (j < text.Length && text[j] == ' ' && j < 3)
            j++;
        if (j >= text.Length || text[j] != '>')
            return (SegKind.Content, 0, 0);

        // Consume every nested level (`> > `) so the ▌ bar renders one per depth (§2.1).
        int end = j;
        while (end < text.Length && text[end] == '>')
        {
            end++;
            if (end < text.Length && text[end] == ' ')
                end++;
        }

        return (SegKind.QuoteMarker, j, end - j);
    }

    private static (SegKind, int, int) ScanListMarker(string text)
    {
        int j = 0;
        while (j < text.Length && text[j] == ' ' && j < 3)
            j++;
        if (j >= text.Length)
            return (SegKind.Content, 0, 0);

        char c = text[j];
        if (c is '-' or '*' or '+')
        {
            int end = j + 1;
            if (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
                return (SegKind.ListMarker, j, end + 1 - j);
            if (end == text.Length)
                return (SegKind.ListMarker, j, end - j); // a bare "-" list line
            return (SegKind.Content, 0, 0);
        }

        int k = j;
        while (k < text.Length && char.IsAsciiDigit(text[k]) && k - j < 9)
            k++;
        if (k > j && k < text.Length && (text[k] == '.' || text[k] == ')'))
        {
            int end = k + 1;
            if (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
                return (SegKind.ListMarker, j, end + 1 - j);
            if (end == text.Length)
                return (SegKind.ListMarker, j, end - j);
        }

        return (SegKind.Content, 0, 0);
    }

    // ───────────────────────────── per-line rendering ─────────────────────────────

    private static List<PieceRun> ClassifyLine(
        string text, int lineStart, bool[] mark, bool[] content, RunStyle[] style,
        SegKind marker, int markerStart, int markerLen, int hardBreakLen, bool revealed,
        out string display, out int[] srcToDisplay, out int[] displayToSrc)
    {
        int textLen = text.Length;
        int hardBreakStart = textLen - hardBreakLen; // where the trailing hard-break marker begins
        var pieces = new List<PieceRun>();
        var sb = new StringBuilder(textLen);
        var toDisplay = new int[textLen + 1];
        var toSrc = new List<int>(textLen + 1);

        int col = 0;
        while (col < textLen)
        {
            SegKind seg = SegAt(col);
            RunStyle segStyle = seg == SegKind.Content ? style[lineStart + col] : RunStyle.None;

            // Group the run: same segment kind, and — for content — same inline style, so a style
            // transition (a bare autolink inside plain text, no delimiter) starts a fresh run.
            int end = col + 1;
            while (end < textLen && SegAt(end) == seg
                && (seg != SegKind.Content || style[lineStart + end] == segStyle))
                end++;
            int len = end - col;
            int srcStart = lineStart + col;

            if (seg == SegKind.Content || (seg == SegKind.Mark && revealed)
                || (seg is SegKind.ListMarker or SegKind.QuoteMarker && revealed))
            {
                int dispStart = sb.Length;
                for (var k = 0; k < len; k++)
                {
                    toDisplay[col + k] = dispStart + k;
                    toSrc.Add(srcStart + k);
                }

                sb.Append(text, col, len);
                var kind = seg == SegKind.Content ? RunKind.Text : RunKind.RevealedMark;
                pieces.Add(new PieceRun(kind, srcStart, len, dispStart, len, Style: segStyle));
            }
            else if (seg == SegKind.Mark)
            {
                int dispPos = sb.Length; // hidden: collapses to the current display position, zero cells
                for (var k = 0; k < len; k++)
                    toDisplay[col + k] = dispPos;
                pieces.Add(new PieceRun(RunKind.HiddenMark, srcStart, len, dispPos, 0));
            }
            else // a hidden structural marker → synthetic glyph mapping to the marker source
            {
                string glyph = GlyphFor(seg, text.AsSpan(col, len));
                int dispStart = sb.Length;
                for (var k = 0; k < len; k++)
                    toDisplay[col + k] = dispStart;
                for (var k = 0; k < glyph.Length; k++)
                    toSrc.Add(srcStart); // atomic: every glyph cell maps to the marker's single stop
                sb.Append(glyph);
                pieces.Add(new PieceRun(RunKind.Synthetic, srcStart, len, dispStart, glyph.Length, Glyph: glyph));
            }

            col = end;
        }

        // The text-end display position — captured BEFORE any trailing ↵ affordance so the line-end
        // caret maps to the cell just before the ↵ (`text  |↵`), not past it.
        int textEndDisplay = sb.Length;

        // On the active line a hard break shows a trailing ↵ affordance (§2.1) — a zero-source-length
        // synthetic mapping to the line's text end, so it adds no caret stop.
        if (revealed && hardBreakLen > 0 && hardBreakStart >= 0)
        {
            sb.Append(HardBreakGlyph);
            for (var k = 0; k < HardBreakGlyph.Length; k++)
                toSrc.Add(lineStart + textLen);
            pieces.Add(new PieceRun(RunKind.Synthetic, lineStart + textLen, 0, textEndDisplay, HardBreakGlyph.Length, Glyph: HardBreakGlyph));
        }

        toDisplay[textLen] = textEndDisplay;
        toSrc.Add(lineStart + textLen); // display end → the line's text end (the terminator boundary)

        display = sb.ToString();
        srcToDisplay = toDisplay;
        displayToSrc = [.. toSrc];
        return pieces;

        SegKind SegAt(int c)
        {
            if (marker != SegKind.Content && c >= markerStart && c < markerStart + markerLen)
                return marker;
            // The trailing hard-break marker hides like any other syntax mark (spec: "invisibly").
            if (hardBreakLen > 0 && c >= hardBreakStart)
                return SegKind.Mark;
            int o = lineStart + c;
            return mark[o] && !content[o] ? SegKind.Mark : SegKind.Content;
        }
    }

    /// <summary>The hard-line-break affordance shown on the active line (§2.1).</summary>
    private const string HardBreakGlyph = "↵";

    private static string GlyphFor(SegKind seg, ReadOnlySpan<char> markerSrc)
    {
        int width = GraphemeWidth.StringWidth(markerSrc);
        if (seg == SegKind.QuoteMarker)
            return Pad(QuoteBarGlyphs(markerSrc), width); // one ▌ per nesting level

        // Ordered markers keep their numerals (meaningful); unordered become a bullet glyph.
        char first = FirstNonSpace(markerSrc);
        return char.IsAsciiDigit(first) ? markerSrc.TrimStart().ToString() : Pad("•", width); // • bullet

        static string Pad(string glyph, int width)
        {
            int gw = GraphemeWidth.StringWidth(glyph);
            return gw >= width ? glyph : glyph + new string(' ', width - gw);
        }

        static char FirstNonSpace(ReadOnlySpan<char> s)
        {
            foreach (char c in s)
                if (c != ' ')
                    return c;
            return '-';
        }
    }

    /// <summary>One <c>▌</c> bar per <c>&gt;</c> nesting level in a blockquote marker (<c>"&gt; &gt; "</c> → <c>▌▌</c>).</summary>
    private static string QuoteBarGlyphs(ReadOnlySpan<char> markerSrc)
    {
        int levels = 0;
        foreach (char c in markerSrc)
            if (c == '>')
                levels++;
        return new string('▌', Math.Max(1, levels));
    }

    // ───────────────────────────── per-row assembly ─────────────────────────────

    private static (Run[] Runs, RowCluster[] Cells) BuildRow(string display, TextLayout wrapped, int row, List<PieceRun> pieces)
    {
        int a = wrapped.LineContentStart(row);
        int b = wrapped.LineContentEnd(row);
        bool lastRow = row == wrapped.LineCount - 1;

        var runs = new List<Run>();
        var cells = new List<RowCluster>();

        foreach (var piece in pieces)
        {
            if (piece.Kind == RunKind.HiddenMark)
            {
                int dp = piece.DisplayStart;
                bool inRow = (dp >= a && dp < b) || (dp == b && lastRow);
                if (!inRow)
                    continue;

                int hc = CellsBetween(display, a, dp);
                runs.Add(new Run(piece.SrcStart, piece.SrcLen, hc, 0, RunKind.HiddenMark));
                continue;
            }

            int ps = piece.DisplayStart;
            int pe = piece.DisplayStart + piece.DisplayLen;

            if (piece.Kind == RunKind.Synthetic)
            {
                // Atomic: the whole marker belongs to the row that owns its start (degenerate
                // sub-cell wrap widths that would split a glyph are not a supported layout).
                if (ps < a || ps >= b)
                    continue;

                int scol = CellsBetween(display, a, ps);
                int swidth = CellsBetween(display, ps, Math.Min(pe, b));
                runs.Add(new Run(piece.SrcStart, piece.SrcLen, scol, swidth, RunKind.Synthetic) { Glyph = piece.Glyph });
                AppendClusters(display, ps, Math.Min(pe, b), scol, piece.SrcStart, atomic: true, RunKind.Synthetic, cells);
                continue;
            }

            int x = Math.Max(ps, a);
            int y = Math.Min(pe, b);
            if (y <= x)
                continue;

            int col = CellsBetween(display, a, x);
            int width = CellsBetween(display, x, y);
            int runSrc = piece.SrcStart + (x - ps); // Text/RevealedMark: source is contiguous and 1:1 with display
            runs.Add(new Run(runSrc, y - x, col, width, piece.Kind) { Style = piece.Style });
            AppendClusters(display, x, y, col, runSrc, atomic: false, piece.Kind, cells);
        }

        runs.Sort(static (l, r) => l.Col != r.Col ? l.Col - r.Col : l.SrcStart - r.SrcStart);
        cells.Sort(static (l, r) => l.Cell - r.Cell);
        return ([.. runs], [.. cells]);
    }

    private static void AppendClusters(
        string display, int from, int to, int cellStart, int srcStart, bool atomic, RunKind kind, List<RowCluster> cells)
    {
        int cell = cellStart;
        var enumerator = display.AsSpan(from, to - from).GetGraphemeEnumerator();
        int offset = 0;
        while (enumerator.MoveNext())
        {
            var cluster = enumerator.Current;
            int width = GraphemeWidth.ClusterWidth(cluster);
            int src = atomic ? srcStart : srcStart + offset;
            // A synthetic (atomic) cluster's source slice is its marker, not its glyph — carry the glyph
            // so a synthetic on the active line (a ↵ hard break) draws its glyph, not the source char.
            string? glyph = atomic ? cluster.ToString() : null;
            cells.Add(new RowCluster(cell, width, src, kind) { Glyph = glyph });
            cell += width;
            offset += cluster.Length;
        }
    }

    private static int CellsBetween(string display, int from, int to)
    {
        if (to <= from)
            return 0;
        return GraphemeWidth.StringWidth(display.AsSpan(from, to - from));
    }
}
