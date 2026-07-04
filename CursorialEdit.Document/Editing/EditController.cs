using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Document.Editing;

/// <summary>
/// The single mutation funnel (architecture Decision 10, §2.1): every buffer change enters
/// through <see cref="Apply"/>, which validates the edit against the live buffer, performs the
/// splice, records the undo group, and raises <see cref="Changed"/>. Undo/redo replay the
/// recorded splices through the same path, flagged non-recording. Anchors live in the buffer
/// and track splices there; the controller adjusts nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Undo groups.</b> One document-scoped stack of coalescing edit groups — the
/// <c>TextBox</c> splice+coalesce records (Cursorial <c>Controls/TextBox.cs</c>) reimplemented
/// at document scope. Adjacent <see cref="EditKind.Typing"/> edits coalesce by splice
/// adjacency: a pure insertion extends an insert run ending exactly where it starts; a pure
/// deletion extends a delete run leftward (its end is the run's start — Backspace) or
/// rightward (it starts at the run's start — forward Delete), with the run's direction locked
/// by its first merge so direction switches start a new group, as in the reference. A group
/// seals — meaning the next edit starts a new group — on: a caret move not caused by an edit
/// (<see cref="NotifyCaretMoved"/>), any non-Typing kind (each is its own atomic unit),
/// explicit <see cref="SealGroup"/>, undo/redo, or <see cref="IdleSealTimeout"/> (750 ms) of
/// idle time between recorded edits.
/// </para>
/// <para>
/// <b>Idle seal timing (deliberate deviation, recorded).</b> The plan names the frame-aligned
/// <c>UITimer</c>, but <c>UITimer.Start</c> requires the thread-ambient
/// <c>AnimationScheduler</c> a <c>UIApplication</c> installs — it cannot run in plain unit
/// tests, and a document-core class must not require a UI frame loop. A group's sealed-ness is
/// observable <i>only</i> through whether the next edit coalesces (undo always consumes whole
/// groups), so the seal is evaluated lazily instead: each recorded edit stamps
/// <see cref="TimeProvider.GetTimestamp"/>, and coalescing additionally requires the elapsed
/// time since the previous recorded edit to be under <see cref="IdleSealTimeout"/>. This is
/// observationally equivalent to an eager timer, deterministic under any clock, and leaves
/// nothing to stop on teardown. Hosts should construct the controller with the application's
/// <see cref="TimeProvider"/> (the one <c>UIApplicationBuilder.WithTimeProvider</c> installs;
/// <c>UITestHost.Time</c> in host tests) so <c>AdvanceTime</c> drives the seal deterministically.
/// </para>
/// <para>
/// <b>Depth and redo.</b> The stack keeps at most <see cref="UndoDepthLimit"/> groups (oldest
/// discarded); any newly recorded edit clears the redo stack. Undo/redo validate the recorded
/// text against the buffer before splicing and throw <see cref="InvalidOperationException"/>
/// on mismatch — with every mutation funneled here that is an invariant violation, and it
/// fails loudly rather than corrupting silently (Decision 13's ethos).
/// </para>
/// <para>All members are UI-thread-only, like the buffer they wrap.</para>
/// </remarks>
public sealed class EditController
{
    /// <summary>Default undo depth (plan §6 WP5): 1000 groups, oldest discarded.</summary>
    public const int DefaultUndoDepthLimit = 1000;

    /// <summary>Default idle seal window (architecture §2.1): 750 ms without a recorded edit seals the open group.</summary>
    public static readonly TimeSpan DefaultIdleSealTimeout = TimeSpan.FromMilliseconds(750);

    private readonly IDocumentBuffer _buffer;
    private readonly TimeProvider _timeProvider;
    private readonly List<UndoGroup> _undo = [];
    private readonly List<UndoGroup> _redo = [];

    /// <summary>Whether the top undo group is open to coalescing the next adjacent Typing edit.</summary>
    private bool _canCoalesce;

