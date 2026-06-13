using GetAndSee.Core.Transfer;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Transfer;

/// <summary>
/// Unit tests for the unified run-level liveness model. The watchdog trips on the byte-progress symptom
/// via an independent timer (deterministic here with a fake clock) or a consecutive-failure burst, and
/// resets on any forward progress so a slow-but-alive device and isolated failures never trip it.
/// </summary>
public sealed class ForwardProgressWatchdogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Trips_when_no_progress_for_the_timeout()
    {
        FakeTimeProvider clock = new();
        using ForwardProgressWatchdog watchdog = new(Timeout, clock);

        watchdog.Tripped.ShouldBeFalse();
        clock.Advance(Timeout);

        watchdog.Tripped.ShouldBeTrue();
        watchdog.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Does_not_trip_before_the_timeout_elapses()
    {
        FakeTimeProvider clock = new();
        using ForwardProgressWatchdog watchdog = new(Timeout, clock);

        clock.Advance(Timeout - TimeSpan.FromSeconds(1));

        watchdog.Tripped.ShouldBeFalse();
    }

    [Fact]
    public void A_slow_but_alive_device_never_trips_the_inactivity_timer()
    {
        // Progress arrives every (timeout − 1s) — a genuinely slow but live link. Each byte resets the
        // inactivity clock, so the watchdog must never trip however long the transfer runs.
        FakeTimeProvider clock = new();
        using ForwardProgressWatchdog watchdog = new(Timeout, clock);

        for (int i = 0; i < 20; i++)
        {
            clock.Advance(Timeout - TimeSpan.FromSeconds(1));
            watchdog.Tripped.ShouldBeFalse($"must not trip while bytes are still flowing (iteration {i})");
            watchdog.RecordProgress();
        }
    }

    [Fact]
    public void Trips_after_the_configured_consecutive_failure_burst()
    {
        FakeTimeProvider clock = new();
        using ForwardProgressWatchdog watchdog = new(Timeout, clock, consecutiveFailureLimit: 3);

        watchdog.RecordFailure();
        watchdog.RecordFailure();
        watchdog.Tripped.ShouldBeFalse();

        watchdog.RecordFailure();
        watchdog.Tripped.ShouldBeTrue();
        watchdog.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Progress_resets_the_consecutive_failure_streak_so_isolated_failures_never_trip()
    {
        FakeTimeProvider clock = new();
        using ForwardProgressWatchdog watchdog = new(Timeout, clock, consecutiveFailureLimit: 3);

        watchdog.RecordFailure();
        watchdog.RecordFailure();
        watchdog.RecordProgress(); // a file copied/skipped between failures — the device is alive
        watchdog.RecordFailure();
        watchdog.RecordFailure();

        watchdog.Tripped.ShouldBeFalse();
    }

    [Fact]
    public void A_recorded_byte_resets_the_inactivity_clock()
    {
        FakeTimeProvider clock = new();
        using ForwardProgressWatchdog watchdog = new(Timeout, clock);

        clock.Advance(Timeout - TimeSpan.FromSeconds(1));
        watchdog.RecordProgress();          // reset just before the deadline
        clock.Advance(Timeout - TimeSpan.FromSeconds(1));
        watchdog.Tripped.ShouldBeFalse();   // the reset pushed the deadline out

        clock.Advance(TimeSpan.FromSeconds(2)); // now a full timeout has passed since the last byte
        watchdog.Tripped.ShouldBeTrue();
    }

    [Fact]
    public void Disposing_stops_the_timer_so_it_cannot_trip_afterwards()
    {
        FakeTimeProvider clock = new();
        ForwardProgressWatchdog watchdog = new(Timeout, clock);

        watchdog.Dispose();
        clock.Advance(Timeout * 4);

        watchdog.Tripped.ShouldBeFalse();
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_timeout_or_failure_limit()
    {
        FakeTimeProvider clock = new();

        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardProgressWatchdog(TimeSpan.Zero, clock));
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardProgressWatchdog(Timeout, clock, consecutiveFailureLimit: 0));
    }
}
