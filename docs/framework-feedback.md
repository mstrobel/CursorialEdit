# Cursorial framework feedback log

Running log of API/usability findings from building CursorialEdit, written from the app-developer's seat.
Each item: what we hit, why it matters, and a proposed change. Status values: `proposed`, `discussed`,
`accepted`, `implemented (local)`, `upstreamed`, `rejected`, `workaround-only`.

Disposition targets: **core** (Cursorial.* proper), **ext** (a Cursorial extensions companion package),
**app** (stays editor-local). All file references are to Cursorial HEAD as of 2026-07-03.

---

## FB-1 — Make `TextLayout` / `GraphemeLayout` / `TextNavigation` public — `implemented (local)` / retired (core)

`Cursorial.UI/Controls/TextEditing.cs` + `TextLayout.cs` contain exactly the machinery a custom text
surface needs — offset↔(line,column) mapping with soft-wrap affinity, grapheme-cluster boundaries,
`Locate`/`OffsetAt`/`IsLineEndBoundary`, word-boundary navigation — but all three types are `internal`.
The editor's WYSIWYG document surface must otherwise reimplement them on the public `GraphemeWidth`/
`GraphemeEnumerator` primitives. Members are already public-shaped; this looks like a visibility decision,
not a design gap. **Proposal:** promote the three types to `public` (possibly under
`Cursorial.UI.Controls.Text`), with the same API-stability caveats as the rest of the control layer.

**Decision note — R4 probe complete (M1.WP6, 2026-07-04). Recommendation: upstream-promote; the
maintainer decides.** Evidence from the probe (`CursorialEdit.Tests/Layout/TextNavigationProbeTests`
+ the checked-in report `CursorialEdit.Tests/Layout/text-navigation-probe-report.md`):

- **Parity was achieved, but only by re-deriving the internals wholesale.** The app-side
  `CaretNavigator` (423 lines, within the WP6 bound) reaches zero-divergence parity with a real
  multi-line `TextBox` on cluster steps, word motion, wrap segmentation, sticky cell-goal columns,
  and Home/End soft-wrap affinity — across CJK/emoji/ZWJ/VS16/combining fixtures on both wire
  presets. Getting there required mirroring, semantics-for-semantics, all three internal types plus
  `TextPresenter.MoveVertical`'s sticky-goal protocol: break-opportunity-after-whitespace-cluster,
  trailing-space-stays-on-row, WordWrap vs WordWrapOverflow over-long-word treatment, `Locate`'s
  `preferLineEnd` affinity resolution, the 4-clause `IsLineEndBoundary` aliasing predicate, and the
  word-landing boundary pin. Only `GraphemeWidth`/`GraphemeEnumerator` were consumable as public API
  — everything else is now duplicated logic whose sole drift tripwire is the probe suite.
- **What promotion saves M2 (and later):** M2.WP5's run-map layout is specified as "wrap/affinity
  via `TextNavigation` if FB-1 landed, else M1's classifier" — promotion deletes the fork; M2.WP8's
  word motion over concatenated visible text and every future wrap/affinity consumer (find-hit
  mapping, M7 split view) ride the same single source of truth as the `TextBox`es in dialogs and
  the find bar, so document-surface and field navigation can never disagree. On landing we delete
  the mirrored ~400 lines per the plan ("delete ours if it lands") and keep the probe as the swap's
  regression net.
- **Scope notes for the upstream PR:** `GraphemeLayout`/`TextNavigation` have no UI dependencies;
  `TextLayout` needs only `WrapMode` (`Cursorial.Rendering.Text`) — promotion under
  `Cursorial.UI.Controls.Text` per the proposal works, though hosting the trio beside `WrapMode`
  would let non-UI consumers (our Document project) use them without a UI reference. Keep the word
  classifier whitespace-delimited as-is (`TextBox` parity is the contract; the plan's
  "letters/digits vs punctuation" gloss does not match shipped behavior — see report §3.1); richer
  classifiers belong to consumers. Timing: before M2.WP5 starts renderer work, so run maps are
  born on the framework types.

## FB-2 — `MenuItem` doesn't participate in `BarCommandSync` — `discussed` (Bars)

Binding a `BarCommand` to a `MenuItem` executes and gates enablement, but `Header`/`Icon`/
`InputGestureText` do not auto-fill, and `MenuItem` toggles its own `IsChecked` instead of reflecting a
checkable command's state. The Bars pitch is "one command set, multiple surfaces"; menus are the surface
where it currently falls short — a Bold toggle shown on toolbar + menu drifts out of check-sync without
app glue. **Direction (agreed with Mike, 2026-07-03):** because `Cursorial.UI.Bars` layers on top of
`Cursorial.UI` (core `MenuItem` cannot reference `BarCommand`), the fix is a Bars-specific menu item —
e.g. `BarMenuItem : MenuItem` in `Cursorial.UI.Bars`, the same pattern as `BarButton`/`BarToggleButton`
over `ButtonBase`: auto-fill unset Header/Icon/gesture from the bound `BarCommand` via `BarCommandSync`,
command-owned check state via `ICheckableCommandParameter` with the self-toggle suppressed. The editor's
menu bar should use `BarMenuItem` throughout; possibly a `BarMenu`/menu-bar convenience alongside it.

## FB-3 — Clipboard is write-only; Ctrl+V is a consumed no-op — `proposed` (core)

`IClipboardService` sets the clipboard via OSC 52, but `TryGetTextAsync` always returns null (no OSC 52
read query), and there is no in-app clipboard fallback. TextBox consumes Ctrl+V/Shift+Insert as no-ops.
Paste only works through the terminal's own bracketed paste. For an editor this splits "paste" into two
inconsistent user paths. **Proposal:** (a) implement the OSC 52 read query (`ESC ]52;c;?`) where the
terminal permits it, surfaced through `TryGetTextAsync`; (b) add an app-internal clipboard store as
fallback so Copy/Cut→Ctrl+V round-trips inside the app even when the terminal denies reads; (c) let
TextBox paste from the service when available.

