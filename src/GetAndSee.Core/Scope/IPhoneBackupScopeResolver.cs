using System.Globalization;
using GetAndSee.Core.Device;

namespace GetAndSee.Core.Scope;

/// <summary>
/// Criteria for filtering iPhone media files during backup to PC.
/// </summary>
public sealed class IPhoneBackupScopeCriteria
{
    /// <summary>The backup scope mode (All, Date, Folder).</summary>
    public ScopeMode ScopeMode { get; set; } = ScopeMode.All;

    /// <summary>Start date threshold for Date scope filtering.</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>End date threshold for Date scope filtering.</summary>
    public DateTime? DateTo { get; set; }

    /// <summary>Set of selected subfolders/directories for Folder scope filtering.</summary>
    public IReadOnlySet<string>? SelectedSubfolders { get; set; }
}

/// <summary>
/// Resolver that filters media files scanned from iPhone based on the selected backup scope criteria,
/// while preserving Live Photo image + video pair integrity.
/// </summary>
public static class IPhoneBackupScopeResolver
{
    /// <summary>
    /// Filters <paramref name="sourceFiles"/> according to <paramref name="criteria"/>.
    /// Candidate filtering occurs before streaming file contents (selective transfer).
    /// </summary>
    /// <param name="sourceFiles">All accessible regular files enumerated from iPhone.</param>
    /// <param name="criteria">The backup scope parameters.</param>
    /// <returns>The filtered candidate list of files to transfer.</returns>
    public static List<RemoteFile> Filter(IReadOnlyList<RemoteFile> sourceFiles, IPhoneBackupScopeCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(criteria);

        if (sourceFiles.Count == 0)
        {
            return new List<RemoteFile>();
        }

        if (criteria.ScopeMode == ScopeMode.All)
        {
            return sourceFiles.ToList();
        }

        var primaryMatched = new List<RemoteFile>();
        DateTime? dateFrom = criteria.DateFrom?.Date;
        DateTime? dateTo = criteria.DateTo?.Date;
        var subfolders = criteria.SelectedSubfolders;

        foreach (var file in sourceFiles)
        {
            if (criteria.ScopeMode == ScopeMode.Date)
            {
                if (file.ModifiedAt.HasValue)
                {
                    DateTime d = file.ModifiedAt.Value.LocalDateTime.Date;
                    if (dateFrom.HasValue && d < dateFrom.Value) continue;
                    if (dateTo.HasValue && d > dateTo.Value) continue;
                }
                primaryMatched.Add(file);
            }
            else if (criteria.ScopeMode == ScopeMode.Folder)
            {
                if (subfolders is not null && subfolders.Count > 0)
                {
                    string folderName = GetSubfolderName(file.Path);
                    if (!IsFolderSelected(folderName, file.Path, subfolders))
                    {
                        continue;
                    }
                }
                primaryMatched.Add(file);
            }
            else
            {
                primaryMatched.Add(file);
            }
        }

        // Live Photo pairing preservation:
        // Group files by Directory + BaseStem key. If an image/video component of a pair is included,
        // include its companion file(s) automatically.
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in primaryMatched)
        {
            string key = GetMediaStemKey(file.Path);
            matchedKeys.Add(key);
        }

        var result = new List<RemoteFile>();
        foreach (var file in sourceFiles)
        {
            string key = GetMediaStemKey(file.Path);
            if (matchedKeys.Contains(key))
            {
                result.Add(file);
            }
        }

        return result;
    }

    /// <summary>
    /// Computes a directory + base stem key for grouping Live Photo pairs (e.g., /DCIM/100APPLE/IMG_0001).
    /// </summary>
    public static string GetMediaStemKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string normalized = path.Replace('\\', '/');
        string dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(normalized);
        return $"{dir}/{stem}";
    }

    /// <summary>
    /// Extracts the subfolder name under /DCIM/ (e.g., "100APPLE" from "/DCIM/100APPLE/IMG_0001.JPG").
    /// </summary>
    public static string GetSubfolderName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string normalized = path.Replace('\\', '/').Trim('/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("DCIM", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }
        return parts.Length > 0 ? parts[0] : normalized;
    }

    private static bool IsFolderSelected(string folderName, string path, IReadOnlySet<string> subfolders)
    {
        if (subfolders.Contains(folderName)) return true;
        if (subfolders.Any(s => s.Equals(folderName, StringComparison.OrdinalIgnoreCase))) return true;

        string normalizedPath = path.Replace('\\', '/');
        return subfolders.Any(s =>
            normalizedPath.Contains("/" + s + "/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("/" + s + "/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase));
    }
}
