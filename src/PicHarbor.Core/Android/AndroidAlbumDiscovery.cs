using System.IO;

namespace PicHarbor.Core.Android;

/// <summary>
/// Options for filtering Android media files and discovering albums.
/// </summary>
public sealed record AndroidBackupFilterOptions
{
    /// <summary>Default minimum file size in bytes for non-video files (100 KB).</summary>
    public const long DefaultMinFileSizeBytes = 100 * 1024;

    /// <summary>Minimum file size in bytes to keep. Files smaller than this are filtered out as junk/thumbnails.</summary>
    public long MinFileSizeBytes { get; init; } = DefaultMinFileSizeBytes;

    /// <summary>When true, files under <see cref="MinFileSizeBytes"/> are excluded unless they are video files.</summary>
    public bool IgnoreSmallImages { get; init; } = true;

    /// <summary>Include image formats (JPG, PNG, HEIC, WEBP, DNG, etc.).</summary>
    public bool IncludePhotos { get; init; } = true;

    /// <summary>Include video formats (MP4, MOV, MKV, 3GP, etc.).</summary>
    public bool IncludeVideos { get; init; } = true;

    /// <summary>Directory keywords that cause a directory to be skipped.</summary>
    public IReadOnlyList<string> ExcludedFolderKeywords { get; init; } = new[]
    {
        ".thumbnails",
        ".trashed",
        ".trash",
        ".pending",
        "cache",
        "Android/data",
        "Android/obb"
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".dng", ".raw", ".cr2", ".nef", ".arw"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".avi"
    };

    /// <summary>Checks whether an extension belongs to photo or video formats.</summary>
    public bool IsSupportedMediaFile(string filename, out bool isVideo)
    {
        string ext = Path.GetExtension(filename);
        isVideo = VideoExtensions.Contains(ext);
        if (isVideo) return IncludeVideos;
        if (PhotoExtensions.Contains(ext)) return IncludePhotos;
        return false;
    }
}

/// <summary>
/// Metadata of a discovered album on the Android device.
/// </summary>
public sealed record AndroidAlbumItem(
    string DisplayName,
    string RemotePath,
    int FileCount,
    long TotalBytes,
    bool IsDefaultSelected,
    string Category);

/// <summary>
/// Album discovery and pruning engine implementing the 3-gate filter rule.
/// </summary>
public static class AndroidAlbumDiscovery
{
    private static readonly string[] SearchRoots = { "/DCIM", "/Pictures", "/Download", "/Movies" };

    /// <summary>
    /// Scans the FTP server for valid albums matching the 3-gate criteria.
    /// </summary>
    public static async Task<IReadOnlyList<AndroidAlbumItem>> DiscoverAlbumsAsync(
        SimpleFtpClient ftpClient,
        AndroidBackupFilterOptions filterOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftpClient);
        filterOptions ??= new AndroidBackupFilterOptions();

        var albums = new List<AndroidAlbumItem>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in SearchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entries = await ftpClient.ListEntriesAsync(root, cancellationToken).ConfigureAwait(false);
                if (entries.Count == 0) continue;

                // Check root directory itself
                await EvaluateDirectoryAsync(root, entries, ftpClient, filterOptions, albums, visitedPaths, cancellationToken).ConfigureAwait(false);

