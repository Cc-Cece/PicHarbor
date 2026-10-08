using System.Globalization;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Android;

/// <summary>
/// Configuration parameters for incremental FTP synchronization to an Android device.
/// </summary>
public sealed class AndroidSyncConfig
{
    /// <summary>Target Android device name (e.g. "Pixel 8").</summary>
    public string DeviceName { get; set; } = "Android Device";

    /// <summary>Target Android device ID. Generated or read via device probe.</summary>
    public string ConfiguredDeviceId { get; set; } = "";

    /// <summary>Remote FTP server IP host address.</summary>
    public string FtpHost { get; set; } = "192.168.1.100";

    /// <summary>Remote FTP server port.</summary>
    public int FtpPort { get; set; } = 2121;

    /// <summary>FTP authentication username.</summary>
    public string FtpUser { get; set; } = "anonymous";

    /// <summary>FTP authentication password.</summary>
    public string FtpPassword { get; set; } = "";

    /// <summary>Target directory on Android remote storage (e.g. "/DCIM/PicHarbor/").</summary>
    public string RemoteTargetDir { get; set; } = "/DCIM/PicHarbor/";

    /// <summary>Android restore mode (Default or HistoricalIncremental).</summary>
    public AndroidRestoreMode RestoreMode { get; set; } = AndroidRestoreMode.Default;

    /// <summary>Restore scope mode (All, DateRange, Subfolder, ManualSelection).</summary>
    public IPhoneRestoreScopeMode ScopeMode { get; set; } = IPhoneRestoreScopeMode.All;

    /// <summary>Start date threshold when <see cref="ScopeMode"/> is DateRange.</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>End date threshold when <see cref="ScopeMode"/> is DateRange.</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>Set of selected subfolder paths when <see cref="ScopeMode"/> is Subfolder.</summary>
    public HashSet<string>? SelectedSubfolders { get; set; }

    /// <summary>Set of manually selected destination paths when <see cref="ScopeMode"/> is ManualSelection.</summary>
    public HashSet<string>? ManualSelectedPaths { get; set; }

    /// <summary>
    /// Determines whether the specified manifest entry should be included for sync under the current <see cref="ScopeMode"/>.
    /// </summary>
    public bool IsEntryIncluded(ManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return ScopeMode switch
        {
            IPhoneRestoreScopeMode.All => true,

            IPhoneRestoreScopeMode.DateRange => MatchesDateRange(entry),

            IPhoneRestoreScopeMode.Subfolder => MatchesSubfolder(entry),

            IPhoneRestoreScopeMode.ManualSelection => ManualSelectedPaths is not null &&
                                                        (ManualSelectedPaths.Contains(entry.DestPath) || ManualSelectedPaths.Contains(entry.DestPath.Replace('\\', '/')) || ManualSelectedPaths.Contains(entry.DestPath.Replace('/', '\\'))),

            _ => true
        };
    }

    private bool MatchesDateRange(ManifestEntry entry)
    {
        DateTimeOffset? captureDate = ParseCaptureDate(entry);
        if (!captureDate.HasValue) return true;

        DateTime dt = captureDate.Value.LocalDateTime;
        if (DateFrom.HasValue && dt.Date < DateFrom.Value.Date) return false;
        if (DateTo.HasValue && dt.Date > DateTo.Value.Date) return false;

        return true;
    }

    private bool MatchesSubfolder(ManifestEntry entry)
    {
        if (SelectedSubfolders is null || SelectedSubfolders.Count == 0) return true;

        string normalizedDest = entry.DestPath.Replace('\\', '/');
        string dir = Path.GetDirectoryName(normalizedDest)?.Replace('\\', '/') ?? string.Empty;

        return SelectedSubfolders.Contains(dir) || SelectedSubfolders.Any(s => normalizedDest.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
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
}
