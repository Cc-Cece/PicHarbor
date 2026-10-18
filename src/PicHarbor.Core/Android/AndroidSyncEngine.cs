using System.IO;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Transfer;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.Android;

/// <summary>
/// Execution summary for an Android FTP incremental synchronization run.
/// </summary>
/// <param name="DeviceId">本次同步所使用的目标设备标识。</param>
/// <param name="CopiedCount">成功上传的文件数。</param>
/// <param name="SkippedCount">按增量规则跳过的文件数。</param>
/// <param name="FailedCount">上传失败的文件数。</param>
/// <param name="BytesCopied">成功上传的字节总数。</param>
/// <param name="Aborted">是否因设备身份未获确认而中止（此时未传输任何文件）。</param>
public sealed record AndroidSyncResult(
    string DeviceId,
    int CopiedCount,
    int SkippedCount,
    int FailedCount,
    long BytesCopied,
    bool Aborted = false);

/// <summary>
/// Incremental synchronization engine that transfers PC-archived files to an Android device via FTP.
/// </summary>
public static class AndroidSyncEngine
{
    /// <summary>
    /// Executes incremental synchronization from PC archive to Android over LAN FTP using specified configuration.
    /// </summary>
    /// <param name="pcDestinationRoot">PC 侧归档根目录。</param>
    /// <param name="config">同步配置。</param>
    /// <param name="confirmIdentity">
    /// 当目标设备身份与 PC 侧已确认标识不一致、或设备端缺少识别文件而无法核对时调用，
    /// 返回 <c>false</c> 表示用户中止。为 <c>null</c> 时一律中止（保守）。
    /// </param>
    /// <param name="progressTarget">进度上报目标。</param>
    /// <param name="onLog">日志回调。</param>
    /// <param name="onItemOutcome">单个文件结果回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="pauseGate">Optional cooperative pause at file boundaries.</param>
    public static async Task<AndroidSyncResult> SyncAsync(
        string pcDestinationRoot,
        AndroidSyncConfig config,
        Func<DeviceIdentityResult, bool>? confirmIdentity = null,
        IProgress<ProgressSnapshot>? progressTarget = null,
        Action<string>? onLog = null,
        Action<string, string, long, CopyStatus, string?>? onItemOutcome = null,
        CancellationToken cancellationToken = default,
        PicHarbor.Core.Transfer.TransferPauseGate? pauseGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pcDestinationRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.FtpHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.RemoteTargetDir);

        using var journal = TransferJournal.Open(pcDestinationRoot);
        IReadOnlyList<ManifestEntry> fullManifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(pcDestinationRoot);

        onLog?.Invoke($"[ANDROID] Connecting to FTP {config.FtpHost}:{config.FtpPort}...");
        using var ftp = new SimpleFtpClient();
        await ftp.ConnectAsync(config.FtpHost, config.FtpPort, config.FtpUser, config.FtpPassword, cancellationToken).ConfigureAwait(false);

        onLog?.Invoke($"[ANDROID] Identifying Android device at '{config.RemoteTargetDir}'...");
        DeviceIdentityResult identity = await AndroidDeviceDetector
            .ResolveIdentityAsync(ftp, config.RemoteTargetDir, config.ConfiguredDeviceId, cancellationToken)
            .ConfigureAwait(false);

        // 不一致 / 无法核对时必须先获得用户确认，绝不静默按本机标识继续
        // —— 否则文件可能传到 B 却被记录成 A，下次回传给 A 会错误跳过。
        if (identity.RequiresConfirmation && (confirmIdentity is null || !confirmIdentity(identity)))
        {
            onLog?.Invoke($"[ANDROID ABORTED] 设备身份未获确认（{identity.Match}），已中止回传。");
            return new AndroidSyncResult(identity.DeviceId, 0, 0, 0, 0, Aborted: true);
        }

