using GetAndSee.Core.Errors;

namespace GetAndSee.Core.Transfer;

/// <summary>
/// A forward-progress circuit breaker for the copy loop: it watches per-file outcomes and concludes the
/// device connection is gone once enough files fail <i>consecutively</i> with no progress in between.
/// </summary>
/// <remarks>
/// <para>
/// This is the manifestation-agnostic backstop for the unplug busy-spin defect (#38). The per-call
/// inactivity watchdog (<see cref="Device.DeviceWatchdog"/>) only catches a native call that
/// <i>parks</i> (never returns). A real cable yank can instead make the native read return
/// <b>fast and wrong</b> — <c>afc_file_read</c> reporting success with zero bytes (a premature EOF that
/// fails the size check), or a non-connection-fatal <see cref="iMobileDevice.Afc.AfcError"/> that maps
/// to a per-file failure. Either way the call returns immediately, sails past the inactivity timer, and
/// the copy loop fails the file and marches on to the next one — pegging a CPU core forever instead of
/// stopping (the #11 → #25 → #38 progression).
/// </para>
/// <para>
/// A per-call timer cannot see this, because no single call is slow. The signal is at the <i>run</i>
/// level: a healthy run makes forward progress (files complete or are skipped), whereas a disconnected
/// device fast-fails <i>every</i> remaining file. Counting consecutive failures and tripping once they
/// cross a limit turns any fast-fail manifestation into a clean, resumable stop without having to
/// enumerate every possible native error code. A single success resets the streak, so an isolated bad
/// or changed file never trips it — only a burst with no progress does.
/// </para>
/// </remarks>
internal sealed class ForwardProgressMonitor
{
    private readonly int consecutiveFailureLimit;
    private int consecutiveFailures;

    /// <summary>Creates a breaker that trips after <paramref name="consecutiveFailureLimit"/> failures in a row.</summary>
    /// <param name="consecutiveFailureLimit">
    /// The number of consecutive per-file failures (with no intervening success) that is treated as a
    /// lost device connection. Must be positive.
    /// </param>
    public ForwardProgressMonitor(int consecutiveFailureLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveFailureLimit);
        this.consecutiveFailureLimit = consecutiveFailureLimit;
    }

    /// <summary>Records forward progress (a file copied or skipped), clearing the consecutive-failure streak.</summary>
    public void RecordSuccess() => consecutiveFailures = 0;

    /// <summary>Records a per-file failure, extending the consecutive-failure streak.</summary>
    public void RecordFailure() => consecutiveFailures++;

    /// <summary>
    /// Throws <see cref="DeviceConnectionLostException"/> when the consecutive-failure streak has reached
    /// the limit — the signature of a disconnected device fast-failing every remaining file. Call this
    /// before copying each file so the run stops cleanly and resumably rather than touching (and failing)
    /// yet another file and spinning.
    /// </summary>
    /// <exception cref="DeviceConnectionLostException">The failure streak has reached the limit.</exception>
    public void ThrowIfConnectionLost()
    {
        if (consecutiveFailures >= consecutiveFailureLimit)
        {
            throw new DeviceConnectionLostException();
        }
    }
}