                // Check immediate subdirectories (depth 1)
                foreach (var entry in entries.Where(e => e.IsDirectory))
                {
                    if (IsIgnoredFolderName(entry.Name, filterOptions)) continue;

                    string subPath = $"{root.TrimEnd('/')}/{entry.Name}";
                    if (!visitedPaths.Add(subPath)) continue;

                    try
                    {
                        var subEntries = await ftpClient.ListEntriesAsync(subPath, cancellationToken).ConfigureAwait(false);
                        await EvaluateDirectoryAsync(subPath, subEntries, ftpClient, filterOptions, albums, visitedPaths, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore directory read errors
                    }
                }
            }
            catch
            {
                // Root does not exist or inaccessible
            }
        }

        // Sort: Camera first, Screenshots second, others by count descending
        return albums
            .OrderByDescending(a => a.Category == "Camera")
            .ThenByDescending(a => a.Category == "Screenshots")
            .ThenByDescending(a => a.FileCount)
            .ToList();
    }

    private static async Task EvaluateDirectoryAsync(
        string dirPath,
        IReadOnlyList<FtpFileSystemEntry> entries,
        SimpleFtpClient ftpClient,
        AndroidBackupFilterOptions filterOptions,
        List<AndroidAlbumItem> albums,
        HashSet<string> visitedPaths,
        CancellationToken cancellationToken)
    {
        // Gate 1: Check .nomedia presence or excluded folder keywords
        if (entries.Any(e => e.Name.Equals(".nomedia", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (string kw in filterOptions.ExcludedFolderKeywords)
        {
            if (dirPath.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        // Gate 2: Media format validation
        var mediaEntries = entries
            .Where(e => !e.IsDirectory && filterOptions.IsSupportedMediaFile(e.Name, out _))
            .ToList();

        if (mediaEntries.Count == 0)
        {
            return;
        }

        // Gate 3: All-small-junk / thumbnail pruning
        if (filterOptions.IgnoreSmallImages)
        {
            bool hasValidSize = false;
            foreach (var m in mediaEntries)
            {
                filterOptions.IsSupportedMediaFile(m.Name, out bool isVideo);
                if (isVideo || m.Size >= filterOptions.MinFileSizeBytes)
                {
                    hasValidSize = true;
                    break;
                }
            }

            // If 100% of files in this directory are tiny junk (< 100KB), prune entire directory
            if (!hasValidSize)
            {
                return;
            }
        }

        // Eligible album!
        int fileCount = mediaEntries.Count;
        long totalBytes = mediaEntries.Sum(m => m.Size);
        var (displayName, category, isDefault) = ResolveAlbumPresentation(dirPath);

        albums.Add(new AndroidAlbumItem(displayName, dirPath, fileCount, totalBytes, isDefault, category));
    }

    private static bool IsIgnoredFolderName(string name, AndroidBackupFilterOptions options)
    {
        if (name.StartsWith('.') && name != ".") return true;
        foreach (string kw in options.ExcludedFolderKeywords)
        {
            if (name.Equals(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static (string DisplayName, string Category, bool IsDefault) ResolveAlbumPresentation(string remotePath)
    {
        string norm = remotePath.TrimEnd('/').Replace('\\', '/');
        string name = Path.GetFileName(norm);

        if (norm.EndsWith("/DCIM/Camera", StringComparison.OrdinalIgnoreCase))
            return ("📷 相机胶卷 (Camera)", "Camera", true);

        if (norm.EndsWith("/Screenshots", StringComparison.OrdinalIgnoreCase) ||
            norm.EndsWith("/ScreenCapture", StringComparison.OrdinalIgnoreCase))
            return ("📱 屏幕截图 (Screenshots)", "Screenshots", true);

        if (norm.EndsWith("/ScreenRecorder", StringComparison.OrdinalIgnoreCase))
            return ("📹 屏幕录制 (Screen Recordings)", "Video", false);

        if (norm.EndsWith("/WeiXin", StringComparison.OrdinalIgnoreCase) ||
            norm.EndsWith("/WeChat", StringComparison.OrdinalIgnoreCase))
            return ("💬 微信相册 (WeChat)", "Social", false);

        if (norm.EndsWith("/QQ", StringComparison.OrdinalIgnoreCase))
            return ("🐧 QQ相册 (QQ)", "Social", false);

        if (norm.EndsWith("/Douyin", StringComparison.OrdinalIgnoreCase) ||
            norm.EndsWith("/TikTok", StringComparison.OrdinalIgnoreCase))
            return ("🎵 抖音/TikTok", "Social", false);

        if (norm.EndsWith("/Download", StringComparison.OrdinalIgnoreCase))
            return ("📥 下载内容 (Download)", "Download", false);

        if (norm.EndsWith("/Raw", StringComparison.OrdinalIgnoreCase))
            return ("🎞️ RAW 原片 (Raw)", "Camera", false);

        if (norm.EndsWith("/Lightroom", StringComparison.OrdinalIgnoreCase))
            return ("🎨 Lightroom 导出", "Creative", false);

        if (norm.EndsWith("/Wallpapers", StringComparison.OrdinalIgnoreCase))
            return ("🖼️ 壁纸 (Wallpapers)", "Other", false);

        return ($"📁 {name}", "Other", false);
    }
}
