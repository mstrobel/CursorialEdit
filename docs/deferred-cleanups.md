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

## WP8+WP9 review — deferred to WP11 (selection-tier rendering)

Two confirmed selection-highlight findings share one root cause and one correct fix — composing the
selection into the per-cell/run DrawText (the M1 PlainTextPresenter model), NOT a background pre-pass.
The paint-order approaches both fail: painting the scrim BEFORE the rows is overwritten by a run's opaque
background; painting it AFTER with a full-opacity SelectionBrush hides the glyph (PaintRectangle only
"shows through" at partial opacity). This is a deliberate presenter-render refactor, and it lands naturally
in **WP11 (caps + theme layer)**, which owns selection-tier rendering and the NoColor degradation:
- **Selection over inline `code` shows a hole** — a `RunStyle.Code` run paints an opaque `CodeFillBrush`
  background in RenderRows AFTER the selection scrim, so selected inline-code cells show the grey code fill
  instead of the highlight. (Code BLOCKS are fine — their fill is a pre-pass under the scrim.) Fix: draw a
  selected run with the selection brush as its cell background (split the run at the selection boundary).
- **NoColor selection is invisible** — PaintSelection has no NoColor branch; M1 falls back to
  `TextAttributes.Inverse`. Applying Inverse needs it in the selected cells' DrawText cellStyle (a scrim
  Inverse is overwritten by the glyph draw), i.e. the same compose-into-draw refactor. WP11 owns the caps
  tiers, so the NoColor selection test lands there with the caps-tier harness.

## WP8+WP9 review — deferred cleanups (perf/dedup, non-behavioral)

- **Plain-surface word motion issues per-character run-map queries** (DocumentCaret) instead of reusing the
  existing O(n) `CaretNavigator.NextWord`/`PrevWord`; `MoveWordLeft` recomputes `PrevVisibleStop` twice per
  char. Reuse the tested word logic on the plain path. (Findings 4, 5.)
- **`TryCell`/`Visible` re-derive the `(blockIndex, map, rel)` triple `LocateCaret` already computes** — the
  mapping now lives in three copies; unify. (Finding 6.)
- **`MarkdownEditingHarness` is a near-verbatim copy of `EditingHarness`** differing only in producer/bridge
  types; factor the shared driver. (Finding 7.)
- **`PaintSelection` re-derives the valid-active-line guard `RenderRows` already computed.** (Finding 8.)
