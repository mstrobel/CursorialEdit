namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// The hand-curated GFM/pinned-extension corpus — the constructs the official CommonMark core suite
/// (<c>corpus/commonmark-spec.json</c>) does not exercise: pipe tables, task lists, strikethrough,
/// bare-URL autolinks, footnotes, definition lists, GitHub alerts, math, and YAML front matter (the
/// feature-spec §2.2/§2.3 set), plus a few rich mixed-inline documents that stress the span oracle.
/// Each document is tagged with the pinned <see cref="Parsing.MarkdownExtension"/> it targets via its
/// <see cref="CorpusDocument.Construct"/> label.
/// </summary>
/// <remarks>
/// Curated, not official: there is no reference-implementation HTML for these extensions, so the
/// conformance report validates them structurally (the pinned pipeline must produce the expected AST
/// node and precise spans) rather than by HTML string match. Vendor an official GFM suite here if one
/// becomes fetchable.
/// </remarks>
public static class GfmExtensionCorpus
{
    /// <summary>The curated documents, one construct family per contiguous group.</summary>
    public static IReadOnlyList<CorpusDocument> Documents { get; } =
    [
        // ───────────────────────── pipe tables (§2.2 / §5) ─────────────────────────
        new("gfm-table-basic", "§2.2 Tables", "PipeTable",
            """
            | Fruit | Qty |
            | ----- | --- |
            | Apple | 3   |
            | Pear  | 12  |
            """,
            CorpusSource.CuratedGfm),
        new("gfm-table-align", "§2.2 Tables", "PipeTable",
            """
            | Left | Center | Right |
            | :--- | :----: | ----: |
            | a    | b      | c     |
            """,
            CorpusSource.CuratedGfm),
        new("gfm-table-inline", "§2.2 Tables", "PipeTable",
            """
            | Name        | Note             |
            | ----------- | ---------------- |
            | **bold**    | see `code`       |
            | [link](/x)  | ~~struck~~ text  |
            """,
            CorpusSource.CuratedGfm),
        new("gfm-table-escaped-pipe", "§2.2 Tables", "PipeTable",
            """
            | Expression | Result |
            | ---------- | ------ |
            | a \| b     | or     |
            """,
            CorpusSource.CuratedGfm),
        new("gfm-table-cjk", "§2.2 Tables", "PipeTable",
            """
            | 名前 | 数量 |
            | ---- | ---- |
            | 林檎 | 三   |
            """,
            CorpusSource.CuratedGfm),

        // ───────────────────────── task lists (§2.2) ─────────────────────────
        new("gfm-tasklist-basic", "§2.2 Task lists", "TaskList",
            """
            - [ ] draft the spec
            - [x] restore the package
            - [ ] wire the pipeline
            """,
            CorpusSource.CuratedGfm),
        new("gfm-tasklist-nested", "§2.2 Task lists", "TaskList",
            """
            - [x] parent done
                - [ ] child pending
                - [x] child done
            """,
            CorpusSource.CuratedGfm),

        // ───────────────────────── strikethrough (§2.2) ─────────────────────────
        new("gfm-strikethrough-basic", "§2.2 Strikethrough", "Strikethrough",
            "This was ~~a mistake~~ corrected.",
            CorpusSource.CuratedGfm),
        new("gfm-strikethrough-inline", "§2.2 Strikethrough", "Strikethrough",
            "Mix ~~struck~~ with **bold** and `code` in one line.",
            CorpusSource.CuratedGfm),

        // ───────────────────────── autolinks (§2.2 [EDGE]) ─────────────────────────
        new("gfm-autolink-http", "§2.2 Autolinks", "AutoLink",
            "See https://example.com/path?q=1 for details.",
            CorpusSource.CuratedGfm),
        new("gfm-autolink-www", "§2.2 Autolinks", "AutoLink",
            "Visit www.example.org today.",
            CorpusSource.CuratedGfm),
        new("gfm-autolink-email", "§2.2 Autolinks", "AutoLink",
            "Contact user@example.com for access.",
            CorpusSource.CuratedGfm),
        new("gfm-autolink-angle", "§2.2 Autolinks", "AutoLink",
            "Core pointy autolink <https://example.net> stays supported.",
            CorpusSource.CuratedGfm),

        // ───────────────────────── footnotes (§2.3) ─────────────────────────
        new("ext-footnote-basic", "§2.3 Footnotes", "Footnote",
            """
            Here is a footnote reference.[^1]

            [^1]: This is the footnote definition.
            """,
            CorpusSource.CuratedGfm),
        new("ext-footnote-multiple", "§2.3 Footnotes", "Footnote",
            """
            First claim[^a] and second claim[^b].

            [^a]: Supporting detail A.
            [^b]: Supporting detail B.
            """,
            CorpusSource.CuratedGfm),

        // ───────────────────────── definition lists (§2.3) ─────────────────────────
        new("ext-deflist-basic", "§2.3 Definition lists", "DefinitionList",
            """
            Apple
            :   A pomaceous fruit.

            Orange
            :   A citrus fruit.
            """,
            CorpusSource.CuratedGfm),
        new("ext-deflist-multi", "§2.3 Definition lists", "DefinitionList",
            """
            Term
            :   First definition.
            :   Second definition.
            """,
            CorpusSource.CuratedGfm),

        // ───────────────────────── GitHub alerts / callouts (§2.3) ─────────────────────────
        new("ext-alert-note", "§2.3 Alerts", "Alert",
            """
            > [!NOTE]
            > Highlights information users should take into account.
            """,
            CorpusSource.CuratedGfm),
        new("ext-alert-tip", "§2.3 Alerts", "Alert",
            """
            > [!TIP]
            > Optional advice for doing things better.
            """,
            CorpusSource.CuratedGfm),
        new("ext-alert-warning", "§2.3 Alerts", "Alert",
            """
            > [!WARNING]
            > Critical content demanding attention.
            """,
            CorpusSource.CuratedGfm),
        new("ext-alert-important", "§2.3 Alerts", "Alert",
            """
            > [!IMPORTANT]
            > Key information users need to know.
            """,
            CorpusSource.CuratedGfm),
        new("ext-alert-caution", "§2.3 Alerts", "Alert",
            """
            > [!CAUTION]
            > Negative potential consequences of an action.
            """,
            CorpusSource.CuratedGfm),
        new("ext-alert-multiline", "§2.3 Alerts", "Alert",
            """
            > [!NOTE]
            > First line of the note.
            > Second line with **emphasis** and `code`.
            """,
            CorpusSource.CuratedGfm),

        // ───────────────────────── mathematics (§2.3 [EDGE]) ─────────────────────────
        new("ext-math-inline", "§2.3 Math", "Math",
            "The identity $a^2 + b^2 = c^2$ is Pythagorean.",
            CorpusSource.CuratedGfm),
        new("ext-math-block", "§2.3 Math", "Math",
            """
            $$
            \int_0^1 x^2 \, dx = \frac{1}{3}
            $$
            """,
            CorpusSource.CuratedGfm),
        new("ext-math-nospace", "§2.3 Math", "Math",
            "Inline $x+y$ renders but a bare 5$ and $6 price does not.",
            CorpusSource.CuratedGfm),

        // ───────────────────────── YAML front matter (§2.3) ─────────────────────────
        new("ext-frontmatter-basic", "§2.3 Front matter", "FrontMatter",
            """
            ---
            title: Example Document
            tags: [markdown, editor]
            ---

            Body paragraph after the front matter.
            """,
            CorpusSource.CuratedGfm),
        new("ext-frontmatter-nested", "§2.3 Front matter", "FrontMatter",
            """
            ---
            author:
              name: Ada
              handle: "@ada"
            draft: false
            ---

            Content.
            """,
            CorpusSource.CuratedGfm),

        // ───────────────────────── mixed inline (span-oracle stress) ─────────────────────────
        new("mix-inline-emphasis-code-link", "§2.1 Mixed inline", "MixedInline",
            "A *slanted* word, some `code`, a [labelled link](https://ex.com \"t\"), and https://bare.example done.",
            CorpusSource.CuratedGfm),
        new("mix-nested-emphasis", "§2.1 Mixed inline", "MixedInline",
            "Outer **bold with *italic* inside** and a trailing __strong__ word.",
            CorpusSource.CuratedGfm),
        new("mix-escapes-entities", "§2.1 Mixed inline", "MixedInline",
            "Escaped \\* star, entity &amp; amp, numeric &#42; and raw <span>html</span> here.",
            CorpusSource.CuratedGfm),
        new("mix-image-and-refs", "§2.1 Mixed inline", "MixedInline",
            """
            An inline ![alt text](/img.png "title") and a [reference link][ref].

            [ref]: https://example.com/reference
            """,
            CorpusSource.CuratedGfm),
    ];
}
