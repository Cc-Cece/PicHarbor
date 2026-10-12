using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Transfer;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.iPhone;

/// <summary>Progress snapshot of a sync-folder merge operation.</summary>
/// <param name="ProcessedFiles">Files processed so far.</param>
/// <param name="TotalFiles">Total files planned.</param>
/// <param name="ProcessedBytes">Bytes processed so far.</param>
/// <param name="TotalBytes">Total bytes planned.</param>
/// <param name="CurrentFile">Display name of the file currently being merged.</param>
public sealed record IPhoneSyncFolderMergeProgress(
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string CurrentFile);

/// <summary>Summary of a completed sync-folder merge.</summary>
/// <param name="CopiedCount">Files copied into the target folder.</param>
/// <param name="SkippedCount">Files already equivalent in the target folder.</param>
/// <param name="FailedCount">Files that failed to copy.</param>
/// <param name="CopiedBytes">Total bytes copied.</param>
public sealed record IPhoneSyncFolderMergeResult(
    int CopiedCount,
    int SkippedCount,
    int FailedCount,
    long CopiedBytes);

/// <summary>
/// Maintenance operations for Apple Devices sync folders: merges the contents of an old sync
/// folder into the current one (incremental, equivalence-aware) so a sync-source switch can
/// carry the old files along, plus scans for scattered legacy folders.
/// </summary>
public static class IPhoneSyncFolderMaintenanceService
{
    /// <summary>Apple Devices' own cache subfolder — never touched by merge/cleanup.</summary>
    public const string PhotoCacheFolderName = "iPod Photo Cache";

    /// <summary>
    /// Incrementally merges every file of <paramref name="oldFolder"/> into <paramref name="newFolder"/>
    /// at the same relative path. Files already equivalent in the target are skipped. The Apple Devices
    /// cache subfolder is ignored. Journal rows stay valid because relative paths are preserved.
    /// Foreign files inside the old folder are copied like any other content (merge never deletes).
    /// </summary>
    public static async Task<IPhoneSyncFolderMergeResult> MergeSyncFolderAsync(
        string oldFolder,
        string newFolder,
        IProgress<IPhoneSyncFolderMergeProgress>? progress = null,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFolder);

        var stopwatch = Stopwatch.StartNew();

        var files = new List<(string SourceAbs, string TargetAbs, string RelPath, long Size)>();
        foreach (string sourceAbs in EnumerateMergeFiles(oldFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relPath = Path.GetRelativePath(oldFolder, sourceAbs);
            string targetAbs = Path.GetFullPath(Path.Combine(newFolder, relPath));
            long size;
            try
            {
                size = new FileInfo(LongPath.ToExtended(sourceAbs)).Length;
            }
            catch
            {
                size = 0;
            }
            files.Add((sourceAbs, targetAbs, relPath, size));
        }

        Directory.CreateDirectory(newFolder);

        long totalBytes = files.Sum(f => f.Size);
        long processedBytes = 0;
        int copied = 0;
        int skipped = 0;
        int failed = 0;

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sourceAbs, targetAbs, relPath, size) = files[i];
            string displayName = Path.GetFileName(targetAbs);

            var equiv = FileEquivalenceResolver.ResolveLocalFile(sourceAbs, targetAbs);
            if (equiv == FileEquivalenceResult.Equivalent)
            {
                skipped++;
                processedBytes += size;
            }
            else
            {
                try
                {
                    string? parentDir = Path.GetDirectoryName(targetAbs);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    File.Copy(sourceAbs, targetAbs, overwrite: true);
                    var sourceInfo = new FileInfo(LongPath.ToExtended(sourceAbs));
                    File.SetLastWriteTimeUtc(targetAbs, sourceInfo.LastWriteTimeUtc);
                    try
                    {
                        if (sourceInfo.CreationTimeUtc > DateTime.MinValue)
                        {
                            File.SetCreationTimeUtc(targetAbs, sourceInfo.CreationTimeUtc);
                        }
                    }
                    catch
                    {
                        // Creation time setting can fail on some filesystems, non-fatal.
                    }

                    copied++;
                    processedBytes += size;
                    onLog?.Invoke($"[merge] Copied: {relPath}");
                }
                catch (Exception ex)
                {
                    failed++;
                    onLog?.Invoke($"[merge] ❌ Failed to copy {relPath}: {ex.Message}");
                }
            }

            double elapsedSec = stopwatch.Elapsed.TotalSeconds;
            double speed = elapsedSec > 0 ? processedBytes / elapsedSec : 0;
            progress?.Report(new IPhoneSyncFolderMergeProgress(
                copied + skipped + failed,
                files.Count,
                processedBytes,
                totalBytes,
                displayName));

