namespace PicHarbor.Core.Organize;

/// <summary>
/// Detects Live Photo pairs among copied files: an image (<c>.HEIC</c>/<c>.HEIF</c>/<c>.JPG</c>) and a
/// <c>.MOV</c> that share the same folder and basename (e.g. <c>IMG_1234.HEIC</c> + <c>IMG_1234.MOV</c>).
/// </summary>
/// <remarks>
/// Because the organizer places both halves into the same <c>YYYY/YYYY-MM</c> folder by their shared
/// EXIF date, a basename match within a folder is a reliable pairing signal — no timestamp tolerance
/// is needed once the files are organized.
/// </remarks>
public static class LivePhotoDetector
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".jpg", ".jpeg" };

    private const string VideoExtension = ".mov";

    /// <summary>
    /// Finds Live Photo pairs among the given destination paths.
    /// </summary>
    /// <param name="destPaths">Relative destination paths of copied files.</param>
    /// <returns>The detected pairs as (image path, video path) tuples.</returns>
    public static IReadOnlyList<(string Image, string Video)> FindPairs(IEnumerable<string> destPaths)
    {
        ArgumentNullException.ThrowIfNull(destPaths);

        var byKey = new Dictionary<string, (string? Image, string? Video)>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in destPaths)
        {
            string extension = Path.GetExtension(path);
            bool isImage = ImageExtensions.Contains(extension);
            bool isVideo = extension.Equals(VideoExtension, StringComparison.OrdinalIgnoreCase);
            if (!isImage && !isVideo)
            {
                continue;
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(path);
            string key = Path.Combine(directory, stem);

            byKey.TryGetValue(key, out (string? Image, string? Video) entry);
            if (isImage)
            {
                entry.Image = path;
            }
            else
            {
                entry.Video = path;
            }

            byKey[key] = entry;
        }

        var pairs = new List<(string Image, string Video)>();
        foreach ((string? image, string? video) in byKey.Values)
        {
            if (image is not null && video is not null)
            {
                pairs.Add((image, video));
            }
        }

        return pairs;
    }
}
