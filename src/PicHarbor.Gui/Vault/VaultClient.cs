// PicHarbor.Gui — Vault 命名管道客户端
// 供界面进程与 PicHarbor.Vault 服务通信。

using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Security.Principal;

namespace PicHarbor.Gui.Vault;

/// <summary>
/// 与 PicHarborVault 服务通信的客户端。
/// 每次调用建立一条短连接（send-then-receive）。
/// </summary>
public sealed class VaultClient
{
    private const string PipeName    = "PicHarborVault";
    private const int    TimeoutMs   = 3000;

    /// <summary>
    /// 文案本地化钩子。GUI 启动时注入 <c>App.GetString</c>；
    /// 未注入时（单元测试、服务模式）回退到字面文案。
    /// 之所以做成钩子而非直接调用 App：本文件会被 PicHarbor.VaultTests 以源码链接方式编译，
    /// 而该测试项目刻意不加载 WPF。
    /// </summary>
    internal static Func<string, string, string> Localize { get; set; } =
        static (_, fallback) => fallback;

    // ──────────────────────────────────────────────────────────────────────
    // 公共 API
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>查询状态；只有 ok=true 且包含 status 才表示服务确实可用。</summary>
    public static async Task<VaultStatus?> QueryStatusAsync(CancellationToken ct = default)
    {
        var resp = await QueryStatusResponseAsync(ct);
        return resp is { Ok: true, Status: not null } ? resp.Status : null;
    }

    public static Task<VaultResponse?> QueryStatusResponseAsync(CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus }, ct);

    // 测试使用随机管道名，但收发实现与生产 GUI 完全相同。
    internal static Task<VaultResponse?> QueryStatusOnPipeAsync(string pipeName, CancellationToken ct = default, bool requireSystem = false)
        => SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus }, ct, pipeName, requireSystem);

    /// <summary>首次设置应用密码。</summary>
    public static Task<VaultResponse?> SetPasswordAsync(string password, CancellationToken ct = default,string? ownerSid=null)
        => SendAsync(new VaultRequest { Command = VaultCommand.SetPassword, Password = password,OwnerSid=ownerSid }, ct);

    /// <summary>更改应用密码。</summary>
    public static Task<VaultResponse?> ChangePasswordAsync(string current, string newPassword, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.ChangePassword, Password = current, NewPassword = newPassword }, ct);

    /// <summary>登记归档根路径。</summary>
    public static Task<VaultResponse?> RegisterRootAsync(string path, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.RegisterRoot, Path = path }, ct);

    /// <summary>注销归档根路径（关闭应用锁时使用）。</summary>
    public static Task<VaultResponse?> UnregisterRootAsync(string path, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.UnregisterRoot, Path = path }, ct);

    /// <summary>解锁——验证密码后服务授予当前用户访问权限。</summary>
    public static Task<VaultResponse?> UnlockAsync(string password, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.Unlock, Password = password }, ct);

    /// <summary>锁定——立刻收回访问权限，无需密码。</summary>
    public static Task<VaultResponse?> LockAsync(CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.Lock }, ct);

    /// <summary>彻底关闭应用锁：清除密码与全部登记根（需管理员）。</summary>
    public static Task<VaultResponse?> ClearPasswordAsync(CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.ClearPassword }, ct);

    // ──────────────────────────────────────────────────────────────────────
    // TOTP / 双因素
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>开始 TOTP 绑定，返回 otpauth URI 与 Base32 密钥。</summary>
    public static Task<VaultResponse?> BeginTotpSetupAsync(string account, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.BeginTotpSetup, Path = account }, ct);

    /// <summary>确认 TOTP 绑定（校验一次动态码）。</summary>
    public static Task<VaultResponse?> VerifyTotpSetupAsync(string code, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.VerifyTotpSetup, TotpCode = code }, ct);

    /// <summary>忘记密码后用 TOTP 重置密码（不自动解锁）。</summary>
    public static Task<VaultResponse?> ResetPasswordWithTotpAsync(string code, string newPassword, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.ResetPasswordWithTotp, TotpCode = code, NewPassword = newPassword }, ct);

    /// <summary>重绑验证器：应用密码 + 当前 TOTP 码双验证通过后生成新密钥。</summary>
    public static Task<VaultResponse?> RebindTotpAsync(string currentPassword, string currentCode, string account, CancellationToken ct = default)
        => SendAsync(new VaultRequest
        {
            Command          = VaultCommand.RebindTotp,
            PasswordForRebind = currentPassword,
            TotpCode         = currentCode,
            Path             = account
        }, ct);

    // ──────────────────────────────────────────────────────────────────────
    // 内部：建立短连接并收发一次
    // ──────────────────────────────────────────────────────────────────────

    private static async Task<VaultResponse?> SendAsync(VaultRequest request, CancellationToken ct,
        string pipeName = PipeName, bool requireSystem = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        string stage = Localize("VaultStageConnectPipe", "连接命名管道");
        try
        {
            // PipeDirection.InOut 会请求 GENERIC_WRITE，它隐含 CreateNewInstance。
            // 显式请求个别读写权限，与服务 DACL 对齐，不给普通客户端创建服务端实例的权利。
            using var pipe = new NamedPipeClientStream(
                ".", pipeName,
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation,
                HandleInheritability.None);

            await pipe.ConnectAsync(cts.Token);
            // Authenticate the OS-owned pipe before sending passwords. JSON fields from a
            // lookalike pipe server cannot establish the service's identity.
            if (requireSystem && !IsSystemServer(pipe))
                return Failure("The application lock service identity could not be verified.");

            // Byte 模式 + 一行 UTF-8 JSON；服务端读到请求后再识别调用方身份。
            stage = Localize("VaultStageSendRequest", "发送请求");
            string reqJson = JsonSerializer.Serialize(request) + "\n";
            byte[] reqBytes = System.Text.Encoding.UTF8.GetBytes(reqJson);
            await pipe.WriteAsync(reqBytes, cts.Token);
            await pipe.FlushAsync(cts.Token);

            // 接收响应（一行 JSON）
            stage = Localize("VaultStageReadResponse", "读取响应");
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(line))
                return Failure(Localize("VaultErrConnClosed", "服务关闭了连接，未返回响应。"));

            stage = Localize("VaultStageParseResponse", "解析响应");
            return JsonSerializer.Deserialize<VaultResponse>(line)
                ?? Failure(Localize("VaultErrEmptyJson", "服务返回了空 JSON 响应。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(string.Format(
                Localize("VaultErrStageTimeout", "{0}超时（{1} ms）。"), stage, TimeoutMs));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(string.Format(
                Localize("VaultErrStageDenied", "{0}被拒绝（Win32 5，管道访问权限）：{1}"), stage, ex.Message));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VaultClient] {ex.Message}");
            return Failure(string.Format(
                Localize("VaultErrStageFailed", "{0}失败（0x{1}）：{2}"),
                stage, ex.HResult.ToString("X8"), ex.Message));
        }
    }

    private static VaultResponse Failure(string error)
        => new() { Ok = false, Error = error };

    private static bool IsSystemServer(NamedPipeClientStream pipe)
    {
        return pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
               owner.IsWellKnown(WellKnownSidType.LocalSystemSid);
    }
}
