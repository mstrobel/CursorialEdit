using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI.Testing;

using CursorialEdit.App;
using CursorialEdit.Diagnostics;

namespace CursorialEdit.Tests.Diagnostics;

/// <summary>
/// The operation-journal + replay diagnostic (capture a live session, reproduce a bug
/// deterministically). The load-bearing test is the <b>round-trip</b>: a scripted session is
/// journaled through one <see cref="UITestHost"/>, then the produced journal is replayed through a
/// fresh host and the final buffer/document must match — deterministic reproduction is the whole
/// point. The rest pin the serialization, the header capture, malformed-journal tolerance, and the
/// off-path being inert.
/// </summary>
public sealed class JournalTests
{
    private static UITestHostOptions HostOptions => new()
    {
        InitialSize = new Size(50, 12),
        Capabilities = TestCapabilities.KittyTruecolor,
    };

    // ───────────────────────────── the round-trip (the core value) ─────────────────────────────

    [Fact]
    public void RoundTrip_ReplayReproducesFinalDocumentAndCaret()
    {
        const string initialContent = "abc";

        // ── SESSION A: capture a scripted session ──
        string journalText;
        string finalTextA;
        (int Column, int Row) caretA;

        using (var hostA = UITestHost.Create(HostOptions))
        {
            var shellA = new EditorShell();
            hostA.ShowRoot(shellA);
            Assert.True(hostA.RunUntilIdle());
            shellA.WireDocument(initialContent, hostA.Time);
            Assert.True(hostA.RunUntilIdle());

            var writer = new StringWriter();
            using (var journal = new SessionJournal(writer))
            {
                journal.WriteHeader(JournalHeaderLine.Create(
                    hostA.Application.Capabilities, 50, 12, path: null, initialContent, DateTimeOffset.UnixEpoch));
                journal.AttachTo(hostA.Application.InputDispatcher);

                // A scripted session: typing, a mouse click (caret placement + capture), typing, undo.
                hostA.SendText("Hello");
                Assert.True(hostA.RunUntilIdle());
                hostA.SendClick(0, 0);
                Assert.True(hostA.RunUntilIdle());
                hostA.SendText("Z");
                Assert.True(hostA.RunUntilIdle());
                hostA.SendKey(Key.Character, KeyModifiers.Control, text: "z"); // undo the "Z"
                Assert.True(hostA.RunUntilIdle());

                Assert.True(journal.EventCount > 0, "the session recorded no events");
            }

            journalText = writer.ToString();
            finalTextA = shellA.Document!.GetText();
            caretA = (hostA.FrameBuffer.CursorColumn, hostA.FrameBuffer.CursorRow);
        }

        // The capture actually edited the document (so the round-trip is not comparing two no-ops).
        Assert.NotEqual(initialContent, finalTextA);
        Assert.Contains("Hello", finalTextA, StringComparison.Ordinal);

        // ── REPLAY: drive the produced journal through a fresh host ──
        var read = JournalReader.Read(new StringReader(journalText));
        Assert.True(read.HasHeader);
        Assert.False(read.Truncated);
        Assert.NotEmpty(read.Events);

        string finalTextB;
        (int Column, int Row) caretB;

        using (var hostB = UITestHost.Create(HostOptions))
        {
            var shellB = new EditorShell();
            hostB.ShowRoot(shellB);
            Assert.True(hostB.RunUntilIdle());
            shellB.WireDocument(read.Header!.Document.Content, hostB.Time); // reconstruct the recorded start
            Assert.True(hostB.RunUntilIdle());

            foreach (var inputEvent in read.Events)
            {
                ReplayDriver.Inject(hostB.Application, inputEvent);
                Assert.True(hostB.RunUntilIdle());
            }

            finalTextB = shellB.Document!.GetText();
            caretB = (hostB.FrameBuffer.CursorColumn, hostB.FrameBuffer.CursorRow);
        }

        // Deterministic reproduction: same final document, same final caret.
        Assert.Equal(finalTextA, finalTextB);
        Assert.Equal(caretA, caretB);
    }

    // ───────────────────────────── serialization round-trips ─────────────────────────────

    [Fact]
    public void Serialization_RoundTripsKeyEvent()
    {
        var key = new KeyEvent
        {
            Key = Key.Character,
            Modifiers = KeyModifiers.Control | KeyModifiers.Shift,
            Kind = KeyEventKind.Down,
            Text = "s".AsMemory(),
            IsRepeat = true,
            RepeatCount = 3,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(5),
        };

        var round = Assert.IsType<KeyEvent>(RoundTrip(key));
        Assert.Equal(key.Key, round.Key);
        Assert.Equal(key.Modifiers, round.Modifiers);
        Assert.Equal(key.Kind, round.Kind);
        Assert.Equal("s", round.Text.ToString());
        Assert.True(round.IsRepeat);
        Assert.Equal(3, round.RepeatCount);
        Assert.Equal(key.Timestamp, round.Timestamp);
    }

