# CursorialEdit v1.0 — Implementation Plan

**Status: APPROVED by the maintainer (Mike), 2026-07-03 — binding for implementation.** Assembled
2026-07-03 from the seven milestone plans; three-lens review (spec coverage / sequencing / framework-fit)
applied 2026-07-03. Findings and their resolutions are in Appendix B; the spec-acceptance coverage matrix
is Appendix A. The M2.WP5 caret-visibility invariant + clip-edge indicators were added post-review at the
maintainer's direction.
Provenance: the [feature spec](markdown-editor-v1-feature-spec.md) is product truth; the
[architecture](architecture.md) is **ratified** (Decisions 1–14 binding — this plan implements them and
does not relitigate); the [integration notes](cursorial-integration-notes.md) are the verified framework
picture at Cursorial HEAD `ca9a528` (= NuGet 0.3.1); [framework-feedback.md](framework-feedback.md)
(FB-1..FB-17) and the [icon ledger](icon-ledger.md) are living companions this plan schedules against.

## 1. How to read this plan

- §2 fixes the repo/solution layout and the build/reference strategy — decided here, binding.
- §3 is the dependency DAG plus the resolutions of every place two milestone planners claimed the same
  work; those resolutions are binding on the milestone sections.
- §4 is the **framework configuration workstream** (FB-17, accepted by Mike 2026-07-03) — two framework
  deliverables running parallel to the editor milestones, with the editor as first consumer.
- §5 states the cross-cutting engineering practices once; they bind every milestone (harness conventions,
  CI gates, benchmark cadence, feedback loop, dogfooding, demo checkpoints).
- §6–§12 are the milestones M1–M7. Each has: objective (and which architecture risks it retires), a
  **mechanically checkable exit gate** (named tests, thresholds, or checked-in artifacts — the *only*
  completion criteria), ordered work packages (WPn), framework work items, milestone-specific tests and
  risks, and a demo checkpoint. §n references are feature-spec sections; "Decision n" is architecture §1.
- A milestone is *done* when every gate checkbox is green in CI — the per-PR lane **plus** the latest
  green nightly-integration lane run (§5.7) — or its named artifact is committed, and the demo checkpoint
  has been run. Demo checkpoints are deliberately *not* gate checkboxes (§5.6): gates are mechanical, the
  demo clause here covers the rest.

**Definition of done for v1.0** (spec §16, verbatim): *every **[IN]** acceptance checkbox in §2–§13
passes; the editor can open, edit, and save the framework's own documentation (including at least one doc
with tables, callouts, footnotes, and code blocks) without data loss, and it dogfoods its own docs.*

---

## 2. Repo layout, projects, and the build/reference strategy

### 2.1 Solution layout (decided)

All app work lives in `/Users/mike.strobel/Workspace/CursorialEdit` (`CursorialEdit.slnx`); all framework
work lives in `/Users/mike.strobel/Workspace/Cursorial`. Four projects:

| Project | Role | May reference |
|---|---|---|
| **`CursorialEdit/`** (exists) | App executable: bootstrap/shell (`App/`), surface (`Views/` — `EditorControl`, `DocumentPanel`, `EditorView`, `SplitViewHost`), `Presenters/`, `Layout/` (run maps, `CaretNavigator`), `Input/` (keymap), `Commands/` (`CommandRegistry`, `TableCommands`), `Chrome/` (toolbar/menu/ribbon/Backstage), `Controls/` (`TableSizePicker`, `LanguagePicker`), `Dialogs/` (editor-specific Link/Image dialogs), `Icons/`, `Themes/` (`MdTheme.xaml`), `Images/` (`IImageBackend`, inertness tracker), `Find/`+`Outline/` (views), `State/` (`AppStatePaths`, `RecentFilesStore`, `AutosaveService`), `Options/` | Cursorial.UI.Bars / UI.Xaml / UI.Xaml.Generator; `CursorialEdit.Document`; `CursorialEdit.Dialogs`. **No direct Markdig reference** — Markdig types reach app code only through the Document project's surface (presenters may traverse `Block`'s Markdig AST refs; that is app code, never promotable code). |
| **`CursorialEdit.Document/`** (new, M1) | The parser/document core the app and tests share: `Buffer/` (`DocumentBuffer`, `Line`, `TextPosition`, `AnchorTable`), `Editing/` (`EditController` + undo, `TableEditingController`, `InlineFormatter`, `BlockFormatter`, `ListRenumberer`, `SmartTyping`, `TypingShortcuts`, `PasteController`), `Parsing/` (pipeline factory, `ReparseWindowPlanner`, `FastPathGate`, `FenceIntervalSet`, `LinkRefTable`, `FootnoteTable`, `FullReparseScheduler`, `SaveNormalizer`), `Model/` (`BlockList`, `Block`, `TableModel`), `Find/` (`FindModel`, `ReplaceController`), `Outline/` (`OutlineModel`), `Persistence/` (`DocumentFile`, `RecoveryJournal`), `Export/` (`ExportService`) | **The only project that declares the Markdig 1.3.2 PackageReference (pinned).** Cursorial.UI for `UITimer`/`UIDispatcher` — and, transitively, Cursorial.Core (itself a published package; `Cursorial.Core/Text/`) for `GraphemeWidth`/`GraphemeEnumerator` — no XAML, no Bars, no controls. |
| **`CursorialEdit.Dialogs/`** (new, M1) | The promotable FB-12 suite: `MessageBox` (M1), `ITaskDialogService` (M1), `TaskDialog` (M6), `OpenFileDialog`/`SaveFileDialog` + `IDialogFileSystem` (M6). Themed via the `ThemeKeys` spine. Post-v1 promotion to a `Cursorial.UI.Dialogs` extensions package is a mechanical move — kept true structurally. | Cursorial.UI + UI.Xaml (+ Generator) **only**. Never Markdig, never the app or Document projects. |
| **`CursorialEdit.Tests/`** (new, M1) | One xunit project for all tiers: unit, UITestHost, fuzz/oracle, benchmarks, conformance, integration — separated by trait (`Category=Benchmark`, `Category=Integration` (PTY/signals, opt-in), `Category=Conformance`; fuzz iteration counts scale via env var for nightly). | ProjectReferences to all three editor projects **plus** ProjectReferences into `~/Workspace/Cursorial` for `Cursorial.UI.Testing`, `Cursorial.UI`, `Cursorial.UI.Bars`, `Cursorial.UI.Xaml`, `Cursorial.UI.Themes` — the verified project-over-package mix (integration notes §9): NuGet unifies to a single `Cursorial.UI` assembly identity, and IVT is assembly-name-keyed, so no source copies, no drift. |

Markdig hygiene is enforced from M1 by two standing tests:
`ArchitectureTests.OnlyDocumentProjectDeclaresMarkdig` and
`ArchitectureTests.DialogsProject_HasNoMarkdigOrEditorReference` (assembly-reference assertions).
`TableSizePicker` and the dialog XAML must additionally expose no Markdig types in their APIs (they are
Markdig-free by review + test even though they live beside app code).

### 2.2 Build/reference strategy (decided)

- **Default: package mode.** App, Document, and Dialogs reference the published Cursorial 0.3.1 packages
  (API-identical to HEAD `ca9a528` — verified). The test project is **always** in project mode (above).
- **One documented switch:** `Directory.Build.props` defines `$(UseLocalCursorial)`. When `true`
  (`dotnet build -p:UseLocalCursorial=true`, or committed in the props file), every Cursorial
  `PackageReference` in the three editor projects flips to a `ProjectReference` into
  `/Users/mike.strobel/Workspace/Cursorial`. This is the sanctioned way to consume in-flight framework
  changes (FB-1 promotion, FB-2, FB-13, FB-14.x, FB-17). There is no second mechanism.
- **CI builds both configurations** while they differ. When a framework change the editor depends on
  merges, commit `UseLocalCursorial=true` in the props file until the next NuGet cut, then revert and
  update the FB status to `upstreamed`.
- **Framework contribution flow:** (1) hit friction → log/annotate the FB item in
  `docs/framework-feedback.md`; (2) branch in the Cursorial repo, implement **with framework-side tests**
  (house patterns: `Cursorial.UI.Bars.Tests` for FB-2, `Cursorial.Drawing.Tests` for FB-13/FB-10, input
  interpreter tests for FB-14.x); (3) the editor consumes via `$(UseLocalCursorial)`; (4) package release
  → revert to package mode. The Cursorial checkout moves from the `ca9a528` baseline only deliberately,
  per merged FB — never incidentally.

---

## 3. Dependency DAG and cross-milestone resolutions

### 3.1 The DAG

The app critical path is strictly sequential — each milestone consumes the previous one's machinery:

```
M1 ──► M2 ──► M3 ──► M4 ──► M5 ──► M6 ──► M7          (app critical path)

FW-A  (FB-17 Stage A: options store + CapabilityOverrides + caps-emoji)
      runs ∥ M1–M2 ──► consumed by M2 (caps override gate), M5 (View.Surface), M7 (capability page)
FW-B  (FB-17 Stage B: Options dialog + first-run wizard + width-ruler tester)
      runs ∥ M6 (after FB-12 TaskDialog vocabulary lands, M6.WP1) ──► consumed by M7 (§15 pages)

FB-1 probe (M1.WP6) ──► decides M2 run-map wrap/word-motion path (upstream vs app classifier)
FB-12 seam (M1.WP10: MessageBox + ITaskDialogService) ──► M3/M4 prompts ──► M6 TaskDialog impl swap
FB-14.1 (M2, Cursorial repo) + FB-14.2/14.3 (M4, Cursorial repo) ──► keymap coverage
      (legacy-safe alternates ship app-side regardless — never blocking)
FB-2  BarMenuItem (M5.WP1, Cursorial repo, first in M5) ──► M5 menu bar
FB-13 + FB-10 (M7.WP0, Cursorial repo, first in M7) ──► M7 image display (feature-flagged)
FB-16/N1 filed once in M1 ──► band convention pressed with data in M3/M5/M7
```

Only two framework items sit on the app path, both mitigated: FB-2 → M5 menu bar (toolbar half proceeds
in parallel; interim plain `MenuItem` behind the same `CommandRegistry`), and FB-13 → M7 image display
(feature-flagged; all non-image M7 work proceeds). FW-A/FW-B slippage never blocks a milestone — each
consumer has a named fallback (§4.3).

### 3.2 Double-ownership resolutions (binding on §6–§12)

1. **Table size picker (M3 vs M5).** M3 builds `TableSizePicker` + the `InsertTable` command, hosted in a
   minimal `Window`/`ShowDialogAsync` popup so §5 acceptance 1 is testable in M3. M5 re-hosts the same
   control in the toolbar/ribbon Insert split-button. No logic moves.
