using System.Text;
using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Persistence;

/// <summary>
/// M1.WP12 gate — the autosave stub on a fake clock: journal written within 5 s of the last edit
/// with the debounce restarting per edit; the real file never touched; atomic temp-then-rename
/// with no residue and a failed write leaving the previous journal intact; epoch-validated
/// post-back discarding stale snapshots; byte-exact round trip of mixed CRLF/LF endings through
/// header + content; untitled-id keying; clean-save Delete. Off-thread writes are joined through
/// <see cref="AutosaveService.PendingWrite"/> — never by sleeping.
/// </summary>
public class AutosaveJournalTests
{
    private static readonly TimeSpan Debounce = AutosaveService.DefaultDebounceInterval;

    // ---- Debounce timing --------------------------------------------------------------------

    [Fact]
    public async Task JournalWrittenWithin5s_AfterLastEdit_FakeClock()
    {
        using var h = new AutosaveHarness("hello");

        h.Edit(" world");
        h.Time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        await h.Service.PendingWrite;
        Assert.False(h.Journal.Exists()); // quiet period not over yet

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await h.Service.PendingWrite;

        Assert.True(h.Journal.Exists());
        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("hello world", record!.Text);
        Assert.Null(h.Service.LastWriteError);
    }

    [Fact]
    public async Task DebounceRestartsOnEdit_EditsAt0_3_6_SingleWriteAt11()
    {
        using var h = new AutosaveHarness();

        h.Edit("a");                              // t+0  → due t+5
        h.Time.Advance(TimeSpan.FromSeconds(3));  // t+3
        h.Edit("b");                              //      → due t+8
        h.Time.Advance(TimeSpan.FromSeconds(3));  // t+6
        h.Edit("c");                              //      → due t+11

        h.Time.Advance(TimeSpan.FromSeconds(4.9)); // t+10.9
        await h.Service.PendingWrite;
        Assert.False(h.Journal.Exists()); // never 5 quiet seconds so far

        h.Time.Advance(TimeSpan.FromSeconds(0.1)); // t+11
        await h.Service.PendingWrite;

        Assert.Equal(1, h.Service.CompletedWriteCount); // one write, carrying all three edits
        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("abc", record!.Text);
    }

    [Fact]
    public async Task PristineDocument_IsNeverJournaled()
    {
        using var h = new AutosaveHarness("saved content");

        h.Service.TriggerNow(); // e.g. focus loss right after open — nothing new to protect
        await h.Service.PendingWrite;

        Assert.False(h.Journal.Exists());
        Assert.Equal(0, h.Service.CompletedWriteCount);
    }

    // ---- The real file is sacred --------------------------------------------------------------

    [Fact]
    public async Task RealFile_NeverTouchedByAutosave()
    {
        using var paths = new TempStatePathProvider();
        Directory.CreateDirectory(paths.Root);
        string sourcePath = Path.Combine(paths.Root, "source.md");
        File.WriteAllText(sourcePath, "original\r\ncontent");
        byte[] originalBytes = File.ReadAllBytes(sourcePath);

        var time = new FakeTimeProvider();
        var buffer = new DocumentBuffer("original\r\ncontent");
        var controller = new EditController(buffer, time);
        var journal = new AutosaveJournal(paths, DocumentKey.ForPath(sourcePath));
        using var service = new AutosaveService(controller, journal, time);

        var end = new TextPosition(1, buffer.GetLine(1).Text.Length);
        controller.Apply(new Edit(end, "", " EDITED"), EditKind.Typing, new CaretState(end), new CaretState(end));
        time.Advance(Debounce);
        await service.PendingWrite;
        service.TriggerNow(); // belt and braces: the explicit trigger must not touch it either
        await service.PendingWrite;

        Assert.Equal(originalBytes, File.ReadAllBytes(sourcePath));        // byte-identical
        Assert.True(journal.Exists());                                     // the journal took the edit
        Assert.NotEqual(sourcePath, journal.GetJournalPath());
        Assert.Equal(paths.JournalDirectory, Path.GetDirectoryName(journal.GetJournalPath()));
        Assert.True(journal.TryRead(out var record));
        Assert.Equal("original\r\ncontent EDITED", record!.Text);
    }

    // ---- Atomicity ----------------------------------------------------------------------------

    [Fact]
    public async Task CompletedWrite_LeavesExactlyTheJournal_NoTempResidue()
    {
        using var h = new AutosaveHarness();

        h.Edit("payload");
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        string[] entries = Directory.GetFileSystemEntries(h.Paths.JournalDirectory);
        Assert.Equal([h.Journal.GetJournalPath()], entries);
    }

