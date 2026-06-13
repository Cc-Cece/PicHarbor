using GetAndSee.Core.Errors;
using GetAndSee.Core.Transfer;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Transfer;

/// <summary>
/// Unit coverage for the forward-progress circuit breaker that turns a fast-fail disconnect burst into
/// a clean, resumable stop (#38) — independent of the copy pipeline.
/// </summary>
public sealed class ForwardProgressMonitorTests
{
    [Fact]
    public void Trips_after_the_configured_number_of_consecutive_failures()
    {
        ForwardProgressMonitor monitor = new(3);

        monitor.ThrowIfConnectionLost();           // 0 failures — healthy
        monitor.RecordFailure();
        monitor.ThrowIfConnectionLost();           // 1
        monitor.RecordFailure();
        monitor.ThrowIfConnectionLost();           // 2
        monitor.RecordFailure();                   // 3 — at the limit

        Should.Throw<DeviceConnectionLostException>(() => monitor.ThrowIfConnectionLost());
    }

    [Fact]
    public void A_success_resets_the_failure_streak()
    {
        ForwardProgressMonitor monitor = new(3);

        monitor.RecordFailure();
        monitor.RecordFailure();
        monitor.RecordSuccess();                   // forward progress clears the streak
        monitor.RecordFailure();
        monitor.RecordFailure();
        monitor.ThrowIfConnectionLost();           // only 2 in a row since the reset — still healthy

        monitor.RecordFailure();                   // now 3 in a row
        Should.Throw<DeviceConnectionLostException>(() => monitor.ThrowIfConnectionLost());
    }

    [Fact]
    public void Rejects_a_non_positive_limit()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardProgressMonitor(0));
    }
}
