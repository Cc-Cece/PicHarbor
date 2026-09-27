using System.Diagnostics;
using System.Globalization;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.iPhone;

/// <summary>
/// Engine responsible for incrementally preparing and exporting media files from a GetAndSee PC archive
/// to the dedicated Apple Sync folder for Apple Devices for Windows synchronization.
/// </summary>
public static class IPhoneSyncEngine
{
    private const string PhotoCacheFolderName = "iPod Photo Cache";

    /// <summary>
    /// Executes the two-stage stage-1 preparation:增量整理并准备专有同步文件夹。
    /// </summary>
    /// <param name="pcDestinationRoot">Root directory of the PC archive containing get-and-see.db.</param>
    /// <param name="config">Export configuration parameters.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="onLog">Optional log message callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of export results.</returns>
    public static async Task<IPhoneSyncResult> ExportAsync(
        string pcDestinationRoot,
        IPhoneExportConfig config,
        IProgress<ProgressSnapshot>? progress = null,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pcDestinationRoot);
        ArgumentNullException.ThrowIfNull(config);

        var stopwatch = Stopwatch.StartNew();

        // 1. Open database and read manifest
        using var journal = TransferJournal.Open(pcDestinationRoot);
        var manifest = journal.ReadManifest();

        // 2. Resolve effective export root folder (.AppleSync/<DeviceModel>/ or custom)
        string exportRoot = config.GetEffectiveExportPath(pcDestinationRoot);
        Directory.CreateDirectory(exportRoot);

        onLog?.Invoke($"[iPhoneSync] Effective export folder: {exportRoot}");
        onLog?.Invoke($"[iPhoneSync] Mode: {config.AlbumMode}, MirrorDelete: {config.EnableMirrorDelete}");

        // 3. Resolve target relative paths for all manifest items (handling Live Photo pairing HEIC+MOV)
        var plannedExports = PlanTargetPaths(manifest, config.AlbumMode);

        int totalArchived = manifest.Count;
        int copiedCount = 0;
        int skippedCount = 0;
        int deletedCount = 0;
        int failedCount = 0;
        long totalCopiedBytes = 0;
        long totalPlannedBytes = manifest.Sum(m => m.SizeBytes);
        long processedBytes = 0;

        // 4. Mirror Delete Phase (if enabled)
        if (config.EnableMirrorDelete)
        {
            deletedCount = CleanOrphanedExportFiles(exportRoot, plannedExports, journal, onLog);
        }

        // 5. Incremental Copy Phase
        for (int i = 0; i < manifest.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = manifest[i];
            string sourceAbsPath = Path.GetFullPath(Path.Combine(pcDestinationRoot, entry.DestPath));
            string targetRelPath = plannedExports[entry.DestPath];
            string targetAbsPath = Path.GetFullPath(Path.Combine(exportRoot, targetRelPath));

            var sourceFileInfo = new FileInfo(LongPath.ToExtended(sourceAbsPath));
            if (!sourceFileInfo.Exists)
            {
                onLog?.Invoke($"[iPhoneSync] ⚠️ Source file missing: {entry.DestPath}");
                failedCount++;
                continue;
            }

            var targetFileInfo = new FileInfo(LongPath.ToExtended(targetAbsPath));
            bool isUpToDate = targetFileInfo.Exists &&
                              targetFileInfo.Length == sourceFileInfo.Length &&
                              Math.Abs((targetFileInfo.LastWriteTimeUtc - sourceFileInfo.LastWriteTimeUtc).TotalSeconds) < 2.0;

            if (isUpToDate)
            {
                skippedCount++;
                processedBytes += sourceFileInfo.Length;

                // Ensure DB has export record
                journal.UpsertIPhoneExportedFile(new IPhoneExportedFileRecord(
                    entry.DestPath,
                    targetRelPath,
                    config.DeviceModel,
                    DateTimeOffset.UtcNow,
                    sourceFileInfo.Length));
            }
            else
            {
                try
                {
                    string? parentDir = Path.GetDirectoryName(targetAbsPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    File.Copy(sourceAbsPath, targetAbsPath, overwrite: true);
                    File.SetLastWriteTimeUtc(targetAbsPath, sourceFileInfo.LastWriteTimeUtc);
                    if (sourceFileInfo.CreationTimeUtc > DateTime.MinValue)
                    {
                        try
                        {
                            File.SetCreationTimeUtc(targetAbsPath, sourceFileInfo.CreationTimeUtc);
                        }
                        catch
                        {
                            // Creation time setting can fail on some filesystems, non-fatal
                        }
                    }

                    copiedCount++;
                    totalCopiedBytes += sourceFileInfo.Length;
                    processedBytes += sourceFileInfo.Length;

                    journal.UpsertIPhoneExportedFile(new IPhoneExportedFileRecord(
                        entry.DestPath,
                        targetRelPath,
                        config.DeviceModel,
                        DateTimeOffset.UtcNow,
                        sourceFileInfo.Length));

                    onLog?.Invoke($"[iPhoneSync] Copied: {targetRelPath}");
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[iPhoneSync] ❌ Failed to copy {entry.DestPath}: {ex.Message}");
                    failedCount++;
                }
            }

            // Report progress snapshot
            if (progress is not null)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double speed = elapsedSec > 0 ? totalCopiedBytes / elapsedSec : 0;
                progress.Report(new ProgressSnapshot(
                    totalArchived,
                    copiedCount,
                    skippedCount,
                    failedCount,
                    totalPlannedBytes,
                    processedBytes,
                    speed,
                    speed,
                    null,
                    Path.GetFileName(targetRelPath),
                    sourceFileInfo.Length,
                    sourceFileInfo.Length));
            }

            // Allow async yield periodically
            if (i % 20 == 0)
            {
                await Task.Yield();
            }
        }

