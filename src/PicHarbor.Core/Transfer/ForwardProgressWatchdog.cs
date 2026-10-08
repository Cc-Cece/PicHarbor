using PicHarbor.Core.Errors;

namespace PicHarbor.Core.Transfer;

/// <summary>
/// The run-level forward-progress (liveness) watchdog: the single, manifestation-agnostic guard that
/// turns "the device stopped delivering bytes" into a clean, resumable stop, regardless of <i>why</i> the
/// bytes stopped (#11 → #25 → #38 → #42).
/// </summary>
/// <remarks>
/// <para>
/// Every prior unplug fix guarded a specific code location (a parked read, a parked open, a between-file
/// failure burst), and real hardware then found the next location. The one invariant across all of them is
/// that forward <b>byte</b> progress stops — the staging <c>.partial</c> freezes. This watchdog watches
/// that symptom directly via the existing per-chunk heartbeat (<c>onBytesStreamed</c>), on an
/// <b>independent timer</b> that does not depend on any device read returning. While a copy is in flight,
/// if no progress is recorded for <c>timeout</c>, it trips a single <see cref="CancellationToken"/> that
/// the copy loop observes, unwinding to the existing <see cref="DeviceConnectionLostException"/> / exit-3
/// path (resumable; <c>summary.txt</c> written).
/// </para>
/// <para>
/// It folds in the Sprint 3.2 forward-progress breaker (#38): a burst of consecutive per-file failures
/// with no intervening progress (a cable-yank that fast-fails every file instead of parking) trips it
/// immediately rather than waiting out the inactivity timer, so the journal is not churned through
/// thousands of failed rows. A single success or any streamed byte resets the streak, so an isolated bad
/// or changed file never trips it.
/// </para>
/// <para>
/// Why a run-level <i>timer</i> and not the old per-read inactivity timer: the per-read guard only armed
/// when a read failed to complete promptly and was enforced through a thread-pool-scheduled continuation,
/// so it provided no run-level, read-independent liveness — a read that returned fast-and-wrong, or a
/// native read that pinned a core and never returned, slipped past it (the #42 intra-file spin). This
/// guard is always armed while copying and fires on the heartbeat alone.
/// </para>
/// </remarks>
internal sealed class ForwardProgressWatchdog : IDisposable
{
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;
    private readonly int consecutiveFailureLimit;
    private readonly Action? onTrip;
    private readonly CancellationTokenSource cts = new();
    private readonly ITimer timer;
    private readonly object gate = new();

    private long lastProgressTicks;
    private int consecutiveFailures;
    private int tripped;
    private bool disposed;

    /// <summary>Creates and arms a forward-progress watchdog.</summary>
    /// <param name="timeout">
    /// Maximum time with no forward progress (no streamed bytes and no completed/skipped file) before the
    /// device is treated as lost. Must be positive; tie this to <c>--read-timeout</c>.
    /// </param>
    /// <param name="clock">Time source for the timer and the progress clock; injectable so the watchdog is testable.</param>
    /// <param name="consecutiveFailureLimit">
    /// Number of consecutive per-file failures (with no intervening progress) that trips the watchdog
    /// immediately — the fast path for a cable-yank that fast-fails every file (#38). Must be positive.
    /// </param>
    /// <param name="onTrip">
    /// The disconnect escape-hatch, run once on the watchdog's independent timer thread the first time it
    /// trips (#45). The main copy thread may be wedged in a synchronous native call that cancellation
    /// cannot interrupt, so this action writes the summary and hard-terminates the process rather than
    /// relying on a clean unwind. <see langword="null"/> keeps the Sprint 3.3 behaviour (cancel only).
    /// </param>
    public ForwardProgressWatchdog(
        TimeSpan timeout,
        TimeProvider? clock = null,
        int consecutiveFailureLimit = 10,
        Action? onTrip = null)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Forward-progress timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailureLimit);

        this.timeout = timeout;
        this.clock = clock ?? TimeProvider.System;
        this.consecutiveFailureLimit = consecutiveFailureLimit;
        this.onTrip = onTrip;
        lastProgressTicks = this.clock.GetUtcNow().UtcTicks;

        // Check several times per timeout window so detection lands close to `timeout` rather than up to
        // 2×timeout. The timer ticks independently of the copy loop, so a read pinned in native code (the
        // #42 spin) cannot prevent the trip.
        TimeSpan interval = TimeSpan.FromTicks(Math.Max(timeout.Ticks / 4, TimeSpan.FromMilliseconds(50).Ticks));
        timer = this.clock.CreateTimer(_ => Check(), null, interval, interval);
    }

    /// <summary>A token that is cancelled when the watchdog trips. The copy loop races its reads against it.</summary>
    public CancellationToken Token => cts.Token;

    /// <summary>Whether the watchdog has tripped (the device is considered lost).</summary>
    public bool Tripped => Volatile.Read(ref tripped) == 1;

    /// <summary>
    /// Records forward progress — a streamed byte chunk or a completed/skipped file. Resets both the
    /// inactivity clock and the consecutive-failure streak. Cheap and lock-free; safe to call on the hot
    /// per-chunk path.
    /// </summary>
    public void RecordProgress()
    {
        Interlocked.Exchange(ref lastProgressTicks, clock.GetUtcNow().UtcTicks);
        Volatile.Write(ref consecutiveFailures, 0);
    }

    /// <summary>
    /// Records a per-file failure. A run of <see cref="consecutiveFailureLimit"/> failures with no
    /// intervening progress trips the watchdog immediately (the #38 fast-fail path).
    /// </summary>
    public void RecordFailure()
    {
        int streak = Interlocked.Increment(ref consecutiveFailures);
        if (streak >= consecutiveFailureLimit)
        {
            Trip();
        }
    }

    private void Check()
    {
        long last = Interlocked.Read(ref lastProgressTicks);
        if (clock.GetUtcNow().UtcTicks - last >= timeout.Ticks)
        {
            Trip();
        }
    }

    private void Trip()
    {
        // Serialize with Dispose so a stray timer Check() that fires during shutdown can never call
        // cts.Cancel() after cts.Dispose() (which would throw ObjectDisposedException on the timer thread
        // and crash the process). Once disposed, a late trip is a no-op.
        bool firstTrip;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            firstTrip = Interlocked.Exchange(ref tripped, 1) == 0;
            if (firstTrip)
            {
                cts.Cancel();
            }
        }

        if (firstTrip)
        {
            // Run the disconnect escape-hatch OUTSIDE the gate (it writes summary.txt and may terminate the
            // process; holding the lock would block Dispose and is needless once we have decided to stop).
            // This runs on the independent timer thread, which the wedged main copy thread cannot block, so
            // a synchronous native call busy-spinning on a dead transport (#45) cannot prevent the exit.
            onTrip?.Invoke();
        }
    }

    /// <summary>
    /// Stops the timer and releases the cancellation source. Safe against the timer's in-flight
    /// <see cref="Check"/> callback: once disposed, a late <see cref="Trip"/> is a no-op, so no
    /// <see cref="ObjectDisposedException"/> can escape on the timer thread during shutdown (reachable when
    /// post-loop work outlasts the timeout since the last byte).
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        // Dispose the timer OUTSIDE the lock: the synchronous ITimer.Dispose does not wait for an in-flight
        // callback, and that callback (Trip) takes the same lock, so disposing under the lock risks a
        // deadlock. Any in-flight Trip has already observed disposed=true and skipped the cancel, so the
        // cts can now be disposed safely.
        timer.Dispose();
        cts.Dispose();
    }
}
