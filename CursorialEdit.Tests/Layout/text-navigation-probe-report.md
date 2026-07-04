# R4 probe — text-navigation divergence report (M1.WP6)

**Date:** 2026-07-04 · **Suite:** `CursorialEdit.Tests.Layout.TextNavigationProbeTests` (+ the pure
suite `CaretNavigatorTests`) · **Result: behavioral parity — zero navigator↔TextBox divergences
observed; 63/63 tests green on both build configurations and both §5.1 wire presets.**

This is the checked-in record the M1 exit gate names ("`TextNavigationProbeTests` divergence report
… checked in"). The companion FB-1 upstream-vs-keep decision note is in
`docs/framework-feedback.md` under FB-1.

## 1. Method

A **real multi-line `TextBox`** (`AcceptsReturn = true`, `TextWrapping = WordWrap` — the framework's
reference text surface, internally driven by `GraphemeLayout`/`TextLayout`/`TextNavigation`) is the
root of a 30×10 `UITestHost` terminal (wrap budget 28 cells after the template's one-column padding
per side), run under **both** `KittyTruecolor` and `Ansi16Legacy`. The app-side
`CursorialEdit.Layout.CaretNavigator` is evaluated over **identical fixtures**, and every landing is
compared through two observables:

- **Logical landing** — the public `TextBox.CaretIndex` after each keystroke, vs the navigator's
  returned col.
- **Visual landing** — where the affinity flag is the only distinguisher (a col on a soft-wrap
  boundary belongs to *two* visual positions), TextBox's caret is observable only via rendering, so
  the probe asserts the **terminal cursor** (`FrameBuffer.CursorColumn/CursorRow`, published by
  `TextPresenter` through `ITerminalCaretService`) against the navigator's `(row, cell)`/affinity,
  as chrome-independent deltas from an empirically captured origin.

Key expectations are additionally **hand-computed and pinned** (exact cols/cells in assertions), so
a shared bug in navigator *and* reference cannot silently pass as "parity".

**Fixtures:** ASCII prose; decomposed combining mark (`e`+U+0301); CJK (`漢`, `汉字…`); emoji
(`👍`); VS16 sequence (`❤️` = U+2764+FE0F) and VS15 counterpart; ZWJ family
(`👨‍👩‍👧‍👦`, 11 UTF-16 units, one 2-cell cluster); mixed-script lines; punctuation-adjacent and
unspaced-CJK word fixtures; three wrap-boundary lines (break-after-whitespace, short middle row,
wide cluster straddling the wrap edge).

## 2. Parity matrix

| Motion class | Fixtures | TextBox observable | Verdict |
|---|---|---|---|
| Left/Right cluster steps | full mixed-cluster walk, both directions | `CaretIndex` + cursor cell each step | **Parity.** Boundaries 0,1,3,4,6,8,19,20; cells 0,1,2,4,6,8,10,11 — never inside a cluster, wide clusters advance 2 cells |
| Ctrl+Left/Right word motion | punctuation, em-dash, unspaced CJK, emoji runs | `CaretIndex` each stop | **Parity** (semantics: §3.1/§3.2) |
| Soft-wrap segmentation | break-after-whitespace; 2-cell cluster at the edge | rendered row text | **Parity.** Trailing space stays on the row; a wide cluster never straddles the edge (moves whole) |
| Up/Down goal column | 3-row wrapped line with a 3-cell middle row; CJK row | `CaretIndex` + cursor (row, cell) | **Parity.** Goal column is sticky in cells across a shorter row; a goal inside a wide cluster snaps at-or-before; Up restores the exact goal cell |
| Home/End affinity | wrap-boundary col aliasing | cursor row (the only observable) | **Parity.** End on a wrapped row lands the *same col* as the next row's Home but renders on the earlier row; Right/Home/Down consume the affinity exactly as `TextLayout.Locate(…, preferLineEnd)` specifies |
| Vertical clamp at edges | Up at first row / Down at last row | `CaretIndex` unchanged | **Parity** (semantics: §3.3) |

**Navigator bugs found by the probe: none.** The navigator was written as a deliberate,
semantics-faithful port of the internal reference (see §4) after source study; the probe's standing
value is pinning that port against drift on either side.

## 3. Semantics findings (documented divergences from *expectations*, not from TextBox)

Each has a dedicated `[Fact]`/`[Theory]` in the probe suite.

### 3.1 Word classifier: whitespace-delimited, not letters/digits-vs-punctuation — **deliberate**

The implementation plan's WP6 gloss describes words as "letters/digits runs vs space/punctuation".
`TextBox`'s actual classifier (`TextNavigation`) is **whitespace-delimited**: skip a whitespace run,
then a non-whitespace run. Consequences (probed, `Divergence_WordClassifier_…` +
`CtrlRightThenCtrlLeft_…`): punctuation adheres to its word (`"foo,"` is one landing unit — from
col 0, a letters/digits classifier would stop at 3 or 5; TextBox lands at 4); `bar—baz` is one word;
**unspaced CJK is a single word** (no per-ideograph stepping). **Verdict:** the navigator mirrors
TextBox — the probe's parity contract is the binding requirement and dialogs/find-bar `TextBox`es
should not feel different from the document surface. A richer (markdown-aware) classifier is an
M2 run-map/M4 decision, recorded in the FB-1 note.

### 3.2 Ctrl+Right lands at the *end* of the word run — **deliberate (TextBox parity)**

TextBox's `NextWord` lands before the following whitespace (end of the current run), not at the
next word's start (the WPF/VS convention). Walk over `"foo, bar—baz 漢字 end"`: 0→4→12→15→19.
Mirrored exactly; flagged here because editor users may expect the WPF landing — if product wants
that, it is an app-side change in *both* surfaces via FB-1's upstream types, not a navigator fork.

### 3.3 Up/Down at the first/last visual row clamps to the same row — **deliberate (TextBox parity)**

TextBox clamps the target row (`MoveVertical` → `Math.Clamp`), so Up at the top row / Down at the
bottom row is a no-op at the goal column — the caret does not jump to line start/end as in some
editors. The navigator mirrors this **within a line**; document-level cross-line motion (Up from a
line's first visual row lands on the previous line's last row, etc.) is M1.WP8 composition on top
of `WrappedLine.MoveVertical`, so nothing is lost.

### 3.4 Cluster-pinned word landings — **no observable divergence**

`TextNavigation` scans `char`-wise and can in principle produce a mid-cluster offset (whitespace
base + combining mark); TextBox then floor-pins in `SetCaretAndSelection` (`PinToBoundary`). The
navigator pins **inside** `NextWord`/`PrevWord` instead, so its *API result* already satisfies the
M1 invariant "caret col is always a cluster boundary" (swept property test,
`WordMotion_ResultsAreAlwaysClusterBoundaries`). Same observable landings.

## 4. Reimplementation surface (the FB-1 evidence)

To reach parity, the app had to re-derive, from **internal** framework source, essentially all of:

| Internal type (Cursorial.UI, `internal`) | What had to be mirrored | App-side counterpart |
|---|---|---|
| `GraphemeLayout` (~113 LOC) | cluster boundary tables, `PrevBoundary`/`NextBoundary`/`PinToBoundary`, col↔column maps, `CharIndexAtOrBefore/AfterColumn` | `CaretNavigator` boundary + cell functions |
| `TextLayout` (~278 LOC) | wrap segmentation (break opportunity **after** a whitespace cluster, trailing space stays on the row, WordWrap vs WordWrapOverflow over-long-word treatment, CharacterWrap), `Locate(…, preferLineEnd)` end-affinity resolution, `OffsetAt` floor-snap, the 4-clause `IsLineEndBoundary` aliasing predicate | `CaretNavigator.Wrap` + `WrappedLine` |
| `TextNavigation` (~20 LOC) | whitespace word runs + the caller-side boundary pin | `NextWord`/`PrevWord` |
| `TextPresenter.MoveVertical` protocol | sticky goal column in **cells** (reset on any non-vertical op, restored across the run), affinity threading | `WrappedLine.MoveVertical` |

Net: **423 lines** of app code (`CursorialEdit/Layout/CaretNavigator.cs`, inside the WP6 ~500-line
bound) duplicating ~410 lines of framework internals, plus 725 lines of tests whose primary job is
keeping the two implementations from drifting. Only `GraphemeWidth`/`GraphemeEnumerator` were
consumable as public API.

## 5. Conclusion

Parity is achieved and pinned. The duplication itself is the finding: three internal types are
public-shaped, needed verbatim by any custom text surface, and every future upstream change to them
silently forks `TextBox` behavior away from this editor's document surface unless this probe
catches it. **Recommendation — promote FB-1 upstream** (details + what it saves M2 in the FB-1
decision note); on landing, delete the mirrored logic per the plan ("delete ours if it lands") and
keep the probe suite as the regression tripwire during the swap.
