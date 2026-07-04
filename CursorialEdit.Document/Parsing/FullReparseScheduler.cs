namespace CursorialEdit.Document.Parsing;

/// <summary>A captured request for a debounced full reparse: the document text to parse and the epoch it was captured at.</summary>
/// <param name="Text">The full document text at capture time.</param>
/// <param name="Epoch">The <see cref="Buffer.IDocumentBuffer.Epoch"/> the text was captured at — the staleness stamp (Decision 13).</param>
public readonly record struct ReparseRequest(string Text, long Epoch);

/// <summary>
/// A debounced, off-thread, epoch-stamped full reparse (architecture Decision 3 step 4 / Decision 13).
/// When a windowed edit changes a document-global construct (a link-reference or footnote definition —
/// see <see cref="DefinitionIndex"/>), the frame is served from the windowed result and this scheduler
/// runs a full Markdig parse off the UI thread after the edits settle, then posts the result back to be
/// reconciled — but only if no newer edit has landed. A result parsed against a superseded epoch is
/// <b>rejected</b>, never applied, so a stale span can never silently corrupt the live model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Marshaling (modelled on <see cref="Persistence.AutosaveService"/>).</b> The snapshot is captured
/// on the UI thread each time a reparse is scheduled (never read off-thread); the debounce runs on a
/// <see cref="TimeProvider"/> timer (deterministic under a fake clock, pool-scheduled under the system
/// clock); the parse runs on <see cref="Task.Run(System.Action)"/>; and the result is delivered back
/// through the injected <c>post</c> dispatcher — <c>UIDispatcher.InvokeAsync</c> in the app,
/// synchronous inline in document-core tests. The apply callback re-checks the epoch against the live
/// buffer, so correctness never depends on <i>which</i> thread apply runs on.
/// </para>
/// <para>
/// <b>Coalescing.</b> Scheduling restarts the debounce and overwrites the pending snapshot, so a burst
/// of definition edits collapses to a single parse of the final text — matching the autosave debounce
/// and the §13 "no full reparse per keystroke" budget (a full reparse runs at most once per quiet
/// period, off-thread).
/// </para>
/// </remarks>
/// <typeparam name="TParse">The off-thread parse product handed to the apply callback.</typeparam>
public sealed class FullReparseScheduler<TParse> : IDisposable
{
    /// <summary>Default debounce after the last scheduling edit before the off-thread reparse runs.</summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(400);

    private readonly Func<ReparseRequest> _capture;
    private readonly Func<string, TParse> _parse;
    private readonly Func<TParse, long, bool> _apply;
    private readonly Action<Action> _post;
    private readonly ITimer _debounce;
    private readonly object _gate = new();

    private ReparseRequest? _pending;
    private Task _current = Task.CompletedTask;
    private int _appliedCount;
    private int _rejectedCount;
    private bool _disposed;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="capture">UI-thread snapshot of the document text + epoch; called on every <see cref="Schedule"/>.</param>
    /// <param name="parse">The pure, off-thread parse of the captured text.</param>
    /// <param name="apply">
    /// UI-thread reconcile of the parse product; must return <see langword="true"/> when it applied and
    /// <see langword="false"/> when it rejected the result (stale epoch). The scheduler counts both.
    /// </param>
    /// <param name="timeProvider">The debounce clock; defaults to <see cref="TimeProvider.System"/>. Pass the app's provider for deterministic tests.</param>
    /// <param name="debounceInterval">The quiet period before the reparse runs; defaults to <see cref="DefaultDebounceInterval"/>.</param>
    /// <param name="post">
    /// Marshals the apply back to the UI thread; defaults to synchronous inline invocation (document-core
    /// tests). Hosts pass <c>UIDispatcher.InvokeAsync</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/>, <paramref name="parse"/>, or <paramref name="apply"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="debounceInterval"/> ≤ 0.</exception>
    public FullReparseScheduler(
        Func<ReparseRequest> capture,
        Func<string, TParse> parse,
        Func<TParse, long, bool> apply,
        TimeProvider? timeProvider = null,
        TimeSpan? debounceInterval = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(apply);

        if (debounceInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceInterval), interval, "Debounce interval must be positive.");

        _capture = capture;
        _parse = parse;
        _apply = apply;
        _post = post ?? (action => action());
        DebounceInterval = debounceInterval ?? DefaultDebounceInterval;

        var provider = timeProvider ?? TimeProvider.System;
        _debounce = provider.CreateTimer(OnElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The quiet period before a scheduled reparse runs.</summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>Number of reparses whose result was applied (epoch still current). Test/status seam.</summary>
    public int AppliedCount
    {
        get { lock (_gate) return _appliedCount; }
    }

    /// <summary>Number of reparses whose result was rejected as stale (a newer edit had landed). Test/status seam.</summary>
    public int RejectedCount
    {
        get { lock (_gate) return _rejectedCount; }
    }

    /// <summary>
    /// The in-flight reparse work: completes when the current parse and its apply post-back have run;
    /// <see cref="Task.CompletedTask"/> when idle. Tests await this after advancing the clock instead of
    /// sleeping; it never faults. Note it does not cover a debounce that has not elapsed yet — advance
    /// the clock first.
    /// </summary>
    public Task PendingReparse
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Requests a full reparse: captures the current snapshot on the UI thread and (re)arms the
    /// debounce. Repeated calls coalesce — only the latest snapshot is parsed when the debounce elapses.
    /// No-op after disposal.
    /// </summary>
    public void Schedule()
    {
        var request = _capture();
        lock (_gate)
        {
            if (_disposed)
                return;

            _pending = request;
            _debounce.Change(DebounceInterval, Timeout.InfiniteTimeSpan); // restart-on-schedule
        }
    }

    /// <summary>Disarms the debounce and stops accepting new work. Work already handed to <see cref="Task.Run(System.Action)"/> completes (await <see cref="PendingReparse"/>). Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pending = null;
        }

        _debounce.Dispose();
    }

    private void OnElapsed(object? state)
    {
        ReparseRequest request;
        lock (_gate)
        {
            if (_disposed || _pending is not { } pending)
                return;

            _pending = null;
            request = pending;
            _current = Task.Run(() => RunParse(request));
        }
    }

    private void RunParse(ReparseRequest request)
    {
        TParse parsed = _parse(request.Text); // off the UI thread — pure

        _post(() =>
        {
            bool applied;
            lock (_gate)
            {
                if (_disposed)
                    return;
            }

            // The apply callback re-checks the epoch against the live buffer and returns whether it
            // applied — the Decision 13 rejection point. Run outside the gate (it reconciles the model).
            applied = _apply(parsed, request.Epoch);

            lock (_gate)
            {
                if (applied)
                    _appliedCount++;
                else
                    _rejectedCount++;
            }
        });
    }
}
