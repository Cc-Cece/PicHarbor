// 测试隔离作用域
// Vault 的密码/TOTP/登记根状态默认落在全局 %ProgramData%\PicHarbor。
// 测试若直接操作那里会：污染真实服务数据、并行时互相踩踏、
// 且被 HardenFile 收紧 ACL 后非提权进程再也读不回自己的文件。
//
// 该作用域把 VaultStore 重定向到一个独立的临时目录，
// 并在测试结束后恢复默认并删除临时目录。

using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

/// <summary>
/// 进入时把 VaultStore 重定向到独立临时目录，退出时恢复。
/// 配合 <c>DisableTestParallelization</c> 使用（全局静态状态不可并行）。
/// </summary>
public sealed class VaultTestScope : IDisposable
{
    private readonly string _dir;

    public VaultTestScope()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PicHarborTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        VaultStore.UseDirectory(_dir);
    }

    /// <summary>本作用域使用的临时目录。</summary>
    public string Directory => _dir;

    public void Dispose()
    {
        VaultStore.UseDirectory(null);
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }
}
