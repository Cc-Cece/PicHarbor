using PicHarbor.Core.Search;

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

    /// <summary>
    /// Folder plus filename stem, the key used to pair a still with its motion clip.
    /// </summary>
    public static string PairKey(string relativePath)
    {
        string directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(relativePath);
        return Path.Combine(directory, stem);
    }

    /// <summary>
    /// Stems that have both a still (<c>.heic</c>/<c>.jpg</c>/<c>.jpeg</c>) and a motion clip
    /// (<c>.mov</c>/<c>.mp4</c>) in the same folder. Matches the gallery's existing pairing.
    /// </summary>
    public static IReadOnlySet<string> FindLivePairKeys(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        var stills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string extension = Path.GetExtension(path);
            string key = PairKey(path);
            if (extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                stills.Add(key);
            }
            else if (extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                videos.Add(key);
            }
        }

        stills.IntersectWith(videos);
        return stills;
    }

    /// <summary>
    /// The All view keeps stills and standalone videos. A motion clip whose stem is a live pair is hidden
    /// so the still occupies the only card.
    /// </summary>
    public static bool ShowInAllView(string relativePath, MediaType type, IReadOnlySet<string> livePairKeys)
    {
        ArgumentNullException.ThrowIfNull(livePairKeys);
        if (type != MediaType.Video)
        {
            return true;
        }

        return !livePairKeys.Contains(PairKey(relativePath));
    }

    /// <summary>
    /// Counts gallery items: each still, screenshot, and standalone video is one.
    /// A paired motion clip (<c>.mov</c> or <c>.mp4</c> with a same-folder still) is not an extra item.
    /// </summary>
    public static int CountDisplayedItems(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var list = paths as ICollection<string> ?? paths.ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        IReadOnlySet<string> keys = FindLivePairKeys(list);
        int pairedClips = 0;
        foreach (string path in list)
        {
            string extension = Path.GetExtension(path);
            bool isClip = extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
            if (isClip && keys.Contains(PairKey(path)))
            {
                pairedClips++;
            }
        }

        return list.Count - pairedClips;
    }
}
