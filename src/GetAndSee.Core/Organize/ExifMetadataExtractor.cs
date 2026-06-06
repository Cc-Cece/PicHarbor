using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace GetAndSee.Core.Organize;

/// <summary>
/// <see cref="IMediaMetadataExtractor"/> backed by MetadataExtractor. Reads EXIF
/// <c>DateTimeOriginal</c>, GPS coordinates, and camera make/model from a local file.
/// </summary>
public sealed class ExifMetadataExtractor : IMediaMetadataExtractor
{
    /// <inheritdoc />
    public MediaMetadata Extract(string filePath)
    {
        try
        {
            IReadOnlyList<MetadataExtractor.Directory> directories = ImageMetadataReader.ReadMetadata(filePath);
            return Build(directories);
        }
        catch (Exception)
        {
            // Unsupported container, corrupt EXIF, screenshot with no metadata, etc.
            // Organization falls back to file mtime; never let metadata reading abort a copy.
            return MediaMetadata.Empty;
        }
    }

    private static MediaMetadata Build(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        DateTime? dateTimeOriginal = null;
        ExifSubIfdDirectory? subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd is not null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime parsed))
        {
            dateTimeOriginal = parsed;
        }

        ExifIfd0Directory? ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        string? make = NullIfEmpty(ifd0?.GetDescription(ExifDirectoryBase.TagMake));
        string? model = NullIfEmpty(ifd0?.GetDescription(ExifDirectoryBase.TagModel));

        double? latitude = null;
        double? longitude = null;
        GpsDirectory? gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gps?.GetGeoLocation() is GeoLocation location && !location.IsZero)
        {
            latitude = location.Latitude;
            longitude = location.Longitude;
        }

        return new MediaMetadata(dateTimeOriginal, latitude, longitude, make, model);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