    /// <summary>
    /// One-shot echo license: the open group's post-edit caret landing, set when an edit
    /// records and consumed by the first matching <see cref="NotifyCaretMoved"/>. See that
    /// method for the caller contract.
    /// </summary>
    private CaretState? _pendingEchoCaret;

    /// <summary>Timestamp of the most recent recorded edit — the idle-seal reference point.</summary>
    private long _lastRecordedTimestamp;

    /// <summary>How a group's content constrains coalescing.</summary>
    private enum GroupShape
    {
        /// <summary>Non-coalescing unit: any non-Typing kind, or replace-typing (removed and inserted both non-empty).</summary>
        Atomic,

        /// <summary>A run of pure insertions; extends at its right edge.</summary>
        InsertRun,

        /// <summary>A single pure deletion — direction not yet determined; the first merge locks it.</summary>
        DeleteRun,

        /// <summary>A Backspace run; extends leftward only.</summary>
        DeleteRunBackward,

        /// <summary>A forward-Delete run; extends rightward only (fixed start).</summary>
        DeleteRunForward,
    }

    /// <summary>
    /// One undo group: a single coalesced splice (<c>TextBox.UndoEntry</c> at document scope).
    /// <see cref="Start"/> is an absolute offset — stable ground for inversion, and the honest
    /// coordinate when a splice seam merged into a CRLF terminator (see <see cref="SpliceResult"/>).
    /// Mutable so a coalescing run grows it in place; <see cref="CaretBefore"/> stays from the
    /// run's first edit.
    /// </summary>
    private sealed class UndoGroup
    {
        public int Start;
        public string Removed = "";
        public string Inserted = "";
        public GroupShape Shape;
        public CaretState CaretBefore;
        public CaretState CaretAfter;
    }

    /// <summary>Creates the mutation funnel for <paramref name="buffer"/>.</summary>
    /// <param name="buffer">The document buffer all edits splice.</param>
    /// <param name="timeProvider">
    /// The clock for the idle seal; defaults to <see cref="TimeProvider.System"/>. Pass the
    /// application's provider so fake-clock tests drive the seal deterministically.
    /// </param>
    /// <param name="undoDepthLimit">Maximum retained undo groups (≥ 1); the oldest group is discarded beyond it.</param>
    /// <param name="idleSealTimeout">Idle window that seals the open group; defaults to <see cref="DefaultIdleSealTimeout"/> (750 ms).</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="undoDepthLimit"/> &lt; 1, or <paramref name="idleSealTimeout"/> ≤ 0.</exception>
    public EditController(
        IDocumentBuffer buffer,
        TimeProvider? timeProvider = null,
        int undoDepthLimit = DefaultUndoDepthLimit,
        TimeSpan? idleSealTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfLessThan(undoDepthLimit, 1);

        if (idleSealTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleSealTimeout), timeout, "Idle seal timeout must be positive.");

        _buffer = buffer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        UndoDepthLimit = undoDepthLimit;
        IdleSealTimeout = idleSealTimeout ?? DefaultIdleSealTimeout;
        _lastRecordedTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>The buffer this controller mutates.</summary>
    public IDocumentBuffer Buffer => _buffer;

    /// <summary>Maximum retained undo groups; the oldest is discarded when a new group exceeds it.</summary>
    public int UndoDepthLimit { get; }

    /// <summary>Idle time between recorded edits that seals the open coalescing group.</summary>
    public TimeSpan IdleSealTimeout { get; }

    /// <summary>Whether <see cref="Undo"/> has a group to consume.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether <see cref="Redo"/> has a group to consume.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Number of undoable groups currently recorded.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>Number of redoable groups currently recorded.</summary>
    public int RedoDepth => _redo.Count;

