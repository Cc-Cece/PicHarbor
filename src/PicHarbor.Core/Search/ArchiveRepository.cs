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
public sealed record ArchiveSummaryStats(
    int TotalFiles,
    long TotalBytes,
    int PhotosCount,
    int VideosCount,
    int ScreenshotsCount,
    int OtherCount,
    DateTimeOffset? LastBackupTime,
    IReadOnlyList<DeviceRecord> Devices);

/// <summary>
/// High-level read-only API helper for querying existing PicHarbor archives.
/// Safely wraps <see cref="TransferJournal"/>, <see cref="MediaSearch"/>, and <see cref="Reorganizer"/> for GUI and tools.
/// </summary>
public static class ArchiveRepository
{
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
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            IReadOnlyList<ManifestSearchRow> rows = journal.ReadSearchRows();
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
            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            IReadOnlyList<ManifestSearchRow> rows = journal.ReadSearchRows();
            IReadOnlyList<DeviceRecord> devices = journal.ReadDevices();
            RunsSummary runsSummary = journal.ReadRunsSummary();

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

            return new ArchiveSummaryStats(
                totalFiles,
                totalBytes,
                photos,
                videos,
                screenshots,
                other,
                runsSummary.LatestRunAt,
                devices);
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
            var reorganizer = new Reorganizer(journal, new DateFolderOrganizer(), destinationRoot);
            return reorganizer.Plan(targetScheme);
        }, cancellationToken);
    }
}
