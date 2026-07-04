using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Tests.Document;

/// <summary>
/// M1.WP4 — <see cref="DocumentBuffer"/> construction, ending detection/preservation, text
/// reassembly, offset↔position round-trips (CRLF and empty-doc edges), the splice primitive's
/// receipt contract, stamps, and anchor gravity rules. The randomized oracle sweep lives in
/// <see cref="BufferFuzzTests"/>.
/// </summary>
public class DocumentBufferTests
{
    private static void AssertLines(DocumentBuffer buffer, params (string Text, LineEnding Ending)[] expected)
    {
        Assert.Equal(expected.Length, buffer.LineCount);

        for (int i = 0; i < expected.Length; i++)
        {
            var line = buffer.GetLine(i);
            Assert.Equal(expected[i].Text, line.Text);
            Assert.Equal(expected[i].Ending, line.Ending);
        }
    }

    // ---- Construction and reassembly -------------------------------------------------------

    [Fact]
    public void EmptyString_IsOneEmptyUnterminatedLine()
    {
        var buffer = new DocumentBuffer(string.Empty);

        AssertLines(buffer, ("", LineEnding.None));
        Assert.Equal(0, buffer.Length);
        Assert.Equal("", buffer.GetText());
        Assert.Equal(0L, buffer.Epoch);
    }

    [Fact]
    public void MixedEndings_DetectedPerLine()
    {
        var buffer = new DocumentBuffer("a\r\nb\nc\r\n");

        AssertLines(buffer,
            ("a", LineEnding.CrLf),
            ("b", LineEnding.Lf),
            ("c", LineEnding.CrLf),
            ("", LineEnding.None));
    }

    [Fact]
    public void LoneCarriageReturn_IsContentNotATerminator()
    {
        var buffer = new DocumentBuffer("a\rb\nc\r");

        AssertLines(buffer,
            ("a\rb", LineEnding.Lf),
            ("c\r", LineEnding.None));
    }

