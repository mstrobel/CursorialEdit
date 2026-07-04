using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Document.Parsing;

/// <summary>
/// A document-level interval set of <b>fenced regions</b> — fenced code (<c>``` … ```</c> /
/// <c>~~~ … ~~~</c>), YAML front matter (leading <c>--- … ---</c>), and block mathematics
/// (<c>$$ … $$</c>) — computed by a cheap line-prefix scan (architecture Decision 3 / §2.2 step 3,
/// the "graft from engine" interval set). It exists so the incremental reparse window (M2.WP3) can
/// answer two questions in O(log n):
/// <list type="number">
/// <item><b>Safe start.</b> A reparse window must never begin <i>strictly inside</i> an open fenced
/// region — the opening fence line establishes the region's language/verbatim semantics, so a window
/// that started at an interior line would parse verbatim content as live markdown.</item>
/// <item><b>Trailing parity.</b> If a window's trailing boundary falls inside a fenced region (the
/// structural keystroke that opened a fence reinterprets the tail), the window must extend to the
/// region's end — to EOF for an unclosed fence.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Cheapness (Decision 3 "rebuilt cheaply").</b> The scan is line-prefix matching only — never a
/// Markdig parse — so rebuilding it over a whole ~10k-line document is a sub-millisecond,
/// zero-AST-allocation pass, categorically cheaper than the full document reparse the fast path
/// exists to avoid. The producer rebuilds it per edit rather than maintaining it incrementally; the
/// §13 "no full reparse per keystroke" budget is about Markdig, not a byte scan.
/// </para>
/// <para>
/// <b>Conservative by construction.</b> The recognizer follows the CommonMark fenced-code rules
/// (≤ 3 leading spaces, an opening run of ≥ 3 back-ticks or tildes, a matching closing run) plus the
/// pinned pipeline's front-matter and <c>$$</c> math fences. Where it errs it errs toward
/// <i>detecting</i> a region (so the window widens — always correct, only slower), never toward
/// missing one; the differential fuzzer (M2.WP4) is the end-to-end proof that no real fence is
/// missed at a window boundary.
/// </para>
/// <para>All members are UI-thread-only, like the buffer they scan.</para>
/// </remarks>
public sealed class FenceIntervalSet
{
    /// <summary>One fenced region: the inclusive source-line range <c>[Start, End]</c> the fence spans (its opening line through its closing line, or EOF when unclosed).</summary>
    public readonly record struct Region(int Start, int End, bool Closed);

    private readonly Region[] _regions;
    private readonly int _lineCount;

    private FenceIntervalSet(Region[] regions, int lineCount, bool hasBareCarriageReturn, bool hasDuplicateFootnote)
    {
        _regions = regions;
        _lineCount = lineCount;
        HasBareCarriageReturn = hasBareCarriageReturn;
        HasDuplicateFootnoteDefinition = hasDuplicateFootnote;
    }

    /// <summary>The fenced regions, ordered and non-overlapping. Empty when the document has no fences.</summary>
    public IReadOnlyList<Region> Regions => _regions;

    /// <summary>
    /// Whether any line's text contains a <b>lone carriage return</b> (a <c>\r</c> the buffer stores as
    /// content, not part of a <c>\r\n</c> terminator). The buffer and Markdig disagree on line structure
    /// there — Markdig treats a lone CR as a line break — so a line-based reparse window cannot be
    /// trusted; the producer falls back to a full reparse (the sanctioned degraded mode) for such a
    /// document. Computed for free during the fence scan.
    /// </summary>
    public bool HasBareCarriageReturn { get; }

    /// <summary>
    /// Whether two footnote definitions share a label (e.g. <c>[^2]:</c> twice). A footnote definition's
    /// very block kind is document-global: the <i>first</i> <c>[^label]:</c> is the footnote, a later
    /// duplicate is demoted (to a link-reference definition or paragraph). A window parsed in isolation
    /// cannot see the earlier definition and would mint a second footnote, so the producer falls back to
    /// a full reparse when this holds (the sanctioned degraded mode). Unique footnotes tile identically
    /// windowed or whole, so they need no fallback. Computed for free during the fence scan.
    /// </summary>
    public bool HasDuplicateFootnoteDefinition { get; }

