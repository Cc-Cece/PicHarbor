namespace PicHarbor.Core.Device;

/// <summary>
/// Read-only abstraction of a device's media file system (e.g. iPhone over AFC, or Android over FTP/ADB).
/// </summary>
public interface IMediaSourceClient : IDisposable
{
    /// <summary>
    /// Information about the connected device, or <see langword="null"/> before <see cref="ConnectAsync"/> succeeds.
    /// </summary>
    DeviceInfo? Device { get; }

    /// <summary>
    /// Establishes the read-only session with the remote device.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the child entry names of a directory on the device.
    /// </summary>
    Task<IReadOnlyList<string>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads metadata (size, modified time, is-directory) for a path on the device.
    /// </summary>
    Task<RemoteFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file on the device for sequential reading.
    /// </summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
}
