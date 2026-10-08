using PicHarbor.Core.Errors;
using PicHarbor.Core.Preflight;

namespace PicHarbor.Core.Device;

/// <summary>
/// The status returned by a non-blocking device connection probe.
/// </summary>
public enum DeviceProbeStatus
{
    /// <summary>No device is connected or recognized on USB.</summary>
    NotConnected,

    /// <summary>Apple driver service (usbmuxd / 127.0.0.1:27015) is not running.</summary>
    DriverServiceUnavailable,

    /// <summary>Device is connected but requires user to unlock screen and tap "Trust This Computer".</summary>
    TrustRequired,

    /// <summary>Device is connected and authenticated via AFC protocol.</summary>
    Connected
}

/// <summary>
/// Result of a non-blocking device connection probe.
/// </summary>
/// <param name="Status">The probe status.</param>
/// <param name="Device">Connected device info, or <see langword="null"/> if not connected.</param>
/// <param name="Message">Optional human-readable diagnostic message.</param>
public sealed record DeviceProbeResult(
    DeviceProbeStatus Status,
    DeviceInfo? Device = null,
    string? Message = null);

/// <summary>
/// Provides non-blocking device connection detection for GUI and background status indicators.
/// </summary>
public static class DeviceDetector
{
    /// <summary>
    /// Probes connected iPhone devices without throwing CLI-formatted exceptions.
    /// </summary>
    /// <param name="timeout">Optional probe timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="DeviceProbeResult"/> indicating the connection status.</returns>
    public static async Task<DeviceProbeResult> ProbeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var preflight = new PreflightChecks(probeTimeout: timeout ?? TimeSpan.FromSeconds(1.5));
        try
        {
            await preflight.EnsureDriverServiceReachableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PreflightException ex)
        {
            return new DeviceProbeResult(DeviceProbeStatus.DriverServiceUnavailable, Message: ex.Message);
        }

        try
        {
            using var client = new AfcIPhoneClient();
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (client.Device is { } device)
            {
                return new DeviceProbeResult(DeviceProbeStatus.Connected, Device: device);
            }
        }
        catch (DeviceException ex)
        {
            if (ex.Message.Contains("Trust This Computer", StringComparison.OrdinalIgnoreCase))
            {
                return new DeviceProbeResult(DeviceProbeStatus.TrustRequired, Message: ex.Message);
            }
            return new DeviceProbeResult(DeviceProbeStatus.NotConnected, Message: ex.Message);
        }
        catch (Exception ex)
        {
            return new DeviceProbeResult(DeviceProbeStatus.NotConnected, Message: ex.Message);
        }

        return new DeviceProbeResult(DeviceProbeStatus.NotConnected);
    }
}