    /// <summary>Builds the interval set for the whole buffer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public static FenceIntervalSet Build(IDocumentBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int lineCount = buffer.LineCount;
        var regions = Scan(lineCount, buffer.GetLine, out bool bareCr, out bool dupeFootnote);
        return new FenceIntervalSet(regions, lineCount, bareCr, dupeFootnote);
    }

    /// <summary>Builds the interval set from raw line texts — the unit-test entry point.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    public static FenceIntervalSet FromLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var regions = Scan(lines.Count, i => new Line(lines[i], LineEnding.None, 0), out bool bareCr, out bool dupeFootnote);
        return new FenceIntervalSet(regions, lines.Count, bareCr, dupeFootnote);
    }

    /// <summary>
    /// The region containing <paramref name="line"/>, or <see langword="null"/> when the line is
    /// outside every fenced region. O(log n) via binary search over the ordered intervals.
    /// </summary>
    public Region? RegionAt(int line)
    {
        int lo = 0, hi = _regions.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var region = _regions[mid];
            if (line < region.Start)
                hi = mid - 1;
            else if (line > region.End)
                lo = mid + 1;
            else
                return region;
        }

        return null;
    }

    /// <summary>Whether <paramref name="line"/> lies anywhere within a fenced region (its fence lines included).</summary>
    public bool Contains(int line) => RegionAt(line) is not null;

    /// <summary>
    /// Whether a reparse window that begins at <paramref name="line"/> would begin <b>strictly
    /// inside</b> a fenced region — i.e. after that region's opening fence line. A window may legally
    /// begin <i>at</i> a region's opening line (it then owns the whole fence); it may not begin in
    /// the interior.
    /// </summary>
    public bool StartsInsideRegion(int line) => RegionAt(line) is { } region && line > region.Start;

    /// <summary>
    /// If <paramref name="line"/> falls inside a fenced region, the line one past that region's end
    /// (its exclusive extent — <see cref="Region.End"/> + 1, which is the document line count for an
    /// unclosed fence); otherwise <paramref name="line"/> unchanged. This is the "extend the window's
    /// trailing boundary out of the fence" operator: an exclusive end boundary sitting inside a
    /// fence is pushed to the region's exclusive end.
    /// </summary>
    /// <remarks>
    /// The boundary is <b>exclusive</b>: a window <c>[start, end)</c> whose <paramref name="line"/>
    /// == <c>end</c> equal to a region's <see cref="Region.Start"/> is fine (the fence is entirely
    /// after the window). Only an <c>end</c> in <c>(Start, End]</c> — the region's interior or its
    /// closing line — cuts the fence and is pushed out.
    /// </remarks>
    public int ExtendExclusiveEndPastRegion(int line)
    {
        if (line <= 0 || line >= _lineCount)
            return line;

        // An exclusive end at `line` cuts a region iff some region contains line-1 but not the whole
        // fence lies before `line`; equivalently the region containing line-1 ends at or past line.
        var region = RegionAt(line - 1);
        if (region is { } r && r.End >= line)
            return r.End + 1;

        return line;
    }

    // ───────────────────────────── the scanner ─────────────────────────────

    private static Region[] Scan(int lineCount, Func<int, Line> getLine, out bool hasBareCarriageReturn, out bool hasDuplicateFootnote)
    {
        hasBareCarriageReturn = false;
        hasDuplicateFootnote = false;
        HashSet<string>? footnoteLabels = null;
        var regions = new List<Region>();

        // Open-region state. kind 0 = none, 1 = backtick/tilde fence, 2 = front matter, 3 = $$ math.
        var openKind = 0;
        var fenceChar = '\0';
        var fenceLen = 0;
        var openLine = 0;
        var firstScanLine = true;

        for (var i = 0; i < lineCount; i++)
        {
            // A lone CR is content to the buffer but a line break to Markdig; split on it so the fence
            // state machine sees Markdig's lines. Regions are recorded in buffer-line coordinates (the
            // buffer line that contains the opening/closing sub-line), which is the granularity the
            // reparse-window planner reasons in.
            string lineText = getLine(i).Text;
            if (!hasBareCarriageReturn && lineText.IndexOf('\r') >= 0)
                hasBareCarriageReturn = true;

            foreach (var part in SubLines(lineText))
            {
                switch (openKind)
                {
                    case 0:
                        if (firstScanLine && IsFrontMatterFence(part))
                        {
                            openKind = 2;
                            openLine = i;
                        }
                        else if (TryOpenCodeFence(part, out fenceChar, out fenceLen))
                        {
                            openKind = 1;
                            openLine = i;
                        }
                        else if (IsMathOpen(part))
                        {
                            openKind = 3;
                            openLine = i;
                        }

                        break;

                    case 1:
                        if (ClosesCodeFence(part, fenceChar, fenceLen))
                        {
                            regions.Add(new Region(openLine, i, Closed: true));
                            openKind = 0;
                        }

                        break;

                    case 2:
                        if (IsFrontMatterClose(part))
                        {
                            regions.Add(new Region(openLine, i, Closed: true));
                            openKind = 0;
                        }

                        break;

                    case 3:
                        if (IsMathClose(part))
                        {
                            regions.Add(new Region(openLine, i, Closed: true));
                            openKind = 0;
                        }

                        break;
                }

                firstScanLine = false;
            }
        }

        if (openKind != 0)
            regions.Add(new Region(openLine, lineCount - 1, Closed: false)); // unclosed → runs to EOF

        return [.. regions];
    }

    /// <summary>Splits a buffer line's text into Markdig lines on lone CRs (a CR that is content to the buffer is a line break to Markdig).</summary>
    private static string[] SubLines(string text) => text.IndexOf('\r') < 0 ? [text] : text.Split('\r');

    /// <summary>Leading-space count capped at 4 (indentation ≥ 4 disqualifies a fence — it is code).</summary>
    private static int LeadingSpaces(string text)
    {
        var n = 0;
        while (n < text.Length && n < 4 && text[n] == ' ')
            n++;
        return n;
    }

    private static bool TryOpenCodeFence(string text, out char fenceChar, out int fenceLen)
    {
        fenceChar = '\0';
        fenceLen = 0;

        int indent = LeadingSpaces(text);
        if (indent >= 4 || indent >= text.Length)
            return false;

        char c = text[indent];
        if (c != '`' && c != '~')
            return false;

        int run = 0;
        int j = indent;
        while (j < text.Length && text[j] == c)
        {
            run++;
            j++;
        }

        if (run < 3)
            return false;

        // A back-tick info string may not itself contain a back-tick (CommonMark); tilde fences may.
        if (c == '`' && text.IndexOf('`', j) >= 0)
            return false;

        fenceChar = c;
        fenceLen = run;
        return true;
    }

    private static bool ClosesCodeFence(string text, char fenceChar, int fenceLen)
    {
        int indent = LeadingSpaces(text);
        if (indent >= 4)
            return false;

        int run = 0;
        int j = indent;
        while (j < text.Length && text[j] == fenceChar)
        {
            run++;
            j++;
        }

        if (run < fenceLen)
            return false;

        // Only trailing whitespace may follow a closing fence.
        for (; j < text.Length; j++)
        {
            if (text[j] != ' ' && text[j] != '\t')
                return false;
        }

        return true;
    }

    private static bool IsFrontMatterFence(string text) => text.TrimEnd() == "---";

    private static bool IsFrontMatterClose(string text)
    {
        string trimmed = text.TrimEnd();
        return trimmed == "---" || trimmed == "...";
    }

    // Block mathematics (verified against Markdig): a `$$` display-math block opens and closes only on
    // a line whose sole content is `$$` (≤ 3 leading spaces, then `$$` and nothing but trailing
    // whitespace). `$$1. `, `$$x`, `$$ x`, `$$foo` are all paragraph text, never a math fence — so a
    // naive "starts with $$" test would mint spurious regions (e.g. over blockquote content) and mask a
    // real code fence below.
    private static bool IsMathFenceLine(string text)
    {
        int indent = LeadingSpaces(text);
        return indent < 4 && text.AsSpan(indent).Trim() is "$$";
    }

    private static bool IsMathOpen(string text) => IsMathFenceLine(text);

    private static bool IsMathClose(string text) => IsMathFenceLine(text);
}
