using GetAndSee.Core.Journal;

namespace GetAndSee.Core.Organize;

/// <summary>
/// 导出前预检校验结果。
/// </summary>
/// <param name="IsSuccess">校验是否完全通过。</param>
/// <param name="BrokenLivePhotoPairs">破坏/未选中的 Live Photo 配对项相对路径列表。</param>
/// <param name="MissingPhysicalFiles">物理磁盘上不存在或已被移动/删除的文件相对路径列表。</param>
public sealed record PreflightCheckResult(
    bool IsSuccess,
    IReadOnlyList<string> BrokenLivePhotoPairs,
    IReadOnlyList<string> MissingPhysicalFiles);

/// <summary>
/// 导出前终极预检校验引擎。
/// </summary>
public static class ExportPreflightCheck
{
    private static readonly HashSet<string> LivePhotoImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif", ".jpg", ".jpeg", ".png", ".webp" };

    private static readonly HashSet<string> LivePhotoVideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mov", ".mp4" };

    /// <summary>
    /// 在物理导出/传输前执行断言校验（包含 PC 磁盘文件物理存在性校验与 Live Photo 成对完整性断言）。
    /// </summary>
    /// <param name="pcDestinationRoot">PC 归档根目录全路径。</param>
    /// <param name="targetExportFiles">计划导出的文件相对路径集合。</param>
    /// <param name="archiveManifest">归档 Manifest 全部记录。</param>
    /// <returns>预检校验结果对象。</returns>
    public static PreflightCheckResult ValidateBeforeExport(
        string pcDestinationRoot,
        IEnumerable<string> targetExportFiles,
        IEnumerable<ManifestEntry> archiveManifest)
    {
        ArgumentNullException.ThrowIfNull(targetExportFiles);
        ArgumentNullException.ThrowIfNull(archiveManifest);

        var brokenPairs = new List<string>();
        var missingFiles = new List<string>();

        var exportSet = targetExportFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        foreach (string file in exportSet)
        {
            // 1. Physical existence check
            string fullPath = string.IsNullOrWhiteSpace(pcDestinationRoot)
                ? file
                : Path.Combine(pcDestinationRoot, file);

            if (!File.Exists(fullPath))
            {
                missingFiles.Add(file);
            }

            // 2. Live Photo pairing completeness check
            string ext = Path.GetExtension(file).ToLowerInvariant();
            bool isPhoto = LivePhotoImageExtensions.Contains(ext);
            bool isVideo = LivePhotoVideoExtensions.Contains(ext);

            if (isPhoto || isVideo)
            {
                string dir = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? string.Empty;
                string stem = Path.GetFileNameWithoutExtension(file);
                string key = $"{dir}/{stem}";

                if (manifestDict.TryGetValue(key, out var siblings))
                {
                    bool hasPartnerInManifest = siblings.Any(s =>
                    {
                        string sExt = Path.GetExtension(s.DestPath).ToLowerInvariant();
                        return (isPhoto && LivePhotoVideoExtensions.Contains(sExt)) ||
                               (isVideo && LivePhotoImageExtensions.Contains(sExt));
                    });

                    if (hasPartnerInManifest)
                    {
                        bool hasPartnerInExport = siblings.Any(s =>
                        {
                            string sNorm = s.DestPath.Replace('\\', '/');
                            string sExt = Path.GetExtension(sNorm).ToLowerInvariant();
                            return exportSet.Contains(sNorm) &&
                                   ((isPhoto && LivePhotoVideoExtensions.Contains(sExt)) ||
                                    (isVideo && LivePhotoImageExtensions.Contains(sExt)));
                        });

                        if (!hasPartnerInExport)
                        {
                            brokenPairs.Add(file);
                        }
                    }
                }
            }
        }

        bool success = brokenPairs.Count == 0 && missingFiles.Count == 0;
        return new PreflightCheckResult(success, brokenPairs, missingFiles);
    }
}
