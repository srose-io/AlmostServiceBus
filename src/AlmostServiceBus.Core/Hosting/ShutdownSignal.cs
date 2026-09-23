using System.Runtime.InteropServices;

namespace AlmostServiceBus.Core.Hosting;

/// <summary>
/// Cancels a token when the operating system asks the process to stop.
/// <para>
/// The host needs its own SIGTERM handler because something else already installed one. Each
/// <c>WebApplication</c> the host starts brings a <c>ConsoleLifetime</c>, which registers
/// SIGINT, SIGQUIT and SIGTERM, marks them handled — so the process is no longer terminated —
/// and asks its own host to stop. Nothing awaits that host's shutdown here, so the signal was
/// swallowed and the process ran on: measured at 30 s per stop, because <c>docker stop</c> then
/// waits its whole timeout and SIGKILLs, which reports exit 137 for what was an orderly stop.
/// </para>
/// <para>
/// <see cref="Console.CancelKeyPress"/> does not cover it: that is SIGINT, which is what a
/// terminal sends, not what a supervisor sends. <see cref="AppDomain.ProcessExit"/> does not
/// either: it runs while the process is already leaving, not to make it leave.
/// </para>
/// </summary>
public static class ShutdownSignal
{
    /// <summary>The signals a supervisor or a terminal uses to ask for a stop.</summary>
    private static readonly PosixSignal[] Signals =
        [PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT];

    /// <summary>
    /// Handles the shutdown signals for the lifetime of the returned registration: each one
    /// cancels <paramref name="shutdown"/> and is marked handled, so the process shuts down the
    /// way the host wants to rather than being terminated where it stands.
    /// </summary>
    public static IDisposable Install(CancellationTokenSource shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);

        var registrations = new List<PosixSignalRegistration>();
        foreach (var signal in Signals)
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    // The signal can arrive after the host has already begun shutting down.
                    try { shutdown.Cancel(); } catch (ObjectDisposedException) { }
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // SIGQUIT is not a thing on Windows; the others are.
            }
        }

        return new Registrations(registrations);
    }

    private sealed class Registrations(List<PosixSignalRegistration> registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
                registration.Dispose();
            registrations.Clear();
        }
    }
}
