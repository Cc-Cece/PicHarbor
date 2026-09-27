using System.IO;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Android;

/// <summary>
/// Execution summary for an Android FTP incremental synchronization run.
/// </summary>
public sealed record AndroidSyncResult(
    string DeviceId,
    int CopiedCount,
    int SkippedCount,
    int FailedCount,
    long BytesCopied);

/// <summary>
/// Incremental synchronization engine that transfers PC-archived files to an Android device via FTP.
/// </summary>
public static class AndroidSyncEngine
{
    /// <summary>
    /// Executes incremental synchronization from PC archive to Android over LAN FTP.
    /// </summary>
    public static async Task<AndroidSyncResult> SyncAsync(
        string pcDestinationRoot,
        string ftpHost,
        int ftpPort,
        string ftpUser,
        string ftpPassword,
        string remoteTargetDir,
        string deviceName,
        string configuredDeviceId,
        IProgress<ProgressSnapshot>? progressTarget = null,
        Action<string>? onLog = null,
        Action<string, string, long, CopyStatus, string?>? onItemOutcome = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pcDestinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ftpHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteTargetDir);

        using var journal = TransferJournal.Open(pcDestinationRoot);
        IReadOnlyList<ManifestEntry> manifestFiles = journal.ReadManifest();

        onLog?.Invoke($"[ANDROID] Connecting to FTP {ftpHost}:{ftpPort}...");
        using var ftp = new SimpleFtpClient();
        await ftp.ConnectAsync(ftpHost, ftpPort, ftpUser, ftpPassword, cancellationToken).ConfigureAwait(false);

        onLog?.Invoke($"[ANDROID] Identifying Android device at '{remoteTargetDir}'...");
        string deviceId = await AndroidDeviceDetector.IdentifyOrPairDeviceAsync(
            ftp, remoteTargetDir, configuredDeviceId, deviceName, cancellationToken).ConfigureAwait(false);

        journal.UpsertAndroidDevice(deviceId, string.IsNullOrWhiteSpace(deviceName) ? "Android Device" : deviceName, DateTimeOffset.UtcNow);
        HashSet<string> syncedSet = journal.GetAndroidSyncedDestPaths(deviceId);

        int copied = 0, skipped = 0, failed = 0;
        long bytesCopied = 0;
        long totalBytes = manifestFiles.Sum(f => f.SizeBytes);

        onLog?.Invoke($"[ANDROID] Starting sync for device {deviceId} ({manifestFiles.Count:N0} files in archive, {syncedSet.Count:N0} already synced)...");

        string normalizedDir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');

        var progressModel = new TransferProgress(manifestFiles.Count, totalBytes);
        using var reporter = new ObservableProgressReporter(progressTarget, TimeSpan.FromMilliseconds(200));
        reporter.Start(progressModel);

        foreach (ManifestEntry file in manifestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string remoteFilePath = $"{normalizedDir}/{file.DestPath.Replace('\\', '/')}";

            if (syncedSet.Contains(file.DestPath))
            {
                skipped++;
                progressModel.StartFile(file.DestPath, file.SizeBytes);
                progressModel.RecordSkippedBytes(file.SizeBytes);
                progressModel.CompleteFile(CopyStatus.Skipped);
                onItemOutcome?.Invoke(file.DestPath, remoteFilePath, file.SizeBytes, CopyStatus.Skipped, "Android 目标目录及 SQLite 日志显示该文件已同步");
                continue;
            }

            string localPath = Path.Combine(pcDestinationRoot, file.DestPath);
            if (!File.Exists(localPath))
            {
                onLog?.Invoke($"[ANDROID WARNING] Local file missing: {file.DestPath}");
                failed++;
                progressModel.StartFile(file.DestPath, file.SizeBytes);
                progressModel.CompleteFile(CopyStatus.Failed);
                onItemOutcome?.Invoke(file.DestPath, remoteFilePath, file.SizeBytes, CopyStatus.Failed, "PC 本地归档目录找不到源文件");
                continue;
            }

            string? remoteSubDir = Path.GetDirectoryName(remoteFilePath)?.Replace('\\', '/');

            progressModel.StartFile(file.DestPath, file.SizeBytes);

            try
            {
                if (!string.IsNullOrWhiteSpace(remoteSubDir))
                {
                    await ftp.EnsureDirectoryExistsAsync(remoteSubDir, cancellationToken).ConfigureAwait(false);
                }

                long lastReported = 0;
                var fileProgress = new Progress<long>(totalSent =>
                {
                    long delta = totalSent - lastReported;
                    if (delta > 0)
                    {
                        lastReported = totalSent;
                        progressModel.RecordBytes(delta);
                    }
                });

                await ftp.UploadFileAsync(localPath, remoteFilePath, fileProgress, cancellationToken).ConfigureAwait(false);

                // Record successful sync in SQLite ONLY after transfer finishes completely
                journal.RecordAndroidSync(file.DestPath, deviceId, DateTimeOffset.UtcNow);
                copied++;
                bytesCopied += file.SizeBytes;
                progressModel.CompleteFile(CopyStatus.Copied);
                onLog?.Invoke($"[ANDROID SYNCED] {file.DestPath}");
                onItemOutcome?.Invoke(file.DestPath, remoteFilePath, file.SizeBytes, CopyStatus.Copied, "FTP 增量上传成功");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                progressModel.CompleteFile(CopyStatus.Failed);
                onLog?.Invoke($"[ANDROID ERROR] Failed uploading {file.DestPath}: {ex.Message}");
                onItemOutcome?.Invoke(file.DestPath, remoteFilePath, file.SizeBytes, CopyStatus.Failed, ex.Message);
            }
        }

        onLog?.Invoke($"[ANDROID COMPLETED] Device {deviceId}: {copied:N0} copied, {skipped:N0} skipped, {failed:N0} failed.");
        return new AndroidSyncResult(deviceId, copied, skipped, failed, bytesCopied);
    }
}
