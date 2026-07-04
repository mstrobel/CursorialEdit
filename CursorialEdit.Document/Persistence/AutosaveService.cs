using System.Diagnostics;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;

namespace CursorialEdit.Document.Persistence;

/// <summary>
/// The M1 autosave stub (implementation-plan §3.2 resolution 6, spec §12): listens to
/// <see cref="EditController.Changed"/>, debounces 5 s restart-on-edit, mirrors the line array
/// on the UI thread, and writes the journal off-thread through <see cref="AutosaveJournal"/>.
/// Autosave never writes the document's real path and never clears the dirty state — an explicit
/// Save is still required. M6.WP4 replaces this stub with the full <c>AutosaveService</c>/
/// <c>RecoveryJournal</c> pair; the journal mechanics carry forward unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Debounce timing (deliberate deviation, recorded — same rationale as
/// <see cref="EditController"/>'s idle seal).</b> The plan names the frame-aligned <c>UITimer</c>,
/// but a document-core class must not require a UI frame loop, and <c>UITimer.Start</c> needs the
/// thread-ambient scheduler a <c>UIApplication</c> installs. Unlike the undo seal, the debounce
/// cannot be evaluated lazily — the write must happen while the app sits idle — so it uses
/// <see cref="TimeProvider.CreateTimer"/>: deterministic under <c>FakeTimeProvider</c>
/// (<c>Advance</c> fires it synchronously), pool-scheduled under the system clock. Hosts pass the
/// application's <see cref="TimeProvider"/> so tests drive it with the same fake clock as
/// everything else.
/// </para>
/// <para>
/// <b>Capture cost (copy-on-write mirror).</b> The timer callback lands on a pool thread and must
/// never read the UI-thread-only buffer, so content has to be captured on the UI thread — but a
/// full O(LineCount) copy per keystroke is GC churn that scales with document size. The service
/// therefore keeps a <i>mirror</i> of the buffer's lines: the first change after a snapshot was
/// handed to the drainer copies the buffer once; every further change in the burst splices only
/// the dirty line range out of the splice receipt (O(changed lines)); the debounce promotion then
/// wraps the mirror in O(1) and marks it shared, so the next change clones before mutating.
/// Invariant: the mirror equals the buffer's lines after every <see cref="EditController.Changed"/>,
/// so the journal always carries the final epoch's exact content after a quiet period.
/// </para>
/// <para>
/// <b>Threading model.</b> Three kinds of threads touch this class:
/// <list type="bullet">
///   <item><description><i>UI thread</i> — <see cref="EditController.Changed"/> (the mirror
///     update point: it reads the UI-thread-only buffer), <see cref="TriggerNow"/>,
///     <see cref="Delete"/>, <see cref="Dispose"/>.</description></item>
///   <item><description><i>Timer thread</i> — the debounce callback (a pool thread in production,
///     the advancing thread under a fake clock). It only wraps the already-mirrored lines into
///     the pending slot (or kicks a retry); it never touches the buffer.</description></item>
///   <item><description><i>Drainer</i> — a single <see cref="Task.Run(Action)"/> loop that owns
///     all journal I/O, so writes to one journal never overlap and always land in epoch order.</description></item>
/// </list>
/// All mutable state is guarded by one private lock; journal I/O happens outside it.
/// </para>
/// <para>
/// <b>Epoch validation (Decision 13 ethos).</b> The pending slot is "the desired journal state",
/// latest epoch wins — with one tie-break: an <b>equal-epoch delete beats a write</b>, because the
/// delete reflects a clean save at that epoch and re-writing the same epoch's content would
/// resurrect a journal the save just retired. The drainer additionally skips any snapshot whose
/// epoch is not newer than what it last journaled, so a stale write request is discarded rather
/// than clobbering newer journaled content. Construction treats the buffer's current epoch as
/// already persisted — autosave only ever journals work newer than what it was attached to.
/// </para>
/// <para>
/// <b>Failure policy.</b> A failed journal operation must not silently kill autosave until the
/// next edit (the provider contract explicitly allows one call to fail and the next to succeed):
/// the failed operation is re-queued — unless a superseding request arrived meanwhile, which then
/// wins the slot as usual — and the debounce timer is re-armed to retry one interval later, up to
/// <see cref="MaxRetriesPerQuietPeriod"/> attempts per quiet period. After the budget is exhausted
/// the service goes quiescent with <see cref="LastWriteError"/> set; any new request (edit
/// promotion, <see cref="TriggerNow"/>, <see cref="Delete"/>) opens a fresh budget and retries.
/// </para>
/// <para>
/// <b>Triggers.</b> The debounce is the guaranteed path; <see cref="TriggerNow"/> is the seam for
/// the focus-loss/app-background accelerator, which arrives with the app wiring (WP11/M6 — focus
/// events are not negotiated on every terminal, so the debounce never depends on them).
/// </para>
/// </remarks>
public sealed class AutosaveService : IDisposable
{
    /// <summary>Default write delay after the last edit (spec §12): 5 s, restarted by every edit.</summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Retry budget per quiet period (see the class remarks): after a failed journal operation,
    /// up to this many timer-paced retries before going quiescent until the next request.
    /// </summary>
    internal const int MaxRetriesPerQuietPeriod = 5;

