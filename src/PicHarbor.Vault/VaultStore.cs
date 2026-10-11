// PicHarbor.Vault — 受保护数据目录
// 统一收口 %ProgramData%\PicHarbor 及其中文件的 ACL，
// 保证服务专属数据（密码校验值、登记根、TOTP 状态）只有 SYSTEM 和管理员可读改。

using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public static class VaultStore
{
    private static string? _overrideDirectory;

    /// <summary>
    /// 存放 Vault 全部私有数据的目录，默认 <c>%ProgramData%\PicHarbor</c>。
    /// <para>
    /// 测试可通过 <see cref="UseDirectory"/> 将其指向隔离的临时目录，
    /// 避免污染真实服务数据、也避免并行测试互相踩踏。
    /// </para>
    /// </summary>
    public static string Directory => _overrideDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PicHarbor");

    /// <summary>
    /// 重定向存储目录（仅测试使用）。传入 <c>null</c> 恢复默认。
    /// 目标目录会被创建。
    /// </summary>
    public static void UseDirectory(string? path)
    {
        _overrideDirectory = path;
        if (path is not null) System.IO.Directory.CreateDirectory(path);
    }

    /// <summary>密码校验值文件。</summary>
    public static string CredentialPath => Path.Combine(Directory, "vault_credential");

    /// <summary>已登记根路径的持久化文件。</summary>
    public static string RootsPath => Path.Combine(Directory, "vault_roots.json");

    /// <summary>
    /// 确保目录存在，并把它自身的 ACL 收紧为「SYSTEM + Administrators 完全控制」，
    /// 断开继承以免普通用户通过父目录继承到访问权。
    /// <para>
    /// 硬化本身需要管理员权限（读取/设置他人拥有的对象 ACL）。若调用方未提升
    /// （例：非提权的调试宿主、引导期的普通用户进程），这里<b>只告警不抛出</b>：
    /// 目录创建成功即可，下一次服务以 SYSTEM 身份启动时会再收紧一次。
    /// </para>
    /// </summary>
    public static void EnsureDirectory()
    {
        System.IO.Directory.CreateDirectory(Directory);
        // 测试隔离模式下不硬化目录 ACL（见 TryHardenFile 说明）。
        if (_overrideDirectory is not null) return;

        try
        {
            HardenDirectory(Directory);
        }
        catch (Exception ex)
        {
            // 非提权调用方无法读取/改写该目录的 ACL——不阻断写入流程。
            System.Diagnostics.Debug.WriteLine($"[Vault] Harden directory failed ({Directory}): {ex.Message}");
        }
    }

    /// <summary>
    /// 把目录 ACL 设为：断继承，仅 SYSTEM 与 Administrators 完全控制（含子项继承）。
    /// 幂等，可重复调用。
    /// </summary>
    public static void HardenDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return;

        var acl = dir.GetAccessControl();

        // 断开继承，不保留继承来的 ACE
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // 清空现有显式 ACE
        foreach (FileSystemAccessRule rule in
                 acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList())
        {
            acl.RemoveAccessRule(rule);
        }

        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        dir.SetAccessControl(acl);
    }

    /// <summary>
    /// 写一个受保护的文件并收紧其 ACL（只给 SYSTEM + Administrators）。
    /// 先写临时文件再原子替换，避免半写状态。
    /// <para>
    /// 只收紧 <b>文件</b> 的 ACL，不动目录：目录 ACL 由 <see cref="HardenDirectory"/> 在
    /// 服务启动时统一处理。这样即便调用方是未提升的管理员（如测试进程），
    /// 也不会因为目录被锁死而无法完成本次写入。
    /// </para>
    /// </summary>
    public static void WriteProtectedFile(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Directory);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, System.Text.Encoding.UTF8);
        TryHardenFile(tmp);
        File.Move(tmp, path, overwrite: true);
        TryHardenFile(path);
    }

    /// <summary>文件 ACL 收紧的容错包装：失败只记录，不抛出（避免阻断写入流程）。</summary>
    private static void TryHardenFile(string path)
    {
        // 测试隔离模式（UseDirectory 重定向到临时目录）下不收紧 ACL，
        // 否则非提权的测试进程写完就再也读不了自己的文件。
        if (_overrideDirectory is not null) return;

        try { HardenFile(path); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Harden file failed ({path}): {ex.Message}");
        }
    }

    /// <summary>
    /// 把单个文件的 ACL 收紧为「SYSTEM + Administrators 完全控制」，断继承。
    /// 测试隔离模式下为空操作，避免锁死非提权测试进程。
    /// </summary>
    public static void HardenFile(string path)
    {
        if (_overrideDirectory is not null) return;

        var file = new FileInfo(path);
        if (!file.Exists) return;

        var acl = file.GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule rule in
                 acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList())
        {
            acl.RemoveAccessRule(rule);
        }

        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        file.SetAccessControl(acl);
    }
}
