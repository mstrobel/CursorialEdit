using System.Buffers;

using CursorialEdit.App;

namespace CursorialEdit.Tests.App;

/// <summary>
/// M1.WP2 — the FB-4 workaround's byte payload, asserted exactly (implementation-plan §6 gate:
/// "unit test on the emergency-restore byte writer — DECRST 1049, cursor show, SGR reset";
/// wave 2 added the DECSET 7 autowrap re-enable and OSC 112 cursor-color reset — restores the
/// framework's signal path also misses; only its clean teardown emits them).
/// </summary>
public sealed class EmergencyRestoreTests
{
    // DECSET 7 (autowrap on) + OSC 112 (cursor color reset) + DECRST 1049 (leave alt screen)
    // + DECSET 25 (show cursor) + SGR 0 (reset), in that order.
    private static readonly byte[] ExpectedSequence =
        "\u001b[?7h\u001b]112\u001b\\\u001b[?1049l\u001b[?25h\u001b[0m"u8.ToArray();

    [Fact]
    public void WriteRestoreBytes_EmitsAutowrap_CursorColor_LeaveAltScreen_ShowCursor_SgrReset_Exactly()
    {
        var writer = new ArrayBufferWriter<byte>();

        EmergencyRestore.WriteRestoreBytes(writer);

        Assert.Equal(ExpectedSequence, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteRestoreBytes_IsPure_SecondCallAppendsTheSameSequence()
    {
        var writer = new ArrayBufferWriter<byte>();

        EmergencyRestore.WriteRestoreBytes(writer);
        EmergencyRestore.WriteRestoreBytes(writer);

        Assert.Equal(ExpectedSequence.Concat(ExpectedSequence).ToArray(), writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void WriteRestoreBytes_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(static () => EmergencyRestore.WriteRestoreBytes(null!));
    }
}