2. **Markdown-typing shortcuts (M2 vs M4).** M2 owns block-start recognition as a *parse reflex* — a
   verification package (`TypingShortcutTests`) proving `## `, `- `, `> `, `1. `, ``` ``` ```, `- [ ] `
   produce the right blocks through live reparse. M4 owns everything *behavioral*: commands, list
   continuation, empty-item termination, auto-pairing, `---`+Enter rule insertion, checkbox toggling,
   ordered renumbering.
3. **TaskDialog (M1 seam vs M6 build).** M1 ports Gallery `MessageBox` into `CursorialEdit.Dialogs` and
   defines `ITaskDialogService` (main instruction / content / buttons / verification checkbox). M3's
   paste-to-table offer and M4's Open-replace prompt call the *service*. M6 builds the full `TaskDialog`
   and swaps the implementation — callers unchanged. (M4's plan said "M1's TaskDialog"; normalized: M4
   consumes `ITaskDialogService`, MessageBox-backed until M6.)
4. **Command registry (M3/M4/M5).** One class: `CursorialEdit/Commands/CommandRegistry.cs`. M3
   contributes `TableCommands` (`BarCommand` defs, Text + `InputGestureText` only); M4 *creates* the
   registry with the full §6/§7 inventory plus the FB-14 keymap (M4's "EditorCommandRegistry" and M5's
   "CommandRegistry" are the same artifact); M5 enriches it — icons, `Description`/SuperTips, checkable
   state + `CommandStateSync`, and M6-owned commands as `CanExecute => false` stubs behind a
   `CommandRegistry.Attach(name, ICommand)` seam that M6 fills.
5. **FrontMatterPresenter (M2 vs M4).** Built once in M2.WP7 (dim style, folded, expand affordance);
   M4 adds only the fold-toggle command wiring and §2.3 conformance coverage.
6. **Autosave (M1 stub vs M6 service).** M1 ships `AutosaveJournal` (5 s debounce, atomic write, no
   recovery prompt). M6.WP0 re-proves atomicity under kill-fuzz, and M6.WP4 replaces the stub with
   `AutosaveService`/`RecoveryJournal` + the recovery prompt. Write-temp-then-rename mechanics carry
   forward unchanged.
7. **RecentFilesStore (M5 vs M6).** Created in M5 (Backstage Recent needs it) against the M1
   `IAppStatePathProvider` seam; M6.WP3 finalizes location, path canonicalization, MRU cap 15, pruning.
8. **Config persistence (spec §17 Q6, re-scoped by FB-17).** *User settings* live in the framework
   options store (`~/.cursorial/`, global + per-app tri-state overlay — §4): the editor writes
   `View.Surface` (M5) and the full §15 set (M7) through it. *Journals and recents are app state, not
   config*: `AppStatePaths` → `$XDG_STATE_HOME/cursorialedit` (macOS
   `~/Library/Application Support/CursorialEdit`, Windows `%LOCALAPPDATA%\CursorialEdit`), journal temp
   files created in the target directory so rename never crosses volumes. M5's `AppConfig` JSON stub
   exists only as the fallback if FW-A slips, migrated on landing. Q6 is thereby answered; recorded for
   maintainer sign-off in the M6 report.
9. **M7 Options re-scope (per FB-17 acceptance).** M7 *consumes* the framework Options dialog (FW-B) and
   contributes the editor's §15 pages as app-registered pages; capability overrides go through the
   first-class `CapabilityOverrides` seam (FW-A / FB-5 proper), not an app-side override service. The
   integration-notes §7 `OnCapabilitiesChanged` recipe is **fallback only**.
10. **Conformance document.** One generator, owned by the Decision-14 harness: created M2
    (`docs/conformance.md`, CommonMark/GFM core), extended M4 (§2.2/§2.3 constructs), shipped final M7.
11. **FB-16/N1 filing.** Filed once, in M1, with R5 spike traces; M3 (500-row table), M5, and M7 (storm
    benchmarks) press the same issue with data — no re-filing.
12. **`EditorView` factoring.** The composite (EditorControl + DocumentPanel + presenters) is factored as
    an instantiable `EditorView` from M2 onward, so M7's split view is a second instantiation over the
    same `DocumentBuffer`/`BlockList`/`EditController`/undo stack — not a refactor. **Owned by M2.WP7**
    (explicit scope + done-when there); M7.WP2 only instantiates.
13. **`EditorIcons` factory (M4 vs M5).** M4.WP2 *creates* `Icons/EditorIcons.cs` containing only the
    ledger rows its content presenters need (task-checkbox, callout types, footnote markers —
    ledger-pinned `Glyph` + `Text`); M5.WP2 *extends* the same factory with the command/toolbar rows and
    pins the remaining codepoints. One owner; no re-sourcing pass in M5.

### 3.3 Terminology normalization

Across all milestone sections: **presenters** (never "renderers"; M4's `Rendering/` folder is
`Presenters/`); `caps-*` root classes and `Md.*` theme tokens per architecture §2.3; the **run map** is
`Run(int SrcStart, int SrcLen /*block-relative*/, int Col, int Width, RunKind)` with
`RunKind ∈ {Text, HiddenMark, RevealedMark, Synthetic}` (Decision 8); the **band** is viewport + 2K,
K = max(viewportRows, 8), until FB-16 makes it contractual; edits are `EditController.Apply(Edit(Start,
Removed, Inserted), EditKind)` — the single pipeline (Decision on architecture §2.1); parser output is
`BlockListChange { Reused, Changed, Added, Removed, LineShift }`; the **reference doc** is the pinned
10k-line benchmark fixture created in M1 and reused verbatim by every later benchmark.

---

## 4. Framework configuration workstream (FB-17 — new since the milestone plans were drafted)

Mike accepted FB-17 (2026-07-03): the framework user-configuration layer is **implemented as part of this
project, in the Cursorial repo**, staged as two deliverables with the editor as first consumer. This
workstream runs parallel to the editor milestones per the DAG (§3.1); all code and tests land framework-
side (per §2.2's contribution flow), and the editor consumes via `$(UseLocalCursorial)`.

**Capacity model (stated so durations read honestly):** FW-A/FW-B interleave with editor work on the
*same* implementing capacity — the "parallel" lanes describe dependency structure, not extra staffing.
Milestone durations must be read as *including* their concurrent framework items (this also covers the
in-milestone framework contributions: FB-14.x, FB-2, FB-13/FB-10). The named fallbacks (§4.3) exist
precisely so an overloaded stretch sheds framework work, never editor gates.

### 4.1 Stage A — storage + override seam (no UI), parallel with M1/M2

Scope (framework repo):
- **Options store under `~/.cursorial/`** — global options + per-app overlay with **tri-state semantics**
  (set / unset / inherit): a per-app value overlays global only when explicitly set, so an app writing its
  file never snapshots-and-freezes global defaults (FB-17 review note 2).
- **Builder wiring** — `UIApplicationBuilder.WithUserConfiguration(...)`, with app-identity override
  (entry-assembly name as default; overridable so renames don't orphan configs — review note 4).
- **First-class `CapabilityOverrides` seam** (FB-5 proper): per-axis forced on/off/auto folded into
  capability stamping, `UIApplication.Capabilities` and root `caps-*` classes kept in sync, overrides
  **surviving `RenegotiateAsync` by design** (review note 7) — same semantics `RequestedColorTier`/
  `NerdFontAvailable` already have.
- **`caps-emoji` registration** (FB-15): the new class + the Icon element's fourth tier
  (Glyph → Image → **Emoji** → Text), user-declared opt-in, default absent; Icon measures at its
  *resolved* tier's width (emoji = 2 cells).

**Exit gate (framework tests in the Cursorial repo):**
- [ ] Options-store round-trip tests: global vs per-app tri-state overlay proven (set overrides, unset
      inherits, deleting a per-app key re-exposes global).
- [ ] Builder wiring + app-id override tests.
- [ ] `CapabilityOverrideTests`: per-axis force off/on/auto restamps `caps-*` **and** the
      `Capabilities` snapshot coherently; overrides survive `RenegotiateAsync`.
- [ ] `caps-emoji` stamps only when opted in; Icon resolves the four-tier ladder and measures 2 cells at
      the emoji tier.
- [ ] Framework-side override re-render fixture: forcing a class through the seam restamps `caps-*` and
      re-resolves dependent styles without restart (a framework-repo replica of the editor's M2 override
      item) — Stage A closes on this **independently**. The editor-side *first-consumer verified*
      checkbox lives in M2's exit gate (§7 `CapsMatrixTests` item), not here.

### 4.2 Stage B — Options dialog UI + first-run wizard + width-ruler tester, aligned with M6/M7

Scope (framework repo; sequenced after M6.WP1 so it inherits the FB-12 TaskDialog vocabulary):
- Framework **Options dialog** with **app-registered pages/sections** (review note 3) — apps get one
  settings surface; the editor contributes its §15 pages in M7.
- Default open keybinding **must be legacy-wire-safe** — *not* `Ctrl+Shift+O` (FB-14: legacy wires
  deliver it as `Ctrl+O`); an F-key default (e.g. F9) or dual wire-form registration, overridable in the
  `BuildApplication()` chain; the surfaced hint shows the binding that works on the current wire.
- **Advanced capability-overrides tab** with prominent warning, writing through the Stage A seam.
- **First-run wizard**: once-ever framework-wide (marker in `~/.cursorial/`), showing the Options binding
  and config-file locations; per-app first-run is a status-bar hint, not a wizard (review note 5).
- **Nerd Font width-ruler tester** (spec §18.4 + review note 6): sample glyphs against a single-cell
  ruler — presence AND single-cell advance both verified (a present-but-double-width glyph corrupts the
  grid); applies to the emoji tier too (VS16 sequences). *Traceability note:* spec §18.4 marks this
  confirmation-render a **[DEFER]** nicety; building it here deliberately supersedes that [DEFER] per
  accepted FB-17 (review note 6, maintainer-sanctioned 2026-07-03) — recorded so the defer boundary
  stays auditable, not accidental scope creep.

**Exit gate (framework tests + editor consumption):**
- [ ] Options dialog opens via the default binding on both `KittyTruecolor` and `Ansi16Legacy` wire
      presets (UITestHost).
- [ ] An app-registered page renders and persists through the store (a framework-side registered-page
      fixture — Stage B closes on it independently of editor schedule).
- [ ] First-run wizard shows exactly once per `~/.cursorial/` marker; suppressed thereafter.
- [ ] Width-ruler tester asserts presence + single-cell advance by cell assertions.
- The editor-side *first-consumer verified* checkbox lives in M7's §15 gate item (§12), not here — the
  editor's Tables page hosting in the framework dialog is M7's evidence, so a slip on either side never
  ambiguously blocks the other's gate.

### 4.3 Consumers and fallbacks

| Consumer | Consumes | Fallback if the stage slips |
|---|---|---|
| M2 caps layer (first consumer) | Stage A `CapabilityOverrides` for the "force a class, no restart" gate item | Integration-notes §7 recipe (`OnCapabilitiesChanged(app.Capabilities with {…})` + `CapabilitiesChanged` re-apply); test matrix is unaffected (composed `TestCapabilities` never needed the seam) |
| M5 `View.Surface` + access-key-cue option | Stage A store (per-app overlay) | `AppConfig` JSON stub in the app-state dir; migrate on landing |
| M6 recents/journals | **Not** the store — app state per §3.2 resolution 8 | n/a |
| M7 §15 Options pages + capability declaration page | Stage B dialog + Stage A seam | Editor-owned Backstage Options page (M7 plan's original scope) + §7 recipe |
| Icon ledger emoji column | Stage A `caps-emoji`/Icon Emoji tier | Icons author Glyph + Text only (unchanged from M5 plan) |

---

## 5. Cross-cutting engineering practices (binding on every milestone)

### 5.1 Harness conventions (stated once; milestone test plans only list what's specific to them)

All UI tests run on `UITestHost` (calling thread = UI thread): `RunFrame`/`RunUntilIdle`/`AdvanceTime`
on the fake clock — **every debounce in the product is driven deterministically this way** (750 ms undo
seal, 5 s autosave, ~200 ms outline rebuild, one-frame find/scroll-sync/swap-back debounces);
`SendKey/SendText/SendClick/SendResize/SendBytes` (bracketed paste and legacy escapes via `SendBytes` on
the real `VtInputDevice`); cell/row/byte assertions; teardown capture for INPC-leak and output-byte
checks. **Capability matrix:** every rendering/caret/input suite runs `TestCapabilities.KittyTruecolor`
**and** `Ansi16Legacy`; render-affecting suites add composed Ansi256 and NoColor (`with (ColorDepth)`)
from M2 (the caps layer's birth); image suites add `KittyGraphics`/`SixelGraphics`/`ITerm2Graphics`/none
in M7. NoColor suites always assert the redundant non-color channel (§18.3), never just "no crash".

### 5.2 The differential/oracle harness is a CI gate from M2 on

Built in M2 week 1 (Decision 14), before any renderer work, and *extended — never forked* — thereafter:
M3 adds `TableEditScriptFuzz`, M4 the §2.2/§2.3 extension sweep, M6 replace-all scripts, M7 the
split-view variant and the no-full-reparse instrumentation script. Per-PR CI runs 10k seeded steps;
nightly runs 100k plus the extension corpora. Failures replay from seed. The harness's passing-sections
report **is** the conformance-document generator (`docs/conformance.md`). A red harness blocks merge from
M2 onward — it is the project's correctness spine.

### 5.3 Benchmark-gate cadence

`[Trait("Category","Benchmark")]` suites copy the framework's pattern (warm, busy-spin, best-of-5,
GC-asserted zero steady-state alloc, frame-budget asserts): median-based gates against the pinned
reference doc, <16 ms typical / 50 ms hard ceiling, run under `KittyTruecolor` and `Ansi16Legacy`, run
twice in CI and fail only on repeat (flake control). The suite is cumulative — every milestone's
benchmarks keep running in all later milestones, so regressions surface at the milestone that caused
them. Roster by milestone: M1 typing latency/1 MB paste/zero-raster scroll; M2 typing + no-full-reparse +
scroll realization; M3 table typing (the R3 gate) + large-table scroll; M4 dialect keystroke + checkbox
toggle; M5 caret-move-with-chrome + typing-with-chrome; M6 typing-with-find-open + find-all + outline
rebuild; M7 keystroke/fling/startup (§13 final numbers).

**Runner profile (cross-machine variance control):** the absolute thresholds (<16 ms, <500 ms startup)
are defined against a **pinned benchmark runner profile** — a labelled/dedicated runner or a documented
reference machine, recorded in the benchmark suite's README. The per-PR merge gate is **self-relative**
(no regression vs a checked-in baseline captured on that profile); absolute numbers are verified at each
milestone exit on the reference profile. A runner-class change requires deliberate re-baselining — it
must never silently flip benchmark gates.

### 5.4 Framework-feedback loop

Every framework friction found during implementation is logged in `docs/framework-feedback.md` as an
FB item (or annotated onto an existing one) **when hit**, with a documented app-side workaround so no FB
blocks the critical path involuntarily. Statuses move `proposed → discussed → accepted →
implemented (local) → upstreamed` per §2.2's contribution flow. Cadence: filings happen as hit;
upstreaming PRs batch at milestone boundaries unless an item is on the DAG (FB-2, FB-13, FB-14.x, FB-17
— scheduled explicitly in §3.1). Each milestone's exit report updates the statuses it touched.

### 5.5 Dogfooding cadence

From **M4 on** (full dialect available), the editor is used to make real edits to Cursorial's own docs:
each milestone demo from M4 includes an open→edit→save session on a real framework doc, and doc edits
arising from this project's own FB work are made *in CursorialEdit* where practical. Every dogfooding
failure becomes a checked-in fixture or an FB item. M7 scripts the practice as
`DogfoodTests.OpenEditSave_CursorialDocs` (byte-clean round-trip of untouched regions) — the definition
of done's dogfood clause.

### 5.6 Demo checkpoints — something visibly runnable at every gate

| Gate | Demo |
|---|---|
| M1 | Open/edit/save a 10k-line plain file in a real terminal; `kill -TERM` leaves the shell sane |
| M2 | Open `README.md`, edit rendered + raw, save losslessly; toggle reveal live |
| M3 | `dotnet run -- docs/demo-tables.md`: growth/shrink/wrap in Ghostty + a 16-color terminal |
| M4 | `fixtures/m4-dialect.md` edited by a scripted command sequence; diff shows only sanctioned normalizations |
| M5 | Toolbar/menus session; relaunch with ribbon, Backstage▸Recent, Alt-KeyTips inside a table (Table Tools visible) |
| M6 | Regex find/replace with live highlights; outline navigation; `kill -9` → relaunch → recovery prompt → byte-exact restore; export HTML |
| M7 | Split view; inline images on Kitty + sixel withdrawing cleanly; Options dialog (framework-hosted) forcing capability classes live; full acceptance sweep |

### 5.7 CI lanes (what "green in CI" means mechanically)

Two named lanes; §1's milestone-done definition evaluates **both**:

- **Per-PR lane** (blocks merge): unit + UITestHost + conformance suites, 10k seeded fuzz steps (§5.2),
  benchmarks per §5.3 (self-relative, run-twice). Excludes `Category=Integration`.
- **Nightly-integration lane** (scheduled; PTY-capable runner): everything tagged
  `Category=Integration` — the M1 scripted PTY kill-TERM test (FB-4), M6's
  `RecoveryTests.KillDashNine_RelaunchOffersRestore` out-of-proc kill test — plus the ≥100k-step fuzz
  runs and the extension corpora (§5.2 nightly scope).

Milestone-exit evaluation = per-PR lane green **plus** the latest nightly-integration lane run green.
Gate items that live in the nightly lane say so explicitly (M1, M2, M6). A red nightly lane blocks
milestone exit, not individual PR merges.

---

## 6. M1 — Core text editing, app skeleton, R4/R5 spikes

**Objective.** A plain-monospace terminal text editor (no markdown) on the architecture's real skeleton —
`EditorControl`/`DocumentPanel`/`DocumentBuffer`/`EditController` — retiring **R4** (FB-1 text machinery)
and **R5** (custom `IScrollContentHost` band contract) and filing **FB-16/N1** upstream. FW-A (§4.1)
kicks off in the Cursorial repo in parallel.

**Exit gate — all mechanically checkable:**
- [ ] Spec §3 (plain-text subset): `UndoGroupTests.TypedSentence_CoalescesToOneGroup_SecondUndoRemovesPriorGroup`;
      `UndoGroupTests.Undo_RestoresCaretAndSelection`; `SelectionTests.Copy_YieldsExactSourceRange`;
      `CaretTests.*` covering arrows/Home/End/Ctrl+Home/End/word-motion/PageUp/Dn/goal-column over
      CJK/emoji/ZWJ fixtures.
- [ ] Spec §10.1: `FileRoundTripTests` (UTF-8/UTF-8-BOM/UTF-16-detect byte fidelity, mixed LF/CRLF
      preserved per line, ensure-trailing-newline configurable); `DirtyStateTests.SavePrompt_ShownOnCloseWhenDirty`
      drives the ported MessageBox via `UITestHost.SendKey` and asserts cells.
- [ ] **R5 retired:** `PanelSpikeTests` storm suite (scroll/edit/resize via `SendResize`, thumb-drag,
      PageDown-at-EOF) shows no blank bands, no extent drift, correct `ITerminalCaretService`
      publication; FB-16 filed upstream with spike evidence.
- [ ] **R4 retired:** `TextNavigationProbeTests` divergence report (our navigator vs `TextBox` behavior
      on cluster/wide-char/wrap-affinity fixtures) checked in; FB-1 upstream-vs-keep decision recorded in
      `framework-feedback.md`.
- [ ] Benchmark: `TypingLatencyBenchmark` — keystroke→frame < 16 ms (hard 50 ms) at caret in a 10k-line
      doc on both wire presets; 10k-line scroll is composite-slide (zero in-band re-raster, asserted by
      the WP7 app-side raster counters).
- [ ] Autosave stub: `AutosaveJournalTests` — journal written ≤5 s after last edit (fake clock),
      write-temp-then-rename asserted, real file never touched.
- [ ] FB-4 workaround: unit test on the emergency-restore byte writer (DECRST 1049, cursor show, SGR
      reset) + scripted PTY kill-TERM test (`Category=Integration` — runs in the §5.7
      nightly-integration lane; milestone exit reads that lane's latest run).
- [ ] `dotnet test` green with the §2.1 project wiring (both build configurations).

**Work packages (ordered; WP3 first after wiring, per the risk register):**
1. **WP1 — Solution/test wiring.** Create `CursorialEdit.Document`, `CursorialEdit.Dialogs`,
   `CursorialEdit.Tests`, and `$(UseLocalCursorial)` exactly per §2. Done-when: smoke test renders a stub
   root under `TestCapabilities.KittyTruecolor` and asserts cells; ArchitectureTests green.
2. **WP2 — Bootstrap + FB-4 teardown.** `UIApplication.CreateBuilder()…Build()` →
   `await app.RunAsync(() => new EditorShell())`; single root, no Window; CLI file-path arg (§10.4).
   App-local synchronous signal-path restore (SIGINT/SIGTERM/SIGHUP) until FB-4 lands.
3. **WP3 — R5 spike FIRST.** `EditorControl : Control` with `HandlesScrolling => true`, templating its
   **own** `ScrollViewer` (the gate checks `TemplatedParent`); SCP direct content =
   `DocumentPanel : Panel, IScrollContentHost` (`IsScrollClient = true`, `IsLogicalScroll = false`,
   `GetExtent()` from prefix sums, band = viewport + 2K); fixed-height fake blocks; storm tests;
   de-realized elements `TearDown()`. File FB-16 in parallel. Pre-agreed fallback: VSP+ItemsControl seam.
4. **WP4 — DocumentBuffer** (Document project): `List<Line>`, `TextPosition(Line, Col)` UTF-16,
   prefix-sum offset cache invalidated from the edit line, per-line `Version`, document `Epoch`,
   `AnchorTable` (position + gravity), `IDocumentBuffer` seam. Done-when: splice fuzz vs naive-string
   oracle green.
5. **WP5 — EditController + undo.** Single primitive `Apply(Edit, EditKind)`; all mutations funnel here
   (undo/redo replay flagged non-recording). Coalescing groups sealed on caret motion / newline /
   structural command / 750 ms idle (frame-aligned `UITimer`); N=1000 configurable; groups restore
   `CaretBefore` incl. selection (`TextBox` records as reference shape, reimplemented at document scope).
6. **WP6 — R4 probe + CaretNavigator (FB-1).** Bounded (~500-line) navigator over public
   `GraphemeWidth`/`GraphemeEnumerator`: cluster-boundary motion, word boundaries, soft-wrap with
   affinity, cell-measured goal column. Probe drives a `TextBox` and the navigator over identical
   fixtures; diff landings; record the FB-1 decision (upstream promotion PR vs keep; delete ours if it
   lands).
7. **WP7 — Plain-text rendering through the real pipeline.** Degenerate `BlockList` (block = paragraph
   run of lines, no parser) feeding `PlainTextPresenter` elements, each `IsRenderBoundary = true`;
   heights from wrap-row prefix sums; edits emit `BlockListChange` so M2's parser drops into an existing
   reconciliation seam; per-visual-row run maps in the Decision-8 shape with only `Text` runs.
   **Includes the lightweight app-side raster/render-call counters** on presenters and `DocumentPanel`
   that this milestone's gate items assert against (M2.WP13 and M7.WP1 *extend* these counters — they do
   not create them). Done-when: keystroke re-rasters exactly one block zone (raster-counter assert).
8. **WP8 — Caret + selection.** Caret = source anchor via `ITerminalCaretService.Publish(owner, col,
   row, CursorShape.BlinkingBar)`, `Clear` on focus loss/scroll-out; selection = document-level source
   range intersected per-presenter at draw; Shift+motion, mouse drag, double/triple-click, Ctrl+A.
9. **WP9 — Clipboard.** Copy/cut serialize source range via `IClipboardService` (OSC 52 write) **and**
   the app-internal store; paste = bracketed paste → one splice (all paste literal in M1). Only
   safe-everywhere chords bound (FB-14 alternates are M4/M5 work).
10. **WP10 — MessageBox port + dialog seam.** Port Gallery `MessageBox` (~100 lines) into
    `CursorialEdit.Dialogs` over `Window`/`ShowDialogAsync<T>` (cancellation throws OCE — handle);
    define `ITaskDialogService` per §3.2 resolution 3. (Ordered before file open/save because WP11's
    save prompt and `DirtyStateTests.SavePrompt_ShownOnCloseWhenDirty` drive the ported MessageBox.)
11. **WP11 — File open/save + dirty state.** `DocumentFile`: encoding detect, per-line ending
    preservation, trailing-newline ensure, atomic save; dirty `●` in a minimal status line; save-prompt
    on replace-when-dirty via WP10's service. No file-browse dialogs (M6) — CLI arg + save to known path.
12. **WP12 — Autosave stub.** `AutosaveJournal`: 5 s debounce (`UITimer`) + focus-loss trigger; snapshot
    = line-array copy; write off-thread, epoch-validated post-back; temp-then-rename in the journal's
    directory; keyed to document path (+ untitled id); deleted on clean save/close. No recovery prompt
    (M6).

**Framework items:** FB-1 probe/decision (WP6); FB-16/N1 filed (WP3); FB-4 workaround now, proposal stays
live; FB-3 consumed (bracketed paste + internal store); FB-7 consumed (§2.1 wiring); FB-12 seam only
(WP10); FB-14 deferred to safe chords. FW-A begins framework-side (§4.1) — not an M1 gate item.

**Tests beyond the gate:** buffer fuzz vs naive-string oracle (text equality, offsets, anchors, endings,
invariant *caret Col is always a cluster boundary*); undo fuzz (undo-to-bottom reproduces original
document + caret, redo-to-top the final); 1 MB paste benchmark. (The Markdig oracle harness is M2 — not
built here.)

**Risks:** (1) plain-text layout baking in line-oriented shapes that fight M2 presenters → WP7 builds the
real BlockList/reconciliation seam from day one, only the block *producer* is throwaway; (2) HEAD drift
while contributing FB-1 → pinned checkout + dual-config CI (§2.2); (3) FB-4 unautomatable on CI → unit
test the byte writer, PTY test opt-in, manual demo in the gate; (4) huge bracketed paste stalling a frame
→ single splice is memmove-cheap, 1 MB benchmark enforces, chunked apply as fallback; (5) UTF-16 Col vs
grapheme drift → only `CaretNavigator` produces caret positions + fuzz invariant; (6) journal location →
`IAppStatePathProvider` seam now, final decision per §3.2 resolution 8.

**Must NOT build (later owners):** parsing/`RunKind` beyond `Text`/reveal/caps/`Md.*`/Icons (M2); tables
(M3); markdown commands/smart paste/image placeholders (M4); Bars surfaces/KeyTips/alternate chords (M5);
find/replace/full TaskDialog/file dialogs/recovery prompt (M6); split view/raw toggle/images/config UI
(M7).

**Demo checkpoint:** per §5.6 M1 row.

---

## 7. M2 — Parse + render pipeline, reveal-on-edit, caps layer

**Objective.** Turn M1's plain-text surface into a WYSIWYG markdown surface for CommonMark core: pinned
Markdig pipeline, windowed incremental reparse proven by the differential/oracle harness, block-relative
run maps, boundary-promoted presenters, reveal-on-edit with the grapheme-snapped slide, raw-source mode,
and the `caps-*`/`Md.*` theme layer. Retires **R1** (reveal-without-reflow) and **R2** (windowed-Markdig
correctness + span fidelity). First consumer of FW-A's `CapabilityOverrides` seam.

**Exit gate — all mechanically checkable:**
- [ ] `DifferentialFuzzTests.WindowedEqualsFull` green for ≥100k seeded edit steps (insert/delete/paste/
      newline-merge mixes, fast-path edits tagged in the mix) — R2 retired (Decision 14). The 100k run
      is the §5.7 nightly-integration lane (per-PR runs 10k); milestone exit reads the lane's latest run.
- [ ] `SpanOracleTests.EverySpanReproducesItsConstruct` green across the CommonMark + GFM corpora for
      all pinned extensions.
- [ ] `docs/conformance.md` generated by `ConformanceReportTests` (§2 acceptance).
- [ ] `RevealTests.ToggleRevealChangesNoCellOutsideActiveBlock`, `RevealTests.ClipEdgeNeverSplitsWideCluster`
      (CJK/emoji), `RevealTests.CaretStaysOnGrapheme` — R1 retired; §4.1 "no reflow of other lines".
- [ ] §2.1 rendered constructs (headings incl. setext-as-H1/H2, emphasis, inline/fenced/indented code,
      blockquotes, ordered/unordered/nested lists, links incl. reference links + `<url>` autolinks, hard
      breaks with active-line `↵`, rules, escapes/entities) render formatted and round-trip byte-exact in
      `RenderRoundTripTests`; §2.1's image row excluded (M4 owns placeholder chips) — images hit the
      fallback presenter until then.
- [ ] §2.4: HTML/unsupported constructs render dimmed-literal, never crash (`FallbackPresenterTests`).
- [ ] §4.2 full-raw toggle: `RawModeTests.CaretPreservedAcrossToggle`; §4.3 `:active-block` well tint
      asserted by cell background in both modes.
- [ ] §18.1–18.4 color/icon axes: `CapsMatrixTests` render a reference doc under `KittyTruecolor`,
      `Ansi16Legacy`, composed Ansi256 and NoColor; NoColor asserts heading levels differ by
      weight/underline with zero color deltas; **forcing a class via override re-renders without restart
      — routed through the FW-A `CapabilityOverrides` seam once landed (fallback: integration-notes §7
      recipe)**.
- [ ] §13: `ParseInstrumentationTests.NoFullDocumentReparsePerKeystroke`; `TypingLatencyBench` <16 ms
      p95 on the 10k-line reference doc.
- [ ] §6.2 typing shortcuts `## `, `- `, `> `, `1. `, ``` ``` ```, `- [ ] ` produce the correct block
      live (`TypingShortcutTests`); `- [ ]` renders via fallback until M4 (§3.2 resolution 2).

