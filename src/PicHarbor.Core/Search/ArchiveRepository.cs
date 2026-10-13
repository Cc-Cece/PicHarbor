using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Reorganize;
using PicHarbor.Core.Summary;

namespace PicHarbor.Core.Search;

/// <summary>
/// Summary information for an archive repository.
/// </summary>
/// <param name="TotalFiles">Total completed media files.</param>
/// <param name="TotalBytes">Total bytes across all completed files.</param>
/// <param name="PhotosCount">Count of photo media files.</param>
/// <param name="VideosCount">Count of video media files.</param>
/// <param name="ScreenshotsCount">Count of screenshot media files.</param>
/// <param name="OtherCount">Count of other media files.</param>
/// <param name="LastBackupTime">Timestamp of the most recent backup run, if any.</param>
/// <param name="Devices">History of connected devices recorded in this archive.</param>
/// <param name="ItemCount">Displayed items. A live pair (still plus motion clip) counts as one.</param>
public sealed record ArchiveSummaryStats(
    int TotalFiles,
    long TotalBytes,
    int PhotosCount,
    int VideosCount,
    int ScreenshotsCount,
    int OtherCount,
    DateTimeOffset? LastBackupTime,
    IReadOnlyList<DeviceRecord> Devices,
    int ItemCount);

