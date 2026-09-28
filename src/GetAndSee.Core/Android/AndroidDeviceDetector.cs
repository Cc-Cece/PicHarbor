using System.IO;

namespace GetAndSee.Core.Android;

/// <summary>
/// Handles Android FTP device identification, pairing, and <c>.getandsee-device-id</c> file verification.
/// </summary>
public static class AndroidDeviceDetector
{
    /// <summary>
    /// The default filename used to store the unique Android device identifier on the FTP server.
    /// </summary>
    public const string DeviceIdFileName = ".getandsee-device-id";

    /// <summary>
    /// Verifies or initializes the Android device ID file on the FTP server.
    /// </summary>
    /// <param name="ftp">An open <see cref="SimpleFtpClient"/>.</param>
    /// <param name="remoteTargetDir">The remote target directory on the FTP server (e.g. /DCIM/GetAndSee/iPhone 15 Pro/).</param>
    /// <param name="configuredDeviceId">The expected device ID from app settings, or empty for auto-pairing.</param>
    /// <param name="deviceName">The display name for the device (e.g. Pixel 8).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified or newly generated Android <c>device_id</c>.</returns>
    public static async Task<string> IdentifyOrPairDeviceAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        string configuredDeviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteTargetDir);

        await ftp.EnsureDirectoryExistsAsync(remoteTargetDir, cancellationToken).ConfigureAwait(false);

        string normalizedDir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');
        string idFilePath = $"{normalizedDir}/{DeviceIdFileName}";

        string? existingContent = await ftp.DownloadTextAsync(idFilePath, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(existingContent))
        {
            // Extract device ID from first line
            string[] lines = existingContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string foundId = lines.Length > 0 ? lines[0].Trim() : string.Empty;

            if (!string.IsNullOrWhiteSpace(configuredDeviceId) &&
                !string.Equals(foundId, configuredDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Android 设备身份不匹配！远端设备 ID ({foundId}) 与本地配置 ({configuredDeviceId}) 不符。请确认是否连错了 FTP 设备，防止误传文件。");
            }

            return string.IsNullOrWhiteSpace(foundId) ? configuredDeviceId : foundId;
        }

        // New Device: generate or use configured ID
        string deviceId = string.IsNullOrWhiteSpace(configuredDeviceId)
            ? $"GAS-{Guid.NewGuid():N}"[..12].ToUpperInvariant()
            : configuredDeviceId;

        string newFileText =
            $"""
            {deviceId}
            GetAndSee device identification file.
            Please do NOT delete or modify this file.
            Deleting or modifying it may prevent GetAndSee
            from correctly identifying this Android device
            and may affect incremental synchronization.
            (设备标识文件，请勿修改或删除)
            """;

        await ftp.UploadTextAsync(idFilePath, newFileText, cancellationToken).ConfigureAwait(false);
        return deviceId;
    }
}
