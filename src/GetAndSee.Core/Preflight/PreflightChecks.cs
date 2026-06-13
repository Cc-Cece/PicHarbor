using System.Net;
using System.Net.Sockets;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Preflight;

/// <summary>
/// Checks that must pass before any copying begins: the Apple driver service is reachable (R21),
/// the destination is writable (R15), and there is enough free space (R4).
/// </summary>
public sealed class PreflightChecks
{
    /// <summary>Loopback port the Apple usbmuxd service listens on.</summary>
    public const int UsbmuxdPort = 27015;

    private const string DriverServiceMessage =
        "iPhone driver service not running — open the Apple Devices app once, or install iTunes.";

    private readonly TimeSpan probeTimeout;
    private readonly Func<HostPowerStatus> powerStatusProvider;

    /// <summary>Creates the pre-flight checks.</summary>
    /// <param name="probeTimeout">How long to wait for the usbmuxd probe; defaults to 2 seconds.</param>
    /// <param name="powerStatusProvider">Host power-status source; defaults to the Windows API. Injectable for tests.</param>
    public PreflightChecks(TimeSpan? probeTimeout = null, Func<HostPowerStatus>? powerStatusProvider = null)
    {
        this.probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(2);
        this.powerStatusProvider = powerStatusProvider ?? HostPower.Get;
    }

    /// <summary>Returns the host's current power state (R14: warn before a long run on battery).</summary>
    /// <returns>The host power status.</returns>
    public HostPowerStatus GetHostPowerStatus() => powerStatusProvider();

    /// <summary>
    /// Probes <c>127.0.0.1:27015</c>. If it is unreachable the Apple driver service is not running,
    /// which looks identical to "no device" — so we surface a distinct, actionable message (R21).
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <exception cref="PreflightException">Thrown when the service cannot be reached.</exception>
    public async Task EnsureDriverServiceReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(probeTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, UsbmuxdPort, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PreflightException(DriverServiceMessage);
        }
        catch (SocketException)
        {
            throw new PreflightException(DriverServiceMessage);
        }
    }

    /// <summary>
    /// Verifies the destination exists (creating it if needed) and is writable by creating and
    /// deleting a probe file (R15).
    /// </summary>
    /// <param name="destinationRoot">The destination root directory.</param>
    /// <exception cref="PreflightException">Thrown when the destination cannot be written.</exception>
    public void EnsureDestinationWritable(string destinationRoot)
    {
        // Route the directory-create and the write-probe through the \\?\ long-path form so a deep
        // destination passes pre-flight on a stock Windows machine (LongPathsEnabled=0), instead of
        // throwing a misleading "not writable" here — before the long-path-safe journal is ever opened
        // (R6 / #39). The probe filename (~61 chars) crosses MAX_PATH at a shallower root than the 14-char
        // journal DB, so this is the earliest place a deep destination is touched. ToExtended is a no-op
        // on short or already-prefixed paths, so shallow destinations are unaffected.
        string extendedRoot = LongPath.ToExtended(destinationRoot);
        try
        {
            System.IO.Directory.CreateDirectory(extendedRoot);
            string probe = Path.Combine(extendedRoot, $".get-and-see-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new PreflightException($"Destination \"{destinationRoot}\" is not writable: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies the destination drive has at least <paramref name="estimatedBytes"/> plus 5% headroom
    /// of free space (R4).
    /// </summary>
    /// <param name="destinationRoot">The destination root directory.</param>
    /// <param name="estimatedBytes">Estimated total bytes to be copied.</param>
    /// <exception cref="PreflightException">Thrown when free space is insufficient.</exception>
    public void EnsureSufficientFreeSpace(string destinationRoot, long estimatedBytes)
    {
        long required = (long)(estimatedBytes * 1.05);
        string? root = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < required)
        {
            throw new PreflightException(
                $"Not enough free space on {root} — need about {ByteSize.Humanize(required)} " +
                $"(estimated transfer plus 5% headroom), but only {ByteSize.Humanize(drive.AvailableFreeSpace)} is free.");
        }
    }
}
