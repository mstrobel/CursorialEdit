using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Document;

/// <summary>
/// M1.WP4 gate: seeded randomized splice scripts driven against a naive string oracle. After
/// every scripted splice the suite asserts (a) text equality, (b) line structure equality
/// against a naive fresh split of the oracle string — the canonical-structure/ending-preservation
/// invariant, (c) offset↔position mapping equality at sampled offsets and positions, (d) range
/// reassembly equality, (e) anchor positions against oracle-computed expectations, and
/// (f) Epoch/Version monotonicity with version churn confined to the spliced line range.
/// The corpus mixes CJK, emoji, ZWJ sequences, combining marks, and bare CR/CRLF/LF; columns
/// are UTF-16 offsets, so scripts deliberately split surrogate pairs — the invariant under test
/// is mapping consistency, not cluster snapping (that is WP6's job).
/// </summary>
public class BufferFuzzTests
{
    /// <summary>Ops per seed; scaled up for nightly runs via the environment (plan §2.1).</summary>
    private static int OpsPerSeed =>
        int.TryParse(Environment.GetEnvironmentVariable("CURSORIALEDIT_FUZZ_OPS"), out int ops) && ops > 0
            ? ops
            : 2000;

    private const int MaxDocumentLength = 24_000;

    private static readonly string[] Tokens =
    [
        "alpha", "beta gamma", "0123", " ", "_",
        "\n", "\n", "\r\n", "\r",
        "漢字テスト", "中文編輯", "한글",
        "😀", "🎉🎉", "👨‍👩‍👧‍👦", "🇦🇺", "☂️", "🏳️‍🌈",
        "e\u0301", "n\u0303o\u0308", "\u200D", "a\uD83D", // combining marks, a bare ZWJ, and a lone high surrogate
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(20260703)]
    [InlineData(987654321)]
    public void SpliceScript_MatchesNaiveStringOracle(int seed)
    {
        var rng = new Random(seed);
        string oracle = GenerateText(rng, rng.Next(50, 200));
        var buffer = new DocumentBuffer(oracle);

        AssertStructure(buffer, oracle);

        var tracked = new List<TrackedAnchor>();
        for (int i = 0; i < 16; i++)
            RegisterTrackedAnchor(buffer, tracked, rng, oracle);

        long lastEpoch = buffer.Epoch;
        int lastVersion = buffer.CurrentVersion;
        int ops = OpsPerSeed;

        for (int op = 0; op < ops; op++)
        {
            var preLines = OracleLines(oracle);

            // ---- Pick a splice (start offset, removed length, inserted text, API form). ----
            int form = rng.Next(3); // 0 = positions, 1 = position + length, 2 = offsets
            int offS, removedLen;
            TextPosition startPos = default, endPos = default;

            if (form == 0)
            {
                // Two valid positions, the end biased near the start so deletes stay local.
                var a = RandomValidPosition(preLines, rng);
                int nearLine = Math.Clamp(a.Line + rng.Next(-3, 4), 0, preLines.Count - 1);
                var b = new TextPosition(nearLine, rng.Next(preLines[nearLine].Text.Length + 1));
                int offA = OracleOffset(preLines, a);
                int offB = OracleOffset(preLines, b);
                if (offB < offA)
                {
                    (a, b) = (b, a);
                    (offA, offB) = (offB, offA);
                }

                startPos = a;
                endPos = b;
                offS = offA;
                removedLen = offB - offA;
            }
            else if (form == 1)
            {
                startPos = RandomValidPosition(preLines, rng);
                offS = OracleOffset(preLines, startPos);
                removedLen = Math.Min(rng.Next(RemovalCap(rng, oracle.Length) + 1), oracle.Length - offS);
            }
            else
            {
                // Raw offsets: allowed to land inside CRLF terminators.
                offS = rng.Next(oracle.Length + 1);
                removedLen = Math.Min(rng.Next(RemovalCap(rng, oracle.Length) + 1), oracle.Length - offS);
            }

            string inserted = oracle.Length > MaxDocumentLength ? "" : GenerateText(rng, rng.Next(0, 6));

            // ---- Oracle expectations, computed independently of the buffer. ----
            string expectedRemoved = oracle.Substring(offS, removedLen);
            string newOracle = string.Concat(oracle.AsSpan(0, offS), inserted, oracle.AsSpan(offS + removedLen));
            int affectedStart = OraclePosition(preLines, offS).Line;
            int affectedEnd = OraclePosition(preLines, offS + removedLen).Line;
            int[] preVersions = SnapshotVersions(buffer);

            // ---- Apply through the chosen API form. ----
            SpliceResult result = form switch
            {
                0 => buffer.Apply(startPos, endPos, inserted),
                1 => buffer.Apply(startPos, removedLen, inserted),
                _ => buffer.ApplyAtOffset(offS, removedLen, inserted),
            };

            // ---- Receipt. ----
            Assert.Equal(offS, result.StartOffset);
            Assert.Equal(expectedRemoved, result.RemovedText);
            var newLines = OracleLines(newOracle);
            Assert.Equal(OraclePosition(newLines, offS + inserted.Length), result.End);

            // ---- Text + structure (ending preservation falls out of structural equality). ----
            Assert.Equal(newOracle, buffer.GetText());
            AssertStructure(buffer, newLines);

            // ---- Epoch/Version monotonicity; version churn confined to the spliced lines. ----
            Assert.True(buffer.Epoch > lastEpoch, "Epoch must strictly increase per applied splice.");
            Assert.True(buffer.CurrentVersion > lastVersion, "CurrentVersion must strictly increase per applied splice.");
            Assert.Equal(buffer.Epoch, result.Epoch);
            lastEpoch = buffer.Epoch;
            lastVersion = buffer.CurrentVersion;
            AssertVersions(buffer, preVersions, affectedStart, affectedEnd, newLines.Count);

            // ---- Sampled mapping equality. ----
            for (int i = 0; i < 4; i++)
            {
                int offset = rng.Next(newOracle.Length + 1);
                Assert.Equal(OraclePosition(newLines, offset), buffer.GetPosition(offset));

                var position = RandomValidPosition(newLines, rng);
                Assert.Equal(OracleOffset(newLines, position), buffer.GetOffset(position));
            }

            // ---- Sampled range reassembly. ----
            {
                int x = rng.Next(newOracle.Length + 1);
                int y = rng.Next(newOracle.Length + 1);
                if (y < x) (x, y) = (y, x);
                var from = OraclePosition(newLines, x);
                var to = OraclePosition(newLines, y);
                int fromOff = OracleOffset(newLines, from);
                int toOff = OracleOffset(newLines, to);
                Assert.Equal(newOracle[fromOff..toOff], buffer.GetText(from, to));
            }

            // ---- Anchors: oracle applies the documented offset mapping rule. ----
            foreach (var t in tracked)
            {
                int o = t.ExpectedOffset;
                int mapped = o < offS ? o
                    : o <= offS + removedLen ? (t.Gravity == AnchorGravity.Left ? offS : offS + inserted.Length)
                    : o - removedLen + inserted.Length;

                var expectedPosition = OraclePosition(newLines, mapped);
                Assert.Equal(expectedPosition, t.Anchor.Position);

                // The anchor's truth is its (possibly terminator-snapped) position; keep the
                // oracle offset in lockstep with what the buffer will capture next splice.
                t.ExpectedOffset = OracleOffset(newLines, expectedPosition);
            }

            oracle = newOracle;

            // Periodically rotate anchors so registration/unregistration stays exercised.
            if (op % 97 == 0 && tracked.Count > 0)
            {
                int victim = rng.Next(tracked.Count);
                Assert.True(buffer.Anchors.Unregister(tracked[victim].Anchor));
                tracked.RemoveAt(victim);
                RegisterTrackedAnchor(buffer, tracked, rng, oracle);
            }
        }

        Assert.Equal((long) ops, buffer.Epoch);
    }

