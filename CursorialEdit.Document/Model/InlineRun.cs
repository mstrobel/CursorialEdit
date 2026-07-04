namespace CursorialEdit.Document.Model;

/// <summary>
/// The inline flavour of an <see cref="InlineRun"/> — the pinned inline construct set projected from
/// the Markdig inline AST. WP5 turns these lazily-realized runs into the per-visual-row layout run
/// map (Decision 8); WP2 only needs a faithful, lazily-computed projection to prove Decision 5's
/// lazy-inline discipline and to feed find/outline queries.
/// </summary>
public enum InlineRunKind
{
    /// <summary>Ordinary literal text.</summary>
    Text = 0,

    /// <summary>Emphasis (<c>*…*</c> / <c>_…_</c>).</summary>
    Emphasis,

    /// <summary>Strong emphasis (<c>**…**</c> / <c>__…__</c>).</summary>
    Strong,

    /// <summary>GFM strikethrough (<c>~~…~~</c>).</summary>
    Strikethrough,

    /// <summary>An inline code span (<c>`…`</c>).</summary>
    Code,

    /// <summary>An inline or reference link (<c>[text](url)</c> / <c>[text][ref]</c>).</summary>
    Link,

    /// <summary>An autolink (bare-URL GFM or pointy <c>&lt;url&gt;</c>).</summary>
    AutoLink,

    /// <summary>An image (<c>![alt](url)</c>).</summary>
    Image,

    /// <summary>Inline mathematics (<c>$…$</c>).</summary>
    Math,

    /// <summary>Raw inline HTML or an HTML entity.</summary>
    Html,

    /// <summary>A hard or soft line break.</summary>
    LineBreak,

    /// <summary>A GFM task-list checkbox marker (<c>[ ]</c>/<c>[x]</c>).</summary>
    TaskMarker,

    /// <summary>A footnote reference (<c>[^id]</c>).</summary>
    FootnoteReference,

    /// <summary>Any other inline node carrying a precise span.</summary>
    Other,
}

/// <summary>
/// One lazily-realized inline run of a <see cref="Block"/> (Decision 5). Offsets are
/// <b>block-relative</b> (Decision 8): measured from the block's source origin
/// (<see cref="Block.SourceStartOffset"/>) rather than the document, so a block that survives an
/// edit unchanged keeps valid runs no matter how far its absolute position shifted — the consumer
/// re-absolutizes with the block's current start when it needs document coordinates.
/// </summary>
/// <remarks>
/// This is a faithful projection of Markdig's already-parsed inline tree, so nested constructs
/// (a literal inside an emphasis run) surface as overlapping runs in document order. WP5's
/// <c>RunMapBuilder</c> flattens these into the non-overlapping per-visual-row map with hidden-mark
/// and synthetic runs; WP2 keeps the projection deliberately thin.
/// </remarks>
/// <param name="SourceStart">Block-relative UTF-16 start of the run (<c>absolute − <see cref="Block.SourceStartOffset"/></c>).</param>
/// <param name="SourceLength">Length of the run in UTF-16 code units.</param>
/// <param name="Kind">The inline construct this run projects.</param>
public readonly record struct InlineRun(int SourceStart, int SourceLength, InlineRunKind Kind);
