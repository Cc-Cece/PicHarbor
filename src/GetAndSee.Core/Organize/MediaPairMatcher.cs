using GetAndSee.Core.Journal;

namespace GetAndSee.Core.Organize;

/// <summary>
/// 媒体组件配对原因。
/// </summary>
public enum MediaPairReason
{
    /// <summary> Live Photo 动态视频配对 (HEIC/JPG 与 MOV/MP4 配对) </summary>
    LivePhoto,
    /// <summary> AAE 编辑历史描述侧车文件 (.AAE) </summary>
    AaeSidecar,
    /// <summary> RAW + JPG 双格式预览配对图 (DNG 与 JPG 配对) </summary>
    RawJpgPair
}

/// <summary>
/// 媒体文件缺失配对组件提示建议。
/// </summary>
/// <param name="SourceRelativePath">触发配对检查的源文件相对路径。</param>
/// <param name="SuggestedRelativePath">建议补全的配对组件相对路径。</param>
/// <param name="Reason">配对原因。</param>
public sealed record MediaPairSuggestion(
    string SourceRelativePath,
    string SuggestedRelativePath,
    MediaPairReason Reason)
{
    /// <summary>
    /// 获取配对原因的可读文本。
    /// </summary>
    public string ReasonText => Reason switch
    {
        MediaPairReason.LivePhoto => "Live Photo 动态视频配对",
        MediaPairReason.AaeSidecar => "AAE 编辑历史描述侧车文件",
        MediaPairReason.RawJpgPair => "RAW + JPG 双格式预览配对图",
        _ => "媒体组件配对"
    };
}

/// <summary>
/// 智能媒体配对检查匹配器。
/// </summary>
public static class MediaPairMatcher
{
    private static readonly HashSet<string> LivePhotoImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".jpg", ".jpeg", ".png", ".webp" };

    private static readonly HashSet<string> LivePhotoVideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4" };

    private static readonly HashSet<string> RawExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".dng", ".cr2", ".nef", ".arw" };

    private static readonly HashSet<string> JpgExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg" };

    /// <summary>
    /// 在归档 Manifest 中查找当前已选文件集合中缺失的配对媒体组件。
    /// </summary>
    /// <param name="selectedRelativePaths">当前已挑选的文件相对路径集合。</param>
    /// <param name="archiveManifest">归档 Manifest 包含的所有记录。</param>
    /// <param name="includeLivePhoto">是否检查 Live Photo 动态视频配对。</param>
    /// <param name="includeAae">是否检查 AAE 侧车描述文件配对。</param>
    /// <param name="includeRawJpg">是否检查 RAW+JPG 配对。</param>
    /// <returns>缺失的配对建议列表。</returns>
    public static IReadOnlyList<MediaPairSuggestion> FindMissingPairs(
        IEnumerable<string> selectedRelativePaths,
        IEnumerable<ManifestEntry> archiveManifest,
        bool includeLivePhoto = true,
        bool includeAae = true,
        bool includeRawJpg = false)
    {
        ArgumentNullException.ThrowIfNull(selectedRelativePaths);
        ArgumentNullException.ThrowIfNull(archiveManifest);

        var selectedSet = selectedRelativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (selectedSet.Count == 0) return Array.Empty<MediaPairSuggestion>();

        // Index manifest by normalized directory + stem
        var manifestDict = new Dictionary<string, List<ManifestEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archiveManifest)
        {
            string normPath = entry.DestPath.Replace('\\', '/');
            string dir = Path.GetDirectoryName(normPath)?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(normPath);
            string key = $"{dir}/{stem}";

            if (!manifestDict.TryGetValue(key, out var list))
            {
                list = new List<ManifestEntry>();
                manifestDict[key] = list;
            }
            list.Add(entry);
        }

        var suggestions = new List<MediaPairSuggestion>();

        foreach (string path in selectedSet)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(path);
            string key = $"{dir}/{stem}";

            if (!manifestDict.TryGetValue(key, out var siblings))
            {
                continue;
            }

            foreach (var sibling in siblings)
            {
                string siblingNormPath = sibling.DestPath.Replace('\\', '/');
                if (selectedSet.Contains(siblingNormPath))
                {
                    continue; // Already selected
                }

                string siblingExt = Path.GetExtension(siblingNormPath).ToLowerInvariant();

                // Rule 1: Live Photo
                if (includeLivePhoto)
                {
                    if ((LivePhotoImageExtensions.Contains(ext) && LivePhotoVideoExtensions.Contains(siblingExt)) ||
                        (LivePhotoVideoExtensions.Contains(ext) && LivePhotoImageExtensions.Contains(siblingExt)))
                    {
                        suggestions.Add(new MediaPairSuggestion(path, siblingNormPath, MediaPairReason.LivePhoto));
                        continue;
                    }
                }

                // Rule 2: AAE sidecar
                if (includeAae)
                {
                    if (siblingExt.Equals(".aae", StringComparison.OrdinalIgnoreCase) ||
                        (ext.Equals(".aae", StringComparison.OrdinalIgnoreCase) && !siblingExt.Equals(".aae", StringComparison.OrdinalIgnoreCase)))
                    {
                        suggestions.Add(new MediaPairSuggestion(path, siblingNormPath, MediaPairReason.AaeSidecar));
                        continue;
                    }
                }

                // Rule 3: RAW + JPG
                if (includeRawJpg)
                {
                    if ((RawExtensions.Contains(ext) && JpgExtensions.Contains(siblingExt)) ||
                        (JpgExtensions.Contains(ext) && RawExtensions.Contains(siblingExt)))
                    {
                        suggestions.Add(new MediaPairSuggestion(path, siblingNormPath, MediaPairReason.RawJpgPair));
                        continue;
                    }
                }
            }
        }

        return suggestions
            .DistinctBy(s => s.SuggestedRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
