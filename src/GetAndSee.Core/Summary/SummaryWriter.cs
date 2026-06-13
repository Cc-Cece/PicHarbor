using System.Globalization;
using System.Text;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Summary;

/// <summary>
/// Generates the human-readable <c>&lt;dest&gt;/summary.txt</c> from the manifest and the run's
/// statistics (brainstorm Session 3). Regenerated at the end of every run.
/// </summary>
public sealed class SummaryWriter
{
    /// <summary>The summary filename written at the destination root.</summary>
    public const string FileName = "summary.txt";

    private static readonly HashSet<string> PhotoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".jpg", ".jpeg", ".dng", ".tiff", ".tif", ".gif", ".webp" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4", ".m4v", ".avi" };

    /// <summary>Writes <c>summary.txt</c> to the destination root.</summary>
    /// <param name="destinationRoot">Destination root directory.</param>
    /// <param name="manifest">Completed-file rows from the journal.</param>
    /// <param name="devices">Devices recorded in the journal.</param>
    /// <param name="runs">Run-history summary from the journal.</param>
    /// <param name="generatedAt">UTC timestamp to stamp the file with.</param>
    /// <param name="flushToDisk">
    /// When <see langword="true"/>, fsync the file before returning. Used by the disconnect escape-hatch
    /// (#45), which hard-terminates the process immediately after, so the bytes must be on disk rather than
    /// only in the OS write cache. The normal end-of-run write leaves this <see langword="false"/> and is
    /// unchanged.
    /// </param>
    public void Write(
        string destinationRoot,
        IReadOnlyList<ManifestEntry> manifest,
        IReadOnlyList<DeviceRecord> devices,
        RunsSummary runs,
        DateTimeOffset generatedAt,
        bool flushToDisk = false)
    {
        string content = Build(destinationRoot, manifest, devices, runs, generatedAt);
        // Prefix the write path with \\?\ so summary.txt is written even when the destination root is
        // deep enough that <dest>/summary.txt exceeds MAX_PATH (R6 / #39). The destination shown inside
        // the text (set in Build) stays the clean, user-facing form.
        string path = LongPath.ToExtended(Path.Combine(destinationRoot, FileName));
        if (!flushToDisk)
        {
            File.WriteAllText(path, content);
            return;
        }

        // Durable write for the escape-hatch's imminent hard terminate: UTF-8 without a BOM (matching
        // File.WriteAllText), then fsync so summary.txt survives the abrupt process kill.
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Builds the summary text without writing it (used by <c>status</c> and tests).</summary>
    /// <param name="destinationRoot">Destination root directory.</param>
    /// <param name="manifest">Completed-file rows from the journal.</param>
    /// <param name="devices">Devices recorded in the journal.</param>
    /// <param name="runs">Run-history summary from the journal.</param>
    /// <param name="generatedAt">UTC timestamp to stamp the file with.</param>
    /// <returns>The full summary text.</returns>
    public string Build(
        string destinationRoot,
        IReadOnlyList<ManifestEntry> manifest,
        IReadOnlyList<DeviceRecord> devices,
        RunsSummary runs,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(runs);

        long totalBytes = 0;
        int photos = 0, videos = 0, screenshots = 0, other = 0, heic = 0, jpg = 0, mov = 0;
        DateTime? minDate = null;
        DateTime? maxDate = null;
        var destPaths = new List<string>(manifest.Count);

        foreach (ManifestEntry entry in manifest)
        {
            totalBytes += entry.SizeBytes;
            destPaths.Add(entry.DestPath);
            string extension = Path.GetExtension(entry.DestPath);
            switch (CategoryOf(extension))
            {
                case Category.Photo:
                    photos++;
                    if (extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".heif", StringComparison.OrdinalIgnoreCase))
                    {
                        heic++;
                    }
                    else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        jpg++;
                    }

                    break;
                case Category.Video:
                    videos++;
                    if (extension.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                    {
                        mov++;
                    }

                    break;
                case Category.Screenshot:
                    screenshots++;
                    break;
                default:
                    other++;
                    break;
            }

            if (TryParseDate(entry.ExifDateTimeOriginalIso ?? entry.SourceMtimeIso, out DateTime date))
            {
                minDate = minDate is null || date < minDate ? date : minDate;
                maxDate = maxDate is null || date > maxDate ? date : maxDate;
            }
        }

        var builder = new StringBuilder();
        builder.Append("get-and-see archive at ").AppendLine(destinationRoot);
        builder.Append("Last updated: ")
            .Append(generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .AppendLine(" UTC");
        builder.AppendLine();

        builder.Append("Total: ").Append(Num(manifest.Count)).Append(" files (")
            .Append(ByteSize.Humanize(totalBytes)).AppendLine(")");
        if (photos > 0)
        {
            builder.Append("  Photos:      ").Append(Num(photos)).AppendLine(PhotoBreakdown(heic, jpg));
        }

        if (videos > 0)
        {
            builder.Append("  Videos:      ").Append(Num(videos)).AppendLine(mov > 0 ? $"  (MOV: {Num(mov)})" : string.Empty);
        }

        int livePhotoPairs = LivePhotoDetector.FindPairs(destPaths).Count;
        if (livePhotoPairs > 0)
        {
            string pairWord = livePhotoPairs == 1 ? " pair" : " pairs";
            builder.Append("  Live Photos: ").Append(Num(livePhotoPairs)).AppendLine(pairWord);
        }

        if (screenshots > 0)
        {
            builder.Append("  Screenshots: ").AppendLine(Num(screenshots));
        }

        if (other > 0)
        {
            builder.Append("  Other:       ").AppendLine(Num(other));
        }

        builder.AppendLine();
        if (minDate is not null && maxDate is not null)
        {
            builder.Append("Date range: ")
                .Append(minDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(" to ")
                .AppendLine(maxDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        string? deviceLine = DescribeDevices(devices);
        if (deviceLine is not null)
        {
            builder.Append("Devices: ").AppendLine(deviceLine);
        }

        if (runs.TotalRuns > 0)
        {
            builder.Append("Runs: ").Append(Num(runs.TotalRuns));
            if (runs.FirstRunAt is not null && runs.LatestRunAt is not null)
            {
                builder.Append(" (first run ")
                    .Append(runs.FirstRunAt.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(", latest ")
                    .Append(runs.LatestRunAt.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Append(')');
            }

            builder.AppendLine();
        }

        if (runs.TotalRuns > 0)
        {
            builder.AppendLine();
            builder.Append("Last run: ")
                .Append(Num(runs.LastCopied)).Append(" copied · ")
                .Append(Num(runs.LastSkipped)).Append(" skipped (already done) · ")
                .Append(Num(runs.LastFailed)).Append(" failed · ")
                .AppendLine(FormatDuration(runs.LastElapsed));
        }

        return builder.ToString();
    }

    private static string PhotoBreakdown(int heic, int jpg)
    {
        if (heic > 0 && jpg > 0)
        {
            return $"  (HEIC: {Num(heic)} · JPG: {Num(jpg)})";
        }

        if (heic > 0)
        {
            return $"  (HEIC: {Num(heic)})";
        }

        return jpg > 0 ? $"  (JPG: {Num(jpg)})" : string.Empty;
    }

    private static string? DescribeDevices(IReadOnlyList<DeviceRecord> devices)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        return string.Join(", ", devices.Select(DescribeDevice));
    }

    private static string DescribeDevice(DeviceRecord device)
    {
        if (!string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(device.Model))
        {
            return $"{device.Name} ({device.Model})";
        }

        return device.Name ?? device.Model ?? device.Udid;
    }

    private static Category CategoryOf(string extension)
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return Category.Screenshot;
        }

        if (PhotoExtensions.Contains(extension))
        {
            return Category.Photo;
        }

        return VideoExtensions.Contains(extension) ? Category.Video : Category.Other;
    }

    private static bool TryParseDate(string? iso, out DateTime date)
    {
        if (!string.IsNullOrEmpty(iso) &&
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }

        return elapsed.TotalMinutes >= 1 ? $"{elapsed.Minutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private enum Category
    {
        Photo,
        Video,
        Screenshot,
        Other,
    }
}
