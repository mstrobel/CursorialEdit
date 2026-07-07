# Icon ledger — CursorialEdit command & content iconography

Per Mike (2026-07-03): the initial toolbar/ribbon designs target **Nerd Font glyphs with emoji/Unicode
fallback**; this ledger tracks every icon the editor requests through the framework's `Icon` element and
accumulates the **image (PNG) assets to procure** for the `caps-images` tier. Mike procures the PNGs.

Conventions:
- **NF glyph** — Nerd Font icon name (nf-md-* = Material Design set unless noted). Codepoints for **wired** rows
  are pinned (`→ U+Fxxxx`) against Nerd Fonts **glyphnames.json v3.4.0** (the plane-15 PUA-A range the nf-md set
  occupies, U+F0000–U+F1AF0 — so the string literals need the `\U000Fxxxx` escape, not `\uXXXX`); un-wired rows keep
  the name only, pinned when they are authored. The single-cell width check the spec's §18.4 confirmation-render
  describes is enforced on the Text floor by RibbonTests/ContextBarTests' shared `IconAssert` guard.
- **Unicode** — single-width-safe floor glyph (`caps-unicode` / `Icon.Text` tier). For **wired** rows this column is
  the ACTUAL wired floor (finalized, width-1, no VS16), which supersedes the provisional glyphs earlier rows carried.
  **Emoji** — richer `caps-emoji` fallback (opt-in, default present per FB-15); emoji are **double-width** in the
  cell grid, which is fine on that tier (grid safety is the Icon's 2-cell emoji measurement, not the Text floor).
  Every wired row now carries all three of Glyph / Emoji / Text (only the Image/PNG tier is still pending).
  Per FB-15: the Icon element's tier order is Glyph → Image → **Emoji** → Text.
- **PNG asset** — requested filename for procurement. Transparent background; nominal sizes TBC in M5
  once the image-tier cell budget is fixed (expect a square asset scaled to a 1-cell-high placement on
  the toolbar and a 2-cell-high "large" ribbon placement — final pixel sizes depend on cell metrics; will
  confirm before procurement so assets are cut once).
- Status: `needed` → `png-requested` → `png-received` → `wired`.

## File

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| New | nf-md-file_plus | ▢+ (composed) | 🗋 | file-new.png | needed |
| Open | nf-md-folder_open | ⌸ (fallback: `[/]`) | 📂 | folder-open.png | needed |
| Save | nf-md-content_save | ⭳ (fallback: ▼into▁) | 💾 | save.png | needed |
| Save As | nf-md-content_save_edit | ⭳✎ | 💾 | save-as.png | needed |
| Export ▸ HTML | nf-md-language_html5 | ⟨/⟩ | 🌐 | export-html.png | needed |
| Export ▸ Plain text | nf-md-file_document_outline | ¶ | 📄 | export-text.png | needed |
| Export ▸ PDF (hidden in v1 — no backend) | nf-md-file_pdf_box | — | — | — | deferred |
| Recent | nf-md-history | ↺ | 🕘 | recent.png | needed |
| Exit | nf-md-exit_to_app | ⏻ | 🚪 | exit.png | needed |

## Edit

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Undo | nf-md-undo → U+F054C | ↶ | ↩️ | undo.png | wired |
| Redo | nf-md-redo → U+F044E | ↷ | ↪️ | redo.png | wired |
| Cut | nf-md-content_cut → U+F0190 | ✁ | ✂️ | cut.png | wired |
| Copy | nf-md-content_copy → U+F018F | ⧉ | 📋 | copy.png | wired |
| Paste | nf-md-content_paste → U+F0192 | ▤ | 📋 | paste.png | wired |
| Paste Special (plain) | nf-md-content_paste (+T badge) | ⎀T | 📋 | paste-plain.png | needed |
| Find | nf-md-magnify | ⌕ | 🔍 | find.png | needed |
| Replace | nf-md-find_replace | ⌕⇄ | 🔁 | replace.png | needed |
| Select All | nf-md-select_all → U+F0486 | ⬚ | 🔲 | select-all.png | wired |

## Format — inline

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Bold | nf-md-format_bold → U+F0264 | ✱ | 🅱 | fmt-bold.png | wired |
| Italic | nf-md-format_italic → U+F0277 | ⟋ | ✍️ | fmt-italic.png | wired |
| Strikethrough | nf-md-format_strikethrough_variant | S̶ | — | fmt-strike.png | needed |
| Inline code | nf-md-code_tags → U+F0174 | ` | 💻 | fmt-code.png | wired |
| Link | nf-md-link_variant | ⧉ (fallback: ∞) | 🔗 | link.png | needed |
| Clear formatting | nf-md-format_clear | Tx | — | fmt-clear.png | needed |

## Format — block

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Heading picker (combo face) | nf-md-format_header_pound | H | — | heading.png | needed |
| Heading 1–6 (menu rows) | nf-md-format_header_1 … _6 | H1…H6 (text) | — | (text rows — none) | n/a |
| Paragraph / body | nf-md-format_paragraph | ¶ | — | paragraph.png | needed |
| Bullet list | nf-md-format_list_bulleted | • ≡ | — | list-bullet.png | needed |
| Ordered list | nf-md-format_list_numbered | 1≡ | — | list-numbered.png | needed |
| Task list | nf-md-format_list_checks | ☑≡ | ✅ | list-task.png | needed |
| Blockquote | nf-md-format_quote_close | ❝ (fallback: ▌) | — | blockquote.png | needed |
| Code block | nf-md-code_braces | { } | — | code-block.png | needed |
| Callout ▸ | nf-md-alert_box_outline | ▣! | 💡 | callout.png | needed |
| Horizontal rule | nf-md-minus | ─ | ➖ | hrule.png | needed |

## Insert

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Insert Table (size picker) | nf-md-table | ▦ | — | table.png | needed |
| Insert Image | nf-md-image_outline | ▨ (fallback: ⛰ in frame) | 🖼 | image.png | needed |
| Insert Footnote | nf-md-format_superscript | x² (fallback: ⁴) | — | footnote.png | needed |
| Insert Special character | nf-md-omega | Ω | — | special-char.png | needed |

## Table Tools (contextual — §5.3)

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Insert row above / below | nf-md-table_row_plus_before → U+F04F4 / _after → U+F04F3 | ↥ / ↧ | ⬆️ / ⬇️ | row-insert-above.png / -below.png | wired |
| Insert column left / right | nf-md-table_column_plus_before → U+F04ED / _after → U+F04EC | ↤ / ↦ | ⬅️ / ➡️ | col-insert-left.png / -right.png | wired |
| Delete row / column | nf-md-table_row_remove → U+F04F5 / table_column_remove → U+F04EE | ⊖ / ⊘ | ❌ / ❌ | row-delete.png / col-delete.png | wired |
| Move row up / down | nf-md-arrow_up_bold_box_outline → U+F0739 / arrow_down_bold_box_outline → U+F0730 | ↑ / ↓ | 🔼 / 🔽 | row-up.png / row-down.png | wired |
| Move column left / right | nf-md-arrow_left_bold_box_outline → U+F0733 / arrow_right_bold_box_outline → U+F0736 | ← / → | ◀️ / ▶️ | col-left.png / col-right.png | wired |
| Align left / center / right | nf-md-format_align_left → U+F0262 / _center → U+F0260 / _right → U+F0263 | ⇤ ↹ ⇥ (per-glyph) | ⬅️ ↔️ ➡️ | align-left.png / -center.png / -right.png | wired |
| Delete table | nf-md-table_remove → U+F0A76 | ⊗ | 🗑️ | table-delete.png | wired |
| Clear cell (added — not in original ledger) | nf-md-eraser → U+F01FE | ∅ | 🧹 | clear-cell.png | wired |
| Cell line break (`<br>`) | nf-md-keyboard_return | ↵ | — | (glyph-only — none) | n/a |

## View

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Toggle Rendered/Raw | nf-md-code_tags_check → U+F0694 (raw) / nf-md-eye → U+F0208 (rendered) | ⌗ (raw wired) / 👁→◉ | ⌨️ (raw) / 👁 (rendered) | view-raw.png / view-rendered.png | wired (raw side) |
| Toggle Split view | nf-md-view_split_vertical | ◫ | — | view-split.png | needed |
| Toggle Outline panel | nf-md-file_tree | ⋮≡ | 🌲 | outline.png | needed |
| Word wrap | nf-md-wrap → U+F05B6 | ↵ | ↩️ | word-wrap.png | wired |
| Truncate (table-cell overflow choice — added, not in original ledger) | nf-md-format_text_wrapping_clip → U+F0D0E | … | ✂️ | truncate.png | wired |
| Theme (dark/light) | nf-md-theme_light_dark | ◐ | 🌓 | theme.png | needed |

## Content icons (document rendering — §2/§7)

| Use | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Task checkbox unchecked / checked | nf-md-checkbox_blank_outline / checkbox_marked | ☐ / ☑ (already the BuiltIn convention) | — | (glyph/Unicode only) | n/a |
| Callout NOTE | nf-md-information_outline | ⓘ (fallback: (i)) | ℹ️ | callout-note.png | needed |
| Callout TIP | nf-md-lightbulb_outline | ⚟ (fallback: (*)) | 💡 | callout-tip.png | needed |
| Callout IMPORTANT | nf-md-alert_circle_outline | (!) | ❗ | callout-important.png | needed |
| Callout WARNING | nf-md-alert_outline | ⚠ (single-width form) | ⚠️ | callout-warning.png | needed |
| Callout CAUTION | nf-md-alert_octagon_outline | ⛔→(✕) | 🛑 | callout-caution.png | needed |
| Image placeholder chip | nf-md-image_broken_variant (broken) / image_outline | ▨ | 🖼 | placeholder-image.png (+ -broken variant) | needed |
| Footnote marker | (text: superscript digits) | ⁰¹²³⁴⁵⁶⁷⁸⁹ | — | n/a | n/a |
| Hard-break reveal `↵` | (text) | ↵ | — | n/a | n/a |
| Dirty indicator | (text) | ● | — | n/a | n/a |
| Overflow chevron / dropdowns / KeyTips | (framework-owned: Toolbar `»`, `▾`, etc.) | — | — | n/a | n/a |

## Open questions before procurement

1. **PNG nominal sizes** — depends on the image-tier cell budget (1-cell toolbar vs 2-cell ribbon-large
   placements) and typical cell pixel metrics; will pin in M5 and update this header so assets are cut once.
2. **Style direction for PNGs** — Tokyo Night-tinted monochrome (recolorable is impossible for PNGs, so
   probably one dark-theme set + one light-theme set) vs full-color; recommend flat single-tint with
   transparency, two variants keyed off `ThemeBase`.
3. Unicode fallbacks on **wired** rows are now finalized to the actual `Icon.Text` floor the code carries
   (verified width-1 / no-VS16 by the `IconAssert` guard). Remaining un-wired rows keep provisional fallbacks
   (marked "fallback:") — those get finalized with the §18.4 width-ruler check when their icons are authored.

### Codepoints to VERIFY on the Nerd Font (flagged for Mike)

All codepoints below resolved cleanly from Nerd Fonts glyphnames.json v3.4.0 by exact `nf-md-*` name, so none are
guesses. Two rows use nf-md names that were NOT in the original ledger (I picked them and added the rows); please
eyeball the rendered glyph:
- **Clear cell** → `nf-md-eraser` (U+F01FE) — an eraser for "clear the cell to empty".
- **Truncate** (overflow choice) → `nf-md-format_text_wrapping_clip` (U+F0D0E) — the "text clipped at the cell
  boundary" icon, the natural counterpart to `nf-md-wrap` for the Wrap⇄Truncate segmented control.

One deliberate emoji-tier deviation from the ledger: the **Raw** toggle wires Emoji `⌨️` (raw source), not the
row's `👁` (which is the *rendered/eye* side of the same toggle and would read as the opposite of "Raw").
