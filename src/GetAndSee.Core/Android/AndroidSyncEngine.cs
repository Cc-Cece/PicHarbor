using System.IO;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.Util;
using GetAndSee.Core.iPhone;

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
    /// Executes incremental synchronization from PC archive to Android over LAN FTP using specified configuration.
    /// </summary>
    public static async Task<AndroidSyncResult> SyncAsync(
        string pcDestinationRoot,
        AndroidSyncConfig config,
        IProgress<ProgressSnapshot>? progressTarget = null,
        Action<string>? onLog = null,
        Action<string, string, long, CopyStatus, string?>? onItemOutcome = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pcDestinationRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.FtpHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.RemoteTargetDir);

        using var journal = TransferJournal.Open(pcDestinationRoot);
        IReadOnlyList<ManifestEntry> fullManifest = journal.ReadManifest();

        onLog?.Invoke($"[ANDROID] Connecting to FTP {config.FtpHost}:{config.FtpPort}...");
        using var ftp = new SimpleFtpClient();
        await ftp.ConnectAsync(config.FtpHost, config.FtpPort, config.FtpUser, config.FtpPassword, cancellationToken).ConfigureAwait(false);

        onLog?.Invoke($"[ANDROID] Identifying Android device at '{config.RemoteTargetDir}'...");
        string deviceId = await AndroidDeviceDetector.IdentifyOrPairDeviceAsync(
            ftp, config.RemoteTargetDir, config.ConfiguredDeviceId, config.DeviceName, cancellationToken).ConfigureAwait(false);

        journal.UpsertAndroidDevice(deviceId, string.IsNullOrWhiteSpace(config.DeviceName) ? "Android Device" : config.DeviceName, DateTimeOffset.UtcNow);

        // Load manual selections if ScopeMode is ManualSelection and set is null
        if (config.ScopeMode == IPhoneRestoreScopeMode.ManualSelection && config.ManualSelectedPaths is null)
        {
            config.ManualSelectedPaths = journal.GetAndroidManualSelections(deviceId);
        }

        var manifestFiles = fullManifest.Where(config.IsEntryIncluded).ToList();
        HashSet<string> syncedSet = journal.GetAndroidSyncedDestPaths(deviceId);

        string normalizedDir = config.RemoteTargetDir.Replace('\\', '/').TrimEnd('/');

        // Remote FTP Mirror Delete Phase (if enabled)
        if (config.EnableMirrorDelete)
        {
            var targetDestSet = manifestFiles.Select(m => m.DestPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> orphanedFiles = journal.GetOrphanedAndroidSyncedFiles(deviceId, targetDestSet);
            foreach (string file in orphanedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string remotePath = $"{normalizedDir}/{file.Replace('\\', '/')}";
                try
                {
                    await ftp.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    onLog?.Invoke($"[ANDROID MIRROR CLEAN] 🗑️ Deleted obsolete file on remote device: {file}");
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[ANDROID MIRROR CLEAN WARNING] Failed deleting remote file {remotePath}: {ex.Message}");
                }
                journal.RemoveAndroidSyncRecord(file, deviceId);
            }
        }

        int copied = 0, skipped = 0, failed = 0;
        long bytesCopied = 0;
        long totalBytes = manifestFiles.Sum(f => f.SizeBytes);

        onLog?.Invoke($"[ANDROID] Starting sync for device {deviceId} ({manifestFiles.Count:N0} files matched by scope out of {fullManifest.Count:N0} in archive, {syncedSet.Count:N0} previously synced)...");

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

    /// <summary>
    /// Legacy overload executing incremental synchronization from PC archive to Android over LAN FTP.
    /// </summary>
    public static Task<AndroidSyncResult> SyncAsync(
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
        var config = new AndroidSyncConfig
        {
            FtpHost = ftpHost,
            FtpPort = ftpPort,
            FtpUser = ftpUser,
            FtpPassword = ftpPassword,
            RemoteTargetDir = remoteTargetDir,
            DeviceName = deviceName,
            ConfiguredDeviceId = configuredDeviceId
        };

        return SyncAsync(pcDestinationRoot, config, progressTarget, onLog, onItemOutcome, cancellationToken);
    }
}
