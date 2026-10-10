using System.IO;

namespace PicHarbor.Core.Android;

/// <summary>
/// Handles Android FTP device identification, pairing, and <c>.picharbor-device-id</c> file verification.
/// </summary>
public static class AndroidDeviceDetector
{
    /// <summary>
    /// The dedicated hardware serial credential filename on the Android device.
    /// </summary>
    public const string SerialFileName = ".picharbor-serial";

    /// <summary>
    /// The default filename used to store the unique Android device identifier on the FTP server.
    /// </summary>
    public const string DeviceIdFileName = ".picharbor-device-id";

    /// <summary>
    /// Legacy filename used by earlier versions of get-and-see.
    /// </summary>
    public const string LegacyDeviceIdFileName = ".getandsee-device-id";

    /// <summary>
    /// Attempts to read the factory hardware serial credential (.picharbor-serial) from the FTP server.
    /// </summary>
    public static async Task<string?> TryReadDeviceSerialAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);
        string normalizedDir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');
        string serialFilePath = $"{normalizedDir}/{SerialFileName}";

        try
        {
            string? content = await ftp.DownloadTextAsync(serialFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content))
            {
                string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    return lines[0].Trim();
                }
            }
        }
        catch
        {
            // Not found
        }
        return null;
    }

    /// <summary>
    /// Writes the factory hardware serial number to the Android device (.picharbor-serial).
    /// </summary>
    public static async Task WriteDeviceSerialAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        string hardwareSerial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareSerial);
        await ftp.EnsureDirectoryExistsAsync(remoteTargetDir, cancellationToken).ConfigureAwait(false);

        string normalizedDir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');
        string serialFilePath = $"{normalizedDir}/{SerialFileName}";

        string content =
            $"""
            {hardwareSerial.Trim()}
            PicHarbor Android Factory Hardware Serial Credential.
            Do NOT delete or edit this file.
            (设备硬件出厂序列号凭据，请勿修改或删除)
            """;

        await ftp.UploadTextAsync(serialFilePath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies or initializes the Android device ID file on the FTP server, prioritizing hardware serial credentials.
    /// </summary>
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

        // 1. Check for modern hardware serial credential first (.picharbor-serial)
        string? serial = await TryReadDeviceSerialAsync(ftp, remoteTargetDir, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            return serial;
        }

        // 2. Check for legacy identification files
        string idFilePath = $"{normalizedDir}/{DeviceIdFileName}";
        string? existingContent = await ftp.DownloadTextAsync(idFilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingContent))
        {
            string legacyPath = $"{normalizedDir}/{LegacyDeviceIdFileName}";
            existingContent = await ftp.DownloadTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(existingContent))
        {
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

        // 3. New device: if a configured hardware serial or ID was provided, write to .picharbor-serial
        string deviceId = string.IsNullOrWhiteSpace(configuredDeviceId)
            ? $"PH-{Guid.NewGuid():N}"[..12].ToUpperInvariant()
            : configuredDeviceId.Trim();

        await WriteDeviceSerialAsync(ftp, remoteTargetDir, deviceId, cancellationToken).ConfigureAwait(false);
        return deviceId;
    }
}
