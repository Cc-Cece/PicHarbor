namespace GetAndSee.Core.Errors;

/// <summary>
/// Raised when a read-only AFC call fails with an error that means the device connection itself is
/// gone — a yanked USB cable or a dropped usbmux/AFC socket — rather than a problem with one file.
/// </summary>
/// <remarks>
/// A sibling of <see cref="DeviceStallException"/>: both stop the copy run cleanly and resumably
/// (the in-flight file is left non-<c>done</c>, exit code 3) instead of marking a single file failed
/// and marching every remaining file into its own connection error or open-hang (#25).
/// </remarks>
public sealed class DeviceConnectionLostException : DeviceException
{
    /// <summary>The default user-facing message shown when the device connection is lost.</summary>
    public const string DefaultMessage =
        "Device connection lost (cable unplugged or device asleep). Progress saved — reconnect and run the same command to resume.";

    /// <summary>Creates a <see cref="DeviceConnectionLostException"/> with the default actionable message.</summary>
    public DeviceConnectionLostException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates a <see cref="DeviceConnectionLostException"/> with a custom message.</summary>
    /// <param name="message">A clear, actionable description of the connection loss.</param>
    public DeviceConnectionLostException(string message)
        : base(message)
    {
    }
}
