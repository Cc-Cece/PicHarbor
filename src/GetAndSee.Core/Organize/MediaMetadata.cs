namespace GetAndSee.Core.Organize;

/// <summary>
/// Metadata extracted from a media file's EXIF/container, used to organize and index it.
/// All members are nullable: screenshots, some videos, and corrupt files may carry none.
/// </summary>
/// <param name="DateTimeOriginal">Wall-clock capture time (EXIF <c>DateTimeOriginal</c>), or <see langword="null"/>.</param>
/// <param name="GpsLatitude">GPS latitude in decimal degrees, or <see langword="null"/>.</param>
/// <param name="GpsLongitude">GPS longitude in decimal degrees, or <see langword="null"/>.</param>
/// <param name="CameraMake">Camera manufacturer (EXIF <c>Make</c>), or <see langword="null"/>.</param>
/// <param name="CameraModel">Camera model (EXIF <c>Model</c>), or <see langword="null"/>.</param>
public sealed record MediaMetadata(
    DateTime? DateTimeOriginal,
    double? GpsLatitude,
    double? GpsLongitude,
    string? CameraMake,
    string? CameraModel)
{
    /// <summary>An empty metadata record (used when nothing could be extracted).</summary>
    public static MediaMetadata Empty { get; } = new(null, null, null, null, null);
}

/// <summary>
/// Extracts <see cref="MediaMetadata"/> from a copied media file on local disk.
/// </summary>
public interface IMediaMetadataExtractor
{
    /// <summary>
    /// Reads metadata from a local file. Implementations must never throw for unreadable or
    /// unsupported files; they return <see cref="MediaMetadata.Empty"/> instead.
    /// </summary>
    /// <param name="filePath">Path to a local media file.</param>
    /// <returns>The extracted metadata, or <see cref="MediaMetadata.Empty"/>.</returns>
    MediaMetadata Extract(string filePath);
}
