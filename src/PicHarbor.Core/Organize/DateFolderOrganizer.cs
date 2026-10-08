using PicHarbor.Core.Device;

namespace PicHarbor.Core.Organize;

/// <summary>
/// Maps a remote file to its relative destination path under a chosen <see cref="OrganizeScheme"/> when a
/// sane capture date is available, otherwise <c>unsorted/&lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// Date source preference (R16): EXIF <c>DateTimeOriginal</c> → file modified time → <c>unsorted</c>.
/// A date is only trusted if it falls within <c>[1990-01-01, now + 1 day]</c>; nonsense dates are
/// routed to <c>unsorted</c>. Filenames are sanitized so device-supplied strings can never escape
/// the destination tree (PROJECT_BRIEF §9.3). The scheme only changes the date-folder shape; the
/// <c>unsorted</c> fallback is identical under every scheme, and two files sharing a capture date (e.g. a
/// Live Photo's <c>.HEIC</c> + <c>.MOV</c>) always co-locate.
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
    /// Computes the destination path for a file under <paramref name="scheme"/>, relative to the
    /// destination root.
    /// </summary>
    /// <param name="file">The enumerated device file.</param>
    /// <param name="metadata">Extracted metadata, or <see langword="null"/> if none is available yet (e.g. dry-run).</param>
    /// <param name="scheme">The archive's folder layout.</param>
    /// <returns>
    /// A relative path such as <c>2024-08\IMG_1234.HEIC</c> (<see cref="OrganizeScheme.Month"/>),
    /// <c>2024\2024-08\IMG_1234.HEIC</c> (<see cref="OrganizeScheme.YearMonth"/>), or
    /// <c>unsorted\IMG_1234.HEIC</c> when no trustworthy date exists (identical under every scheme).
    /// </returns>
    public string GetRelativeDestination(RemoteFile file, MediaMetadata? metadata, OrganizeScheme scheme)
    {
        string fileName = SanitizeFileName(ExtractFileName(file.Path));
        DateTime? date = ResolveDate(metadata?.DateTimeOriginal, file.ModifiedAt);
        return GetRelativeDestination(fileName, date, scheme);
    }

    /// <summary>
    /// Computes the relative destination path for an already-final leaf <paramref name="fileName"/> placed by
    /// <paramref name="captureDate"/> under <paramref name="scheme"/>. This is the pure folder-shape rule
    /// shared by the copier (which derives the name and date from a device file) and <c>reorganize</c> (which
    /// derives them from the journal), so the two can never disagree on where a file belongs.
    /// </summary>
    /// <param name="fileName">The final destination leaf name — already sanitized, and carrying any <c>_2</c>/<c>_3</c> suffix assigned at copy time.</param>
    /// <param name="captureDate">The resolved capture date (see <see cref="ResolveDate(DateTime?, DateTimeOffset?)"/>), or <see langword="null"/> for <c>unsorted</c>.</param>
    /// <param name="scheme">The archive's folder layout.</param>
    /// <returns>
    /// A relative path such as <c>2024-08\IMG_1234.HEIC</c> (<see cref="OrganizeScheme.Month"/>) or
    /// <c>unsorted\IMG_1234.HEIC</c> when no trustworthy date exists (identical under every scheme).
    /// </returns>
    public string GetRelativeDestination(string fileName, DateTime? captureDate, OrganizeScheme scheme)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        if (captureDate is null)
        {
            // Unsorted is identical under every scheme — a file with no trustworthy date is never
            // placed in a dated folder.
            return Path.Combine(UnsortedFolder, fileName);
        }

        string year = captureDate.Value.Year.ToString("D4");
        string month = $"{captureDate.Value.Year:D4}-{captureDate.Value.Month:D2}";
        return scheme switch
        {
            OrganizeScheme.Month => Path.Combine(month, fileName),
            OrganizeScheme.YearMonth => Path.Combine(year, month, fileName),
            OrganizeScheme.Year => Path.Combine(year, fileName),
            OrganizeScheme.Flat => fileName,
            _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown organize scheme."),
        };
    }

    /// <summary>
    /// Resolves a file's folder date from its two stored timestamps using the R16 trust rule: a sane EXIF
    /// <c>DateTimeOriginal</c> wins, else a sane file modified time, else <see langword="null"/> (⇒ <c>unsorted</c>).
    /// Shared by the copier and <c>reorganize</c> so both agree on placement from the same inputs.
    /// </summary>
    /// <param name="exifOriginal">EXIF capture time (wall clock), or <see langword="null"/>.</param>
    /// <param name="modifiedAt">File modified time, or <see langword="null"/>.</param>
    /// <returns>The trusted capture date, or <see langword="null"/> when neither timestamp is trustworthy.</returns>
    public DateTime? ResolveDate(DateTime? exifOriginal, DateTimeOffset? modifiedAt)
    {
        if (exifOriginal is DateTime exif && IsSane(exif))
        {
            return exif;
        }

        if (modifiedAt is DateTimeOffset mtime)
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
