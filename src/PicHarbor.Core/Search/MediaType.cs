namespace PicHarbor.Core.Search;

/// <summary>The coarse media category of a file, derived from its extension for the <c>search</c> filter.</summary>
public enum MediaType
{
    /// <summary>A still image (HEIC, JPG, PNG-as-photo, DNG, …).</summary>
    Photo,

    /// <summary>A video clip (MOV, MP4, …).</summary>
    Video,

    /// <summary>A screen capture (PNG — iOS saves screenshots as PNG while the camera shoots HEIC/JPG).</summary>
    Screenshot,

    /// <summary>Anything else (sidecars, raw dumps, unknown extensions).</summary>
    Other,
}

/// <summary>
/// Classifies a file into a <see cref="MediaType"/> from its extension. Heuristic and extension-only — no
/// file contents are read. On iOS the camera shoots <c>HEIC</c>/<c>JPG</c> and screenshots are <c>PNG</c>,
/// so a <c>.png</c> is treated as a screenshot.
/// </summary>
public static class MediaTypeClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".avi", ".hevc", ".mkv", ".3gp", ".3g2", ".mts", ".m2ts",
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".jpg", ".jpeg", ".jfif", ".gif", ".tif", ".tiff", ".bmp", ".webp", ".dng", ".raw",
    };

    /// <summary>Classifies a file path by extension.</summary>
    /// <param name="path">A file name or path (only its extension is inspected).</param>
    /// <returns>The derived <see cref="MediaType"/>.</returns>
    public static MediaType Classify(string path)
    {
        string extension = Path.GetExtension(path ?? string.Empty);
        if (VideoExtensions.Contains(extension))
        {
            return MediaType.Video;
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Screenshot;
        }

        return PhotoExtensions.Contains(extension) ? MediaType.Photo : MediaType.Other;
    }
}
