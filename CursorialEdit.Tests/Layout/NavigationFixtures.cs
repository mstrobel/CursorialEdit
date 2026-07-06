namespace CursorialEdit.Tests.Layout;

/// <summary>
/// The grapheme fixtures the <see cref="TextNavigationProbeTests"/> R4 TextBox-parity probe drives
/// (hand-computed cluster inventory + display-cell maps). A single definition keeps the probe's
/// fixtures and their pinned expectations in one place: extending a fixture here extends the probe at
/// once (a stale hand-computed map then fails the pinned tests loudly).
/// </summary>
internal static class NavigationFixtures
{
    /// <summary>
    /// "a" + é(e+U+0301) + 漢 + 👍 + ❤️(U+2764+VS16) + family(ZWJ ×3) + "x".
    /// UTF-16 boundaries: 0,1,3,4,6,8,19,20 — cells: 0,1,2,4,6,8,10,11.
    /// </summary>
    public const string ClusterFixture =
        "a" + "é" + "漢" + "\U0001F44D" + "❤️"
        + "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466" + "x";

    /// <summary>The hand-computed UTF-16 cluster boundaries of <see cref="ClusterFixture"/>.</summary>
    public static readonly int[] ClusterBoundaries = [0, 1, 3, 4, 6, 8, 19, 20];

    /// <summary>The hand-computed display cell of each <see cref="ClusterBoundaries"/> entry.</summary>
    public static readonly int[] ClusterCells = [0, 1, 2, 4, 6, 8, 10, 11];

    /// <summary>
    /// "foo, bar—baz 漢字 end": whitespace-delimited words (TextBox parity) — punctuation adheres
    /// to its word, unspaced CJK is one word, Ctrl+Right lands at the END of the word run.
    /// </summary>
    public const string WordFixture = "foo, bar—baz 漢字 end";

    /// <summary>Two 👍 (4 UTF-16 units) + space + "ok" — the emoji word-run fixture.</summary>
    public const string EmojiWordFixture = "\U0001F44D\U0001F44D ok";

    /// <summary>10 a's + space + 22 b's; at width 28 WordWrap breaks after the space: rows [0,11) + [11,33).</summary>
    public const string Wrap28Fixture = "aaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>27a+space ("aaa…a " = 28 cells) / "bb " (3 cells) / 27 c's — the sticky-goal-column fixture.</summary>
    public static readonly string ThreeRowFixture = new string('a', 27) + " bb " + new string('c', 27);

    /// <summary>27 a's + space + unspaced CJK — row 1 is all wide clusters (goal-inside-wide-cluster landings).</summary>
    public static readonly string CjkRowFixture = new string('a', 27) + " 汉字汉字汉字";

    /// <summary>27 a's then CJK with no whitespace — the wide cluster must move whole, never straddle the wrap edge.</summary>
    public static readonly string StraddleFixture = new string('a', 27) + "漢漢漢";
}