            if (i % 20 == 0)
            {
                await Task.Yield();
            }
        }

        stopwatch.Stop();
        onLog?.Invoke($"[merge] Finished in {stopwatch.Elapsed:mm\\:ss}. Copied: {copied}, Skipped: {skipped}, Failed: {failed}");
        return new IPhoneSyncFolderMergeResult(copied, skipped, failed, processedBytes);
    }

    /// <summary>Enumerates mergeable files under the folder, excluding the Apple Devices cache.</summary>
    public static IEnumerable<string> EnumerateMergeFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        foreach (string path in Directory.EnumerateFiles(folder, "*", options))
        {
            string relPath = Path.GetRelativePath(folder, path);
            if (IPhoneSyncEngine.IsPhotoCachePath(relPath))
            {
                continue;
            }

            yield return path;
        }
    }

    /// <summary>A scattered sync folder discovered by a scan, reconciled against its device's current folder.</summary>
    public sealed record SyncFolderScanItem(
        string Folder,
        string? DeviceName,
        bool IsCurrent,
        int FileCount,
        long TotalBytes,
        int EquivalentCount,
        int MissingCount,
        string? ReconcileTarget);

    /// <summary>
    /// Scans for scattered Apple Devices sync folders: every <c>iphone_devices.sync_folder</c> plus
    /// heuristic subdirectories of <c>&lt;archive&gt;\.AppleSync\</c>, skipping ignored folders. Each
    /// candidate is reconciled against its device's current sync folder (equivalence count) so the UI
    /// can offer merge/cleanup with real numbers. Runs synchronously — call from a worker thread.
    /// </summary>
    public static IReadOnlyList<SyncFolderScanItem> ScanSyncFolders(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var results = new List<SyncFolderScanItem>();

        using (var journal = TransferJournal.OpenReadOnly(archivePath))
        {
            var deviceRows = journal.GetAllIPhoneDevices();
            var ignored = journal.GetIgnoredSyncFolders()
                .Select(NormalizeDir)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in deviceRows)
            {
                if (!string.IsNullOrWhiteSpace(row.SyncFolder) && Directory.Exists(row.SyncFolder))
                {
                    candidates.Add(NormalizeDir(row.SyncFolder));
                }
            }

            string appleSyncRoot = Path.Combine(archivePath, ".AppleSync");
            if (Directory.Exists(appleSyncRoot))
            {
                foreach (string dir in Directory.EnumerateDirectories(appleSyncRoot))
                {
                    candidates.Add(NormalizeDir(dir));
                }
            }

            foreach (string candidate in candidates)
            {
                if (ignored.Contains(NormalizeDir(candidate))) continue;

                var owningRow = deviceRows.FirstOrDefault(r =>
                    !string.IsNullOrWhiteSpace(r.SyncFolder) &&
                    string.Equals(NormalizeDir(r.SyncFolder), candidate, StringComparison.OrdinalIgnoreCase));

                long totalBytes = 0;
                int fileCount = 0;
                foreach (string file in EnumerateMergeFiles(candidate))
                {
                    fileCount++;
                    try
                    {
                        totalBytes += new FileInfo(LongPath.ToExtended(file)).Length;
                    }
                    catch { }
                }

                if (owningRow is not null)
                {
                    // Live folder for a known device — nothing scattered here.
                    results.Add(new SyncFolderScanItem(candidate, owningRow.DeviceModel, true, fileCount, totalBytes, fileCount, 0, null));
                    continue;
                }

                // Reconcile target: the device row whose sticky folder name matches the leaf, else the single device.
                string leaf = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var targetRow = deviceRows.FirstOrDefault(r =>
                    string.Equals(r.FolderName ?? r.DeviceModel, leaf, StringComparison.OrdinalIgnoreCase))
                    ?? (deviceRows.Count == 1 ? deviceRows[0] : null);

                string? target = targetRow?.SyncFolder;
                int equivalent = 0;
                int missing = 0;
                if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
                {
                    foreach (string file in EnumerateMergeFiles(candidate))
                    {
                        string relPath = Path.GetRelativePath(candidate, file);
                        string counterpart = Path.GetFullPath(Path.Combine(target, relPath));
                        var equiv = FileEquivalenceResolver.ResolveLocalFile(file, counterpart);
                        if (equiv == FileEquivalenceResult.Equivalent)
                        {
                            equivalent++;
                        }
                        else
                        {
                            missing++;
                        }
                    }
                }
                else
                {
                    missing = fileCount;
                }

                results.Add(new SyncFolderScanItem(
                    candidate,
                    targetRow?.DeviceModel ?? owningRow?.DeviceModel,
                    false,
                    fileCount,
                    totalBytes,
                    equivalent,
                    missing,
                    target));
            }
        }

        return results;
    }

    private static string NormalizeDir(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // --- Recycle-bin deletion (SHFileOperation with FOF_ALLOWUNDO; no extra package dependency) ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOERRORUI = 0x400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW op);

    /// <summary>
    /// Moves a directory to the Recycle Bin (recoverable). Only call after the caller has confirmed
    /// the folder is a journal-recognizable scattered sync folder and the user explicitly agreed.
    /// </summary>
    public static void CleanupToRecycleBin(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }

        var op = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = folder + "\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
        };

        int rc = SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
        {
            throw new IOException($"Failed to move folder to the Recycle Bin (code {rc}).");
        }
    }
}
