# Deferred cleanups

Non-bug quality findings surfaced by the review cycles, deferred to a batched cleanup pass (they are
duplication / efficiency / minor design, not correctness). Grouped by area; each is safe to leave until a
dedicated pass or until the named milestone touches the area anyway.

## Caret / layout (DocumentCaret, EditorControl) — several are FB-1 tax

- **DocumentCaret.MoveWordRight/MoveWordLeft** re-implement the intra-line word-run scan + cluster pin
  that `CaretNavigator.NextWord`/`PrevWord` (probe-verified against `TextBox`) already provide. Collapses
  when FB-1 lands (or by delegating now). *(wave4/5 candidate)*
- **DocumentCaret.GetSelection** duplicates the clamped-intersection math of the private `Intersect`
  helper. Fold to one helper.
- **EditorControl.PublishCaret** duplicates `OnCaretUpdated`'s `_publishing`-guarded
  `VisualDocumentPosition` read block (copy-paste with slight variation).
- **DocumentCaret.LocateCaret's per-read defensive `Snap`** + **EditorControl's `_publishing`
  re-entrancy guard** are two special cases compensating for the same root cause (the owner republishes
  the caret mid-splice, before the caret's own epilogue). A single "defer caret publication until the
  edit epilogue" mechanism would retire both. *(design — revisit in M2 when the parser drives republish)*
- **AfterStateChange walks every realized presenter** on every caret state change even when no selection
  is or was involved. Band-bounded (cheap), but a "no selection now and none last pass → skip the walk"
  fast-path is trivial.

## Clipboard / editing efficiency

- **EditorControl Cut serializes the selection twice** — once via `caret.SelectedText()` for the
  clipboard, then again inside `DeleteSelection` → the buffer slice. Capture once, reuse.

## Rendering

- **PlainTextPresenter.DrawSelectedRow** resolves `ThemeKeys.SelectionBrush` via `TryFindResource` once
  per selected row instead of once per `Render` pass. Hoist to the pass.

## Shell

- **EditorShell's Ctrl+S chord match** inlines the same letter-normalization test
  (`char.ToLowerInvariant(...) == 's'`) that `EditorControl.IsLetter`-style helpers already express.
  Share one helper.
- **Autosave teardown** (`TearDownDocumentServices` queues the old service's journal delete then disposes
  it; `AttachDocumentServices` immediately creates the new service): assessed as *not a bug in practice*
  (old delete and new writes target different journal files for different documents; the same-file reopen
  window is cleared by the near-instant delete before the 5 s debounce). Worth a note if M6's recovery
  work makes reopen-same-file common — then await the queued delete in `Dispose`.

## Tests

- **DirtyStateTests.Type** re-implements the append-through-the-controller helper that already exists as
  `AutosaveHarness.Edit`; **MissingStartupFile_…** inlines ~14 lines duplicating `Harness.Create`.
  Consolidate the shell-test setup helpers.

## Minor UX (assessed low-severity, deferred)

- **Opening a new document does not reset the scroll offset to top** (WireDocument→AttachDocument): after
  opening a second large file, the viewport can retain the prior scroll row with the origin caret
  off-screen until the first caret move calls `EnsureVisible`. A scroll-to-top on attach fixes it.
  *(candidate EditorControl.cs:220 — confirm/fix when M6 wires real open UX)*

## M2 parse foundation (WP1/WP2 review — perf cleanups, not correctness)

Deferred from the WP1+WP2 review (the two correctness bugs — content-hash identity migration and the
span-oracle literal/HTML blind spot — were fixed in the review-fix commit; these are efficiency/hygiene):

- **MarkdigBlockProducer residue pass** — `UnmatchedCount` rescans both match arrays to their end on
  each kind-mismatch step (O(oldWindow·newWindow)); two running counters make it linear.
- **MarkdigBlockProducer hashes each window block 2–3×/edit** (pass-1 match, `CreateBlock`, and the
  Reused/Changed check) — cache the per-segment hash in an array indexed by j; reuse `CreateBlock`'s.
- **`CreateBlock` iterates a block's lines twice** (`MaxVersion` then `HashLines`) — fuse into one walk.
- **ConformanceReporter re-runs `SpanOracle.Inspect` over every GFM doc** already inspected by RunOracle
  — pass the observations through instead of re-parsing.
- **`ConformanceReportTests` writes `docs/conformance.md` into the working tree as a test side effect** —
  acceptable (the doc is a checked-in generated artifact) but gate the write behind an env flag or move
  to a `[Trait("Category","Conformance")]`-only path so a plain `dotnet test` doesn't dirty the tree.

## Benchmark robustness (recurring flake) — ✅ RESOLVED in WP13

`TypingLatencyBenchmark` used to trip its 50 ms hard ceiling on a single outlier keystroke under
back-to-back full-suite load (p50 ~4–8 ms; one keystroke blipped ~55 ms from GC/JIT/scheduling). Fixed
in M2.WP13: the hard ceiling now gates **p90** (the sustained worst keystroke), not the single max, with
a separate loose CATASTROPHIC max ceiling (250 ms) that still fails a genuine hang. Verified stable across
back-to-back full-suite runs.

## WP7a review — deferred cleanups (perf/dead-code, non-behavioral)

- **`CodeBlockPresenter` re-tokenizes every code line on every Render** (no per-line memoization). On the
  edit hot path a code block re-scans all its lines each frame. Cache tokenization per (line, text) —
  invalidate on `SetContent`. (Review finding 6.)
- **`RunMapBuilder.AppendClusters` allocates a per-cluster glyph string (`cluster.ToString()`) for every
  atomic synthetic cluster**, but `RowCluster.Glyph` is only read on the active clipped row (where the
  sole synthetic is the ↵). Inactive bullets/quote-bars allocate dead strings. Defer the glyph
  materialization to the active-row path. (Review finding 8.)
- **`RunMapBuilder.GlyphFor`'s `FirstNonSpace` helper + `markerSrc.TrimStart()` are dead** — the marker
  slice always starts at a non-space. Remove the no-op defensiveness. (Review finding 9.)

## WP7b review — deferred cleanup

- **`MarkdownViewBridge.GetCaretMap` rebuilds a full `RunMap` on every caret query for an out-of-band
  block** (a block not in the realized render band, e.g. after Ctrl+End into a long document), with no
  caching — each of VisualDocumentPosition / LocateCaret / MoveVertical re-runs `RunMapBuilder.Build`
  over the block's lines+inlines. Cache the last out-of-band (BlockId, width) → RunMap. (Review finding 4.)

## WP8+WP9 review — selection-tier rendering — ✅ RESOLVED in WP11b (de3ab56)

Both selection-highlight findings (inline-code/code-block hole; NoColor invisibility) were fixed in
M2.WP11b by composing the selection into the per-cell/run DrawText (the M1 PlainTextPresenter model,
via `LeafBlockPresenter.DrawSelectableText`/`DrawSelectedSpan`) instead of a background pre-pass — the
selected sub-span draws with the SelectionBrush as its own background (so a run's opaque code fill can't
punch a hole) and on the NoColor tier with `TextAttributes.Inverse` composed into the cell style. Cleanup
finding 8 below (`PaintSelection` re-derives the active-line guard) is also moot — `PaintSelection` was
deleted. Kept here as a record.

## WP8+WP9 review — deferred cleanups (perf/dedup, non-behavioral)

- **Plain-surface word motion issues per-character run-map queries** (DocumentCaret) instead of reusing the
  existing O(n) `CaretNavigator.NextWord`/`PrevWord`; `MoveWordLeft` recomputes `PrevVisibleStop` twice per
  char. Reuse the tested word logic on the plain path. (Findings 4, 5.)
- **`TryCell`/`Visible` re-derive the `(blockIndex, map, rel)` triple `LocateCaret` already computes** — the
  mapping now lives in three copies; unify. (Finding 6.)
- **`MarkdownEditingHarness` is a near-verbatim copy of `EditingHarness`** differing only in producer/bridge
  types; factor the shared driver. (Finding 7.)
- **`PaintSelection` re-derives the valid-active-line guard `RenderRows` already computed.** (Finding 8.)

## WP10 review — deferred cleanups (raw-path dedup, non-behavioral)

- **RawMarkdownHighlighter re-implements markdown marker grammar** (list/quote/ATX/fence/thematic-break)
  that RunMapBuilder + CodeBlockPresenter already own — and the copies already disagree. Unify onto one
  grammar source. (Review finding 4.)
- **RunMapBuilder.BuildRaw is a near-verbatim clone of Build's row-assembly body** (finding 5) and
  **allocates full-length mark/content/style arrays that are always default** just to satisfy ClassifyLine
  (finding 6). Parameterize the shared builder with a "raw/identity" mode instead of cloning.
- **RawSourcePresenter.RenderRows re-implements the base row loop + CodeBlockPresenter's token-overdraw
  cursor** (finding 7). Factor the shared overdraw helper.

## WP11a review — deferred: hard-break ↵ through the §18.4 glyph seam

- The front-matter fold chevrons now resolve through `EditorGlyph` (the §18.4 imperative Icon analogue),
  but the hard-break **↵** affordance still draws the hardcoded `RunMapBuilder.HardBreakGlyph = "↵"`.
  It's emitted deep in the reviewed `RunMapBuilder` layout hot path, which has no ambient `UIApplication`
  to resolve the capability ladder against. Deferred — it resolves to the same Unicode floor "↵" today,
  so nothing is lost; wiring it needs a glyph-resolution seam threaded into the layout builder (or a
  post-layout glyph substitution), which is a larger change to reviewed code.

## WP11b review — deferred cleanup

- **The verbatim selection path duplicates the map-based one.** `SelectedCellsForVerbatimLine` +
  `SlideSelection` + `LineSourceStart` compute a line's selected cell interval from `Lines[i].Text`
  directly, while the run-map path (`SelectedCells`) does the same via `map.Locate`. The verbatim
  presenters (Raw/FrontMatter/Fallback) all have a run map available, so they could route through the
  single map-based path instead of the parallel verbatim one. Low-value DRY (both are correct + tested);
  unify if the two ever drift. (Review finding 5, PLAUSIBLE.)
  (Finding 4 — DrawSelectableText enumerating clusters for a fully-unselected run — is minor and left as-is:
  the early `!_selectionActive || selection.IsEmpty` guard already covers the common no-selection frame.)

## M3 spike (WP1-3) review — deferred to their owning WPs

R3 is retired (both pinned AND growing-column typing measured in budget; stable-geometry asserts exactly-1
row-zone). Two fixes applied in the review commit (table height estimate; the per-cell width alloc). The
rest are geometry-mismatch items that route the table off the old 1:1 source-line path and are properly
built by the later table WPs — a half-fix now would be throwaway:

- **#1 Caret hit-test map mismatch (→ WP4/WP9).** `TablePresenter` inherits the source-line `MapForWidth`,
  but the rendered geometry is the taller grid, so clicking/arrowing into a table mis-places the caret. The
  composite cell-caret map is built by WP4 (`TableEditingController` cell focus) + WP9 (navigation
  integration) — the very next table wave. Until then, caret-into-table is imprecise (regression from the
  fallback's 1:1). **First thing WP4/WP9 must wire.**
- **#6 Empty-cell source offset → 0 (→ WP4, FIRST task).** A blank/ragged `CellSpan.Empty` has `Start=0`, so
  an empty cell's fragment maps to the block origin. Latent in the render-only spike (empty fragments are
  skipped at draw), but WP4's cell splice + WP9 caret land wrong on an empty cell. The correct fix is the
  empty-cell insertion position, which is WP4's cell-source-range logic (and must not hand-parse pipes —
  risk d). **WP4 must fix this before typing into empty cells.**