## FB-4 — `TerminalSessionOptions.EmergencyRestoreBytes` still missing — `proposed` (core)

Already recorded in Cursorial's own CLAUDE.md as a known gap; confirmed at HEAD. On SIGINT/SIGTERM the
session restores cooked mode + input opt-ins but does not leave the alt screen or re-show the cursor — a
signal-killed full-screen app strands the shell on the alternate screen. For an editor whose motto is
"never lose the user's work," exit hygiene is part of the story. **Proposal:** land the
`EmergencyRestoreBytes` seam (registered bytes written synchronously in the signal path: show cursor,
SGR reset, DECRST 1049). Until then the editor carries its own signal-path emergency write.

## FB-5 — No first-class per-axis capability override API — `proposed` (core)

Spec §18.1 says every capability resolves detect→user-override-wins. Deeper inspection found a workable
seam: `UIApplication.OnCapabilitiesChanged(TerminalCapabilities)` is public
(`UIApplication.Theme.cs:152`) and restamps `caps-images`/`caps-motion`/`caps-kitty-keyboard`/
`caps-image-*` through the normal path when handed a spoofed record
(`app.Capabilities with { ... }` — the Gallery's tier-cycling uses exactly this). But it's a side-door,
not an override API: (a) `UIApplication.Capabilities` keeps reporting the true negotiated record, so
stamped classes desync from the app-visible snapshot; (b) overrides do NOT survive `RenegotiateAsync` —
the app must re-apply in a `CapabilitiesChanged` handler; (c) the fan-out is styling-scoped — it can
cleanly force capabilities OFF, but forcing ON can't make the session negotiate a protocol it didn't
detect. Editing root `caps-*` classes directly remains non-viable (restamp replaces the whole subset).
**Proposal:** a first-class `UIApplication.CapabilityOverrides` (per-axis forced on/off/auto) folded into
stamping and surviving renegotiation — same semantics `RequestedColorTier`/`NerdFontAvailable` already
have. Alternative sanctioned shape: a capability-rewriting `ITerminalHost` decorator (works today via
`UIApplicationBuilder.WithTerminalHost` and survives renegotiation for free, but requires manual host
construction).

## FB-6 — Horizontal `ScrollBarVisibility.Auto` silently degrades to `Disabled` — `proposed` (core)

`ScrollViewer.cs:284-285, 337-339`: horizontal `Auto` (the default!) yields no horizontal scrolling at
all — with only a DEBUG diagnostic. `Hidden`/`Visible` work (wheel/keys/programmatic offsets). Surprising
default: content silently becomes unreachable. **Proposal:** either implement horizontal Auto (show bar on
overflow, no banding required for the horizontal axis) or change the degrade target to `Hidden` so the
axis still scrolls; the current `Auto→Disabled` is the least useful mapping. Editor uses `Hidden`
explicitly for table regions meanwhile.

## FB-7 — Publish `Cursorial.UI.Testing` (and the `Cursorial.UI.Themes` overlay) — `proposed` (packaging)

Both are `IsPackable=false`, so a NuGet consumer can't write headless UI tests (UITestHost is the only
test substrate) or use the XAML theme overlay. The editor will dev against local ProjectReferences, but
any third-party app author hits the same wall. **Proposal:** pack both at the next release; Testing is
arguably the more important one — it's the framework's biggest testability asset.

## FB-8 — Kitty graphics gated by terminal-family allow-list, no `a=q` probe — `proposed` (core)

Kitty graphics enable only for Kitty/Ghostty/Rio families; terminals implementing the protocol outside
the list (WezTerm, Konsole 22.04+) silently fall to sixel/none. Sixel, by contrast, is honestly
DA1-attribute-driven. **Proposal:** add the `a=q` runtime handshake to negotiation (probe + response
sentinel, same pattern as XTVERSION/DA1) so the capability reflects reality rather than the list.

## FB-9 — Kitty placement lifecycle: delete+retransmit only; overlays disable scroll fast-path — `proposed` (core / Rendering)

Fragments reposition by delete+retransmit (no placement-id `a=p` reuse / `a=d,d=i` hide), and any visible
Overlay fragment disables `FrameRenderer`'s SU/SD scroll optimization, so document scrolling with images
on-screen falls back to per-cell diff. Workable for v1 (spec withdraws images to placeholders during
scroll anyway), but placement-id reuse would make image-heavy documents scroll smoothly.
**Proposal (post-v1 priority):** placement-id-addressed reposition/hide on the Kitty path, and revisit
the overlay/scroll-detect interaction once placements can follow a scroll.

## FB-10 — Sixel `#00FFFF` key: nudge true-cyan content — `proposed` (core / Rendering, small)

The color-key transparency (alpha≤127 → `#00FFFF` key + `P2=1`) already ships — better than the docs
claimed. Remaining nit from spec §7.2: genuinely-cyan opaque pixels collide with the key. **Proposal:**
during quantization, remap opaque `(0,255,255)` → `(0,254,255)` (imperceptible ±1 nudge) so the key is a
pure internal reservation. Also note: clipping requires RGBA-constructed fragments — pre-encoded payloads
are unclippable; worth a doc note or an API hint.

## FB-11 — PNG decoder: paletted/grayscale/interlaced PNGs throw on the sixel path — `proposed` (core / Rendering)

`PngDecoder` handles 8-bit color types 0/2/3/4/6 non-interlaced for RGBA construction, but on the sixel
fragment path unsupported variants surface as `NotSupportedException` → placeholder fallback. Real-world
markdown embeds paletted PNGs constantly (screenshots, badges). **Proposal:** at minimum paletted (type 3
w/ tRNS) and grayscale expansion into the RGBA path used by sixel; interlaced can stay unsupported with a
clear diagnostic.

## FB-12 — MessageBox / TaskDialog + file dialogs for an extensions package — `accepted` (ext)

