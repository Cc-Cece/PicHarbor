using System.Globalization;
using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Scope;

/// <summary>
/// Policy for manifest entries that carry no capture date (neither EXIF DateTimeOriginal
/// nor source file mtime). Only takes effect when a date filter is active.
/// </summary>
public enum UndatedFilePolicy
{
    /// <summary>Include undated entries when a date filter is active (default; conservative).</summary>
    Include = 0,

    /// <summary>Exclude undated entries when a date filter is active.</summary>
    Skip = 1
}

/// <summary>
/// Unified combination filter shared by all three restore/upload ends (iPhone, Android, Google Photos).
/// Semantics: subfolder filter AND date filter (empty/null = filter disabled); undated entries follow
/// <see cref="UndatedPolicy"/> when the date filter is active. An all-empty instance is equivalent to
/// "include everything", so callers can always AND it onto a base scope (All / ManualSelection).
/// </summary>
public sealed class ScopeFilterSet
{
    /// <summary>
    /// Selected subfolder paths (relative directories, normalized with '/'; may be deep paths like
    /// "2024/10-Paris"). Null or empty means the subfolder filter is disabled.
    /// </summary>
    public HashSet<string>? SelectedSubfolders { get; set; }

    /// <summary>Start date (inclusive, day granularity). Null means the date filter is disabled.</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>End date (inclusive, day granularity). Null means the date filter is disabled.</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>How to treat entries without capture date while the date filter is active.</summary>
    public UndatedFilePolicy UndatedPolicy { get; set; } = UndatedFilePolicy.Include;

    /// <summary>Gets a value indicating whether the subfolder filter is enabled (non-empty selection).</summary>
    public bool HasSubfolderFilter => SelectedSubfolders is { Count: > 0 };

    /// <summary>Gets a value indicating whether the date filter is enabled (any bound present).</summary>
    public bool HasDateFilter => DateFrom is not null || DateTo is not null;

    /// <summary>
    /// Combined evaluation: base scope is NOT handled here (caller applies All / ManualSelection first);
    /// this applies subfolder filter AND date filter. Disabled filters pass everything through.
    /// </summary>
    public bool IsEntryIncluded(ManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (HasSubfolderFilter && !MatchSubfolder(entry.DestPath, SelectedSubfolders!))
        {
            return false;
        }

        if (HasDateFilter)
        {
            DateTimeOffset? capture = ParseCaptureDate(entry);
            if (!capture.HasValue)
            {
                return UndatedPolicy == UndatedFilePolicy.Include;
            }

            DateTime day = capture.Value.LocalDateTime.Date;
            if (DateFrom.HasValue && day < DateFrom.Value.Date) return false;
            if (DateTo.HasValue && day > DateTo.Value.Date) return false;
        }

        return true;
    }

    /// <summary>
    /// Subfolder matcher shared by all ends. <paramref name="destPath"/> is normalized to '/' separators;
    /// the entry's directory must equal a selection, or lie under one (prefix with a '/' boundary so that
    /// "2024-08" never matches "2024-08x"). Empty/null selection always matches (filter disabled).
    /// Matching is OrdinalIgnoreCase.
    /// </summary>
    public static bool MatchSubfolder(string destPath, IReadOnlySet<string>? selected)
    {
        if (selected is null || selected.Count == 0) return true;

        string normalizedDest = destPath.Replace('\\', '/');
        string dir = Path.GetDirectoryName(normalizedDest)?.Replace('\\', '/') ?? string.Empty;

        if (selected.Contains(dir)) return true;
        return selected.Any(s => !string.IsNullOrWhiteSpace(s) && normalizedDest.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Capture date resolution shared by all ends: EXIF <c>ExifDateTimeOriginalIso</c> first,
    /// then source file mtime <c>SourceMtimeIso</c> (AssumeUniversal), otherwise null.
    /// </summary>
    public static DateTimeOffset? ParseCaptureDate(ManifestEntry entry)
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
