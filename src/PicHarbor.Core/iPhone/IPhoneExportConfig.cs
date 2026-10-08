using System.Globalization;
using PicHarbor.Core.Journal;

namespace PicHarbor.Core.iPhone;

/// <summary>
/// Configuration parameters for incremental export to the dedicated Apple Sync folder.
/// </summary>
public sealed class IPhoneExportConfig
{
    /// <summary>Target iPhone device model/identifier (e.g. "iPhone 15 Pro" or "iPhone").</summary>
    public string DeviceModel { get; set; } = "iPhone";

    /// <summary>
    /// Custom export folder path override. When null or empty, defaults to
    /// <c>&lt;pcDestinationRoot&gt;/.AppleSync/&lt;DeviceModel&gt;/</c>.
    /// </summary>
    public string? CustomSyncFolder { get; set; }

    /// <summary>Album organization mode (YearMonth or Flat).</summary>
    public IPhoneAlbumMode AlbumMode { get; set; } = IPhoneAlbumMode.YearMonth;

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
    /// Computes the effective export folder path based on <see cref="CustomSyncFolder"/> or the default
    /// <c>&lt;pcDestinationRoot&gt;/.AppleSync/&lt;DeviceModel&gt;/</c> pattern.
    /// </summary>
    public string GetEffectiveExportPath(string pcDestinationRoot)
    {
        if (!string.IsNullOrWhiteSpace(CustomSyncFolder))
        {
            return CustomSyncFolder;
        }

        string sanitizedModel = string.IsNullOrWhiteSpace(DeviceModel) ? "iPhone" : DeviceModel;
        return Path.Combine(pcDestinationRoot, ".AppleSync", sanitizedModel);
    }

    /// <summary>
    /// Determines whether the specified manifest entry should be included for export under the current <see cref="ScopeMode"/>.
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
