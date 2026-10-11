// PicHarbor.Vault — 命名管道服务
// 每个连接在独立 Task 里处理，验证调用方 token SID，
// 拒绝非目标会话的连接发送特权命令（RegisterRoot、Unlock 等）。

using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PicHarbor.Vault.Protocol;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public sealed partial class PipeServer : BackgroundService
{
    /// <summary>生产环境的固定管道名。</summary>
    public const string DefaultPipeName = "PicHarborVault";

    private readonly VaultState _state;
    private readonly ILogger<PipeServer> _logger;
    private readonly string _pipeName;

    public PipeServer(VaultState state, ILogger<PipeServer> logger, string? pipeName = null)
    {
        _state    = state;
        _logger   = logger;
        _pipeName = pipeName ?? DefaultPipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 服务启动第一步：收回所有根的权限（防蓝屏/断电空窗）。
        // 用 try 包住：任何初始化异常都不应让整个服务进程退出。
        try
        {
            _state.RevokeAllOnStartup();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevokeAllOnStartup failed; continuing to serve.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // ── 管道 ACL ──────────────────────────────────────────────
                // 服务以 LocalSystem 运行。管道若不显式设 DACL，会拿到「默认」安全描述符，
                // 其结果是把交互式登录用户挡在门外：客户端 CreateFile 直接
                // ERROR_ACCESS_DENIED (5)，表现为 GUI 一直「服务已安装但未能连接」。
                // 因此必须显式授予「已认证用户」读写，让未提权的 GUI 能连上来。
                //
                // 在创建时仅传入 DACL；不请求 SACL，不需要 SeSecurityPrivilege。
                // 禁止退回默认 ACL，否则会把真正的权限错误隐藏为“服务运行但不可用”。
                pipe = CreatePipeInstance();

                await pipe.WaitForConnectionAsync(stoppingToken);
                var accepted = pipe;
                pipe = null;   // 所有权转移给处理任务
                _ = Task.Run(() => HandleConnectionAsync(accepted, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe accept error");
                pipe?.Dispose();
                // 短暂退避后重试，避免异常时忙循环
                try { await Task.Delay(500, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>原子创建显式 DACL 管道；生产与测试走同一路径，不做默认 ACL 降级。</summary>
    private NamedPipeServerStream CreatePipeInstance()
        => NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            4096, 4096, BuildPipeSecurity(), HandleInheritability.None,
            additionalAccessRights: 0);

    /// <summary>
    /// SYSTEM、有效管理员和创建者可管理实例；已认证用户仅可读写。
    /// 断继承用保护 DACL，绝不能用 Deny Everyone 覆盖所有 Allow。
    /// 不设置 SACL/任意所有者，因此无需 SeSecurityPrivilege。
    /// </summary>
    internal static PipeSecurity BuildPipeSecurity(SecurityIdentifier? creatorSid = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // 生产服务创建者就是 SYSTEM；普通用户隔离测试也需要创建后续实例。
        // 给创建者授权并不等于给所有客户端 CreateNewInstance。
        using var creator = WindowsIdentity.GetCurrent();
        creatorSid ??= creator.User;
        if (creatorSid is not null)
            security.AddAccessRule(new PipeAccessRule(creatorSid,
                PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            SecurityIdentifier? callerSid = null;
            bool callerIsAdmin = false;
            int callerPid = 0;

            // ── 顺序至关重要 ─────────────────────────────────────────────
            // Win32 ImpersonateNamedPipeClient 要求「服务端已先从管道读取过数据」，
            // 否则返回 ERROR_CANNOT_IMPERSONATE (1368)，.NET 抛 IOException
            //（"无法在该管道上模拟"）。因此必须：
            //   1) 先读一条客户端消息
            //   2) 再 RunAsClient 取调用方 SID / 是否管理员
            //   3) 然后才能做身份/权限判定
            // 先模拟后读会 100% 失败——这正是「服务在跑但客户端永远超时」的根因。
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Read request failed");
                return;
            }

            if (string.IsNullOrWhiteSpace(line)) return;

            // 客户端已经写过数据，此刻模拟客户端是合法的。
            try
            {
                pipe.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent();
                    callerSid     = id.User;
                    callerIsAdmin = IsAuthorizedIdentity(id);
                });

                // 获取调用方 PID（用于进程监视）
                callerPid = GetClientPid(pipe);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Caller identification failed");
                callerSid = null;
            }

            // 权限分两类：
            //   日常命令（QueryStatus / Unlock / Lock）——未提升的 GUI 也要能用，
            //     它们本身都带应用密码或只读状态，靠密码校验兜底。
            //   特权命令（SetPassword / ChangePassword / RegisterRoot / UnregisterRoot）
            //     ——只能由管理员或 SYSTEM 发起，对应启用/关闭锁时的提权流程。

            VaultRequest? req;
            try { req = JsonSerializer.Deserialize<VaultRequest>(line); }
            catch { await WriteResponseAsync(pipe, new VaultResponse { Ok = false, Error = "Invalid JSON." }, ct); return; }

            if (req is null) return;

            if (!callerIsAdmin && !IsDailyCommand(req.Command, _state))
            {
                await WriteResponseAsync(pipe, new VaultResponse { Ok = false, Error = "Access denied." }, ct);
                return;
            }

            if (callerSid is null)
            {
                // 无法识别调用方身份：不允许进入任何需要 SID 的处理分支。
                await WriteResponseAsync(pipe, new VaultResponse { Ok = false, Error = "Cannot identify caller." }, ct);
                return;
            }

            VaultResponse resp;
            try
            {
                resp = req.Command switch
                {
                    VaultCommand.QueryStatus    => HandleQueryStatus(),
                    VaultCommand.SetPassword    => HandleSetPassword(req),
                    VaultCommand.ChangePassword => HandleChangePassword(req),
                    VaultCommand.RegisterRoot   => HandleRegisterRoot(req),
                    VaultCommand.UnregisterRoot => HandleUnregisterRoot(req, callerSid),
                    VaultCommand.Unlock         => HandleUnlock(req, callerSid, callerPid),
                    VaultCommand.Lock           => HandleLock(),
                    VaultCommand.ClearPassword  => HandleClearPassword(callerSid),

                    VaultCommand.BeginTotpSetup        => HandleBeginTotpSetup(req, callerSid),
                    VaultCommand.VerifyTotpSetup       => HandleVerifyTotpSetup(req, callerSid),
                    VaultCommand.ResetPasswordWithTotp => HandleResetPasswordWithTotp(req),
                    VaultCommand.RebindTotp            => HandleRebindTotp(req, callerSid),

                    _                                  => new VaultResponse { Ok = false, Error = $"Unknown command: {req.Command}" }
                };
            }
            catch (Exception ex)
            {
                // 任何命令处理异常都必须转成一条响应回给客户端，
                // 绝不能让异常逃出本方法——否则 using 会在 handler 中途释放管道，
                // 客户端只看到 ERROR_BROKEN_PIPE (109)，且服务端无日志。
                _logger.LogError(ex, "Command handler failed: {Command}", req.Command);
                resp = new VaultResponse { Ok = false, Error = "Internal error." };
            }

            await WriteResponseAsync(pipe, resp, ct);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 命令处理
    // ──────────────────────────────────────────────────────────────────────

    private VaultResponse HandleQueryStatus()
    {
        // 已退出客户端模拟：报告的是实际服务进程身份，而不是调用方。
        using var identity = WindowsIdentity.GetCurrent();
        return new VaultResponse
        {
            Ok = true,
            Status = new VaultStatus
            {
                HasPassword = _state.HasPassword,
                IsUnlocked = _state.IsUnlocked,
                RegisteredRoots = _state.RegisteredRoots,
                HasTotp = _state.HasTotp,
                ServerPid = Environment.ProcessId,
                ServerSid = identity.User?.Value,
                ServerBuild = typeof(PipeServer).Module.ModuleVersionId.ToString()
            }
        };
    }

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

    private VaultResponse HandleClearPassword(SecurityIdentifier callerSid)
    {
        return _state.TryClearPassword(callerSid, out string err) ? Ok() : Fail(err);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TOTP / 双因素
    // ──────────────────────────────────────────────────────────────────────

    private VaultResponse HandleBeginTotpSetup(VaultRequest req, SecurityIdentifier callerSid)
    {
        // 账号标签用于 otpauth URI，取调用方 SID 便于用户在验证器里辨认
        string account = string.IsNullOrWhiteSpace(req.Path) ? callerSid.Value : req.Path!;
        return _state.TryBeginTotpSetup(account, out string? uri, out string? secret, out string err)
            ? new VaultResponse { Ok = true, OtpAuthUri = uri, SecretBase32 = secret }
            : Fail(err);
    }

    private VaultResponse HandleVerifyTotpSetup(VaultRequest req, SecurityIdentifier callerSid)
    {
        if (string.IsNullOrEmpty(req.TotpCode)) return Fail("totpCode is required.");
        return _state.TryConfirmTotpSetup(req.TotpCode!, out string err) ? Ok() : Fail(err);
    }

    private VaultResponse HandleResetPasswordWithTotp(VaultRequest req)
    {
        if (string.IsNullOrEmpty(req.TotpCode)) return Fail("totpCode is required.");

        // 新密码：优先用 newPassword，没有则用 newPassword2 占位
        string? newPassword = req.NewPassword ?? req.NewPasswordConfirm;
        if (string.IsNullOrEmpty(newPassword)) return Fail("New password is required.");

        return _state.TryResetPasswordWithTotp(req.TotpCode!, newPassword!, out string err)
            ? Ok() : Fail(err);
    }

    private VaultResponse HandleRebindTotp(VaultRequest req, SecurityIdentifier callerSid)
    {
        if (string.IsNullOrEmpty(req.TotpCode)) return Fail("totpCode is required.");
        if (string.IsNullOrEmpty(req.PasswordForRebind)) return Fail("passwordForRebind is required.");

        string account = string.IsNullOrWhiteSpace(req.Path) ? callerSid.Value : req.Path!;
        return _state.TryRebindTotp(req.PasswordForRebind!, req.TotpCode!, account,
                                    out string? uri, out string? secret, out string err)
            ? new VaultResponse { Ok = true, OtpAuthUri = uri, SecretBase32 = secret }
            : Fail(err);
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

    /// <summary>
    /// 判断已捕获的调用方身份是否为 SYSTEM 或 Administrators 组成员。
    /// 必须在 <c>RunAsClient</c> 的模拟上下文中调用（或在模拟期间捕获其结果）。
    /// </summary>
    internal static bool IsAuthorizedIdentity(WindowsIdentity id)
        => id.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
        || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// 未提升的 GUI 必须能发起的日常命令。它们自身靠应用密码或只读状态兜底，
    /// 因此不要求调用方是管理员。
    /// <para>
    /// 特例：在<b>尚未启用应用锁</b>（<see cref="VaultState.HasPassword"/> 为 false）的
    /// 引导阶段，允许普通已登录用户登记根并设置首个密码——此时本就没有锁可保护，
    /// 首个启用者即成为所有者。一旦密码已设，这两条命令即恢复「仅管理员」。
    /// </para>
    /// </summary>
    private static bool IsDailyCommand(string command, VaultState state)
    {
        switch (command)
        {
            case VaultCommand.QueryStatus:
            case VaultCommand.Unlock:
            case VaultCommand.Lock:
            // 忘记密码重置：用户此刻本就进不来，靠 TOTP 动态码 + 限流兜底，不要求管理员
            case VaultCommand.ResetPasswordWithTotp:
                return true;

            // 引导阶段（尚无密码）：允许普通用户登记根 + 设置首个密码
            case VaultCommand.RegisterRoot:
            case VaultCommand.SetPassword:
                return !state.HasPassword;

            default:
                return false;
        }
    }
}
