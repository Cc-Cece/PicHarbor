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
}
