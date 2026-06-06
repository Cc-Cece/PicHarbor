using GetAndSee.Core.Device;

namespace GetAndSee.Core.Organize;

/// <summary>
/// Maps a remote file to its relative destination path: <c>YYYY/YYYY-MM/&lt;name&gt;</c> when a sane
/// capture date is available, otherwise <c>unsorted/&lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// Date source preference (R16): EXIF <c>DateTimeOriginal</c> → file modified time → <c>unsorted</c>.
/// A date is only trusted if it falls within <c>[1990-01-01, now + 1 day]</c>; nonsense dates are
/// routed to <c>unsorted</c>. Filenames are sanitized so device-supplied strings can never escape
/// the destination tree (PROJECT_BRIEF §9.3).
/// </remarks>
public sealed class DateFolderOrganizer
{
    /// <summary>Folder for files with no trustworthy capture date.</summary>
    public const string UnsortedFolder = "unsorted";

    private static readonly DateTime MinSaneDate = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TimeProvider clock;

    /// <summary>Creates an organizer.</summary>
    /// <param name="clock">Time source for the upper date-sanity bound; defaults to the system clock.</param>
    public DateFolderOrganizer(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Computes the destination path for a file, relative to the destination root.
    /// </summary>
    /// <param name="file">The enumerated device file.</param>
    /// <param name="metadata">Extracted metadata, or <see langword="null"/> if none is available yet (e.g. dry-run).</param>
    /// <returns>A relative path such as <c>2024\2024-08\IMG_1234.HEIC</c> or <c>unsorted\IMG_1234.HEIC</c>.</returns>
    public string GetRelativeDestination(RemoteFile file, MediaMetadata? metadata)
    {
        string fileName = SanitizeFileName(ExtractFileName(file.Path));
        DateTime? date = ResolveDate(file, metadata);

        if (date is null)
        {
            return Path.Combine(UnsortedFolder, fileName);
        }

        string year = date.Value.Year.ToString("D4");
        string month = $"{date.Value.Year:D4}-{date.Value.Month:D2}";
        return Path.Combine(year, month, fileName);
    }

    private DateTime? ResolveDate(RemoteFile file, MediaMetadata? metadata)
    {
        if (metadata?.DateTimeOriginal is DateTime exif && IsSane(exif))
        {
            return exif;
        }

        if (file.ModifiedAt is DateTimeOffset mtime)
        {
            DateTime utc = mtime.UtcDateTime;
            if (IsSane(utc))
            {
                return utc;
            }
        }

        return null;
    }

    private bool IsSane(DateTime candidate)
    {
        DateTime upperBound = clock.GetUtcNow().UtcDateTime.AddDays(1);
        DateTime asUtc = candidate.Kind == DateTimeKind.Utc
            ? candidate
            : DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
        return asUtc >= MinSaneDate && asUtc <= upperBound;
    }

    /// <summary>Returns the final path segment (filename) of an AFC path.</summary>
    /// <param name="remotePath">An AFC path using forward slashes.</param>
    /// <returns>The filename portion.</returns>
    public static string ExtractFileName(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return "unnamed";
        }

        int slash = remotePath.LastIndexOf('/');
        string name = slash >= 0 ? remotePath[(slash + 1)..] : remotePath;
        return name.Length == 0 ? "unnamed" : name;
    }

    /// <summary>
    /// Replaces characters that are invalid in a Windows filename with underscores and strips
    /// trailing dots/spaces, guaranteeing a non-empty, path-safe name.
    /// </summary>
    /// <param name="fileName">A candidate filename.</param>
    /// <returns>A sanitized filename safe to write to disk.</returns>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "unnamed";
        }

        Span<char> buffer = stackalloc char[fileName.Length];
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < fileName.Length; i++)
        {
            char c = fileName[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        string sanitized = new string(buffer).Trim().TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "unnamed" : sanitized;
    }
}
