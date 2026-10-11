// AclEngine 权限切换测试
// 在临时目录上验证锁定/解锁/收回的所有者与 ACL 行为。
// 需要管理员权限（改所有者/ACL）；非管理员环境下跳过。

using System.Security.AccessControl;
using System.Security.Principal;
using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

/// <summary>
/// ACL 引擎的集成测试。涉及所有者变更与 ACL 写入，需提升权限。
/// </summary>
public class AclEngineTests : IDisposable
{
    private readonly string _root;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    private static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public AclEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PicHarborAclTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            // 先恢复继承与用户权限，确保能删干净
            try
            {
                var acl = new DirectoryInfo(_root).GetAccessControl();
                acl.SetAccessRuleProtection(false, true);
                new DirectoryInfo(_root).SetAccessControl(acl);
            }
            catch { }

            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    private SecurityIdentifier CurrentUserSid
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User!;
        }
    }

    [Fact]
    public void Lock_removes_inheritance_and_sets_system_only()
    {
        Assert.SkipUnless(IsElevated, "需要管理员权限以修改 ACL/所有者。");

        AclEngine.Lock(_root);

        var acl = new DirectoryInfo(_root).GetAccessControl();
        acl.AreAccessRulesProtected.ShouldBeTrue("锁定后应断开继承");

        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        owner.ShouldBe(SystemSid, "锁定后所有者应为 SYSTEM");

        var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier))
                       .Cast<FileSystemAccessRule>()
                       .Where(r => r.AccessControlType == AccessControlType.Allow)
                       .ToList();

        // 只应剩 SYSTEM 的完全控制
        rules.Count.ShouldBe(1);
        rules[0].IdentityReference.ShouldBe(SystemSid);
    }

    [Fact]
    public void Grant_then_Lock_roundtrip()
    {
        Assert.SkipUnless(IsElevated, "需要管理员权限以修改 ACL/所有者。");

        AclEngine.Lock(_root);
        AclEngine.Grant(_root, CurrentUserSid);

        var afterGrant = new DirectoryInfo(_root).GetAccessControl();
        bool hasUser = afterGrant.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                   && r.IdentityReference.Equals(CurrentUserSid)
                   && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        hasUser.ShouldBeTrue("解锁后当前用户应获得 Modify 权限");

        AclEngine.Revoke(_root, CurrentUserSid, CancellationToken.None);

        // Phase 1 是同步的：用户 ACE 应已被移除
        var afterRevoke = new DirectoryInfo(_root).GetAccessControl();
        bool stillHasUser = afterRevoke.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                   && r.IdentityReference.Equals(CurrentUserSid));
        stillHasUser.ShouldBeFalse("收回后当前用户的 Allow 权限应被移除");
    }

    [Fact]
    public void Revoke_restores_owner_to_system_for_new_files()
    {
        Assert.SkipUnless(IsElevated, "需要管理员权限以修改 ACL/所有者。");

        // 解锁，用户新建文件，然后收回 → 该文件所有者应回到 SYSTEM
        AclEngine.Lock(_root);
        AclEngine.Grant(_root, CurrentUserSid);

        string newFile = Path.Combine(_root, "user_created.txt");
        File.WriteAllText(newFile, "created while unlocked");

        AclEngine.Revoke(_root, CurrentUserSid, CancellationToken.None);

        // Phase 2 是后台线程，等待它跑完
        var deadline = DateTime.UtcNow.AddSeconds(10);
        bool ownerIsSystem = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var acl = new FileInfo(newFile).GetAccessControl();
                var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is not null && owner.Equals(SystemSid)) { ownerIsSystem = true; break; }
            }
            catch { }

            Thread.Sleep(200);
        }

        ownerIsSystem.ShouldBeTrue("收回后用户新建文件的所有者应回到 SYSTEM");
    }

    [Fact]
    public void Restore_re_enables_inheritance()
    {
        Assert.SkipUnless(IsElevated, "需要管理员权限以修改 ACL/所有者。");

        AclEngine.Lock(_root);
        AclEngine.Restore(_root, CurrentUserSid);

        var acl = new DirectoryInfo(_root).GetAccessControl();
        acl.AreAccessRulesProtected.ShouldBeFalse("关闭应用锁后应恢复继承");
    }
}
