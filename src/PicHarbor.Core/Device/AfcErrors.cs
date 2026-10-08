using iMobileDevice.Afc;
using PicHarbor.Core.Errors;

namespace PicHarbor.Core.Device;

/// <summary>
/// Maps read-only AFC error codes to the right <see cref="DeviceException"/>, distinguishing a lost
/// device <i>connection</i> (stop the run cleanly and resumably) from a single unreadable file
/// (fail just that file and keep going).
/// </summary>
internal static class AfcErrors
{
    /// <summary>
    /// Returns <see langword="true"/> for AFC error codes that mean the device connection/transport
    /// itself is gone — a yanked USB cable or a dropped usbmux/AFC socket — rather than a problem with
    /// one file.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: per-file conditions (<c>ObjectNotFound</c>, <c>PermDenied</c>,
    /// <c>ReadError</c>, <c>IoError</c>, …) are excluded so a single corrupt or locked file is still
    /// failed-and-skipped, not mistaken for a disconnect that aborts the whole run.
    /// </remarks>
    /// <param name="error">The AFC error code returned by a read-only native call.</param>
    public static bool IsConnectionFatal(AfcError error) => error switch
    {
        AfcError.MuxError => true,             // usbmux / USB transport failure (the cable layer)
        AfcError.ServiceNotConnected => true,  // the AFC service socket is no longer connected
        AfcError.ServiceClientFailed => true,  // the AFC service client failed at the connection level
        _ => false,
    };

    /// <summary>
    /// Builds the typed exception for a failed read-only AFC call: a
    /// <see cref="DeviceConnectionLostException"/> when the connection is gone (so the run stops
    /// cleanly and resumably), otherwise a plain <see cref="DeviceException"/> describing a per-file
    /// failure the caller can record and move past.
    /// </summary>
    /// <param name="error">The AFC error code returned by the native call.</param>
    /// <param name="message">The per-file diagnostic message (used only for non-connection errors).</param>
    public static DeviceException ToException(AfcError error, string message) =>
        IsConnectionFatal(error)
            ? new DeviceConnectionLostException()
            : new DeviceException(message);
}
