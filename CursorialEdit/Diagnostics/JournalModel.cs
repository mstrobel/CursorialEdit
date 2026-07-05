using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Terminal;

namespace CursorialEdit.Diagnostics;

/// <summary>
/// The wire format for the operation journal (the diagnostic capture/replay feature). Every line of
/// a journal file is one self-describing JSON object carrying a <c>"kind"</c> discriminator — the
/// first line is a <see cref="JournalHeaderLine"/> (schema, capabilities, size, and the initial
/// document), and every subsequent line is one recorded input event. The format is deliberately
/// JSONL: human-greppable (<c>grep '"kind":"key"'</c>) yet machine-replayable, and append-only so a
/// crash mid-session still leaves a usable prefix.
/// </summary>
/// <remarks>
/// The one-of hierarchy uses <see cref="System.Text.Json"/> polymorphism with a custom
/// <c>"kind"</c> discriminator, which <see cref="System.Text.Json"/> always emits as the first
/// property — so the discriminator is greppable at a fixed position and deserialization can dispatch
/// on it directly. Enums (including the <c>[Flags]</c> modifier masks) serialize as their names via
/// <see cref="JsonStringEnumConverter"/>, so a line reads like the event it represents.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JournalHeaderLine), "header")]
[JsonDerivedType(typeof(KeyEventLine), "key")]
[JsonDerivedType(typeof(MouseEventLine), "mouse")]
[JsonDerivedType(typeof(PasteEventLine), "paste")]
[JsonDerivedType(typeof(ResizeEventLine), "resize")]
public abstract record JournalLine
{
    /// <summary>
    /// Reconstructs the framework <see cref="InputEvent"/> an event line represents, or
    /// <see langword="null"/> for the non-event <see cref="JournalHeaderLine"/>. Replay feeds the
    /// result back through the real input system (see <see cref="ReplayDriver"/>).
    /// </summary>
    public virtual InputEvent? ToInputEvent() => null;

    /// <summary>
    /// Projects a captured <see cref="InputEvent"/> onto its journal line, or <see langword="null"/>
    /// for event kinds the journal does not model. <paramref name="timestampOverride"/> supplies a
    /// timestamp when the source (a synthesized paste, whose args carry none) has none of its own.
    /// </summary>
    public static JournalLine? FromInputEvent(InputEvent inputEvent, DateTimeOffset? timestampOverride = null)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);

        var timestamp = timestampOverride ?? inputEvent.Timestamp;

        return inputEvent switch
        {
            KeyEvent key => new KeyEventLine
            {
                Ts = timestamp,
                Key = key.Key,
                Mods = key.Modifiers,
                Kind = key.Kind,
                Text = key.Text.IsEmpty ? null : key.Text.ToString(),
                IsRepeat = key.IsRepeat,
                RepeatCount = key.RepeatCount,
            },
            MouseEvent mouse => new MouseEventLine
            {
                Ts = timestamp,
                Kind = mouse.Kind,
                Column = mouse.Position.Column,
                Row = mouse.Position.Row,
                Button = mouse.Button,
                ButtonsHeld = mouse.ButtonsHeld,
                Mods = mouse.Modifiers,
                ClickCount = mouse.ClickCount,
                WheelDeltaX = mouse.WheelDeltaX,
                WheelDeltaY = mouse.WheelDeltaY,
            },
            PasteEvent paste => new PasteEventLine { Ts = timestamp, Text = paste.Text.ToString() },
            ResizeEvent resize => new ResizeEventLine { Ts = timestamp, Columns = resize.Columns, Rows = resize.Rows },
            _ => null,
        };
    }
}

/// <summary>
/// The first line of every journal — the session context replay needs to reconstruct the starting
/// state: the schema version, the wall-clock capture time, the negotiated terminal capabilities
/// (family/name + color depth), the terminal size in cells, and the initial document (path +
/// content + a content hash).
/// </summary>
public sealed record JournalHeaderLine : JournalLine
{
    /// <summary>The schema version of this journal (see <see cref="JournalSchema.Version"/>).</summary>
    public required int Schema { get; init; }

    /// <summary>Wall-clock time the session was captured (for the human reading the file; not used for replay pacing).</summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>The producing application's name (and, when known, version).</summary>
    public required string App { get; init; }

    /// <summary>The negotiated terminal identity + color depth in force during capture.</summary>
    public required TerminalInfo Terminal { get; init; }

    /// <summary>The terminal size, in character cells, at capture start.</summary>
    public required SizeInfo Size { get; init; }

    /// <summary>The initial document — path (if any) plus the exact content replay starts from.</summary>
    public required DocumentInfo Document { get; init; }

    /// <summary>
    /// Builds a header from the live session context: the negotiated <paramref name="capabilities"/>,
    /// the terminal <paramref name="columns"/>×<paramref name="rows"/>, and the initial document
    /// (<paramref name="path"/> + <paramref name="content"/>). <paramref name="createdUtc"/> is the
    /// wall-clock capture time (taken once at session start via the standard app time source, since
    /// it is only a human-facing stamp).
    /// </summary>
    public static JournalHeaderLine Create(
        TerminalCapabilities capabilities,
        int columns,
        int rows,
        string? path,
        string content,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(content);

        var bytes = Encoding.UTF8.GetBytes(content);

        return new JournalHeaderLine
        {
            Schema = JournalSchema.Version,
            CreatedUtc = createdUtc,
            App = JournalSchema.AppName,
            Terminal = new TerminalInfo(
                capabilities.Terminal.Family,
                capabilities.Terminal.Name,
                capabilities.Output.Color.Depth),
            Size = new SizeInfo(columns, rows),
            Document = new DocumentInfo(
                path,
                content,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                bytes.Length),
        };
    }
}

