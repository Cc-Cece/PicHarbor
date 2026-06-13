using GetAndSee.Core.Errors;

namespace GetAndSee.Core.Transfer;

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
    private readonly CancellationTokenSource cts = new();
    private readonly ITimer timer;

    private long lastProgressTicks;
    private int consecutiveFailures;
    private int tripped;

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
    public ForwardProgressWatchdog(TimeSpan timeout, TimeProvider? clock = null, int consecutiveFailureLimit = 10)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Forward-progress timeout must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailureLimit);

        this.timeout = timeout;
        this.clock = clock ?? TimeProvider.System;
        this.consecutiveFailureLimit = consecutiveFailureLimit;
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
        if (Interlocked.Exchange(ref tripped, 1) == 0)
        {
            cts.Cancel();
        }
    }

    /// <summary>Stops the timer and releases the cancellation source.</summary>
    public void Dispose()
    {
        timer.Dispose();
        cts.Dispose();
    }
}