    private readonly EditController _controller;
    private readonly IDocumentBuffer _buffer;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _debounce;
    private readonly object _gate = new();

    // ---- Guarded by _gate -----------------------------------------------------------------

    /// <summary>The copy-on-write line mirror (class remarks); null until the first change.</summary>
    private List<Line>? _mirror;

    /// <summary>Whether <see cref="_mirror"/> is referenced by a snapshot — the next change must clone before mutating.</summary>
    private bool _mirrorShared;

    /// <summary>Whether a change is waiting for the debounce to promote it.</summary>
    private bool _dirty;

    /// <summary>The epoch/timestamp of the most recent change (valid while <see cref="_dirty"/>).</summary>
    private long _dirtyEpoch;
    private DateTimeOffset _dirtyTimestamp;

    /// <summary>The desired journal state handed to the drainer (latest epoch wins); null snapshot = delete.</summary>
    private PendingOperation? _pending;

    /// <summary>The current drainer task; completed when no journal I/O is in flight or queued.</summary>
    private Task _drain = Task.CompletedTask;

    private bool _drainRunning;
    private int _retryCount;
    private long _lastJournaledEpoch;
    private int _completedWriteCount;
    private int _fullCaptureCount;
    private Exception? _lastWriteError;
    private bool _disposed;

    /// <summary>One unit of drainer work: write <paramref name="Snapshot"/>, or delete when it is <see langword="null"/>.</summary>
    private readonly record struct PendingOperation(AutosaveSnapshot? Snapshot, long Epoch);

    /// <summary>
    /// Creates the service and subscribes to <paramref name="controller"/>.Changed. The buffer's
    /// construction-time epoch counts as already persisted: a document with no further edits is
    /// never journaled, even by <see cref="TriggerNow"/>.
    /// </summary>
    /// <param name="controller">The edit funnel whose <see cref="EditController.Changed"/> drives the debounce.</param>
    /// <param name="journal">The journal this service writes.</param>
    /// <param name="timeProvider">
    /// The clock for the debounce timer and snapshot timestamps; defaults to
    /// <see cref="TimeProvider.System"/>. Pass the application's provider so fake-clock tests
    /// drive the debounce deterministically.
    /// </param>
    /// <param name="debounceInterval">Write delay after the last edit; defaults to <see cref="DefaultDebounceInterval"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> or <paramref name="journal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="debounceInterval"/> ≤ 0.</exception>
    public AutosaveService(
        EditController controller,
        AutosaveJournal journal,
        TimeProvider? timeProvider = null,
        TimeSpan? debounceInterval = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(journal);

        if (debounceInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceInterval), interval, "Debounce interval must be positive.");

        _controller = controller;
        _buffer = controller.Buffer;
        Journal = journal;
        _timeProvider = timeProvider ?? TimeProvider.System;
        DebounceInterval = debounceInterval ?? DefaultDebounceInterval;
        _lastJournaledEpoch = _buffer.Epoch;

        _controller.Changed += OnChanged;
        _debounce = _timeProvider.CreateTimer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The journal this service writes to and deletes.</summary>
    public AutosaveJournal Journal { get; }

    /// <summary>Write delay after the last edit; each edit restarts it.</summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>
    /// The epoch of the last completed journal operation (write or delete); the buffer's
    /// construction-time epoch until then. Snapshots at or below this are stale and never written.
    /// </summary>
    public long LastJournaledEpoch
    {
        get { lock (_gate) return _lastJournaledEpoch; }
    }

    /// <summary>Number of journal writes that have completed successfully (status/test seam).</summary>
    public int CompletedWriteCount
    {
        get { lock (_gate) return _completedWriteCount; }
    }

    /// <summary>
    /// Number of full O(LineCount) buffer copies the mirror has made (test seam): exactly one per
    /// edit burst — the first change after a snapshot handoff — never one per keystroke.
    /// </summary>
    internal int FullCaptureCount
    {
        get { lock (_gate) return _fullCaptureCount; }
    }

    /// <summary>The most recent journal I/O failure, cleared by the next successful operation. Autosave failures never throw into the app.</summary>
    public Exception? LastWriteError
    {
        get { lock (_gate) return _lastWriteError; }
    }