/// <summary>The negotiated terminal identity recorded in the header (enough to know what wire the session ran on).</summary>
public sealed record TerminalInfo(TerminalFamily Family, string? Name, ColorDepth ColorDepth);

/// <summary>The terminal size in character cells.</summary>
public sealed record SizeInfo(int Columns, int Rows);

/// <summary>The initial document replay reconstructs its starting buffer from.</summary>
public sealed record DocumentInfo(string? Path, string Content, string Sha256, int ByteLength);

/// <summary>One recorded keyboard event.</summary>
public sealed record KeyEventLine : JournalLine
{
    /// <summary>Time the event was observed (from the framework's time source).</summary>
    public required DateTimeOffset Ts { get; init; }

    /// <summary>The named key (<see cref="Cursorial.Input.Key.Character"/> means "printable — see <see cref="Text"/>").</summary>
    public required Key Key { get; init; }

    /// <summary>The lock-free modifier mask (matches shortcut patterns).</summary>
    public required KeyModifiers Mods { get; init; }

    /// <summary>Whether this is a key-down or key-up. (Named <c>keyKind</c> on the wire to avoid clashing with the <c>kind</c> discriminator.)</summary>
    [JsonPropertyName("keyKind")]
    public required KeyEventKind Kind { get; init; }

    /// <summary>The composed printable text (<see langword="null"/> for named/control keys).</summary>
    public string? Text { get; init; }

    /// <summary>Auto-repeat marker (only ever true on key-down).</summary>
    public bool IsRepeat { get; init; }

    /// <summary>The repeat ordinal (1 for the initial press).</summary>
    public int RepeatCount { get; init; } = 1;

    /// <inheritdoc/>
    public override InputEvent ToInputEvent() => new KeyEvent
    {
        Key = Key,
        Modifiers = Mods,
        Kind = Kind,
        Text = (Text ?? string.Empty).AsMemory(),
        IsRepeat = IsRepeat,
        RepeatCount = RepeatCount,
        Timestamp = Ts,
    };
}

/// <summary>One recorded mouse event (button, motion, drag, or wheel).</summary>
public sealed record MouseEventLine : JournalLine
{
    /// <summary>Time the event was observed.</summary>
    public required DateTimeOffset Ts { get; init; }

    /// <summary>What kind of mouse activity this is. (Named <c>mouseKind</c> on the wire to avoid clashing with the <c>kind</c> discriminator.)</summary>
    [JsonPropertyName("mouseKind")]
    public required MouseEventKind Kind { get; init; }

    /// <summary>The pointer column in terminal cells.</summary>
    public required int Column { get; init; }

    /// <summary>The pointer row in terminal cells.</summary>
    public required int Row { get; init; }

    /// <summary>The button associated with the event (<see cref="MouseButton.None"/> for motion-only).</summary>
    public required MouseButton Button { get; init; }

    /// <summary>The mask of all buttons held at the time of the event.</summary>
    public required MouseButtons ButtonsHeld { get; init; }

    /// <summary>Modifier keys held during the event.</summary>
    public required KeyModifiers Mods { get; init; }

    /// <summary>Multi-click count (1 for a single click; meaningful only on button events).</summary>
    public int ClickCount { get; init; } = 1;

    /// <summary>Horizontal wheel delta in 1/120-notch units.</summary>
    public int WheelDeltaX { get; init; }

    /// <summary>Vertical wheel delta in 1/120-notch units.</summary>
    public int WheelDeltaY { get; init; }

    /// <inheritdoc/>
    public override InputEvent ToInputEvent() => new MouseEvent
    {
        Kind = Kind,
        Position = new CellPosition(Column, Row),
        Button = Button,
        ButtonsHeld = ButtonsHeld,
        Modifiers = Mods,
        WheelDeltaX = WheelDeltaX,
        WheelDeltaY = WheelDeltaY,
        ClickCount = ClickCount,
        Timestamp = Ts,
    };
}

/// <summary>One recorded bracketed paste (a whole paste delivered as a single event).</summary>
public sealed record PasteEventLine : JournalLine
{
    /// <summary>Time the paste was observed (best-effort — see <see cref="SessionJournal"/>).</summary>
    public required DateTimeOffset Ts { get; init; }

    /// <summary>The pasted text (may contain embedded newlines).</summary>
    public required string Text { get; init; }

    /// <inheritdoc/>
    public override InputEvent ToInputEvent() => new PasteEvent
    {
        Text = (Text ?? string.Empty).AsMemory(),
        Timestamp = Ts,
    };
}

/// <summary>One recorded terminal resize.</summary>
public sealed record ResizeEventLine : JournalLine
{
    /// <summary>Time the resize was observed.</summary>
    public required DateTimeOffset Ts { get; init; }

    /// <summary>The new width in character cells.</summary>
    public required int Columns { get; init; }

    /// <summary>The new height in character cells.</summary>
    public required int Rows { get; init; }

    /// <inheritdoc/>
    public override InputEvent ToInputEvent() => new ResizeEvent
    {
        Columns = Columns,
        Rows = Rows,
        Timestamp = Ts,
    };
}

/// <summary>Schema constants and the shared <see cref="JsonSerializerOptions"/> for journal (de)serialization.</summary>
public static class JournalSchema
{
    /// <summary>The current journal schema version (bump on any breaking wire change).</summary>
    public const int Version = 1;

    /// <summary>The application name stamped into every header.</summary>
    public const string AppName = "CursorialEdit";

    /// <summary>
    /// The single serializer configuration both the writer and reader use: enums (including the
    /// <c>[Flags]</c> masks) as names for greppability, and null reference/optional fields omitted
    /// so a line reads clean.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