    [Fact]
    public async Task FailedWrite_ThrowingPathProvider_OriginalJournalIntact()
    {
        using var h = new AutosaveHarness("base");

        h.Edit("!");
        h.Service.TriggerNow();
        await h.Service.PendingWrite;
        Assert.True(h.Journal.TryRead(out var first));

        h.Paths.FailWith = new IOException("state directory unavailable");
        h.Edit("?");
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.IsType<IOException>(h.Service.LastWriteError);
        Assert.Equal(1, h.Service.CompletedWriteCount);

        h.Paths.FailWith = null;
        Assert.True(h.Journal.TryRead(out var after));
        Assert.Equal(first!.Text, after!.Text); // previous journal untouched by the failure
        Assert.Equal([h.Journal.GetJournalPath()], Directory.GetFileSystemEntries(h.Paths.JournalDirectory));
    }

    [Fact]
    public async Task FailedRename_CleansTempFile_ThenRecoversOnNextTrigger()
    {
        using var h = new AutosaveHarness();
        string journalPath = h.Journal.GetJournalPath();
        Directory.CreateDirectory(journalPath); // block the destination: rename must fail mid-write

        h.Edit("x");
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.NotNull(h.Service.LastWriteError);
        // The temp file was written and then cleaned up: only the blocking directory remains.
        Assert.Equal([journalPath], Directory.GetFileSystemEntries(h.Paths.JournalDirectory));

        Directory.Delete(journalPath);
        h.Service.TriggerNow();
        await h.Service.PendingWrite;

        Assert.Null(h.Service.LastWriteError);
        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("x", record!.Text);
    }

    [Fact]
    public async Task TransientWriteFailure_RetriesOnTheTimer_NoEditRequired()
    {
        // Review wave3-2: a single transient failure must not abandon the pending work until the
        // next edit — the re-queued operation retries one debounce interval later and succeeds.
        using var h = new AutosaveHarness();

        h.Edit("x");
        h.Paths.FailWith = new IOException("state directory briefly unavailable");
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.IsType<IOException>(h.Service.LastWriteError);
        Assert.Equal(0, h.Service.CompletedWriteCount);

        h.Paths.FailWith = null;
        h.Time.Advance(Debounce); // the retry tick — NO further edit happened
        await h.Service.PendingWrite;

        Assert.Null(h.Service.LastWriteError);
        Assert.Equal(1, h.Service.CompletedWriteCount);
        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("x", record!.Text);
    }

    [Fact]
    public async Task PermanentWriteFailure_BoundedRetries_ThenQuiescent_UntilTheNextRequest()
    {
        using var h = new AutosaveHarness();

        h.Paths.FailWith = new IOException("state directory gone");
        h.Edit("x");

        // Initial attempt + every timer-paced retry; then extra ticks that must do nothing.
        for (var tick = 0; tick < AutosaveService.MaxRetriesPerQuietPeriod + 5; tick++)
        {
            h.Time.Advance(Debounce);
            await h.Service.PendingWrite;
        }

        // One resolve per write attempt: the initial one plus the bounded retries — no spin, and
        // quiescence after the budget (the extra ticks above added nothing).
        Assert.Equal(1 + AutosaveService.MaxRetriesPerQuietPeriod, h.Paths.ResolveCount);
        Assert.IsType<IOException>(h.Service.LastWriteError);
        Assert.Equal(0, h.Service.CompletedWriteCount);

        // A new request re-opens the budget: the next edit journals everything once I/O recovers.
        h.Paths.FailWith = null;
        h.Edit("y");
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.Null(h.Service.LastWriteError);
        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("xy", record!.Text);
    }

    // ---- Epoch validation ---------------------------------------------------------------------

    [Fact]
    public async Task StaleSnapshot_DiscardedWhenNewerEpochAlreadyJournaled()
    {
        using var h = new AutosaveHarness();

        h.Edit("old");
        var stale = AutosaveSnapshot.Capture(h.Buffer, h.Time.GetUtcNow());

        h.Edit(" new");
        h.Service.TriggerNow();
        await h.Service.PendingWrite;
        Assert.Equal(1, h.Service.CompletedWriteCount);
        long journaledEpoch = h.Service.LastJournaledEpoch;

        h.Service.RequestWrite(stale); // the funnel the timer/TriggerNow share, fed a stale snapshot
        await h.Service.PendingWrite;

        Assert.Equal(1, h.Service.CompletedWriteCount);            // write result discarded
        Assert.Equal(journaledEpoch, h.Service.LastJournaledEpoch); // post-back state not regressed
        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("old new", record!.Text);                      // newer content survives
    }

