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
    private readonly List<string> _roots;
    private readonly Dictionary<string, string> _protectionErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offlineRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TotpTransaction = new();
    private int _passwordFailures;
    private DateTimeOffset _passwordLockout;
    public IReadOnlyDictionary<string,string> ProtectionErrors { get { lock (_lock) return new Dictionary<string,string>(_protectionErrors); } }
    private SecurityIdentifier? _unlockedUser;
    private ProcessWatcher? _watcher;
    private CancellationTokenSource? _revokeCts;
    private readonly Action<string> _lockDirectory;
    private readonly Action<string,SecurityIdentifier> _grantDirectory;
    private bool _hasCompletedRevoke;
    private int _permissionChangeDepth;
    private string? _permissionOperation = "Starting";
    private VaultPermissionStatus _permissionStatus = new(false, [], new Dictionary<string,string>(), true, "Starting");

    public VaultState() : this(AclEngine.Lock,AclEngine.Grant) { }

    internal VaultState(Action<string> lockDirectory,Action<string,SecurityIdentifier> grantDirectory)
    {
        _lockDirectory=lockDirectory;_grantDirectory=grantDirectory;
        AclEngine.EnableOwnershipPrivileges();
        VaultStore.ValidateProtectedStore();
        VaultStore.EnsureDirectory();
        // 从磁盘恢复已登记的根，保证服务重启后收回仍有对象。
        _roots = RootStore.Load();
        foreach(string root in _roots)
            if(!PathWhitelist.IsAllowed(root,out string reason)) throw new InvalidDataException(reason);
        PublishPermissionStatus();
    }

    public bool HasPassword => PasswordStore.HasPassword;
    public bool IsUnlocked  => ReadPermissionStatus().IsUnlocked;
    public bool HasTotp     => TotpStore.HasSecret;
    private static string OwnerPath => Path.Combine(VaultStore.Directory,"vault_owner");
    public bool IsAuthorizedSession(SecurityIdentifier caller)
    {
        return !ReadPermissionStatus().InProgress && _unlockedUser?.Equals(caller)==true;
    }
    public bool IsApplicationOwner(SecurityIdentifier caller)
        => !File.Exists(OwnerPath) || File.ReadAllText(OwnerPath).Trim() == caller.Value;
    public void BindOwner(SecurityIdentifier caller)
        => VaultStore.WriteProtectedFile(OwnerPath,caller.Value);

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
        if(File.Exists(OwnerPath) && File.ReadAllText(OwnerPath).Trim()!=callerSid.Value)
        { error="This application lock belongs to another Windows user.";return false; }
        lock (_lock) { if (_passwordLockout > DateTimeOffset.UtcNow) { error = "Too many password attempts. Try again later."; return false; } }
        if (!PasswordStore.HasPassword) { error = "No password set."; return false; }
        if (!PasswordStore.Verify(password))
        {
            lock (_lock) { if (++_passwordFailures >= 5) _passwordLockout = DateTimeOffset.UtcNow.AddSeconds(30); }
            error = "Wrong password."; return false;
        }

        lock (_lock)
        {
            using var change=BeginPermissionChange("Unlocking");
            if(_unlockedUser?.Equals(callerSid)==true && _protectionErrors.Count==0)
            {
                _watcher?.Dispose();
                _watcher=new ProcessWatcher(callerPid,()=>ForceLock());
                _passwordFailures=0;error="";return true;
            }
            // 若已解锁则先收回再重新授予（切换用户场景）
            InternalRevoke();
            if(_protectionErrors.Count!=0)
            {error="Unable to prepare protected directories for unlocking.";return false;}

            _passwordFailures = 0;
            _protectionErrors.Clear();
            foreach (string root in _roots)
            {
                if(IsDriveOffline(root)) { _offlineRoots.Add(root);continue; }
                try
                {
                    if(_offlineRoots.Contains(root)) { _lockDirectory(root);_offlineRoots.Remove(root); }
                    _grantDirectory(root, callerSid);
                }
                catch (Exception ex)
                {
                    _protectionErrors[root] = ex.Message;
                }
            }

            if (_protectionErrors.Count != 0) { InternalRevoke(); error = "Unable to unlock all protected directories."; return false; }
            _unlockedUser = callerSid;
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
            using var change=BeginPermissionChange("Registering");
            string norm = NormPath(path);
            if (!_roots.Contains(norm, StringComparer.OrdinalIgnoreCase))
            {
                AclSnapshotStore.Capture(norm);
                _roots.Add(norm);
                RootStore.Save(_roots);
            }

            // 登记即锁定：断继承、只留 SYSTEM 完全控制、所有者设为 SYSTEM。
            // 这样「启用应用锁」当场生效，无需等下一次解锁/锁定循环。
            try
            {
                _lockDirectory(norm);
                if(_unlockedUser is not null) _grantDirectory(norm,_unlockedUser);
                _protectionErrors.Remove(norm);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Vault] Lock on register failed ({norm}): {ex.Message}");
                _protectionErrors[norm]=ex.Message;_hasCompletedRevoke=false;
                error = $"Registered but failed to lock: {ex.Message}";
                return false;
            }
        }
        error = "";
        return true;
    }

    public bool TryUnregister(string path, SecurityIdentifier callerSid, out string error)
    {
        lock (_lock)
        {
            using var change=BeginPermissionChange("Restoring");
            string norm = NormPath(path);
            int idx = _roots.FindIndex(r => r.Equals(norm, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { error = "Path not registered."; return false; }

            // 先恢复该目录（让用户可以读写）
            try { AclSnapshotStore.Restore(norm, callerSid); }
            catch (Exception ex) { error = $"Unable to restore directory permissions: {ex.Message}"; return false; }

            RootStore.Save(_roots.Where((_,index)=>index!=idx));
            _roots.RemoveAt(idx);
            AclSnapshotStore.Forget(norm);
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
        lock (_lock)
        {
            using var change=BeginPermissionChange("Starting");
            // 先把服务私有数据目录与文件的 ACL 收紧（幂等），
            // 防止历史遗留或用户手动创建的文件权限过宽。
            TryHardenStore();
            InternalRevoke();
        }
    }

    private static void TryHardenStore()
    {
        // 测试隔离模式下 VaultStore.EnsureDirectory 已跳过硬化，这里保持对称。
        try
        {
            VaultStore.EnsureDirectory();
            if (File.Exists(VaultStore.CredentialPath)) VaultStore.HardenFile(VaultStore.CredentialPath);
            if (File.Exists(VaultStore.RootsPath))      VaultStore.HardenFile(VaultStore.RootsPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Harden store failed: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 密码管理
    // ──────────────────────────────────────────────────────────────────────

    public bool TrySetPassword(string newPassword, out string error)
    {
        if (string.IsNullOrEmpty(newPassword)) { error = "Password cannot be empty."; return false; }
        try
        {
            PasswordStore.SetPassword(newPassword);
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Cannot write credential store (service not running with sufficient rights): {ex.Message}";
            return false;
        }
        error = "";
        return true;
    }

    public bool TryChangePassword(string current, string newPassword, out string error)
    {
        if (!PasswordStore.Verify(current)) { error = "Wrong current password."; return false; }
        if (string.IsNullOrEmpty(newPassword)) { error = "New password cannot be empty."; return false; }
        try
        {
            PasswordStore.SetPassword(newPassword);
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Cannot write credential store (service not running with sufficient rights): {ex.Message}";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>
    /// 彻底关闭应用锁：恢复所有登记根的访问权限（Restore），清空登记列表，
    /// 并删除应用密码校验值（此后 <see cref="HasPassword"/> 为 false）。
    /// 属破坏性操作，仅允许管理员/SYSTEM 调用。
    /// </summary>
    public bool TryClearPassword(SecurityIdentifier callerSid, out string error)
    {
        lock (_lock)
        {
            using var change=BeginPermissionChange("Restoring");
            // 先解除解锁态（若有），再对每个根做 Restore，让用户重新可读写
            InternalRevoke();

            foreach (string root in _roots.ToList())
            {
                try { AclSnapshotStore.Restore(root, callerSid); }
                catch (Exception ex)
                {
                    error = $"Unable to restore {root}: {ex.Message}";
                    return false;
                }
            }

            RootStore.Save([]);
            foreach(string root in _roots) AclSnapshotStore.Forget(root);
            _roots.Clear();

            // 清掉 TOTP 绑定，避免残留密钥在下次启用时意外复用
            TotpStore.Clear();

            // 删除密码校验值文件
            try
            {
                if (File.Exists(VaultStore.CredentialPath))
                    File.Delete(VaultStore.CredentialPath);
            }
            catch (Exception ex)
            {
                error = $"Failed to remove password: {ex.Message}";
                return false;
            }
        }

        error = "";
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    // TOTP / 双因素（追加任务书）
    // ──────────────────────────────────────────────────────────────────────

    // 待确认的绑定密钥（BeginTotpSetup 生成，VerifyTotpSetup 确认后才落盘）
    private byte[]? _pendingTotpSecret;
    private readonly Lock _totpLock = new();

    /// <summary>
    /// 开始 TOTP 绑定：生成新密钥，返回 otpauth URI 与 Base32 密钥。
    /// 此时密钥<b>尚未生效</b>，必须再调 <see cref="TryConfirmTotpSetup"/> 用一次动态码确认。
    /// </summary>
    public bool TryBeginTotpSetup(string account, out string? otpAuthUri, out string? secretBase32, out string error)
    {
        lock(TotpTransaction) return TryBeginTotpSetupCore(account,out otpAuthUri,out secretBase32,out error);
    }

    private bool TryBeginTotpSetupCore(string account, out string? otpAuthUri, out string? secretBase32, out string error)
    {
        otpAuthUri = null;
        secretBase32 = null;

        lock (_totpLock)
        {
            _pendingTotpSecret = TotpEngine.GenerateSecret();
            secretBase32 = TotpEngine.ToBase32(_pendingTotpSecret);
            otpAuthUri = TotpEngine.BuildOtpAuthUri(secretBase32, account, "PicHarbor");
        }

        error = "";
        return true;
    }

    /// <summary>
    /// 确认 TOTP 绑定：校验一次动态码，通过后才真正保存密钥并清空防重放/限流状态。
    /// </summary>
    public bool TryConfirmTotpSetup(string code, out string error)
    {
        lock (TotpTransaction) return TryConfirmTotpSetupCore(code, out error);
    }

    private bool TryConfirmTotpSetupCore(string code, out string error)
    {
        byte[]? secret;
        lock (_totpLock) { secret = _pendingTotpSecret; }

        if (secret is null) { error = "No pending TOTP setup. Call BeginTotpSetup first."; return false; }

        if (!TotpEngine.Verify(secret, code, NowUnix(), out long step))
        {
            error = "Invalid verification code.";
            return false;
        }

        // 保存密钥，并把该时间步标记为已用，防止立刻重放
        TotpStore.SetSecret(secret);
        TotpStore.MarkStepConsumed(step);
        TotpStore.RecordSuccess();

        lock (_totpLock) { _pendingTotpSecret = null; }

        error = "";
        return true;
    }

    /// <summary>
    /// 用 TOTP 重置应用密码。验证通过只更新密码，<b>不自动解锁</b>——
    /// 目录仍保持锁定，用户须用新密码再解锁一次。
    /// </summary>
    public bool TryResetPasswordWithTotp(string code, string newPassword, out string error)
    {
        lock (TotpTransaction) return TryResetPasswordWithTotpCore(code, newPassword, out error);
    }

    private bool TryResetPasswordWithTotpCore(string code, string newPassword, out string error)
    {
        if (string.IsNullOrEmpty(newPassword)) { error = "New password cannot be empty."; return false; }

        if (!TryValidateTotp(code, out error)) return false;

        try
        {
            PasswordStore.SetPassword(newPassword);
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Cannot write credential store (service not running with sufficient rights): {ex.Message}";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>
    /// 重绑验证器：需同时通过「当前应用密码 + 当前 TOTP 码」双验证，
    /// 通过后才能生成新密钥（返回 otpauth URI，仍须再确认一次）。
    /// </summary>
    public bool TryRebindTotp(string currentPassword, string currentCode, string account,
                              out string? otpAuthUri, out string? secretBase32, out string error)
    {
        lock(TotpTransaction) return TryRebindTotpCore(currentPassword,currentCode,account,out otpAuthUri,out secretBase32,out error);
    }

    private bool TryRebindTotpCore(string currentPassword, string currentCode, string account,
                              out string? otpAuthUri, out string? secretBase32, out string error)
    {
        otpAuthUri = null;
        secretBase32 = null;

        if (!PasswordStore.Verify(currentPassword)) { error = "Wrong current password."; return false; }
        if (!TryValidateTotp(currentCode, out error)) return false;

        return TryBeginTotpSetup(account, out otpAuthUri, out secretBase32, out error);
    }

    /// <summary>
    /// 通用 TOTP 校验：检查绑定存在、限流、动态码正确且未被重放。
    /// 校验成功/失败会更新限流与防重放状态。
    /// </summary>
    private bool TryValidateTotp(string code, out string error)
    {
        lock (TotpTransaction) return TryValidateTotpCore(code, out error);
    }

    private bool TryValidateTotpCore(string code, out string error)
    {
        byte[]? secret = TotpStore.GetSecret();
        if (secret is null) { error = "TOTP not configured."; return false; }

        long now = NowUnix();

        if (TotpStore.IsLockedOut(now, out int remaining))
        {
            error = $"Too many failed attempts. Try again in {remaining} seconds.";
            return false;
        }

        if (!TotpEngine.Verify(secret, code, now, out long step))
        {
            TotpStore.RecordFailure(now, out _);
            error = "Invalid verification code.";
            return false;
        }

        // 防重放：同一时间步只能成功一次
        if (TotpStore.IsStepConsumed(step))
        {
            TotpStore.RecordFailure(now, out _);
            error = "This verification code has already been used.";
            return false;
        }

        TotpStore.MarkStepConsumed(step);
        TotpStore.RecordSuccess();
        error = "";
        return true;
    }

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ──────────────────────────────────────────────────────────────────────
    // 内部
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>调用前必须持有 _lock。</summary>
    private void InternalRevoke()
    {
        if(_unlockedUser is null && _hasCompletedRevoke && _protectionErrors.Count==0) return;
        using var change=BeginPermissionChange("Locking");
        _watcher?.Dispose();
        _watcher = null;

        var cts = _revokeCts;
        _revokeCts = null;
        cts?.Cancel();
        cts?.Dispose();

        _unlockedUser = null;
        _protectionErrors.Clear();
        foreach (string root in _roots)
        {
            if(IsDriveOffline(root)) { _offlineRoots.Add(root);continue; }
            try { _lockDirectory(root); }
            catch (Exception ex) { _protectionErrors[root] = ex.Message; }
        }
        _hasCompletedRevoke=_protectionErrors.Count==0;
    }

    public void RefreshReconnectedRoots()
    {
        lock(_lock)
        {
            foreach(string root in _offlineRoots.ToList())
            {
                if(IsDriveOffline(root)) continue;
                using var change=BeginPermissionChange("Reconnecting");
                try
                {
                    _lockDirectory(root);
                    if(_unlockedUser is not null) _grantDirectory(root,_unlockedUser);
                    _offlineRoots.Remove(root);_protectionErrors.Remove(root);
                }
                catch(Exception ex) { _protectionErrors[root]=ex.Message; }
            }
        }
    }

    internal VaultPermissionStatus ReadPermissionStatus() => Volatile.Read(ref _permissionStatus);

    private void PublishPermissionStatus()
        => Volatile.Write(ref _permissionStatus,new(_permissionOperation is null && _unlockedUser is not null,
            Array.AsReadOnly(_roots.ToArray()),new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(new Dictionary<string,string>(_protectionErrors)),
            _permissionOperation is not null,_permissionOperation));

    private IDisposable BeginPermissionChange(string operation)
    {
        if(_permissionChangeDepth++==0) { _permissionOperation=operation;PublishPermissionStatus(); }
        return new PermissionChange(this);
    }

    private sealed class PermissionChange(VaultState state) : IDisposable
    {
        public void Dispose()
        {
            if(--state._permissionChangeDepth==0) { state._permissionOperation=null;state.PublishPermissionStatus(); }
        }
    }

    private static bool IsDriveOffline(string root)
    {
        try { return !new DriveInfo(Path.GetPathRoot(root)!).IsReady; }
        catch(IOException) { return true; }
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

internal sealed record VaultPermissionStatus(bool IsUnlocked,IReadOnlyList<string> Roots,
    IReadOnlyDictionary<string,string> Errors,bool InProgress,string? Operation);
