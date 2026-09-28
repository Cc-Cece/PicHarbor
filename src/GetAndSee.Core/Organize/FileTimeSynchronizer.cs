using GetAndSee.Core.Journal;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Organize;

/// <summary>
/// Progress snapshot reported during batch EXIF file time synchronization.
/// </summary>
/// <param name="Processed">Number of manifest rows processed so far.</param>
/// <param name="Total">Total number of rows in the archive manifest.</param>
/// <param name="Updated">Number of local files whose timestamps were successfully updated.</param>
public readonly record struct FileTimeSyncProgress(int Processed, int Total, int Updated);

/// <summary>
/// Synchronizes local Windows destination file modification times (LastWriteTime) and optionally creation times
/// (CreationTime) with EXIF capture dates (<c>exif_datetime_original</c> / <c>CapturedAt</c>).
/// </summary>
public static class FileTimeSynchronizer
{
    /// <summary>
    /// Applies an EXIF capture timestamp to a local file's Windows file attributes.
    /// Strictly read-only on source media — only alters the local destination file attributes.
    /// </summary>
    /// <param name="localFilePath">Absolute path to the local destination file.</param>
    /// <param name="capturedAt">The capture date/time extracted from EXIF.</param>
    /// <param name="syncCreationTime">When <see langword="true"/>, also sets CreationTimeUtc in addition to LastWriteTimeUtc.</param>
    /// <returns><see langword="true"/> if timestamps were updated; otherwise <see langword="false"/>.</returns>
    public static bool TrySyncFileTime(string localFilePath, DateTimeOffset capturedAt, bool syncCreationTime = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        string extendedPath = LongPath.ToExtended(localFilePath);
        if (!File.Exists(extendedPath))
        {
            return false;
        }

        try
        {
            DateTime utcTime = capturedAt.UtcDateTime;
            File.SetLastWriteTimeUtc(extendedPath, utcTime);
            if (syncCreationTime)
            {
                File.SetCreationTimeUtc(extendedPath, utcTime);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Scans an existing archive database manifest and updates all completed local files' timestamps.
    /// </summary>
    /// <param name="destinationRoot">The root folder of an existing get-and-see archive.</param>
    /// <param name="syncCreationTime">When <see langword="true"/>, also sets CreationTimeUtc.</param>
    /// <param name="progress">Optional progress reporter receiving progress snapshots.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>Total number of files whose timestamps were updated.</returns>
    public static Task<int> BatchSyncArchiveFileTimesAsync(
        string destinationRoot,
        bool syncCreationTime = false,
        IProgress<FileTimeSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var journal = TransferJournal.OpenReadOnly(destinationRoot);
            IReadOnlyList<ManifestSearchRow> rows = journal.ReadSearchRows();

            int total = rows.Count;
            int processed = 0;
            int updatedCount = 0;

            progress?.Report(new FileTimeSyncProgress(0, total, 0));

            foreach (ManifestSearchRow row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                processed++;
                try
                {
                    if (row.CapturedAt is DateTimeOffset capturedAt && !string.IsNullOrWhiteSpace(row.RelativePath))
                    {
                        string fullPath = Path.Combine(destinationRoot, row.RelativePath);
                        if (TrySyncFileTime(fullPath, capturedAt, syncCreationTime))
                        {
                            updatedCount++;
                        }
                    }
                }
                catch
                {
                    // Safe fallthrough for individual unreadable or missing file paths
                }

                if (processed % 10 == 0 || processed == total)
                {
                    progress?.Report(new FileTimeSyncProgress(processed, total, updatedCount));
                }
            }

            return updatedCount;
        }, cancellationToken);
    }
}
