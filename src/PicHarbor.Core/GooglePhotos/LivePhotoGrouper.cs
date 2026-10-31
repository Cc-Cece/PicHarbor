namespace PicHarbor.Core.GooglePhotos;

/// <summary>One still image and its paired video.</summary>
internal sealed record LivePhotoPair(GooglePhotosPreparedFile Still, GooglePhotosPreparedFile Video);

/// <summary>Splits an upload list into ordinary files and two-part Live Photos.</summary>
internal static class LivePhotoGrouper
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".jpg", ".jpeg",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4",
    };

    /// <summary>
    /// Pairs files that share a folder and base name. A still that is already a motion photo,
    /// or a stem with more than one image or video, stays ungrouped.
    /// </summary>
    public static (List<GooglePhotosPreparedFile> Ordinary, List<LivePhotoPair> Pairs) Separate(
        IReadOnlyList<GooglePhotosPreparedFile> files)
    {
        var groups = new Dictionary<string, List<GooglePhotosPreparedFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string extension = Path.GetExtension(file.Path);
            if (!ImageExtensions.Contains(extension) && !VideoExtensions.Contains(extension))
            {
                continue;
            }

            string directory = Path.GetDirectoryName(file.Path) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(file.Path);
            string key = directory + "|" + stem;
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(file);
        }

        var paired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<LivePhotoPair>();
        foreach (var group in groups.Values)
        {
            var images = group.Where(file => ImageExtensions.Contains(Path.GetExtension(file.Path))).ToList();
            var videos = group.Where(file => VideoExtensions.Contains(Path.GetExtension(file.Path))).ToList();
            if (images.Count != 1 || videos.Count != 1)
            {
                continue;
            }

            if (MotionPhotoMux.IsMotionContainer(images[0].Path))
            {
                continue;
            }

            pairs.Add(new LivePhotoPair(images[0], videos[0]));
            paired.Add(images[0].Path);
            paired.Add(videos[0].Path);
        }

        var ordinary = files.Where(file => !paired.Contains(file.Path)).ToList();
        return (ordinary, pairs);
    }
}