**Work packages (ordered; WP1 in week 1 before any renderer work; WP6 before WP7 fan-out):**
1. **WP1 — Pipeline + oracle harness (R2 spike).** `Parsing/MarkdownPipelineFactory` with the pinned
   pipeline (Decision 2, Markdig 1.3.2). Span oracle: every precise inline span's source slice re-parses
   to the same construct, over vendored CommonMark/GFM corpora; `ConformanceReporter` writes
   `docs/conformance.md`. Done-when: oracle green or divergences catalogued per-construct.
2. **WP2 — BlockList + re-adoption (Decision 4).** `Block` carries `BlockId`, `Kind`, `LineCount` (not
   `StartLine`), `ContentStamp`, Markdig block ref, lazy `InlineRuns`; prefix sums from the edit point;
   re-adoption by (kind, first line with `Version` ≤ `ContentStamp`) with prefix/suffix trimming; content
   hash only as secondary check for moved blocks. Done-when:
   `ReAdoptionTests.EditedBlockKeepsBlockIdEveryKeystroke`.
3. **WP3 — Incremental reparse (Decision 3).** `ReparseWindowPlanner` (seed = edited blocks ± one
   top-level neighbor, expand while in container), `FastPathGate` (boundary-char scan over inserted
   **and** removed text), `FenceIntervalSet` (O(log n) safe-start), fence-parity extend-to-EOF,
   `LinkRefTable`/`FootnoteTable` with label→referencing-block invalidation, `FullReparseScheduler`
   (debounced off-thread, epoch-stamped, rejected on mismatch — Decision 13). Hooks
   `EditController.Apply`.
4. **WP4 — Differential fuzzer (Decision 14a).** Random documents × seeded edit scripts; after each edit
   assert windowed splice ≡ full parse (kinds, spans, line ranges) + `BlockId` stability for untouched
   blocks; fast-path-eligible and deletion edits are mandatory generators. CI 10k / nightly 100k (§5.2).
   Fallback per R2: always-window, then full-reparse degraded mode.
5. **WP5 — Run-map layout (Decision 8).** `RunMapBuilder` building per-visual-row maps from lazily
   realized inlines; wrap/affinity via `TextNavigation` if FB-1 landed, else M1's classifier; hidden
   marks zero-width at true source positions; synthetic runs map to marker source (`- `) or `SrcLen=0`.
   `RunMapBuilder` carries a **`WrapMode` layout flag** (wrap-on default; wrap-off = one visual row per
   logical line, active line kept visible via the R1 horizontal-slide machinery, document never scrolls
   horizontally) — the substrate for §8.1's View ▸ Toggle Word-Wrap command, surfaced in M5.WP4.
   **Caret-visibility invariant (binding, both modes):** the slide offset is *defined* as the function
   that keeps the caret within the visible span with 2 cells of edge slack (the `TextBox` convention),
   recomputed on every caret move/insertion — a caret outside the visible span after any edit is a test
   failure, not a UX nit. Clipped lines draw **continuation indicators** (dim `❮`/`❯` in the edge cells,
   the less/vim idiom) whenever content extends beyond the visible span, in both wrap-off and
   reveal-slide cases. Done-when: `RunMapTests` prove total source↔cell mapping both directions (both
   wrap modes); `CaretVisibilityTests` assert the invariant under scripted long-line edits (typing at
   line end/middle, undo restore, find-match navigation); indicator cells asserted in both clip cases.
6. **WP6 — Reveal slide (R1 spike — first renderer work).** Prototype `ParagraphPresenter` + reveal:
   active line renders natural-width, horizontally slid, grapheme-snapped clip (offsets snap to cluster
   boundaries; a straddling 2-cell cluster becomes blank padding). Fallback: force-unwrap active line to
   one slidable row.