    [Fact]
    public void Serialization_RoundTripsKeyUpWithNoText()
    {
        var key = new KeyEvent
        {
            Key = Key.LeftArrow,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Up,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

        var round = Assert.IsType<KeyEvent>(RoundTrip(key));
        Assert.Equal(Key.LeftArrow, round.Key);
        Assert.Equal(KeyEventKind.Up, round.Kind);
        Assert.True(round.Text.IsEmpty);
    }

    [Fact]
    public void Serialization_RoundTripsMouseButtonEvent()
    {
        var mouse = new MouseEvent
        {
            Kind = MouseEventKind.ButtonDown,
            Position = new CellPosition(7, 3),
            Button = MouseButton.Left,
            ButtonsHeld = MouseButtons.Left,
            Modifiers = KeyModifiers.Alt,
            ClickCount = 2,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1),
        };

        var round = Assert.IsType<MouseEvent>(RoundTrip(mouse));
        Assert.Equal(MouseEventKind.ButtonDown, round.Kind);
        Assert.Equal(new CellPosition(7, 3), round.Position);
        Assert.Equal(MouseButton.Left, round.Button);
        Assert.Equal(MouseButtons.Left, round.ButtonsHeld);
        Assert.Equal(KeyModifiers.Alt, round.Modifiers);
        Assert.Equal(2, round.ClickCount);
        Assert.Equal(mouse.Timestamp, round.Timestamp);
    }

    [Fact]
    public void Serialization_RoundTripsMouseWheelEvent()
    {
        var wheel = new MouseEvent
        {
            Kind = MouseEventKind.Wheel,
            Position = new CellPosition(1, 1),
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            WheelDeltaY = -120,
            WheelDeltaX = 240,
            Timestamp = DateTimeOffset.UnixEpoch,
        };

        var round = Assert.IsType<MouseEvent>(RoundTrip(wheel));
        Assert.Equal(MouseEventKind.Wheel, round.Kind);
        Assert.Equal(-120, round.WheelDeltaY);
        Assert.Equal(240, round.WheelDeltaX);
    }

    [Fact]
    public void Serialization_RoundTripsPasteEvent()
    {
        var paste = new PasteEvent
        {
            Text = "pasted\nlines".AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(2),
        };

        var round = Assert.IsType<PasteEvent>(RoundTrip(paste));
        Assert.Equal("pasted\nlines", round.Text.ToString());
        Assert.Equal(paste.Timestamp, round.Timestamp);
    }

    [Fact]
    public void Serialization_RoundTripsResizeEvent()
    {
        var resize = new ResizeEvent
        {
            Columns = 132,
            Rows = 43,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(3),
        };

        var round = Assert.IsType<ResizeEvent>(RoundTrip(resize));
        Assert.Equal(132, round.Columns);
        Assert.Equal(43, round.Rows);
        Assert.Equal(resize.Timestamp, round.Timestamp);
    }

    [Fact]
    public void Serialization_EmitsGreppableKindDiscriminator()
    {
        var line = JournalLine.FromInputEvent(new KeyEvent
        {
            Key = Key.Character,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = "x".AsMemory(),
            Timestamp = DateTimeOffset.UnixEpoch,
        })!;

        var json = System.Text.Json.JsonSerializer.Serialize(line, JournalSchema.SerializerOptions);

        // Human-greppable: a stable "kind" discriminator and named enums (never numbers).
        Assert.Contains("\"kind\":\"key\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Key\":\"Character\"", json, StringComparison.Ordinal);
    }

    // ───────────────────────────── header capture ─────────────────────────────

    [Fact]
    public void Header_CapturesCapabilitiesSizeAndDocument()
    {
        var caps = TestCapabilities.KittyTruecolor;
        const string content = "# Title\n\nbody\n";

        var header = JournalHeaderLine.Create(caps, columns: 100, rows: 30, path: "/tmp/doc.md", content, DateTimeOffset.UnixEpoch);

        var reader = new StringReader(SerializeLines(header));
        var read = JournalReader.Read(reader);

        Assert.True(read.HasHeader);
        var round = read.Header!;

        // Capabilities: at least the family/identity + color depth.
        Assert.Equal(caps.Terminal.Family, round.Terminal.Family);
        Assert.Equal(caps.Output.Color.Depth, round.Terminal.ColorDepth);

        // Size.
        Assert.Equal(100, round.Size.Columns);
        Assert.Equal(30, round.Size.Rows);

        // Document: path + exact content + a content hash, enough to reconstruct the start.
        Assert.Equal("/tmp/doc.md", round.Document.Path);
        Assert.Equal(content, round.Document.Content);
        Assert.False(string.IsNullOrEmpty(round.Document.Sha256));
        Assert.Equal(JournalSchema.Version, round.Schema);
    }

    [Fact]
    public void Header_CapturesColorDepthDowngrade()
    {
        // A NoColor wire records its depth honestly (the diagnostic must reflect the real session).
        var caps = TestCapabilities.KittyTruecolor with
        {
            Output = TestCapabilities.KittyTruecolor.Output with { Color = ColorCapabilities.None },
        };

        var header = JournalHeaderLine.Create(caps, 80, 24, null, string.Empty, DateTimeOffset.UnixEpoch);
        Assert.Equal(ColorDepth.NoColor, header.Terminal.ColorDepth);
    }

