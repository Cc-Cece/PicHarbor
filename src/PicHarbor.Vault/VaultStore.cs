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
    internal static bool IsIsolated => _overrideDirectory is not null;

    internal static void ValidateProtectedStore()
    {
        if(IsIsolated) return;
        ValidateProtectedFiles(Directory);
    }

    internal static void ValidateProtectedFiles(string directory)
    {
        if(System.IO.Directory.Exists(directory) && (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("The protected service directory cannot be a linked directory.");
        var names=new[]{"vault_credential","vault_roots.json","vault_totp.json","vault_owner"}.Concat(
            System.IO.Directory.Exists(directory) ? System.IO.Directory.EnumerateFiles(directory,"acl-*.json").Select(Path.GetFileName).OfType<string>() : []);
        foreach(string name in names)
        {
            string path=Path.Combine(directory,name);
            if(!File.Exists(path)) continue;
            var acl=new FileInfo(path).GetAccessControl();
            var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
            var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            if(!acl.AreAccessRulesProtected || !system.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
                throw new InvalidDataException("Service protection data has an untrusted owner or inherited access rules. Administrator repair is required.");
            if(acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.AccessControlType==AccessControlType.Allow && !r.IdentityReference.Equals(system) && !r.IdentityReference.Equals(admins)))
                throw new InvalidDataException("Service protection data is accessible to an unauthorized account.");
        }
    }

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
    /// 硬化失败必须中止写入，不能把未受保护的存储报告为成功。
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
            throw new IOException("Unable to secure the protection service directory.",ex);
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

        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(content);stream.Write(bytes);stream.Flush(flushToDisk:true);
            }
            TryHardenFile(tmp);
            File.Move(tmp, path, overwrite: true);
            TryHardenFile(path);
        }
        finally { if(File.Exists(tmp)) File.Delete(tmp); }
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
            throw new IOException("Unable to secure a protection service file.",ex);
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