7. **WP7 — Presenter suite (Decision 7).** `ParagraphPresenter` (paragraphs + ATX/setext headings),
   `CodeBlockPresenter` (language capture; per-line-group child boundaries when large; `MiniHighlighter`
   for C#/XAML/JSON/Markdown/shell — time-boxed two days, monochrome overflow is spec-legal),
   `QuotePresenter` per line (`▌` per depth), `RulePresenter`, `FrontMatterPresenter` (dim, folded,
   expand affordance → `InvalidateScrollExtent()`; owned here per §3.2 resolution 5), `ListItemPresenter`
   per item (bullet/number synthetic runs), `FallbackSourcePresenter` (HTML per §2.4; tables until M3;
   extension constructs until M4). Every presenter `IsRenderBoundary = true`; drawing via
   `RenderContext.DrawFormattedText` with programmatic `TextRun`s; reconciliation by `BlockId` in
   `DocumentPanel`. **Owns §3.2 resolution 12:** the composite (EditorControl + DocumentPanel +
   presenters) is factored as an instantiable `EditorView` here. Done-when: §2.1 suites green; keystroke
   re-rasters one zone; **two `EditorView`s instantiate over one
   `DocumentBuffer`/`BlockList`/`EditController` in a UITestHost smoke test** (render-only — scroll-sync
   and caret arbitration stay M7.WP2).
8. **WP8 — Caret/selection/word-motion over runs.** Source anchor → block → row → run → cell; horizontal
   motion walks clusters in `Text`/`RevealedMark` runs, structurally skips zero-width `HiddenMark`; word
   motion over concatenated visible text; goal column in cells; copy emits exact source.
9. **WP9 — Reveal + active-block integration.** `ActiveBlockId` from caret; active presenter re-layouts
   with `RevealedMark` runs (fences reveal; quotes reveal only active line's `>`; partial emphasis per
   §4.1 [EDGE]); `:active-block` → well tint. Done-when: caret crossing blocks re-rasters exactly two
   zones.
10. **WP10 — Raw mode (Decision 12).** `ViewMode.Raw` as presenter mode: source lines with token coloring
    from the same parse; identity run maps; `Ctrl+/` on the Kitty wire + legacy-safe alternate (Alt+/)
    pending FB-14.1.
11. **WP11 — Caps + theme layer (§18).** `Themes/MdTheme.xaml`: `Md.*` tokens in `ThemeDictionaries`
    keyed by `ThemeVariantKey` (author Base+Ansi256, hand-pick Ansi16, collapse NoColor), built as an
    **app-owned dictionary assigned to `UIApplication.Theme`** — it layers over the code-first
    `CursorialTheme.BuiltIn` backstop via the framework's lookup chain (`Resources → Theme → BuiltIn`;
    control parity verified), so there is **no dependency on the unpackaged `Cursorial.UI.Themes`
    assembly in the default package mode** (FB-7: `IsPackable=false`, no 0.3.1 package).
    `Cursorial.UI.Themes/Themes/Default/Palette.xaml` serves as an authoring *reference* only (the test
    project references it in project mode, which is fine); presenter classes `md-h1..h6`, `md-code`, `md-quote`,
    `md-frontmatter`; NoColor by weight/underline (StyleQuantizer handles attribute degradation). Glyphs
    (front-matter chevron, `↵`) route through `Icon` to establish the §18.4 pattern. Override gate item
    through FW-A (§4.3).
12. **WP12 — Typing shortcuts (verification package only, §3.2 resolution 2).**
13. **WP13 — Instrumentation + benchmarks.** Parse/raster counters (extending M1.WP7's app-side
    counters), `TypingLatencyBench`, `ScrollRealizationBench` (lazy inline realization on band
    re-anchor), conformance report generation.

