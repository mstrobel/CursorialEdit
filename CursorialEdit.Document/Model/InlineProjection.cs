using Markdig.Syntax.Inlines;

namespace CursorialEdit.Document.Model;

/// <summary>
/// The shared Markdig-inline → <see cref="InlineRun"/> projection (Decision 5): the single classifier
/// both <see cref="Block.RealizeInlineRuns"/> (whole-block inlines) and
/// <see cref="TableModel.CellInlineRuns"/> (per-cell inlines) run, so the two never drift on which
/// construct maps to which kind. It stays in the Document project with Markdig — the projection's output
/// (block/cell-relative <see cref="InlineRun"/> spans) is plain data the app consumes without ever naming
/// a Markdig type (<c>ArchitectureTests</c>).
/// </summary>
internal static class InlineProjection
{
    /// <summary>
    /// Projects <paramref name="inlines"/> (a leaf's inline tree, or a container's descendant inlines)
    /// into origin-relative <see cref="InlineRun"/>s, dropping any span outside <c>[0, sourceLength)</c>
    /// (a Markdig precise-span quirk, or a document-global construct surfacing a foreign inline) exactly
    /// as <see cref="Block.RealizeInlineRuns"/> does.
    /// </summary>
    /// <param name="inlines">The Markdig inline nodes to classify.</param>
    /// <param name="origin">The absolute UTF-16 offset the returned spans are made relative to.</param>
    /// <param name="sourceLength">The length of the origin's serialized source — runs outside it are dropped.</param>
    public static List<InlineRun> Project(IEnumerable<Inline> inlines, int origin, int sourceLength)
    {
        var runs = new List<InlineRun>();
        foreach (var inline in inlines)
        {
            var span = inline.Span;
            if (span.IsEmpty || span.Length <= 0)
                continue;

            int start = span.Start - origin;
            if (start < 0 || start + span.Length > sourceLength)
                continue;

            if (Classify(inline) is { } kind)
                runs.Add(new InlineRun(start, span.Length, kind));
        }

        return runs;
    }

    /// <summary>The pinned inline-construct classification (§2.1) — <see langword="null"/> for delimiter runs and transient parse scaffolding, which carry no reproducible run.</summary>
    public static InlineRunKind? Classify(Inline inline) => inline switch
    {
        DelimiterInline => null,
        LiteralInline => InlineRunKind.Text,
        EmphasisInline e => e.DelimiterChar == '~' ? InlineRunKind.Strikethrough
            : e.DelimiterCount >= 2 ? InlineRunKind.Strong
            : InlineRunKind.Emphasis,
        CodeInline => InlineRunKind.Code,
        LinkInline { IsAutoLink: true } => InlineRunKind.AutoLink,
        LinkInline { IsImage: true } => InlineRunKind.Image,
        LinkInline => InlineRunKind.Link,
        AutolinkInline => InlineRunKind.AutoLink,
        Markdig.Extensions.Mathematics.MathInline => InlineRunKind.Math,
        Markdig.Extensions.TaskLists.TaskList => InlineRunKind.TaskMarker,
        Markdig.Extensions.Footnotes.FootnoteLink { IsBackLink: false } => InlineRunKind.FootnoteReference,
        HtmlInline or HtmlEntityInline => InlineRunKind.Html,
        LineBreakInline => InlineRunKind.LineBreak,
        _ => InlineRunKind.Other,
    };
}
