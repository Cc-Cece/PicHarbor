// VaultState 登记/关锁流程测试
// 通过 VaultTestScope 隔离到临时目录。
//
// 注意：TryRegister 现在会「登记即锁定」（改所有者 + 收紧 ACL），
// 需要管理员权限；非提权环境下列为 AclEngineTests.AclEngineTests 一样跳过。

using System.Security.Principal;
using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public class VaultStateLifecycleTests : IDisposable
{
    private readonly VaultTestScope _scope = new();
    private readonly VaultState _state = new();

    private static readonly SecurityIdentifier TestSid =
        new(WellKnownSidType.BuiltinUsersSid, null);

    private static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Register_rejects_disallowed_paths()
    {
        // 盘符根在通过白名单校验前就被拒，不触碰 ACL，无需提权
        _state.TryRegister(@"C:\", out _).ShouldBeFalse();
        _state.HasPassword.ShouldBeFalse();
    }

    [Fact]
    public void Register_records_path_for_allowed_directory()
    {
        Assert.SkipUnless(IsElevated, "TryRegister 会改所有者/ACL，需管理员权限。");

        string dir = Path.Combine(Path.GetTempPath(), "PicHarborLifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            bool ok = _state.TryRegister(dir, out string err);
            ok.ShouldBeTrue(err);
            _state.RegisteredRoots.ShouldContain(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ClearPassword_drops_password_and_roots()
    {
        Assert.SkipUnless(IsElevated, "ClearPassword 会对登记根做 Restore，需管理员权限。");

        string dir = Path.Combine(Path.GetTempPath(), "PicHarborLifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            _state.TrySetPassword("pw", out _).ShouldBeTrue();
            _state.TryRegister(dir, out _).ShouldBeTrue();
            _state.HasPassword.ShouldBeTrue();

            _state.TryClearPassword(TestSid, out string err).ShouldBeTrue(err);

            _state.HasPassword.ShouldBeFalse();
            _state.RegisteredRoots.ShouldBeEmpty();
            _state.IsUnlocked.ShouldBeFalse();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Registered_roots_persist_across_state_instances()
    {
        Assert.SkipUnless(IsElevated, "TryRegister 会改所有者/ACL，需管理员权限。");

        string dir = Path.Combine(Path.GetTempPath(), "PicHarborLifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            _state.TryRegister(dir, out _).ShouldBeTrue();

            // 新实例应从 vault_roots.json 恢复
            var reloaded = new VaultState();
            reloaded.RegisteredRoots.ShouldContain(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
