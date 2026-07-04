# Icon ledger — CursorialEdit command & content iconography

Per Mike (2026-07-03): the initial toolbar/ribbon designs target **Nerd Font glyphs with emoji/Unicode
fallback**; this ledger tracks every icon the editor requests through the framework's `Icon` element and
accumulates the **image (PNG) assets to procure** for the `caps-images` tier. Mike procures the PNGs.

Conventions:
- **NF glyph** — proposed Nerd Font icon name (nf-md-* = Material Design set unless noted). Codepoints
  deliberately omitted here; they get pinned against the Nerd Fonts cheat sheet when the icon resources
  are authored (M5), with the single-cell width check the spec's §18.4 confirmation-render describes.
- **Unicode** — single-width-safe floor glyph (`caps-unicode` tier). **Emoji** — richer fallback, but
  emoji are **double-width** in the cell grid. Per FB-15 (framework-feedback.md, direction agreed with
  Mike): the Icon element grows a fourth tier — Glyph → Image → **Emoji** (`caps-emoji`, user opt-in) →
  Text — so the two columns here map to two distinct tiers rather than one shared floor.
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
| Undo | nf-md-undo | ↶ | ↩️ | undo.png | needed |
| Redo | nf-md-redo | ↷ | ↪️ | redo.png | needed |
| Cut | nf-md-content_cut | ✂ | ✂️ | cut.png | needed |
| Copy | nf-md-content_copy | ⿻ (fallback: ▤▤) | 📋 | copy.png | needed |
| Paste | nf-md-content_paste | ⎀ | 📋 | paste.png | needed |
| Paste Special (plain) | nf-md-content_paste (+T badge) | ⎀T | 📋 | paste-plain.png | needed |
| Find | nf-md-magnify | ⌕ | 🔍 | find.png | needed |
| Replace | nf-md-find_replace | ⌕⇄ | 🔁 | replace.png | needed |
| Select All | nf-md-select_all | ⬚ | — | select-all.png | needed |

## Format — inline

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Bold | nf-md-format_bold | **B** (styled letter) | 🅱 | fmt-bold.png | needed |
| Italic | nf-md-format_italic | *I* (styled letter) | — | fmt-italic.png | needed |
| Strikethrough | nf-md-format_strikethrough_variant | S̶ | — | fmt-strike.png | needed |
| Inline code | nf-md-code_tags | ⟨⟩ | — | fmt-code.png | needed |
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
| Insert row above / below | nf-md-table_row_plus_before / _after | ▤↑ / ▤↓ | — | row-insert-above.png / -below.png | needed |
| Insert column left / right | nf-md-table_column_plus_before / _after | ▥← / ▥→ | — | col-insert-left.png / -right.png | needed |
| Delete row / column | nf-md-table_row_remove / table_column_remove | ▤✕ / ▥✕ | — | row-delete.png / col-delete.png | needed |
| Move row up / down | nf-md-arrow_up_bold_box_outline / down | ⇑ / ⇓ | — | row-up.png / row-down.png | needed |
| Move column left / right | nf-md-arrow_left_bold_box_outline / right | ⇐ / ⇒ | — | col-left.png / col-right.png | needed |
| Align left / center / right | nf-md-format_align_left / _center / _right | ⫷ ≡ ⫸ (per-glyph) | — | align-left.png / -center.png / -right.png | needed |
| Delete table | nf-md-table_remove | ▦✕ | — | table-delete.png | needed |
| Cell line break (`<br>`) | nf-md-keyboard_return | ↵ | — | (glyph-only — none) | n/a |

## View

| Command | NF glyph | Unicode | Emoji | PNG asset | Status |
|---|---|---|---|---|---|
| Toggle Rendered/Raw | nf-md-code_tags_check (raw) / nf-md-eye (rendered) | ⌨ / 👁→◉ | 👁 | view-raw.png / view-rendered.png | needed |
| Toggle Split view | nf-md-view_split_vertical | ◫ | — | view-split.png | needed |
| Toggle Outline panel | nf-md-file_tree | ⋮≡ | 🌲 | outline.png | needed |
| Word wrap | nf-md-wrap | ⏎← | — | word-wrap.png | needed |
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
3. Several Unicode fallbacks above are provisional (marked "fallback:") — final picks happen with the
   §18.4 width-ruler check when the icon resources are authored.
