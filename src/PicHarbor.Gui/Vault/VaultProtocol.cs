// PicHarbor.Gui — Vault 协议定义（与服务端同步的镜像）
// 保持与 PicHarbor.Vault/Protocol/VaultProtocol.cs 内容一致。

using System.Text.Json.Serialization;

namespace PicHarbor.Gui.Vault;

public static class VaultCommand
{
    public const string QueryStatus    = "QueryStatus";
    public const string SetPassword    = "SetPassword";
    public const string ChangePassword = "ChangePassword";
    public const string RegisterRoot   = "RegisterRoot";
    public const string UnregisterRoot = "UnregisterRoot";
    public const string Unlock         = "Unlock";
    public const string Lock           = "Lock";
}

public sealed class VaultRequest
{
    [JsonPropertyName("cmd")]        public string  Command     { get; set; } = "";
    [JsonPropertyName("password")]   public string? Password    { get; set; }
    [JsonPropertyName("newPassword")] public string? NewPassword { get; set; }
    [JsonPropertyName("path")]       public string? Path        { get; set; }
}

public sealed class VaultResponse
{
    [JsonPropertyName("ok")]     public bool         Ok     { get; set; }
    [JsonPropertyName("error")]  public string?      Error  { get; set; }
    [JsonPropertyName("status")] public VaultStatus? Status { get; set; }
}

public sealed class VaultStatus
{
    [JsonPropertyName("hasPassword")]     public bool                  HasPassword     { get; set; }
    [JsonPropertyName("isUnlocked")]      public bool                  IsUnlocked      { get; set; }
    [JsonPropertyName("registeredRoots")] public IReadOnlyList<string> RegisteredRoots { get; set; } = [];
}