/// <summary>
/// High-level read-only API helper for querying existing PicHarbor archives.
/// Safely wraps <see cref="TransferJournal"/>, <see cref="MediaSearch"/>, and <see cref="Reorganizer"/> for GUI and tools.
/// </summary>
public static class ArchiveRepository
{
    /// <summary>Reads only registered members; legacy archives remain readable without migration.</summary>
    public static IReadOnlyList<ManifestSearchRow> ReadLibrarySearchRows(string destinationRoot)
    {
        if (File.Exists(Path.Combine(destinationRoot,".picharbor","library.json")) || PicHarbor.Core.Storage.LibraryCatalog.HasCachedCatalog(destinationRoot))
            return new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot).ReadSearchRows();
        using var journal = TransferJournal.OpenReadOnly(destinationRoot);
        return journal.ReadSearchRows();
    }

    /// <summary>Reads all registered logical media, including unavailable locations for accurate reporting.</summary>
    public static IReadOnlyList<ManifestEntry> ReadLibraryManifest(string destinationRoot)
    {
        if (!File.Exists(Path.Combine(destinationRoot,".picharbor","library.json")) && !PicHarbor.Core.Storage.LibraryCatalog.HasCachedCatalog(destinationRoot))
        {
            using var journal=TransferJournal.OpenReadOnly(destinationRoot);
            return journal.ReadManifest();
        }
        var catalog = new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot);
        return catalog.ReadLogicalMedia()
            .Select(m => new ManifestEntry(m.RelativePath,m.Size,m.CapturedAt?.ToString("O"),null)
            { ArchiveRoot=m.Root, MediaId=m.Sha256, Availability=m.State,RelatedMediaIds=m.Sha256 is null ? [] : catalog.RelatedMedia(m.Sha256) }).ToList();
    }

    /// <summary>Resolves stable selections and unambiguous legacy paths without conflating member disks.</summary>
    public static Dictionary<string,ManifestEntry> BuildSelectionMap(IEnumerable<ManifestEntry> entries, string? preferredRoot = null)
    {
        var result=new Dictionary<string,ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var all=entries.ToList();
        foreach(var entry in all) result[entry.SelectionKey]=entry;
        foreach(var group in all.GroupBy(e=>e.DestPath.Replace('\\','/'),StringComparer.OrdinalIgnoreCase))
        {
            var local=group.FirstOrDefault(e=>e.ArchiveRoot is null || string.Equals(e.ArchiveRoot,preferredRoot,StringComparison.OrdinalIgnoreCase));
            if(local is not null) result[group.Key]=local;
            else if(group.Count()==1) result[group.Key]=group.First();
        }
        return result;
    }
    /// <summary>
    /// Searches an existing archive manifest by criteria asynchronously.
    /// </summary>
    public static Task<IReadOnlyList<MediaSearchHit>> SearchAsync(
        string destinationRoot,
        MediaSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(criteria);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ManifestSearchRow> rows = ReadLibrarySearchRows(destinationRoot);
            return MediaSearch.Find(rows, criteria);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads archive summary statistics (total files, size, photos vs videos breakdown, connected devices).
    /// </summary>
    public static Task<ArchiveSummaryStats> GetStatsAsync(
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ManifestSearchRow> rows = ReadLibrarySearchRows(destinationRoot);
            IReadOnlyList<DeviceRecord> devices;
            RunsSummary runsSummary;
            if(File.Exists(Path.Combine(destinationRoot,".picharbor","library.json")) || PicHarbor.Core.Storage.LibraryCatalog.HasCachedCatalog(destinationRoot))
            {
                var catalog=new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot);catalog.ReadMedia();
                devices=catalog.ReadDevices();runsSummary=catalog.ReadRunsSummary();
            }
            else
            {
                using var journal=TransferJournal.OpenReadOnly(destinationRoot);
                devices=journal.ReadDevices();runsSummary=journal.ReadRunsSummary();
            }

            int totalFiles = rows.Count;
            long totalBytes = 0;
            int photos = 0, videos = 0, screenshots = 0, other = 0;

            foreach (ManifestSearchRow row in rows)
            {
                totalBytes += row.SizeBytes;
                MediaType type = MediaTypeClassifier.Classify(row.RelativePath);
                switch (type)
                {
                    case MediaType.Photo: photos++; break;
                    case MediaType.Video: videos++; break;
                    case MediaType.Screenshot: screenshots++; break;
                    default: other++; break;
                }
            }

            int itemCount = rows.Count-LivePhotoDetector.FindLibraryPairs(rows).Select(p=>p.Video).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return new ArchiveSummaryStats(
                totalFiles,
                totalBytes,
                photos,
                videos,
                screenshots,
                other,
                runsSummary.LatestRunAt,
                devices,
                itemCount);
        }, cancellationToken);
    }

    /// <summary>
    /// Previews reorganizing an archive layout (dry run) without making any changes to disk.
    /// </summary>
    public static Task<ReorganizePlan> PreviewReorganizeAsync(
        string destinationRoot,
        OrganizeScheme targetScheme,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            if(File.Exists(Path.Combine(destinationRoot,".picharbor","library.json"))) return LibraryReorganizer.Preview(destinationRoot,targetScheme);
            var reorganizer = new Reorganizer(journal, new DateFolderOrganizer(), destinationRoot);
            return reorganizer.Plan(targetScheme);
        }, cancellationToken);
    }

    /// <summary>Reads database physical storage metrics and record statistics.</summary>
    public static Task<DatabaseMetricsRecord> GetDatabaseMetricsAsync(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.GetDatabaseMetrics();
        });
    }

    /// <summary>Reads all backup session batches for a specific device.</summary>
    public static Task<IReadOnlyList<BackupSessionRecord>> GetBackupSessionsAsync(string destinationRoot, string deviceUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        return Task.Run(() =>
        {
            if(File.Exists(TransferJournal.ResolveDatabasePath(destinationRoot)))
                using (var rw = TransferJournal.Open(destinationRoot)) rw.BackfillLegacySessionsIfEmpty();
            if(File.Exists(Path.Combine(destinationRoot,".picharbor","library.json")) || PicHarbor.Core.Storage.LibraryCatalog.HasCachedCatalog(destinationRoot))
            {
                var catalog=new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot);catalog.ReadMedia();
                return catalog.ReadBackupSessions(deviceUid);
            }
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.ReadBackupSessions(deviceUid);
        });
    }

    /// <summary>Reads file transfer items belonging to a backup session batch.</summary>
    public static Task<IReadOnlyList<BackupHistoryItemRecord>> GetBackupHistoryItemsAsync(string destinationRoot, long sessionId, string? storageId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return Task.Run(() =>
        {
            if(storageId is not null)
            {
                var catalog=new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot);catalog.ReadMedia();
                return catalog.ReadBackupHistory(storageId,sessionId);
            }
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.ReadBackupHistoryItems(sessionId);
        });
    }

    /// <summary>Reads aggregated totals (sessions, files, bytes) for a device.</summary>
    public static Task<(int TotalSessions, int TotalFiles, long TotalBytes)> GetDeviceBackupSummaryAsync(string destinationRoot, string deviceUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        return Task.Run(() =>
        {
            if(File.Exists(TransferJournal.ResolveDatabasePath(destinationRoot)))
                using (var rw = TransferJournal.Open(destinationRoot)) rw.BackfillLegacySessionsIfEmpty();
            if(File.Exists(Path.Combine(destinationRoot,".picharbor","library.json")) || PicHarbor.Core.Storage.LibraryCatalog.HasCachedCatalog(destinationRoot))
            {
                var catalog=new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot);catalog.ReadMedia();
                var sessions=catalog.ReadBackupSessions(deviceUid);
                return (sessions.Count,sessions.Sum(s=>s.FilesCount),sessions.Sum(s=>s.TotalSizeBytes));
            }
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.ReadDeviceBackupSummary(deviceUid);
        });
    }

    /// <summary>
    /// 仅更新既有设备的硬件序列号（人工修正自动识别结果），不改名、不新建记录。
    /// </summary>
    public static Task<bool> UpdateDeviceHardwareSerialAsync(
        string destinationRoot,
        string deviceUid,
        string? hardwareSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            return journal.UpdateDeviceHardwareSerial(deviceUid, hardwareSerial);
        });
    }

    /// <summary>
    /// 判断该硬件序列号是否已被归档库中<b>其它</b>设备占用（唯一性校验）。
    /// </summary>
    public static Task<bool> IsHardwareSerialTakenByOtherAsync(
        string destinationRoot,
        string hardwareSerial,
        string? excludeUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareSerial);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.IsHardwareSerialTakenByOther(hardwareSerial, excludeUid);
        });
    }

    /// <summary>
    /// 将既有设备的标识迁移为新值，保留其全部备份历史（用于重新识别后修复旧记录）。
    /// </summary>
    public static Task<bool> MigrateDeviceUdidAsync(
        string destinationRoot,
        string oldDeviceUid,
        string newDeviceUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDeviceUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDeviceUid);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            return journal.MigrateDeviceUdid(oldDeviceUid, newDeviceUid);
        });
    }

    /// <summary>读取设备的显示名称；设备不存在时返回 <c>null</c>。</summary>
    public static Task<string?> GetDeviceNameAsync(string destinationRoot, string deviceUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.GetDeviceName(deviceUid);
        });
    }

    /// <summary>
    /// 更新设备显示名（重命名）。只改显示名，不触碰归档目录名；与其它设备重名时返回 <c>false</c>。
    /// </summary>
    public static Task<bool> UpdateDeviceNameAsync(string destinationRoot, string deviceUid, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            return journal.UpdateDeviceName(deviceUid, name);
        });
    }

    /// <summary>判断某个显示名称是否已被归档库中其它设备占用。</summary>
    public static Task<bool> IsDeviceNameTakenByOtherAsync(string destinationRoot, string name, string? excludeUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.IsDeviceNameTakenByOther(name, excludeUid);
        });
    }

    /// <summary>读取设备的归档文件夹名；未记录时返回 <c>null</c>。</summary>
    public static Task<string?> GetDeviceArchiveFolderAsync(string destinationRoot, string deviceUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.GetDeviceArchiveFolder(deviceUid);
        });
    }

    /// <summary>记录设备的归档文件夹名。</summary>
    public static Task<bool> SetDeviceArchiveFolderAsync(string destinationRoot, string deviceUid, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            return journal.SetDeviceArchiveFolder(deviceUid, folder);
        });
    }

    /// <summary>判断某个归档文件夹名是否已被归档库中其它设备占用。</summary>
    public static Task<bool> IsDeviceArchiveFolderTakenByOtherAsync(string destinationRoot, string folder, string? excludeUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.IsDeviceArchiveFolderTakenByOther(folder, excludeUid);
        });
    }

    /// <summary>从该设备已备份文件的相对路径推导归档文件夹名（取路径第一段）。</summary>
    public static Task<string?> InferDeviceArchiveFolderFromHistoryAsync(string destinationRoot, string deviceUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            return journal.InferDeviceArchiveFolderFromHistory(deviceUid);
        });
    }

    /// <summary>Prunes redundant audit history data older than cutoff time and vacuums database.</summary>
    public static Task<(int PrunedSessions, int PrunedRecords)> PruneRedundantHistoryAsync(string destinationRoot, DateTimeOffset cutoffTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            var res = journal.PruneRedundantHistory(cutoffTime);
            journal.VacuumDatabase();
            return res;
        });
    }

    /// <summary>Runs WAL checkpoint and VACUUM to reclaim space.</summary>
    public static Task VacuumDatabaseAsync(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            journal.VacuumDatabase();
        });
    }

    /// <summary>Scans archive database against disk to safely clean ghost records.</summary>
    public static Task<(int Scanned, int Removed)> RepairDatabaseConsistencyAsync(
        string destinationRoot,
        IProgress<(int Scanned, int Removed)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        return Task.Run(() =>
        {
            using var journal = TransferJournal.Open(destinationRoot);
            var result = journal.RepairDatabaseConsistency(destinationRoot, progress);
            journal.VacuumDatabase();
            return result;
        });
    }
}
