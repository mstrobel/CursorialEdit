# Cursorial integration notes — verified framework picture for CursorialEdit

Compiled 2026-07-03 from a multi-agent sweep of the Cursorial repo (design docs, Tokyo Night mockups,
and source) followed by a source-grounded verification pass of every "deferred/GAP" claim against HEAD.
**Where design docs and source disagreed, source won** — the docs' §15 deferral list is stale in several
places. Framework friction items live in `framework-feedback.md` (FB-n references below).

Cursorial HEAD = `ca9a528` = tag `v0.3.1` + one CI packaging commit; **the published 0.3.1 NuGet packages
are API-identical to HEAD**. All `file:line` refs are Cursorial-repo-relative at that commit.

---

## 1. Corrected ledger: docs said deferred, source says shipped

| Claimed deferred | Reality at HEAD / v0.3.1 |
|---|---|
| Multi-line TextBox + undo | Shipped: `TextBox` (`Controls/TextBox.cs`, ~1000 lines) — `AcceptsReturn`/`AcceptsTab`/`TextWrapping`/`MinLines`/`MaxLines`, anchor+caret selection, mouse drag-select with capture and caret-follow scrolling, word/line multi-click, undo/redo (`IsUndoEnabled`, `UndoLimit`, splice+coalesce edit records restoring caret/anchor). |
| Virtualization | Shipped (workstreams V0–V5, all pre-tag): `VirtualizingStackPanel : VirtualizingPanel, ILogicalScrollHost` — opt-in via `ItemsPanel` + `VirtualizingPanel.IsVirtualizing=true`, recycling by default, **variable-height items** (sticky height cache + prefix-sum, O(log n) offset→item), `ScrollUnit` Item/Cell. The 32K `MaxScrollExtent` cap applies **only to the legacy non-host measure path**; the host path caps at `int.MaxValue/2`. 100+ tests (Sections 38/40/41/44). |
| TreeView / ComboBox | Both shipped with keyboard nav, selection, default themes (`Controls/TreeView.cs`, `ComboBox.cs`; editable combo incl.). TreeView is *not* virtualized end-to-end (container recycling only). |
| `:alternate` striping | Shipped, generator-owned, virtualization-safe — style `:alternate` pseudo-class for zebra tables (spec §5.1). |
| GridSplitter | Shipped (`Controls/GridSplitter.cs : Thumb`), respects `ColumnDefinition.MinWidth` — sufficient for §4.2 split view. |
| Sixel transparency | The `#00FFFF` color-key + `P2=1` scheme **ships** (`Fragments/SixelEncoder.cs:11`, `MedianCutQuantizer.cs:57-65`; alpha≤127 → key). Only the true-cyan ±1 nudge is missing (FB-10). |
| Kitty clipping | Ships — source-rect re-placement (`KittyImageFragment.cs:101`); a doc comment claiming Kitty can't crop is stale. |
| caps-* catalog | Full §18.2 catalog stamps today: color tier, `caps-motion`, `caps-kitty-keyboard`, `caps-images`, `caps-image-clipping`, `caps-image-occlusion`, `caps-nerdfont`, `caps-unicode` (`StyleEngine.cs:1466-1523`). `caps-ascii` reserved/unstamped as documented. |

## 2. Confirmed real gaps (verified at HEAD)

