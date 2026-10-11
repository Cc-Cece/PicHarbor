// PicHarbor.Vault — ACL 引擎
// 负责锁定（收回权限）和解锁（授予权限）已登记目录。
// 两阶段收回：先封根，再后台扫描子项所有者。

using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public sealed class AclEngine
{
    // 收回时后台扫描使用低优先级，避免阻塞 UI
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);

    // ──────────────────────────────────────────────────────────────────────
    // 解锁：给指定用户 SID 加上「修改」权限（继承到子项）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 给 <paramref name="userSid"/> 加上 Modify + 继承，使其可读写 <paramref name="root"/>。
    /// </summary>
    public static void Grant(string root, SecurityIdentifier userSid)
    {
        var rule = new FileSystemAccessRule(
            userSid,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        ApplyToDirectory(root, dir =>
        {
            var acl = dir.GetAccessControl();
            acl.AddAccessRule(rule);
            dir.SetAccessControl(acl);
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    // 收回：两阶段
    // 阶段 1（立即）：在根目录加显式 Deny-All，移除用户 ACE
    // 阶段 2（后台）：遍历子树，把所有者改回 SYSTEM，清理残余 ACE
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 立即封根（Phase 1），并在后台线程完成子树清理（Phase 2）。
    /// </summary>
    public static void Revoke(string root, SecurityIdentifier userSid, CancellationToken ct)
    {
        // Phase 1 — 立即：移除用户 ACE + 封根
        Phase1RevokeRoot(root, userSid);

        // Phase 2 — 后台：遍历并修正子项所有者
        _ = Task.Run(() => Phase2RevokeTree(root, userSid, ct), ct);
    }

    private static void Phase1RevokeRoot(string root, SecurityIdentifier userSid)
    {
        try
        {
            ApplyToDirectory(root, dir =>
            {
                var acl = dir.GetAccessControl();

                // 移除该用户所有 Allow 规则
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
                {
                    if (rule.AccessControlType == AccessControlType.Allow &&
                        rule.IdentityReference.Equals(userSid))
                    {
                        acl.RemoveAccessRule(rule);
                    }
                }

                dir.SetAccessControl(acl);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Phase1 revoke root failed: {ex.Message}");
        }
    }

    private static void Phase2RevokeTree(string root, SecurityIdentifier userSid, CancellationToken ct)
    {
        try
        {
            foreach (string entry in EnumerateAllEntries(root))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    TakeOwnershipAndRemoveAce(entry, userSid);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Vault] Phase2 entry failed ({entry}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Phase2 tree walk failed: {ex.Message}");
        }
    }

    private static void TakeOwnershipAndRemoveAce(string path, SecurityIdentifier userSid)
    {
        bool isDir = Directory.Exists(path);

        if (isDir)
        {
            var dir = new DirectoryInfo(path);
            var acl = dir.GetAccessControl();
            // 把所有者改回 SYSTEM
            acl.SetOwner(SystemSid);
            // 移除用户残余 ACE
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            {
                if (rule.IdentityReference.Equals(userSid))
                    acl.RemoveAccessRule(rule);
            }
            dir.SetAccessControl(acl);
        }
        else
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.SetOwner(SystemSid);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
            {
                if (rule.IdentityReference.Equals(userSid))
                    acl.RemoveAccessRule(rule);
            }
            file.SetAccessControl(acl);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 首次锁定：断开继承，只留 SYSTEM 完全控制
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把目录设置为「完全锁定」状态：断开继承，只给 SYSTEM 完全控制，将所有者设为 SYSTEM。
    /// 这是首次启用时以及服务启动时的初始收回动作。
    /// </summary>
    public static void Lock(string root)
    {
        ApplyToDirectory(root, dir =>
        {
            var acl = dir.GetAccessControl();

            // 断开继承，保留现有规则（先保留，后面全部清空）
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // 清空所有现有 ACE
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList())
            {
                acl.RemoveAccessRule(rule);
            }

            // 只给 SYSTEM 完全控制（含子项继承）
            acl.AddAccessRule(new FileSystemAccessRule(
                SystemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            acl.SetOwner(SystemSid);
            dir.SetAccessControl(acl);
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    // 关闭应用锁：把目录恢复为当前用户可读写（恢复继承）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 关闭应用锁时恢复目录为普通用户可读写：恢复继承，给 <paramref name="userSid"/> 加 Modify。
    /// </summary>
    public static void Restore(string root, SecurityIdentifier userSid)
    {
        ApplyToDirectory(root, dir =>
        {
            var acl = dir.GetAccessControl();

            // 恢复继承
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

            // 给用户加 Modify
            acl.AddAccessRule(new FileSystemAccessRule(
                userSid,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dir.SetAccessControl(acl);
        });
    }

    // ──────────────────────────────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────────────────────────────

    private static void ApplyToDirectory(string path, Action<DirectoryInfo> action)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        action(dir);
    }

    private static IEnumerable<string> EnumerateAllEntries(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible    = true,
            AttributesToSkip      = FileAttributes.ReparsePoint
        };
        // 先目录，再文件（所有者改完之后再处理文件）
        foreach (string dir in Directory.EnumerateDirectories(root, "*", opts))
            yield return dir;
        foreach (string file in Directory.EnumerateFiles(root, "*", opts))
            yield return file;
    }
}
