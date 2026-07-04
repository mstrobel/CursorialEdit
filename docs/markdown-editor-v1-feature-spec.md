# Terminal Markdown Editor — v1.0 Feature Specification

**Status:** Ready for implementation planning
**Audience:** Implementing agent + maintainer
**Framework:** C# / XAML terminal UI toolkit (WPF- & Avalonia-compatible), whole-cell rendering, Tokyo Night theming
**Purpose of this document:** Define the complete v1.0 *feature set and behavior* so an agent can plan and build without inferring product decisions. It intentionally does **not** prescribe the object model, class layout, or internal algorithms — those are the implementer's to design. Where behavior is subtle, this document makes the decision explicitly rather than leaving it open.

---

## Contents
0. How to read this spec
1. Product summary & goals
2. Markdown dialect & rendering scope
3. Editing model — cursor, selection, undo
4. WYSIWYG behavior & the source/render duality
5. Tables — the headline feature
6. Block & inline editing operations
7. Links, images, footnotes & interactive elements
8. Command surface — Bars integration
9. Find & replace (with regex)
10. Documents, files & export
11. Document outline / TOC navigator
12. Autosave & crash recovery
13. Performance & responsiveness targets
14. Deferred (explicitly out of v1.0)
15. Configuration
16. Suggested milestone plan
17. Open questions for the maintainer
18. Capability gating & graceful degradation

---

## 0. How to read this spec

- **[IN]** — in scope for v1.0. Must be implemented.
- **[DEFER]** — explicitly considered and cut from v1.0. Do **not** build now; noted so the boundary is deliberate, not an oversight. Design should not actively preclude these.
- **[DECISION]** — a behavioral choice made here to remove ambiguity. Implement as stated unless the maintainer overrides.
- **[EDGE]** — an edge case an implementer would otherwise coin-flip on; the resolution is given.
- **[REF]** — refers to an existing mockup/artifact in the component suite that already demonstrates the intended look or behavior.
- **[CAP: name]** — behavior that depends on a terminal capability. The named capability is a root-level style class (see §18) resolved as *detect → user-override wins*; the editor selects on it rather than assuming it. Specific color/attribute fallback **values** are the maintainer's to annotate; this document scaffolds where the gate applies.

Acceptance criteria are listed per feature area as checkboxes; v1.0 is "done" when all **[IN]** criteria pass.

---

## 1. Product summary & goals

A WYSIWYG markdown editor that runs in the terminal, built as the v1.0 showcase/dogfood application for the framework. It will be used to author the framework's own documentation.

**Primary goals**
1. Author markdown as rendered content — headings, emphasis, lists, tables, etc. appear in their formatted state in the same character grid, not as raw syntax.
2. Exercise the framework end-to-end: Bars (ribbon + toolbar), the control suite, text rendering, focus/caret, theming.
3. Never lose the user's work.

**Non-goals for v1.0** (see §14 for the full deferred list): multi-document tabs, collaborative editing, spell check, plugin system, arbitrary HTML rendering, export to formats other than the ones listed in §10.

**Guiding constraint — whole-cell rendering.** Everything the editor draws obeys the framework's whole-cell discipline: integer cell widths, no sub-cell borders, box-drawing glyphs and the i-beam text cursor are the sanctioned sub-cell primitives. Table column widths, wrap points, and cursor positions are all reasoned in cells. [REF: the whole-cell contract in the Bars design guide; the markdown-editor and rich-editors mockups.]

**Guiding constraint — capability gating.** The editor runs on terminals of widely varying capability (color depth, styled underlines, image protocols, glyph coverage). Rendering is **capability-gated via root-level style classes** (`caps-*`), not imperative branching — features select on the resolved capability set the same way the framework's theming does. §18 is the authoritative catalog; every **[CAP: …]**-tagged behavior in this document degrades per that section. Iconography specifically is delegated to the framework's **Icon element** (three-tier degradation — §18.4), so features request an icon rather than hardcoding a glyph.

---

## 2. Markdown dialect & rendering scope

v1.0 targets **GitHub Flavored Markdown (GFM) plus a defined set of extensions.** Each construct below is marked by how it is treated in the editing surface:

- **Rendered** — displayed in formatted form (WYSIWYG); raw marks reveal only on the active line (§4).
- **Mark-visible** — shown with its syntax characters styled but not hidden (no meaningful "rendered" form in a cell grid).
- **[DEFER]** — not supported in v1.0.

