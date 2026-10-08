using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Search;

/// <summary>
/// The filters for a <c>search</c> over an archive's manifest. Every filter is optional; when several are
/// set a file must satisfy all of them (logical AND).
/// </summary>
/// <param name="From">Inclusive lower bound on capture date, or <see langword="null"/>.</param>
/// <param name="To">Inclusive upper bound on capture date, or <see langword="null"/>.</param>
/// <param name="Type">Required media type, or <see langword="null"/> for any.</param>
/// <param name="Camera">Case-insensitive substring matched against camera make or model, or <see langword="null"/>.</param>
/// <param name="MinSize">Inclusive minimum size in bytes, or <see langword="null"/>.</param>
/// <param name="MaxSize">Inclusive maximum size in bytes, or <see langword="null"/>.</param>
/// <param name="HasGps">When <see langword="true"/>, only files that carry GPS coordinates match.</param>
/// <param name="IncludeHeic">When <see langword="true"/>, HEIC/HEIF files are included; when <see langword="false"/>, HEIC/HEIF files are excluded.</param>
/// <param name="FileNameKeyword">Case-insensitive substring matched against file name or relative path, or <see langword="null"/>.</param>
public sealed record MediaSearchCriteria(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    MediaType? Type = null,
    string? Camera = null,
    long? MinSize = null,
    long? MaxSize = null,
    bool HasGps = false,
    bool IncludeHeic = true,
    string? FileNameKeyword = null);

/// <summary>A single file that matched a <see cref="MediaSearchCriteria"/>.</summary>
/// <param name="RelativePath">Destination path relative to the archive root.</param>
/// <param name="CapturedAt">Resolved capture date, or <see langword="null"/>.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Type">The file's derived media type.</param>
public sealed record MediaSearchHit(
    string RelativePath,
    DateTimeOffset? CapturedAt,
    long SizeBytes,
    MediaType Type);

/// <summary>
/// Applies a <see cref="MediaSearchCriteria"/> to manifest rows. Pure and side-effect-free — it reads no
/// device and opens no files; the caller supplies the rows from a read-only journal.
/// </summary>
public static class MediaSearch
{
    /// <summary>Filters and orders manifest rows by the criteria.</summary>
    /// <param name="rows">The manifest rows to search (typically every completed file).</param>
    /// <param name="criteria">The filters to apply.</param>
    /// <returns>Matching files as hits, ordered by capture date (oldest first, undated last) then path.</returns>
    public static IReadOnlyList<MediaSearchHit> Find(IReadOnlyList<ManifestSearchRow> rows, MediaSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(criteria);

        List<MediaSearchHit> hits = new();
        foreach (ManifestSearchRow row in rows)
        {
            MediaType type = MediaTypeClassifier.Classify(row.RelativePath);
            if (Matches(row, type, criteria))
            {
                hits.Add(new MediaSearchHit(row.RelativePath, row.CapturedAt, row.SizeBytes, type));
            }
        }

        hits.Sort(static (left, right) =>
        {
            int byDate = CompareCaptured(left.CapturedAt, right.CapturedAt);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.RelativePath, right.RelativePath);
        });
        return hits;
    }

    // Orders by capture date ascending with undated files last (so a browse reads oldest → newest, then
    // "no date"). Nullable.Compare would instead sort nulls first, which is not the intent.
    private static int CompareCaptured(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        return right is null ? -1 : left.Value.CompareTo(right.Value);
    }

    private static bool Matches(ManifestSearchRow row, MediaType type, MediaSearchCriteria criteria)
    {
        if (criteria.Type is MediaType wanted && type != wanted)
        {
            return false;
        }

        if (!criteria.IncludeHeic)
        {
            string ext = Path.GetExtension(row.RelativePath);
            if (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.FileNameKeyword))
        {
            string fileName = Path.GetFileName(row.RelativePath);
            if (!fileName.Contains(criteria.FileNameKeyword, StringComparison.OrdinalIgnoreCase) &&
                !row.RelativePath.Contains(criteria.FileNameKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (criteria.From is DateTimeOffset from && (row.CapturedAt is not DateTimeOffset captured || captured < from))
        {
            return false;
        }

        if (criteria.To is DateTimeOffset to && (row.CapturedAt is not DateTimeOffset capturedTo || capturedTo > to))
        {
            return false;
        }

        if (criteria.MinSize is long min && row.SizeBytes < min)
        {
            return false;
        }

        if (criteria.MaxSize is long max && row.SizeBytes > max)
        {
            return false;
        }

        if (criteria.HasGps && (row.GpsLatitude is null || row.GpsLongitude is null))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(criteria.Camera) && !MatchesCamera(row, criteria.Camera))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesCamera(ManifestSearchRow row, string camera) =>
        (row.CameraMake is not null && row.CameraMake.Contains(camera, StringComparison.OrdinalIgnoreCase)) ||
        (row.CameraModel is not null && row.CameraModel.Contains(camera, StringComparison.OrdinalIgnoreCase));
}
