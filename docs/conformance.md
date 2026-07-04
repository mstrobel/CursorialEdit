# CommonMark / GFM Conformance — CursorialEdit

> **Generated artifact.** Produced by `CursorialEdit.Tests.Conformance.ConformanceReportTests`
> (`[Trait("Category","Conformance")]`). Do not edit by hand — re-run the test to refresh.
> This is the feature-spec §2 acceptance conformance document (architecture Decision 14c).

- **Parser:** Markdig `1.3.2` (assembly `1.3.0.0`), pinned per architecture Decision 2.
- **CommonMark suite:** official `spec.json` v0.31.2 — 652 examples across 26 sections (vendored, not curated).
- **GFM/extension corpus:** 32 curated documents (no reference implementation exists for these; validated structurally + by the span oracle).
- **Span oracle (Decision 14b):** 1702 precise-span checks, **0 failing**.

## 1. Pinned pipeline (Decision 2)

| Extension | Markdig method | 1.3.2 | Feature-spec | Presentation owner |
| --- | --- | :---: | --- | --- |
| PipeTables | `UsePipeTables()` | yes | §5 / §2.2 | M3 (TablePresenter; FallbackSourcePresenter until then) |
| TaskLists | `UseTaskLists()` | yes | §2.2 | M4 (checkbox glyph; fallback renders literal until then) |
| StrikethroughEmphasis | `UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)` | yes | §2.2 | M2 (emphasis run styling) |
| AutoLinks | `UseAutoLinks()` | yes | §2.2 [EDGE] | M2 (link run styling) |
| Footnotes | `UseFootnotes()` | yes | §2.3 | M4 (footnote back-reference navigation) |
| DefinitionLists | `UseDefinitionLists()` | yes | §2.3 | M4 (definition-list presentation) |
| AlertBlocks | `UseAlertBlocks()` | yes | §2.3 (callouts) | M4 (CalloutPresenter; FallbackSourcePresenter until then) |
| Mathematics | `UseMathematics()` | yes | §2.3 [EDGE] | M4 (math presentation) |
| YamlFrontMatter | `UseYamlFrontMatter()` | yes | §2.3 | M2 (FrontMatterPresenter, §3.2 resolution 5) |
| PreciseSourceLocation | `UsePreciseSourceLocation()` | yes | architecture Decision 8/14 | M2 (run maps, reveal, find) — load-bearing everywhere |

> All ten pinned extensions exist in Markdig 1.3.2 — none had to be dropped or substituted.

## 2. CommonMark core conformance (official spec.json)

Each example is rendered through the **pinned** pipeline and compared to the CommonMark
reference HTML. A mismatch is *extension-induced* when a plain CommonMark pipeline still
matches the reference (an enabled extension deliberately reinterpreted the input — e.g. a
bare URL became an autolink, `~~x~~` became strikethrough, `$x$` became math, a `|` row
became a table); it is a *core diff* only when plain Markdig itself differs from the reference.

| Section | Examples | Pinned = ref | Extension-induced | Core diff |
| --- | ---: | ---: | ---: | ---: |
| Tabs | 11 | 9 | 0 | 2 |
| Backslash escapes | 13 | 13 | 0 | 0 |
| Entity and numeric character references | 17 | 17 | 0 | 0 |
| Precedence | 1 | 1 | 0 | 0 |
| Thematic breaks | 19 | 18 | 0 | 1 |
| ATX headings | 18 | 18 | 0 | 0 |
| Setext headings | 27 | 26 | 1 | 0 |
| Indented code blocks | 12 | 10 | 0 | 2 |
| Fenced code blocks | 29 | 29 | 0 | 0 |
| HTML blocks | 44 | 43 | 0 | 1 |
| Link reference definitions | 27 | 27 | 0 | 0 |
| Paragraphs | 8 | 8 | 0 | 0 |
| Blank lines | 1 | 1 | 0 | 0 |
| Block quotes | 25 | 25 | 0 | 0 |
| List items | 48 | 33 | 0 | 15 |
| Lists | 26 | 14 | 0 | 12 |
| Inlines | 1 | 1 | 0 | 0 |
| Code spans | 22 | 22 | 0 | 0 |
| Emphasis and strong emphasis | 132 | 132 | 0 | 0 |
| Links | 90 | 90 | 0 | 0 |
| Images | 22 | 22 | 0 | 0 |
| Autolinks | 19 | 17 | 2 | 0 |
| Raw HTML | 20 | 20 | 0 | 0 |
| Hard line breaks | 15 | 15 | 0 | 0 |
| Soft line breaks | 2 | 2 | 0 | 0 |
| Textual content | 3 | 3 | 0 | 0 |
| **Total** | **652** | **616** | **3** | **33** |

- **616/652** examples render identically to the CommonMark reference under the pinned pipeline.
- **3** differ only because an enabled extension reinterpreted the input (expected; these
  inputs exercise GFM/extension syntax the vanilla spec treats as plain text).
- **33** core diffs: plain Markdig 1.3.2 differs from the CommonMark 0.31.2 reference —
  **33 of them are whitespace-only** (Markdig serializes a loose list item as
  `<li><p>…` where the reference emits `<li>\n<p>…`; the block **AST is identical**), and
  **0 are structural**. The editor consumes the AST and precise spans, **never Markdig's
  HTML**, so whitespace-only diffs do not affect it; they are recorded here only to characterize
  the pinned Markdig against the latest spec for WP2/WP3/M4 planning.

## 3. GFM / extension coverage (curated corpus)

| Construct | Documents | Span-checked instances (all reproduced) |
| --- | ---: | ---: |
| Alert | 6 | 12 |
| AutoLink | 4 | 12 |
| DefinitionList | 2 | 7 |
| Footnote | 2 | 10 |
| FrontMatter | 2 | 2 |
| Math | 3 | 6 |
| MixedInline | 4 | 39 |
| PipeTable | 5 | 33 |
| Strikethrough | 2 | 13 |
| TaskList | 2 | 12 |

## 4. Span oracle (architecture Decision 14b)

Every construct carrying a `UsePreciseSourceLocation` span is checked: the span's source
slice must reproduce the construct it claims (structural delimiter/bracket shape + a
round-trip re-parse where re-parsing in isolation is well-defined).

| Construct | Reproduced | Checked |
| --- | ---: | ---: |
| CodeSpan | 37 | 37 |
| Emphasis | 169 | 169 |
| FootnoteReference | 3 | 3 |
| GfmAutoLink | 5 | 5 |
| HtmlEntity | 27 | 27 |
| HtmlInline | 36 | 36 |
| Image | 24 | 24 |
| Link | 49 | 49 |
| Literal | 1264 | 1264 |
| MathInline | 2 | 2 |
| PointyAutolink | 18 | 18 |
| ReferenceLink | 59 | 59 |
| Strikethrough | 3 | 3 |
| TaskListMarker | 6 | 6 |
| **All** | **1702** | **1702** |

**Verdict: clean.** Every precise span across the CommonMark + GFM corpora delimits the exact source of the construct it belongs to. Run maps, reveal-on-edit, and find can trust `document.Substring(span.Start, span.Length)` for every pinned construct.

## 5. Span-divergence catalogue (per-construct)

_None._ Markdig 1.3.2 stamps a correct precise span on every pinned construct exercised
by the corpus. If a future Markdig bump introduces a compensable span gap, it is catalogued
here (construct, example, expected vs actual, severity, owning milestone) so the oracle gate
stays green while WP2/WP3/M4 apply the recorded compensation.