        string deviceId = identity.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            onLog?.Invoke("[ANDROID ABORTED] 无法确定目标设备身份，已中止回传。");
            return new AndroidSyncResult(string.Empty, 0, 0, 0, 0, Aborted: true);
        }

        onLog?.Invoke($"[ANDROID] Device identity resolved: {deviceId} (source={identity.Source}, match={identity.Match}).");

        // Load manual selections if ScopeMode is ManualSelection and set is null
        if (config.ScopeMode == IPhoneRestoreScopeMode.ManualSelection && config.ManualSelectedPaths is null)
        {
            config.ManualSelectedPaths = journal.GetAndroidManualSelections(deviceId);
        }

        var manifestFiles = fullManifest.Where(config.IsEntryIncluded).ToList();
        var exportPaths=PicHarbor.Core.Storage.MediaExportPaths.Build(manifestFiles,m=>m.DestPath);
        HashSet<string> syncedSet = journal.GetAndroidSyncedDestPaths(deviceId);

        string normalizedDir = config.RemoteTargetDir.Replace('\\', '/').TrimEnd('/');

        int copied = 0, skipped = 0, failed = 0;
        long bytesCopied = 0;
        long totalBytes = manifestFiles.Sum(f => f.SizeBytes);

        onLog?.Invoke($"[ANDROID] Starting sync for device {deviceId} (RestoreMode: {config.RestoreMode}, {manifestFiles.Count:N0} files matched by scope out of {fullManifest.Count:N0} in archive)...");

        var progressModel = new TransferProgress(manifestFiles.Count, totalBytes);
        using var reporter = new ObservableProgressReporter(progressTarget, TimeSpan.FromMilliseconds(200));
        reporter.Start(progressModel);

        foreach (ManifestEntry file in manifestFiles)
        {
            if (pauseGate is not null) await pauseGate.WaitAsync(cancellationToken, ftp.KeepAliveAsync).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!PicHarbor.Core.Storage.MediaFileGuard.TryVerify(file,pcDestinationRoot,out string localPath,cancellationToken))
            {
                failed++;progressModel.StartFile(file.DestPath,file.SizeBytes);progressModel.CompleteFile(CopyStatus.Failed);
                onLog?.Invoke($"[ANDROID WARNING] Source unavailable or changed: {file.DestPath}");continue;
            }
            string remoteFilePath = $"{normalizedDir}/{exportPaths[file.SelectionKey]}";

            bool shouldSkip = false;
            string skipReasonDesc = "";

            if (config.RestoreMode == AndroidRestoreMode.HistoricalIncremental)
            {
                if (syncedSet.Contains(file.SelectionKey) || file.MediaId is null && syncedSet.Contains(file.DestPath))
                {
                    shouldSkip = true;
                    skipReasonDesc = "根据设备历史恢复记录跳过（Historical Incremental）";
                }
            }
            else
            {
                // Default Restore: Check Current Target State (FTP remote file size equivalence)
                long? remoteSize = await ftp.GetFileSizeAsync(remoteFilePath, cancellationToken).ConfigureAwait(false);
                if (remoteSize.HasValue && remoteSize.Value == file.SizeBytes)
                {
                    shouldSkip = true;
                    skipReasonDesc = "Android 目标文件已存在且大小等价（Current Target Equivalence）";
                }
            }

            if (shouldSkip)
            {
                skipped++;
                progressModel.StartFile(file.DestPath, file.SizeBytes);
                progressModel.RecordSkippedBytes(file.SizeBytes);
                progressModel.CompleteFile(CopyStatus.Skipped);
                onItemOutcome?.Invoke(file.DestPath, remoteFilePath, file.SizeBytes, CopyStatus.Skipped, skipReasonDesc);
                continue;
            }

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
                journal.RecordAndroidSync(file.SelectionKey, deviceId, DateTimeOffset.UtcNow);
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

        if (pauseGate is not null) await pauseGate.WaitAsync(cancellationToken, ftp.KeepAliveAsync).ConfigureAwait(false);
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
            ConfiguredDeviceId = configuredDeviceId
        };

        return SyncAsync(
            pcDestinationRoot: pcDestinationRoot,
            config: config,
            progressTarget: progressTarget,
            onLog: onLog,
            onItemOutcome: onItemOutcome,
            cancellationToken: cancellationToken);
    }
}