        // 6. Record device sync history
        journal.UpsertIPhoneDevice(config.DeviceModel, exportRoot, DateTimeOffset.UtcNow);

        stopwatch.Stop();
        onLog?.Invoke($"[iPhoneSync] Finished in {stopwatch.Elapsed:mm\\:ss}. Copied: {copiedCount}, Skipped: {skippedCount}, Deleted: {deletedCount}, Failed: {failedCount}");

        return new IPhoneSyncResult(
            totalArchived,
            copiedCount,
            skippedCount,
            deletedCount,
            failedCount,
            totalCopiedBytes,
            stopwatch.Elapsed)
        {
            ExportedFolder = exportRoot
        };
    }

    /// <summary>
    /// Checks whether the given relative path is inside the protected <c>iPod Photo Cache</c> folder.
    /// </summary>
    public static bool IsPhotoCachePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        string[] parts = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.Equals(PhotoCacheFolderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Computes target relative paths for each manifest entry, guaranteeing Live Photo pairs (HEIC + MOV)
    /// share the exact same subfolder.
    /// </summary>
    private static Dictionary<string, string> PlanTargetPaths(IReadOnlyList<ManifestEntry> manifest, IPhoneAlbumMode albumMode)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (albumMode == IPhoneAlbumMode.Flat)
        {
            foreach (var entry in manifest)
            {
                result[entry.DestPath] = Path.GetFileName(entry.DestPath);
            }
            return result;
        }

        // YearMonth Mode: Group by directory and base filename for Live Photo pairing
        // First pass: resolve capture dates for all entries
        var captureFolderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest)
        {
            string fileName = Path.GetFileName(entry.DestPath);
            string folder = Path.GetDirectoryName(entry.DestPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string key = Path.Combine(folder, baseName);

            DateTimeOffset? dt = ParseCaptureDate(entry);
            string yearMonth = dt.HasValue ? dt.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "UnknownDate";

            // Prefer image capture date over video capture date for Live Photo pairs
            string ext = Path.GetExtension(fileName).ToUpperInvariant();
            bool isImage = ext is ".HEIC" or ".JPG" or ".JPEG" or ".PNG" or ".DNG";

            if (!captureFolderMap.ContainsKey(key) || isImage)
            {
                captureFolderMap[key] = yearMonth;
            }
        }

        foreach (var entry in manifest)
        {
            string fileName = Path.GetFileName(entry.DestPath);
            string folder = Path.GetDirectoryName(entry.DestPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string key = Path.Combine(folder, baseName);

            string yearMonth = captureFolderMap.TryGetValue(key, out var ym) ? ym : "UnknownDate";
            result[entry.DestPath] = Path.Combine(yearMonth, fileName).Replace('\\', '/');
        }

        return result;
    }

    private static DateTimeOffset? ParseCaptureDate(ManifestEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ExifDateTimeOriginalIso) &&
            DateTimeOffset.TryParse(entry.ExifDateTimeOriginalIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exifDt))
        {
            return exifDt;
        }

        if (!string.IsNullOrEmpty(entry.SourceMtimeIso) &&
            DateTimeOffset.TryParse(entry.SourceMtimeIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mtimeDt))
        {
            return mtimeDt;
        }

        return null;
    }

    /// <summary>
    /// Scans exportRoot and removes physical files that are no longer part of the planned export set,
    /// strictly ignoring <c>iPod Photo Cache</c>.
    /// </summary>
    private static int CleanOrphanedExportFiles(
        string exportRoot,
        Dictionary<string, string> plannedExports,
        TransferJournal journal,
        Action<string>? onLog)
    {
        int deletedCount = 0;
        var validTargetRelPaths = new HashSet<string>(plannedExports.Values, StringComparer.OrdinalIgnoreCase);
        var targetToDestMap = plannedExports.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(exportRoot)) return 0;

        var files = Directory.GetFiles(exportRoot, "*", SearchOption.AllDirectories);
        foreach (var fullPath in files)
        {
            string relPath = Path.GetRelativePath(exportRoot, fullPath).Replace('\\', '/');

            // CRITICAL: Protect iPod Photo Cache from any modification or deletion!
            if (IsPhotoCachePath(relPath))
            {
                continue;
            }

            if (!validTargetRelPaths.Contains(relPath))
            {
                try
                {
                    File.Delete(fullPath);
                    deletedCount++;
                    onLog?.Invoke($"[iPhoneSync] 🗑️ Mirror Deleted orphan file: {relPath}");

                    // If DB has an export record for this target, remove it
                    if (targetToDestMap.TryGetValue(relPath, out var destPath))
                    {
                        journal.DeleteIPhoneExportedFile(destPath);
                    }
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[iPhoneSync] ⚠️ Could not delete orphan file {relPath}: {ex.Message}");
                }
            }
        }

        // Clean up empty directories (except iPod Photo Cache and root)
        CleanEmptyDirectories(exportRoot, onLog);

        return deletedCount;
    }

    private static void CleanEmptyDirectories(string startDir, Action<string>? onLog)
    {
        foreach (var dir in Directory.GetDirectories(startDir))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName.Equals(PhotoCacheFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CleanEmptyDirectories(dir, onLog);

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try
                {
                    Directory.Delete(dir);
                }
                catch
                {
                    // Ignore directory deletion errors
                }
            }
        }
    }
}
