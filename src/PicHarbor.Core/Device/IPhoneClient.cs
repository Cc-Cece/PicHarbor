namespace PicHarbor.Core.Device;

/// <summary>
/// Read-only view of an iPhone's media file system over AFC (Apple File Conduit).
/// </summary>
/// <remarks>
/// <para>
/// This is the project's single most safety-critical contract. By design the interface exposes
/// <b>only</b> read operations — connect, list a directory, stat a file, and open a file for
/// reading. There is deliberately no write, delete, rename, truncate, or directory-create method
/// here or on any implementation, so no caller can mutate the device (PROJECT_BRIEF §9.1).
/// </para>
/// <para>
/// The prohibition is enforced at build time: <c>ReadOnlyContractTests</c> reflects over the
/// compiled <c>PicHarbor.Core</c> assembly and fails the build if any AFC/lockdown mutation symbol
/// is referenced (see <c>docs/sprint-1/afc-library-decision.md</c> §5.5).
/// </para>
/// </remarks>
public interface IPhoneClient : IDisposable
{
    /// <summary>
    /// Information about the connected device, or <see langword="null"/> before <see cref="ConnectAsync"/> succeeds.
    /// </summary>
    DeviceInfo? Device { get; }

    /// <summary>
    /// Establishes the read-only AFC session: locate the device, perform the lockdown handshake,
    /// start <c>com.apple.afc</c>, and create the AFC client.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A task that completes once the device is connected and ready to read.</returns>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the immediate child entry names of an AFC directory (excluding <c>.</c> and <c>..</c>).
    /// </summary>
    /// <param name="path">Absolute device path, e.g. <c>/DCIM/</c>.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The child entry names. Use <see cref="GetFileInfoAsync"/> to classify each one.</returns>
    Task<IReadOnlyList<string>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads metadata (size, modified time, directory flag) for a single AFC path.
    /// </summary>
    /// <param name="path">Absolute device path.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The parsed file information.</returns>
    Task<RemoteFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a file on the device for sequential reading.
    /// </summary>
    /// <param name="path">Absolute device path to a regular file.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A forward-only, read-only stream over the file's bytes. The caller owns and disposes it.</returns>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
}
