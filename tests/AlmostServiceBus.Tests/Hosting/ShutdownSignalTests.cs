using System.Diagnostics;
using AlmostServiceBus.Core.Hosting;

namespace AlmostServiceBus.Tests.Hosting;

public class ShutdownSignalTests
{
    /// <summary>
    /// The regression: the host used to handle only Ctrl+C, so a SIGTERM from a supervisor did
    /// nothing at all as PID 1 in a container and `docker stop` fell back to SIGKILL. Sending the
    /// real signal at this process is the only honest test of it — and if the handler ever stops
    /// marking the signal handled, this test run dies where it stands, which is the answer too.
    /// </summary>
    [Fact]
    public async Task Sigterm_CancelsTheShutdownToken_AndDoesNotEndTheProcess()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // A process cannot send itself SIGTERM on Windows.

        using var cts = new CancellationTokenSource();
        using var signals = ShutdownSignal.Install(cts);

        using (var kill = Process.Start("kill", $"-TERM {Environment.ProcessId}"))
        {
            Assert.NotNull(kill);
            await kill.WaitForExitAsync();
            Assert.Equal(0, kill.ExitCode);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(cts.IsCancellationRequested, "SIGTERM did not cancel the shutdown token.");
    }

    /// <summary>Disposing the registration puts the signals back as they were.</summary>
    [Fact]
    public void Install_IsDisposedIdempotently()
    {
        using var cts = new CancellationTokenSource();
        var signals = ShutdownSignal.Install(cts);
        signals.Dispose();
        signals.Dispose();
    }
}