    /// <summary>
    /// The in-flight journal work: completes when everything currently queued for the drainer
    /// (writes and deletes) has been processed; <see cref="Task.CompletedTask"/> when idle.
    /// Tests join this instead of sleeping; it never faults (failures land in
    /// <see cref="LastWriteError"/>). Note it does not cover a debounce that has not elapsed yet —
    /// advance the clock first.
    /// </summary>
    public Task PendingWrite
    {
        get { lock (_gate) return _drain; }
    }

    /// <summary>
    /// Journals the current document state immediately, bypassing (and disarming) the debounce —
    /// the focus-loss/app-background trigger seam. UI thread only (captures the buffer). No-op if
    /// nothing changed since the last journaled epoch or after disposal.
    /// </summary>
    public void TriggerNow()
    {
        var snapshot = AutosaveSnapshot.Capture(_buffer, _timeProvider.GetUtcNow());
        lock (_gate)
        {
            if (_disposed)
                return;

            _dirty = false;
            _debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            RequestLocked(new PendingOperation(snapshot, snapshot.Epoch));
        }
    }

    /// <summary>
    /// Removes the journal for a clean save/close (spec §12): disarms the debounce, discards any
    /// captured snapshot, and queues the delete through the drainer so it lands <i>after</i> any
    /// in-flight write rather than racing it. The buffer's current epoch becomes the persisted
    /// baseline — only strictly newer edits journal again. UI thread only.
    /// </summary>
    public void Delete()
    {
        long epoch = _buffer.Epoch;
        lock (_gate)
        {
            if (_disposed)
                return;

            _dirty = false;
            _debounce.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            RequestLocked(new PendingOperation(null, epoch));
        }
    }

