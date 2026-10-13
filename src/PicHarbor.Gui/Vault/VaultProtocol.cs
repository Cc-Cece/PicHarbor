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

    /// <summary>彻底关闭应用锁：清除应用密码与全部登记根（需管理员）。</summary>
    public const string ClearPassword  = "ClearPassword";

    // TOTP / 双因素
    public const string BeginTotpSetup        = "BeginTotpSetup";
    public const string VerifyTotpSetup       = "VerifyTotpSetup";
    public const string ResetPasswordWithTotp = "ResetPasswordWithTotp";
    public const string RebindTotp            = "RebindTotp";
}

public sealed class VaultRequest
{
    [JsonPropertyName("ownerSid")] public string? OwnerSid { get; set; }
    [JsonPropertyName("cmd")]               public string  Command            { get; set; } = "";
    [JsonPropertyName("password")]          public string? Password           { get; set; }
    [JsonPropertyName("newPassword")]       public string? NewPassword        { get; set; }
    [JsonPropertyName("path")]              public string? Path               { get; set; }
    [JsonPropertyName("totpCode")]          public string? TotpCode           { get; set; }
    [JsonPropertyName("newPassword2")]      public string? NewPasswordConfirm { get; set; }
    [JsonPropertyName("passwordForRebind")] public string? PasswordForRebind  { get; set; }
}

public sealed class VaultResponse
{
    [JsonPropertyName("ok")]             public bool         Ok             { get; set; }
    [JsonPropertyName("error")]          public string?      Error          { get; set; }
    [JsonPropertyName("status")]         public VaultStatus? Status         { get; set; }
    [JsonPropertyName("otpAuthUri")]     public string?      OtpAuthUri     { get; set; }
    [JsonPropertyName("secretBase32")]   public string?      SecretBase32   { get; set; }
    [JsonPropertyName("lockoutSeconds")] public int?         LockoutSeconds { get; set; }
}

public sealed class VaultStatus
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("protectionErrors")] public IReadOnlyDictionary<string,string> ProtectionErrors { get; set; } = new Dictionary<string,string>();
    [JsonPropertyName("hasPassword")]     public bool                  HasPassword     { get; set; }
    [JsonPropertyName("isUnlocked")]      public bool                  IsUnlocked      { get; set; }
    [JsonPropertyName("registeredRoots")] public IReadOnlyList<string> RegisteredRoots { get; set; } = [];
    [JsonPropertyName("hasTotp")]         public bool                  HasTotp         { get; set; }
    [JsonPropertyName("serverPid")]       public int                   ServerPid       { get; set; }
    [JsonPropertyName("serverSid")]       public string?               ServerSid       { get; set; }
    [JsonPropertyName("serverBuild")]     public string?               ServerBuild     { get; set; }
}