- **#2 Document selection not painted over a table (→ WP8).** `TableRowPresenter` draws cells with no
  `SelectionProvider` consultation, so a document selection spanning a table shows no highlight on it (the
  fallback composed it). Full table selection is the cell-rect model in WP8; the document-selection overlay
  lands there.
- **#4 Inline markdown in cells renders literal (→ table polish). ✅ RESOLVED (2026-07-06).** Cells now render
  WYSIWYG per Decision 9's per-cell reveal: an inactive cell shows its `**bold**`/`` `code` ``/`*italic*`/
  `~~strike~~`/`[text](url)` FORMATTED (marks hidden, content styled like the prose paragraph path) and the
  active cell (caret in it) reveals its RAW markdown, its row reflowing since the marks-hidden display is
  narrower. `TableModel.CellInlineRuns` projects cell inlines via the shared `InlineProjection` (Markdig stays
  in the Document project); a per-cell `CellFormat` mirrors `RunMapBuilder.ClassifyMarks`/`ClassifyLine`;
  `LayoutRow` gained an `activeColumn` param; `TableRowPresenter`/`TableCaretMap` draw + map the styled runs;
  and a plain cell renders byte-identically (raw path, no projection). See architecture.md Decision 9 / §2.5 and
  `TableInlineFormatTests`.
