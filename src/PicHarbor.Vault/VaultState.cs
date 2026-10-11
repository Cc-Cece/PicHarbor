// PicHarbor.Vault — Vault 状态机
// 持有已登记根、当前解锁用户 SID、进程监视器。
// 所有公共方法加锁，允许多线程管道连接并发调用。

using System.Runtime.Versioning;
using System.Security.Principal;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public sealed class VaultState : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<string> _roots = [];
    private SecurityIdentifier? _unlockedUser;
    private ProcessWatcher? _watcher;
    private CancellationTokenSource? _revokeCts;

    public bool HasPassword => PasswordStore.HasPassword;
    public bool IsUnlocked  => _unlockedUser is not null;

    public IReadOnlyList<string> RegisteredRoots
    {
        get { lock (_lock) { return [.. _roots]; } }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 解锁
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>校验密码并解锁，记录调用方 SID，启动进程监视。</summary>
    public bool TryUnlock(string password, SecurityIdentifier callerSid, int callerPid, out string error)
    {
        if (!PasswordStore.HasPassword) { error = "No password set."; return false; }
        if (!PasswordStore.Verify(password)) { error = "Wrong password."; return false; }

        lock (_lock)
        {
            // 若已解锁则先收回再重新授予（切换用户场景）
            InternalRevoke();

            _unlockedUser = callerSid;
            foreach (string root in _roots)
            {
                try { AclEngine.Grant(root, callerSid); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Vault] Grant failed ({root}): {ex.Message}");
                }
            }

            _revokeCts = new CancellationTokenSource();
            _watcher = new ProcessWatcher(callerPid, () => ForceLock());
        }

        error = "";
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 锁定（由用户或服务触发）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>立刻收回权限，无需密码。</summary>
    public void ForceLock()
    {
        lock (_lock) { InternalRevoke(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 路径管理
    // ──────────────────────────────────────────────────────────────────────

    public bool TryRegister(string path, out string error)
    {
        if (!PathWhitelist.IsAllowed(path, out error)) return false;

        lock (_lock)
        {
            string norm = NormPath(path);
            if (!_roots.Contains(norm, StringComparer.OrdinalIgnoreCase))
                _roots.Add(norm);
        }
        error = "";
        return true;
    }

    public bool TryUnregister(string path, SecurityIdentifier callerSid, out string error)
    {
        lock (_lock)
        {
            string norm = NormPath(path);
            int idx = _roots.FindIndex(r => r.Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { error = "Path not registered."; return false; }

            // 先恢复该目录（让用户可以读写）
            try { AclEngine.Restore(norm, callerSid); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Vault] Restore failed ({norm}): {ex.Message}"); }

            _roots.RemoveAt(idx);
        }
        error = "";
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 服务启动时的首次收回（Phase 1 for all roots）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>服务启动第一步调用，确保蓝屏/断电后权限被收回。</summary>
    public void RevokeAllOnStartup()
    {
        lock (_lock) { InternalRevoke(); }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 密码管理
    // ──────────────────────────────────────────────────────────────────────

    public bool TrySetPassword(string newPassword, out string error)
    {
        if (string.IsNullOrEmpty(newPassword)) { error = "Password cannot be empty."; return false; }
        PasswordStore.SetPassword(newPassword);
        error = "";
        return true;
    }

    public bool TryChangePassword(string current, string newPassword, out string error)
    {
        if (!PasswordStore.Verify(current)) { error = "Wrong current password."; return false; }
        if (string.IsNullOrEmpty(newPassword)) { error = "New password cannot be empty."; return false; }
        PasswordStore.SetPassword(newPassword);
        error = "";
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 内部
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>调用前必须持有 _lock。</summary>
    private void InternalRevoke()
    {
        _watcher?.Dispose();
        _watcher = null;

        var cts = _revokeCts;
        _revokeCts = null;
        cts?.Cancel();
        cts?.Dispose();

        if (_unlockedUser is not null)
        {
            var userSid = _unlockedUser;
            _unlockedUser = null;

            var newCts = new CancellationTokenSource();
            _revokeCts = newCts;
            foreach (string root in _roots)
            {
                try { AclEngine.Revoke(root, userSid, newCts.Token); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Vault] Revoke failed ({root}): {ex.Message}");
                }
            }
        }
    }

    private static string NormPath(string path)
        => System.IO.Path.GetFullPath(path).TrimEnd(
               System.IO.Path.DirectorySeparatorChar,
               System.IO.Path.AltDirectorySeparatorChar);

    public void Dispose()
    {
        lock (_lock) { InternalRevoke(); }
    }
}
