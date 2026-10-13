// PicHarbor.Gui — 统一入口点
// 同一个 exe 扮演两种角色：
//   ① GUI 主程序（默认，无参数或普通参数）——启动 WPF 主窗口。
//   ② Vault 服务宿主（--vault-service）——由 Windows 服务控制管理器拉起，
//      以 LocalSystem 身份运行目录 ACL 管理服务（无界面）。
//
// 服务注册时 binPath 指向本 exe + --vault-service，因此用户只需持有
// 单个 PicHarbor.Gui.exe 即可完成全部功能，无需额外分发第二个文件。
//
// 附带的管理子命令（各需一次 UAC 提权，由 GUI 在启用/关闭应用锁时内部触发）：
//   --install-vault-service     安装并启动服务
//   --uninstall-vault-service   停止并删除服务（保留服务数据）
//   --purge-vault-data          删除 %ProgramData%\PicHarbor 全部私有数据
//
// 启用向导与双因素绑定用的提权子命令（结果经 --report 写 JSON 报告文件回传，
// 避免依赖标准输出；密码经 --password-file 临时文件传递，禁止命令行明文）：
//   --vault-enable               装服务 → 登记若干根 → 设密码
//   --vault-begin-totp           生成 TOTP 密钥并回传 otpauth URI
//   --vault-verify-totp          用 6 位动态码确认 TOTP 绑定
//   --vault-change-password      改密码（旧密码 或 TOTP 码 二选一身份确认）

