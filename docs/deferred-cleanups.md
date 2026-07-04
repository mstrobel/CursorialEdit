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
