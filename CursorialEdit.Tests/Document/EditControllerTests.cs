using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Document;

/// <summary>
/// M1.WP5 — the <see cref="EditController"/> funnel contract: Apply validates
/// <see cref="Edit.Removed"/> against the live buffer before splicing (failing loudly, buffer
/// untouched), splices through the buffer's offset path (CRLF-seam honest), raises
/// <see cref="EditController.Changed"/> for every splice (undo/redo included), and treats
/// <see cref="EditKind.Replay"/> as non-recording. Grouping semantics live in
/// <see cref="UndoGroupTests"/>; the randomized sweep in <see cref="UndoFuzzTests"/>.
/// </summary>
public class EditControllerTests
{
    private static (DocumentBuffer Buffer, EditController Controller) Create(string text = "")
    {
        var buffer = new DocumentBuffer(text);
        return (buffer, new EditController(buffer, new FakeTimeProvider()));
    }

    private static CaretState Caret(int line, int col) => new(new TextPosition(line, col));

    // ---- Apply: splice + receipt + notification ---------------------------------------------

    [Fact]
    public void Apply_SplicesBufferAndReturnsReceipt()
    {
        var (buffer, controller) = Create("hello\nworld");
        var payloads = new List<SpliceResult>();
        controller.Changed += payloads.Add;

        var result = controller.Apply(
            new Edit(new TextPosition(1, 0), "world", "earth"),
            EditKind.Structural, Caret(1, 0), Caret(1, 5));

        Assert.Equal("hello\nearth", buffer.GetText());
        Assert.Equal(6, result.StartOffset);
        Assert.Equal("world", result.RemovedText);
        Assert.Equal(new TextPosition(1, 5), result.End);
        Assert.Equal(buffer.Epoch, result.Epoch);
        Assert.Equal([result], payloads);
        Assert.True(controller.CanUndo);
    }

    [Fact]
    public void Apply_RemovedSpanningTerminator_UsesLiteralTerminatorText()
    {
        var (buffer, controller) = Create("ab\ncd");

        controller.Apply(
            new Edit(new TextPosition(0, 1), "b\nc", ""),
            EditKind.Typing, Caret(1, 1), Caret(0, 1));

        Assert.Equal("ad", buffer.GetText());
    }

    [Fact]
    public void Apply_DegenerateEdit_NotifiesButRecordsNothing_AndKeepsRunOpen()
    {
        var (buffer, controller) = Create();
        controller.Apply(new Edit(TextPosition.Zero, "", "a"), EditKind.Typing, Caret(0, 0), Caret(0, 1));
        controller.Apply(new Edit(new TextPosition(0, 1), "", "b"), EditKind.Typing, Caret(0, 1), Caret(0, 2));
        Assert.Equal(1, controller.UndoDepth);

        int notifications = 0;
        controller.Changed += _ => notifications++;
        long epochBefore = buffer.Epoch;

        controller.Apply(new Edit(new TextPosition(0, 2), "", ""), EditKind.Typing, Caret(0, 2), Caret(0, 2));

        Assert.Equal(1, notifications);                 // degenerate splices still notify
        Assert.Equal(epochBefore + 1, buffer.Epoch);    // and bump the epoch, per the buffer contract
        Assert.Equal(1, controller.UndoDepth);          // but never record

        // …and they do not seal the open typing run.
        controller.Apply(new Edit(new TextPosition(0, 2), "", "c"), EditKind.Typing, Caret(0, 2), Caret(0, 3));
        Assert.Equal(1, controller.UndoDepth);
    }

    // ---- Apply: validation failures (loud, side-effect-free) ---------------------------------

    [Fact]
    public void Apply_RemovedMismatch_ThrowsAndLeavesBufferAndHistoryUntouched()
    {
        var (buffer, controller) = Create("abc");
        long epoch = buffer.Epoch;

        var exception = Assert.Throws<ArgumentException>(() => controller.Apply(
            new Edit(TextPosition.Zero, "xyz", "Q"), EditKind.Typing, Caret(0, 0), Caret(0, 1)));

        Assert.Equal("edit", exception.ParamName);
        Assert.Equal("abc", buffer.GetText());
        Assert.Equal(epoch, buffer.Epoch);
        Assert.False(controller.CanUndo);
    }

    [Fact]
    public void Apply_RemovedTerminatorMismatch_Throws()
    {
        var (buffer, controller) = Create("ab\ncd");

        Assert.Throws<ArgumentException>(() => controller.Apply(
            new Edit(new TextPosition(0, 1), "b\r\nc", ""), EditKind.Typing, Caret(1, 1), Caret(0, 1)));

        Assert.Equal("ab\ncd", buffer.GetText());
    }

    [Fact]
    public void Apply_RemovedOverrunningDocumentEnd_Throws()
    {
        var (buffer, controller) = Create("ab");

        Assert.Throws<ArgumentException>(() => controller.Apply(
            new Edit(new TextPosition(0, 1), "bcd", ""), EditKind.Typing, Caret(0, 4), Caret(0, 1)));

        Assert.Equal("ab", buffer.GetText());
    }