**Mid-milestone checkpoint + shed order.** M2 is the plan's heaviest milestone (13 WPs, two spike
retirements). Checkpoint after WP6: both R1 and R2 are retired there — the milestone's risk mass is
gone. If M2 runs long past the checkpoint, the pre-authorized shed order is (1) `MiniHighlighter`
(already time-boxed; monochrome code blocks are spec-legal), then (2) the hand-picked Ansi16 palette
polish (ship derived mappings; hand-pick in M7's sweep). **Never shed** the oracle harness, the
differential fuzzer, or the reveal invariants — they are the correctness spine.

**Framework items:** FB-1 decision consumed (same-day swap if it lands mid-milestone — test graph is on
HEAD); **FB-14.1 contributed** (decode `0x1F` → `Ctrl+/` in `VtInputInterpreter` + regression test, the
raw toggle as first consumer); FB-16 convention consumed (adopt `BandInfo` if it lands); FW-A consumed
per §4.3; FB-15 no-op here.

**Risks:** window context poisoning (deep lazy continuation/list-tightness) → fuzzer biased toward
container joins, widen seed rule on confirmed class, always-window fallback; `UsePreciseSourceLocation`
gaps in extensions → WP1 oracle runs first, per-kind span-repair table or mark-visible rendering;
lazy-inline realization cost on scroll → cache realized runs by `ContentStamp`, `ScrollRealizationBench`
gates; MiniHighlighter scope creep → fixed token-class budget, time-boxed; Markdig leakage →
ArchitectureTests (§2.1); setext/lazy-continuation vs fast path (`---` adjacency flips rule↔setext-H2
without boundary chars in the edit) → seed always includes next top-level block, dedicated setext-flip
fuzz generator, fast-path gate treats edits adjacent to `---`/`===` as windowed.

**Must NOT build:** `TableModel`/`TablePresenter`/table editing (M3 — tables hit
`FallbackSourcePresenter`); extension presenters, checkbox toggle, `ImageChipPresenter`, dialogs,
formatting commands, list continuation, auto-pairing (M4); Bars surfaces (M5); find/outline/full
autosave/export (M6); split view, image display, Options UI (M7). Parse-side support for all extensions
ships now (one pinned pipeline); only their presentation waits.

**Demo checkpoint:** per §5.6 M2 row.

---

## 8. M3 — Tables (headline feature, highest risk)

**Objective.** GFM pipe tables as live box-drawing grids per spec §5 / architecture §2.5 / Decision 11:
`TableModel` + `TablePresenter` with per-row `IsRenderBoundary = true` child presenters (committed
deliverable), cell-measured widths via `GraphemeWidth`, live one-column reflow, all three overflow modes,
the full §5.3 keyboard operation set as commands, cell-rect selection, and the creation flows. Retires
**R3** (table raster inside the 16 ms budget).

**Exit gate — all mechanically checkable:**
- [ ] **R3 retired (M3-entry gate):** `TableTypingBenchmark` (framework benchmark methodology) types 200
      keystrokes into a 40×20 table with wrapped cells; worst frame < 16 ms typical / 50 ms hard, zero
      steady-state alloc, on both wire presets. **Must pass on the WP1–WP3 skeleton before WP4+
      proceeds.**
- [ ] §5 acceptance 1: `TableCreationTests.PickerInsert_TypeCells_TabCreatesRow` — border glyphs
      (`┌┬┐├┼┤└┴┘─│`) land on exact columns after every edit (cell assertions).
- [ ] §5 acceptance 2 (keyboard half): `TableOperationTests` — one test per §5.3 row, keyboard-driven,
      asserting resulting buffer text re-parses (Markdig) to the expected shape. All ops exist in
      `TableCommands`; the menu/ribbon half completes in M5 against these same commands.
- [ ] §5 acceptance 3+4: `TableReflowTests.WidenOnType_ShrinkOnDelete` — one column widens on new-widest,
      shrinks on delete (O(1) via `CountAtMax`); frame-byte capture proves damage bounded to the row
      strip + border band (§13 "no full-table reflow").
- [ ] §5 acceptance 5: `TableRoundTripTests.AlignmentRoundTrips` (`:---/:--:/--:`).
- [ ] §5 acceptance 6: `TableOverflowTests` — wrap (default), truncate (`…` + reveal-on-focus),
      column-window scroll at viewport widths 40/80; document never scrolls horizontally (no horizontal
      `ScrollViewer` exists — FB-6 sidestep asserted structurally).
- [ ] §3.1 [EDGE]: `TableNavigationTests.GoalColumnSnapsToNearestCell`; arrow onto a table line enters a
      cell.
- [ ] §3.4/§5.2: `PasteToTableTests` (offer/decline via `ITaskDialogService`), `PipeRowShortcutTests`
      (auto delimiter row).
- [ ] Fuzz: `TableEditScriptFuzz` (harness extension, §5.2) green for 10k scripts.

**Work packages (spike phase WP1→WP3 first, mandated by the risk register):**
1. **WP1 — `TableModel`** (Document project). Derived overlay over the block's source lines (Decision 1):
   per-row source line index + pipe-aware, escape-aware `CellSpan[]` (block-relative UTF-16); per-column
   GFM alignment; per-column `(WidthCells, MaxContentWidth, CountAtMax)` measured with
   `GraphemeWidth.StringWidth/ClusterWidth`, clamp 3..40. **Cell spans derive from Markdig's Table AST +
   `UsePreciseSourceLocation`, never a hand pipe-scanner** (risk d). Includes the cell-layout pass: given
   widths + overflow mode, emit per-visual-row cell fragments (wrapped logical row = N visual rows).
   Done-when: ASCII/CJK/emoji/escaped-pipe/backtick-pipe fixtures pass; extraction ≡ Markdig cell
   boundaries under fuzz.
2. **WP2 — `TablePresenter` + `TableRowPresenter`, render-only.** One `TableRowPresenter` per logical
   row, each `IsRenderBoundary = true`; borders drawn by row presenters (top border owned by row 0);
   header bold + fill; `:alternate` on body rows (zebra off by default); run maps per **visual** row with
   `Synthetic` border runs (`SrcLen = 0`, no caret stop) and `Text` runs per cell fragment; height = Σ
   visual heights → prefix sums → `InvalidateScrollExtent()`.
3. **WP3 — R3 benchmark gate.** Drives WP1+WP2 plus a minimal splice path (real `EditController.Apply`).
   Passing = milestone entry. Fallback per register: dirty-cell diff drawing inside row presenters.
4. **WP4 — `TableEditingController` core** (Document project; keyed from the app input layer).
   `EditController` routes keys here when the caret's block is a table. Intra-cell typing/backspace/
   delete → ordinary splices on cell source ranges with pipe escaping; paste into cell converts
   newlines→spaces, escapes pipes; Tab/Shift+Tab with row wrap; Tab in last cell appends a row; Enter
   commits downward, exits from last row; `Delete` clears a selected cell; Insert-`<br>` renders `↵`.
   Cell focus = (row, col) indices surviving reparse via Decision-4 re-adoption. Each op one sealed undo
   group; undo restores cell focus.
5. **WP5 — Live reflow.** Re-measure edited cell; widen if > `MaxContentWidth`; shrink only when the
   unique widest cell (`CountAtMax == 1`) shrank → recompute that one column. Width change invalidates
   the border band but re-rasters only moved row strips; height change shifts prefix sums +
   `InvalidateScrollExtent` without repainting neighbors.
6. **WP6 — Overflow modes.** Wrap-in-cell default; truncate (`…`, full text on focused cell);
   column-window scroll: integer first-visible-column offset applied **inside the presenter's render**
   (Decision 11, FB-6 sidestep) — focused cell always kept in-window; edge indicators as synthetic runs.
   Mode in in-code `TableOptions` (§15 surface is M7).
7. **WP7 — Structural operations (§5.3 complete).** `Commands/TableCommands.cs` `BarCommand` registry
   (Text + `InputGestureText` now; icons M5 per §3.2 resolution 4): InsertRow/Column ops (`Alt+↑/↓`
   legacy-safe), DeleteRow (header-delete promotes next), DeleteColumn (last column deletes table),
   MoveRow/Column (carries alignment), SetAlignment, DeleteTable, ClearCell, InsertCellBreak — whole-line
   structural splices through `EditController.Apply` with an `EditKind.TableStructural` re-adoption hint
   (risk b).
8. **WP8 — Cell-rect selection + copy-as-sub-table.** Shift+arrows/Shift+Tab/mouse drag select whole-cell
   rectangles painted by row presenters; copy emits a markdown sub-table (header synthesized per GFM).
9. **WP9 — Navigation integration.** Arrow onto a table line enters nearest cell by goal column; ↑/↓
   preserve goal column across/out of tables; word motion within a cell (FB-1 outcome or M1 classifier).
10. **WP10 — Creation flows.** (a) `TableSizePicker` + `InsertTable` in a minimal dialog (§3.2
    resolution 1); (b) pipe-row shortcut: Enter on `| a | b |` auto-inserts the delimiter row, caret to
    cell (0,0); (c) paste-to-table offer via `ITaskDialogService`; decline inserts literally.
11. **WP11 — Round-trip + fuzz closure.** `SaveNormalizer` table policy: normalize inter-pipe padding
    **only** for user-edited rows / structurally-touched tables; untouched tables byte-identical.
    `TableEditScriptFuzz`: random tables × scripts (cell edits incl. pipes/escapes/CJK, every WP7 op,
    pipe-row conversions) asserting windowed ≡ full, `CellSpan` slices reproduce source, re-serialized
    output reparses to the same shape.

**Framework items:** FB-6 sidestepped structurally (asserted); FB-1 consumed if landed; FB-14 verified
non-blocking (all M3 chords in the safe-everywhere set; `Ansi16Legacy` `SendBytes` proves it); FB-7/FB-16
mechanics per §2; possible new FB if per-row boundary count stresses the scene at 500 rows (file with
`LargeTableScrollBenchmark` numbers).

**Milestone benchmarks:** `TableTypingBenchmark` (gate), `LargeTableScrollBenchmark` (500-row PageDown
storm, band re-anchor budget).

**Risks:** (a) wrapped cells vs per-visual-row run maps → WP1's cell-layout pass is the single owner of
visual-row fragments; wrapped-CJK fixture spiked in WP2 before WP4; (b) structural ops defeat re-adoption
→ `EditKind.TableStructural` carries `BlockId` as explicit hint; fallback kind+span-overlap when window
contains exactly one table; (c) 500 sticky boundaries stress scene → early benchmark; fallback N-row
group presenters; file FB with numbers; (d) cell-span drift from Markdig pipe rules → never hand-parse;
span oracle covers tables; (e) column-window keyboard UX spec-silent → decision recorded (focused cell
drives window); flagged to Mike in the M3 report for ratification; (f) truncate-mode reveal-on-focus vs
reveal invariants → reuse R1's grapheme-snapped slide within the cell box; assert no cell outside the row
strip changes.

**Must NOT build:** Bars surfaces/contextual tab/context menus/KeyTips (M5 — commands only); Bars hosting
of the picker (M5); replace-all table protection (M6 — but export the pipe-escape helper it will use);
image chips (M4); split view, §15 Options UI (M7).

**Demo checkpoint:** per §5.6 M3 row.

---

## 9. M4 — Full dialect, editing commands, interactive elements

**Objective.** The complete §2.2/§2.3 dialect as rendered, interactive content; every §6 inline/block/
smart-editing command; §7 links/footnotes/checkboxes and image *placeholders* (protocol display is M7).
All commands are `BarCommand` definitions in the newly created `CommandRegistry` (§3.2 resolution 4),
bound to the FB-14-aware keymap now and consumed by M5's surfaces later. Retires the **R2 residual**
(fast-path blacklist symmetry across extensions). No architecture decision reopened.

**Exit gate — all mechanically checkable:**
- [ ] `DialectConformanceTests` green: conformance report gains §2.2/§2.3 sections (task lists,
      strikethrough, autolinks with exactly the `http/https/mailto/www.` scheme list, footnotes,
      definition lists, alerts, math incl. `$`-currency [EDGE], front matter).
- [ ] `WindowedParseFuzzTests` extension sweep green — fast-path symmetry for `:`, `[^`, `$$`,
      front-matter `---` proven with insert **and** delete edits (R2 residual retired).
- [ ] `CommandRoundTripTests`: every §6.1/§6.2 command yields source whose full reparse equals the
      windowed result and reproduces the construct; double-application is identity (toggles) or no-op.
- [ ] `TypingShortcutTests`, `ListContinuationTests`, `OrderedRenumberTests` (§6 acceptance 2–4).
- [ ] `PartialOverlapToggleTests` (bold over half-bold bolds the whole selection, §3.2 [DECISION]);
      `InlineCodeBacktickFenceTests` (double-backtick fence, §6.1 [EDGE]).
- [ ] §7 minus image display: `LinkDialogTests`; `OpenLinkExplicitTests` (plain click only moves caret;
      relative-`.md` → resolved-path prompt with "Open (replace)"); `ImagePlaceholderTests` (chip + alt
      text; broken local path → warning tint, no crash); `FootnoteNavigationTests` (to definition and
      back); `CheckboxToggleTests` (Space + click flip `[ ]`↔`[x]`, one undo unit, strikethrough+dim).
- [ ] `SmartPasteTests` + `TableCellPasteEscapingTests` (§3 acceptance 4).
- [ ] `KeymapMatrixTests` on both wire presets: every M4 chord fires via FB-14 alternates. Legacy-wire
      posture (the wire delivers `Ctrl+Shift+X` byte-identically to `Ctrl+X`, so Cut firing on those
      bytes is wire physics, not a distinguishable bug) — asserted as: (a) on `Ansi16Legacy`,
      Strikethrough/PlainPaste are never *registered* under a gesture form that collides with or shadows
      Cut/Paste; (b) the legacy-safe alternates fire their commands; (c) each command's surfaced
      `InputGestureText` hint is the binding that actually works on the current wire.
- [ ] §2 acceptance 2: `SaveNormalizerTests` — bullet-marker→`-` applied on save (unconditional per §2.1
      [DECISION]); setext→ATX / indented→fenced applied per the Q4 posture flag (both postures tested);
      untouched regions byte-exact; the user-facing normalization changelog `docs/normalizations.md`
      checked in (WP10).
- [ ] `SpecialCharacterInsertTests.PickerInsertsAtCaret_OneUndoUnit` (§8.1 Insert ▸ Special character —
      WP8).
- [ ] Benchmark `DialectKeystrokeBenchmark`: typing inside a 500-item task list and a callout re-rasters
      one presenter zone, p95 < 16 ms.

**Work packages (WP1 first — R2 residue spike):**
1. **WP1 — Harness extension sweep.** Extend the Decision-14 corpora with all §2.2/§2.3 constructs; fuzz
   windowed-vs-full with fast-path and deletion edits crossing `:`, `[^`, `$$`, front-matter boundaries;
   autolink scheme oracle (Markdig also matches `ftp://` — pin the §2.2 [EDGE] list). Blacklist edits in
   `Parsing/FastPathGate` if the fuzzer finds holes. Done-when: 24h-class fuzz green; conformance report
   emits §2.2/§2.3.
2. **WP2 — Extension presenters** (`Presenters/`): `CalloutPresenter` (quote bar + type Icon + caps
   title; message-box vocabulary), def-list parts in `ParagraphPresenter`, math runs (`md-math`,
   mark-visible), footnote markers (superscript digits per ledger), content icons created in
   `Icons/EditorIcons.cs` — only the ledger rows this WP needs, per §3.2 resolution 13 (M5.WP2 extends
   the same factory) — strikethrough via `TextAttributes`
   (dim fallback keyed off `TextStylingCapabilities.Strikethrough`), autolink runs filtered to the scheme
   list at run derivation, task-checkbox Icon (`nf-md-checkbox_*` / `☐☑` floor) + done-item
   strikethrough+dim. Theme: `Md.Callout*`, `Md.Math` tokens incl. NoColor variants (Icon + caps label
   carry type; no fill). FrontMatterPresenter exists (M2) — only fold-toggle wiring here (§3.2 res. 5).
3. **WP3 — Command/keymap spine.** Create `Commands/CommandRegistry.cs` (§3.2 resolution 4): `BarCommand`
   definitions (Text/Icon-slot/InputGestureText per ledger) for all §6/§7 commands; `Input/Keymap.cs`
   branches on `ProtocolCapabilities.KittyKeyboardProtocol`: dual `Ctrl+Space` gestures (FB-14.3),
   legacy-safe alternates for `Ctrl+Shift+X`/`Ctrl+Shift+V`, legacy alternate for ``Ctrl+` ``
   (NUL-ambiguous). Done-when: `KeymapMatrixTests` green on both presets; M5 binds surfaces without
   touching handlers.
4. **WP4 — Inline formatting engine** (`Editing/InlineFormatter`): Bold/Italic/Strike/InlineCode/
   Link-wrap/ClearFormatting as span algebra over the block's Markdig inline spans (forcing lazy-inline
   realization via the Decision-5 query hook); §3.2 partial-overlap toggles toward applied;
   backtick-fence [EDGE] with space padding. Each command one sealed undo group restoring
   caret/selection.
5. **WP5 — Block formatting engine** (`BlockFormatter` + `ListRenumberer`): heading set(1–6)/cycle/
   paragraph; bullet/ordered/task conversions; ordered auto-renumber in the reparse-notification path;
   blockquote wrap/unwrap incl. nesting; code-fence wrap + `LanguagePicker` (shipped `ComboBox`, reused
   by M5); callout wrap with type parameter; HR insert; footnote insert (`[^n]` next-free id from
   `FootnoteTable`, definition stub, caret into definition).
6. **WP6 — Smart editing** (`SmartTyping`): Enter continuation for lists/tasks/quotes, empty-item
   termination; auto-pairing (wrap-selection for `*`, `` ` ``, `[`, `(`, `_`; no-selection pairing on for
   `` ` ``/`[`/`(`, off for `*`/`_`); Tab nests lists / 2-space indent in prose, stray tabs→spaces, no
   silent indented-code; smart quotes/dashes toggle **default off**. Options on in-memory
   `EditorOptions` (M7 persists via FW-A store).
7. **WP7 — Interactive elements.** Checkbox toggle (Space/click via run-map hit test) as one undo unit;
   footnote activation scroll-to-definition + highlight, back-reference return via `AnchorTable`;
   front-matter fold toggle; link URL reveal + status-bar readout + hover tooltip gated on `caps-motion`.
8. **WP8 — Link/Image dialogs + Open Link.** `Dialogs/LinkDialog.xaml`, `ImageDialog.xaml` (app project;
   `Window` + `ShowDialogAsync`, `TextBox` fields — sanctioned reuse); `ImageChipPresenter` (Icon + alt
   text, single caret stop, single-line chip) with off-thread local-path existence check → `:broken`
   warning tint (epoch-validated post-back); `OpenLinkCommand`: explicit only (Ctrl+Click/context),
   OS-launch for absolute URLs, relative-`.md` → `ITaskDialogService` prompt with resolved path + "Open
   (replace)" honoring the save prompt. Also **`SpecialCharacterDialog`** (§8.1 Insert ▸ Special
   character): a grid/list picker over `Window`/`ShowDialogAsync` reusing the
   TableSizePicker/LanguagePicker patterns, curated set (typographic punctuation, arrows, box-drawing,
   common symbols), inserts at caret through `EditController.Apply` as one undo unit; registered as
   `Insert.SpecialCharacter` in WP3's registry so M5's §8.1 inventory manifest is complete.
9. **WP9 — Smart/plain paste** (`PasteController`): bracketed paste routes through smart rules — in-cell
   newline→space + pipe escaping (plugs into M3's cell-range edits); plain paste = one-shot "next paste
   literal" mode skipping smart transforms; **micro-decision (flagged to maintainer):** structural cell
   escaping still applies under plain paste — encoded in `TableCellPasteEscapingTests`.
10. **WP10 — `SaveNormalizer` dialect policies + normalization changelog (§2 acceptance 2).** Extends
    M3.WP11's table-padding policy with the §2.1 [DECISION] rewrites, as targeted line rewrites driven
    by block spans (architecture §2.2): **bullet-marker→`-` unconditional** (not in Q4's scope);
    **setext→ATX and indented→fenced behind a Q4 posture flag** — conservative default until the §17 Q4
    answer lands: rewrite only blocks the user edited, untouched regions byte-exact (flag flips to
    always-on if Q4 answers "normalize freely"). Creates the **user-facing normalization changelog**
    `docs/normalizations.md` enumerating every on-save normalization (table padding included); shipped
    final by M7.WP8. This WP is what makes the M4 demo's "diff shows only sanctioned normalizations"
    claim (§5.6) real. Done-when: `SaveNormalizerTests.*` green (gate item above).

**Framework items:** **FB-14.2 + FB-14.3 contributed** (Cursorial repo: `modifyOtherKeys` negotiator
opt-in; `KeyGesture` dual-shape `Ctrl+Space` matching) — M4 is where those chords first bind; app keeps
legacy alternates regardless. FB-15 consumed if landed (Unicode floor suffices otherwise). FB-12 seam
consumed (Open-replace). FB-1/FB-3 as established. `$(UseLocalCursorial)` while FB-14 changes are in
flight.

**Milestone tests beyond the gate:** command idempotence property fuzz; paste-structure fuzz (random
payload into random cell preserves row/pipe structure); formatter/renumber/smart-typing unit tier runs
headless against `DocumentBuffer` + `EditController` (UITestHost reserved for rendering/interaction/
keymap); `CheckboxToggleBenchmark`; broken-image-probe benchmark (doc with 50 broken chips).

**Risks:** autolink scheme drift → run-derivation filter + oracle pin; span algebra over never-realized
inlines → force realization via the Decision-5 path, test on freshly-loaded document; legacy chord
collisions → alternates from the verified safe set, `KeymapMatrixTests` asserts no shadowing per wire;
edit-adjacent delimiter ambiguity (`***`) → post-command reparse assertion + idempotence fuzz; renumber/
undo interaction → renumber folds into the triggering edit's undo group, applied once per
`BlockListChange`, epoch-guarded; plain-paste semantics vs source-of-truth → documented micro-decision;
broken-path probe jank → off-thread + epoch-validated post-back, benchmarked.

**Must NOT build:** Bars surfaces/KeyTips (M5 — stop at `BarCommand` definitions + keymap);
find/replace/outline/full autosave/export (M6); split view, `IImageBackend`/image swap, FB-13
dependency, config persistence (M7); command palette, multi-cursor, footnote auto-collection, math
typesetting, HTML rendering (§14).

**Demo checkpoint:** per §5.6 M4 row. Dogfooding begins (§5.5).

---

## 10. M5 — Bars command surface

**Objective.** Surface the M1–M4 command set on Bars: Toolbar + classic menu bar default, Ribbon opt-in,
one `BarCommand` inventory driving all surfaces. All Bars primitives verified present at HEAD except
`BarMenuItem` — **FB-2 is contributed here, first** (longest lead). M5 retires no R1–R5 (closed in
M1–M3); its risk load is FB-2 plus the milestone risks below.

**Exit gate — all mechanically checkable:**
- [ ] §8 ✓1: `CommandInventoryTests.AllSpec81CommandsRegisteredExactlyOnce` (reflection over
      `CommandRegistry` vs a literal §8.1 manifest) and
      `SurfaceParityTests.MenuToolbarRibbonBindSameCommandInstances`.
- [ ] §8 ✓2: `TableToolsTests.ContextualTabVisibleOnlyWhenCaretInTable` (caret in/out via `SendKey`;
      `RibbonTab.Visibility` + Table-menu enablement asserted).
- [ ] §8 ✓3: `SurfaceSwitchTests.BoldViaToolbarRibbonAndMenuProduceIdenticalEdits` (same `Edit` records,
      same undo group shape).
- [ ] §8 ✓4: `ChromeOverflowTests.NarrowResizeCollapsesToChevronAndItemsRemainInvocable` (`SendResize`
      200→60 cols; invoke an overflowed command from the `»` popup).
- [ ] FB-2 landed in Cursorial with `BarMenuItemTests.*` green (auto-fill + command-owned check state).
      *This is a deliberate hard exit dependency:* the interim plain-`MenuItem` mitigation (risk e)
      unblocks **in-milestone progress only** — it does not soften this gate. In-project control of the
      framework (WP1 is first, longest lead) is what makes a hard gate acceptable; if FB-2 stalls past
      the toolbar half's completion, escalate to Mike rather than shipping the interim `MenuItem`.
- [ ] KeyTips: `KeyTipSurfaceTests.AltShowsBadgesOnActiveSurfaceOnly` on both surfaces; SuperTips render
      from `Description`.
- [ ] Icon gate: `IconLedgerTests.PinnedGlyphsMeasureDeclaredCellWidth` (every NF codepoint + Unicode
      floor pick measured via `GraphemeWidth.StringWidth` against declared width) and
      `IconLedgerTests.NoTofuWhenNerdFontAbsent`. *Scope note:* this validates layout-budget consistency
      and catches wide/ambiguous **Unicode-floor** picks (e.g. the Copy pick `⿻` U+2FFB genuinely
      measures 2); NF PUA codepoints all measure 1 in `GraphemeWidth` by construction, so **real NF
      rendering width is verified only by the FW-B width-ruler tester (§4.2)** — this test does not
      satisfy §18.4's confirmation-render for NF glyphs.
- [ ] `WordWrapToggleTests.WrapOff_OneVisualRowPerLogicalLine_NoHorizontalDocScroll` — View ▸ Toggle
      Word-Wrap (WP4) drives M2.WP5's `WrapMode` flag; active line stays visible via the R1 slide.
- [ ] Benchmark: `Bench_CaretMoveWithFullChrome` — caret motion with full chrome + check-state sync
      inside the 16 ms frame, both wire presets.

**Work packages:**
1. **WP1 — FB-2 `BarMenuItem` (Cursorial repo, first).** `BarMenuItem : MenuItem` in
   `Cursorial.UI.Bars`: auto-fill unset `Header`/`Icon`/`InputGestureText` from the bound `BarCommand`
   via the internal `BarCommandSync` (same-assembly requirement is why it lives in Bars); SuperTip
   auto-provision; check state owned by `ICheckableCommandParameter` with `MenuItem` self-toggle
   suppressed (mirror `BarToggleButton.IsCheckedCommandOwned`); theme entry; tests in
   `Cursorial.UI.Bars.Tests` on house patterns. Editor consumes via `$(UseLocalCursorial)`.
2. **WP2 — Icon resources (app).** Extends the M4-created `Icons/EditorIcons.cs` (§3.2 resolution 13 —
   one owner, no re-sourcing pass): one factory per ledger row returning a
   **fresh** `Icon` per call (never shared — risk a), `Glyph` = pinned NF codepoint, `Text` = Unicode
   floor, `GlyphWidth` pinned where non-1. Pin every codepoint against the Nerd Fonts cheat sheet now;
   resolve provisional "fallback:" picks with the width ruler. Update the ledger to `wired` for
   glyph/Unicode tiers; PNG rows stay `needed` (`ImageUri` seam unset); Emoji column wires only if FW-A's
   `caps-emoji`/Icon Emoji tier has landed (§4.3), else stays unwired — do not build the tier app-side.
3. **WP3 — `CommandRegistry` enrichment (app; §3.2 resolution 4).** Full §8.1 inventory: icons from
   `EditorIcons`, `Description` for SuperTips, `InputGestureText` from the M4 keymap table (hints match
   what fires per wire), `IsCheckable` toggles with one `CheckableCommandParameter` each;
   `CommandStateSync` listens to caret/selection-format + view-mode change, updates `IsChecked`, raises
   `RaiseCanExecuteChanged` **coalesced to one pass per settled caret move** (risk c). M6-owned commands
   (Find, Replace, Export▸, Outline) declared as `CanExecute => false` stubs behind
   `CommandRegistry.Attach(name, ICommand)`.
4. **WP4 — Default surface: Toolbar + menu bar.** `Chrome/EditorChrome.xaml(+.cs)`: (a) `Menu` of
   `BarMenuItem`s (File/Edit/Format/Insert/Table/View; Table menu enabled only in-table; View menu
   includes Toggle Word-Wrap wired to M2.WP5's `WrapMode` flag — gate item above); (b) `Toolbar`
   per §8.3 — save, undo/redo, bold/italic/code, heading combo (`BarComboBox`, face tracks caret block
   level), list toggles, insert-table split-button (invoke = M3 picker; dropdown = Table/Paste-to-table),
   callout split-button, link, find(stub); per-item `OverflowMode` pinned so save/undo never fold.
   Done-when: overflow + menu round-trip tests green; §5.3 ops reachable from the Table menu (§5
   "keyboard **and** menu" now fully satisfied on the default surface).
5. **WP5 — Ribbon opt-in surface.** `Chrome/EditorRibbon.xaml(+.cs)`: tabs Home/Insert/Format/View per
   §8.2 (`RibbonTab`/`RibbonGroup`); File tab → `BackstageRequested` → `Backstage` via `BackstageHost`
   with New/Open/Save/Save As/Export▸(PDF hidden)/Recent/Exit. Recent backed by `State/RecentFilesStore`
   (created here per §3.2 resolution 7, updated by M1's open/save path). QAT default save/undo/redo;
   `QuickAccessMoreCommandsRequested` stub (persistence → M7 Options).
6. **WP6 — Contextual Table Tools.** `Chrome/TableContextBinding`: on `EditController.ActiveBlockChanged`
   with a table block, show the `RibbonTab.IsContextual = true` tab, else collapse (framework handles
   selected-tab fallback); same signal gates the Table menu + §5.3 command enablement (alignment toggles
   use per-column check state from `TableModel`). Done-when: gate ✓2 +
   `TableToolsTests.OperationsFromRibbonProduceValidGfm`.
7. **WP7 — KeyTips.** `app.EnableKeyTips()` at bootstrap; pin top-level badges (`KeyTip.Key`: menu
   F/E/O/I/T/V; ribbon tabs F/H/N/M/W); suppress auto-assign collisions; menu-bar KeyTips single-level in
   v1 — access keys (`_`-prefixed headers) cover submenu levels. Badge-collision audit test clean.
8. **WP8 — Surface-switch config.** One honored key `View.Surface = Toolbar|Ribbon` written through the
   **FW-A options store** (per-app overlay; §4.3 — the `AppConfig` JSON stub only if Stage A slipped);
   `EditorChrome` builds the configured surface at startup; a debug command flips it live (chrome row
   rebuilds; `CommandRegistry` instances are surface-independent so nothing rebinds).
   `SurfaceSwitchTests` boots both configs headlessly.
9. **WP9 — Gate demo + ledger/docs update + PNG procurement kickoff.** Run the §5.6 demo; update
   `framework-feedback.md` (FB-2 → `upstreamed`/`implemented (local)`, FB-15 status) and the icon-ledger
   header with the PNG nominal-size answer (toolbar placements are 1-cell — recorded for procurement).
   **Send the PNG procurement request to Mike here** (sizes are now pinned): flip the ledger's PNG rows
   `needed → png-requested`. This anchors the procurement deadline — M7.WP4 wires received assets, and
   escalates at M7 start if none have arrived (§12).

**Framework items:** FB-2 contributed (WP1); FB-15 tracked (consume via FW-A if landed); FB-7 consumed;
FB-14 consumed (gesture hints from the M4 table); possible new FB if `BarCommand.Icon` needs a sanctioned
multi-surface icon-source shape (app-side factory is the workaround either way).

**Milestone tests beyond the gate:** framework-side WP1 suite; chrome/icon capability matrix incl.
NoColor check-state via non-color channel and default-caps no-tofu; `Bench_TypingWithChromeVsWithout`
(chrome adds **zero** re-raster to document zones — zone-raster instrumentation). No new fuzz surface: M5
adds no parse/edit paths — `SurfaceParityTests` proves every surfaced command routes through the
already-fuzzed `EditController.Apply` pipeline by comparing `Edit` records.

**Risks:** (a) `Icon` is a `Control` — one instance auto-filled into toolbar+ribbon+menu violates
single-parent → factories + dual-surface bind test + FB if auto-fill is otherwise unusable; (b) NF
codepoint width/coverage → pinned codepoints + `GlyphWidth` + ruler test; `caps-nerdfont` default-absent
keeps the floor path shipped; (c) check-state sync churn (~40 commands per caret move) → coalesced
settled-caret pass, dirty-only `RaiseCanExecuteChanged`, benchmark gate; (d) Backstage layout integration
→ use `BackstageHost` exactly as `BackstageTests` does; modal layer above `EditorChrome`, Esc-dismiss
test; (e) FB-2 schedule coupling → WP1 first, toolbar half parallel, interim plain `MenuItem` behind the
same registry (deleted when FB-2 lands, logic unchanged).

**Must NOT build:** find/replace bar, outline panel, export renderers, autosave/recovery prompts (M6 —
disabled stubs only); Options UI, capability-override page, QAT persistence, PNG icon tier, §7.2 image
display (M7); command palette, multi-level menu KeyTips (deferred).

**Demo checkpoint:** per §5.6 M5 row.

---

## 11. M6 — Cross-cutting features (find/replace, outline, autosave/recovery, export, dialogs, recents)

**Objective.** Every §9/§11/§12/§10.3 acceptance item plus the §10.1 dialog surface, building FB-12's
TaskDialog/MessageBox/file-dialog suite app-agnostic for later extraction. Retires no R1–R5 (already
burned down); **consumes** the Decision-14 harness to prove replace-all correctness; this is where the
spec's "never lose work" goal (§1) becomes real. FW-B (§4.2) starts framework-side once WP1's TaskDialog
vocabulary exists.

**Exit gate — all mechanically checkable:**
- [ ] §9: `FindBarTests.IncrementalFind_HighlightsAllMatches_CountIndex_Wraps` (cell-asserted highlights,
      `n of m`, wrap).
- [ ] §9: `ReplaceTests.RegexDotNet_GroupReferences_DollarOne` (.NET regex, `$1`, replaced-count shown).
- [ ] §9: `FindTests.MatchInsideHiddenMarks_ForceRevealsAndScrolls`;
      `FindTests.FindsSourceSyntax_StarStar_And_Urls` (source-domain search).
- [ ] §9 [EDGE]: `ReplaceAllTableProtectionFuzz` — 10k randomized replace-alls over table-bearing docs;
      structure invariant or skip-with-warning; zero corruption.
- [ ] §11: `OutlineTests.LiveUpdate_Debounced`, `ClickNavigates_CaretAtHeading`,
      `CurrentSectionTracksCaret`, `EmptyState_And_FrontMatterExcluded`.
- [ ] §12: `AutosaveTests.JournalWrittenWithin5s_FakeClock`;
      `RecoveryTests.KillDashNine_RelaunchOffersRestore` (out-of-proc, `Category=Integration` — §5.7
      nightly-integration lane; milestone exit reads that lane's latest run); restore
      byte-exact; discard/clean-save deletes journal; real file never touched;
      `AtomicWrite_TempThenRename` torn-write test.
- [ ] §10.3: `ExportTests.Html_Standalone_RendersPinnedPipeline`,
      `PlainText_StripsMarks_TablesReadable`; copy-as via `IClipboardService`; Export▸PDF absent from
      every surface.
- [ ] §10.1: `FileDialogTests.Open_SaveAs_Breadcrumb_OverwriteConfirm` on the `IDialogFileSystem` fake.
- [ ] Recents: `RecentFilesStoreTests.PersistRoundTrip_MRU_Cap`; Backstage shows recents; Q6 decision
      recorded in docs (§3.2 resolution 8).
- [ ] Promotability: `DialogsProject_HasNoMarkdigOrEditorReference` still green.
- [ ] Benchmark: `Benchmark_TypingWithFindBarOpen_10kDoc` < 16 ms/frame with ~500 live match highlights.

**Work packages:**
1. **WP0 — Journal atomicity + focus-trigger spike (2 days, first).** Prove write-temp→fsync→rename
   survives mid-write kill (torn-file injection); prove `InputDispatcher.TerminalFocusChanged` (public,
   DECSET 1004, gated on `ProtocolCapabilities.FocusEvents`) fires under UITestHost via
   `SendBytes("\x1b[O"u8)`.
2. **WP1 — TaskDialog + MessageBox (FB-12) in `CursorialEdit.Dialogs`.** `TaskDialog : Window` per the
   agreed shape: main instruction, content, severity icon via `Icon`, command-link buttons, verification
   checkbox, expandable details; `ShowAsync` via `ShowDialogAsync<TResult>` (OCE-wrapped). It becomes the
   `ITaskDialogService` implementation — M1/M3/M4 callers unchanged (§3.2 resolution 3). `MessageBox`
   stays as thin convenience. Theme via `ThemeKeys` overlay. *First, because WP2/WP4/WP8 and the
   dirty-close triad consume it, and FW-B inherits its vocabulary.*
3. **WP2 — File dialogs (§10.1) + the FB-18/FB-19 framework controls.** *Framework-side first
   (Cursorial repo, FB-2 delivery pattern, consumed via `$(UseLocalCursorial)`):* **`Breadcrumb`**
   (FB-18, accepted 2026-07-03 — general-purpose path-segment items control: per-segment invoke,
   overflow/ellipsis at narrow widths, keyboard segment navigation, editable-path swap per the mockup)
   and **`ListView`** (FB-19, accepted 2026-07-03 — WPF-style columned selector over the existing
   `SelectingItemsControl`/`ItemContainerGenerator` infra: pluggable GridView-analog column definitions,
   header row, cell-measured column widths, virtualization-compatible, `:alternate` striping), each with
   framework tests on house patterns. *Then app/Dialogs side:* `OpenFileDialog`/`SaveFileDialog : Window`
   per the colorpicker-filedialogs mockup: `Breadcrumb` path bar, places rail, `ListView` details list
   (name/size/modified), filename `TextBox`, extension filter, overwrite confirm via TaskDialog;
   filesystem behind `IDialogFileSystem` (enumerate/stat only). App side: wire M5's
   `File.Open`/`File.SaveAs` stubs via `CommandRegistry.Attach`; dirty-close triad through the service.
4. **WP3 — `AppStatePaths` + `RecentFilesStore` finalization (§3.2 resolutions 7–8).** Journals →
   platform state dir; settings → FW-A store; `recent.json` MRU cap 15, canonicalized paths, pruned on
   missing-file display; bound into Backstage Recent + File menu. Q6 decision recorded for maintainer
   sign-off.
5. **WP4 — `AutosaveService` + crash recovery (§12).** Triggers: 5 s `UITimer` debounce after last
   `Apply` (restart-on-edit) + `TerminalFocusChanged(false)` where negotiated. Snapshot = line-array copy
   stamped with `Document.Epoch`; serialize+write off-thread, epoch-validated post-back, skip if newer
   epoch already journaled. Journal = header (schema version, path or untitled GUID, timestamp, BOM flag,
   ending preservation, checksum) + content; filename = SHA-256 of the realpath-canonicalized path;
   atomic per WP0. Clean save/close deletes. On launch/open: journal newer than file mtime (or any
   untitled journal) → TaskDialog Restore/Discard; restore loads as dirty. Autosave never clears the
   dirty dot, never writes the real path. Replaces the M1 stub (§3.2 resolution 6).
6. **WP5 — `FindModel` + `FindBar` (§9 find half).** Bar docked in the shell `Grid`, Esc-dismiss
   returns caret to the document (`TextBox` + toggles — sanctioned reuse). Search domain = source: pooled
   `"\n"`-joined snapshot rebuilt per `Document.Epoch` (regex may span lines; offsets mapped back through
   prefix sums). Options: case, whole-word, .NET regex (`Multiline`, `MatchTimeout` 250 ms; invalid
   pattern → inline error state). Incremental: one-frame debounce, off-thread for large docs,
   epoch-validated. Matches land in the `AnchorTable` (shift under edits); presenters paint highlights by
   intersecting the match set through run maps — the selection mechanism, covering `HiddenMark` runs;
   navigating to a hidden-mark match force-reveals + scrolls. Keymap (FB-14 posture): Ctrl+F/F3 safe
   everywhere; Shift+F3 Kitty-only (legacy drops it) → always-on alternates Ctrl+N/Ctrl+P in-bar + ◀▶
   buttons; replace-row toggle Alt+R (Ctrl+H is Backspace on legacy — Kitty-only extra binding).
7. **WP6 — Replace + replace-all.** Both funnel through `EditController.Apply` — matches applied in
   descending offset order inside one sealed undo group. Regex replacement via `Match.Result`. **Table
   protection [EDGE]:** classify each match against the `BlockList`; inside a `TableModel` cell span,
   transform the replacement (pipes → `\|`, newlines → space, using M3's exported escape helper); a match
   crossing a cell/pipe boundary or touching the delimiter row is skipped and tallied ("n replaced, m
   skipped to protect table structure"). Windowed ≡ full equivalence over replace-all scripts (harness
   reuse); `ReplaceAll_SingleUndoGroup_RestoresCaret`.
8. **WP7 — Outline panel (§11).** `OutlineModel` rebuilt from `BlockList` headings on `BlockListChange`,
   ~200 ms debounce; heading text realizes inlines lazily (Decision 5 sanctions); front matter excluded;
   setext included as H1/H2. View: `TreeView` + `HierarchicalDataTemplate` in a `Grid` column behind
   M5's View▸Outline toggle (via `Attach`), `GridSplitter`. Click → scroll + caret at heading (focus back
   to `EditorControl`); current-section tracking via `SetCurrentValue` + reentrancy guard; empty state.
9. **WP8 — Export + copy-as (§10.3).** `ExportService` (Document project — Markdig allowed): HTML via
   Markdig `HtmlRenderer` over the same pinned pipeline/AST, standalone template (title = first H1 or
   filename, inline minimal CSS); plain text via AST walker (lists keep markers; tables as padded grids
   using `TableModel` widths). Save through WP2 dialog; copy-as → `IClipboardService` OSC 52 (fallback:
   internal store + status-bar notice). Export▸PDF hidden everywhere. Golden-file tests over the dialect
   corpus.

**Framework items:** FB-12 contributed here (promotion post-v1 mechanical); FB-14 consumed (no new
framework ask — gaps covered by alternates); FB-2/FB-3 consumed; FB-4 verified by the out-of-proc kill
test (swap in `EmergencyRestoreBytes` if upstreamed); FW-B begins framework-side (§4.2).

**Milestone tests beyond the gate:** journal round-trip fuzz over mixed LF/CRLF + BOM docs (byte-identical
restore); find-all on 1 MB off-thread with no frame overrun; autosave snapshot copy < 1 frame; 1k-heading
outline rebuild within debounce budget; NoColor find-highlight/outline-node via reverse-video/weight;
TaskDialog severity resolves through the Icon ladder without tofu.

**Risks:** regex pathology → `MatchTimeout` + off-thread + bar warning state; snapshot churn at 1 MB →
pooled builder, rebuild only while find active, per-line fallback when the pattern can't span lines;
journal torn writes/key collisions → WP0 spike, checksum header (unreadable journal = absent + notice),
realpath keys, GUIDs for untitled; no focus events on legacy → 5 s debounce is the guaranteed path,
focus-loss is an accelerator; TreeView not end-to-end virtualized → 1k-heading benchmark, fallback
indent-styled virtualized `ListBox` over the same `OutlineModel`; replace-all vs reveal/caret mid-flight
→ one sealed group, presenter reconciliation once at group end.

**Must NOT build:** split view/raw-pane sync, §15 Options UI + capability page (M7 — WP3 provides only
persistence substrate), image swap/FB-13 (M7), §13 perf passes beyond M6's own gates (M7), PDF export /
find-in-selection / palette / outline drag-reorder (deferred).

**Demo checkpoint:** per §5.6 M6 row.

---

## 12. M7 — Split view, inline images, performance, configuration, ship

**Objective.** The last feature tranche (split view §4.2, capability-gated inline images §7.2,
configuration §15 — re-scoped to consume the FB-17 layer per §3.2 resolution 9), the §13 performance
numbers, and the v1.0 definition-of-done sweep. Closes the residual tails of **R3** (final §13 table
numbers across presets) and **R5** (band contract at 10k-line scale), and retires the **FB-13**
stale-image hazard (framework-side blocker, fixed first).

**Exit gate — all mechanically checkable:**
- [ ] **FB-13 fixed in Cursorial:** `SceneFragmentClearTests.ClearToTransparent_DropsFragments` +
      `ScenePool_Rent_DropsStaleFragments` pass; `FragmentPassthroughTests` still green.
- [ ] §4.2: `SplitViewTests.EditEitherPane_PropagatesLive_OneUndoStack`,
      `ScrollSync_NoFeedbackOscillation`, `ModeToggle_CaretPreserved`; vertical default + horizontal
      option demoed.
- [ ] §7.2: image swap matrix green under `KittyGraphics`/`SixelGraphics`/`ITerm2Graphics` —
      `ImageSwapTests.Withdraw_OnCaretEnter_Selection_RawMode_ScrollClip` (byte-asserted: zero image
      bytes post-withdraw on all profiles), `Sixel_TeardownRepaintsPlaceholderCells`,
      `BrokenLocalImage_WarnTint_NoCrash`, `RemoteUrl_PlaceholderOnly`, `NoProtocol_SwapLogicDisabled`.
- [ ] §13 benchmarks: keystroke render < 16 ms median / 50 ms ceiling on the reference doc **and** inside
      a 40×20 wrapped table; 10k-line fling within frame budget; startup-to-editable < 500 ms;
      instrumentation asserts **zero synchronous full-document reparses on the keystroke path** and
      **zero full-table reflows** across the fuzz edit script. The script *includes* link-ref/footnote
      definition-set edits (to exercise the scheduler): `FullReparseScheduler`-sanctioned debounced
      off-thread reparses (Decision 3/13) are counted separately and asserted to fire **only** on
      definition-set edits — they are sanctioned machinery, not gate failures.
- [ ] §10.2: `LargeFileTests.SecondaryPane_IdleRerender_Past10k`; limits documented.
- [ ] §15: every listed setting present in the (framework-hosted) Options surface, persisted, observably
      wired (one test per setting group) — **except font size/zoom, excluded per §15's "host-permitting"
      clause: a terminal host cannot resize fonts; the exclusion is recorded in the WP8 tracking
      matrix**; capability declaration forces classes with no restart and
      survives `RenegotiateAsync` (`CapabilityOverrideTests.Override_SurvivesRenegotiation` — through the
      FW-A seam).
- [ ] §18 sweep: full acceptance under `KittyTruecolor`/`Ansi16Legacy`/composed Ansi256/NoColor;
      no-info-by-color-alone audit; Icon PNG tier and content images verified single-backend; the PNG
      tier exercised with wired assets (`IconLedgerTests.PngTier_ResolvesUnderImagesCapableNoNerdFont`,
      WP4) **or** the procurement-slip disposition recorded in the tracking matrix (Unicode floor
      shipped, ledger rows honestly `needed`/`png-requested`).
- [ ] **Definition of done:** every §2–§13 [IN] checkbox ticked in the tracking matrix; final
      `docs/conformance.md` ships; `DogfoodTests.OpenEditSave_CursorialDocs` round-trips byte-clean on
      untouched regions.

**Work packages:**
1. **WP0 — FB-13 fragment-clear fix (Cursorial repo, FIRST — hard blocker).** Clear the scene buffer's
   fragment table in `Scene.ClearToTransparent` (parity with `CellBufferView.Clear`'s intersecting-
   fragment drop) and in `ScenePool.Rent`; add the two regressions. Timebox 2 days to a scoping verdict;
   escalate to Mike if not the expected parity change; image display is feature-flagged so all non-image
   M7 work proceeds regardless. Land **FB-10** (opaque `#00FFFF` → `#00FEFF` nudge in
   `MedianCutQuantizer`) in the same pass; verify decode always constructs RGBA fragments (pre-encoded
   payloads are unclippable).
2. **WP1 — Perf instrumentation (before features).** Counters on `EditController`/parser (window
   line-count, fast-path hits, full-reparse count), `DocumentPanel` (zones re-rastered/frame),
   `TablePresenter` (columns re-measured), keystroke stopwatch, startup timer — queryable from tests
   (extending M1.WP7/M2.WP13's counters). The full-reparse counter is split: **synchronous
   keystroke-path full reparses** (asserted == 0 by the typing script) vs
   **`FullReparseScheduler`-sanctioned debounced reparses** (tracked separately; asserted to fire only
   on definition-set edits, which the script deliberately includes).
3. **WP2 — Split view.** `SplitViewHost` = `Grid` + `GridSplitter`, `Orientation` (vertical default),
   two `EditorView`s (§3.2 resolution 12) over one `DocumentBuffer`/`BlockList`/`EditController`/undo
   stack; `BlockListChange` fans out. Scroll sync: master pane's first visible source line mapped through
   the peer's height prefix sums, one-frame debounce, `_syncing` guard. Focus decides which pane
   publishes the terminal caret. Wire M5's View▸Toggle Split command. Images stay placeholder-only in
   the raw pane (inert rule 3).
4. **WP3 — `IImageBackend` + swap engine.** App-side wrapper over the sanctioned path (cached
   `Cursorial.Rendering.Content.Image` via `RenderContext.DrawContent`); mechanism differences (occlude
   hide vs cohabit repaint; clip vs withdraw) selected off resolved `caps-image-occlusion`/
   `caps-image-clipping`, never protocol names. `ImageInertnessTracker` evaluates rules 1–4 against
   `ActiveBlockId`, selection, view mode, viewport clip; withdraw immediate, swap-back debounced one
   frame. `ImageChipPresenter` gains the paint path: single-row chip footprint invariant, max height
   `[CAP-tunable]`, never changes line count. Decode off-thread, epoch-validated; local paths only;
   failure → `md-image-broken` + swap permanently disabled for that image.
5. **WP4 — Icon-tier backend unification check + PNG asset wiring.** Verify the framework Icon PNG tier
   and WP3 share one transmit/clip/teardown implementation (the fragment layer) — the app owns only swap
   *policy*; test asserts both paths emit through the same fragment type (§18.4 "single backend").
   **Wire the maintainer-procured PNG assets** (requested at M5.WP9) into `EditorIcons`' `ImageUri` seam;
   flip ledger rows `png-received → wired`; add
   `IconLedgerTests.PngTier_ResolvesUnderImagesCapableNoNerdFont` (caps-images present, `caps-nerdfont`
   absent → Icon resolves at the PNG tier, not the Unicode floor). **Escalate to Mike at M7 start if
   assets are missing**; slip fallback = ship the Unicode floor (spec-legal — §18.4's ladder still
   resolves), record rows at their true status, and document the §18-acceptance-3 PNG-tier disposition
   in the WP8 tracking matrix so the checkbox is honest either way.
6. **WP5 — Performance passes to §13.** `KeystrokeLatencyBenchmark` (reference doc + 40×20 wrapped
   table), `ScrollFlingBenchmark` (10k lines; band re-anchor cost; a with-images variant proving
   withdraw-on-scroll protects the SU/SD fast-path per FB-9), `StartupBenchmark` (<500 ms). Fix hot
   spots; revalidate the band convention under `SendResize`/thumb-drag storms — closes R5's tail; press
   FB-16 with the numbers.
7. **WP6 — §10.2 large-file degradation.** Past 10k lines the secondary split pane re-renders on idle
   (UITimer debounce); status-bar notice on open; limit documented. Primary-pane latency unchanged at
   15k lines.
8. **WP7 — Configuration surface (§15) — re-scoped per §3.2 resolution 9.** `OptionsModel` persists
   through the FW-A store (atomic writes are the store's job). The editor registers its §15 pages in the
   **FW-B framework Options dialog**: theme (via `ThemeVariantKey`), editing (auto-pair per char, smart
   quotes, indent, list continuation), save (endings, trailing newline, encoding), tables (overflow
   mode/max width/zebra → `TableModel`/`:alternate`), autosave (interval/enabled → M6 service), view
   defaults (surface, rendered-vs-split, outline state, split orientation). Capability declaration =
   the framework dialog's advanced tab writing the FW-A `CapabilityOverrides` seam; all app capability
   reads go through the resolved set (never raw `app.Capabilities`). **Fallback if FW-B slipped:**
   editor-owned Backstage Options page + integration-notes §7 recipe (the M7 plan's original scope).
9. **WP8 — Acceptance sweep, conformance doc, dogfood.** Tracking matrix of every §2–§13 + §18 [IN]
   checkbox with owning test (seeded from Appendix A; records the §15 font-size exclusion and the PNG
   procurement disposition); final conformance doc from the Decision-14 harness; the user-facing
   normalization changelog `docs/normalizations.md` (M4.WP10) verified current against the shipped
   `SaveNormalizer` policies (§2 acceptance 2); scripted dogfood test over real Cursorial docs.

**Framework items:** FB-13 + FB-10 contributed (WP0); FB-5 subsumed by FW-A (consumed, not
worked-around, unless Stage A slipped); FB-16 pressed with WP5 data; FB-9 sidestepped
(withdraw-on-scroll; post-v1); FB-11 placeholder fallback is spec-legal — contribute paletted-PNG decode
only if dogfood shows high breakage (stretch).

**Milestone tests beyond the gate:** byte assertions as the withdraw oracle (captured output contains the
profile's teardown — Kitty placement delete / sixel cell repaint — and no image payload); split-view
fuzz variant (both panes consistent under fuzzed edits); capability-override tests flip classes
mid-session; stale off-thread decode raced under the fake clock.

**Risks:** FB-13 exceeds the parity one-liner → day-one timebox + escalation + feature flag; scroll-sync
oscillation → guard + debounce + convergence-within-2-frames storm test; FB-9 fast-path loss with images
on screen → withdraw fires before the fling; benchmark variant proves it; fallback: scroll-in-progress as
an explicit swap trigger; sixel clipping silently unavailable → RGBA-only decode path + viewport-edge
clip test; capability-override desync → FW-A seam survives renegotiation by design (that was the point);
benchmark flake → §5.3 discipline (medians, pinned doc, run-twice).

**Must NOT build:** iTerm2 inline display, remote fetch, multi-row images, resize handles, click-to-zoom,
decode cache (§14); FB-9 placement-id reuse (post-v1); launch-time/env capability overrides (§18.1
[DEFER]); command palette; autosave-to-real-file; `caps-ascii` floor.

**Demo checkpoint:** per §5.6 M7 row — the ship gate.

---

## 13. Tracking appendix

- **Acceptance tracking matrix** (created M7.WP8, maintained from M2): every spec [IN] checkbox → owning
  test/artifact → milestone. The §16 definition of done is evaluated against this matrix.
- **FB index by milestone:** M1 files FB-16/N1, decides FB-1, workarounds FB-4; M2 contributes FB-14.1;
  M4 contributes FB-14.2/14.3; M5 contributes FB-2; M6 contributes FB-12 (in-repo, promotable); M7
  contributes FB-13 + FB-10; FW-A implements FB-5 + FB-15 + the FB-17 store; FW-B implements the FB-17
  UI. FB-3/6/7/8/9/11 are consumed/sidestepped/tracked as noted per milestone.
- **Open items for the maintainer** (carried from the plans, non-blocking): **Q4 gates M4.WP10's
  setext→ATX / indented→fenced rewrites** — implemented behind a posture flag whose conservative default
  (rewrite only user-edited blocks; untouched regions byte-exact) ships pending the §17 answer, while
  bullet-marker→`-` is unconditional per §2.1 [DECISION]; M3's column-window focus policy (risk e) and
  M4's plain-paste-still-escapes-cells micro-decision are flagged for ratification in their milestone
  reports; Q6 is answered by FB-17 + §3.2 resolution 8, recorded in M6; PNG icon nominal sizes are
  pinned in M5.WP9, where the procurement request goes out (`needed → png-requested`); M7.WP4 wires
  received assets or records the slip disposition.

---

## Appendix A — Spec-acceptance coverage matrix (§2–§13 + §18 [IN] checkboxes)

Reconstructed at review (2026-07-03) from the feature spec's per-section acceptance lists, incorporating
the coverage audit's counts and the review fixes (Appendix B). **56 criteria total** (§2:4, §3:4, §4:3,
§5:6, §6:4, §7:8, §8:4, §9:4, §10:4, §11:3, §12:4, §13:3 = 51 in §2–§13, plus §18:5). After the review
fixes: **56/56 owned** (pre-review: 53 covered, 3 orphaned/partial). M7.WP8's tracking matrix is seeded
from this table and carries the two recorded dispositions (§15 font-size exclusion; PNG procurement).

### §2 Dialect & rendering scope

| # | Criterion (abridged) | Owner | Gate evidence |
|---|---|---|---|
| 2.1 | Every *Rendered* construct displays formatted and round-trips without corrupting unrelated content | M2 (core), M4 (extensions) | `RenderRoundTripTests`, `SpanOracleTests`, `DialectConformanceTests` |
| 2.2 | [DECISION] normalizations applied on save + documented in user-facing changelog | M3.WP11 (table padding), **M4.WP10** (bullet→`-`; setext/indented per Q4 flag; `docs/normalizations.md`), M7.WP8 (ships final) | `SaveNormalizerTests.*` (M4 gate); changelog checked in |
| 2.3 | Unsupported constructs never crash; dimmed literal | M2.WP7 | `FallbackPresenterTests` (M2 gate) |
| 2.4 | Conformance document listing passing spec sections | M2 (created), M4 (extended), M7 (final) | `ConformanceReportTests` → `docs/conformance.md` |

### §3 Editing model

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 3.1 | Cursor traverses rendered text, skips hidden marks except active line | M2.WP8 (M1.WP6/WP8 substrate) | `RunMapTests`, `RevealTests.CaretStaysOnGrapheme`, `CaretTests.*` |
| 3.2 | Copy of formatted selection yields correct markdown source | M1.WP9, M2.WP8 | `SelectionTests.Copy_YieldsExactSourceRange` (M1 gate) |
| 3.3 | Typed sentence undoes as one group; next undo removes prior group | M1.WP5 | `UndoGroupTests.*` (M1 gate) |
| 3.4 | Smart paste renders markdown; plain paste does not | M4.WP9 | `SmartPasteTests`, `TableCellPasteEscapingTests` (M4 gate) |

### §4 WYSIWYG duality

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 4.1 | Reveal/hide marks with no reflow of other lines | M2.WP6/WP9 | `RevealTests.*` (M2 gate; R1 retired) |
| 4.2 | Raw toggle shows colored source; back at same cursor position | M2.WP10 | `RawModeTests.CaretPreservedAcrossToggle` (M2 gate) |
| 4.3 | Split view scroll-synced, live edits both ways, one undo stack | M7.WP2 (on M2.WP7's `EditorView`) | `SplitViewTests.*` (M7 gate) |

### §5 Tables

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 5.1 | Picker insert, type into cells, Tab-to-new-row, borders aligned | M3.WP10 | `TableCreationTests.PickerInsert_TypeCells_TabCreatesRow` |
| 5.2 | Every §5.3 op via keyboard **and** menu; valid GFM | M3.WP7 (keyboard), M5.WP4/WP6 (menu/ribbon) | `TableOperationTests` + `TableToolsTests.OperationsFromRibbonProduceValidGfm` |
| 5.3 | Typing widens column; borders redraw; surrounding lines undisturbed | M3.WP5 | `TableReflowTests.WidenOnType_ShrinkOnDelete` + damage capture |
| 5.4 | Deleting widest content shrinks column back | M3.WP5 | `TableReflowTests` (`CountAtMax` path) |
| 5.5 | Column alignment round-trips | M3.WP1/WP11 | `TableRoundTripTests.AlignmentRoundTrips` |
| 5.6 | Wider-than-viewport behaves per configured overflow mode | M3.WP6 | `TableOverflowTests` (wrap/truncate/scroll at 40/80 cols) |

### §6 Block & inline operations

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 6.1 | Every command applies and round-trips to valid markdown | M4.WP4/WP5 | `CommandRoundTripTests` (M4 gate) |
| 6.2 | Typing shortcuts auto-apply | M2.WP12 (parse reflex), M4.WP6 (behavior) | `TypingShortcutTests` (M2+M4 gates) |
| 6.3 | List/quote continuation + empty-item termination | M4.WP6 | `ListContinuationTests` (M4 gate) |
| 6.4 | Ordered lists renumber on edit | M4.WP5 | `OrderedRenumberTests` (M4 gate) |

### §7 Links, images, footnotes, interactive

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 7.1 | Link dialog; URL visible on focus; Open Link explicit, never plain click | M4.WP7/WP8 | `LinkDialogTests`, `OpenLinkExplicitTests` |
| 7.2 | Image placeholders + alt text; broken paths warn, no crash | M4.WP8 | `ImagePlaceholderTests` |
| 7.3 | Image paints when inert; withdraws on caret/selection/scroll-clip/raw | M7.WP3 | `ImageSwapTests.Withdraw_OnCaretEnter_Selection_RawMode_ScrollClip` |
| 7.4 | Withdraw leaves no artifact across profiles (sixel repaint / Kitty placement delete / iTerm2 placeholder-only) | M7.WP3 | byte-asserted withdraw oracle, `Sixel_TeardownRepaintsPlaceholderCells` |
| 7.5 | Decode/transmit never blocks keystrokes; remote URLs placeholder-only | M7.WP3/WP5 | `RemoteUrl_PlaceholderOnly` + with-images fling benchmark |
| 7.6 | No protocol → placeholders only, swap logic disabled | M7.WP3 | `NoProtocol_SwapLogicDisabled` |
| 7.7 | Footnote activation navigates to definition and back | M4.WP7 | `FootnoteNavigationTests` |
| 7.8 | Checkbox toggle edits source and updates rendering | M4.WP7 | `CheckboxToggleTests` |

### §8 Command surface

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 8.1 | All §8.1 commands exist, bound once, on active surfaces (incl. Insert ▸ Special character — M4.WP8; View ▸ Toggle word-wrap — M2.WP5 flag + M5.WP4 command) | M4.WP3 (registry), M5.WP3/WP4/WP5 | `CommandInventoryTests.AllSpec81CommandsRegisteredExactlyOnce`, `SpecialCharacterInsertTests`, `WordWrapToggleTests` |
| 8.2 | Table Tools contextual group only when caret in table | M5.WP6 | `TableToolsTests.ContextualTabVisibleOnlyWhenCaretInTable` |
| 8.3 | Toolbar↔Ribbon switch preserves behavior, no duplicated logic | M5.WP8 | `SurfaceSwitchTests.BoldViaToolbarRibbonAndMenuProduceIdenticalEdits`, `SurfaceParityTests` |
| 8.4 | Toolbar overflow collapses at narrow widths | M5.WP4 | `ChromeOverflowTests.NarrowResizeCollapsesToChevronAndItemsRemainInvocable` |

### §9 Find & replace

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 9.1 | Incremental find: highlights, count + index, wraps | M6.WP5 | `FindBarTests.IncrementalFind_HighlightsAllMatches_CountIndex_Wraps` |
| 9.2 | .NET regex find/replace with `$1` group references | M6.WP6 | `ReplaceTests.RegexDotNet_GroupReferences_DollarOne` |
| 9.3 | Find matches markdown source (`**`, URLs) | M6.WP5 | `FindTests.FindsSourceSyntax_StarStar_And_Urls`, `MatchInsideHiddenMarks_ForceRevealsAndScrolls` |
| 9.4 | Replace-all never corrupts table structure | M6.WP6 | `ReplaceAllTableProtectionFuzz` (10k scripts) |

### §10 Documents, files & export

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 10.1 | Open/save round-trips content, encoding, line endings | M1.WP11 | `FileRoundTripTests` (M1 gate) |
| 10.2 | Dirty indicator + save prompt correct | M1.WP10/WP11, M6.WP2 (dialogs) | `DirtyStateTests.SavePrompt_ShownOnCloseWhenDirty`, `FileDialogTests` |
| 10.3 | HTML + plain export correct; PDF present only if backend (hidden in v1 — verified none) | M6.WP8 | `ExportTests.*`; Export▸PDF absent from every surface (M6 gate) |
| 10.4 | 10k-line responsive; beyond degrades gracefully, documented | M1/M7 benchmarks, M7.WP6 | `LargeFileTests.SecondaryPane_IdleRerender_Past10k`; limits doc |

### §11 Outline

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 11.1 | Outline reflects heading structure, live debounced updates | M6.WP7 | `OutlineTests.LiveUpdate_Debounced` |
| 11.2 | Click navigates; current section highlighted | M6.WP7 | `OutlineTests.ClickNavigates_CaretAtHeading`, `CurrentSectionTracksCaret` |
| 11.3 | Toggle from View menu/ribbon | M5.WP3 (stub) + M6.WP7 (`Attach`) | `OutlineTests` + `CommandInventoryTests` |

### §12 Autosave & recovery

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 12.1 | Edits journaled within interval; kill + relaunch offers recovery | M1.WP12 (stub), M6.WP4 | `AutosaveTests.JournalWrittenWithin5s_FakeClock`, `RecoveryTests.KillDashNine_RelaunchOffersRestore` (§5.7 lane) |
| 12.2 | Restore yields exact content; discard removes journal | M6.WP4 | `RecoveryTests` byte-exact restore (M6 gate) |
| 12.3 | Clean save/close removes journal; real file never written by autosave | M1.WP12, M6.WP4 | `AutosaveJournalTests` + M6 gate items |
| 12.4 | Recovery writes atomic | M6.WP0/WP4 | `AtomicWrite_TempThenRename` torn-write test |

### §13 Performance

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 13.1 | Typing in large doc and wide table within latency targets | M1→M7 cumulative benchmarks | `TypingLatencyBenchmark` (M1/M2), `TableTypingBenchmark` (M3), `KeystrokeLatencyBenchmark` (M7 final) |
| 13.2 | 10k-line scroll smooth (virtualized) | M1.WP3/WP7, M7.WP5 | zero in-band re-raster assert (M1), `ScrollFlingBenchmark` (M7) |
| 13.3 | No full-doc reparse / full-table reflow per keystroke (instrumented) | M2.WP13, M3.WP5, M7.WP1 | `ParseInstrumentationTests.NoFullDocumentReparsePerKeystroke`; M7 split sync-vs-scheduled counters |

### §18 Capability gating

| # | Criterion | Owner | Gate evidence |
|---|---|---|---|
| 18.1 | [CAP] behavior degrades via `caps-*` selectors; forced class re-renders without restart | M2.WP11 (+FW-A seam) | `CapsMatrixTests` override item (M2 gate — the FW-A first-consumer proof), `CapabilityOverrideTests.Override_SurvivesRenegotiation` (M7) |
| 18.2 | Each color tier renders its palette; NoColor conveys nothing by color alone | M2.WP11, M7 sweep | `CapsMatrixTests` NoColor asserts; M7 no-info-by-color-alone audit |
| 18.3 | Icon resolves NF → PNG → Unicode; absent `caps-nerdfont` shows no tofu | M4.WP2/M5.WP2 (factory), **M7.WP4** (PNG wiring) | `IconLedgerTests.NoTofuWhenNerdFontAbsent` (M5), `IconLedgerTests.PngTier_ResolvesUnderImagesCapableNoNerdFont` (M7, or recorded slip disposition) |
| 18.4 | Icon PNG tier and §7.2 images share one backend | M7.WP4 | single-fragment-type assertion (M7 gate) |
| 18.5 | Unrecognized terminal fully usable after manual capability declaration | M7.WP7 (FW-A/FW-B) | capability declaration page tests; override tests flip classes mid-session |

---

## Appendix B — Review resolutions (2026-07-03 three-lens review)

Every blocker/major finding and its resolution; minors listed with their dispositions. No findings were
silently dropped; none were rebutted — all 23 were applied (one, B-14, applied in modified form, noted).

### Majors (8 — all applied)

| # | Lens / where | Finding | Resolution |
|---|---|---|---|
| B-1 | coverage / §2 acceptance 2 | Save normalizations (bullet→`-`, setext→ATX, indented→fenced) and the user-facing changelog had no owning WP; M4's demo claim was a phantom | **New M4.WP10**: bullet→`-` unconditional; setext/indented behind a Q4 posture flag (conservative default); creates `docs/normalizations.md`; `SaveNormalizerTests` added to the M4 gate; M7.WP8 verifies the changelog; tracking-appendix Q4 line now names M4.WP10 as gated |
| B-2 | coverage / §8.1 Insert ▸ Special character | Command in no WP; M5's literal-manifest inventory test would fail or pass dishonestly | Added `SpecialCharacterDialog` to **M4.WP8** (grid/list picker over `ShowDialogAsync`, TableSizePicker/LanguagePicker patterns), registered as `Insert.SpecialCharacter`; `SpecialCharacterInsertTests` added to the M4 gate |
| B-3 | coverage / §8.1 View ▸ Toggle word-wrap | No milestone implemented a non-wrapped mode; layout-touching behavior would surface at M5 test time | Semantics decided now (wrap-off = one visual row per logical line via the R1 slide; document never scrolls horizontally): `WrapMode` flag added to **M2.WP5**, command in **M5.WP4**, `WordWrapToggleTests` added to the M5 gate |
| B-4 | coverage / §18 acceptance 3 (PNG tier) | PNG rows never moved past `needed`; no procurement checkpoint; NF→PNG→Unicode ladder unverifiable at M7 | Procurement request anchored at **M5.WP9** (`needed → png-requested`); **M7.WP4** wires assets, flips rows to `wired`, adds `IconLedgerTests.PngTier_ResolvesUnderImagesCapableNoNerdFont`; escalation at M7 start + honest slip disposition defined (Unicode floor, recorded status) in the M7 §18 gate item |
| B-5 | sequencing / §1 vs §5.1 CI | Gate items lived in opt-in `Category=Integration` tests and nightly 100k fuzz with no defined CI lane — "green in CI" was mechanically unsatisfiable | **New §5.7** defines per-PR + nightly-integration lanes; §1's done-definition now reads both; M1 (PTY kill-TERM), M2 (100k fuzz), M6 (kill -9 recovery) gate items reference the lane explicitly |
| B-6 | sequencing / §3.2 res. 12 vs M2 WPs | Binding `EditorView` factoring (exists to prevent an M7 refactor) had no owning M2 WP | **M2.WP7 owns it**: explicit scope + done-when (two `EditorView`s over one buffer in a UITestHost smoke test, render-only); resolution 12 names the owner |
| B-7 | sequencing / M7 gate + WP1 | "Zero full reparses" contradicted the sanctioned `FullReparseScheduler` (definition-set edits legitimately trigger debounced reparses) | Gate + WP1 reworded: **zero synchronous full reparses on the keystroke path**; scheduler-sanctioned debounced reparses counted separately, asserted to fire only on definition-set edits; the fuzz/typing script explicitly includes definition edits |
| B-8 | framework-fit / M2.WP11 | `MdTheme.xaml` merged over `CursorialDefaultTheme.LoadTheme()` — but Cursorial.UI.Themes is unpackaged (FB-7, `IsPackable=false` verified), so WP11 was unbuildable in default package mode | Reworded: app-owned dictionary assigned to `UIApplication.Theme`, layering over the code-first `CursorialTheme.BuiltIn` backstop (chain `Resources → Theme → BuiltIn`, verified at `UIApplication.Theme.cs`); `Cursorial.UI.Themes` Palette.xaml demoted to authoring reference only |

### Minors (15 — all applied)

| # | Lens / where | Resolution |
|---|---|---|
| B-9 | coverage / M7 §15 gate vs WP7 | Font-size "host-permitting" exclusion now annotated on the M7 gate item and recorded in the WP8 tracking matrix |
| B-10 | coverage / §4.2 vs spec §18.4 [DEFER] | Traceability note added to §4.2: the width-ruler tester deliberately supersedes the spec [DEFER] per accepted FB-17 review note 6 |
| B-11 | sequencing / M1 WP10↔WP11 | Swapped: MessageBox port (now WP10) precedes file open/save (now WP11) whose save prompt consumes it; §3.1 and M1 framework-item references updated |
| B-12 | sequencing / §3.1 vs M5 FB-2 gate | M5 gate item now states the interim-`MenuItem` mitigation covers in-milestone progress only; FB-2 landing is a deliberate hard exit dependency with a named escalation path |
| B-13 | sequencing / M1 raster counters | M1.WP7 now explicitly builds the app-side raster/render-call counters its own gate asserts; M2.WP13/M7.WP1 extend rather than create |
| B-14 | sequencing / demo rows in M2–M5 gates | Demo checkboxes **removed** from gate lists (finding's first option); demos remain mandatory via §5.6 + §1's demo clause — gates now contain only mechanical items |
| B-15 | sequencing / M4-vs-M5 icon ownership | New **§3.2 resolution 13**: M4.WP2 creates `EditorIcons` with only its ledger rows; M5.WP2 extends the same factory — one owner, no re-sourcing |
| B-16 | sequencing / M7 PNG wiring | Same fix as B-4 (M5.WP9 procurement anchor + M7.WP4 wiring + escalation) |
| B-17 | sequencing / §5.3 benchmark variance | Pinned runner profile added to §5.3; per-PR gate is self-relative vs a checked-in baseline; absolute numbers verified at milestone exit on the reference profile |
| B-18 | sequencing / FW-A/FW-B gate coupling | Both stage gates split: framework-side fixtures close each stage independently; the "first-consumer verified" checkbox lives in the consuming editor milestone's gate (M2, M7) |
| B-19 | sequencing / §4 staffing assumption | Capacity model stated in §4: FW-A/FW-B interleave on the same implementing capacity; milestone durations include their framework items |
| B-20 | sequencing / M2 scope bulge | Mid-milestone checkpoint after WP6 (both spikes retired) + pre-authorized shed order (MiniHighlighter, then Ansi16 polish; never harness/fuzzer/reveal invariants) added to M2 |
| B-21 | framework-fit / M4 KeymapMatrixTests | Impossible negative ("Ctrl+Shift+X must not trigger Cut" on a byte-identical wire) reworded to three checkable assertions: no colliding registration on legacy, alternates fire, hints match the working binding per wire |
| B-22 | framework-fit / §2.1 Document row | `GraphemeWidth`/`GraphemeEnumerator` attribution corrected to Cursorial.Core (transitive, itself a published package); Cursorial.UI retained for `UITimer`/`UIDispatcher` |
| B-23 | framework-fit / M5 icon-width gate | Scope note added: the test validates layout-budget consistency and Unicode-floor picks (`⿻` U+2FFB measures 2); NF PUA codepoints measure 1 by construction, so NF rendering width is verified only by the FW-B width ruler |
