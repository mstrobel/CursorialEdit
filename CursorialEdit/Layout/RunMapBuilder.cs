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
/// renders each line to a display string, wraps it through the probed M1
/// <see cref="CaretNavigator.Wrap"/>, and assembles the per-visual-row runs plus the clip clusters.
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
    private readonly record struct PieceRun(RunKind Kind, int SrcStart, int SrcLen, int DisplayStart, int DisplayLen);

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
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> or <paramref name="inlineRuns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lines"/> is empty.</exception>
    public static RunMap Build(
        IReadOnlyList<Line> lines,
        IReadOnlyList<InlineRun> inlineRuns,
        BlockKind kind,
        int? headingLevel = null,
        int wrapWidth = 0,
        WrapMode wrapMode = WrapMode.WordWrap,
        int? activeLine = null)
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
        var (mark, content) = ClassifyMarks(blockText, inlineRuns, kind, lineSrcStart, lineTextLen);

        var wrapped = new WrappedLine[lineCount];
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

            var pieces = ClassifyLine(
                lines[i].Text, lineSrcStart[i], mark, content,
                marker, markerStart, markerLen, revealed,
                out string display, out int[] toDisplay, out int[] toSrc);

            wrapped[i] = CaretNavigator.Wrap(display, wrapWidth, revealed ? WrapMode.NoWrap : wrapMode);
            srcToDisplay[i] = toDisplay;
            displayToSrc[i] = toSrc;
            lineFirstRow[i] = rowRuns.Count;

            for (var r = 0; r < wrapped[i].RowCount; r++)
            {
                var (runs, cells) = BuildRow(display, wrapped[i], r, pieces);
                rowRuns.Add(runs);
                rowCells.Add(cells);
                rowWidth.Add(wrapped[i].RowWidth(r));
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

    private static string BuildBlockText(IReadOnlyList<Line> lines, int sourceLength)
    {
        var sb = new StringBuilder(sourceLength);
        foreach (var line in lines)
            sb.Append(line.Text).Append(line.EndingText);
        return sb.ToString();
    }

    // ───────────────────────────── mark classification ─────────────────────────────

    /// <summary>
    /// Marks vs. content over the whole block source. Pass A flags every formatting-container and code
    /// span as a candidate mark; pass B flags the visible-text leaves (and code interiors) as content,
    /// which wins — so a container's delimiters (span minus children) and code's backtick fences remain
    /// marks while nested content shows. The ATX heading prefix is folded in as a mark.
    /// </summary>
    private static (bool[] Mark, bool[] Content) ClassifyMarks(
        string blockText, IReadOnlyList<InlineRun> inlineRuns, BlockKind kind, int[] lineSrcStart, int[] lineTextLen)
    {
        int n = blockText.Length;
        var mark = new bool[n];
        var content = new bool[n];

        foreach (var run in inlineRuns)
        {
            int s = run.SourceStart;
            int e = run.SourceStart + run.SourceLength;
            if (s < 0 || e > n || e <= s)
                continue;

            if (IsContainer(run.Kind) || run.Kind == InlineRunKind.Code)
                for (var i = s; i < e; i++)
                    mark[i] = true;
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
            MarkAtxPrefix(blockText, lineSrcStart[0], lineTextLen[0], mark, content);

        return (mark, content);
    }

    private static bool IsContainer(InlineRunKind kind) => kind is
        InlineRunKind.Emphasis or InlineRunKind.Strong or InlineRunKind.Strikethrough
        or InlineRunKind.Link or InlineRunKind.Image;

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

        int end = j + 1;
        if (end < text.Length && text[end] == ' ')
            end++;
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
        string text, int lineStart, bool[] mark, bool[] content,
        SegKind marker, int markerStart, int markerLen, bool revealed,
        out string display, out int[] srcToDisplay, out int[] displayToSrc)
    {
        int textLen = text.Length;
        var pieces = new List<PieceRun>();
        var sb = new StringBuilder(textLen);
        var toDisplay = new int[textLen + 1];
        var toSrc = new List<int>(textLen + 1);

        int col = 0;
        while (col < textLen)
        {
            SegKind seg = SegAt(col);
            int end = col + 1;
            while (end < textLen && SegAt(end) == seg)
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
                pieces.Add(new PieceRun(kind, srcStart, len, dispStart, len));
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
                pieces.Add(new PieceRun(RunKind.Synthetic, srcStart, len, dispStart, glyph.Length));
            }

            col = end;
        }

        toDisplay[textLen] = sb.Length;
        toSrc.Add(lineStart + textLen); // display end → the line's text end (the terminator boundary)

        display = sb.ToString();
        srcToDisplay = toDisplay;
        displayToSrc = [.. toSrc];
        return pieces;

        SegKind SegAt(int c)
        {
            if (marker != SegKind.Content && c >= markerStart && c < markerStart + markerLen)
                return marker;
            int o = lineStart + c;
            return mark[o] && !content[o] ? SegKind.Mark : SegKind.Content;
        }
    }

    private static string GlyphFor(SegKind seg, ReadOnlySpan<char> markerSrc)
    {
        int width = GraphemeWidth.StringWidth(markerSrc);
        if (seg == SegKind.QuoteMarker)
            return Pad("▌", width); // ▌ quote bar

        // Ordered markers keep their numerals (meaningful); unordered become a bullet glyph.
        char first = markerSrc.Length > 0 ? markerSrc[0] : '-';
        return char.IsAsciiDigit(first) ? markerSrc.ToString() : Pad("•", width); // • bullet

        static string Pad(string glyph, int width)
        {
            int gw = GraphemeWidth.StringWidth(glyph);
            return gw >= width ? glyph : glyph + new string(' ', width - gw);
        }
    }

    // ───────────────────────────── per-row assembly ─────────────────────────────

    private static (Run[] Runs, RowCluster[] Cells) BuildRow(string display, WrappedLine wrapped, int row, List<PieceRun> pieces)
    {
        int a = wrapped.RowStart(row);
        int b = wrapped.RowEnd(row);
        bool lastRow = row == wrapped.RowCount - 1;

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
                runs.Add(new Run(piece.SrcStart, piece.SrcLen, scol, swidth, RunKind.Synthetic));
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
            runs.Add(new Run(runSrc, y - x, col, width, piece.Kind));
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
            cells.Add(new RowCluster(cell, width, src, kind));
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