    // ---- Script generation ----------------------------------------------------------------

    private static string GenerateText(Random rng, int tokenCount)
    {
        if (tokenCount > 0 && rng.Next(100) == 0)
            return string.Concat(Enumerable.Repeat(Tokens[rng.Next(Tokens.Length)] + "\n", 200)); // rare multi-KB paste

        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < tokenCount; i++)
            builder.Append(Tokens[rng.Next(Tokens.Length)]);
        return builder.ToString();
    }

    private static int RemovalCap(Random rng, int docLength) =>
        docLength > MaxDocumentLength ? 2000 : rng.Next(10) == 0 ? 400 : 40;

    private static TextPosition RandomValidPosition(List<OracleLine> lines, Random rng)
    {
        int line = rng.Next(lines.Count);
        return new TextPosition(line, rng.Next(lines[line].Text.Length + 1));
    }

    private static void RegisterTrackedAnchor(DocumentBuffer buffer, List<TrackedAnchor> tracked, Random rng, string oracle)
    {
        var lines = OracleLines(oracle);
        var position = RandomValidPosition(lines, rng);
        var gravity = rng.Next(2) == 0 ? AnchorGravity.Left : AnchorGravity.Right;
        tracked.Add(new TrackedAnchor(buffer.Anchors.Register(position, gravity), OracleOffset(lines, position), gravity));
    }

    private sealed class TrackedAnchor(Anchor anchor, int expectedOffset, AnchorGravity gravity)
    {
        public Anchor Anchor { get; } = anchor;
        public AnchorGravity Gravity { get; } = gravity;
        public int ExpectedOffset { get; set; } = expectedOffset;
    }

    // ---- Naive oracle (independent reimplementation of the documented rules) -----------------

    private readonly record struct OracleLine(string Text, LineEnding Ending)
    {
        public int TotalLength => Text.Length + Ending switch
        {
            LineEnding.Lf   => 1,
            LineEnding.CrLf => 2,
            _               => 0,
        };
    }

    /// <summary>Fresh split of a string: '\n' breaks a line, a directly preceding '\r' folds into CRLF, a lone '\r' is content; last line is unterminated.</summary>
    private static List<OracleLine> OracleLines(string text)
    {
        var lines = new List<OracleLine>();
        int start = 0;

        while (true)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                lines.Add(new OracleLine(text[start..], LineEnding.None));
                return lines;
            }

            if (nl > start && text[nl - 1] == '\r')
                lines.Add(new OracleLine(text[start..(nl - 1)], LineEnding.CrLf));
            else
                lines.Add(new OracleLine(text[start..nl], LineEnding.Lf));

            start = nl + 1;
        }
    }

    private static int OracleOffset(List<OracleLine> lines, TextPosition position)
    {
        int offset = 0;
        for (int i = 0; i < position.Line; i++)
            offset += lines[i].TotalLength;
        return offset + position.Col;
    }

    /// <summary>Offset → position with the documented snap: offsets inside a CRLF terminator clamp to end-of-text on that line.</summary>
    private static TextPosition OraclePosition(List<OracleLine> lines, int offset)
    {
        int start = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            int total = lines[i].TotalLength;
            if (i == lines.Count - 1 || offset < start + total)
                return new TextPosition(i, Math.Min(offset - start, lines[i].Text.Length));
            start += total;
        }

        throw new InvalidOperationException("Unreachable: OracleLines always yields at least one line.");
    }

    // ---- Assertions ---------------------------------------------------------------------------

    private static void AssertStructure(DocumentBuffer buffer, string oracle) =>
        AssertStructure(buffer, OracleLines(oracle));

    private static void AssertStructure(DocumentBuffer buffer, List<OracleLine> expected)
    {
        Assert.Equal(expected.Count, buffer.LineCount);

        for (int i = 0; i < expected.Count; i++)
        {
            var line = buffer.GetLine(i);
            Assert.Equal(expected[i].Text, line.Text);
            Assert.Equal(expected[i].Ending, line.Ending);
        }
    }

    private static int[] SnapshotVersions(DocumentBuffer buffer)
    {
        var versions = new int[buffer.LineCount];
        for (int i = 0; i < versions.Length; i++)
            versions[i] = buffer.GetLine(i).Version;
        return versions;
    }

    private static void AssertVersions(DocumentBuffer buffer, int[] preVersions, int affectedStart, int affectedEnd, int newCount)
    {
        int suffixLength = preVersions.Length - 1 - affectedEnd;

        for (int i = 0; i < affectedStart; i++)
            Assert.Equal(preVersions[i], buffer.GetLine(i).Version);

        for (int j = 1; j <= suffixLength; j++)
            Assert.Equal(preVersions[preVersions.Length - j], buffer.GetLine(newCount - j).Version);

        for (int i = affectedStart; i < newCount - suffixLength; i++)
            Assert.Equal(buffer.CurrentVersion, buffer.GetLine(i).Version);

        for (int i = 0; i < newCount; i++)
            Assert.True(buffer.GetLine(i).Version <= buffer.CurrentVersion, "No line version may exceed CurrentVersion.");
    }
}