    // ───────────────────────────── malformed / truncated ─────────────────────────────

    [Fact]
    public void MalformedJournal_TruncatedTail_ReplaysValidPrefixAndReports()
    {
        var header = JournalHeaderLine.Create(TestCapabilities.KittyTruecolor, 50, 12, null, string.Empty, DateTimeOffset.UnixEpoch);
        var good = JournalLine.FromInputEvent(new KeyEvent
        {
            Key = Key.Character, Modifiers = KeyModifiers.None, Kind = KeyEventKind.Down,
            Text = "a".AsMemory(), Timestamp = DateTimeOffset.UnixEpoch,
        })!;

        // A valid header + one valid event + a truncated (half-written) final line.
        var text = SerializeLine(header) + "\n" + SerializeLine(good) + "\n" + "{\"kind\":\"key\",\"Ts\":\"19";

        var read = JournalReader.Read(new StringReader(text));

        Assert.True(read.HasHeader);
        Assert.True(read.Truncated);
        Assert.Single(read.Events); // the valid prefix replays
        Assert.NotNull(read.StoppedReason);
        Assert.Equal(3, read.LineNumber); // stopped on the third line
    }

    [Fact]
    public void MalformedJournal_MissingHeader_ReportsNoHeader()
    {
        var evt = JournalLine.FromInputEvent(new KeyEvent
        {
            Key = Key.Character, Modifiers = KeyModifiers.None, Kind = KeyEventKind.Down,
            Text = "a".AsMemory(), Timestamp = DateTimeOffset.UnixEpoch,
        })!;

        var read = JournalReader.Read(new StringReader(SerializeLine(evt)));

        Assert.False(read.HasHeader);
        Assert.True(read.Truncated);
        Assert.NotNull(read.StoppedReason);
    }

    [Fact]
    public void MalformedJournal_ToleratesBlankLines()
    {
        var header = JournalHeaderLine.Create(TestCapabilities.KittyTruecolor, 50, 12, null, string.Empty, DateTimeOffset.UnixEpoch);
        var evt = JournalLine.FromInputEvent(new KeyEvent
        {
            Key = Key.Character, Modifiers = KeyModifiers.None, Kind = KeyEventKind.Down,
            Text = "a".AsMemory(), Timestamp = DateTimeOffset.UnixEpoch,
        })!;

        var text = SerializeLine(header) + "\n\n" + SerializeLine(evt) + "\n\n";
        var read = JournalReader.Read(new StringReader(text));

        Assert.True(read.HasHeader);
        Assert.False(read.Truncated);
        Assert.Single(read.Events);
    }

    // ───────────────────────────── off = inert ─────────────────────────────

    [Fact]
    public void JournalingOff_NotAttached_RecordsNothingEvenWhileEventsFlow()
    {
        using var host = UITestHost.Create(HostOptions);
        var shell = new EditorShell();
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());
        shell.WireDocument(string.Empty, host.Time);
        Assert.True(host.RunUntilIdle());

        // A journal that was constructed but NEVER attached is inert: nothing subscribes to the
        // dispatcher, so events flow with zero journal overhead.
        var writer = new StringWriter();
        using var journal = new SessionJournal(writer);
        Assert.False(journal.IsAttached);

        host.SendText("typing with no journal attached");
        Assert.True(host.RunUntilIdle());

        Assert.Equal(0, journal.EventCount);
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void Dispose_Unsubscribes_SoNoEventsAreRecordedAfterwards()
    {
        using var host = UITestHost.Create(HostOptions);
        var shell = new EditorShell();
        host.ShowRoot(shell);
        Assert.True(host.RunUntilIdle());
        shell.WireDocument(string.Empty, host.Time);
        Assert.True(host.RunUntilIdle());

        var writer = new StringWriter();
        var journal = new SessionJournal(writer);
        journal.WriteHeader(JournalHeaderLine.Create(host.Application.Capabilities, 50, 12, null, string.Empty, DateTimeOffset.UnixEpoch));
        journal.AttachTo(host.Application.InputDispatcher);

        host.SendText("a");
        Assert.True(host.RunUntilIdle());
        var recordedWhileAttached = journal.EventCount;
        Assert.True(recordedWhileAttached > 0);

        journal.Dispose(); // unsubscribes

        host.SendText("bcd");
        Assert.True(host.RunUntilIdle());

        Assert.Equal(recordedWhileAttached, journal.EventCount); // no growth after dispose
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static InputEvent RoundTrip(InputEvent inputEvent)
    {
        var line = JournalLine.FromInputEvent(inputEvent)!;
        var json = System.Text.Json.JsonSerializer.Serialize(line, JournalSchema.SerializerOptions);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<JournalLine>(json, JournalSchema.SerializerOptions)!;
        return parsed.ToInputEvent()!;
    }

    private static string SerializeLine(JournalLine line)
        => System.Text.Json.JsonSerializer.Serialize(line, JournalSchema.SerializerOptions);

    private static string SerializeLines(params JournalLine[] lines)
        => string.Join('\n', lines.Select(SerializeLine));
}