    [Fact]
    public void CrCrLf_KeepsFirstCrInText()
    {
        var buffer = new DocumentBuffer("x\r\r\n");

        AssertLines(buffer,
            ("x\r", LineEnding.CrLf),
            ("", LineEnding.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("a")]
    [InlineData("a\nb\r\nc\rd\n")]
    [InlineData("漢字\r\n😀👨‍👩‍👧‍👦\né\r")]
    public void GetText_RoundTripsByteExactly(string text)
    {
        var buffer = new DocumentBuffer(text);

        Assert.Equal(text, buffer.GetText());
        Assert.Equal(text.Length, buffer.Length);
    }

    // ---- Offset mapping ---------------------------------------------------------------------

    [Fact]
    public void OffsetAndPosition_RoundTripAtEveryValidPosition()
    {
        var buffer = new DocumentBuffer("ab\r\n漢字\nx\r");

        for (int line = 0; line < buffer.LineCount; line++)
        {
            for (int col = 0; col <= buffer.GetLine(line).Text.Length; col++)
            {
                var position = new TextPosition(line, col);
                int offset = buffer.GetOffset(position);
                Assert.Equal(position, buffer.GetPosition(offset));
            }
        }
    }

    [Fact]
    public void GetPosition_InsideCrlfTerminator_SnapsToEndOfLineText()
    {
        var buffer = new DocumentBuffer("ab\r\ncd");

        // Offset 2 is end-of-text; offset 3 is between '\r' and '\n' — snaps back to (0, 2).
        Assert.Equal(new TextPosition(0, 2), buffer.GetPosition(2));
        Assert.Equal(new TextPosition(0, 2), buffer.GetPosition(3));
        Assert.Equal(new TextPosition(1, 0), buffer.GetPosition(4));
    }

    [Fact]
    public void GetPosition_AtLength_IsEndOfLastLine()
    {
        var buffer = new DocumentBuffer("a\nbc");
        Assert.Equal(new TextPosition(1, 2), buffer.GetPosition(buffer.Length));

        var trailingNewline = new DocumentBuffer("a\n");
        Assert.Equal(new TextPosition(1, 0), trailingNewline.GetPosition(2));
    }

    [Fact]
    public void EmptyDocument_OffsetEdges()
    {
        var buffer = new DocumentBuffer();

        Assert.Equal(0, buffer.GetOffset(TextPosition.Zero));
        Assert.Equal(TextPosition.Zero, buffer.GetPosition(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetPosition(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetPosition(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetOffset(new TextPosition(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetOffset(new TextPosition(1, 0)));
    }

    // ---- Range reassembly ---------------------------------------------------------------------

    [Fact]
    public void GetText_Range_IncludesInteriorTerminators()
    {
        var buffer = new DocumentBuffer("ab\r\ncd\nef");

        Assert.Equal("b\r\ncd\ne", buffer.GetText(new TextPosition(0, 1), new TextPosition(2, 1)));
        Assert.Equal("cd", buffer.GetText(new TextPosition(1, 0), new TextPosition(1, 2)));
        Assert.Equal("", buffer.GetText(new TextPosition(1, 1), new TextPosition(1, 1)));
        Assert.Equal(buffer.GetText(), buffer.GetText(TextPosition.Zero, new TextPosition(2, 2)));
    }

    [Fact]
    public void GetText_Range_EndBeforeStart_Throws()
    {
        var buffer = new DocumentBuffer("ab\ncd");
        Assert.Throws<ArgumentException>(() => buffer.GetText(new TextPosition(1, 0), new TextPosition(0, 0)));
    }

    [Fact]
    public void GetTextAtOffset_ReadsBoundariesInsideCrlfTerminators()
    {
        // "ab\r\ncd": offsets 2 and 3 address the CRLF's interior — unreachable for the
        // position-based GetText, honest for the offset read (the ApplyAtOffset companion).
        var buffer = new DocumentBuffer("ab\r\ncd");

        Assert.Equal("\r\n", buffer.GetTextAtOffset(2, 2));
        Assert.Equal("\r", buffer.GetTextAtOffset(2, 1));
        Assert.Equal("\ncd", buffer.GetTextAtOffset(3, 3));
        Assert.Equal("b\r\nc", buffer.GetTextAtOffset(1, 4));
        Assert.Equal("", buffer.GetTextAtOffset(3, 0));
        Assert.Equal(buffer.GetText(), buffer.GetTextAtOffset(0, buffer.Length));
    }

    [Fact]
    public void GetTextAtOffset_OutOfRange_Throws()
    {
        var buffer = new DocumentBuffer("ab\ncd");

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetTextAtOffset(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetTextAtOffset(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetTextAtOffset(4, 2)); // reaches past Length 5
    }

    // ---- Splices ------------------------------------------------------------------------------

    [Fact]
    public void Insert_WithinLine_ReturnsReceipt()
    {
        var buffer = new DocumentBuffer("hello world");
        var result = buffer.Apply(new TextPosition(0, 5), new TextPosition(0, 5), ",");

        Assert.Equal("hello, world", buffer.GetText());
        Assert.Equal("", result.RemovedText);
        Assert.Equal(5, result.StartOffset);
        Assert.Equal(new TextPosition(0, 6), result.End);
        Assert.Equal(1L, result.Epoch);
        Assert.Equal(buffer.Epoch, result.Epoch);
    }

    [Fact]
    public void Insert_Newline_SplitsLine()
    {
        var buffer = new DocumentBuffer("ab");
        var result = buffer.Apply(new TextPosition(0, 1), new TextPosition(0, 1), "\n");

        AssertLines(buffer, ("a", LineEnding.Lf), ("b", LineEnding.None));
        Assert.Equal(new TextPosition(1, 0), result.End);
    }

    [Fact]
    public void Insert_MultiLineWithMixedEndings_DetectsPerLine()
    {
        var buffer = new DocumentBuffer("XY");
        buffer.Apply(new TextPosition(0, 1), new TextPosition(0, 1), "1\r\n2\n");

        AssertLines(buffer, ("X1", LineEnding.CrLf), ("2", LineEnding.Lf), ("Y", LineEnding.None));
        Assert.Equal("X1\r\n2\nY", buffer.GetText());
    }

    [Fact]
    public void Delete_AcrossLines_ReturnsRemovedWithTerminators()
    {
        var buffer = new DocumentBuffer("ab\r\ncd\nef");
        var result = buffer.Apply(new TextPosition(0, 1), new TextPosition(2, 1), "");

        Assert.Equal("b\r\ncd\ne", result.RemovedText);
        AssertLines(buffer, ("af", LineEnding.None));
        Assert.Equal(new TextPosition(0, 1), result.End);
    }

    [Fact]
    public void Delete_TerminatorRange_MergesLines()
    {
        var buffer = new DocumentBuffer("ab\ncd");
        var result = buffer.Apply(new TextPosition(0, 2), new TextPosition(1, 0), "");

        Assert.Equal("\n", result.RemovedText);
        AssertLines(buffer, ("abcd", LineEnding.None));
    }

    [Fact]
    public void Replace_SpansLines_InsertReplacesRange()
    {
        var buffer = new DocumentBuffer("one\ntwo\nthree");
        var result = buffer.Apply(new TextPosition(0, 1), new TextPosition(2, 2), "@@");

        Assert.Equal("ne\ntwo\nth", result.RemovedText);
        Assert.Equal("o@@ree", buffer.GetText());
        Assert.Equal(new TextPosition(0, 3), result.End);
    }

    [Fact]
    public void Apply_ByRemovedLength_CountsTerminatorCharacters()
    {
        var buffer = new DocumentBuffer("ab\r\ncd");
        var result = buffer.Apply(new TextPosition(0, 2), 2, "");

        Assert.Equal("\r\n", result.RemovedText);
        AssertLines(buffer, ("abcd", LineEnding.None));
    }

    [Fact]
    public void Apply_ByRemovedLength_BeyondEnd_Throws()
    {
        var buffer = new DocumentBuffer("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Apply(new TextPosition(0, 1), 5, "x"));
    }

    [Fact]
    public void ApplyAtOffset_RemovingCrOfCrlf_LeavesLfEnding()
    {
        var buffer = new DocumentBuffer("ab\r\ncd");
        var result = buffer.ApplyAtOffset(2, 1, "");

        Assert.Equal("\r", result.RemovedText);
        AssertLines(buffer, ("ab", LineEnding.Lf), ("cd", LineEnding.None));
        Assert.Equal("ab\ncd", buffer.GetText());
    }

    [Fact]
    public void ApplyAtOffset_RemovingLfOfCrlf_LeavesBareCrInText()
    {
        var buffer = new DocumentBuffer("ab\r\ncd");
        var result = buffer.ApplyAtOffset(3, 1, "");

        Assert.Equal("\n", result.RemovedText);
        AssertLines(buffer, ("ab\rcd", LineEnding.None));
    }

    [Fact]
    public void Insert_CrBeforeLfEnding_MergesIntoCrlfTerminator()
    {
        // Canonical-structure rule: the inserted '\r' lands directly in front of an LF
        // terminator, so the buffer's structure must equal a fresh parse of "ab\r\ncd".
        var buffer = new DocumentBuffer("ab\ncd");
        var result = buffer.Apply(new TextPosition(0, 2), new TextPosition(0, 2), "\r");

        Assert.Equal("ab\r\ncd", buffer.GetText());
        AssertLines(buffer, ("ab", LineEnding.CrLf), ("cd", LineEnding.None));

        // The exact end offset (3) falls inside the merged CRLF terminator; End snaps to (0, 2).
        Assert.Equal(new TextPosition(0, 2), result.End);

        // Byte-exact inversion via the offset form (the SpliceResult contract).
        var undo = buffer.ApplyAtOffset(result.StartOffset, 1, result.RemovedText);
        Assert.Equal("ab\ncd", buffer.GetText());
        AssertLines(buffer, ("ab", LineEnding.Lf), ("cd", LineEnding.None));
        Assert.Equal("\r", undo.RemovedText);
    }

    [Fact]
    public void Insert_IntoEmptyDocument()
    {
        var buffer = new DocumentBuffer();
        var result = buffer.Apply(TextPosition.Zero, TextPosition.Zero, "a\nb");

        AssertLines(buffer, ("a", LineEnding.Lf), ("b", LineEnding.None));
        Assert.Equal(new TextPosition(1, 1), result.End);
    }

    [Fact]
    public void Delete_EverythingLeavesOneEmptyLine()
    {
        var buffer = new DocumentBuffer("a\r\nb\nc");
        buffer.Apply(TextPosition.Zero, new TextPosition(2, 1), "");

        AssertLines(buffer, ("", LineEnding.None));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Apply_NullInserted_Throws()
    {
        var buffer = new DocumentBuffer("a");
        Assert.Throws<ArgumentNullException>(() => buffer.Apply(TextPosition.Zero, TextPosition.Zero, null!));
    }

    [Fact]
    public void Apply_EndBeforeStart_Throws()
    {
        var buffer = new DocumentBuffer("ab\ncd");
        Assert.Throws<ArgumentException>(() => buffer.Apply(new TextPosition(1, 0), new TextPosition(0, 2), "x"));
    }

    // ---- Epoch and per-line versions ------------------------------------------------------------

    [Fact]
    public void Epoch_BumpsOncePerApply_IncludingDegenerateSplices()
    {
        var buffer = new DocumentBuffer("ab");
        Assert.Equal(0L, buffer.Epoch);

        buffer.Apply(new TextPosition(0, 1), new TextPosition(0, 1), "x");
        Assert.Equal(1L, buffer.Epoch);

        buffer.Apply(new TextPosition(0, 1), new TextPosition(0, 1), "");
        Assert.Equal(2L, buffer.Epoch);
    }

    [Fact]
    public void Versions_BumpOnlyWithinTheSplicedLineRange()
    {
        var buffer = new DocumentBuffer("a\nb\nc\nd");
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(0, buffer.GetLine(i).Version));

        buffer.Apply(new TextPosition(1, 0), new TextPosition(2, 1), "X");

        Assert.Equal(1, buffer.CurrentVersion);
        Assert.Equal(0, buffer.GetLine(0).Version); // untouched prefix
        Assert.Equal(1, buffer.GetLine(1).Version); // rewritten window
        Assert.Equal(0, buffer.GetLine(2).Version); // untouched suffix (was line 3)
        Assert.Equal("d", buffer.GetLine(2).Text);
    }

    // ---- Anchors ----------------------------------------------------------------------------

    [Fact]
    public void Anchor_BeforeSplice_Unmoved()
    {
        var buffer = new DocumentBuffer("abcdef");
        var anchor = buffer.Anchors.Register(new TextPosition(0, 2), AnchorGravity.Left);

        buffer.Apply(new TextPosition(0, 4), new TextPosition(0, 5), "XY");
        Assert.Equal(new TextPosition(0, 2), anchor.Position);
    }

    [Fact]
    public void Anchor_AtInsertionPoint_GravityDecides()
    {
        var buffer = new DocumentBuffer("abcd");
        var left = buffer.Anchors.Register(new TextPosition(0, 2), AnchorGravity.Left);
        var right = buffer.Anchors.Register(new TextPosition(0, 2), AnchorGravity.Right);

        buffer.Apply(new TextPosition(0, 2), new TextPosition(0, 2), "..");

        Assert.Equal(new TextPosition(0, 2), left.Position);
        Assert.Equal(new TextPosition(0, 4), right.Position);
    }

    [Fact]
    public void Anchor_InsideRemovedRange_CollapsesPerGravity()
    {
        var buffer = new DocumentBuffer("0123456789");
        var left = buffer.Anchors.Register(new TextPosition(0, 5), AnchorGravity.Left);
        var right = buffer.Anchors.Register(new TextPosition(0, 5), AnchorGravity.Right);

        buffer.Apply(new TextPosition(0, 3), new TextPosition(0, 7), "AB");

        Assert.Equal(new TextPosition(0, 3), left.Position);  // splice start
        Assert.Equal(new TextPosition(0, 5), right.Position); // after inserted text
    }

    [Fact]
    public void Anchor_AfterSplice_ShiftsByDelta()
    {
        var buffer = new DocumentBuffer("ab\ncd\nef");
        var sameLine = buffer.Anchors.Register(new TextPosition(1, 2), AnchorGravity.Left);
        var laterLine = buffer.Anchors.Register(new TextPosition(2, 1), AnchorGravity.Left);

        // Replace "b\nc" with "Z" — one line disappears.
        buffer.Apply(new TextPosition(0, 1), new TextPosition(1, 1), "Z");

        Assert.Equal("aZd\nef", buffer.GetText());
        Assert.Equal(new TextPosition(0, 3), sameLine.Position);
        Assert.Equal(new TextPosition(1, 1), laterLine.Position);
    }

    [Fact]
    public void Anchor_OnRemovedLine_CollapsesToValidPosition()
    {
        var buffer = new DocumentBuffer("aa\nbb\ncc");
        var anchor = buffer.Anchors.Register(new TextPosition(1, 1), AnchorGravity.Right);

        buffer.Apply(new TextPosition(0, 0), new TextPosition(2, 0), "");

        Assert.Equal("cc", buffer.GetText());
        Assert.Equal(TextPosition.Zero, anchor.Position);
    }

    [Fact]
    public void Anchor_RegisterInvalidPosition_Throws()
    {
        var buffer = new DocumentBuffer("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Anchors.Register(new TextPosition(0, 3), AnchorGravity.Left));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Anchors.Register(new TextPosition(1, 0), AnchorGravity.Left));
    }

    [Fact]
    public void Anchor_Unregister_StopsTracking()
    {
        var buffer = new DocumentBuffer("abc");
        var anchor = buffer.Anchors.Register(new TextPosition(0, 2), AnchorGravity.Left);

        Assert.True(buffer.Anchors.Unregister(anchor));
        Assert.False(buffer.Anchors.Unregister(anchor));
        Assert.Equal(0, buffer.Anchors.Count);

        buffer.Apply(TextPosition.Zero, TextPosition.Zero, "xxx");
        Assert.Equal(new TextPosition(0, 2), anchor.Position); // frozen
    }
}
