// PicHarbor.Gui — Vault 命名管道客户端
// 供界面进程与 PicHarbor.Vault 服务通信。

using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace PicHarbor.Gui.Vault;

/// <summary>
/// 与 PicHarborVault 服务通信的客户端。
/// 每次调用建立一条短连接（send-then-receive）。
/// </summary>
public sealed class VaultClient
{
    private const string PipeName    = "PicHarborVault";
    private const int    TimeoutMs   = 3000;

    // ──────────────────────────────────────────────────────────────────────
    // 公共 API
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>查询 Vault 当前状态。返回 null 表示服务不可达（未安装或未启动）。</summary>
    public static async Task<VaultStatus?> QueryStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new VaultRequest { Command = VaultCommand.QueryStatus }, ct);
        return resp?.Status;
    }

    /// <summary>首次设置应用密码。</summary>
    public static Task<VaultResponse?> SetPasswordAsync(string password, CancellationToken ct = default)
        => SendAsync(new VaultRequest { Command = VaultCommand.SetPassword, Password = password }, ct);

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

    // ──────────────────────────────────────────────────────────────────────
    // 内部：建立短连接并收发一次
    // ──────────────────────────────────────────────────────────────────────

    private static async Task<VaultResponse?> SendAsync(VaultRequest request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",               // 本机
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(cts.Token);

            // 发送请求
            string reqJson = JsonSerializer.Serialize(request) + "\n";
            byte[] reqBytes = System.Text.Encoding.UTF8.GetBytes(reqJson);
            await pipe.WriteAsync(reqBytes, cts.Token);
            await pipe.FlushAsync(cts.Token);

            // 接收响应（一行 JSON）
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(line)) return null;

            return JsonSerializer.Deserialize<VaultResponse>(line);
        }
        catch (OperationCanceledException)
        {
            // 超时或外部取消——服务不可达
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VaultClient] {ex.Message}");
            return null;
        }
    }
}
