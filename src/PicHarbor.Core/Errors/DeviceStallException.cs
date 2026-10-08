namespace PicHarbor.Core.Errors;

/// <summary>
/// Raised by the read-stall watchdog (#11 / R2) when the device produces no bytes within the
/// configured read timeout — the symptom of a yanked cable, a sleeping device, or a USB bus reset.
/// </summary>
/// <remarks>
/// A subtype of <see cref="DeviceException"/> so existing device-error handling catches it, but
/// distinct so the run can stop cleanly and resumably rather than marking a single file failed and
/// thrashing on every subsequent (also-stalled) read.
/// </remarks>
public sealed class DeviceStallException : DeviceException
{
    /// <summary>The default user-facing message shown when a read stalls.</summary>
    public const string DefaultMessage =
        "Device stopped responding (asleep or disconnected). Progress saved — reconnect and run the same command to resume.";

    /// <summary>Creates a <see cref="DeviceStallException"/> with the default actionable message.</summary>
    public DeviceStallException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates a <see cref="DeviceStallException"/> with a custom message.</summary>
    /// <param name="message">A clear, actionable description of the stall.</param>
    public DeviceStallException(string message)
        : base(message)
    {
    }
}
