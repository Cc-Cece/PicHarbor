using Microsoft.Extensions.Time.Testing;
using PicHarbor.Core.Transfer;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Transfer;

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
    public void Disposing_at_the_trip_threshold_does_not_throw()
    {
        // A Check() fires (and trips) as the fake clock crosses the timeout; disposing right then must be
        // clean — no exception escapes from tearing the watchdog down at the moment it would trip.
        FakeTimeProvider clock = new();
        ForwardProgressWatchdog watchdog = new(Timeout, clock);

        clock.Advance(Timeout);

        Should.NotThrow(() => watchdog.Dispose());
    }

    [Fact]
    public void A_late_trip_after_dispose_is_a_safe_no_op_not_a_crash()
    {
        // Shutdown race: the timer's synchronous Dispose() does not wait for an in-flight Check(), so a
        // stray Check() -> Trip() could otherwise call cts.Cancel() AFTER cts.Dispose() — an
        // ObjectDisposedException on the timer thread that crashes the process (reachable when post-loop
        // work outlasts the read timeout since the last byte). Drive the same Trip() path after Dispose
        // (RecordFailure at the limit) and assert it is a no-op, not a throw.
        FakeTimeProvider clock = new();
        ForwardProgressWatchdog watchdog = new(Timeout, clock, consecutiveFailureLimit: 1);

        watchdog.Dispose();

        Should.NotThrow(() => watchdog.RecordFailure());
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_timeout_or_failure_limit()
    {
        FakeTimeProvider clock = new();

        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardProgressWatchdog(TimeSpan.Zero, clock));
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardProgressWatchdog(Timeout, clock, consecutiveFailureLimit: 0));
    }

    [Fact]
    public void An_inactivity_trip_runs_the_escape_hatch_once()
    {
        // 3.4: on trip the watchdog runs the disconnect escape-hatch (write summary + hard-terminate) on its
        // independent timer thread, not just a token cancel — because the main thread may be wedged in a
        // synchronous native spin (#45) that cancellation cannot interrupt.
        FakeTimeProvider clock = new();
        int calls = 0;
        using ForwardProgressWatchdog watchdog = new(Timeout, clock, onTrip: () => Interlocked.Increment(ref calls));

        clock.Advance(Timeout);

        watchdog.Tripped.ShouldBeTrue();
        watchdog.Token.IsCancellationRequested.ShouldBeTrue();
        calls.ShouldBe(1);
    }

    [Fact]
    public void A_failure_burst_trip_runs_the_escape_hatch_once()
    {
        FakeTimeProvider clock = new();
        int calls = 0;
        using ForwardProgressWatchdog watchdog = new(
            Timeout, clock, consecutiveFailureLimit: 2, onTrip: () => Interlocked.Increment(ref calls));

        watchdog.RecordFailure();
        watchdog.RecordFailure();

        watchdog.Tripped.ShouldBeTrue();
        calls.ShouldBe(1);
    }

    [Fact]
    public void The_escape_hatch_never_runs_while_the_device_stays_alive()
    {
        // False-positive guard: a slow-but-alive device resets the clock on every chunk, so the escape-hatch
        // (a process terminate in production) must never fire.
        FakeTimeProvider clock = new();
        int calls = 0;
        using ForwardProgressWatchdog watchdog = new(Timeout, clock, onTrip: () => Interlocked.Increment(ref calls));

        for (int i = 0; i < 5; i++)
        {
            clock.Advance(Timeout - TimeSpan.FromSeconds(1));
            watchdog.RecordProgress();
        }

        calls.ShouldBe(0);
        watchdog.Tripped.ShouldBeFalse();
    }
}
