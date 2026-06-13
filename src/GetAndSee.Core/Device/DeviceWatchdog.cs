using GetAndSee.Core.Errors;

namespace GetAndSee.Core.Device;

/// <summary>
/// The shared device-inactivity watchdog: races a blocking device operation against a timeout on an
/// injectable <see cref="TimeProvider"/> and, if the operation does not settle in time, abandons it
/// and throws <see cref="DeviceStallException"/> so the run can stop cleanly and resumably (#11 / #25 / R2).
/// </summary>
/// <remarks>
/// <para>
/// The native AFC calls (<c>afc_file_open</c>, <c>afc_read_directory</c>, <c>afc_get_file_info</c>,
/// <c>afc_file_read</c>) are blocking with no timeout: a cable yank, a sleeping device, or a USB bus
/// reset parks them forever (the #11/#25 hang). This helper runs the operation off the caller's path
/// and races it against <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>. If the
/// timeout wins, the operation is <i>abandoned</i>: a continuation is scheduled so that whenever it
/// eventually settles, any resource it produced is released (no handle leak), while the caller gets a
/// <see cref="DeviceStallException"/> instead of blocking forever.
/// </para>
/// <para>
/// This is the single implementation of the abandon-on-timeout/abandon-on-cancellation logic. The four
/// blocking native calls (open/stat/list) guard against a parked call with a timeout here, and
/// <see cref="AbandonableReadStream"/> reuses the same race to abandon a stuck read when the run-level
/// forward-progress watchdog cancels it — so the behavior is defined and tested in exactly one place.
/// </para>
/// </remarks>
internal static class DeviceWatchdog
{
    /// <summary>
    /// Runs a blocking native call on a worker thread and guards it with the inactivity timeout.
    /// </summary>
    /// <typeparam name="T">The native call's result type.</typeparam>
    /// <param name="blockingNativeCall">The synchronous, blocking native call to guard.</param>
    /// <param name="timeout">Maximum time to wait before treating the call as a stall.</param>
    /// <param name="clock">Time source for the timeout; injectable so the watchdog is testable.</param>
    /// <param name="cancellationToken">Token observed for caller cancellation (distinct from a stall).</param>
    /// <param name="onAbandoned">
    /// Optional cleanup invoked when an <i>abandoned</i> call eventually settles, receiving the settled
    /// task so it can release any resource the late call produced (e.g. close an AFC handle). Not
    /// invoked on the normal, in-time path.
    /// </param>
    /// <returns>The native call's result.</returns>
    /// <exception cref="DeviceStallException">The call produced no result within <paramref name="timeout"/>.</exception>
    public static Task<T> RunWithTimeoutAsync<T>(
        Func<T> blockingNativeCall,
        TimeSpan timeout,
        TimeProvider clock,
        CancellationToken cancellationToken,
        Action<Task<T>>? onAbandoned = null)
    {
        ArgumentNullException.ThrowIfNull(blockingNativeCall);
        cancellationToken.ThrowIfCancellationRequested();

        // Run the blocking native call on a worker so a parked call can never block the caller; the
        // race below is what turns "parked forever" into a clean, abandonable stall.
        return RaceAgainstTimeoutAsync(Task.Run(blockingNativeCall), timeout, clock, cancellationToken, onAbandoned);
    }

    /// <summary>
    /// Races an already-running device <paramref name="operation"/> against the inactivity timeout.
    /// </summary>
    /// <typeparam name="T">The operation's result type.</typeparam>
    /// <param name="operation">The in-flight device operation to guard.</param>
    /// <param name="timeout">Maximum time to wait before treating the operation as a stall.</param>
    /// <param name="clock">Time source for the timeout; injectable so the watchdog is testable.</param>
    /// <param name="cancellationToken">Token observed for caller cancellation (distinct from a stall).</param>
    /// <param name="onAbandoned">
    /// Optional cleanup invoked when an <i>abandoned</i> operation eventually settles (see
    /// <see cref="RunWithTimeoutAsync{T}"/>).
    /// </param>
    /// <returns>The operation's result.</returns>
    /// <exception cref="DeviceStallException">The operation did not settle within <paramref name="timeout"/>.</exception>
    public static async Task<T> RaceAgainstTimeoutAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        TimeProvider clock,
        CancellationToken cancellationToken,
        Action<Task<T>>? onAbandoned = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        // Only materialize the timer when the operation doesn't complete promptly — a healthy, fast
        // call pays no timer cost on the hot path.
        if (!operation.IsCompleted)
        {
            using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = Task.Delay(timeout, clock, delayCts.Token);
            Task finished = await Task.WhenAny(operation, delay).ConfigureAwait(false);
            if (finished != operation)
            {
                // The timeout (or caller cancellation) won the race. Abandon the orphaned operation:
                // schedule its cleanup for whenever it finally settles so no handle leaks, then surface
                // the stall (or the caller's cancellation) rather than blocking forever.
                Abandon(operation, onAbandoned);
                cancellationToken.ThrowIfCancellationRequested();
                throw new DeviceStallException();
            }

            // The operation finished first; disposing delayCts here cancels the now-irrelevant timer.
        }

        return await operation.ConfigureAwait(false);
    }

    private static void Abandon<T>(Task<T> operation, Action<Task<T>>? onAbandoned)
    {
        // Never block the caller on the orphaned operation. When it eventually settles, observe any
        // fault (so it cannot escalate to an unobserved-exception crash) and run the cleanup hook.
        _ = operation.ContinueWith(
            settled =>
            {
                _ = settled.Exception;
                onAbandoned?.Invoke(settled);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
