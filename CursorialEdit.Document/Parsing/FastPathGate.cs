namespace CursorialEdit.Document.Parsing;

/// <summary>
/// The fast-path admission gate (architecture Decision 3 step 2 / §2.2): decides whether an edit is
/// provably incapable of changing block structure, so the reparse can be confined to the single
/// edited block instead of expanding a context window. It inspects the inserted <b>and the removed</b>
/// text symmetrically — deleting a blank line merges two paragraphs while "introducing" no character
/// (the perf judge's deletion hole), so the removed side is gated exactly like the inserted side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conservative superset.</b> Admission is a one-way guarantee: an admitted edit definitely cannot
/// restructure blocks; a rejected edit merely <i>might</i>, and is routed to the always-correct window
/// path. So the gate is free to reject aggressively. It admits only edits whose inserted and removed
/// text are entirely <b>letters</b> and whose seams are strictly interior to a run of letters — a
/// word-internal edit, which cannot start, end, or reshape any block construct (headings, list/quote
/// markers, fences, tables, thematic breaks, setext underlines, indentation, definitions all require a
/// non-letter). Any boundary-significant character — a newline, a fence/table/quote/list/heading/rule/
/// math/definition marker, whitespace (indentation and the space after a marker are structural), or a
/// bracket (footnote/reference definitions) — on either side rejects the edit, as does an edit that
/// touches the very start of a line or abuts a non-letter (where a neighbouring marker could combine
/// with it). Edits adjacent to <c>---</c>/<c>===</c> reject through this same net: those runs are not
/// letters, so a seam beside them is not letter-interior.
/// </para>
/// <para>
/// <b>What admission does <i>not</i> cover.</b> Document-global constructs (link-reference and
/// footnote definitions) can change a block's rendering without changing structure; the producer runs
/// its definition-set diff on every edit, fast path included, so admission here never bypasses that
/// invalidation. Reference-link inline resolution against out-of-window definitions is likewise a
/// lazy-inline/full-reparse concern (Decision 5/13), not a block-structure one.
/// </para>
/// </remarks>
public static class FastPathGate
{
    /// <summary>
    /// Whether an edit that replaced <paramref name="removed"/> with <paramref name="inserted"/> is
    /// admitted to the single-block fast path, given the characters immediately surrounding the edited
    /// region in the <b>post-splice</b> buffer: <paramref name="before"/> is the character just before
    /// the edit's start offset, <paramref name="after"/> the character just after the inserted text
    /// (for a pure deletion, the character that followed the removed span). Both are
    /// <see langword="null"/> at a document edge.
    /// </summary>
    /// <param name="before">The post-splice character immediately before the edited region, or <see langword="null"/> at document start.</param>
    /// <param name="after">The post-splice character immediately after the edited region, or <see langword="null"/> at document end.</param>
    /// <param name="removed">The exact removed text (empty for a pure insertion).</param>
    /// <param name="inserted">The exact inserted text (empty for a pure deletion).</param>
    /// <returns><see langword="true"/> only when the edit provably cannot restructure blocks.</returns>
    public static bool IsEligible(char? before, char? after, string removed, string inserted)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(inserted);

        // A degenerate edit changes nothing structural, but it also changes no content — the producer
        // never calls the gate for one; treat it as ineligible for a clean single meaning.
        if (removed.Length == 0 && inserted.Length == 0)
            return false;

        if (!AllLetters(inserted) || !AllLetters(removed))
            return false;

        // The edit must sit strictly inside a run of letters: a letter immediately before the seam and
        // a letter immediately after the edited region. This rules out edits at a line/document edge
        // (a terminator or absent neighbour is not a letter) and edits abutting any marker character,
        // so no admitted edit can begin, end, or reshape a block construct — including an indentation
        // change or the space after a list/quote marker.
        return before is { } b && IsLetter(b) && after is { } a && IsLetter(a);
    }

    private static bool AllLetters(string text)
    {
        foreach (char c in text)
        {
            if (!IsLetter(c))
                return false;
        }

        return true;
    }

    // ASCII/Unicode letters only — deliberately excludes digits (ordered-list markers), whitespace
    // (indentation), and every punctuation/symbol that can carry block meaning.
    private static bool IsLetter(char c) => char.IsLetter(c);
}