- **No rich-document/WYSIWYG editing control** — expected; the editor's document surface is the app's core work. `TextBox`'s undo model and `PasswordBox`'s `DisplayText`/`ToDisplayIndex`/`ToModelIndex` seams are the reference shapes.
- **`TextLayout`/`GraphemeLayout`/`TextNavigation` are `internal`** (FB-1) — reimplement or make public.
- **Clipboard is write-only** (OSC 52 set; `TryGetTextAsync` always null; Ctrl+V a consumed no-op; no internal fallback) (FB-3). Paste arrives solely as bracketed paste → `TextInput{FromPaste=true}`.
- **Whole-zone re-raster** on any `AffectsRender` change (partial intra-zone re-raster deferred by design) — the knob is more render boundaries (§4 below).
- **Horizontal `ScrollBarVisibility.Auto` → `Disabled`** (FB-6); use `Hidden`/`Visible` for table regions.
- **No PDF backend** anywhere → hide Export▸PDF (spec §10.3's conditional defer applies).
- **No MessageBox / file dialogs in the framework** (FB-12, accepted) — direction: build a TaskDialog-style component (main instruction/content/command links/verification checkbox) with MessageBox as a convenience over it, plus open/save dialogs; app-agnostic first, then promote to an extensions package. Gallery's `MessageBox` and `SaveDialogDemo` are the porting bases.
- **MenuItem lacks BarCommand auto-fill/check-sync** (FB-2) — direction agreed: `BarMenuItem` in Bars.
- **`EmergencyRestoreBytes` missing** — signal-killed app can strand the shell on the alt screen with hidden cursor (FB-4).
- **Kitty graphics = family allow-list** (Kitty/Ghostty/Rio; no `a=q` probe) (FB-8); sixel honestly DA1-driven (attr 4).
- **Kitty reposition = delete + full retransmit**; any Overlay fragment disables the SU/SD scroll fast-path (Overlay = Kitty only; sixel/iTerm2 are Cells-layer) (FB-9).
- **Scene re-raster does not clear the fragment table** — an element that stops drawing an image leaves it on screen (empirically confirmed); no UI-level fragment-removal API (FB-13). Must be fixed framework-side for the §7.2 swap.
- **`Cursorial.UI.Testing` + `Cursorial.UI.Themes` unpackaged** (FB-7); see §9 for the verified ProjectReference mix.
- **TreeView not end-to-end virtualized**; nothing virtualizes *within* one element (a giant table/code block realized as one container measures whole).
- **No docking framework / command palette / DataGrid / Hyperlink control** — all fine: spec defers or Grid+GridSplitter covers it.

## 3. Document-surface architecture (the M1/M2 fork — both routes viable)

**Custom surface route (recommended by the seams):** `IScrollContentHost` is public and app-implementable
(`Controls/IScrollContentHost.cs:15`; XML docs bless custom hosts). Host publishes extent (uncapped past
32K, `CapHostExtent` → `int.MaxValue/2`) + line/page steps; **offset stays SCP-owned** (styled
`ScrollOffsetRow/Column`, `[AffectsComposite]`, storyboard-animatable — a deliberate WPF `IScrollInfo`
deviation). `ScrollOwner.InvalidateScrollExtent()` (public) refines extent on edits. Discovery: the host
must be the SCP's **direct content** (`<ScrollViewer><DocumentSurface/></ScrollViewer>`). Keyboard
ownership: override `Control.HandlesScrolling => true` on the editor control — but the gate checks
`TemplatedParent`, so **the editor control must template its own ScrollViewer** (a loose XAML wrapper
won't see it). Caveat: SCP band geometry (`BandStartRow`/`BandLength`) is `internal`; reconstruct the
band as viewport + 2K, K = max(viewportRows, 8) — documented formula, no API contract.

**Virtualized-items route:** an ItemsControl of block elements over `VirtualizingStackPanel` also works
on 0.3.1 (variable heights, recycling, focus keep-alive, uncapped extent). Weaknesses: opt-in is easy to
forget (silent fallback to eager + 32K clamp), `ScrollUnit=Cell` needed for row-precise scrolling, and no
intra-block virtualization.

**Rendering economics:** SCP is always a render boundary with a banded scene (band = viewport + 2K);
in-band scroll is a pure composite slide (zero raster), band re-anchor re-rasters once (~2.2 ms measured
at 200×60). Whole-zone re-raster per `AffectsRender` change → **give each block (paragraph/table/code
block) `IsRenderBoundary = true`** so a keystroke re-rasters one block, not the band. Boundary promotion
is sticky until detach. Caret: publish element-local `(col,row,CursorShape.BlinkingBar)` through
`ITerminalCaretService` — real terminal cursor, native blink, zero re-raster, and the v1 accessibility
affordance.

**Frame-loop rules that shape the editor:** single UI thread; `UIDispatcher.InvokeAsync` always queues
(never inline); background work (parse, autosave, file I/O) off-thread + post back; `UITimer` is
frame-aligned (right tool for the 750 ms undo idle-seal and autosave debounce); discarded subtrees need
`UIElement.TearDown()` or INPC subscriptions leak; never touch `Scene`/`CellBuffer` from app code;
`SetCurrentValue` for programmatic writes that mustn't kill bindings/styles.

## 4. Keymap constraints (spec §6/§8) — legacy wire vs Kitty

Verified decode matrix (`VtInputInterpreter.cs`; gesture matching `KeyGesture.cs:178-188`, exact-modifier,
Ctrl+letter normalized to `(Character, letter, Control)` on every wire):

- **Safe everywhere:** Ctrl+letter, Alt+letter, Alt+arrows (`CSI 1;3A/B` — table row ops OK),
  Ctrl/Shift/Alt+arrows/Home/End/PgUp/PgDn (`Ctrl+Home/End` doc motion OK), plain F1–F12, Shift+Tab.
- **Kitty/CSI-u only:** any Ctrl+Shift+letter (legacy wire drops Shift — worse, it *fires the unshifted
  binding*: Ctrl+Shift+X arrives as Ctrl+X), Ctrl+punctuation. Spec's `Ctrl+Shift+X` (strikethrough) and
  `Ctrl+Shift+V` (plain paste) need legacy-safe alternates or Kitty-gated hints.
- **Broken on legacy:** `Ctrl+/` — the 0x1F byte is *discarded* by the interpreter (FB-14.1);
  `` Ctrl+` `` — NUL on the wire, indistinguishable from Ctrl+Space; `Ctrl+Space` itself needs **two**
  bindings (legacy `(Key.Space, Ctrl)` vs Kitty `(Character," ",Ctrl)` — FB-14.3); `Shift+F3` — xterm's
  `CSI 1;2R` collides with the cursor-position report and is dropped.
- The negotiator never enables xterm `modifyOtherKeys` though the decoder supports it (FB-14.2) — landing
  that would legalize the whole Kitty-only class on xterm-family terminals.
- Branch keymap/shortcut-hints on `ProtocolCapabilities.KittyKeyboardProtocol` at startup.

## 5. Text rendering — all §2 channels exist; degradation is automatic

`Style` (per-cell/per-run record) carries foreground/background, `TextAttributes` (bold/italic/underline/
strikethrough/…), `UnderlineStyle` (5 shapes), `UnderlineColor`, and OSC 8 `Hyperlink`. `TextRun` carries
a full `Style` + per-run brush `Tag`. `RenderContext.DrawText(..., baseStyle:)` and
`DrawFormattedText(FormattedText, …)` cover both hand-drawn and rich-text paths. **`StyleQuantizer` runs
automatically inside `FrameRenderer`**: extended underline → Single, colored underline → default,
unsupported attributes dropped, colors quantized per tier — so links can emit styled/colored underlines
unconditionally; §7.1's capability gating needs **zero app code** (only cosmetic substitutes, e.g.
strikethrough on Ansi16 vanishes rather than simulating — key a dim fallback off
`TextStylingCapabilities.Strikethrough` if desired). `TextMarkup` string syntax lacks underline-shape/
color/hyperlink tags — build `TextRun` inlines programmatically (the editor does anyway).

## 6. Images (§7.2 foundation)

- **Closer than expected:** `ImagePresenter` + a templated `Image` control with `PlaceholderContent`/
  `PlaceholderTemplate` and a `:placeholder` pseudo-class already exist — nearly the spec's chip model.
  Sanctioned draw path: `RenderContext.DrawContent(Rect, IContent)` with a **cached**
  `Cursorial.Rendering.Content.Image` (fresh content per frame churns Kitty image ids).
- Fragment anchors recompute **every composite** (scroll frames included) — no drift; cells + fragments
  emit inside one synchronized-output bracket. Clipping: Kitty source-rect re-placement; sixel pixel-crop
  re-encode; iTerm2 none → whole-image withdraw (the spec's placeholder-only posture falls out for free).
- **Swap blocker:** stop-drawing does not remove the fragment (FB-13) — fix `Scene.ClearToTransparent`
  fragment parity in the framework before M7.
- Costs: Kitty move = delete + PNG retransmit; any Overlay fragment disables SU/SD scroll detection
  (Kitty only — sixel/iTerm2 are Cells-layer). The spec's withdraw-on-scroll policy sidesteps both.
- PNG decoder: 8-bit non-interlaced; paletted/grayscale variants can throw on the sixel RGBA path →
  placeholder fallback (FB-11); JPEG/GIF only where the terminal itself decodes (iTerm2/WezTerm).
- Popup/window occlusion over images is compositor-handled (`SubtractOccluder`) — no app work.

## 7. Theming & capability selection (§18)

- **App-authored XAML theme tokens work end-to-end:** `ResourceDictionary.ThemeDictionaries` keyed by
  `ThemeVariantKey` (`"Dark+Ansi256"`, `"NoColor"`, …) parse in the runtime loader and lower in the X4
  generator (variant keys must be plain strings; entry keys may be `{x:Static}`). Tier descent never
  ascends: author RGB at `(Base, Ansi256)` to serve truecolor by descent, hand-pick `Ansi16`, collapse at
  `NoColor` — the editor's `Md.*` tokens (H1–H6, callout tints, code roles) are just new keys. Template:
  `Cursorial.UI.Themes/Themes/Default/Palette.xaml`. Layer over `CursorialDefaultTheme.LoadTheme()` via
  `MergedDictionaries`, or put tokens in `UIApplication.Resources` (probed before Theme). The NoColor
  "no info by color alone" contract is authorial discipline, not enforced.
- **Capability overrides (Options page):** color tier → `RequestedColorTier`; Nerd Font →
  `NerdFontAvailable` (both persist natively). Other axes → `app.OnCapabilitiesChanged(app.Capabilities
  with {...})` + re-apply in a `CapabilitiesChanged` handler to survive renegotiation; forcing OFF is
  clean, forcing ON is styling-only (can't make the session speak an unnegotiated protocol). Cleaner
  long-term: FB-5. Note resize does **not** renegotiate; only explicit `RenegotiateAsync` does.

## 8. Bars (§8)

`BarCommand` (define-once: Text/Icon/InputGestureText/IsCheckable/Description) auto-fills toolbar/ribbon
controls via `BarCommandSync`; `SuperTip` auto-provisions from `Description`. `Toolbar` discrete overflow
(`»` popup band, per-item `OverflowMode`), `Ribbon` tabs/groups/density collapse/**contextual tabs**
(visibility-bound — the Table Tools tab), Backstage, QAT (raises `QuickAccessMoreCommandsRequested`; no
dialog/persistence supplied), minimize. KeyTips: `UIApplication.EnableKeyTips()`; menu-bar KeyTips are
single-level in v1. Menus exist (`Menu`/`MenuItem`/`ContextMenu`) but need `BarMenuItem` (FB-2) for
command parity. Bars theme tokens live on the shared `ThemeKeys` spine.

## 9. Testing & project wiring

- **Verified empirically:** a test project can `ProjectReference` `Cursorial.UI.Testing` (HEAD) alongside
  the app's 0.3.1 `PackageReference`s — NuGet project-over-package unification yields a single
  `Cursorial.UI` identity (no strong naming, no bind failures); test binaries exercise project-built
  framework code. To kill drift risk, ProjectReference `Cursorial.UI.Bars`/`Cursorial.UI.Xaml` in the
  test graph too. A source-copy of the 5 Testing files is fragile (IVT is keyed to the assembly name).
- `UITestHost`: calling thread = UI thread; `RunFrame`/`RunUntilIdle`/`AdvanceTime` on a fake clock;
  `SendKey/SendText/SendClick/SendResize/SendBytes` (real `VtInputDevice` on the fake clock); cell/row/
  byte assertions; teardown capture. Presets: `KittyTruecolor`, `Ansi16Legacy`, `NoMotion`,
  `NoMouseCursorShape`, `KittyGraphics`, `SixelGraphics`, `ITerm2Graphics`; compose Ansi256/NoColor via
  `with` expressions (`ColorDepth` includes both). Copy the benchmark-gate pattern
  (`[Trait("Category","Benchmark")]`, GC-asserted zero-alloc, frame-budget asserts) for the §13 targets.

## 10. App bootstrap (from Gallery / XamlAot)

`UIApplication.CreateBuilder()...Build()` → `await app.RunAsync(() => root)` (factory overload; root
constructed on the UI thread; single root element works — no Window required; `Window` +
`ShowDialogAsync<T>` for dialogs, cancellation **throws** OCE). `.xaml` via `<CursorialXaml Include=…/>`
(generator targets auto-imported from the NuGet analyzer package; files also embed for runtime loading);
`x:Class` + `InitializeComponent()` codebehind with typed `x:Name` fields; `PublishAot` flips
`CursorialXamlStrictAot` (reflection-free path — proven by the XamlAotStrict demo). MVVM: DataContext +
implicit `DataTemplate` by VM type (Gallery's Shell/pages pattern). Signal safety: parameterless
`TerminalSession.OpenAsync` path registers SIGINT/SIGTERM/SIGHUP restore (modulo FB-4's alt-screen gap).

## 11. Design references (Tokyo Night mockups)

`docs/ui-layer-design/` in the Cursorial repo: `…markdown-editor.html` is the primary visual spec (layout,
active-line well, reveal-on-edit, table growth, insert-table picker, rendered-vs-raw, status bar/dirty
dot, **the fixed-`ch` table-cell width lesson** — spec §5.1 [CRITICAL]); `…rich-editors.html` (input/field
styling), `…bars-design-guide.html` + toolbar/ribbon (whole-cell contract, §8 surfaces),
`…colorpicker-filedialogs.html` (file-dialog design for the §10.1 build), `…ide-shell.html` (split-pane
mechanics for §4.2), `…borderless-windows.html` (the message-box archetypes §2.3 callouts reuse — note:
the spec's "[REF: windows-dialogs mockup]" means this file; no file of that name exists),
`…control-gallery-final.html` (state conventions: text-control focus = well + caret; pickable = reverse
video — the §18.3 non-color channels).
