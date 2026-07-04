using CursorialEdit.Document.Buffer;

namespace CursorialEdit.Document.Persistence;

/// <summary>
/// An immutable point-in-time copy of a document for journaling (architecture §12 threading
/// note): a line sequence — cheap to copy, because <see cref="Line"/> is a small struct over
/// immutable strings — stamped with the buffer's <see cref="IDocumentBuffer.Epoch"/> and a
/// capture timestamp.
/// </summary>
/// <remarks>
/// <b>Threading.</b> <see cref="Capture"/> must run on the UI thread (it reads the UI-thread-only
/// buffer); everything else on the instance is immutable and safe from any thread, which is the
/// point — the expensive serialization (<see cref="BuildText"/>) runs off-thread on the copy.
/// The list handed to <see cref="ForLines"/> must never mutate afterwards (the service's
/// copy-on-write mirror guarantees that by cloning before its next mutation).
/// </remarks>
public sealed class AutosaveSnapshot
{
    private readonly IReadOnlyList<Line> _lines;

    private AutosaveSnapshot(IReadOnlyList<Line> lines, long epoch, DateTimeOffset timestamp)
    {
        _lines = lines;
        Epoch = epoch;
        Timestamp = timestamp;
    }

    /// <summary>The buffer's <see cref="IDocumentBuffer.Epoch"/> at capture — the staleness stamp (Decision 13).</summary>
    public long Epoch { get; }

    /// <summary>When the snapshot was captured, from the service's <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Number of lines in the snapshot.</summary>
    public int LineCount => _lines.Count;

    /// <summary>
    /// Copies <paramref name="buffer"/>'s current lines. UI thread only — the buffer's contract.
    /// </summary>
    /// <param name="buffer">The buffer to snapshot.</param>
    /// <param name="timestamp">The capture time (pass the app's <see cref="TimeProvider"/> reading, not wall clock).</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    public static AutosaveSnapshot Capture(IDocumentBuffer buffer, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var lines = new Line[buffer.LineCount];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = buffer.GetLine(i);

        return new AutosaveSnapshot(lines, buffer.Epoch, timestamp);
    }

    /// <summary>
    /// Wraps an already-copied line sequence without another copy — the O(1) promotion path for
    /// <see cref="AutosaveService"/>'s copy-on-write mirror. <paramref name="lines"/> must be
    /// effectively immutable from this moment on (see the class remarks).
    /// </summary>
    internal static AutosaveSnapshot ForLines(IReadOnlyList<Line> lines, long epoch, DateTimeOffset timestamp)
        => new(lines, epoch, timestamp);

    /// <summary>
    /// Serializes the snapshot to document text — each line followed by its own terminator, so
    /// mixed CRLF/LF endings survive byte-exact (delegates to <see cref="Line.Serialize"/>, the
    /// same reassembly rule as <see cref="IDocumentBuffer.GetText()"/>). Safe from any thread.
    /// </summary>
    public string BuildText() => Line.Serialize(_lines);
}