- **#7 Reconcile re-Refreshes every row each keystroke (→ WP5).** Even stable-geometry edits run
  `LayoutRow`+run-map+signature for all N rows (only the invalidation is 1-zone). In budget today (R3
  passes), but WP5's live-reflow is where "skip unchanged rows" + the incremental column recompute land —
  the growing-column full re-raster (20 zones, measured in budget) is WP5's optimization target.
- **#10 `WrapCell` duplicates `CaretNavigator.Wrap(CharacterWrap)` (→ cleanup).** The grapheme-boundary
  char-wrap already exists in the layout code; route through it to shrink the risk-a surface, once verified
  to produce identical fragments.

## WP5 review deferrals (2026-07-05, commit after 0015662)
- **TableRowPresenter reimplements LeafBlockPresenter's selection-draw seam** (finding 4). DrawCellText/ResolveSelection/CellSelection duplicate DrawSelectableText/DrawSelectedSpan/ResolveSelection — TableRowPresenter is a UIElement (not a LeafBlockPresenter), so it can't reach the protected members and copies them. Drift risk. → Reconcile in **WP8** (cell-rect selection reworks this draw path anyway); the right fix is to lift the shared grapheme prefix/selected/suffix split into a helper both can call.
- **TablePresenter.LineStarts duplicates TableModel.LineStartOffsets** (finding 5). Verbatim, but cross-project (Presenters vs Document, LineStartOffsets is private static) — dedup needs a public/internal accessor or InternalsVisibleTo. Low value; revisit if a third copy appears.