    [Fact]
    public void Apply_DefaultEdit_Throws()
    {
        var (_, controller) = Create("ab");

        Assert.Throws<ArgumentException>(() => controller.Apply(
            default, EditKind.Typing, Caret(0, 0), Caret(0, 0)));
    }

    [Fact]
    public void Apply_InvalidStartPosition_Throws()
    {
        var (_, controller) = Create("ab");

        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Apply(
            new Edit(new TextPosition(5, 0), "", "x"), EditKind.Typing, Caret(5, 0), Caret(5, 1)));
    }

    // ---- Replay -------------------------------------------------------------------------------

    [Fact]
    public void Apply_ReplayKind_NotifiesButNeverRecords_AndSealsOpenGroup()
    {
        var (buffer, controller) = Create();
        controller.Apply(new Edit(TextPosition.Zero, "", "a"), EditKind.Typing, Caret(0, 0), Caret(0, 1));
        controller.Apply(new Edit(new TextPosition(0, 1), "", "b"), EditKind.Typing, Caret(0, 1), Caret(0, 2));
        Assert.Equal(1, controller.UndoDepth);

        int notifications = 0;
        controller.Changed += _ => notifications++;

        controller.Apply(new Edit(new TextPosition(0, 2), "", "ZZ"), EditKind.Replay, Caret(0, 2), Caret(0, 4));

        Assert.Equal("abZZ", buffer.GetText());
        Assert.Equal(1, notifications);
        Assert.Equal(1, controller.UndoDepth); // not recorded

        // The replay sealed the run: this insertion is splice-adjacent to the open group
        // ("ab" ends at offset 2) and would otherwise coalesce.
        controller.Apply(new Edit(new TextPosition(0, 2), "", "c"), EditKind.Typing, Caret(0, 2), Caret(0, 3));
        Assert.Equal(2, controller.UndoDepth);

        // Undo-to-bottom unwinds only the recorded groups — the replayed splice stays.
        Assert.NotNull(controller.Undo());
        Assert.NotNull(controller.Undo());
        Assert.False(controller.CanUndo);
        Assert.Equal("ZZ", buffer.GetText());
    }

    // ---- Undo/redo mechanics ------------------------------------------------------------------

    [Fact]
    public void UndoRedo_OnEmptyStacks_ReturnNull()
    {
        var (_, controller) = Create("ab");

        Assert.False(controller.CanUndo);
        Assert.False(controller.CanRedo);
        Assert.Null(controller.Undo());
        Assert.Null(controller.Redo());
    }

    [Fact]
    public void UndoRedo_RoundTrip_RestoresTextAndNotifies()
    {
        var (buffer, controller) = Create("one two");
        int notifications = 0;
        controller.Changed += _ => notifications++;

        controller.Apply(new Edit(new TextPosition(0, 4), "two", "2"), EditKind.Structural, Caret(0, 4), Caret(0, 5));
        Assert.Equal("one 2", buffer.GetText());

        var undoCaret = controller.Undo();
        Assert.Equal("one two", buffer.GetText());
        Assert.Equal(Caret(0, 4), undoCaret);
        Assert.True(controller.CanRedo);

        var redoCaret = controller.Redo();
        Assert.Equal("one 2", buffer.GetText());
        Assert.Equal(Caret(0, 5), redoCaret);
        Assert.True(controller.CanUndo);
        Assert.False(controller.CanRedo);

        Assert.Equal(3, notifications); // apply + undo + redo
    }

    [Fact]
    public void Undo_AcrossCrlfSeamMerge_RestoresExactStructure()
    {
        // "ab\r" is one line whose text ends in a bare CR (content, not terminator). Inserting
        // "\n" after it merges the CR into a CRLF terminator — the recorded inserted text then
        // occupies a range whose end has no valid TextPosition. Undo must invert byte-exactly
        // through the offset path.
        var (buffer, controller) = Create("ab\r");
        Assert.Equal(1, buffer.LineCount);

        controller.Apply(new Edit(new TextPosition(0, 3), "", "\n"), EditKind.Newline, Caret(0, 3), Caret(1, 0));
        Assert.Equal("ab\r\n", buffer.GetText());
        Assert.Equal(2, buffer.LineCount);
        Assert.Equal(("ab", LineEnding.CrLf), (buffer.GetLine(0).Text, buffer.GetLine(0).Ending));

        Assert.NotNull(controller.Undo());
        Assert.Equal("ab\r", buffer.GetText());
        Assert.Equal(1, buffer.LineCount);
        Assert.Equal(("ab\r", LineEnding.None), (buffer.GetLine(0).Text, buffer.GetLine(0).Ending));

        Assert.NotNull(controller.Redo());
        Assert.Equal("ab\r\n", buffer.GetText());
        Assert.Equal(2, buffer.LineCount);
    }

    [Fact]
    public void Undo_AfterOutOfBandBufferMutation_FailsLoudly()
    {
        var (buffer, controller) = Create();
        controller.Apply(new Edit(TextPosition.Zero, "", "ab"), EditKind.Typing, Caret(0, 0), Caret(0, 2));

        // Bypassing the funnel is a contract violation; the next undo must throw, not corrupt.
        buffer.ApplyAtOffset(0, 2, "XY");

        Assert.Throws<InvalidOperationException>(() => controller.Undo());
    }

    [Fact]
    public void Undo_PureDeletionGroup_AfterReplayShrinkBeyondBounds_FailsLoudlyAndLeavesStacksUntouched()
    {
        var (buffer, controller) = Create("aaaaaaaaaaxyz");

        // A pure-deletion Typing group (Removed="xyz", Inserted=""): its undo inverse validates
        // an EMPTY expected text, so bounds are the only checkable coherence property.
        controller.Apply(new Edit(new TextPosition(0, 10), "xyz", ""), EditKind.Typing, Caret(0, 13), Caret(0, 10));
        Assert.Equal("aaaaaaaaaa", buffer.GetText());
        Assert.Equal(1, controller.UndoDepth);

        // A Replay shrink (Replay callers own coherence — incoherence fails loudly at the next
        // undo) moves the document end below the group's recorded start offset 10.
        controller.Apply(new Edit(TextPosition.Zero, "aaaaaa", ""), EditKind.Replay, Caret(0, 6), Caret(0, 0));
        Assert.Equal("aaaa", buffer.GetText());

        Assert.Throws<InvalidOperationException>(() => controller.Undo());

        // The throw left history and buffer untouched: no group migrated to redo, nothing spliced.
        Assert.True(controller.CanUndo);
        Assert.Equal(1, controller.UndoDepth);
        Assert.Equal(0, controller.RedoDepth);
        Assert.Equal("aaaa", buffer.GetText());
    }

    [Fact]
    public void Undo_PureDeletionGroup_AfterInBoundsReplayShift_ReinsertsAtTheRecordedOffset()
    {
        // ACCEPTED LIMITATION (pinned deliberately): a pure-deletion group records
        // Inserted == "", and empty expected content cannot be verified against the buffer —
        // only its BOUNDS can. A Replay edit that shifts the document while leaving the
        // recorded offset in bounds therefore undoes "successfully" at the now-stale offset.
        // Replay callers own coherence (they must rebase or clear history); this test pins the
        // boundary of what undo validation can catch for empty-expected group shapes.
        var (buffer, controller) = Create("aaaaaaaaaaxyzbbbbb");

        controller.Apply(new Edit(new TextPosition(0, 10), "xyz", ""), EditKind.Typing, Caret(0, 13), Caret(0, 10));
        Assert.Equal("aaaaaaaaaabbbbb", buffer.GetText());

        controller.Apply(new Edit(TextPosition.Zero, "aaaaa", ""), EditKind.Replay, Caret(0, 5), Caret(0, 0));
        Assert.Equal("aaaaabbbbb", buffer.GetText());

        // Offset 10 is still in bounds (== Length), so the vacuous content check passes and the
        // deleted text re-inserts at the recorded — shifted — offset. Not a corruption bug the
        // controller can detect; the Replay caller violated its coherence obligation.
        Assert.NotNull(controller.Undo());
        Assert.Equal("aaaaabbbbbxyz", buffer.GetText());
    }

    [Fact]
    public void ClearHistory_EmptiesBothStacks()
    {
        var (_, controller) = Create();
        controller.Apply(new Edit(TextPosition.Zero, "", "a"), EditKind.Typing, Caret(0, 0), Caret(0, 1));
        controller.SealGroup();
        controller.Apply(new Edit(new TextPosition(0, 1), "", "b"), EditKind.Typing, Caret(0, 1), Caret(0, 2));
        Assert.NotNull(controller.Undo());
        Assert.True(controller.CanUndo);
        Assert.True(controller.CanRedo);

        controller.ClearHistory();

        Assert.False(controller.CanUndo);
        Assert.False(controller.CanRedo);
        Assert.Null(controller.Undo());
        Assert.Null(controller.Redo());
    }

    // ---- Construction contracts ---------------------------------------------------------------

    [Fact]
    public void Constructor_RejectsInvalidConfiguration()
    {
        var buffer = new DocumentBuffer();

        Assert.Throws<ArgumentNullException>(() => new EditController(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditController(buffer, undoDepthLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditController(buffer, idleSealTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EditController(buffer, idleSealTimeout: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Defaults_MatchThePlan()
    {
        var controller = new EditController(new DocumentBuffer());

        Assert.Equal(1000, controller.UndoDepthLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(750), controller.IdleSealTimeout);
    }
}