    /// <summary>
    /// Raised after every splice this controller performs — applies, undos, and redos alike —
    /// with the buffer's receipt. The parser/view reconciliation seam until WP7's
    /// <c>BlockListChange</c> replaces it as the rich notification.
    /// </summary>
    public event Action<SpliceResult>? Changed;

    /// <summary>
    /// Applies one edit: validates <see cref="Edit.Removed"/> against the buffer (failing
    /// loudly on mismatch, buffer untouched), splices, records the undo group (unless
    /// <paramref name="kind"/> is <see cref="EditKind.Replay"/>), and raises <see cref="Changed"/>.
    /// A degenerate edit (nothing removed, nothing inserted) splices — bumping the buffer's
    /// epoch, per its contract — and notifies, but records nothing and leaves the open group's
    /// coalescing state untouched.
    /// </summary>
    /// <param name="edit">The edit; <see cref="Edit.Removed"/> must equal the buffer text at <see cref="Edit.Start"/>.</param>
    /// <param name="kind">How the edit folds into the undo history.</param>
    /// <param name="before">Caret state to restore when this edit's group is undone (first edit of a run wins).</param>
    /// <param name="after">Caret state to restore when this edit's group is redone (last edit of a run wins).</param>
    /// <returns>The buffer's splice receipt.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="edit"/> carries null strings (a <c>default</c> <see cref="Edit"/>), or
    /// its <see cref="Edit.Removed"/> does not match the buffer at <see cref="Edit.Start"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="Edit.Start"/> is not a valid position.</exception>
    public SpliceResult Apply(Edit edit, EditKind kind, CaretState before, CaretState after)
    {
        if (edit.Removed is null || edit.Inserted is null)
            throw new ArgumentException("Edit.Removed and Edit.Inserted must be non-null (a default Edit is not applicable).", nameof(edit));

        int startOffset = _buffer.GetOffset(edit.Start); // validates Start
        ValidateTextAt(startOffset, edit.Removed,
            mismatch => new ArgumentException(
                $"Edit.Removed does not match the buffer at {edit.Start} (offset {startOffset}): {mismatch}", nameof(edit)));

        var result = _buffer.ApplyAtOffset(startOffset, edit.Removed.Length, edit.Inserted);

        if (kind == EditKind.Replay)
        {
            _canCoalesce = false; // the document moved under the open group; typing adjacency is void
            _pendingEchoCaret = null;
        }
        else if (edit.Removed.Length > 0 || edit.Inserted.Length > 0)
            Record(startOffset, edit.Removed, edit.Inserted, kind, before, after);

        Changed?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Seals the open coalescing group for a caret move <b>not caused by an edit</b> (§3.3):
    /// arrow keys, clicks, find navigation.
    /// </summary>
    /// <remarks>
    /// <b>Caller contract.</b> Route <b>every</b> caret change here, in order — including the
    /// landing an applied edit itself produces. Each recorded edit grants a <b>one-shot</b>
    /// echo license for its own post-edit caret: the first notification equal to that landing
    /// is consumed as the edit's echo and does not seal; every subsequent notification — equal
    /// to the landing or not — seals per the normal rule. A standing equality guard would
    /// misclassify a genuine user move that happens to land on the run's post-edit position
    /// (a click at the caret, End on a line ending there) as an echo; the one-shot license
    /// keeps exactly one echo per edit and treats everything else as an independent move,
    /// matching the <c>TextBox</c> reference's seal-on-explicit-caret-move behavior.
    /// </remarks>
    /// <param name="current">The caret state after the move.</param>
    public void NotifyCaretMoved(CaretState current)
    {
        if (!_canCoalesce)
            return;

        if (_pendingEchoCaret is { } echo && current == echo)
        {
            _pendingEchoCaret = null; // the edit's own landing — its one-shot license is spent
            return;
        }

        _canCoalesce = false;
        _pendingEchoCaret = null;
    }

    /// <summary>Explicitly seals the open coalescing group; the next edit starts a new one. Idempotent.</summary>
    public void SealGroup()
    {
        _canCoalesce = false;
        _pendingEchoCaret = null;
    }

    /// <summary>Discards all undo and redo history (e.g. on loading a different document).</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _canCoalesce = false;
        _pendingEchoCaret = null;
    }

