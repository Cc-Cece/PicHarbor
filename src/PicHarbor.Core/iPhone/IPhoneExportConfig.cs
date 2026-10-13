using System.Globalization;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Scope;

namespace PicHarbor.Core.iPhone;

/// <summary>
/// Configuration parameters for incremental export to the dedicated Apple Sync folder.
/// </summary>
public sealed class IPhoneExportConfig
{
    /// <summary>Target iPhone device model/identifier (e.g. "iPhone 15 Pro" or "iPhone").</summary>
    public string DeviceModel { get; set; } = "iPhone";

    /// <summary>Device UDID (unique identifier) when known; used to key journal records.</summary>
    public string? DeviceUdid { get; set; }

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
    /// Combined filter set (subfolder AND date, day granularity). When non-null, combination semantics
    /// are enabled: <see cref="ScopeMode"/> acts as the base set (All passes everything,
    /// ManualSelection restricts to <see cref="ManualSelectedPaths"/>) and the filters are ANDed on top.
    /// When null, the legacy exclusive <see cref="ScopeMode"/> switch is used for backward compatibility.
    /// </summary>
    public ScopeFilterSet? Filters { get; set; }

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
    /// Determines whether the specified manifest entry should be included for export under the current
    /// configuration. When <see cref="Filters"/> is set, combination semantics apply (base scope ANDed
    /// with filters); otherwise the legacy exclusive <see cref="ScopeMode"/> switch is evaluated.
    /// </summary>
    public bool IsEntryIncluded(ManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (Filters is not null)
        {
            if (ScopeMode == IPhoneRestoreScopeMode.ManualSelection &&
                (ManualSelectedPaths is null ||
                 !(ManualSelectedPaths.Contains(entry.SelectionKey) || ManualSelectedPaths.Contains(entry.DestPath) ||
                   ManualSelectedPaths.Contains(entry.DestPath.Replace('\\', '/')) ||
                   ManualSelectedPaths.Contains(entry.DestPath.Replace('/', '\\')))))
            {
                return false;
            }

            return Filters.IsEntryIncluded(entry);
        }

        return ScopeMode switch
        {
            IPhoneRestoreScopeMode.All => true,

            IPhoneRestoreScopeMode.DateRange => MatchesDateRange(entry),

            IPhoneRestoreScopeMode.Subfolder => MatchesSubfolder(entry),

            IPhoneRestoreScopeMode.ManualSelection => ManualSelectedPaths is not null &&
                                                        (ManualSelectedPaths.Contains(entry.SelectionKey) || ManualSelectedPaths.Contains(entry.DestPath) || ManualSelectedPaths.Contains(entry.DestPath.Replace('\\', '/')) || ManualSelectedPaths.Contains(entry.DestPath.Replace('/', '\\'))),

            _ => true
        };
    }

    private bool MatchesDateRange(ManifestEntry entry)
    {
        DateTimeOffset? captureDate = ScopeFilterSet.ParseCaptureDate(entry);
        if (!captureDate.HasValue) return true;

        DateTime dt = captureDate.Value.LocalDateTime;
        if (DateFrom.HasValue && dt.Date < DateFrom.Value.Date) return false;
        if (DateTo.HasValue && dt.Date > DateTo.Value.Date) return false;

        return true;
    }

    private bool MatchesSubfolder(ManifestEntry entry)
    {
        return ScopeFilterSet.MatchSubfolder(entry.DestPath, SelectedSubfolders);
    }
}