## Viewport-layout review deferral (2026-07-05, after e8826d6)
- **Double word-wrap on cold-start table realize** (review finding 4). When a table realizes before the first OnViewportChanged (_wrapWidth==0), TablePresenter's ctor builds every row's wrapped layout at the fallback width, then the first MeasureOverride's ApplyViewportWidths re-derives them at the real width — each cell wrapped twice on the initial pass. Perf only (one-time per table, not per-keystroke). Fix would defer the ctor's row build until the first measure; deferred to avoid ctor-restructuring risk. Revisit if table realize cost shows up.

## Cell-inline review deferrals (2026-07-06, after the inline-formatting fixes)
The 3 CONFIRMED correctness findings were fixed (empty-display fallback, End-key content-end, Style in the render signature — mutation-checked). Deferred, non-blocking:
- **#4 Truncate non-active cell click-box (PLAUSIBLE).** A formatted+truncated over-wide cell's caret stop covers only its visible prefix, not a column-clamped click-box — a click in the blank area right of the … *may* fall through to a neighbor cell. Edge case (Truncate mode only); verify + clamp the click-box to the column if it reproduces.
- **#5 WrapCellFormatted/TruncateCellFormatted duplicate WrapCell/TruncateCell.** Near-verbatim copies differing only in raw vs display+styled-runs; unify to one parametrized helper so the raw and formatted caret-mappings can't drift. Refactor risk (touches the caret map) — do with a fresh caret-map test pass.
- **#6 `_cellRuns` retained for the model's lifetime, consumed only by the test-only `CellInlineRuns` accessor** (production reads `_formats`). Make the accessor compute on demand, or drop it.
- **#7 `CellFormat._contentLength` assigned but never read; #8 `TableModel.Format(int,int)` accessor has no callers.** Remove both (trivial dead surface).