### 2.1 CommonMark core — all [IN], Rendered
| Construct | Rendering notes |
|---|---|
| ATX headings `#`..`######` | Six levels; distinct color + weight per level **[CAP: caps-nocolor → weight/underline only; §18.3]**. The `#` marks reveal on the active line. |
| Setext headings (`===`/`---` underline) | **[DECISION]** Parsed and rendered as H1/H2, but **normalized to ATX on save** unless the maintainer wants round-trip fidelity. **[EDGE]** A line of `---` is a horizontal rule unless it immediately follows a paragraph line (then setext H2) — follow CommonMark disambiguation exactly. |
| Bold `**`/`__`, Italic `*`/`_`, Bold-italic | Rendered with weight/slant. |
| Inline code `` ` `` and `` `` `` `` `` | Rendered with code background fill. |
| Fenced code blocks ``` ``` ``` / `~~~` | Rendered as a filled block; language tag captured. Syntax highlighting = **[DECISION]** v1 highlights a **small built-in set** (the framework's own languages: C#, XAML, JSON, Markdown, shell); unknown languages render monochrome in the code fill. Broader highlighting is [DEFER]. |
| Indented code blocks (4-space) | Rendered as code block. **[DECISION]** Normalized to fenced on save. |
| Blockquotes `>` (incl. nested) | Rendered with a `▌` quote bar per nesting level. |
| Unordered lists `-`/`*`/`+` | Rendered with a bullet glyph + indent. **[DECISION]** Bullet marker normalized to `-` on save. |
| Ordered lists `1.`/`1)` | Rendered with the number + indent; renumbering per §6. |
| Nested lists | Full nesting; indent per level in cells. |
| Links `[text](url)` incl. titles | Rendered as underlined link text; URL revealed on active line / in a hover affordance (§7). |
| Reference links `[text][ref]` + definitions | Rendered as links; definitions are mark-visible. |
| Autolinks `<url>` | Rendered as links. |
| Images `![alt](url)` | **[DECISION]** Rendered as a **placeholder chip** (an Icon §18.4 + alt text). Where capabilities allow (`caps-images` + a teardown path), the decoded image paints over the placeholder while the region is inert and withdraws on cursor/selection/scroll — the **image⇔placeholder swap**, full spec in §7.2 (capability-gated; iTerm2 placeholder-only in v1). Placeholder is always the addressable source of truth. |
| Hard line breaks (two trailing spaces / `\`) | Honored. **[DECISION]** Rendered invisibly; on the active line, show a trailing `↵` glyph so the user can see it exists. |
| Horizontal rules `---`/`***`/`___` | Rendered as a full-width rule of box-drawing cells. |
| Backslash escapes, entity refs | Honored per CommonMark. |

### 2.2 GFM extensions — all [IN], Rendered
| Construct | Rendering notes |
|---|---|
| **Tables** (pipe tables) | Rendered as live box-drawing grids. This is the headline feature — full spec in §5. |
| Task list items `- [ ]` / `- [x]` | Rendered with a checkbox **Icon** (§18.4); **toggling is a click/keyboard action** that edits the source (§6). Completed items get strikethrough + dim. |
| Strikethrough `~~text~~` | Rendered struck-through. |
| Autolinked URLs (bare) | Bare `https://…` in text becomes a link. **[EDGE]** Only linkify schemes `http`, `https`, `mailto`, `www.` — not arbitrary text. |

### 2.3 Extensions beyond GFM — all [IN]
| Construct | Treatment | Notes |
|---|---|---|
| **Footnotes** `[^id]` + `[^id]: def` | Rendered | Reference renders as a superscript-style marker (e.g. `⁴` or `[4]` styled); definition block rendered at its location with a back-reference. **[DECISION]** v1 renders footnotes **in place** (no auto-collection to document bottom); collection/reordering is [DEFER]. |
| **Definition lists** (`Term` / `: definition`) | Rendered | Term in emphasis, definition indented. Use PHP-Markdown-Extra syntax. |
| **Callouts / admonitions** | Rendered | **[DECISION]** Support the GitHub blockquote-alert syntax: `> [!NOTE]`, `> [!TIP]`, `> [!IMPORTANT]`, `> [!WARNING]`, `> [!CAUTION]`. Rendered as a callout box (quote-bar + title + type **Icon** §18.4), color keyed to type **[CAP: caps-nocolor → title + Icon carry meaning, no fill color; §18.3]**. Reuses the message-box vocabulary from the suite. [REF: message-box archetypes in the windows-dialogs mockup.] |
| **Math** | Rendered (best-effort) | **[DECISION]** Inline `$…$` and block `$$…$$` are **recognized, captured, and rendered mark-visible with a distinct "math" style** in v1 — the editor does *not* typeset LaTeX into glyphs (out of scope for a cell grid). The math source is shown in a monospace math color so it's clearly a math span. Actual typesetting (Unicode-math approximation) is [DEFER]. **[EDGE]** A lone `$` (currency) must not start a math span — require no space after opening `$` and no space before closing `$`, per common math-extension rules. |
| YAML front matter (`---`…`---` at file start) | Mark-visible | Recognized as a metadata block, rendered in a dim "front matter" style, folded by default with an expand affordance. Editing allowed as raw text. |

### 2.4 Explicitly unsupported in v1.0 — [DEFER]
- **Raw inline/block HTML** — **[DECISION]** HTML tags in the source are **passed through and rendered mark-visible (as literal text, dimmed)**, *not* interpreted. No HTML rendering. This keeps the renderer bounded. Flagged so the agent does not attempt an HTML subsystem.
- Wiki-links `[[…]]`, mermaid/diagram fences, emoji shortcodes `:smile:`, custom container directives `:::`, table of contents auto-generation markers, include/transclusion. All [DEFER].

**Acceptance criteria — §2**
- [ ] Every construct marked *Rendered* displays in formatted form and round-trips through open→edit→save without corrupting unrelated content.
- [ ] Normalizations in [DECISION] rows are applied on save and documented in the user-facing changelog.
- [ ] Unsupported constructs (§2.4) never crash the parser; they render as dimmed literal text.
- [ ] A conformance document lists exactly which CommonMark/GFM spec sections pass (link the spec section numbers).
---

## 3. Editing model — cursor, selection, undo

**[DECISION] v1.0 is classic single-cursor, single-selection.** No multi-cursor, no column/block selection. Deferred to keep the caret/selection and undo logic tractable while the core stabilizes.

### 3.1 Cursor
- One insertion point, rendered as the framework's **i-beam cursor** (bar at the cell's leading edge, blinking). [REF: i-beam cursor conversion across the suite.]
- Movement: arrows, Home/End (line), Ctrl+Home/End (document), Ctrl+←/→ (word), PageUp/PageDown (viewport).
- **[DECISION] The cursor navigates the *rendered* document, not the raw source.** Moving right through **bold** text moves character-by-character over the visible letters; the hidden `**` marks are skipped unless the active line has revealed them (§4). Word-motion uses rendered text word boundaries.
- **[EDGE]** Moving the cursor onto a line containing a table enters the table's first cell (§5.4 for in-table navigation). Moving onto a rendered image placeholder treats the whole placeholder as one caret stop.
- **[EDGE]** Vertical movement (↑/↓) preserves a "goal column" measured in cells, standard editor behavior; landing in a table snaps to the nearest cell.

### 3.2 Selection
- Single contiguous range. Shift+motion extends; mouse drag selects; Ctrl+A selects document; double-click selects word; triple-click selects block/paragraph.
- Rendered with the selection fill from the theme.
- **[DECISION] Selection operates on rendered content but maps to a source range.** Copying a selection that spans **bold** text copies the source `**text**` to the clipboard (see §3.4 clipboard formats). Selecting half of a rendered table cell selects within that cell only.
- **[EDGE]** A selection that partially covers a formatting span (e.g. starts mid-bold) — on cut/delete, the source is edited at the true source offsets; the renderer must re-derive spans afterward. Applying **bold** to a selection already partially bold **[DECISION]** toggles toward bold (makes the whole selection bold) rather than un-bolding, matching common editor behavior.

### 3.3 Undo / redo
- **[DECISION] Undo granularity is by *edit group*, not keystroke.** A run of typed characters coalesces into one undo unit until broken by: a cursor move, a newline, a command (bold, etc.), or an idle timeout (**[DECISION]** ~750 ms). Each structural command (insert table, toggle checkbox, format) is its own undo unit.
- Unlimited depth within a session (bounded only by memory; **[DECISION]** cap at a configurable N=1000 steps, oldest discarded).
- Redo stack cleared on new edit after undo (standard).
- **[EDGE]** Undo must restore cursor/selection position to where it was before the undone edit.

### 3.4 Clipboard
- **[DECISION] Copy/cut writes markdown source** for the selected range (so pasting into another markdown context preserves formatting).
- **[DECISION] Paste is smart:**
  - Pasting text containing markdown syntax inserts it and re-parses (so pasted `**bold**` becomes rendered bold).
  - Pasting into a table cell that contains newlines/pipes → **[EDGE]** newlines are converted to spaces and pipes escaped, to avoid breaking the row (§5).
  - Pasting a block of tab/comma-delimited text **onto an empty line** → **[DECISION]** offer to convert to a table (§5.2 paste-to-table). If declined, insert literally.
- Plain-text paste (Ctrl+Shift+V) bypasses markdown re-parse and inserts literally.

**Acceptance criteria — §3**
- [ ] Cursor traverses rendered text correctly, skipping hidden marks except on the active line.
- [ ] Copy of a formatted selection yields correct markdown source on the clipboard.
- [ ] Typing a sentence then undoing removes the whole sentence as one unit; pressing undo again removes the prior group.
- [ ] Smart paste renders pasted markdown; plain paste does not.

---

## 4. WYSIWYG behavior & the source/render duality

**[DECISION] v1.0 uses the *hybrid* model:** the primary surface is WYSIWYG with reveal-on-edit, **plus** a toggleable raw-source view.

### 4.1 Reveal-on-edit (the default WYSIWYG surface)
- Markdown constructs render formatted. **On the line (or block) containing the cursor, the raw syntax marks become visible** (dimmed) so the user can edit them precisely; when the cursor leaves, they re-hide and the line renders clean.
- **[DECISION] The reveal unit is the *block*, not just the physical line**, for multi-line constructs: placing the cursor inside a fenced code block reveals its fences; inside a table, the table stays in rendered form but the cell is editable (tables are a special case, §5); inside a multi-line blockquote, only the active line's `>` shows.
- **[EDGE]** Emphasis spanning the boundary: if a `**bold**` run is entirely on the active line, its `**` marks show. If bold spans from an inactive line into the active line, **[DECISION]** reveal the marks only on the active line's portion (partial reveal is acceptable; do not reflow other lines).
- **[EDGE]** When marks reveal, the line's cell width changes (the `**` now occupy cells). **[DECISION]** This is allowed to shift the active line's content horizontally; it must **not** reflow or shift any other line. The cursor stays on its intended character.

### 4.2 Raw-source view (toggle)
- A command/keybind (**[DECISION]** default `Ctrl+/` or a ribbon View toggle) switches the document between **Rendered** and **Raw Source** modes.
- **[DECISION] Two presentations, both [IN]:**
  1. **Full raw mode** — the entire document shown as plain monospace markdown source with syntax *coloring* (not rendering); good for power editing.
  2. **Split view** — rendered on one side, raw source on the other, scroll-synced, edits in either reflect live. **[DECISION]** Split can be vertical or horizontal; default vertical (side-by-side). [REF: the IDE-shell mockup's docking/split panes for the layout mechanics.]
- **[EDGE]** Cursor position is preserved across mode switches by mapping source offset ↔ rendered position.
- **[EDGE]** In split view, the two panes share one document model and one undo stack (they are views, not copies).

### 4.3 Active-line highlight
- The block containing the cursor gets a subtle whole-cell background tint (the `--well`/active-line treatment from the suite) in both rendered and split modes. [REF: markdown-editor mockup active line.]

**Acceptance criteria — §4**
- [ ] Cursor entering/leaving a construct reveals/hides its marks with no reflow of other lines.
- [ ] Toggling to raw source shows colored markdown; toggling back restores rendered view at the same cursor position.
- [ ] Split view stays scroll-synced and edits propagate live both ways on a single undo stack.

---

## 5. Tables — the headline feature

Tables render as **live box-drawing grids** that grow and shrink with content. The author edits cells directly and never touches a pipe. This is the single most important feature to get right and the primary showcase. [REF: the table-growth demo and rendered-vs-raw comparison in the markdown-editor mockup.]

### 5.1 Rendering
- A GFM pipe table renders as a bordered grid using box-drawing glyphs (`┌┬┐├┼┤└┴┘─│`).
- **[DECISION] Column widths are computed from content** — each column is as wide as its widest cell (header or body), measured in **cells** (`ch`), clamped to a configurable min (**[DECISION]** 3 cells) and max (**[DECISION]** default 40 cells; content beyond wraps or truncates per §5.6).
- **[CRITICAL] Width must be reasoned in cells, not pixels or character counts.** Box-drawing glyphs and text glyphs can differ in advance width in a fallback font; the implementation must pin each cell's width so borders and content align in any font. [REF: this exact bug was hit and fixed in the markdown-editor mockup by using fixed `ch`-width cells — carry that lesson forward. Do not assume equal character counts yield equal widths.]
- Alignment per column (GFM `:---`, `:--:`, `--:`) → left/center/right, honored in rendering and preserved on save.
- **[DECISION]** Zebra striping on body rows is optional and **off by default** (configurable); header row is always distinct (bold + fill).

### 5.2 Creating tables
- **Insert Table command** (ribbon Insert group / keybind): opens a **size picker** (drag-to-size grid, e.g. hover 3×3). [REF: insert-table size picker in the mockup.] On commit, inserts an empty grid of that size with placeholder header cells, cursor in the first cell.
- **Paste-to-table** (§3.4): pasting tab- or comma-delimited multi-line text onto an empty line offers conversion to a table, inferring columns from the delimiter and the first row as header.
- **[DECISION] Markdown-typing shortcut:** typing a pipe row like `| a | b |` and pressing Enter recognizes the intent and **[DECISION]** converts it to a rendered table (GFM requires a delimiter row; the editor auto-inserts the `|---|---|` row). This makes tables discoverable to markdown users.

### 5.3 Table editing operations — all [IN]
Each must be available via keyboard **and** a context menu / ribbon when the cursor is in a table:
| Operation | Behavior / default binding |
|---|---|
| Edit cell text | Type in the focused cell; grid reflows live as width changes (§5.5). |
| Next / previous cell | `Tab` / `Shift+Tab`; wraps to next row; **[EDGE]** `Tab` in the last cell of the last row **creates a new row** and moves into it. |
| Move between rows | `↑`/`↓` move to the cell above/below in the same column (goal-column preserved). |
| Insert row above / below | Command + binding (e.g. `Alt+↑`/`Alt+↓` inserts). New row cells empty. |
| Insert column left / right | Command + binding. New column cells empty; alignment defaults to left. |
| Delete row | Command; **[EDGE]** deleting the header row promotes the next row to header (a GFM table must have a header). |
| Delete column | Command; if it's the last column, **[EDGE]** deleting it deletes the whole table. |
| Move row up / down | Reorders rows. |
| Move column left / right | Reorders columns (carries alignment). |
| Set column alignment | Left / center / right, per column; updates the delimiter row on save. |
| Delete table | Removes the whole construct. |
| Clear cell | `Delete` on a selected cell empties it without removing structure. |

### 5.4 In-table navigation & selection
- The cursor moves cell-to-cell (Tab/arrows) and character-by-character within a cell.
- **[DECISION]** Selecting across cells selects whole cells (a rectangular cell selection *within the table only*) — this is the one place a block-like selection exists, and it is scoped to tables, not the general editor. Copying a cell selection yields the corresponding markdown sub-table.
- **[EDGE]** `Enter` inside a cell does **not** insert a newline (GFM cells are single-line); instead **[DECISION]** `Enter` commits and moves to the cell below (or out of the table if on the last row). To put a line break in a cell, use the GFM convention `<br>` — inserted via a command, shown as `↵` in the cell.

### 5.5 Live reflow (the growth/shrink behavior)
- **[DECISION] On every cell edit, recompute affected column widths and repaint.** Typing a character that makes a cell the new widest in its column widens that column and redraws the grid's borders; deleting the widest content shrinks it back.
- **[DECISION] Reflow is incremental where possible:** only the column whose width changed (and the border cells) need redraw, not the whole document. Rows added/removed extend/contract the grid vertically. Performance target in §13.
- **[EDGE]** A very wide table that exceeds the viewport width scrolls horizontally within its own region (§5.6), or wraps cell content (§5.6) — configurable; default is **wrap long cells** to keep the table within the text column.

### 5.6 Overflow & wrapping
- **[DECISION]** When a column would exceed its max width (§5.1), its cell content **wraps within the cell** (the row grows taller, cell text flows to multiple lines inside the box). Alternative: **truncate with `…`** and reveal full text on cell focus — **[DECISION]** wrapping is the v1 default; truncation is a configurable mode.
- **[EDGE]** A table wider than the whole viewport even after wrapping: the table region gets its own horizontal scroll; the rest of the document does not scroll horizontally.

**Acceptance criteria — §5**
- [ ] Inserting a table via the picker, typing into cells, and Tab-through-to-new-row all work; borders stay aligned in the app's default font.
- [ ] Every operation in §5.3 works from both keyboard and menu, and the resulting markdown source is valid GFM.
- [ ] Typing a longer value visibly widens the column and redraws borders without disturbing surrounding document lines.
- [ ] Deleting the widest cell content shrinks the column back.
- [ ] Column alignment round-trips (render → save → reopen) correctly.
- [ ] A table wider than the viewport behaves per the configured overflow mode without breaking layout.

---

## 6. Block & inline editing operations

Beyond raw typing, these are the structured-edit commands. All **[IN]**. Each needs a keybind, a ribbon control, and (where sensible) a markdown-typing shortcut.

### 6.1 Inline formatting
| Command | Binding | Markdown shortcut | Toggle behavior |
|---|---|---|---|
| Bold | Ctrl+B | `**` around selection | Toggles; applies to whole selection if mixed (§3.2). |
| Italic | Ctrl+I | `*` | Toggles. |
| Strikethrough | Ctrl+Shift+X | `~~` | Toggles. |
| Inline code | Ctrl+` | `` ` `` | Toggles; **[EDGE]** if selection contains a backtick, use double-backtick fence. |
| Link | Ctrl+K | `[]()` | Wraps selection as link text, opens URL entry (§7.1). |
| Clear formatting | Ctrl+Space (or command) | — | Strips inline marks from selection. |

### 6.2 Block formatting
| Command | Behavior |
|---|---|
| Heading level 1–6 | Applies/changes ATX heading on the current block; a "cycle heading" command and a level picker (ribbon Style combo) both exist. Typing `## ` at line start auto-applies (markdown shortcut). |
| Paragraph / body | Removes block formatting, returns to plain paragraph. |
| Bullet list | Converts block(s) to `-` list; `Tab`/`Shift+Tab` indent/outdent nests; typing `- ` at line start auto-starts. |
| Ordered list | Converts to `1.` list; **[DECISION]** auto-renumbers on edit (deleting item 2 renumbers 3→2…). Typing `1. ` auto-starts. |
| Task list | Converts to `- [ ]`; toggling the checkbox (Space on the line, or click) flips `[ ]`↔`[x]`; typing `- [ ] ` auto-starts. |
| Blockquote | Wraps block in `>`; nesting supported; typing `> ` auto-starts. |
| Code block | Wraps selection in a fence; language can be set via a small picker; typing ```` ``` ```` auto-starts a fence. |
| Callout | Inserts/wraps as `> [!NOTE]` etc.; type selectable (Note/Tip/Important/Warning/Caution). |
| Horizontal rule | Inserts `---` as a full-width rule; typing `---`+Enter on an empty line auto-inserts. |
| Insert footnote | Inserts a `[^n]` reference + a definition stub, cursor in the definition. |
| Insert table | §5.2. |
| Insert link / image | §7. |

### 6.3 Smart editing behaviors
- **[DECISION] List continuation:** pressing Enter in a list item creates the next item with the same marker/indent; pressing Enter on an *empty* item ends the list (outdents to paragraph). Same for blockquotes and task lists.
- **[DECISION] Auto-pairing:** typing `*`, `` ` ``, `[`, `(`, `_` around a selection wraps it; typed with no selection, **[DECISION]** auto-pairing of brackets/backticks is **on for `` ` ``, `[`, `(`** and **off for `*`/`_`** (too noisy in prose). Configurable.
- **[DECISION] Indent behavior:** `Tab` in a list nests; `Tab` in a normal paragraph inserts **[DECISION]** the configured indent (default: convert to spaces, width 2) — but **[EDGE]** if the whole document is being written as prose, a leading `Tab` should not silently create an indented code block; require an explicit command for code-block-via-indent, and treat stray tabs as spaces.
- **[DECISION] Smart quotes / dashes:** **off by default** in v1 (markdown authors often want literal characters); available as a toggle. 

**Acceptance criteria — §6**
- [ ] Every command applies and round-trips to valid markdown.
- [ ] Markdown-typing shortcuts (`## `, `- `, `> `, `1. `, ```` ``` ````, `- [ ] `) auto-apply as described.
- [ ] List/quote continuation and empty-item termination behave as specified.
- [ ] Ordered lists renumber on edit.

---

## 7. Links, images, footnotes & interactive elements

### 7.1 Links
- Rendered as underlined link text **[CAP: caps-nocolor → underline + weight carry link state, no color; styled/colored underline is itself capability-gated — see §18]**. **[DECISION]** The URL is shown when the cursor is on the link (reveal-on-edit) and in a **status-bar readout** / hover tooltip.
- **Insert/Edit Link** dialog: fields for text + URL (+ optional title). [REF: file-dialog patterns in the colorpicker-filedialogs mockup for the dialog styling.]
- **[DECISION]** Following a link (open in browser/OS) is via an explicit **Open Link** command (e.g. Ctrl+Click or a context action), never on a plain click (plain click positions the cursor). **[EDGE]** For relative links to other local markdown files, **[DECISION]** Open Link opens that file in the editor **[DEFER if multi-doc is out]** — since tabs are deferred (§14), v1 opens it replacing the current doc *after the autosave/save-prompt*, or shows "linked file" info. Pick one: **[DECISION]** v1 shows an info toast with the resolved path and offers "Open (replace)".

### 7.2 Images & inline image display

**Placeholder is the source of truth; the displayed image is a capability-gated enhancement anchored over an always-addressable placeholder.**

Terminal image protocols occupy grid cells that the editor must otherwise keep addressable — the cursor lands there, selection highlights there, reflow moves them. An image sitting in the grid is opaque to all of that. Therefore images are a **display enhancement over a placeholder**, never editable grid content, and whether/how an image displays is **gated on capability classes (§18), not protocol names** — the editor selects on `caps-images`, `caps-image-occlusion`, and `caps-image-clipping`, so its behavior follows the framework's resolved capability set rather than hardcoding per-terminal logic.

**[DECISION] Model:**
- Every image is a **placeholder chip** in the document — an Icon (§18.4) + alt text as styled cells. The placeholder is canonical: all cursor, selection, reflow, and layout logic operate **only** on it. The image layer never participates in editing.
- When `caps-images` is present **and** a workable teardown path exists (`caps-image-occlusion` **or** `caps-image-clipping` — see profiles) **and** the region is **inert** (below), the editor paints the decoded image anchored to the placeholder's footprint. When the region becomes interactive, the image is withdrawn and the placeholder shows through — the **image⇔placeholder swap**. Swap *policy* is identical across terminals; only the *mechanism* differs, per capability.

**[DECISION] A region is *inert* (image may display) when all hold:**
1. The cursor is **not on the placeholder's line/block** (reveal-on-edit governs this boundary — §4).
2. **No selection intersects** the placeholder's character range.
3. The document is in a mode that permits paint — rendered view and the rendered pane of split view; **never** full raw-source mode (raw shows literal `![alt](url)`).
4. The placeholder's paint region is displayable given clipping capability — see the scroll/clip rule below.

**[DECISION] Swap triggers (image → placeholder) — withdraw the instant any occurs:** cursor enters the placeholder's line/block; a selection is extended over the placeholder (so the highlight renders on the placeholder cells, not against an opaque image); switch to raw-source mode; the placeholder leaves the displayable region (see clipping); reflow/repaint in progress for that region (paint only after layout settles).

**[DECISION] Swap-back (placeholder → image)** occurs when the region returns to inert on the next settle tick (debounced ~one frame) so rapid cursor motion or drag-selection across many images doesn't thrash the protocol.

**[DECISION] Footprint & layout:** v1 keeps the placeholder a **single-line chip** (one row tall) even while an image is shown — the image paints in a bounded area anchored to the placeholder **without changing line count or reflowing text** (a multi-row image that shifts surrounding lines reintroduces the caret-math problem). Image scaled to a **[CAP-tunable]** max height (cells) × aspect-preserved width, clamped to the text column. Multi-row images with text flow are **[DEFER]**.

**[DECISION] Two orthogonal image capabilities drive the mechanics.** These are independent axes, not a single "protocol" switch — model them behind one `IImageBackend` abstraction whose implementations answer to the resolved classes:

- **`caps-image-occlusion` — does the image occlude cell content, or cohabit it?**
  - *Occlude (Kitty overlay plane):* the image composites **above** the cell grid, addressed by placement **id**; the text cells beneath are **never overwritten**. Teardown on swap = **hide/delete the placement** (id call); the placeholder cells beneath are already intact — **nothing to repaint**. The overlay does not scroll with the buffer on its own, so **[DECISION]** the backend repositions/hides placements to follow the placeholder's anchor as the document scrolls, hiding them when the anchor leaves the viewport.
  - *Cohabit (sixel — no occlusion):* the image **is** the content of its cells. Teardown on swap = **actively repaint the placeholder cells** (residue is otherwise left in the buffer — the primary artifact hazard). The image scrolls with its cells.

- **`caps-image-clipping` — can the framework show a partial image at the viewport edge?**
  - *Present (Kitty via placement geometry; sixel via RGB-payload manipulation):* on partial scroll, **clip the image to the visible region** rather than withdrawing it whole — smooth boundary scrolling.
  - *Absent:* on partial scroll (inert condition 4 fails), **withdraw the whole image** to its placeholder until it's fully in view again.

**[DECISION] Resolved v1 profiles** (the editor never branches on protocol name — these are what the capability classes evaluate to today):
| Terminal | `caps-images` | `caps-image-occlusion` | `caps-image-clipping` | v1 behavior |
|---|---|---|---|---|
| **Kitty** | ✓ | ✓ (overlay) | ✓ (geometry) | Full: occlude + clip. Hide-placement teardown, cells intact, clip on scroll. Best-fidelity path. |
| **sixel** | ✓ | ✗ (cohabit) | ✓ (RGB-payload) | Display + clip with **repaint-cells** teardown. Alpha preserved via transparency key (below). |
| **iTerm2** | ✓ | ✗ | ✗ | **Placeholder-only in v1** — protocol present but no clipping/occlusion path yet; inline display deferred pending that model. |
| none | ✗ | — | — | Placeholder-only; swap logic not engaged. |

**[DECISION] sixel transparency.** sixel has no alpha channel; the framework keys transparency on `#00FFFF`, so alpha-bearing icons/images (rounded/irregular edges, PNG icons) composite cleanly on the cell layer instead of as opaque rectangles. **[EDGE]** True-cyan content pixels are nudged off the key by an imperceptible ±1 on a B/G channel during encode (same pass as clip-payload manipulation), so the key is an internal reservation with **no user-visible color restriction**.

**[DECISION] Protocol/decoding handling:**
- Capability classes are resolved at startup (detect → user override wins — §18); the editor reads the resolved set. Unrecognized terminals are handled by user capability declaration, not failure.
- Decode/transmit off the UI path; **[EDGE]** never block keystroke rendering on decode/transmit (§13 latency is hard) — an undecoded image shows its placeholder until ready.
- **[EDGE]** Remote URLs: v1 does **not** fetch remote images for display (network + security) — local file paths only; remote images show the placeholder (URL revealed on the active line). Remote fetch is **[DEFER]**.
- **[EDGE]** Broken/missing/undecodable local image → placeholder with a warning tint; no crash; swap disabled for that image.

**[DECISION] Editing an image** (alt/path/title) uses the Insert/Edit Image dialog; the placeholder — not the painted image — is the caret/click target.

**[DEFER]:** multi-row inline images with text flow; remote fetch; iTerm2 inline display (pending an occlusion/clipping model); resize handles; click-to-zoom overlay; cross-session decode cache.

### 7.3 Footnotes
- Reference marker rendered inline; definition rendered at its source location.
- **[DECISION]** Clicking/activating a footnote reference **scrolls to and highlights** its definition; the definition has a back-reference to return. (In-place footnotes per §2.3; no auto-collection.)

### 7.4 Task checkboxes
- Toggling is a first-class interaction: click the checkbox glyph or press Space with the cursor on the item. Flips source `[ ]`↔`[x]`, updates strikethrough/dim. Counts toward undo as one unit.

**Acceptance criteria — §7**
- [ ] Link insert/edit dialog works; URL is visible on focus; Open Link is explicit and never fires on plain click.
- [ ] Images render as placeholders with alt text; broken paths warn without crashing.
- [ ] On a supporting terminal, a local image paints when its region is inert and withdraws cleanly when the cursor enters its line, when a selection crosses it, on scroll-clip, and in raw mode.
- [ ] Withdraw leaves no artifact across profiles: sixel (cohabit) repaints the placeholder cells; Kitty (occlude) deletes the placement with no stale overlay and repositions/hides placements on scroll; iTerm2 shows placeholder-only.
- [ ] Image decode/transmit never blocks keystroke rendering; remote images show placeholders only.
- [ ] With no image protocol available, behavior is placeholders-only with swap logic disabled.
- [ ] Footnote activation navigates to the definition and back.
- [ ] Checkbox toggle edits source and updates rendering.

---

## 8. Command surface — Bars integration

The editor's commands are hosted on the framework's **Bars** surfaces. [REF: the unified Bars design guide — this editor is the concrete consumer of that spec.] **[DECISION] v1 ships with a Toolbar + menu bar as the default surface, and a Ribbon as an available (opt-in) layout** — both bind the same commands, per the Bars "one command set, multiple surfaces" architecture.

### 8.1 Required command inventory
All commands are `ICommand`/`RoutedUICommand` bound once and surfaced in ribbon groups, toolbar, and/or menus:

- **File:** New, Open, Save, Save As, Export ▸ (PDF/HTML/plain — §10), Recent (backstage), Exit. Autosave is automatic (§12), not a command.
- **Edit:** Undo, Redo, Cut, Copy, Paste, Paste Special (plain), Find, Replace, Select All.
- **Format (inline):** Bold, Italic, Strikethrough, Inline Code, Link, Clear Formatting.
- **Format (block):** Heading level picker (combo), Paragraph, Bullet/Ordered/Task list, Blockquote, Code Block, Callout ▸, Horizontal Rule.
- **Insert:** Table (size picker), Image, Link, Footnote, Code Block, Callout, Special character.
- **Table (contextual):** the §5.3 operations — surfaced in a **contextual tab/group that appears only when the cursor is in a table** (reuses Bars contextual-tab behavior).
- **View:** Toggle Rendered/Raw, Toggle Split view, Toggle Outline panel, Toggle word-wrap, Theme (dark/light), Zoom/font-size if supported by host.

### 8.2 Ribbon layout (when ribbon surface active)
Tabs: **File** (backstage — §10.3), **Home** (clipboard + inline format + lists), **Insert**, **Format**, **View**. Contextual **Table Tools** tab appears in-table. [REF: ribbon states + backstage in the Bars guide.]

### 8.3 Toolbar layout (default surface)
A single overflow-aware toolbar of the highest-frequency commands (save, undo/redo, bold/italic/code, heading combo, list toggles, insert-table, link, find), with the computed **discrete overflow** `»` behavior. [REF: toolbar + overflow in the Bars guide.] A classic menu bar carries the full command set.

### 8.4 Keyboard access
- Every command reachable by keybind; **KeyTips** (Alt overlay) available on whichever surface is active. [REF: KeyTips in the Bars guide.]
- **[DECISION]** A discoverable **command palette** (fuzzy command search) is **[DEFER]** for v1 — noted because agents often add one; do not build it now.

**Acceptance criteria — §8**
- [ ] All §8.1 commands exist, are bound once, and appear on the active surface(s).
- [ ] The Table Tools contextual group shows only when the cursor is in a table.
- [ ] Switching between Toolbar and Ribbon surfaces preserves command behavior with no duplicated logic.
- [ ] Toolbar overflow collapses correctly at narrow widths.

---

## 9. Find & replace (with regex) — [IN]

- **Find:** incremental; highlights all matches; shows match count and current index; Enter / F3 next, Shift+F3 previous; wraps.
- **Replace:** replace current, replace all; shows count replaced.
- **Options:** case-sensitive, whole-word, **regex** (all [IN]). Regex flavor = **[DECISION]** .NET `System.Text.RegularExpressions`; replacement supports `$1` group references.
- **[DECISION] Find operates on the *source* text, not the rendered view**, so a user can find markdown syntax (e.g. search `**` or a link URL). Matches are mapped back to rendered positions for highlighting; **[EDGE]** a match that falls inside hidden marks reveals/scrolls to that location.
- **[DECISION]** Scope: whole document in v1. "In selection" scope is **[DEFER]**.
- **[EDGE]** Replace-all inside tables must not break table structure — replacements that would introduce a pipe or newline into a cell are escaped or skipped with a warning (consistent with §3.4/§5).
- UI: a find/replace bar (docked, dismissible), styled per the suite's input controls. [REF: rich-editors / control-gallery input styling.]

**Acceptance criteria — §9**
- [ ] Incremental find highlights all matches with count + index and wraps.
- [ ] Regex find/replace with group references works using .NET regex.
- [ ] Find matches markdown source (can find `**`, URLs, etc.).
- [ ] Replace-all never corrupts table structure.

---

## 10. Documents, files & export

### 10.1 Open / Save
- Open/Save/Save As via file dialogs. [REF: open + save-as dialogs, breadcrumb, overwrite-confirm in the colorpicker-filedialogs mockup.]
- **[DECISION] Encoding:** read/write **UTF-8** by default; detect and preserve a BOM if present; detect UTF-8/UTF-16 on open. Other encodings [DEFER].
- **[DECISION] Line endings:** detect on open (LF/CRLF), **preserve** on save; new files default to **[DECISION]** platform-native (or a configurable default).
- **[DECISION] Trailing newline:** ensure file ends with exactly one newline on save (configurable).
- **Dirty state:** modified indicator (the `●` in the title, per the mockup); prompt to save on close/replace if dirty (unless autosaved — §12).

### 10.2 Large files & performance guardrails
- **[DECISION]** v1 targets documents up to **~10k lines / ~1 MB** with full responsiveness (§13). Beyond that, **[DECISION]** the editor still opens but may disable live split-view re-render or degrade gracefully (documented limit, not a crash). Virtualized rendering of off-screen content is expected (§13).

### 10.3 Export — [IN] set
- **Export ▸ HTML** — render the document to standalone HTML (the rendered form). **[IN]**
- **Export ▸ PDF** — via the framework/host's PDF path if available; **[DECISION]** if no PDF backend exists yet, this is **[DEFER]** and the menu item is hidden — do not block v1 on a PDF engine. Confirm availability with maintainer.
- **Export ▸ Plain text** — strip formatting to readable plain text. **[IN]**
- Copy-as: **[DECISION]** "Copy as HTML" and "Copy as plain text" commands [IN] (cheap, reuse export renderers).

### 10.4 File association / CLI
- **[DECISION]** Launch with a file path argument opens that file. Directory argument or multiple files → **[DEFER]** (ties to multi-doc). Register `.md`/`.markdown` handling = host-dependent, **[DEFER]** for v1.

**Acceptance criteria — §10**
- [ ] Open/save round-trips content, encoding, and line endings without corruption.
- [ ] Dirty indicator and save-prompt behave correctly.
- [ ] HTML and plain-text export produce correct output; PDF is present only if a backend exists.
- [ ] Opening a ~10k-line file stays responsive per §13; larger files degrade gracefully with a documented message.

---

## 11. Document outline / TOC navigator — [IN]

- A toggleable side panel listing the document's headings as a nested tree (H1→H6). [REF: TreeView in the control gallery; docking panel in the IDE-shell mockup.]
- **[DECISION]** Live-updates as headings are edited (debounced).
- Clicking an entry scrolls to and places the cursor at that heading.
- **[DECISION]** Current-location tracking: the entry for the section containing the cursor is highlighted as the user scrolls/edits.
- **[EDGE]** Documents with no headings show an empty-state message; front matter is excluded from the outline.
- **[DECISION]** Outline reordering (drag a heading to move its whole section) is a **nice-to-have; [DEFER]** unless cheap — v1 minimum is navigation only.

**Acceptance criteria — §11**
- [ ] Outline reflects heading structure and updates live.
- [ ] Clicking navigates; current section is highlighted.
- [ ] Toggle from View menu/ribbon works.

---

## 12. Autosave & crash recovery — [IN]

**Never lose the user's work** is a primary goal.

- **[DECISION] Autosave model:** periodic + on-idle write of the working document to a **recovery/journal file** separate from the user's file (do not silently overwrite the user's file). Interval **[DECISION]** ~5 s after last edit, and on focus-loss / app-background.
- **[DECISION] Recovery file location & naming:** a per-document recovery file in an app state directory, keyed to the document path (and a marker for unsaved/untitled docs).
- **[DECISION] On crash/restart:** if a recovery file newer than the saved file exists, prompt the user to **restore** or **discard** on next launch (or when reopening that file). Show a diff-free, simple "unsaved changes from <time> were recovered" prompt.
- **[DECISION] On clean save/close:** the recovery file for that document is removed.
- **[EDGE] Untitled documents:** autosave to a recovery file with a generated id; on restart, offer to restore into a new untitled buffer.
- **[EDGE]** Recovery must never corrupt the real file; writes are atomic (write-temp-then-rename).
- **[DECISION] Autosave is not "save":** it does not clear the dirty indicator or write the user's file. An explicit Save is still required to persist to the real path. (Configurable "autosave to real file" mode is **[DEFER]**.)

**Acceptance criteria — §12**
- [ ] Edits are journaled within the interval; killing the process and relaunching offers recovery.
- [ ] Restoring yields the exact unsaved content; discarding removes the journal.
- [ ] Clean save/close removes the journal; real file is never written by autosave.
- [ ] Recovery writes are atomic.

---

## 13. Performance & responsiveness targets

- **[DECISION] Keystroke-to-render latency:** typing (including inside a reflowing table) feels instant — target **< 16 ms** per keystroke render on the reference document; hard ceiling **50 ms**. Table reflow must not visibly lag.
- **[DECISION] Incremental rendering:** editing one block re-renders only the affected region, not the whole document. Off-screen content is virtualized; open/scroll of a 10k-line doc stays smooth.
- **[DECISION] Table reflow cost:** recompute only the affected column(s) and border cells on cell edit (§5.5), not the entire table where avoidable, and never the whole document.
- **[DECISION] Parse strategy:** re-parse incrementally / per-block on edit rather than reparsing the entire document each keystroke; full reparse acceptable on load and on large structural paste.
- **[DECISION]** Startup to editable **< 500 ms** for a typical file.

**Acceptance criteria — §13**
- [ ] Typing in a large document and in a wide table stays within latency targets (measured).
- [ ] Scrolling a 10k-line document is smooth (virtualized).
- [ ] No full-document reparse or full-table reflow on a single keystroke (verified by instrumentation).

---

## 14. Deferred (explicitly out of v1.0)

Recorded so the boundary is deliberate. Design should avoid precluding these but must **not** implement them now:

- **Multiple open documents / tabs** — single active document in v1. (Biggest single deferral; affects link-following §7.1 and CLI §10.4.)
- **Spell check** — needs a dictionary subsystem.
- **Live word/char count & reading stats** — cut for v1 (cheap to add later).
- **Command palette** (fuzzy command search).
- **Multi-cursor / column selection** (general editor; the table cell-selection in §5.4 is the only block-like selection).
- **Math typesetting** (math is captured & shown as styled source, not rendered to glyphs — §2.3).
- **Raw HTML rendering** (passed through as literal text — §2.4).
- **Multi-row inline images** (image occupies N rows with text reflowing around/after it), **remote image fetching for display**, **iTerm2 inline display** (pending an occlusion/clipping path), image resize handles, click-to-zoom overlay, cross-session image caching. Single-row inline image display is **[IN]** on Kitty (occlude + clip) and sixel (cohabit + clip + keyed transparency) per §7.2; these richer behaviors are deferred.
- **Footnote auto-collection/renumber**, **outline drag-reorder**, **find "in selection" scope**, **PDF export if no backend**, **non-UTF encodings**, **autosave-to-real-file mode**, **plugins/extensions**, **collaborative editing**, **diagram/mermaid fences**, **wiki-links**, **emoji shortcodes**, **custom directives**.

---

## 15. Configuration (settings the user can change)

Expose at least these (backstage/Options); all have the defaults noted above:
- Theme (dark/light), font size (host-permitting).
- Editing: auto-pairing per character, smart quotes/dashes (off), indent width & spaces-vs-tabs, list auto-continuation.
- Save: default line endings, ensure-trailing-newline, default encoding.
- Tables: default overflow mode (wrap/truncate/scroll), max column width, zebra striping on/off.
- Autosave: interval, enabled.
- View: default surface (toolbar/ribbon), default to rendered vs split, outline panel default state.

---

## 16. Suggested milestone plan (for the planning agent)

Sequenced so each milestone is independently testable and de-risks the hardest parts early. The agent may re-plan, but should preserve the ordering intent: **document model + rendering pipeline first, tables early (highest risk), polish last.**

**M1 — Core text editing (no markdown).** Document buffer, i-beam cursor, single selection, undo/redo groups, clipboard, file open/save (UTF-8, line-ending preservation), dirty state. Plain monospace text only. *Gate: §3, §10.1 criteria (minus markdown), autosave stub.*

**M2 — Parse + render pipeline & reveal-on-edit.** Incremental block parser for CommonMark core; rendered view with reveal-on-edit; active-line highlight; raw-source toggle. **Establish the capability-class layer here** (§18): stamp `caps-*` on the root, wire the color-depth tiers + `caps-nocolor` into the token/brush selection, and route iconography through the Icon element — so every later rendering feature selects on capabilities from the start rather than retrofitting. *Gate: §2.1 rendered constructs, §4 criteria, §18.1–18.4 for the color/icon axes (image axis lands with M7).*

**M3 — Tables (headline, highest risk).** Rendering with `ch`-cell width discipline; all §5.3 operations; live reflow; overflow modes; insert picker + paste-to-table. *Gate: all §5 criteria. Do this early — it's the riskiest feature.*

**M4 — Full dialect.** GFM (task lists, strikethrough, autolinks) + extensions (footnotes, def lists, callouts, math-as-source, front matter); block/inline commands (§6); links/footnotes/checkboxes and image **placeholders** (§7 — image *protocol display* deferred to M7, but placeholders and the Insert/Edit Image dialog land here). *Gate: §2.2/§2.3, §6, and §7 criteria except the protocol-image-display items.*

**M5 — Bars command surface.** Toolbar + menu bar (default), ribbon (opt-in), contextual Table Tools, KeyTips, size picker & dialogs wired to commands. *Gate: §8 criteria.*

**M6 — Cross-cutting features.** Find & replace with regex (§9); outline navigator (§11); full autosave & crash recovery (§12); export HTML/plain (+PDF if backend) (§10.3). *Gate: §9, §11, §12, §10.3 criteria.*

**M7 — Split view, performance, polish.** Split rendered/raw view (§4.2); **capability-gated inline image display + placeholder-swap** (§7.2 — Kitty occlude+clip, sixel cohabit+clip+keyed-transparency, iTerm2 placeholder-only; sequenced here because it is latency-sensitive, must not regress §13 targets, and depends on stable reflow/scroll from earlier milestones, plus the shared image backend also used by the Icon PNG tier §18.4); performance passes to hit §13 targets (incremental render/parse/reflow, virtualization); configuration surface (§15); large-file behavior. *Gate: §13 criteria, §7.2 image criteria, §18 capability criteria, §15, remaining acceptance items.*

**Definition of done for v1.0:** every **[IN]** acceptance checkbox in §2–§13 passes; the editor can open, edit, and save the framework's own documentation (including at least one doc with tables, callouts, footnotes, and code blocks) without data loss, and it dogfoods its own docs.

---

## 17. Open questions for the maintainer

Resolve these before or during M-planning (they affect scope):
1. **PDF export** — is there a PDF backend in the framework/host yet? If not, confirm it's hidden for v1 (§10.3).
2. **Terminal image display** — *Resolved:* capability-gated per §7.2/§18. Kitty = full (occlude + clip); sixel = display + clip with keyed transparency; iTerm2 = placeholder-only in v1 (pending an occlusion/clipping path). Remaining tuning for the maintainer: the **max image height (cells)** default and the **swap-back debounce** interval — best set against the real renderer.
3. **Relative-link following** with tabs deferred — confirm the "info toast + open (replace)" behavior for local `.md` links (§7.1), or prefer read-only info until tabs land.
4. **Setext & indented-code normalization on save** (§2.1) — acceptable, or must the editor preserve original syntax byte-for-byte where unchanged?
5. **Default command surface** — ship Toolbar-default with Ribbon opt-in (spec's assumption), or Ribbon-first to showcase Bars?
6. **Config persistence location** — where do settings + recovery files live (app data dir convention)?

---

## 18. Capability gating & graceful degradation

The editor runs on terminals of widely varying capability. **The framework exposes each capability as a root-level style class (`caps-*`) stamped on every root element; degradation is expressed in XAML selectors/setters that key off those classes — not imperative branching in the render path.** This is the same styling mechanism the theming system uses (§ Bars guide). This section is the authoritative catalog for the editor; every **[CAP: …]** tag elsewhere degrades per here.

### 18.1 Resolution model — detect, then override
- Each capability resolves as **detect → user-override wins**. Detection signal quality varies by axis: strong (image protocols), weak/heuristic (color depth), or **none** (Nerd Font — no terminal advertises glyph coverage). Where detection is absent or wrong, the user declares the capability.
- **[DECISION]** Overriding a capability is adding/removing its `caps-` class on the root; dependent selectors re-resolve through normal style invalidation — no bespoke override plumbing. This is also **how unrecognized terminals are supported**: the user declares capabilities manually and everything cascades.
- **[DECISION]** Overrides are app state and **survive renegotiation** (e.g. terminal resize/reconnect re-detects, but user forces persist).
- Surfacing: capability declaration lives in **Options/config** (§15). A launch-time/env override path is host-dependent and **[DEFER]** unless trivial.

### 18.2 Capability classes the editor selects on
Authoritative list is the framework's; the editor-relevant subset (names verbatim):

| Class | Gates | Editor behavior when **absent** |
|---|---|---|
| `caps-truecolor` / `caps-ansi256` / `caps-ansi16` / `caps-nocolor` | **Mutually-exclusive** color-depth tier. The Tokyo Night token brushes resolve per active tier. | Not "absent" — exactly one is always active. The token system selects the brush set for the tier; **[CAP: maintainer]** supplies the truecolor→256→16 palette mappings. `caps-nocolor` = §18.3. |
| `caps-images` | Any inline-image protocol is present. | No inline image display; placeholders only (§7.2). |
| `caps-image-occlusion` | Image occludes cell content (Kitty overlay) vs. cohabits it. | Cohabit teardown = repaint cells on swap (sixel); see §7.2 profiles. |
| `caps-image-clipping` | Framework can show a partial image at the viewport edge. | Withdraw whole image on partial scroll instead of clipping (§7.2). |
| `caps-nerdfont` | Nerd Font PUA glyph coverage (no-probe opt-in). | Icon falls to next tier (§18.4). |
| `caps-unicode` | Unicode glyph rendering. **Unconditional in v1** (always stamped). | (Floor in v1 — see §18.4; `caps-ascii` is reserved, not yet stamped.) |
| `caps-motion` | Mouse motion events. | No hover affordances / drag-select via mouse; keyboard paths unaffected. |
| `caps-kitty-keyboard` | Kitty keyboard protocol (disambiguated keys). | Fall back to standard key handling; all commands still reachable. |

**[EDGE]** `caps-ascii` is **reserved and not stamped in v1** — there is no negotiated glyph-capability source to distinguish an emoji-safe terminal from a plain one yet. The Icon floor in v1 is therefore `caps-unicode` (§18.4); a pure-ASCII floor is future work.

### 18.3 The `caps-nocolor` path (monochrome)
Under `caps-nocolor`, **every signal the editor conveys through color must fall to a non-color channel.** This directly affects the suite's foreground-color conventions:
- Heading levels (color-coded) → weight/underline differentiation.
- Callout types (color-keyed note/tip/warning) → the type label + Icon carry the meaning; no fill color.
- Code/syntax highlighting → monochrome (structure via the code fill/box only).
- Link states, and the check/radio + link **foreground-color** conventions from the control gallery → their **secondary channel** (underline, brackets, weight, reverse) rather than color.
- Selection/active-line → reverse-video / fill instead of tint.

**[CAP: maintainer]** specify the exact non-color channel chosen per signal. **Acceptance:** in `caps-nocolor`, no information is conveyed by color alone (every colored distinction has a redundant non-color channel).

### 18.4 Iconography — the Icon element (three-tier)
All editor iconography is delegated to the framework's **Icon element**; features **request an icon, never hardcode a glyph**, and inherit degradation. Icon-bearing features to route through it: task-list checkbox, callout type icons, image placeholder glyph, footnote marker, and all command/toolbar/ribbon/split-arrow icons.

**[DECISION] Tier ladder (v1), resolved against the capability set:**
1. **`caps-nerdfont`** → Nerd Font PUA glyph (preferred: inherits text color + cell metrics, styleable, cheap).
2. **else `caps-images`** → the icon painted as a **PNG via the image backend** (§7.2 — *same `IImageBackend`*; icon PNGs and content images share transmit/clip/teardown; on sixel, alpha via the `#00FFFF` key). Icons are static, so they use the simplest safe teardown for the active profile.
3. **else `caps-unicode`** → a Unicode glyph (the **floor** in v1).

**[EDGE]** Because there's no glyph-coverage detection, `caps-nerdfont` is a **user-options opt-in**; **safe default is absent** (fall to PNG/Unicode) so first run never shows tofu. An optional confirmation-render in Options — sample glyphs shown against a single-cell width ruler to verify **presence *and* variant width** (a present-but-double-width glyph corrupts the grid) — is a **[DEFER]** nicety, not required for v1.

**[EDGE]** The Icon PNG tier and §7.2 image display are the **same backend, different callers** — build the transmit/clip/occlusion/teardown once; do not implement image handling twice.

**Acceptance criteria — §18**
- [ ] Every **[CAP: …]** behavior degrades via `caps-*` selectors, not code branches; forcing a class via override changes rendering with no restart.
- [ ] Each color-depth tier renders with its palette; `caps-nocolor` conveys no information by color alone.
- [ ] Icon resolves Nerd Font → PNG → Unicode against the capability set; absent `caps-nerdfont` default shows no tofu.
- [ ] Icon PNG tier and §7.2 images share one backend (verified: single implementation).
- [ ] An unrecognized terminal is fully usable after manual capability declaration.
