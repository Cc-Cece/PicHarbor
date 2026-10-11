// PicHarbor.Vault — 路径白名单
// 只接受归档根（及归档外的自定义同步目录），
// 拒绝盘符根、Windows 目录、用户配置根等过宽路径。

using System.IO;

namespace PicHarbor.Vault;

/// <summary>
/// 校验客户端提交的路径是否允许被 Vault 服务管理。
/// 规则：路径必须是绝对路径、不是危险宽路径、且层级深度 >= 2（即至少 X:\Foo\Bar）。
/// </summary>
public static class PathWhitelist
{
    // 拒绝这些目录及其父路径（盘符根比较时忽略大小写）
    private static readonly string[] DangerousPrefixes =
    [
        // 盘符根本身（例如 C:\、D:\）通过层级检查拦截
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    /// <summary>
    /// 返回 <c>true</c> 表示路径允许登记；返回 <c>false</c> 时 <paramref name="reason"/> 说明原因。
    /// </summary>
    public static bool IsAllowed(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Path is empty.";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            reason = $"Invalid path: {ex.Message}";
            return false;
        }

        if (!Path.IsPathRooted(full))
        {
            reason = "Path must be absolute.";
            return false;
        }

        // 盘符根：X:\ 之后没有更多分量
        string? root = Path.GetPathRoot(full);
        if (string.Equals(full, root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            reason = "Registering a drive root is not allowed.";
            return false;
        }

        // 深度检查：至少 X:\Foo\Bar（两层），防止过宽的 X:\Foo
        int separatorCount = full.Skip(3).Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
        if (separatorCount < 1)
        {
            reason = "Path is too shallow (must be at least two levels deep below the drive root).";
            return false;
        }

        // 危险前缀检查
        foreach (string dangerous in DangerousPrefixes)
        {
            if (string.IsNullOrEmpty(dangerous)) continue;
            string normalDangerous = Path.GetFullPath(dangerous).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(normalDangerous, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Path overlaps with a protected system directory ({normalDangerous}).";
                return false;
            }
        }

        reason = "";
        return true;
    }
}
