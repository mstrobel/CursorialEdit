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

## Benchmark robustness (recurring flake)

- **`TypingLatencyBenchmark` trips its 50 ms hard ceiling on a single outlier under back-to-back
  full-suite load** (p50 stays ~4–8 ms; one keystroke spikes ~54–56 ms from GC/JIT/scheduling under
  parallel test pressure). Passes 5/5 isolated. The `max < 50 ms` assertion is inherently load-fragile.
  Fix options: assert the hard ceiling on p99 (not max), warm more aggressively, or mark the benchmark
  `[Trait("Category","Benchmark")]` and run it serialized/excluded from the parallel default run (per
  §5.7 lanes). Not a product regression — a test-harness robustness item.

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