    [Fact]
    public async Task TriggerNow_WithNothingNewSinceLastJournal_SkipsTheWrite()
    {
        using var h = new AutosaveHarness();

        h.Edit("x");
        h.Service.TriggerNow();
        await h.Service.PendingWrite;
        Assert.Equal(1, h.Service.CompletedWriteCount);

        h.Service.TriggerNow(); // same epoch — journaling it again would be a pointless rewrite
        await h.Service.PendingWrite;
        Assert.Equal(1, h.Service.CompletedWriteCount);
    }

    // ---- Round-trip fidelity --------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_MixedLineEndings_ByteExact()
    {
        const string text = "alpha\r\nbeta\ngamma\r\n\r\n直子 🎉 é\nlast";
        using var h = new AutosaveHarness(text);

        h.Edit("!"); // dirty it so the snapshot journals
        string expected = h.Buffer.GetText();

        h.Service.TriggerNow();
        await h.Service.PendingWrite;

        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal(expected, record!.Text);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(record.Text));
        Assert.Equal(h.Key.Descriptor, record.Source);
        Assert.Equal(h.Time.GetUtcNow(), record.Timestamp);
    }

    [Fact]
    public async Task CorruptedJournal_ReadsAsAbsent()
    {
        using var h = new AutosaveHarness();

        h.Edit("content");
        h.Service.TriggerNow();
        await h.Service.PendingWrite;

        string path = h.Journal.GetJournalPath();
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF; // flip a content byte: checksum must catch it
        File.WriteAllBytes(path, bytes);

        Assert.False(h.Journal.TryRead(out _));
    }

    // ---- Untitled keying -------------------------------------------------------------------------

    [Fact]
    public async Task UntitledDocument_JournalsUnderGeneratedId()
    {
        var key = DocumentKey.NewUntitled();
        using var h = new AutosaveHarness("", key);

        Assert.StartsWith("untitled-", key.JournalFileName, StringComparison.Ordinal);

        h.Edit("draft");
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal(key.Descriptor, record!.Source);
        Assert.StartsWith("untitled:", record.Source, StringComparison.Ordinal);
        Assert.Equal("draft", record.Text);
    }

    // ---- Delete (clean save/close) ----------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheJournal()
    {
        using var h = new AutosaveHarness();

        h.Edit("x");
        h.Service.TriggerNow();
        await h.Service.PendingWrite;
        Assert.True(h.Journal.Exists());

        h.Service.Delete(); // clean save: WP11's hook
        await h.Service.PendingWrite;

        Assert.False(h.Journal.Exists());
        Assert.Null(h.Service.LastWriteError);
    }

    [Fact]
    public async Task Delete_CancelsThePendingDebounce_UntilTheNextEdit()
    {
        using var h = new AutosaveHarness();

        h.Edit("x");        // debounce armed
        h.Service.Delete(); // clean save lands before it elapses
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.False(h.Journal.Exists()); // the pre-save snapshot must not resurrect the journal
        Assert.Equal(0, h.Service.CompletedWriteCount);

        h.Edit("y");        // strictly newer than the save baseline — journals again
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("xy", record!.Text);
    }

    [Fact]
    public async Task EqualEpochTriggerNow_AfterQueuedDelete_DoesNotResurrectTheJournal()
    {
        // Review wave3-3: save (Delete) then immediate focus loss (TriggerNow) with no edit in
        // between — the equal-epoch write must NOT displace the queued delete. The first write is
        // held inside the journal so both requests deterministically race for the pending slot.
        using var h = new AutosaveHarness();
        h.Edit("x"); // epoch E

        using var drainerEntered = new ManualResetEventSlim(false);
        using var releaseDrainer = new ManualResetEventSlim(false);
        h.Paths.OnResolve = () =>
        {
            drainerEntered.Set();
            releaseDrainer.Wait(TimeSpan.FromSeconds(30));
        };

        h.Service.TriggerNow(); // write(E) — the drainer blocks inside Journal.Write
        Assert.True(drainerEntered.Wait(TimeSpan.FromSeconds(30)), "the drainer never reached the journal");
        h.Paths.OnResolve = null;

        h.Service.Delete();     // clean save at E: delete(E) queued behind the in-flight write
        h.Service.TriggerNow(); // focus loss at the same epoch: must lose the tie to the delete

        releaseDrainer.Set();
        await h.Service.PendingWrite;

        Assert.False(h.Journal.Exists()); // the delete drained last — the saved document stays clean
        Assert.Null(h.Service.LastWriteError);
    }

    [Fact]
    public async Task StrictlyNewerWrite_StillReplacesAQueuedDelete()
    {
        // The other half of the tie-break: a delete is stale against a strictly newer edit.
        using var h = new AutosaveHarness();
        h.Edit("x"); // epoch E1

        using var drainerEntered = new ManualResetEventSlim(false);
        using var releaseDrainer = new ManualResetEventSlim(false);
        h.Paths.OnResolve = () =>
        {
            drainerEntered.Set();
            releaseDrainer.Wait(TimeSpan.FromSeconds(30));
        };

        h.Service.TriggerNow(); // write(E1) — held in flight
        Assert.True(drainerEntered.Wait(TimeSpan.FromSeconds(30)), "the drainer never reached the journal");
        h.Paths.OnResolve = null;

        h.Service.Delete();     // delete(E1) queued
        h.Edit("y");            // epoch E2 > E1
        h.Service.TriggerNow(); // write(E2) displaces the now-stale delete

        releaseDrainer.Set();
        await h.Service.PendingWrite;

        Assert.True(h.Journal.TryRead(out var record));
        Assert.Equal("xy", record!.Text);
    }

    // ---- Capture economics (the copy-on-write mirror) ----------------------------------------------

    [Fact]
    public async Task TypingBurst_CopiesTheBufferOnce_AndJournalsTheFinalEpoch()
    {
        // Review wave3-5: a burst costs ONE full O(LineCount) copy (the first change after a
        // snapshot handoff), keystroke deltas after — and the journal still carries the burst's
        // final content.
        using var h = new AutosaveHarness("seed");

        h.Edit("a");
        h.Edit("b");
        h.Edit("c");
        Assert.Equal(1, h.Service.FullCaptureCount);

        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;
        Assert.True(h.Journal.TryRead(out var first));
        Assert.Equal("seedabc", first!.Text);

        h.Edit("d"); // the promoted mirror is shared — this burst clones once
        h.Edit("e");
        Assert.Equal(2, h.Service.FullCaptureCount);

        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;
        Assert.True(h.Journal.TryRead(out var second));
        Assert.Equal("seedabcde", second!.Text);
        Assert.Equal(2, h.Service.FullCaptureCount);
    }

    [Fact]
    public async Task StructuralEditBursts_MirrorTracksTheBufferByteExact()
    {
        // The mirror's delta path must stay byte-exact through line-count-changing splices,
        // CRLF seams, and undo replays — the journal equals GetText() after every quiet period.
        using var h = new AutosaveHarness("alpha\r\nbeta\ngamma\n\ndelta");

        Splice(h, new TextPosition(0, 3), new TextPosition(1, 2), "X\nY"); // cross-line replace over a CRLF seam
        Splice(h, new TextPosition(2, 0), new TextPosition(3, 0), "");     // whole-line deletion
        Splice(h, new TextPosition(2, 0), new TextPosition(2, 0), "mid1\nmid2\r\nmid3"); // multi-line insertion
        h.Edit("!");                                                       // same-line append
        h.Controller.Undo();                                               // inverse splices through the same feed

        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.Equal(1, h.Service.FullCaptureCount); // the whole burst rode one copy + deltas
        Assert.True(h.Journal.TryRead(out var first));
        Assert.Equal(h.Buffer.GetText(), first!.Text);

        Splice(h, new TextPosition(0, 0), new TextPosition(1, 0), "");     // second burst: clone, then deltas
        h.Edit("\nzz");

        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.Equal(2, h.Service.FullCaptureCount);
        Assert.True(h.Journal.TryRead(out var second));
        Assert.Equal(h.Buffer.GetText(), second!.Text);
    }

    /// <summary>Applies a replacement whose removed text is read back from the buffer (position-exact splices).</summary>
    private static void Splice(AutosaveHarness h, TextPosition start, TextPosition end, string inserted)
    {
        string removed = h.Buffer.GetText(start, end);
        h.Controller.Apply(new Edit(start, removed, inserted), EditKind.Typing, new CaretState(start), new CaretState(start));
    }

    // ---- Lifecycle ---------------------------------------------------------------------------------

    [Fact]
    public void PendingWrite_IsCompletedWhenIdle()
    {
        using var h = new AutosaveHarness();
        Assert.True(h.Service.PendingWrite.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_StopsFurtherJournaling()
    {
        using var h = new AutosaveHarness();

        h.Edit("x");
        h.Service.Dispose();
        h.Time.Advance(Debounce);
        await h.Service.PendingWrite;

        Assert.False(h.Journal.Exists());
    }
}
