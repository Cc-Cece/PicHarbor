using System.Globalization;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Scope;

namespace PicHarbor.Core.Android;

/// <summary>
/// Configuration parameters for incremental FTP synchronization to an Android device.
/// </summary>
public sealed class AndroidSyncConfig
{
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
    /// Combined filter set (subfolder AND date, day granularity). When non-null, combination semantics
    /// are enabled: <see cref="ScopeMode"/> acts as the base set (All passes everything,
    /// ManualSelection restricts to <see cref="ManualSelectedPaths"/>) and the filters are ANDed on top.
    /// When null, the legacy exclusive <see cref="ScopeMode"/> switch is used for backward compatibility.
    /// </summary>
    public ScopeFilterSet? Filters { get; set; }

    /// <summary>
    /// Determines whether the specified manifest entry should be included for sync under the current
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