using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PicHarbor.Vault;
namespace PicHarbor.Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 归一化参数，便于大小写不敏感匹配
        string? command = args.FirstOrDefault();

        if (Matches(command, "--vault-service"))
            return RunVaultService(args);

        if (Matches(command, "--install-vault-service"))
            return RunInstallService();

        if (Matches(command, "--uninstall-vault-service"))
            return RunUninstallService();

        if (Matches(command, "--purge-vault-data"))
            return RunPurgeVaultData();

        if (Matches(command, "--disable-app-lock"))
            return RunDisableAppLock();

        // 启用向导 / 双因素绑定的提权子命令（internal，供 VaultServiceManager 内联转发复用）
        if (Matches(command, "--vault-enable"))
            return RunVaultEnable(args);

        if (Matches(command, "--vault-begin-totp"))
            return RunVaultBeginTotp(args);

        if (Matches(command, "--vault-verify-totp"))
            return RunVaultVerifyTotp(args);

        if (Matches(command, "--vault-change-password"))
            return RunVaultChangePassword(args);

        // 诊断不启动 WPF、不改归档；修复仅执行既有 UAC 安装/重启流程。
        if (Matches(command, "--vault-status") || Matches(command, "--repair-vault-service"))
            return RunVaultDiagnostic(args, repair: Matches(command, "--repair-vault-service"));

        // 默认：启动 WPF GUI
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunVaultDiagnostic(string[] args, bool repair)
    {
        try
        {
            (bool ok, string message)? installation = repair
                ? Util.VaultServiceManager.EnsureServiceInstalledAsync().GetAwaiter().GetResult()
                : null;
            var response = Vault.VaultClient.QueryStatusResponseAsync().GetAwaiter().GetResult();
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var result = new
            {
                clientSid = identity.User?.Value,
                clientElevated = Util.VaultServiceManager.IsElevated,
                clientPid = Environment.ProcessId,
                expectedServerBuild = typeof(PipeServer).Module.ModuleVersionId.ToString(),
                repairSucceeded = installation?.ok,
                repairMessage = installation?.message,
                response
            };
            string json = System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            // 显式指定报告路径，否则只向标准输出写入；不记录密码或 TOTP 密钥。
            if (args.Length == 3 && Matches(args[1], "--report")) File.WriteAllText(args[2], json);
            else Console.WriteLine(json);
            return response is { Ok: true, Status: not null }
                && response.Status.ServerSid == "S-1-5-18"
                && response.Status.ServerBuild == typeof(PipeServer).Module.ModuleVersionId.ToString()
                && (installation is null || installation.Value.ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (args.Length == 3 && Matches(args[1], "--report"))
                File.WriteAllText(args[2], ex.ToString());
            return 1;
        }
    }

    private static bool Matches(string? arg, string expected)
        => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);

    // ──────────────────────────────────────────────────────────────────────
    // 服务宿主模式
    // ──────────────────────────────────────────────────────────────────────

    private static int RunVaultService(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // 关键：某个 BackgroundService 抛异常时不要直接停整个 Host（否则服务会立刻停）。
        // 我们的后台服务各自已做异常兜底，这里再兜一层，保证服务进程长活。
        builder.Services.Configure<HostOptions>(o =>
        {
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        builder.Services.AddSingleton<VaultState>();
        builder.Services.AddHostedService<PipeServer>();
        builder.Services.AddHostedService<SessionLockWatcher>();

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ServiceInstaller.ServiceName;
        });

        var host = builder.Build();

        // 构建完成后立即做一次首次收回（BackgroundService 里也会再做一次，双重保险）
        using (var scope = host.Services.CreateScope())
        {
            var state = scope.ServiceProvider.GetRequiredService<VaultState>();
            state.RevokeAllOnStartup();
        }

        host.Run();
        return 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 管理子命令（提权执行）
    // ──────────────────────────────────────────────────────────────────────

    private static int RunInstallService()
    {
        if (!ServiceInstaller.Install(out string msg))
            return 1;
        if (!ServiceInstaller.Start(out string startMsg))
            return 2;
        return 0;
    }

    private static int RunUninstallService()
    {
        return ServiceInstaller.Uninstall(out _) ? 0 : 1;
    }

    private static int RunPurgeVaultData()
    {
        try
        {
            string dir = VaultStore.Directory;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// 提权后执行「彻底关闭应用锁」：通过管道向服务发 ClearPassword。
    /// 服务端要求调用方为管理员/SYSTEM，因此必须走提权进程。
    /// </summary>
    private static int RunDisableAppLock()
    {
        try
        {
            var resp = Vault.VaultClient.ClearPasswordAsync().GetAwaiter().GetResult();
            return resp is { Ok: true } ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 启用向导 / 双因素：提权子命令（结果写 --report JSON 文件回传）
    // ──────────────────────────────────────────────────────────────────────
    //
    // 参数约定（均由 GUI 侧构造）：
    //   --vault-enable        --roots <p1|p2|...> --password-file <tmp> [--report <json>]
    //   --vault-begin-totp    [--account <name>] [--report <json>]
    //   --vault-verify-totp   --code <6位> [--report <json>]
    //
    // 退出码：
    //   0 = 完全成功
    //   1 = 前置失败（服务不可用 / 参数缺失等）
    //   2 = 部分根登记失败（密码已设）
    //   3 = 高危中间态：有根登记成功但密码设置失败

    /// <summary>从参数数组里取 <c>--name value</c> 形式的值；缺省返回 null。</summary>
    internal static string? GetOption(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (Matches(args[i], name)) return args[i + 1];
        }
        return null;
    }

    /// <summary>写 JSON 报告到 --report 指定文件；未指定则写标准输出。</summary>
    internal static void WriteReport(string[] args, object report)
    {
        string json = JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true });
        string? reportPath = GetOption(args, "--report");
        if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, json);
        else Console.WriteLine(json);
    }

    /// <summary>异常兜底：把致命错误写进报告，保证 GUI 侧总能读到结构化结果。</summary>
    internal static void WriteFatal(string[] args, Exception ex)
    {
        try
        {
            WriteReport(args, new EnableReport
            {
                ClientPid = Environment.ProcessId,
                ClientElevated = Util.VaultServiceManager.IsElevated,
                FatalError = ex.ToString()
            });
        }
        catch { /* 报告写入本身失败时无计可施 */ }
    }

    internal static int RunVaultEnable(string[] args)
    {
        var report = new EnableReport
        {
            ClientPid = Environment.ProcessId,
            ClientElevated = Util.VaultServiceManager.IsElevated
        };
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            report.ClientSid = identity.User?.Value;

            string? rootsArg = GetOption(args, "--roots");
            string? passwordFile = GetOption(args, "--password-file");

            if (string.IsNullOrWhiteSpace(rootsArg) || string.IsNullOrWhiteSpace(passwordFile))
            {
                report.FatalError = "Missing --roots or --password-file.";
                WriteReport(args, report);
                return 1;
            }

            string[] roots = rootsArg.Split('|', StringSplitOptions.RemoveEmptyEntries |
                                                 StringSplitOptions.TrimEntries);
            if (roots.Length == 0)
            {
                report.FatalError = "No roots provided.";
                WriteReport(args, report);
                return 1;
            }

            // 密码从临时文件读取；无论成败都在 finally 删除。
            string password = "";
            try
            {
                if (!File.Exists(passwordFile))
                {
                    report.FatalError = "Password file not found.";
                    WriteReport(args, report);
                    return 1;
                }
                password = File.ReadAllText(passwordFile);
            }
            finally
            {
                try { if (File.Exists(passwordFile)) File.Delete(passwordFile); } catch { }
            }

            if (string.IsNullOrEmpty(password))
            {
                report.FatalError = "Empty password.";
                WriteReport(args, report);
                return 1;
            }

            // ① 确保服务在线（可重复调用，已安装则幂等）
            var install = Util.VaultServiceManager.EnsureServiceInstalledAsync().GetAwaiter().GetResult();
            if (!install.ok)
            {
                report.FatalError = install.message;
                WriteReport(args, report);
                return 1;
            }
            report.ServiceReady = true;
            report.ServerPid = QueryServerPid();

            // ② 逐个登记根：收集成败，不中断（部分成功也要保留）
            report.RootsTotal = roots.Length;
            foreach (string root in roots)
            {
                var resp = Vault.VaultClient.RegisterRootAsync(root).GetAwaiter().GetResult();
                if (resp is { Ok: true })
                {
                    report.RootsSucceeded.Add(root);
                }
                else
                {
                    report.RootsFailed.Add(new RootFailure
                    {
                        Path = root,
                        Error = resp?.Error ?? "Register failed (no response)."
                    });
                }
            }

            // ③ 设置应用密码——这是「有锁」的关键一步
            var setResp = Vault.VaultClient.SetPasswordAsync(password,ownerSid:GetOption(args,"--owner-sid")).GetAwaiter().GetResult();
            report.PasswordSet = setResp is { Ok: true };
            if (!report.PasswordSet)
            {
                report.FatalError = setResp?.Error ?? "SetPassword failed (no response).";
            }

            report.ServerPid = QueryServerPid();
            WriteReport(args, report);

            // 退出码语义见方法头注释
            if (!report.PasswordSet) return report.RootsSucceeded.Count > 0 ? 3 : 1;
            return report.RootsFailed.Count > 0 ? 2 : 0;
        }
        catch (Exception ex)
        {
            WriteFatal(args, ex);
            return 1;
        }
    }

    internal static int RunVaultBeginTotp(string[] args)
    {
        var report = new TotpBeginReport { ClientPid = Environment.ProcessId };
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            report.ClientSid = identity.User?.Value;

            string account = GetOption(args, "--account") ?? Environment.UserName;
            var resp = Vault.VaultClient.BeginTotpSetupAsync(account).GetAwaiter().GetResult();
            if (resp is { Ok: true })
            {
                report.OtpAuthUri = resp.OtpAuthUri;
                report.SecretBase32 = resp.SecretBase32;
            }
            else
            {
                report.FatalError = resp?.Error ?? "BeginTotpSetup failed (no response).";
            }

            report.ServerPid = QueryServerPid();
            WriteReport(args, report);
            return report.FatalError is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteFatal(args, ex);
            return 1;
        }
    }

    internal static int RunVaultVerifyTotp(string[] args)
    {
        var report = new TotpVerifyReport { ClientPid = Environment.ProcessId };
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            report.ClientSid = identity.User?.Value;

            string? code = GetOption(args, "--code");
            if (string.IsNullOrWhiteSpace(code))
            {
                report.FatalError = "Missing --code.";
                WriteReport(args, report);
                return 1;
            }

            var resp = Vault.VaultClient.VerifyTotpSetupAsync(code).GetAwaiter().GetResult();
            report.VerifyOk = resp is { Ok: true };
            report.VerifyError = report.VerifyOk ? null : (resp?.Error ?? "Verify failed (no response).");

            report.ServerPid = QueryServerPid();
            WriteReport(args, report);
            return report.VerifyOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteFatal(args, ex);
            return 1;
        }
    }

    /// <summary>
    /// 提权执行「修改密码」。身份确认二选一（由 --mode 指定）：
    ///   --mode password  --password-file &lt;旧的&gt; --new-password-file &lt;新的&gt;   走 ChangePassword
    ///   --mode totp      --code &lt;6位&gt;      --new-password-file &lt;新的&gt;   走 ResetPasswordWithTotp
    /// 两个密码均经临时文件传递，禁止出现在命令行里。
    /// </summary>
    internal static int RunVaultChangePassword(string[] args)
    {
        var report = new ChangePasswordReport { ClientPid = Environment.ProcessId };
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            report.ClientSid = identity.User?.Value;
            report.ClientElevated = Util.VaultServiceManager.IsElevated;

            string mode = (GetOption(args, "--mode") ?? "password").Trim().ToLowerInvariant();
            string? newPasswordFile = GetOption(args, "--new-password-file");

            if (string.IsNullOrWhiteSpace(newPasswordFile))
            {
                report.FatalError = "Missing --new-password-file.";
                WriteReport(args, report);
                return 1;
            }

            // 读新密码（读完即删，避免残留）
            string newPassword = "";
            try
            {
                if (!File.Exists(newPasswordFile))
                {
                    report.FatalError = "New password file not found.";
                    WriteReport(args, report);
                    return 1;
                }
                newPassword = File.ReadAllText(newPasswordFile);
            }
            finally
            {
                try { if (File.Exists(newPasswordFile)) File.Delete(newPasswordFile); } catch { }
            }

            if (string.IsNullOrEmpty(newPassword))
            {
                report.FatalError = "Empty new password.";
                WriteReport(args, report);
                return 1;
            }

            Vault.VaultResponse? resp;

            if (mode == "totp")
            {
                string? code = GetOption(args, "--code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    report.FatalError = "Missing --code.";
                    WriteReport(args, report);
                    return 1;
                }
                resp = Vault.VaultClient.ResetPasswordWithTotpAsync(code, newPassword)
                                 .GetAwaiter().GetResult();
            }
            else
            {
                string? oldPasswordFile = GetOption(args, "--password-file");
                if (string.IsNullOrWhiteSpace(oldPasswordFile))
                {
                    report.FatalError = "Missing --password-file.";
                    WriteReport(args, report);
                    return 1;
                }

                string oldPassword = "";
                try
                {
                    if (!File.Exists(oldPasswordFile))
                    {
                        report.FatalError = "Password file not found.";
                        WriteReport(args, report);
                        return 1;
                    }
                    oldPassword = File.ReadAllText(oldPasswordFile);
                }
                finally
                {
                    try { if (File.Exists(oldPasswordFile)) File.Delete(oldPasswordFile); } catch { }
                }

                if (string.IsNullOrEmpty(oldPassword))
                {
                    report.FatalError = "Empty old password.";
                    WriteReport(args, report);
                    return 1;
                }

                resp = Vault.VaultClient.ChangePasswordAsync(oldPassword, newPassword)
                                 .GetAwaiter().GetResult();
            }

            report.Changed = resp is { Ok: true };
            report.ChangeError = report.Changed ? null : (resp?.Error ?? "Change failed (no response).");

            report.ServerPid = QueryServerPid();
            WriteReport(args, report);
            return report.Changed ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteFatal(args, ex);
            return 1;
        }
    }

    /// <summary>查询服务端 PID（用于检测「两次调用之间服务重启」导致 pending TOTP 丢失）。</summary>
    internal static int QueryServerPid()    {
        try
        {
            var status = Vault.VaultClient.QueryStatusAsync().GetAwaiter().GetResult();
            return status?.ServerPid ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>--vault-enable 的 JSON 报告结构。</summary>
internal sealed class EnableReport
{
    public string? ClientSid { get; set; }
    public bool ClientElevated { get; set; }
    public int ClientPid { get; set; }
    public bool ServiceReady { get; set; }
    public bool PasswordSet { get; set; }
    public int RootsTotal { get; set; }
    public List<string> RootsSucceeded { get; set; } = [];
    public List<RootFailure> RootsFailed { get; set; } = [];
    public string? FatalError { get; set; }
    public int ServerPid { get; set; }
}

internal sealed class RootFailure
{
    public string Path { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>--vault-begin-totp 的 JSON 报告结构。</summary>
internal sealed class TotpBeginReport
{
    public string? ClientSid { get; set; }
    public int ClientPid { get; set; }
    public string? OtpAuthUri { get; set; }
    public string? SecretBase32 { get; set; }
    public string? FatalError { get; set; }
    public int ServerPid { get; set; }
}

/// <summary>--vault-verify-totp 的 JSON 报告结构。</summary>
internal sealed class TotpVerifyReport
{
    public string? ClientSid { get; set; }
    public int ClientPid { get; set; }
    public bool VerifyOk { get; set; }
    public string? VerifyError { get; set; }
    public string? FatalError { get; set; }
    public int ServerPid { get; set; }
}

/// <summary>--vault-change-password 的 JSON 报告结构。</summary>
internal sealed class ChangePasswordReport
{
    public string? ClientSid { get; set; }
    public bool ClientElevated { get; set; }
    public int ClientPid { get; set; }
    public bool Changed { get; set; }
    public string? ChangeError { get; set; }
    public string? FatalError { get; set; }
    public int ServerPid { get; set; }
}