    /// <summary>
    /// Unsubscribes and stops the debounce timer. Journal work already handed to the drainer
    /// completes normally (await <see cref="PendingWrite"/> to observe it); nothing new is
    /// accepted. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _dirty = false;
            _mirror = null;
        }

        _controller.Changed -= OnChanged;
        _debounce.Dispose();
    }

    // ---- UI thread: mirror the change ---------------------------------------------------------

    private void OnChanged(SpliceResult result)
    {
        // The mirror is updated on the UI thread (the timer callback fires on a pool thread and
        // must never touch the buffer), but with a copy-on-write discipline so a typing burst
        // costs one full copy plus O(changed lines) per keystroke — see the class remarks.
        var timestamp = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_disposed)
                return;

            UpdateMirrorLocked(result);
            _dirty = true;
            _dirtyEpoch = result.Epoch;
            _dirtyTimestamp = timestamp;
            _debounce.Change(DebounceInterval, Timeout.InfiniteTimeSpan); // restart-on-edit
        }
    }

    /// <summary>
    /// Brings <see cref="_mirror"/> up to the buffer's current lines: a full copy when there is
    /// no private mirror (first change after construction or after a snapshot handoff), otherwise
    /// an in-place splice of the dirty line range derived from the receipt — the same post-splice
    /// window math the block producer uses.
    /// </summary>
    private void UpdateMirrorLocked(SpliceResult result)
    {
        if (_mirror is null || _mirrorShared)
        {
            var lines = new List<Line>(_buffer.LineCount);
            for (var i = 0; i < _buffer.LineCount; i++)
                lines.Add(_buffer.GetLine(i));

            _mirror = lines;
            _mirrorShared = false;
            _fullCaptureCount++;
            return;
        }

        // Nothing before StartOffset changed, so the line containing it indexes identically pre-
        // and post-splice; the removed text's '\n' count spans the old range, End.Line ends the
        // new one. Replace in place where the counts overlap (O(1) for the typing case).
        int dirtyStart = _buffer.GetPosition(result.StartOffset).Line;
        int oldDirtyEnd = dirtyStart + CountLineBreaks(result.RemovedText);
        int newDirtyEnd = result.End.Line;

        int oldCount = oldDirtyEnd - dirtyStart + 1;
        int newCount = newDirtyEnd - dirtyStart + 1;
        int common = Math.Min(oldCount, newCount);
        for (var i = 0; i < common; i++)
            _mirror[dirtyStart + i] = _buffer.GetLine(dirtyStart + i);

        if (oldCount > newCount)
        {
            _mirror.RemoveRange(dirtyStart + common, oldCount - newCount);
        }
        else if (newCount > oldCount)
        {
            var grown = new Line[newCount - oldCount];
            for (var i = 0; i < grown.Length; i++)
                grown[i] = _buffer.GetLine(dirtyStart + common + i);

            _mirror.InsertRange(dirtyStart + common, grown);
        }

        Debug.Assert(_mirror.Count == _buffer.LineCount,
            $"Autosave mirror drifted: {_mirror.Count} lines mirrored, buffer has {_buffer.LineCount}.");
    }

    private static int CountLineBreaks(string text)
    {
        var breaks = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                breaks++;
        }

        return breaks;
    }

    // ---- Timer thread: promote the mirror to pending (or kick a retry) ------------------------

    private void OnDebounceElapsed(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_dirty)
            {
                // O(1) promotion: wrap the mirrored lines — no buffer access on this thread.
                // Marking the mirror shared makes the next UI-thread change clone before mutating.
                _dirty = false;
                _mirrorShared = true;
                var snapshot = AutosaveSnapshot.ForLines(_mirror!, _dirtyEpoch, _dirtyTimestamp);
                RequestLocked(new PendingOperation(snapshot, snapshot.Epoch));
            }
            else if (_pending is not null)
            {
                // Retry tick: a failed operation was re-queued without a running drainer (see
                // Drain's failure path) — attempt it again.
                StartDrainLocked();
            }
        }
    }

    // ---- Write funnel ------------------------------------------------------------------------

    /// <summary>
    /// The write-request funnel <see cref="TriggerNow"/> and the debounce share; internal so
    /// tests can inject a stale snapshot and prove the epoch validation discards it.
    /// </summary>
    internal void RequestWrite(AutosaveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_disposed)
                return;

            RequestLocked(new PendingOperation(snapshot, snapshot.Epoch));
        }
    }

    private void RequestLocked(PendingOperation operation)
    {
        // Latest epoch wins the slot: the journal should converge on the newest desired state,
        // whether that is content or deletion. Equal-epoch ties go to a queued delete — see
        // Displaces and the class remarks (review wave3-3).
        if (_pending is { } current && !Displaces(operation, current))
            return;

        _pending = operation;
        _retryCount = 0; // every accepted request opens a fresh retry budget (class remarks)
        StartDrainLocked();
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> should take the pending slot from
    /// <paramref name="current"/>: strictly newer epochs always win; on equal epochs a pending
    /// delete survives (it reflects a clean save at that epoch — re-writing the same epoch's
    /// content would resurrect the journal the save retired), while an equal-epoch write is
    /// replaced by either kind.
    /// </summary>
    private static bool Displaces(in PendingOperation candidate, in PendingOperation current)
    {
        if (candidate.Epoch != current.Epoch)
            return candidate.Epoch > current.Epoch;

        return current.Snapshot is not null;
    }

    private void StartDrainLocked()
    {
        if (!_drainRunning)
        {
            _drainRunning = true;
            _drain = Task.Run(Drain);
        }
    }

    // ---- Drainer (pool thread): all journal I/O ----------------------------------------------

    private void Drain()
    {
        while (true)
        {
            PendingOperation operation;
            lock (_gate)
            {
                if (_pending is not { } next)
                {
                    _drainRunning = false;
                    return;
                }

                _pending = null;

                // Epoch validation: a snapshot no newer than what is already journaled is stale —
                // discard the request instead of rewriting older content (Decision 13 ethos).
                // Deletes are exempt: a clean save must remove the journal even when the journal
                // already holds exactly the saved epoch's content.
                if (next.Snapshot is not null && next.Epoch <= _lastJournaledEpoch)
                    continue;

                operation = next;
            }

            try
            {
                if (operation.Snapshot is { } snapshot)
                    Journal.Write(snapshot);
                else
                    Journal.Delete();

                lock (_gate)
                {
                    _lastJournaledEpoch = Math.Max(_lastJournaledEpoch, operation.Epoch);
                    if (operation.Snapshot is not null)
                        _completedWriteCount++;

                    _lastWriteError = null;
                    _retryCount = 0; // success closes the failure episode
                }
            }
            catch (Exception error)
            {
                // Autosave must never take the app down; the failure is surfaced on
                // LastWriteError and the previous journal remains intact (atomic write). The
                // operation is NOT abandoned (review wave3-2): a transient failure retries on
                // the timer during the quiet period, bounded per the class remarks.
                lock (_gate)
                {
                    _lastWriteError = error;

                    // A request queued during the failed attempt keeps the slot when it
                    // supersedes this one — loop and serve it now.
                    if (_pending is { } queued && !Displaces(operation, queued))
                        continue;

                    // Re-queue the failed operation and hand the retry to the timer instead of
                    // spinning here; exhausting the budget leaves the service quiescent with
                    // LastWriteError set until the next request re-opens it.
                    _pending = operation;
                    _drainRunning = false;

                    if (!_disposed && _retryCount < MaxRetriesPerQuietPeriod)
                    {
                        _retryCount++;
                        _debounce.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
                    }

                    return;
                }
            }
        }
    }
}
