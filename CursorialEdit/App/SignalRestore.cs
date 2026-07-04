using System.Buffers;
using System.Runtime.InteropServices;

namespace CursorialEdit.App;

/// <summary>
/// The FB-4 workaround's registration path: hooks <see cref="PosixSignalRegistration"/> for
/// SIGINT / SIGTERM / SIGHUP / SIGQUIT — the framework's own signal set — and synchronously
/// writes <see cref="EmergencyRestore"/>'s bytes to stdout <b>before</b> the framework's own
/// session teardown runs, so a signal-killed app leaves the shell on the main screen with a
/// visible cursor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Interplay with the framework's handlers (ordering is load-bearing).</b> The happy-path
/// <c>TerminalSession.OpenAsync()</c> — which <c>UIApplication.RunAsync</c> uses when no BYO
/// session is supplied — registers its own SIGINT/SIGTERM/SIGHUP/SIGQUIT handlers in the
/// <c>TerminalSession</c> constructor (<c>Cursorial.Core/Terminal/TerminalSession.cs</c>,
/// <c>RegisterSafetyHandlers</c>). That handler cancels the default signal action, synchronously
/// writes the negotiator's tracked opt-in disables, restores termios, then calls
/// <c>Environment.Exit(128 + signo)</c> — <b>it never returns</b>. The .NET runtime invokes
/// multiple registrations for one signal sequentially on a single thread-pool thread in
/// <b>reverse registration order</b> (most recently registered first — verified empirically on
/// .NET 10). Two consequences:
/// </para>
/// <list type="number">
/// <item><description>
/// A handler registered <i>before</i> the session opens would be ordered <i>after</i> the
/// framework's and would never run (the process exits inside the framework's handler).
/// <see cref="Register"/> must therefore be called <b>after</b> the session is open — the app
/// hooks <c>UIApplication.Started</c>, which fires on the UI thread after startup (session
/// open + alt-screen entry) and before the first frame.
/// </description></item>
/// <item><description>
/// Registered that way, the on-signal write order is: <b>ours</b> (leave alt screen, show
/// cursor, SGR reset) → <b>framework's</b> (negotiator opt-in disables, termios restore, exit).
/// That replicates the framework's clean-teardown order — the frame loop also leaves the alt
/// screen before session disposal writes the disables — so the workaround cannot corrupt the
/// framework's restore: both writes happen on the same thread (no interleaving or torn escape
/// sequences), leaving the alt screen first means the negotiator's Kitty-keyboard pop lands on
/// the main screen's flag stack (where negotiation pushed it; the alt screen's extra push is
/// discarded with the alt screen itself), and the remaining disables are screen-independent
/// global modes.
/// </description></item>
/// </list>
/// <para>
/// The handler never touches <c>PosixSignalContext.Cancel</c>: process fate belongs to the
/// framework's handler (which cancels and exits itself) or, if no session is live, to the
/// default signal disposition. Restore bytes are written at most once per registration (a
/// SIGTERM/SIGHUP pair must not emit twice), only when stdout is a real TTY, and via a direct
/// <c>write(2)</c> on fd 1 with EINTR retry — mirroring the framework's own
/// <c>IStdioTransports.WriteBytesSync</c> signal-path idiom (no async pipeline to hang on).
/// </para>
/// <para>
/// <b>Windows:</b> only SIGINT maps to a console event and the alt-screen strand is a POSIX
/// signal concern, so <see cref="Register"/> is a no-op off POSIX platforms.
/// </para>
/// </remarks>
public static class SignalRestore
{
    /// <summary>
    /// Registers the SIGINT/SIGTERM/SIGHUP/SIGQUIT restore handlers. Call <b>after</b> the
    /// framework session is open (from <c>UIApplication.Started</c>) so the handlers run before
    /// the framework's — see the class remarks for why the order works out that way. Dispose the
    /// returned handle after <c>RunAsync</c> completes to unregister.
    /// </summary>
    /// <returns>A handle that unregisters the handlers when disposed.</returns>
    public static IDisposable Register()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            return EmptyRegistration.Instance;

        var buffer = new ArrayBufferWriter<byte>(32);
        EmergencyRestore.WriteRestoreBytes(buffer);

        // Everything the handler needs is captured up front — the signal path must not
        // allocate, consult Console for the first time, or otherwise do interceptable work.
        return new Registration(buffer.WrittenSpan.ToArray(), stdoutIsTty: !Console.IsOutputRedirected);
    }

    private sealed class EmptyRegistration : IDisposable
    {
        public static readonly EmptyRegistration Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly byte[] _bytes;
        private readonly bool _stdoutIsTty;
        private readonly List<PosixSignalRegistration> _registrations = new(4);
        private int _restored;

        public Registration(byte[] bytes, bool stdoutIsTty)
        {
            _bytes = bytes;
            _stdoutIsTty = stdoutIsTty;

            TryRegister(PosixSignal.SIGINT);
            TryRegister(PosixSignal.SIGTERM);
            TryRegister(PosixSignal.SIGHUP);
            TryRegister(PosixSignal.SIGQUIT); // the framework registers it too (Environment.Exit(131)) — our bytes must be written first, same ordering rationale
        }

        private void TryRegister(PosixSignal signal)
        {
            // Per-signal, matching the framework's own defensiveness: an unsupported or
            // sandbox-blocked signal must not break the others (or app startup).
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, Handle));
            }
            catch
            {
                // best-effort — the workaround degrades, the app still runs
            }
        }

        private void Handle(PosixSignalContext context)
        {
            // Deliberately do NOT set context.Cancel: the framework's handler (invoked right
            // after this one) cancels and exits; with no live session the default disposition
            // terminates the process — both are the desired outcomes.
            if (Interlocked.Exchange(ref _restored, 1) != 0)
                return; // at most once (e.g. SIGTERM followed by SIGHUP)

            if (!_stdoutIsTty)
                return; // never write escape bytes into a redirected stream

            WriteToStdout(_bytes);
        }

        /// <summary>Synchronous best-effort <c>write(2)</c> loop on fd 1 (short writes and EINTR retried).</summary>
        private static void WriteToStdout(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                nint written = Write(fd: 1, ref MemoryMarshal.GetReference(bytes), (nuint) bytes.Length);
                if (written < 0)
                {
                    if (Marshal.GetLastWin32Error() == 4 /* EINTR */)
                        continue;
                    return; // best-effort — unwriteable fd
                }

                if (written == 0)
                    return;

                bytes = bytes[(int) written..];
            }
        }

        // Classic DllImport rather than LibraryImport: the source-generated marshaller demands
        // AllowUnsafeBlocks, which this project doesn't (and needn't) enable — `ref byte` of a
        // blittable buffer marshals identically either way.
        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static extern nint Write(int fd, ref byte buf, nuint count);

        public void Dispose()
        {
            foreach (var registration in _registrations)
            {
                try
                {
                    registration.Dispose();
                }
                catch
                {
                    // best-effort
                }
            }

            _registrations.Clear();
        }
    }
}
