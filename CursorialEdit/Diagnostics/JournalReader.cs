using System.Text.Json;

using Cursorial.Input.Events;

namespace CursorialEdit.Diagnostics;

/// <summary>
/// The outcome of parsing a journal: the header, the reconstructed input events, and — when the file
/// was malformed or truncated — where and why parsing stopped. Parsing is deliberately tolerant: it
/// replays every valid line it can and reports the rest, because a journal captured up to the moment
/// of a crash is exactly the interesting case.
/// </summary>
public sealed class JournalReadResult
{
    /// <summary>The parsed header, or <see langword="null"/> when the first line was missing or malformed (nothing is replayable).</summary>
    public JournalHeaderLine? Header { get; init; }

    /// <summary>The reconstructed input events, in file order (the valid prefix when truncated).</summary>
    public required IReadOnlyList<InputEvent> Events { get; init; }

    /// <summary>The raw event lines corresponding to <see cref="Events"/> (test/inspection observability).</summary>
    public required IReadOnlyList<JournalLine> EventLines { get; init; }

    /// <summary>Whether parsing stopped early on a malformed/truncated line (or a missing header).</summary>
    public bool Truncated { get; init; }

    /// <summary>A human-readable reason parsing stopped, or <see langword="null"/> when the whole file parsed.</summary>
    public string? StoppedReason { get; init; }

    /// <summary>The 1-based file line number parsing reached (the last line successfully read, or where it stopped).</summary>
    public int LineNumber { get; init; }

    /// <summary>Whether a header was parsed — i.e. whether anything is replayable at all.</summary>
    public bool HasHeader => Header is not null;
}

/// <summary>
/// Parses a JSONL operation journal (<see cref="JournalLine"/> stream) into a
/// <see cref="JournalReadResult"/>. The first non-blank line must be a <see cref="JournalHeaderLine"/>;
/// each subsequent line is one input event. A line that fails to parse (a truncated final line, or
/// corruption) stops parsing gracefully — everything before it is returned as the replayable prefix.
/// </summary>
public static class JournalReader
{
    /// <summary>Parses a journal file at <paramref name="path"/>.</summary>
    public static JournalReadResult ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var reader = new StreamReader(path);
        return Read(reader);
    }

    /// <summary>Parses a journal from <paramref name="reader"/> (does not dispose it).</summary>
    public static JournalReadResult Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        JournalHeaderLine? header = null;
        var events = new List<InputEvent>();
        var eventLines = new List<JournalLine>();
        var lineNumber = 0;

        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(raw))
                continue; // tolerate blank lines anywhere

            JournalLine? line;
            try
            {
                line = JsonSerializer.Deserialize<JournalLine>(raw, JournalSchema.SerializerOptions);
            }
            catch (JsonException ex)
            {
                // A truncated final line (crash mid-write) or corruption: stop here, keep the prefix.
                return new JournalReadResult
                {
                    Header = header,
                    Events = events,
                    EventLines = eventLines,
                    Truncated = true,
                    StoppedReason = $"line {lineNumber} is not valid JSON ({ex.Message})",
                    LineNumber = lineNumber,
                };
            }

            if (line is null)
            {
                return new JournalReadResult
                {
                    Header = header,
                    Events = events,
                    EventLines = eventLines,
                    Truncated = true,
                    StoppedReason = $"line {lineNumber} deserialized to null",
                    LineNumber = lineNumber,
                };
            }

            if (header is null)
            {
                if (line is JournalHeaderLine parsedHeader)
                {
                    header = parsedHeader;
                    continue;
                }

                // No header where one is required: nothing can be replayed.
                return new JournalReadResult
                {
                    Header = null,
                    Events = events,
                    EventLines = eventLines,
                    Truncated = true,
                    StoppedReason = $"line {lineNumber} is a '{line.GetType().Name}' but the journal must begin with a header",
                    LineNumber = lineNumber,
                };
            }

            if (line is JournalHeaderLine)
            {
                // A second header is unexpected; stop cleanly rather than guess.
                return new JournalReadResult
                {
                    Header = header,
                    Events = events,
                    EventLines = eventLines,
                    Truncated = true,
                    StoppedReason = $"line {lineNumber} is a second header",
                    LineNumber = lineNumber,
                };
            }

            if (line.ToInputEvent() is { } inputEvent)
            {
                events.Add(inputEvent);
                eventLines.Add(line);
            }
        }

        return new JournalReadResult
        {
            Header = header,
            Events = events,
            EventLines = eventLines,
            Truncated = header is null,
            StoppedReason = header is null ? "the journal contained no header" : null,
            LineNumber = lineNumber,
        };
    }
}
