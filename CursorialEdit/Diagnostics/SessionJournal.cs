using System.Text.Json;

using Cursorial.Input.Events;
using Cursorial.UI.Input;

namespace CursorialEdit.Diagnostics;

/// <summary>
/// The capture side of the operation journal (the diagnostic tool that lets the maintainer record a
/// live session and reproduce a bug deterministically). It writes the JSONL <see cref="JournalLine"/>
/// stream: a <see cref="JournalHeaderLine"/> first, then one line per recorded input event, flushed
/// per line so a crash mid-session still leaves a usable prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capture point.</b> <see cref="AttachTo"/> subscribes to
/// <see cref="InputDispatcher.PreProcessInput"/> — the single comprehensive hook that fires for
/// every input event before the routed dispatch, so no per-handler instrumentation is needed. Only
/// events that carry a reconstructable device record are recorded: <see cref="KeyEventArgs"/> and
/// <see cref="MouseEventArgs"/> (both expose their immutable <c>Device</c>), plus a paste
/// (<see cref="TextInputEventArgs"/> with <see cref="TextInputEventArgs.FromPaste"/>). A
/// <b>non-paste</b> <see cref="TextInputEventArgs"/> is deliberately skipped: it is the text-input
/// the framework synthesizes from a printable key that was already recorded, and replay re-synthesizes
/// it automatically — recording it too would double-apply the character.
/// </para>
/// <para>
/// <b>Off = inert.</b> Nothing subscribes until <see cref="AttachTo"/> is called, so when the
/// <c>--journal</c> flag is absent the journal is never constructed and the input path carries zero
/// overhead. <see cref="Dispose"/> unsubscribes, so recording stops cleanly.
/// </para>
/// <para><b>Threading.</b> The capture handler runs on the UI thread (inside dispatch); this type is not otherwise thread-safe.</para>
/// </remarks>
public sealed class SessionJournal : IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private InputDispatcher? _dispatcher;
    private DateTimeOffset _lastTimestamp;
    private int _eventCount;
    private bool _disposed;

    /// <summary>
    /// Creates a journal writing to <paramref name="writer"/>. When <paramref name="ownsWriter"/> is
    /// set, <see cref="Dispose"/> also disposes the writer (the file-backed factory sets it).
    /// </summary>
    public SessionJournal(TextWriter writer, bool ownsWriter = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _ownsWriter = ownsWriter;
    }

    /// <summary>
    /// Opens (creating the directory as needed) a UTF-8 journal file at <paramref name="path"/>,
    /// truncating any existing file. The returned journal owns the underlying stream.
    /// </summary>
    public static SessionJournal CreateForFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // FileShare.Read so a `tail -f` can follow the journal while the session runs. UTF-8 with no
        // BOM keeps the first line clean for grep.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        return new SessionJournal(writer, ownsWriter: true);
    }

    /// <summary>The number of input-event lines written so far (the header is not counted).</summary>
    public int EventCount => _eventCount;

    /// <summary>Whether the journal is currently subscribed to a dispatcher (i.e. actively recording).</summary>
    public bool IsAttached => _dispatcher is not null;

    /// <summary>Writes the session header — the first line. Call once, before <see cref="AttachTo"/>.</summary>
    public void WriteHeader(JournalHeaderLine header)
    {
        ArgumentNullException.ThrowIfNull(header);
        _lastTimestamp = header.CreatedUtc;
        WriteLine(header);
    }

    /// <summary>
    /// Begins recording by subscribing to <paramref name="dispatcher"/>'s
    /// <see cref="InputDispatcher.PreProcessInput"/>. Idempotent-safe against a double attach (throws
    /// if already attached elsewhere to surface the mistake).
    /// </summary>
    public void AttachTo(InputDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_dispatcher is not null)
            throw new InvalidOperationException("This SessionJournal is already attached to a dispatcher.");

        _dispatcher = dispatcher;
        dispatcher.PreProcessInput += OnPreProcessInput;
    }

    private void OnPreProcessInput(object? sender, InputEventArgs e)
    {
        switch (e)
        {
            // KeyEventArgs and every MouseEventArgs subtype expose the immutable device record —
            // reconstruct the line straight from it. (MouseButtonEventArgs / MouseWheelEventArgs
            // derive from MouseEventArgs, so this one arm covers all mouse events.)
            case KeyEventArgs key:
                _lastTimestamp = key.Device.Timestamp;
                Record(key.Device);
                break;

            case MouseEventArgs mouse:
                _lastTimestamp = mouse.Device.Timestamp;
                Record(mouse.Device);
                break;

            // A paste has no device record on the args (it is delivered as text input); reconstruct a
            // PasteEvent from the text. It carries no timestamp of its own, so stamp it with the last
            // event's time — replay pacing treats timestamps loosely.
            case TextInputEventArgs { FromPaste: true } paste:
                WriteEvent(new PasteEventLine { Ts = _lastTimestamp, Text = paste.Text.ToString() });
                break;

            // A non-paste TextInputEventArgs is synthesized from an already-recorded key — skip it
            // (replay re-synthesizes it from that key). All other args carry no reconstructable event.
        }
    }

    private void Record(InputEvent inputEvent)
    {
        if (JournalLine.FromInputEvent(inputEvent) is { } line)
            WriteEvent(line);
    }

    private void WriteEvent(JournalLine line)
    {
        WriteLine(line);
        _eventCount++;
    }

    private void WriteLine(JournalLine line)
    {
        _writer.WriteLine(JsonSerializer.Serialize(line, JournalSchema.SerializerOptions));
        _writer.Flush(); // per-entry flush: a crash mid-session must leave a usable journal
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_dispatcher is { } dispatcher)
        {
            dispatcher.PreProcessInput -= OnPreProcessInput;
            _dispatcher = null;
        }

        _writer.Flush();
        if (_ownsWriter)
            _writer.Dispose();
    }
}