    /// <summary>
    /// Undoes the top group: validates the recorded inserted text still occupies its range,
    /// replays the inverse splice (non-recording), moves the group to the redo stack, and
    /// raises <see cref="Changed"/>. Validation and the splice both run <b>before</b> the group
    /// moves between stacks, so a throw leaves the stacks consistent with the untouched buffer.
    /// </summary>
    /// <returns>
    /// The caret state captured before the group's first edit — including its selection —
    /// for the caller to apply; <see langword="null"/> when there is nothing to undo.
    /// </returns>
    /// <exception cref="InvalidOperationException">The recorded group no longer matches the buffer (history incoherence — an invariant violation).</exception>
    public CaretState? Undo()
    {
        if (_undo.Count == 0)
            return null;

        var group = _undo[^1];
        ValidateTextAt(group.Start, group.Inserted,
            mismatch => new InvalidOperationException(
                $"Undo history is incoherent with the buffer at offset {group.Start}: {mismatch}"));

        var result = _buffer.ApplyAtOffset(group.Start, group.Inserted.Length, group.Removed);

        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(group);
        _canCoalesce = false;
        _pendingEchoCaret = null;

        Changed?.Invoke(result);
        return group.CaretBefore;
    }

    /// <summary>
    /// Redoes the most recently undone group: validates the recorded removed text still
    /// occupies its range, replays the splice (non-recording), moves the group back to the
    /// undo stack, and raises <see cref="Changed"/>. Validation and the splice both run
    /// <b>before</b> the group moves between stacks, so a throw leaves the stacks consistent
    /// with the untouched buffer.
    /// </summary>
    /// <returns>
    /// The caret state captured after the group's last edit, for the caller to apply;
    /// <see langword="null"/> when there is nothing to redo.
    /// </returns>
    /// <exception cref="InvalidOperationException">The recorded group no longer matches the buffer (history incoherence — an invariant violation).</exception>
    public CaretState? Redo()
    {
        if (_redo.Count == 0)
            return null;

        var group = _redo[^1];
        ValidateTextAt(group.Start, group.Removed,
            mismatch => new InvalidOperationException(
                $"Redo history is incoherent with the buffer at offset {group.Start}: {mismatch}"));

        var result = _buffer.ApplyAtOffset(group.Start, group.Removed.Length, group.Inserted);

        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(group);
        _canCoalesce = false;
        _pendingEchoCaret = null;

        Changed?.Invoke(result);
        return group.CaretAfter;
    }

    // ---- Recording ------------------------------------------------------------------------

    private void Record(int start, string removed, string inserted, EditKind kind, CaretState before, CaretState after)
    {
        _redo.Clear(); // a new edit invalidates the redo branch

        long now = _timeProvider.GetTimestamp();
        bool withinIdleWindow = _timeProvider.GetElapsedTime(_lastRecordedTimestamp, now) < IdleSealTimeout;
        _lastRecordedTimestamp = now;

        if (kind == EditKind.Typing && _canCoalesce && withinIdleWindow && _undo.Count > 0
            && TryCoalesce(_undo[^1], start, removed, inserted, after))
        {
            _pendingEchoCaret = after; // re-arm the one-shot echo license for this edit's landing
            return;
        }

        var shape = Classify(kind, removed, inserted);
        _undo.Add(new UndoGroup
        {
            Start = start, Removed = removed, Inserted = inserted, Shape = shape,
            CaretBefore = before, CaretAfter = after,
        });
        _canCoalesce = kind == EditKind.Typing && shape != GroupShape.Atomic;
        _pendingEchoCaret = _canCoalesce ? after : null;

        if (_undo.Count > UndoDepthLimit)
            _undo.RemoveAt(0); // depth cap: oldest discarded
    }

