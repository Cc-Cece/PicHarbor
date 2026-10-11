// PicHarbor.Vault — TOTP 状态持久化
// 密钥用 DPAPI（LocalMachine 作用域）加密后落盘，ACL 只给 SYSTEM + Administrators。
// 同时持久化防重放（最近已接受的时间步）与失败限流状态。

using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace PicHarbor.Vault;

/// <summary>
/// TOTP 相关的磁盘状态文件：<c>%ProgramData%\PicHarbor\vault_totp.json</c>。
/// 密钥以 DPAPI 密文存储（Base64），防重放与限流状态明文存放（不含敏感信息）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class TotpStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(VaultStore.Directory, "vault_totp.json");

    /// <summary>磁盘上的 TOTP 状态。密钥单独加密存储。</summary>
    private sealed class PersistedState
    {
        public string? SecretProtected { get; set; }   // Base64(DPAPI 密文)
        public long?   LastAcceptedStep { get; set; }  // 防重放：最近接受的时间步
        public int     FailedAttempts   { get; set; }  // 限流：连续失败次数
        public long?   LockoutUntilUnix { get; set; }  // 限流：锁定截止时间（Unix 秒）
    }

    // ──────────────────────────────────────────────────────────────────────
    // 密钥
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>是否已绑定 TOTP。</summary>
    public static bool HasSecret
    {
        get
        {
            var s = Load();
            return !string.IsNullOrEmpty(s.SecretProtected);
        }
    }

    /// <summary>写入（或覆盖）TOTP 密钥。以 DPAPI 加密。</summary>
    public static void SetSecret(byte[] secret)
    {
        var state = Load();
        state.SecretProtected = Convert.ToBase64String(
            ProtectedData.Protect(secret, optionalEntropy: null, DataProtectionScope.LocalMachine));
        Save(state);
    }

    /// <summary>读取 TOTP 密钥。未绑定或解密失败时返回 <c>null</c>。</summary>
    public static byte[]? GetSecret()
    {
        var state = Load();
        if (string.IsNullOrEmpty(state.SecretProtected)) return null;

        try
        {
            byte[] cipher = Convert.FromBase64String(state.SecretProtected);
            return ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.LocalMachine);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] TOTP secret decrypt failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>清除 TOTP 绑定（含防重放与限流状态）。</summary>
    public static void Clear()
    {
        try { if (File.Exists(StorePath)) File.Delete(StorePath); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Vault] TOTP clear failed: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 防重放
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>指定时间步是否已被接受过（防重放）。</summary>
    public static bool IsStepConsumed(long step)
    {
        var state = Load();
        return state.LastAcceptedStep is long last && step <= last;
    }

    /// <summary>记录一个已成功接受的时间步。只前进不后退。</summary>
    public static void MarkStepConsumed(long step)
    {
        var state = Load();
        if (state.LastAcceptedStep is not long last || step > last)
        {
            state.LastAcceptedStep = step;
            Save(state);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 限流
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>当前是否处于失败锁定中。锁定中 <paramref name="remainingSeconds"/> 给出剩余秒数。</summary>
    public static bool IsLockedOut(long nowUnixSeconds, out int remainingSeconds)
    {
        var state = Load();
        if (state.LockoutUntilUnix is long until && until > nowUnixSeconds)
        {
            remainingSeconds = (int)(until - nowUnixSeconds);
            return true;
        }
        remainingSeconds = 0;
        return false;
    }

    /// <summary>
    /// 记录一次验证失败。连续失败达到阈值时进入指数退避锁定。
    /// 返回当前是否已被锁定。
    /// </summary>
    public static bool RecordFailure(long nowUnixSeconds, out int lockoutSeconds)
    {
        var state = Load();
        state.FailedAttempts++;

        // 5 次以内不锁；之后按失败次数指数退避：30 * 2^(n-5) 秒，上限 30 分钟
        if (state.FailedAttempts >= 5)
        {
            int exponent = Math.Min(state.FailedAttempts - 5, 6);
            lockoutSeconds = Math.Min((int)(30 * Math.Pow(2, exponent)), 1800);
        }
        else
        {
            lockoutSeconds = 0;
        }

        if (lockoutSeconds > 0)
            state.LockoutUntilUnix = nowUnixSeconds + lockoutSeconds;

        Save(state);
        return lockoutSeconds > 0;
    }

    /// <summary>记录一次成功验证：清零失败计数与锁定。</summary>
    public static void RecordSuccess()
    {
        var state = Load();
        state.FailedAttempts = 0;
        state.LockoutUntilUnix = null;
        Save(state);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 读写
    // ──────────────────────────────────────────────────────────────────────

    private static PersistedState Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new PersistedState();
            string json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<PersistedState>(json, JsonOptions) ?? new PersistedState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] TOTP load failed: {ex.Message}");
            return new PersistedState();
        }
    }

    private static void Save(PersistedState state)
    {
        try
        {
            string json = JsonSerializer.Serialize(state, JsonOptions);
            VaultStore.WriteProtectedFile(StorePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] TOTP save failed: {ex.Message}");
        }
    }
}
