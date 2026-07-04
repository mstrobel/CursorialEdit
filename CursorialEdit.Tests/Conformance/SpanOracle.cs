using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// The span-vs-source oracle (architecture Decision 14b): parse a document through the pinned
/// pipeline and, for every construct that carries a precise <c>UsePreciseSourceLocation</c> span,
/// assert the span's source slice — <c>document.Substring(Start, Length)</c> — actually reproduces
/// the construct it claims (an emphasis span's slice is the <c>**…**</c>, a link span's slice is the
/// <c>[..](..)</c>, a code span's slice is the backticked run). This catches spans that are
/// <i>consistently</i> wrong, which the windowed-vs-full differential (M2.WP4) cannot — a shifted
/// span agrees with itself under both parses yet corrupts every run map built from it.
/// </summary>
/// <remarks>
/// <para>
/// Each inline construct is validated two ways: a <b>structural</b> check (the slice is delimited by
/// the construct's own delimiters/brackets) and, where re-parsing in isolation is well-defined, a
/// <b>round-trip</b> check (re-parsing the slice through the pinned pipeline yields the same construct
/// with matching salient identity). Reference links and task/footnote markers, whose meaning depends
/// on surrounding definitions/list context, are validated structurally only — re-parsing their slice
/// alone cannot resolve them, which is a property of markdown, not a span defect.
/// </para>
/// <para>
/// A failing check becomes a <see cref="SpanObservation"/> with <see cref="SpanObservation.Reproduces"/>
/// <see langword="false"/>; the gate test promotes those to divergences and checks them against the
/// per-construct <see cref="AcceptedSpanDivergences"/> catalogue.
/// </para>
/// </remarks>
public static class SpanOracle
{
    // ── inline construct labels ──
    public const string Emphasis = "Emphasis";
    public const string Strikethrough = "Strikethrough";
    public const string CodeSpan = "CodeSpan";
    public const string Link = "Link";
    public const string ReferenceLink = "ReferenceLink";
    public const string Image = "Image";
    public const string GfmAutoLink = "GfmAutoLink";
    public const string PointyAutolink = "PointyAutolink";
    public const string MathInlineConstruct = "MathInline";
    public const string HtmlInlineConstruct = "HtmlInline";
    public const string HtmlEntity = "HtmlEntity";
    public const string Literal = "Literal";
    public const string TaskListMarker = "TaskListMarker";
    public const string FootnoteReference = "FootnoteReference";

    // ── block construct labels (presence/coverage) ──
    public const string Heading = "Heading";
    public const string FencedCode = "FencedCode";
    public const string IndentedCode = "IndentedCode";
    public const string BlockQuote = "BlockQuote";
    public const string ListBlockConstruct = "List";
    public const string ThematicBreak = "ThematicBreak";
    public const string TableBlock = "Table";
    public const string FrontMatter = "FrontMatter";
    public const string MathBlockConstruct = "MathBlock";
    public const string FootnoteDefinition = "FootnoteDefinition";
    public const string DefinitionListConstruct = "DefinitionList";
    public const string Alert = "Alert";

    /// <summary>
    /// Inspects one corpus document and returns every precise-span check made against its inlines.
    /// </summary>
    public static IReadOnlyList<SpanObservation> Inspect(CorpusDocument doc, MarkdownPipeline pipeline)
    {
        string src = doc.Markdown;
        var observations = new List<SpanObservation>();
        var parsed = Markdown.Parse(src, pipeline);

        foreach (var node in parsed.Descendants())
        {
            if (node is Inline inline)
                InspectInline(src, doc.Id, inline, pipeline, observations);
        }

        return observations;
    }