Neither exists in the framework; Gallery carries an app-local `MessageBox` (~100 lines, directly
portable) and `SaveDialogDemo` shows the modal/overwrite-confirm pattern without real browsing. The
editor must build both (spec §10.1: open/save dialogs with breadcrumb, places rail, overwrite confirm).
**Mike (2026-07-03): MessageBox — or something richer akin to the Windows Task Dialog — is definitely
needed.** Direction: build a **TaskDialog-style component** (main instruction + content + severity icon
via the Icon element + custom/command-link buttons + optional verification checkbox + expandable
details), with classic `MessageBox.ShowAsync(text, title, buttons)` as a thin convenience over it. The
editor needs the rich form natively: crash-recovery prompt (§12 Restore/Discard with timestamp detail),
unsaved-changes triad (Save / Don't Save / Cancel), overwrite confirm, and the §7.1 relative-link
"Open (replace)" affordance are all task-dialog shapes. Design vocabulary: the message-box archetypes in
`tokyo-night-terminal-borderless-windows.html` (severity glyph + title + body, fill separation, `--well`
inset — the same vocabulary §2.3 callouts reuse). Build app-agnostic (theme via `ThemeKeys`, no editor
coupling) in CursorialEdit first, then promote to a `Cursorial.UI.Dialogs`-style extensions package.
The file-dialog mockup (`tokyo-night-terminal-colorpicker-filedialogs.html`) is the design reference for
the open/save pair.

## FB-13 — Scene re-raster doesn't clear the fragment table (stale-image hazard) — `proposed` (core / Drawing)

Empirically confirmed against the built DLLs: `Scene.Draw` → `ClearToTransparent`
(`Cursorial.Drawing/Scenes/Scene.cs:87,95`) wipes cells only; the scene buffer's **fragment table
survives re-raster**. An element that drew an image last frame and simply stops drawing it (the spec
§7.2 image→placeholder swap!) leaves the image on screen — the fragment keeps passing through the
compositor. Related: `ScenePool.Rent` re-clears cells only, so fragments can leak across pooled scene
renters. Once a fragment actually leaves the scene, everything downstream is correct (erase pass,
ghost-footprint force-repaint, per-protocol teardown — well tested in `FragmentPassthroughTests`).
**Proposal:** clear scene-buffer fragments in `ClearToTransparent`/scene reuse — the view-scoped
`CellBufferView.Clear` already drops intersecting fragments (`CellBufferView.cs:375-399`), so this is
parity, likely a one-liner + regression test. Without it the editor's placeholder swap has no sanctioned
teardown path (no UI-level fragment-removal API exists).

## FB-14 — Legacy-wire input gaps that constrain the default keymap — `proposed` (core / Input)

Three findings from mapping the spec's default chords onto the VT input layer without Kitty keyboard:
1. **`0x1C–0x1F` C0 bytes are dropped** (`VtInputInterpreter.cs:1690` "ignored in v1") — `Ctrl+/`
   (0x1F, the spec's raw-view toggle) never arrives even though the byte is on the wire. Proposal:
   decode `0x1F` → `Ctrl+/` (with the known Ctrl+-/Ctrl+_ ambiguity documented).
2. **The negotiator never enables xterm `modifyOtherKeys`** even though the interpreter already decodes
   both its forms (`CSI 27;mod;cp~` and `CSI cp;mod u`). One opt-in (`CSI >4;2m`) would give
   xterm-family terminals Ctrl+Shift+letter, Ctrl+punctuation, etc. — the whole class of chords
   currently Kitty-only.
3. **`Ctrl+Space` is unmatchable by a single gesture**: legacy NUL decodes to `(Key.Space, Control)`
   while Kitty/CSI-u delivers `(Key.Character, " ", Control)`; `KeyGesture.Parse("Ctrl+Space")` pins to
   the character form (ND10) so it misses the legacy wire. Framework controls work around it with dual
   checks (`ListBox.cs:130-133`). Proposal: make `KeyGesture` treat the two shapes as one chord (special
   case in `Matches`), or ship a documented dual-gesture helper.

## FB-15 — Icon element: fourth tier (Emoji) between Image and Text — `discussed` (core)

Surfaced while building the editor's icon ledger (`icon-ledger.md`): emoji and single-width Unicode are
two different floors, not one. Emoji are richer but (a) render **double-width** in the cell grid
(`GraphemeWidth` emoji blocks → 2) and (b) have no negotiated detection signal (same problem as Nerd
Font coverage — spec §18.2's reserved `caps-ascii` note is the same disease). Folding both into one
`Text` tier forces the app to either always budget 2 cells or risk grid corruption.
**Direction (Mike, 2026-07-03: "Icon class may need a fourth tier wedged in there — I expected that
might become necessary"):** extend the ladder to **Glyph (`caps-nerdfont`) → Image (`caps-images`) →
Emoji (new `caps-emoji`) → Text (`caps-unicode` floor)**. Emoji-above-Text wins on emoji-capable,
image-less terminals (e.g. Apple Terminal); PNG stays above emoji because it's theme-controllable and
width-exact. Design constraints:
1. ~~`caps-emoji` is a **user-declared opt-in, default absent** (mirrors `caps-nerdfont`'s no-tofu-first-run
   posture) until a glyph-capability source exists.~~ **Revised (maintainer decision, Mike, 2026-07-04):
   `caps-emoji` is a user **opt-OUT, default present**. Rationale: emoji coverage in modern terminals is
   near-universal — unlike Nerd Font PUA coverage, where the default-absent no-tofu posture rightly stays
   (`caps-nerdfont` remains opt-in; the asymmetry is deliberate) — and grid safety is owned by the Icon
   element's 2-cell emoji measurement (constraint 2), not by hiding the tier. The `capabilities.emoji` key
   keeps its boolean shape: absent/`true` = enabled, `false` disables (tri-state overlay unaffected).
2. **Width contract per tier**: the Icon must measure at its *resolved* tier's width (emoji = 2 cells,
   others typically 1) so Bars/toolbar layouts budget correctly; the §18.4 confirmation-render width
   ruler applies to the emoji tier too (VS16 sequences especially).
3. Ledger/API shape: an `Emoji` property alongside `Glyph`/`Image`/`Text` (unset = tier skipped), same
   highest-provided-and-supported resolution rule.
The editor's icon ledger already carries separate Emoji and Unicode columns, so it feeds the four-tier
model as-is.

## FB-16 — Expose `ScrollContentPresenter` band geometry to custom scroll hosts — `proposed` (core)

A custom `IScrollContentHost` (the sanctioned public seam for content-assisted scrolling) must realize
content covering the SCP's raster band, but `BandStartRow`/`BandLength`/`BandPadding`/`BandAnchorRow`
are `internal` (`Controls/ScrollContentPresenter.cs:203-212`) — only same-assembly hosts
(`VirtualizingStackPanel`) can read them. An app host today reconstructs the band from the documented
convention (band = viewport + 2K, K = max(viewportRows, 8)), which works but is not contractual; if the
band policy ever changes, external hosts raster blank rows. **Proposal:** expose read-only band geometry
to the host — e.g. members on the injected `ScrollOwner`, or pass a `BandInfo` through
`IScrollContentHost.SetViewport`/`InvalidateRealization`. Surfaced by CursorialEdit's `DocumentPanel`
(the editor's document surface is exactly such a host); its M1 spike exercises the convention.

**M1.WP3 spike evidence (2026-07-03, `PanelSpikeTests` storm suite green under `KittyTruecolor` +
`Ansi16Legacy`):** the seam works end-to-end — extent delegation, `InvalidateScrollExtent` refinement
mid-scroll, re-anchor-driven `InvalidateRealization`, composite-slide scrolling with zero block
re-raster (render-call counters flat across in-band scrolls *and* across re-anchors for retained
render-boundary blocks), and content-coordinate caret publication all behaved as documented. But the
naive convention is **not sufficient as written**: the SCP's band is anchored at the offset of the
*last re-anchor*, which lags the current offset by up to K in either direction, so a host that
reconstructs `bandStart = clamp(offset − K, …)` from the *current* offset at measure time can miss up
to 2K rows of the true band — rows a subsequent in-band slide (which never re-measures the host)
brings into the viewport as blanks. `DocumentPanel` therefore realizes the reconstructed band padded
by K per side (`[offset − 2K, offset + viewport + 2K)`, provably a superset given the |offset −
anchor| ≤ K invariant) — up to 2K rows of over-realization that a public `BandStartRow`/`BandLength`
would eliminate exactly. Second finding while storming extent refinement: the SCP scroll offset's
coercion is a view over a durable raw value, so shrink-then-regrow *resurrects* the pre-shrink offset
(viewport teleport), and the value store's equal-coerced-value write gate makes an app-side pin
impossible while shrunk — worth folding into the same seam discussion (a `BandInfo`/host API could
also give hosts a sanctioned offset-pin). Both behaviors are pinned by
`CursorialEdit.Tests/Views/PanelSpikeTests.cs`; R5 is retired on the convention *plus* the K-padding
workaround.

## FB-17 — Framework user-configuration layer (maintainer-proposed) — `accepted` (core)

Mike drafted `docs/user-configuration.md` (2026-07-03): a Cursorial-level options layer — persistence
under `~/.cursorial/` (global + per-app overlay), capability overrides (images/emoji/Nerd Font/terminal
caps for unknown terminals), theme preference, access-key cues, translucency/animation toggles, keyboard
platform profiles, an Options dialog with a default keybinding overridable in the `BuildApplication()`
chain, Nerd Font test-glyph confirmation, advanced-tab capability overrides, and a first-run wizard.
**This subsumes parts of FB-5 (the override API becomes the runtime seam this layer writes through) and
FB-15 (emoji toggle = the `caps-emoji` opt-in's UI/persistence), and answers editor-spec §17 Q6.**

App-developer review notes (CursorialEdit's seat):
1. **The `Ctrl+Shift+O` default keybinding is a legacy-wire trap** (FB-14): on non-Kitty/non-CSI-u wires
   Ctrl+Shift+letter arrives as plain Ctrl+letter — so the Options dialog would be unreachable (or worse,
   collide with an app's Ctrl+O = Open) precisely on the degraded terminals where the user most needs it
   to declare capabilities. Recommend a chord that survives every wire (an F-key, e.g. F9, or
   Shift+F10-adjacent) or registering both wire forms; at minimum the first-run wizard should show a
   binding that works on the current wire.
2. **Overlay semantics need tri-state**: per-app values should overlay global only when explicitly set
   (set/unset/inherit), otherwise an app writing its options file snapshots and freezes global defaults.
3. **App-registered option pages**: the editor (spec §15) has app-specific settings (table overflow mode,
   autosave interval, view defaults). If the framework Options dialog accepts app-contributed
   pages/sections, apps get ONE settings surface instead of two dialogs — CursorialEdit volunteers as the
   first consumer.
4. **App identity**: entry-assembly name is a fine default but should be overridable in the builder
   (assembly renames orphan configs; two tools may deliberately share a profile).
5. **First-run wizard**: framework-wide once-ever (marker in `~/.cursorial/`) feels right; a per-app
   wizard would grate. Per-app first-run could instead show a one-line status-bar hint with the Options
   binding.
6. **Nerd Font test glyphs**: include the width-ruler check from editor-spec §18.4 (glyphs rendered
   against a single-cell ruler) — presence AND single-cell advance both matter; a present-but-double-width
   glyph corrupts the grid.
7. Capability-override persistence should survive renegotiation by design (the FB-5 caveat) — if the
   layer applies overrides via a first-class `CapabilityOverrides` seam rather than the
   `OnCapabilitiesChanged` spoof, that falls out naturally.

Editor-plan impact: M7's Options/capability-declaration work re-scopes to *consume* this layer when it
lands (with the app-side recipe from the integration notes §7 as the fallback if the editor ships first).

**Update (2026-07-03, Mike): accepted — we implement this as part of the markdown-editor project, in the
Cursorial framework.** Proposed staging (two framework deliverables, editor as first consumer):
- **Stage A — storage + override seam (no UI), early (parallel with editor M1/M2):** the options store
  under `~/.cursorial/` (global + per-app, tri-state overlay), builder wiring
  (`UIApplicationBuilder.WithUserConfiguration(...)`, app-id override), and the first-class
  `CapabilityOverrides` runtime seam (FB-5 proper: per-axis forced on/off/auto folded into capability
  stamping, surviving renegotiation) + `caps-emoji` registration (FB-15). Editor M2's caps/theme layer
  and M5's access-key-cue option consume it.
- **Stage B — Options dialog UI + first-run wizard + Nerd Font width-ruler tester, aligned with editor
  M6/M7:** framework Options dialog with app-registered pages (editor contributes its §15 pages in M7),
  advanced capability-overrides tab with warning, wizard once-ever framework-wide. Benefits from FB-12's
  TaskDialog vocabulary landing first.
- Open design points carried from the review notes above: default keybinding must be legacy-wire-safe
  (not Ctrl+Shift+letter — FB-14), tri-state overlay semantics, tester covers presence AND single-cell
  width.

## FB-18 — Breadcrumb path bar as a general-purpose control — `accepted` (core)

Mike (2026-07-03), reviewing the file-dialog designs
(`docs/ui-layer-design/tokyo-night-terminal-colorpicker-filedialogs.html`): the breadcrumb path bar
should be **generalized into its own control**, not built as file-dialog-internal chrome. Scope: a
`Breadcrumb` items control (segments + separator glyphs, per-segment invoke/command, overflow/ellipsis
at narrow widths, keyboard navigation across segments, editable-path swap affordance per the mockup),
themed via the `ThemeKeys` spine. Built framework-side as part of the editor project (FB-2 pattern:
Cursorial repo + framework tests, editor consumes via `$(UseLocalCursorial)`), scheduled ahead of the
M6 file dialogs, which become its first consumer.

## FB-19 — `ListView` (WPF-style, columned) — `accepted` (core)

Mike (2026-07-03): the file dialog needs a **ListView control like WPF's** — deferred in Cursorial so
far, now worked into this project's framework track. Scope: `ListView : ListBox`-style selector with a
pluggable view (WPF's `GridView` analog — column definitions, header row, cell templates or bindable
cell text, column sizing in cells per the whole-cell contract), riding the existing
`ItemsControl`/`SelectingItemsControl`/`ItemContainerGenerator` infrastructure (virtualization-compatible
via `VirtualizingStackPanel`, `:alternate` striping for row zebra). First consumer: the M6 file-dialog
details list (name/size/modified); design vocabulary from the file-dialog + datagrid mockups (a full
DataGrid stays out of scope — spec-deferred). Same delivery pattern as FB-18.

## FB-20 — `Window.Shown` never fires on the modal path — `proposed` (core / Windowing)

Found porting Gallery's MessageBox (M1.WP10): `Shown` is raised only at the end of modeless
`Show(WindowManager)` (`Cursorial.UI/Windowing/Window.cs:286`); `ShowDialogAsync` →
`WindowManager.ShowDialog` raises nothing — Gallery MessageBox's own `Shown`-based focus hook is dead
code (its dialogs never receive initial button focus). Workaround: one-shot `Window.Activated` (raised
synchronously inside `FinishShow` after content attach + provisional layout). **Proposal:** raise `Shown`
on the dialog path too, or document `Activated` as the dialog-ready hook.

## FB-21 — No public seam to show a modal on an explicit manager/app — `proposed` (core / Windowing)

`WindowManager.ShowDialog` is internal; `Window.ShowDialogAsync` resolves `_owner?.Manager ??
UIApplication.Current`. An app-agnostic dialog suite (FB-12) that takes an explicit `UIApplication`
parameter must dispatcher-marshal so the thread-local `Current` resolves — the parameter is otherwise
meaningless. **Proposal:** a public `ShowDialogAsync(WindowManager, …)` (or `UIApplication` overload)
removing the ambient-static dependency.

## FB-22 — `UISynchronizationContext` installed only during frame execution: a UITestHost determinism trap — `proposed` (core / docs or Testing)

An async wrapper started outside a frame (typical UITestHost test body) captures no UI sync-context, so
its post-close continuations resume on the thread pool. Harmless for pure tails; a determinism trap for
tests asserting completion right after `RunUntilIdle`. **Proposal:** at minimum a testing-docs note;
possibly install the context for the lifetime of the headless app.

**FB-17 status update (2026-07-03): Stage A DELIVERED for review** — branch `fw-a-user-configuration`
(3 commits, tree clean, not merged) in the Cursorial worktree `.worktrees/fw-a`. Full Cursorial.UI.Tests
suite 2784/2784 green (Configuration section 59 new tests); Bars/Xaml suites green. Delivered:
`UserOptionsStore` (flat string map, global + per-app tri-state overlay, atomic writes, corrupt-tolerant,
inbox `System.Text.Json` reader only — no new deps), `IUserConfigurationPathProvider`, `UserOptionKeys`,
`CapabilityOverrides` record (per-protocol graphics axes + motion + kitty-keyboard; color-tier and
NerdFont/emoji deliberately excluded — they have native opt-ins), `UIApplication.EmojiAvailable` +
`caps-emoji` stamping (**posture flipped on-branch to opt-OUT/default-present, maintainer decision
2026-07-04** — near-universal terminal emoji coverage, unlike Nerd Font PUA coverage where opt-in stays;
grid safety owned by the Icon's 2-cell emoji measure, not tier hiding; `capabilities.emoji` stays a
boolean, `false` disables) + the **Icon fourth tier (FB-15) implemented** (Glyph→Image→Emoji→Text, 2-cell
emoji measure incl. VS16), `EffectiveCapabilities` sharing the stamping fold (renegotiation survival by
construction), `UIApplicationBuilder.WithUserConfiguration(...)`, wired keys (theme base, tier, NerdFont,
emoji, overrides, images-disable, AnimationsEnabled), reserved keys documented. Review notes for the
maintainer: `UITestHostOptions.ConfigureBuilder` hook added (consider first-class); `KittyKeyboardFlags`
init-only coherence footgun when forcing the protocol off; the graphics-protocol gate now exists in three
mirrored spots (shared predicate wanted); `Capabilities`/`EffectiveCapabilities`/`EffectiveInputCapabilities`
naming is getting crowded; no framework startup-diagnostics sink exists (store diagnostics ride
`LoadDiagnostics`); `UIApplication.Configuration.cs` lacks a `DependentUpon` nesting entry (csproj edits
were out of the agent's scope).

**FB-4 annotations (2026-07-04, from M1.WP2):** (a) Confirmed at HEAD: the signal path writes only
negotiator opt-in disables + termios — no alt-screen leave, cursor show, or SGR reset. (b) NEW TRAP:
`PosixSignalRegistration` invokes multiple handlers for one signal **newest-first**, and
`TerminalSession.HandleSignal` calls `Environment.Exit` inside its handler — so an app handler
registered *before* session open silently never runs. Sequencing a workaround requires knowing both the
runtime's reverse invocation order and the session's registration moment (the editor registers from
`UIApplication.Started`, which fires after session open + alt-screen entry). `EmergencyRestoreBytes` (or
a pre-teardown hook) would eliminate the trap — fold into the proposal. (c) Minor: on a non-TTY launch,
`StdioTransports`' stty subprocess prints `stty: stdin isn't a terminal` to inherited stderr before the
`InvalidOperationException` surfaces — apps can't produce a clean friendly error; suggest capturing the
subprocess stderr.

**FB-4 annotation (2026-07-04, wave-2 review):** the signal path ALSO misses the DECAWM re-enable
(DECSET 7) and the OSC 112 cursor-color reset. `FrameRenderer` disables autowrap on every frame
(`FrameRenderer.cs:152`) and only `FrameRenderer.Close` re-enables it (`FrameRenderer.cs:886` — its own
comment says it "MUST run before leaving the session or the shell inherits a no-wrap terminal"); the
frame loop recolors the terminal cursor via OSC 12 at startup and resets it via OSC 112 only on clean
teardown (`UIApplication.FrameLoop.cs:200/795`). A signal-killed app therefore strands the shell with
no-wrap AND a theme-colored cursor even after the alt-screen/cursor-visibility strands are fixed. Fold
both into the `EmergencyRestoreBytes` proposal payload (the editor's `EmergencyRestore` now emits
DECSET 7 + OSC 112 ahead of its DECRST 1049 / cursor-show / SGR-reset bytes, mirroring the clean-path
order).

**FB-21 annotation (2026-07-04):** a modal shown while teardown races removes the window manager throws
`InvalidOperationException` from `Window.ShowDialogAsync` (`Window.cs:312`), and there is no public
shutdown observable (`UIApplication.IsShutdown`/`UIDispatcher.HasShutdown`) to condition on — the
dialog suite now pre-checks `UIApplication.Current?.WindowManager is null` on the UI thread (race-free
by thread affinity, but chain-mirroring is fragile). Proposal add-on: a public shutdown observable, or
dialog-path graceful dismissal during teardown.

## FB-23 — Built-but-never-run `UIApplication` leaks a builder-supplied host — `proposed` (core, small)

`UIApplicationBuilder.WithTerminalHost(host, disposeWithApp: true)` records the intent, but `_ownsHost`
is only assigned during `RunAsync`/`StartHeadless` — `DisposeAsync` on an app that was Built but never
run disposes nothing. Harmless for in-memory synthetic hosts (how we hit it, in a Build-without-run
test); a real session host would leak its transports. **Proposal:** honor `disposeWithApp` from Build
time.

**FB-22 annotation (2026-07-04):** second determinism trap confirmed while pinning the dialog shutdown
race: `UIDispatcher` binds its owner thread at Build, so `Task.Run` is an unsound "off-UI-thread"
vehicle in tests — the pool can schedule the delegate on the very thread that owns the (disposed)
dispatcher, flipping `CheckAccess()` and the code path under test. Suites pinning marshaled-path
behavior need a dedicated thread. Belongs in the same testing-docs note as the sync-context caveat.

**FB-16 annotation (2026-07-04, from M1.WP7):** third piece of evidence — wrap-row block heights depend
on viewport columns, but a content-assisted host's DATA SOURCE has no sanctioned way to learn the
content width: `IScrollContentHost.SetViewport` lands on the panel, and the app must relay it outward
(push-on-attach included, since the SCP republishes only on change). A host-facing viewport/band
contract (`BandInfo` through `SetViewport`, per the FB-16 proposal) would delete the relay along with
the Kmax ratchet.

**FB-3 annotation (2026-07-04, WP9 experience report):** building on the write-only clipboard was
mechanically clean (`UIApplication.Clipboard.SetText` is fire-and-forget and internally capability-gated
— good API) but the UX split is real: Ctrl+V (app store) and terminal-paste (bracketed) silently differ
in reach, and "copied elsewhere, Ctrl+V does nothing" has no signal we can give. Proposals (b)+(c)
(service-level fallback store + TextBox consuming it) would delete the app-side `InternalClipboard`
entirely. Small addendum for `Cursorial.UI.Testing`: neither stock `TestCapabilities` preset negotiates
`ClipboardWrite`, though the real negotiator grants it for Kitty AND Xterm families — the presets
undersell real wires; suites asserting OSC 52 bytes must compose it on manually.

## FB-24 — Activation auto-focus ignores a focusable ROOT element — `proposed` (core / focus)

Found wiring the reveal demo (`RevealDemoView` used directly as the `RunAsync` root). The activation
auto-focus (the post-layout first-tab-stop walk, P6.1) only considers **descendants** — a focusable
root element with no focusable descendants is never focused, so it receives no `OnGotFocus`, cannot
publish a terminal caret, and can't drive focus-gated behavior. `EditorControl` works only because it is
a *child* of `EditorShell`. Keys still route to the root (so it looked half-working: reveal moved on
arrows, but no cursor). **Workaround:** dispatcher-post an explicit `Focus()` after layout settles.
**Proposal:** include the root element itself in the activation first-tab-stop walk (or auto-focus the
root when it is focusable and no descendant tab stop exists).

## FB-25 — `FillRectangle` overwrites glyphs (no glyph-transparent fill) — `proposed` (rendering)

Found painting the markdown selection highlight and the active-block well. `RenderContext.FillRectangle`
uses `overwrite: true`, so it clobbers any glyphs already drawn in the rect **and** corrupts wide-cell
(double-width cluster) bookkeeping — unusable for a background scrim behind text. The working primitive is
`PaintRectangle` (intra-scene, glyph-transparent), but it only shows through if painted BEFORE the text
over still-empty cells (it relies on `DrawText`'s transparent background). **Friction:** the natural-named
API (`FillRectangle`) is the wrong one for the common "tint a region behind existing content" case, and the
right one (`PaintRectangle`) has an ordering constraint that isn't obvious. **Proposal:** document the
distinction at both call sites, and/or offer a `FillRectangle(..., overwrite: false)` overload that composites
as a background scrim without touching glyphs or wide-cell state.

## FB-26 — No run map for "this block with an ARBITRARY active line" — `proposed` (app-layer seam)

Found doing vertical (goal-column) motion into a block that is about to become active. `MoveVertical`
computes the goal cell against the target block's INACTIVE map, but the line then renders on the ACTIVE
(revealed) map; for lines with LEADING marks (headings `# `, list/quote markers) the revealed layout occupies
cells the hidden layout doesn't, so the landing drifts by the marker width. The caret host exposes
"map for the current active line" but not "map as if line N were active", so the app can't pre-compute the
landing against the layout it will actually render. Bounded (a cell or two, leading-mark blocks only) and
deferred, but the missing seam is the root cause. **Proposal (app-side):** let the caret query a block's
run map for an arbitrary hypothetical active line, so vertical landings resolve against the post-reveal layout.

## FB-27 — Command-owned checked state via coercion + `Handled`; move parameter to Cursorial.UI — `discussed` (core / commands)

Raised by Mike, 2026-07-05, designing the "wrap while editing" toggle command (must show greyed +
unchecked when "wrap for display" is off — the option is moot without display wrap). Design converged
2026-07-05 (Mike drove it):

**Chosen design (coercion, not a proposed/effective field pair).**
- Move `ICheckableCommandParameter` / `CheckableCommandParameter` from `Cursorial.UI.Bars` into
  `Cursorial.UI` proper (it binds to `ToggleButton` + the coercion pipeline, both in UI; only incidentally
  in Bars). Make it the **default** command parameter for **`ToggleButton` and its subclasses** (the
  checkable control family — `BarToggleButton`, checkable menu entries, etc.) when the app provides none.
  A plain `Button` has no checked state, so it gets no checkable default. (Confirm every control that
  consumes `ICheckableCommandParameter` — e.g. a checkable split/menu entry — is in the `ToggleButton`
  hierarchy so it inherits the default; anything outside it needs the same wiring.)
- Add a `Handled` flag to the parameter. Register a **coercion callback** on `ToggleButton.IsChecked`
  (the framework has the full pipeline — `RecoerceLocal`/`IsCoerced`/base-vs-coerced in `EffectiveValue`):
  when `Handled`, coerce the effective checked state to the parameter's value (override); when NOT handled,
  return the base value unchanged — **normal toggle behavior, `IsChecked` untouched.** So `Handled` is a
  pure opt-in and existing checkable commands are unaffected (backward-compatible at zero cost).
- The user's preference is preserved as the DP **base value** by the coercion system — **no ProposedIsChecked
  field, no restore-on-reavailable bookkeeping.** For wrap-on-edit: display-wrap off → command sets
  `Handled=true` + forced value + `CanExecute=false` → greyed + (unchecked or checked, per the forced value);
  the preference reappears automatically when `Handled` clears.

**Implementation notes / open points (from the consuming seat):**
1. **Re-coercion trigger:** the parameter isn't a DP, so `Handled`/value changes won't auto-invalidate. The
   control must `CoerceValue(IsCheckedProperty)` on the `CanExecuteChanged`/`OnCommandStateChanged` hook it
   already uses to re-query command state — the single wiring point; the signal already exists.
2. **Where the default parameter lives (RESOLVED, Mike 2026-07-05):** `ToggleButton` (Cursorial.UI) provides
   its own **per-control** default parameter, so it works standalone and stays command-agnostic. `BarCommandSync`
   (Bars — already owns "one command drives every surface") **replaces** that per-control default with a
   command-**shared** parameter at bind time, so multi-surface sync is the sync layer's job, not the base
   control's or `BarCommand`'s. The swap (and the unbind revert) should trigger `CoerceValue(IsCheckedProperty)`
   immediately — same re-coercion as `CanExecuteChanged` — so the displayed state snaps to the shared
   parameter's `Handled`/value at bind time instead of lagging to the first re-query.
   - **Lazy allocation of the per-control default (impl, Mike 2026-07-05):** allocate it in
     `OnCommandStateChanged` only when `IsChecked`'s base value source `Kind == Default` (nothing has
     provided a checked source yet). Reorder the bar buttons' `OnCommandStateChanged` overrides to run their
     sync logic BEFORE `base.OnCommandStateChanged()`, so `BarCommandSync` installs the shared parameter first
     (making the source non-`Default`) and the base then skips the per-control allocation. **Comment this
     base-last inversion** at each override — a future "call base first" tidy would silently break sync
     precedence. The pre-first-`OnCommandStateChanged` window is safe: with no parameter, coercion returns the
     base value (normal toggle), and `Handled` can't be set before command state is first queried.
   - **Document the ordering on the `OnCommandStateChanged` API itself** (a `<remarks>`), so any future control
     author sees the rule, not just the scattered per-override comments. Fold in the rationale so it reads as
     load-bearing, e.g.: *"Derived controls may auto-assign a command parameter in this method. An override that
     does so should assign before invoking the base implementation, which installs its own default parameter
     when none is present — so the derived assignment takes precedence."*
3. **What `Handled` coerces TO:** coerce to a parameter-specified value (not a hardcoded `false`) → gives
   greyed+checked ("on but locked") for free alongside greyed+unchecked.

Generalizes to every context-gated toggle (wrap-on-edit, bold/italic reflecting the caret's run, table-context
ops greyed outside a table) with zero app bookkeeping. **Lands with the reveal-wrap View commands** (M5 command
surface); the reveal-wrap RENDERING + config flag land earlier (after the M3 R3 gate). See
[[feedback-reveal-wrap-decision]].

## FB-28 — Input capture/automation seams (from the operation-journal build) — `proposed` (UI / input)

Found building the `--journal`/`--replay` diagnostic (deterministic session capture + replay via the
public `InputDispatcher.PreProcessInput` + `ProcessEvent`). Two gaps, both minor/worked-around:
- **`PreProcessInput` does not fire for `ResizeEvent`/`FocusEvent`** — they return `NotUIInput` /
  are handled in `ProcessFocusEvent` without raising the pre-process event. So a single-hook capture sees
  only key/mouse/paste (the edit-affecting input — fine for journaling). If resize/focus capture is ever
  wanted from one hook, raise them through `PreProcessInput` too (or expose a sibling event).
- **No public `UIApplication` accessor for the live terminal size** — `NotifyResized` is a setter with no
  getter; `FrameBufferInternal` is internal. The journal header takes size best-effort from
  `Console.WindowWidth/Height`. A public `UIApplication.ViewportSize` (columns×rows) would let an app read
  the negotiated size directly. (All INPUT fields needed were public — `KeyEventArgs.Device`,
  `MouseEventArgs.Device`, `TextInputEventArgs.Text/FromPaste`, `ProcessEvent`, `NotifyResized`.)

**FB-1 RETIRED (2026-07-05) — trio promoted, mirror deleted.** The three types are now `public` in
`Cursorial.Rendering.Text` (`GraphemeLayout`/`TextNavigation` in `TextEditing.cs`, `TextLayout` in
`TextLayout.cs`), Document-reachable (the Document project already referenced `Cursorial.Rendering` for
`WrapMode`), so the editor now consumes them directly and the ~432-line app-side `CaretNavigator`/
`WrappedLine` mirror (plus its dedicated unit test) is **deleted**. Adoption mapping used:
`Wrap`→`TextLayout.Build`; `WrappedLine.{RowStart,RowEnd,RowWidth,RowCount,RowOfCol,ColAt,IsRowEndBoundary}`
→`TextLayout.{LineContentStart,LineContentEnd,LineWidth,LineCount,Locate().Line,OffsetAt,IsLineEndBoundary}`;
cluster steps `{Next,Prev,Snap}`→`GraphemeLayout.{NextBoundary,PrevBoundary,PinToBoundary}`; col↔cell
`{CellOfCol,ColAtOrBeforeCell,ColAtOrAfterCell}`→`GraphemeLayout.{ColumnOf,CharIndexAtOrBeforeColumn,
CharIndexAtOrAfterColumn}`; word motion→`TextNavigation.{NextWord,PrevWord}`.
`TableEditingController`'s duplicated single-line cluster walkers (the "cleanup 10" copies, blocked because
the mirror lived up in the app-layer `CursorialEdit.Layout`) are **deduped** onto `GraphemeLayout` now that
the wall is gone. The R4 probe (`TextNavigationProbeTests`) was retargeted onto the promoted types and stays
**zero-divergence** vs a live `TextBox` across CJK/emoji/ZWJ/VS/wrap-affinity; the whole suite is green.
`TextPresenter.MoveVertical`'s sticky-goal stays app-side (`DocumentCaret`, over `ICaretMap`); the probe's
single-line Up/Down/Home/End oracle is now a 3-line composition over `TextLayout.Locate`/`OffsetAt`/
`IsLineEndBoundary` (no mirrored line-packer).

Two minor hardening candidates surfaced during adoption (both **non-blocking** — behavior-neutral for the
editor, filed only so the primitives can be tightened later):
1. **`GraphemeLayout.Build` takes `string?` only, no `ReadOnlySpan<char>` overload.** Two cold call sites
   that hold a span slice of a larger string (`PlainTextPresenter.DrawSelectedRow`, `TableCaretMap`) must
   `slice.ToString()` to build a per-row layout — an avoidable allocation on the render/caret paths. A
   `Build(ReadOnlySpan<char>)` overload (the enumerator it walks is already span-based) would remove it.
   (`BlockRunMap.NearestOffset` sidesteps this by reusing the stored `TextLayout.LineGlyphs(row)` — the
   per-visual-line layout is already built, so no span-Build is needed there.)
2. **`TextNavigation.NextWord`/`PrevWord` do not pin the landing to a grapheme-cluster boundary**, whereas
   the mirror did (`SnapToCluster` on the returned index, defensively). Behavior-neutral for every probed
   fixture (a whitespace-delimited word run never ends mid-cluster in the tested inventory, and `TextBox`
   itself doesn't pin — the probe stays green), and the editor's production word motion (`DocumentCaret`)
   is its own visible-cluster walk that never calls these — so there is **no divergence to preserve** and
   no editor-side wrapper was needed. Noting only as a theoretical robustness gap (a word run ending inside
   a cluster that mixes whitespace + combining marks would land off-boundary); keep the classifier
   whitespace-delimited (`TextBox` parity is the probe's pinned contract).