    private static GroupShape Classify(EditKind kind, string removed, string inserted)
    {
        if (kind != EditKind.Typing)
            return GroupShape.Atomic;

        if (removed.Length == 0)
            return GroupShape.InsertRun;

        return inserted.Length == 0
            ? GroupShape.DeleteRun
            : GroupShape.Atomic; // replace-typing: atomic, like TextBox's Other
    }

    /// <summary>
    /// Tries to fold a Typing edit into the open group by splice adjacency (the document-scope
    /// <c>TextBox.TryCoalesce</c>: direction is inferred from adjacency rather than tagged by
    /// the key handler, because the controller sees only splices). <c>CaretBefore</c> is never
    /// touched — undo restores the run's original caret.
    /// </summary>
    private static bool TryCoalesce(UndoGroup top, int start, string removed, string inserted, CaretState after)
    {
        if (removed.Length == 0 && inserted.Length > 0)
        {
            if (top.Shape != GroupShape.InsertRun || start != top.Start + top.Inserted.Length)
                return false; // not an insert run, or non-contiguous

            top.Inserted += inserted; // contiguous typing
        }
        else if (inserted.Length == 0 && removed.Length > 0)
        {
            if (top.Shape is GroupShape.DeleteRun or GroupShape.DeleteRunBackward
                && start + removed.Length == top.Start)
            {
                top.Removed = removed + top.Removed; // Backspace extends the run leftward
                top.Start = start;
                top.Shape = GroupShape.DeleteRunBackward;
            }
            else if (top.Shape is GroupShape.DeleteRun or GroupShape.DeleteRunForward
                     && start == top.Start)
            {
                top.Removed += removed; // forward Delete extends the run rightward (start fixed)
                top.Shape = GroupShape.DeleteRunForward;
            }
            else
            {
                return false; // direction switch or non-contiguous ⇒ new group
            }
        }
        else
        {
            return false; // replace-typing never merges
        }

        top.CaretAfter = after;
        return true;
    }

    // ---- Validation -----------------------------------------------------------------------

    /// <summary>
    /// Asserts that <paramref name="expected"/> occupies the serialized range starting at
    /// <paramref name="offset"/>, throwing via <paramref name="makeError"/> (buffer untouched)
    /// otherwise. Reads by offset, not position, because a recorded range boundary may fall
    /// inside a CRLF terminator that a splice seam merged (see <see cref="SpliceResult"/>).
    /// The range's bounds are checked even when <paramref name="expected"/> is empty — an empty
    /// expected text (a pure deletion's undo, a pure insertion's redo) has no content to verify,
    /// so bounds are the only coherence check available for those group shapes.
    /// </summary>
    private void ValidateTextAt(int offset, string expected, Func<string, Exception> makeError)
    {
        if (offset < 0)
            throw makeError($"the range starts at negative offset {offset}.");

        if (offset + expected.Length > _buffer.Length)
            throw makeError($"the range reaches offset {offset + expected.Length} but the document ends at {_buffer.Length}.");

        if (expected.Length == 0)
            return; // nothing verifiable beyond the bounds

        // The buffer owns the offset-honest read (boundaries inside CRLF terminators included) —
        // the same seam ApplyAtOffset splices through, so validation reads exactly what the
        // splice would remove.
        string actual = _buffer.GetTextAtOffset(offset, expected.Length);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw makeError($"expected {Describe(expected)} but the buffer holds {Describe(actual)}.");
    }

    /// <summary>Compact, terminator-visible description of a text for mismatch diagnostics.</summary>
    private static string Describe(string text)
    {
        const int maxShown = 64;
        string shown = text.Length <= maxShown ? text : text[..maxShown] + $"… ({text.Length} units)";
        return "\"" + shown.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }
}
