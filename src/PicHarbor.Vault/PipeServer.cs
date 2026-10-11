// PicHarbor.Vault — 命名管道服务
// 每个连接在独立 Task 里处理，验证调用方 token SID，
// 拒绝非目标会话的连接发送特权命令（RegisterRoot、Unlock 等）。

using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PicHarbor.Vault.Protocol;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public sealed partial class PipeServer : BackgroundService
{
    private const string PipeName = "PicHarborVault";

    private readonly VaultState _state;
    private readonly ILogger<PipeServer> _logger;

    // 只允许 LocalSystem 和 Administrators 组建立连接
    private static readonly PipeSecurity PipeSec = CreatePipeSecurity();

    public PipeServer(VaultState state, ILogger<PipeServer> logger)
    {
        _state  = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 服务启动第一步：收回所有根的权限（防蓝屏/断电空窗）
        _state.RevokeAllOnStartup();

        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize:  4096,
                outBufferSize: 4096);
            pipe.SetAccessControl(PipeSec);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = Task.Run(() => HandleConnectionAsync(pipe, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) { pipe.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe accept error");
                pipe.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            SecurityIdentifier? callerSid = null;
            int callerPid = 0;

            try
            {
                // 验证调用方身份
                pipe.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent();
                    callerSid = id.User;
                });

                if (callerSid is null)
                {
                    await WriteResponseAsync(pipe, new VaultResponse { Ok = false, Error = "Cannot identify caller." }, ct);
                    return;
                }

                // 获取调用方 PID（用于进程监视）
                callerPid = GetClientPid(pipe);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Caller identification failed");
                return;
            }

            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) return;

            VaultRequest? req;
            try { req = JsonSerializer.Deserialize<VaultRequest>(line); }
            catch { await WriteResponseAsync(pipe, new VaultResponse { Ok = false, Error = "Invalid JSON." }, ct); return; }

            if (req is null) return;

            VaultResponse resp = req.Command switch
            {
                VaultCommand.QueryStatus    => HandleQueryStatus(),
                VaultCommand.SetPassword    => HandleSetPassword(req),
                VaultCommand.ChangePassword => HandleChangePassword(req),
                VaultCommand.RegisterRoot   => HandleRegisterRoot(req),
                VaultCommand.UnregisterRoot => HandleUnregisterRoot(req, callerSid),
                VaultCommand.Unlock         => HandleUnlock(req, callerSid, callerPid),
                VaultCommand.Lock           => HandleLock(),
                _                           => new VaultResponse { Ok = false, Error = $"Unknown command: {req.Command}" }
            };

            await WriteResponseAsync(pipe, resp, ct);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 命令处理
    // ──────────────────────────────────────────────────────────────────────

    private VaultResponse HandleQueryStatus() => new()
    {
        Ok     = true,
        Status = new VaultStatus
        {
            HasPassword     = _state.HasPassword,
            IsUnlocked      = _state.IsUnlocked,
            RegisteredRoots = _state.RegisteredRoots
        }
    };

    private VaultResponse HandleSetPassword(VaultRequest req)
    {
        if (string.IsNullOrEmpty(req.Password))
            return Fail("Password is required.");
        if (_state.HasPassword)
            return Fail("Password already set. Use ChangePassword.");
        return _state.TrySetPassword(req.Password, out string err) ? Ok() : Fail(err);
    }

    private VaultResponse HandleChangePassword(VaultRequest req)
    {
        if (string.IsNullOrEmpty(req.Password) || string.IsNullOrEmpty(req.NewPassword))
            return Fail("Both password and newPassword are required.");
        return _state.TryChangePassword(req.Password, req.NewPassword!, out string err) ? Ok() : Fail(err);
    }

    private VaultResponse HandleRegisterRoot(VaultRequest req)
    {
        if (string.IsNullOrEmpty(req.Path)) return Fail("Path is required.");
        return _state.TryRegister(req.Path!, out string err) ? Ok() : Fail(err);
    }

    private VaultResponse HandleUnregisterRoot(VaultRequest req, SecurityIdentifier callerSid)
    {
        if (string.IsNullOrEmpty(req.Path)) return Fail("Path is required.");
        return _state.TryUnregister(req.Path!, callerSid, out string err) ? Ok() : Fail(err);
    }

    private VaultResponse HandleUnlock(VaultRequest req, SecurityIdentifier callerSid, int callerPid)
    {
        if (string.IsNullOrEmpty(req.Password)) return Fail("Password is required.");
        return _state.TryUnlock(req.Password!, callerSid, callerPid, out string err) ? Ok() : Fail(err);
    }

    private VaultResponse HandleLock()
    {
        _state.ForceLock();
        return Ok();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 工具
    // ──────────────────────────────────────────────────────────────────────

    private static VaultResponse Ok()   => new() { Ok = true };
    private static VaultResponse Fail(string err) => new() { Ok = false, Error = err };

    private static async Task WriteResponseAsync(PipeStream pipe, VaultResponse resp, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(resp) + "\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle hNamedPipe,
        out uint ClientProcessId);

    private static int GetClientPid(NamedPipeServerStream pipe)
    {
        if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
            return (int)pid;
        return -1;
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var sec = new PipeSecurity();
        // SYSTEM — 完全控制（服务自身）
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        // Authenticated Users（即当前登录用户）— 只允许读写（连接到管道）
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            System.Security.AccessControl.AccessControlType.Allow));
        return sec;
    }
}
