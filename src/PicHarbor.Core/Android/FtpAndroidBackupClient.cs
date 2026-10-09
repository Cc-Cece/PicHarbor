using System.IO;
using PicHarbor.Core.Device;

namespace PicHarbor.Core.Android;

/// <summary>
/// Read-only Android media source client powered by SimpleFtpClient, implementing <see cref="IMediaSourceClient"/>.
/// </summary>
public sealed class FtpAndroidBackupClient : IMediaSourceClient
{
    private readonly string host;
    private readonly int port;
    private readonly string username;
    private readonly string password;
    private readonly string deviceId;
    private readonly string deviceName;
    private readonly SimpleFtpClient ftpClient;
    private bool disposed;

    /// <summary>Gets the underlying SimpleFtpClient for album discovery.</summary>
    public SimpleFtpClient UnderlyingFtpClient => ftpClient;

    /// <summary>Gets the connected device metadata.</summary>
    public DeviceInfo? Device { get; private set; }

    /// <summary>Creates a new instance of FtpAndroidBackupClient.</summary>
    public FtpAndroidBackupClient(
        string host,
        int port,
        string username,
        string password,
        string deviceId = "default",
        string deviceName = "Android Device")
    {
        this.host = host;
        this.port = port;
        this.username = username;
        this.password = password;
        this.deviceId = string.IsNullOrWhiteSpace(deviceId) ? "android" : deviceId;
        this.deviceName = string.IsNullOrWhiteSpace(deviceName) ? "Android Device" : deviceName;
        this.ftpClient = new SimpleFtpClient();
    }

    /// <summary>Connects to the remote FTP server and authenticates.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await ftpClient.ConnectAsync(host, port, username, password, cancellationToken).ConfigureAwait(false);
        Device = new DeviceInfo($"android:{deviceId}", deviceName, "Android");
    }

    /// <summary>Lists directory entries on the FTP server.</summary>
    public async Task<IReadOnlyList<string>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = await ftpClient.ListEntriesAsync(path, cancellationToken).ConfigureAwait(false);
        return entries.Select(e => e.Name).ToList();
    }

    /// <summary>Gets file metadata from the FTP server.</summary>
    public async Task<RemoteFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        long? size = await ftpClient.GetFileSizeAsync(path, cancellationToken).ConfigureAwait(false);
        return new RemoteFileInfo(size ?? 0, null, false);
    }

    /// <summary>Opens a read-only stream from the FTP server.</summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        return await ftpClient.OpenReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerate all eligible media files across the specified remote album paths according to filter options.
    /// </summary>
    public async Task<IReadOnlyList<RemoteFile>> EnumerateFilesAsync(
        IEnumerable<string> albumPaths,
        AndroidBackupFilterOptions filterOptions,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        filterOptions ??= new AndroidBackupFilterOptions();
        var result = new List<RemoteFile>();

        foreach (string albumPath in albumPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normPath = albumPath.TrimEnd('/').Replace('\\', '/');
            if (!normPath.StartsWith('/')) normPath = "/" + normPath;

            try
            {
                var entries = await ftpClient.ListEntriesAsync(normPath, cancellationToken).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    if (entry.IsDirectory) continue;

                    if (!filterOptions.IsSupportedMediaFile(entry.Name, out bool isVideo))
                    {
                        continue;
                    }

                    if (filterOptions.IgnoreSmallImages && !isVideo && entry.Size < filterOptions.MinFileSizeBytes)
                    {
                        continue;
                    }

                    if (entry.ModifiedAt.HasValue)
                    {
                        DateTime fileDate = entry.ModifiedAt.Value.Date;
                        if (dateFrom.HasValue && fileDate < dateFrom.Value.Date) continue;
                        if (dateTo.HasValue && fileDate > dateTo.Value.Date) continue;
                    }

                    string fullRemotePath = $"{normPath}/{entry.Name}";
                    result.Add(new RemoteFile(fullRemotePath, entry.Size, entry.ModifiedAt));
                }
            }
            catch
            {
                // Proceed with other albums if one fails
            }
        }

        return result;
    }

    /// <summary>Disposes underlying network resources.</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            ftpClient.Dispose();
        }
    }
}
