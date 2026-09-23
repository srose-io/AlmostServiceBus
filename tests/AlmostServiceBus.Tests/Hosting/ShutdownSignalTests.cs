using AlmostServiceBus.Core.Hosting;

namespace AlmostServiceBus.Tests.Hosting;

/// <summary>
/// The regression ShutdownSignal fixes — a SIGTERM the host swallowed, so `docker stop` waited its
/// whole timeout and SIGKILLed — is measured against the container rather than asserted here.
/// A test that raises the signal is a test that raises it at the WHOLE test process: it was written,
/// it passed, and over ten runs of this suite two unrelated socket tests failed beside it where ten
/// runs without it (four under deliberate load) failed none. One test is not worth that.
/// <para>
/// The measurement, 23 September 2026, image built by docker/build-docker.sh from this branch:
/// `docker stop --time 30` answered in 229 ms and docker events read
/// `kill signal=15` → `die exitCode=0`, with no second kill. Before the fix the same stop took the
/// full thirty seconds and ended in exit 137.
/// </para>
/// </summary>
public class ShutdownSignalTests
{
    /// <summary>Installing is safe to unwind twice, which is what a `using` in Program.cs needs.</summary>
    [Fact]
    public void Install_IsDisposedIdempotently()
    {
        using var cts = new CancellationTokenSource();
        var signals = ShutdownSignal.Install(cts);
        signals.Dispose();
        signals.Dispose();
    }

    /// <summary>A null token source is a programming error, not a silent no-op.</summary>
    [Fact]
    public void Install_RefusesWithoutATokenSource() =>
        Assert.Throws<ArgumentNullException>(() => ShutdownSignal.Install(null!));
}
