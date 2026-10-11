// PicHarbor.Vault — 管道协议常量和消息结构
// 服务端与 GUI 客户端共用同一套协议定义。

using System.Text.Json.Serialization;

namespace PicHarbor.Vault.Protocol;

// ──────────────────────────────────────────────────────────────────────────────
// 命令类型（固定集合，服务端拒绝其他字符串）
// ──────────────────────────────────────────────────────────────────────────────

public static class VaultCommand
{
    public const string QueryStatus   = "QueryStatus";
    public const string SetPassword   = "SetPassword";
    public const string ChangePassword = "ChangePassword";
    public const string RegisterRoot  = "RegisterRoot";
    public const string UnregisterRoot = "UnregisterRoot";
    public const string Unlock        = "Unlock";
    public const string Lock          = "Lock";

    /// <summary>彻底关闭应用锁：清除应用密码与全部登记根（需管理员）。</summary>
    public const string ClearPassword = "ClearPassword";

    // ── TOTP / 双因素（追加任务书） ──

    /// <summary>开始 TOTP 绑定：生成新密钥并返回 otpauth URI（尚未生效，需 VerifyTotpSetup 确认）。</summary>
    public const string BeginTotpSetup = "BeginTotpSetup";

    /// <summary>确认 TOTP 绑定：带上动态码，验证通过后才真正保存密钥。</summary>
    public const string VerifyTotpSetup = "VerifyTotpSetup";

    /// <summary>忘记密码后用 TOTP 重置密码（不自动解锁）。</summary>
    public const string ResetPasswordWithTotp = "ResetPasswordWithTotp";

    /// <summary>用 TOTP 授权后重绑验证器（需应用密码 + 当前 TOTP 码双验证）。</summary>
    public const string RebindTotp = "RebindTotp";
}

// ──────────────────────────────────────────────────────────────────────────────
// 请求 / 响应
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>客户端向服务发送的请求。每条请求独占一行 JSON，以 \n 结尾。</summary>
public sealed class VaultRequest
{
    /// <summary>命令名称，取 <see cref="VaultCommand"/> 常量。</summary>
    [JsonPropertyName("cmd")]
    public string Command { get; set; } = "";

    /// <summary>应用密码明文（仅 Unlock / SetPassword / ChangePassword 携带）。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>更改密码时的新密码。</summary>
    [JsonPropertyName("newPassword")]
    public string? NewPassword { get; set; }

    /// <summary>RegisterRoot / UnregisterRoot 携带的目录路径。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>TOTP 动态验证码（6 位）。用于绑定确认、重置密码、重绑授权。</summary>
    [JsonPropertyName("totpCode")]
    public string? TotpCode { get; set; }

    /// <summary>重置/重绑时的新密码。</summary>
    [JsonPropertyName("newPassword2")]
    public string? NewPasswordConfirm { get; set; }

    /// <summary>重绑 TOTP 时携带的当前应用密码（与 TotpCode 双重验证）。</summary>
    [JsonPropertyName("passwordForRebind")]
    public string? PasswordForRebind { get; set; }
}

/// <summary>服务向客户端返回的响应。每条响应独占一行 JSON，以 \n 结尾。</summary>
public sealed class VaultResponse
{
    /// <summary>操作是否成功。</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>失败时的错误描述（不含敏感信息）。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>QueryStatus 时填充的状态快照。</summary>
    [JsonPropertyName("status")]
    public VaultStatus? Status { get; set; }

    /// <summary>TOTP 绑定时返回的 otpauth:// URI（供生成二维码）。</summary>
    [JsonPropertyName("otpAuthUri")]
    public string? OtpAuthUri { get; set; }

    /// <summary>TOTP 绑定时返回的 Base32 密钥（供人工录入）。</summary>
    [JsonPropertyName("secretBase32")]
    public string? SecretBase32 { get; set; }

    /// <summary>限流锁定时的剩余秒数。</summary>
    [JsonPropertyName("lockoutSeconds")]
    public int? LockoutSeconds { get; set; }
}

/// <summary>服务当前状态快照（不含口令材料）。</summary>
public sealed class VaultStatus
{
    /// <summary>是否已设置过应用密码。</summary>
    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; set; }

    /// <summary>当前是否处于已解锁状态。</summary>
    [JsonPropertyName("isUnlocked")]
    public bool IsUnlocked { get; set; }

    /// <summary>已登记的归档根路径列表（只读副本）。</summary>
    [JsonPropertyName("registeredRoots")]
    public IReadOnlyList<string> RegisteredRoots { get; set; } = [];

    /// <summary>是否已绑定 TOTP 验证器。</summary>
    [JsonPropertyName("hasTotp")]
    public bool HasTotp { get; set; }

    [JsonPropertyName("serverPid")] public int ServerPid { get; set; }
    [JsonPropertyName("serverSid")] public string? ServerSid { get; set; }
    [JsonPropertyName("serverBuild")] public string? ServerBuild { get; set; }
}
