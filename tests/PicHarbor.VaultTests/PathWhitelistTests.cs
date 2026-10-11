// 路径白名单单元测试
// 不需要管理员权限，不改任何 ACL。

using PicHarbor.Vault;
using Shouldly;

namespace PicHarbor.VaultTests;

public class PathWhitelistTests
{
    // ──────────────────────────────────────────────────────────────────────
    // 应该被拒绝的路径
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_is_rejected(string path)
    {
        PathWhitelist.IsAllowed(path, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("C:\\")]
    [InlineData("D:\\")]
    [InlineData("E:\\")]
    public void Drive_root_is_rejected(string path)
    {
        PathWhitelist.IsAllowed(path, out string reason).ShouldBeFalse();
        reason.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("C:\\Users")]           // 太浅（1 层）
    [InlineData("D:\\Pictures")]        // 太浅
    public void Shallow_path_is_rejected(string path)
    {
        PathWhitelist.IsAllowed(path, out string reason).ShouldBeFalse();
        reason.ShouldNotBeEmpty();
    }

    [Fact]
    public void Windows_dir_is_rejected()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // e.g. C:\Windows\System32
        string path = Path.Combine(windows, "System32", "DriverStore");
        PathWhitelist.IsAllowed(path, out string reason).ShouldBeFalse();
        reason.ShouldContain("protected");
    }

    [Fact]
    public void UserProfile_subdir_is_rejected()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(profile, "Documents", "Secret");
        PathWhitelist.IsAllowed(path, out string reason).ShouldBeFalse();
        reason.ShouldContain("protected");
    }

    [Fact]
    public void AppData_subdir_is_rejected()
    {
        string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string path = Path.Combine(appdata, "PicHarbor", "config");
        PathWhitelist.IsAllowed(path, out string reason).ShouldBeFalse();
        reason.ShouldContain("protected");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 应该被允许的路径（不需要目录实际存在）
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("E:\\Pictures\\PicHarbor")]
    [InlineData("D:\\Archive\\Photos\\iPhone")]
    [InlineData("F:\\MyMedia\\Vault\\Test")]
    public void Valid_deep_path_is_allowed(string path)
    {
        PathWhitelist.IsAllowed(path, out string reason).ShouldBeTrue(reason);
    }

    [Fact]
    public void Trailing_separator_is_normalized_and_allowed()
    {
        PathWhitelist.IsAllowed("E:\\Pictures\\PicHarbor\\", out string reason).ShouldBeTrue(reason);
    }

    [Fact]
    public void Relative_path_is_rejected()
    {
        // Path.GetFullPath resolves relative paths against CWD (which is under UserProfile),
        // so the whitelist rejects it either as "protected system directory" or "absolute".
        // Either way, it must be rejected.
        PathWhitelist.IsAllowed("Pictures\\PicHarbor", out string reason).ShouldBeFalse();
        reason.ShouldNotBeEmpty();
    }
}