    /// <summary>
    /// The block-level constructs present in a document (for coverage assertions and the conformance
    /// report). Not part of the strict inline gate; a lightweight in-bounds/marker sanity is recorded
    /// as observations by <see cref="InspectBlocks"/> instead.
    /// </summary>
    public static IReadOnlySet<string> PresentBlockConstructs(CorpusDocument doc, MarkdownPipeline pipeline)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Markdown.Parse(doc.Markdown, pipeline).Descendants())
            ClassifyBlock(node, label => present.Add(label));
        return present;
    }

    /// <summary>
    /// The inline constructs the oracle verified at least once in a document (for coverage assertions).
    /// </summary>
    public static IReadOnlySet<string> VerifiedInlineConstructs(CorpusDocument doc, MarkdownPipeline pipeline) =>
        Inspect(doc, pipeline)
            .Where(o => o.Reproduces)
            .Select(o => o.Construct)
            .ToHashSet(StringComparer.Ordinal);

    private static void ClassifyBlock(MarkdownObject node, Action<string> add)
    {
        // Most-derived first: AlertBlock : QuoteBlock, FencedCodeBlock : CodeBlock, MathBlock is fenced.
        switch (node)
        {
            case AlertBlock: add(Alert); break;
            case MathBlock: add(MathBlockConstruct); break;
            case YamlFrontMatterBlock: add(FrontMatter); break;
            case FencedCodeBlock: add(FencedCode); break;
            case CodeBlock: add(IndentedCode); break;
            case QuoteBlock: add(BlockQuote); break;
            case Table: add(TableBlock); break;
            case DefinitionList: add(DefinitionListConstruct); break;
            case Footnote: add(FootnoteDefinition); break;
            case ListBlock: add(ListBlockConstruct); break;
            case HeadingBlock: add(Heading); break;
            case ThematicBreakBlock: add(ThematicBreak); break;
        }
    }

    private static void InspectInline(
        string src, string docId, Inline inline, MarkdownPipeline pipeline, List<SpanObservation> obs)
    {
        switch (inline)
        {
            case EmphasisInline e:
            {
                string construct = e.DelimiterChar == '~' ? Strikethrough : Emphasis;
                if (!Resolve(src, e.Span, out string slice))
                {
                    obs.Add(Absent(docId, construct, e.Span));
                    return;
                }

                string delim = new(e.DelimiterChar, e.DelimiterCount);
                bool structural = slice.Length >= 2 * e.DelimiterCount
                    && slice.StartsWith(delim, StringComparison.Ordinal)
                    && slice.EndsWith(delim, StringComparison.Ordinal);
                bool roundTrip = structural && FirstInline<EmphasisInline>(slice, pipeline) is { } re
                    && re.DelimiterChar == e.DelimiterChar && re.DelimiterCount == e.DelimiterCount;
                obs.Add(Result(docId, construct, e.Span, slice, structural && roundTrip,
                    !structural ? $"slice not delimited by '{delim}'"
                    : !roundTrip ? "slice did not re-parse to the same emphasis run" : null));
                return;
            }

            case CodeInline c:
            {
                if (!Resolve(src, c.Span, out string slice))
                {
                    obs.Add(Absent(docId, CodeSpan, c.Span));
                    return;
                }

                string delim = new(c.Delimiter, c.DelimiterCount);
                bool structural = slice.Length >= 2 * c.DelimiterCount
                    && slice.StartsWith(delim, StringComparison.Ordinal)
                    && slice.EndsWith(delim, StringComparison.Ordinal);
                bool roundTrip = structural && FirstInline<CodeInline>(slice, pipeline) is { } re
                    && string.Equals(re.Content, c.Content, StringComparison.Ordinal);
                obs.Add(Result(docId, CodeSpan, c.Span, slice, structural && roundTrip,
                    !structural ? "slice not delimited by backtick run"
                    : !roundTrip ? "slice did not re-parse to the same code span" : null));
                return;
            }

            case AutolinkInline a:
            {
                if (!Resolve(src, a.Span, out string slice))
                {
                    obs.Add(Absent(docId, PointyAutolink, a.Span));
                    return;
                }

                bool structural = slice.StartsWith('<') && slice.EndsWith('>');
                bool roundTrip = structural && FirstInline<AutolinkInline>(slice, pipeline) is { } re
                    && string.Equals(re.Url, a.Url, StringComparison.Ordinal);
                obs.Add(Result(docId, PointyAutolink, a.Span, slice, structural && roundTrip,
                    !structural ? "slice not enclosed in <…>"
                    : !roundTrip ? "slice did not re-parse to the same autolink" : null));
                return;
            }

            case LinkInline l:
            {
                InspectLink(src, docId, l, pipeline, obs);
                return;
            }

            case MathInline m:
            {
                if (!Resolve(src, m.Span, out string slice))
                {
                    obs.Add(Absent(docId, MathInlineConstruct, m.Span));
                    return;
                }

                string delim = new(m.Delimiter, m.DelimiterCount);
                bool structural = slice.Length >= 2 * m.DelimiterCount
                    && slice.StartsWith(delim, StringComparison.Ordinal)
                    && slice.EndsWith(delim, StringComparison.Ordinal);
                bool roundTrip = structural && FirstInline<MathInline>(slice, pipeline) is not null;
                obs.Add(Result(docId, MathInlineConstruct, m.Span, slice, structural && roundTrip,
                    !structural ? $"slice not delimited by '{delim}'"
                    : !roundTrip ? "slice did not re-parse to inline math" : null));
                return;
            }

            case HtmlEntityInline ent:
            {
                if (!Resolve(src, ent.Span, out string slice))
                {
                    obs.Add(Absent(docId, HtmlEntity, ent.Span));
                    return;
                }

                string original = ent.Original.ToString();
                bool ok = string.Equals(slice, original, StringComparison.Ordinal)
                    && slice.StartsWith('&') && slice.EndsWith(';');
                obs.Add(Result(docId, HtmlEntity, ent.Span, slice, ok,
                    ok ? null : $"slice '{Trunc(slice)}' != entity source '{Trunc(original)}'"));
                return;
            }

            case HtmlInline html:
            {
                if (!Resolve(src, html.Span, out string slice))
                {
                    // A genuinely synthetic inline (empty/negative span) has no source to reproduce.
                    // A NON-empty span that overshoots the source is a real out-of-bounds precise
                    // span (it would crash WP5's Substring) — flag it, like every other construct.
                    if (IsPresentButUnresolvable(src, html.Span))
                        obs.Add(Absent(docId, HtmlInlineConstruct, html.Span));
                    return;
                }

                string tag = html.Tag ?? string.Empty;
                bool ok = string.Equals(slice, tag, StringComparison.Ordinal) && slice.StartsWith('<');
                obs.Add(Result(docId, HtmlInlineConstruct, html.Span, slice, ok,
                    ok ? null : $"slice '{Trunc(slice)}' != raw tag '{Trunc(tag)}'"));
                return;
            }

            case TaskList t:
            {
                if (!Resolve(src, t.Span, out string slice))
                {
                    obs.Add(Absent(docId, TaskListMarker, t.Span));
                    return;
                }

                bool ok = slice.Length == 3 && slice[0] == '[' && slice[2] == ']'
                    && (slice[1] == ' ' || slice[1] == 'x' || slice[1] == 'X');
                obs.Add(Result(docId, TaskListMarker, t.Span, slice, ok,
                    ok ? null : $"slice '{Trunc(slice)}' is not a '[ ]'/'[x]' marker"));
                return;
            }

            case FootnoteLink fn when !fn.IsBackLink:
            {
                if (!Resolve(src, fn.Span, out string slice))
                {
                    obs.Add(Absent(docId, FootnoteReference, fn.Span));
                    return;
                }

                bool ok = slice.StartsWith("[^", StringComparison.Ordinal) && slice.EndsWith(']');
                obs.Add(Result(docId, FootnoteReference, fn.Span, slice, ok,
                    ok ? null : $"slice '{Trunc(slice)}' is not a '[^id]' reference"));
                return;
            }

            case LiteralInline lit:
            {
                if (!Resolve(src, lit.Span, out string slice))
                {
                    // Synthetic literal (empty/negative span, e.g. inserted text) — nothing to
                    // reproduce. But a non-empty span that overshoots the source is a defective
                    // precise span (the load-bearing case for run maps) — flag it.
                    if (IsPresentButUnresolvable(src, lit.Span))
                        obs.Add(Absent(docId, Literal, lit.Span));
                    return;
                }

                string content = lit.Content.ToString();
                bool ok = ReproducesLiteral(slice, content);
                obs.Add(Result(docId, Literal, lit.Span, slice, ok,
                    ok ? null : $"slice '{Trunc(slice)}' != literal content '{Trunc(content)}'"));
                return;
            }

            // LineBreakInline and the transient DelimiterInline carry no reproducible delimiter; a
            // container inline is covered by its typed subclasses above. Nothing to assert.
        }
    }

    private static void InspectLink(
        string src, string docId, LinkInline l, MarkdownPipeline pipeline, List<SpanObservation> obs)
    {
        if (l.IsAutoLink)
        {
            if (!Resolve(src, l.Span, out string autoSlice))
            {
                obs.Add(Absent(docId, GfmAutoLink, l.Span));
                return;
            }

            bool structural = autoSlice.Length > 0
                && autoSlice[0] is not ('[' or '<' or '!');
            bool roundTrip = structural && FirstInline<LinkInline>(autoSlice, pipeline) is { IsAutoLink: true };
            obs.Add(Result(docId, GfmAutoLink, l.Span, autoSlice, structural && roundTrip,
                !structural ? "bare-URL slice unexpectedly bracketed"
                : !roundTrip ? "slice did not re-parse to a bare-URL autolink" : null));
            return;
        }

        string construct = l.IsImage ? Image : Link;
        if (!Resolve(src, l.Span, out string slice))
        {
            obs.Add(Absent(docId, construct, l.Span));
            return;
        }

        // Classify by terminal delimiter, not by an interior "](" (which an inner image inside a
        // reference link's text — [![img](x)][ref] — would otherwise trip): inline links close with
        // ')', reference/shortcut links close with ']'.
        string open = l.IsImage ? "![" : "[";
        bool isInlineForm = slice.EndsWith(')') && slice.Contains("](", StringComparison.Ordinal);
        if (isInlineForm)
        {
            bool structural = slice.StartsWith(open, StringComparison.Ordinal) && slice.EndsWith(')');
            bool roundTrip = structural && FirstInline<LinkInline>(slice, pipeline) is { } re
                && re.IsImage == l.IsImage && string.Equals(re.Url, l.Url, StringComparison.Ordinal);
            obs.Add(Result(docId, construct, l.Span, slice, structural && roundTrip,
                !structural ? $"inline link slice not shaped '{open}..(..)'"
                : !roundTrip ? "slice did not re-parse to the same link" : null));
            return;
        }

        // Reference/shortcut form: [text][ref], [text][], or [text]. Its resolution depends on a
        // definition elsewhere, so it is validated structurally only (re-parsing in isolation would
        // — correctly — not resolve it). Images keep the Image label; text links use ReferenceLink.
        string refConstruct = l.IsImage ? Image : ReferenceLink;
        bool refStructural = slice.StartsWith(open, StringComparison.Ordinal) && slice.EndsWith(']');
        obs.Add(Result(docId, refConstruct, l.Span, slice, refStructural,
            refStructural ? null : $"reference link slice not shaped '{open}..]'"));
    }

    // CommonMark backslash-escapable ASCII punctuation.
    private const string Escapable = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>
    /// Whether a literal's source slice reproduces its (rendered) content. Markdig normalizes literal
    /// content for rendering — it strips a table cell's inter-pipe padding and resolves backslash
    /// escapes — while the <c>Span</c> keeps pointing at the full <i>source</i> region (which is
    /// exactly what a run map needs). So the slice reproduces the literal when, after undoing that
    /// normalization (unescape, then trim the whitespace cell/paragraph stripping removes), the two
    /// agree. A genuinely shifted span points at unrelated source and still fails.
    /// </summary>
    private static bool ReproducesLiteral(string slice, string content)
    {
        if (string.Equals(slice, content, StringComparison.Ordinal))
            return true;

        string unescaped = Unescape(slice);
        if (string.Equals(unescaped, content, StringComparison.Ordinal))
            return true;

        if (content.Length == 0)
            return slice.Trim().Length == 0;

        return string.Equals(unescaped.Trim(), content.Trim(), StringComparison.Ordinal);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
            return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && Escapable.IndexOf(s[i + 1]) >= 0)
            {
                sb.Append(s[i + 1]);
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    private static bool Resolve(string src, SourceSpan span, out string slice)
    {
        if (span.IsEmpty || span.Start < 0 || span.Length <= 0 || span.Start + span.Length > src.Length)
        {
            slice = string.Empty;
            return false;
        }

        slice = src.Substring(span.Start, span.Length);
        return true;
    }

    /// <summary>
    /// A span that CLAIMS a source range (non-empty, non-negative start and length) but does not fit
    /// the source — the out-of-bounds precise-span defect that would crash a <c>Substring</c> consumer.
    /// Distinguished from a genuinely synthetic span (empty/negative), which is legitimately skipped.
    /// </summary>
    private static bool IsPresentButUnresolvable(string src, SourceSpan span) =>
        !span.IsEmpty && span.Start >= 0 && span.Length > 0 && span.Start + span.Length > src.Length;

    private static T? FirstInline<T>(string text, MarkdownPipeline pipeline) where T : Inline =>
        Markdown.Parse(text, pipeline).Descendants().OfType<T>().FirstOrDefault();

    private static SpanObservation Result(
        string docId, string construct, SourceSpan span, string slice, bool ok, string? reason) =>
        new(docId, construct, span.Start, span.Length, slice, ok, ok ? null : reason);

    private static SpanObservation Absent(string docId, string construct, SourceSpan span) =>
        new(docId, construct, span.Start, span.Length, string.Empty, false,
            "absent or out-of-bounds precise span");

    private static string Trunc(string s) =>
        s.Length <= 40 ? s : s[..37] + "...";
}
