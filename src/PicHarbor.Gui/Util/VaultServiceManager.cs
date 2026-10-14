// PicHarbor.Gui — Vault 服务生命周期管理（GUI 侧）
// 让 GUI 在没有独立 Vault exe 的前提下，按需自行安装/卸载 Vault 服务：
//   · 服务 binPath 指向本 exe（PicHarbor.Gui.exe）+ --vault-service
//   · 安装/卸载需要管理员权限，这里用 runas 自我提权重启本 exe 的管理子命令
// 用户全程只接触一个 PicHarbor.Gui.exe。

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using PicHarbor.Gui.Vault;

namespace PicHarbor.Gui.Util;

/// <summary>
/// GUI 侧的服务管理入口。所有需要提权的动作都通过 <see cref="RunElevated"/> 触发，
/// 由本 exe 的管理子命令（见 <c>Program.cs</c>）在提权进程中执行。
/// </summary>
internal static class VaultServiceManager
{
    /// <summary>当前进程是否已提权（管理员）。</summary>
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>本 exe 的完整路径（服务注册时将指向它）。</summary>
    private static string ExePath
        => Environment.ProcessPath
           ?? throw new InvalidOperationException(
               App.GetString("VaultErrExePathUnknown", "无法确定当前可执行文件路径。"));

    // ──────────────────────────────────────────────────────────────────────
    // 服务可达性
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>通过管道查询服务是否在线且可用（能返回状态即视为在线）。</summary>
    public static async Task<bool> IsServiceReachableAsync()
    {
        var status = await VaultClient.QueryStatusAsync();
        return status is not null;
    }

    /// <summary>轮询等待服务就绪（安装并启动后调用）。</summary>
    public static async Task<bool> WaitUntilReachableAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (await IsServiceReachableAsync()) return true;
            await Task.Delay(300, ct);
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 安装 / 卸载（自我提权）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 安装并启动 Vault 服务。若当前已提权则直接执行子命令，
    /// 否则弹一次 UAC 提权。返回是否成功。
    /// </summary>
    public static async Task<(bool ok, string message)> EnsureServiceInstalledAsync()
    {
        int code = await RunElevatedAsync("--install-vault-service").ConfigureAwait(false);
        bool ok = code == 0;
        if (!ok)
            return (false, string.Format(
                App.GetString("VaultErrInstallFailed", "安装 Vault 服务失败（退出码 {0}）。"), code));

        bool reachable = await WaitUntilReachableAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var response = await VaultClient.QueryStatusResponseAsync().ConfigureAwait(false);
        if (reachable && response is { Ok: true, Status: not null })
        {
            string expectedBuild = typeof(PicHarbor.Vault.PipeServer).Module.ModuleVersionId.ToString();
            if (response.Status.ServerSid != "S-1-5-18")
                return (false, App.GetString("VaultErrNotLocalSystem",
                    "管道响应方不是 LocalSystem 服务，请检查同名调试进程。"));
            if (response.Status.ServerBuild != expectedBuild)
                return (false, App.GetString("VaultErrBuildMismatch",
                    "服务仍运行旧版本，状态响应的构建标识与当前 GUI 不一致。"));
            return (true, App.GetString("VaultMsgInstallOk",
                "Vault 服务已安装并启动，普通客户端状态查询成功。"));
        }
        return (false, string.Format(
            App.GetString("VaultErrStatusQueryFailed", "服务安装完成，但状态查询失败：{0}"),
            response?.Error ?? App.GetString("VaultMsgMissingStatus", "响应缺少 status。")));
    }

    /// <summary>停止并删除 Vault 服务（保留服务数据）。</summary>
    public static async Task<(bool ok, string message)> UninstallServiceAsync()
    {
        int code = await RunElevatedAsync("--uninstall-vault-service").ConfigureAwait(false);
        return code == 0
            ? (true, App.GetString("VaultMsgUninstalled", "Vault 服务已卸载。"))
            : (false, string.Format(
                App.GetString("VaultErrUninstallFailed", "卸载 Vault 服务失败（退出码 {0}）。"), code));
    }

    internal static Task<(bool ok, string message)> RemoveAppLockComponentsAsync()
        => AppLockRemovalWorkflow.RunAsync(DisableAppLockAsync, () =>
        {
            var config = PicHarbor.Gui.Config.AppSettings.Load();
            config.AppLockWasEnabled = false;
            PicHarbor.Gui.Config.AppSettings.Save(config);
        }, UninstallServiceAsync, PurgeVaultDataAsync);

    /// <summary>删除服务数据目录（%ProgramData%\PicHarbor）。</summary>
    public static async Task<(bool ok, string message)> PurgeVaultDataAsync()
    {
        int code = await RunElevatedAsync("--purge-vault-data").ConfigureAwait(false);
        return code == 0
            ? (true, App.GetString("VaultMsgDataPurged", "服务数据已清除。"))
            : (false, string.Format(
                App.GetString("VaultErrPurgeFailed", "清除服务数据失败（退出码 {0}）。"), code));
    }

    /// <summary>
    /// 彻底关闭应用锁（清密码 + 解除全部登记根）。服务端要求调用方为管理员，
    /// 因此这里走提权路径，由本 exe 的 --disable-app-lock 子命令连管道执行。
    /// </summary>
    public static async Task<(bool ok, string message)> DisableAppLockAsync()
    {
        if (IsElevated)
        {
            var resp = await VaultClient.ClearPasswordAsync().ConfigureAwait(false);
            return resp is { Ok: true }
                ? (true, App.GetString("VaultMsgAppLockDisabled", "应用锁已关闭。"))
                : (false, resp?.Error ?? App.GetString("VaultErrDisableFailed", "关闭应用锁失败。"));
        }

        int code = await RunElevatedAsync("--disable-app-lock").ConfigureAwait(false);
        return code == 0
            ? (true, App.GetString("VaultMsgAppLockDisabled", "应用锁已关闭。"))
            : (false, App.GetString("VaultErrDisableFailedElevated",
                "关闭应用锁失败（可能需要管理员权限或服务未运行）。"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // 启用向导 / 双因素绑定（提权 + JSON 报告回传）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 提权执行「启用应用锁」：装服务 → 登记若干归档根 → 设密码。
    /// 密码经临时文件传递（禁止命令行明文），报告经临时 JSON 文件回传。
    /// 返回解析后的报告；UAC 被用户取消时 <see cref="EnableReport.FatalError"/> 为 null
    /// 且各字段为空，另由返回值 <c>uacCancelled=true</c> 标识。
    /// </summary>
    public static async Task<(EnableReport? report, bool uacCancelled)> EnableAppLockElevatedAsync(
        IReadOnlyList<string> roots, string password)
    {
        if (roots.Count == 0) return (null, false);

        string tempDir = Path.Combine(Path.GetTempPath(), "PicHarborElevate");
        Directory.CreateDirectory(tempDir);
        string pwdFile = Path.Combine(tempDir, $"pwd_{Guid.NewGuid():N}.tmp");
        string reportFile = Path.Combine(tempDir, $"enable_{Guid.NewGuid():N}.json");

        try
        {
            // 密码写入临时文件（仅当前用户可读；提权进程同机同用户可读）
            File.WriteAllText(pwdFile, password);

            // 路径可能含空格/反斜杠：用 | 分隔的单参数规避 Shell 引号解析问题
            string rootsArg = string.Join("|", roots);
            using var requestingUser=System.Security.Principal.WindowsIdentity.GetCurrent();
            string ownerSid=requestingUser.User!.Value;
            string argument =
                $"--vault-enable --roots \"{rootsArg}\" --password-file \"{pwdFile}\" --report \"{reportFile}\" --owner-sid \"{ownerSid}\"";

            int code = await RunElevatedAsync(argument).ConfigureAwait(false);
            if (code == -1 && !File.Exists(reportFile))
                return (null, true); // UAC 取消

            var report = ReadReport<EnableReport>(reportFile);
            return (report, false);
        }
        finally
        {
            try { if (File.Exists(pwdFile)) File.Delete(pwdFile); } catch { }
            try { if (File.Exists(reportFile)) File.Delete(reportFile); } catch { }
        }
    }

    /// <summary>提权生成 TOTP 密钥（出码）。UAC 取消时返回 (null, true)。</summary>
    public static async Task<(TotpBeginReport? report, bool uacCancelled)> BeginTotpElevatedAsync(string account)
    {
        string reportFile = Path.Combine(Path.GetTempPath(), $"totp_begin_{Guid.NewGuid():N}.json");
        try
        {
            string argument = $"--vault-begin-totp --account \"{account}\" --report \"{reportFile}\"";
            int code = await RunElevatedAsync(argument).ConfigureAwait(false);
            if (code == -1 && !File.Exists(reportFile)) return (null, true);

            return (ReadReport<TotpBeginReport>(reportFile), false);
        }
        finally
        {
            try { if (File.Exists(reportFile)) File.Delete(reportFile); } catch { }
        }
    }

    /// <summary>提权确认 TOTP 绑定（提交动态码）。UAC 取消时返回 (null, true)。</summary>
    public static async Task<(TotpVerifyReport? report, bool uacCancelled)> VerifyTotpElevatedAsync(string code)
    {
        string reportFile = Path.Combine(Path.GetTempPath(), $"totp_verify_{Guid.NewGuid():N}.json");
        try
        {
            string argument = $"--vault-verify-totp --code \"{code}\" --report \"{reportFile}\"";
            int exitCode = await RunElevatedAsync(argument).ConfigureAwait(false);
            if (exitCode == -1 && !File.Exists(reportFile)) return (null, true);

            return (ReadReport<TotpVerifyReport>(reportFile), false);
        }
        finally
        {
            try { if (File.Exists(reportFile)) File.Delete(reportFile); } catch { }
        }
    }

    /// <summary>
    /// 提权修改应用密码。身份确认二选一：
    ///   <paramref name="oldPassword"/> 非空 → 走 ChangePassword（旧密码验证）
    ///   否则 <paramref name="totpCode"/> 非空 → 走 ResetPasswordWithTotp（TOTP 验证）
    /// 两个密码都经临时文件传递（禁止命令行明文）。UAC 取消时返回 (null, true)。
    /// </summary>
    public static async Task<(ChangePasswordReport? report, bool uacCancelled)> ChangePasswordElevatedAsync(
        string? oldPassword, string? totpCode, string newPassword)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "PicHarborElevate");
        Directory.CreateDirectory(tempDir);
        string newPwdFile = Path.Combine(tempDir, $"newpwd_{Guid.NewGuid():N}.tmp");
        string reportFile = Path.Combine(tempDir, $"chpwd_{Guid.NewGuid():N}.json");

        bool useTotp = !string.IsNullOrEmpty(totpCode) && string.IsNullOrEmpty(oldPassword);
        string oldPwdFile = Path.Combine(tempDir, $"pwd_{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(newPwdFile, newPassword);

            string argument;
            if (useTotp)
            {
                argument = $"--vault-change-password --mode totp --code \"{totpCode}\" " +
                           $"--new-password-file \"{newPwdFile}\" --report \"{reportFile}\"";
            }
            else
            {
                File.WriteAllText(oldPwdFile, oldPassword ?? "");
                argument = $"--vault-change-password --mode password --password-file \"{oldPwdFile}\" " +
                           $"--new-password-file \"{newPwdFile}\" --report \"{reportFile}\"";
            }

            int code = await RunElevatedAsync(argument).ConfigureAwait(false);
            if (code == -1 && !File.Exists(reportFile)) return (null, true);

            return (ReadReport<ChangePasswordReport>(reportFile), false);
        }
        finally
        {
            try { if (File.Exists(oldPwdFile)) File.Delete(oldPwdFile); } catch { }
            try { if (File.Exists(newPwdFile)) File.Delete(newPwdFile); } catch { }
            try { if (File.Exists(reportFile)) File.Delete(reportFile); } catch { }
        }
    }

    /// <summary>
    /// 探测「Vault 服务是否已安装」——用于决定「彻底移除组件」按钮是否显示。
    /// 与 <see cref="IsServiceReachableAsync"/> 的区别：服务已安装但未启动时后者为 false，
    /// 而本方法仍返回 true（按服务是否注册判断）。
    /// </summary>
    public static bool IsServiceInstalled()
    {
        try { return PicHarbor.Vault.ServiceInstaller.IsInstalled(); }
        catch { return true; } // 探测失败时保守显示，避免误藏「移除组件」入口
    }

    private static T? ReadReport<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VaultServiceManager] 读取报告失败：{ex.Message}");
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 自我提权执行
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 以管理员身份运行本 exe 的指定参数。已提权时直接跑（同步等待），
    /// 否则用 runas 动词重启并等待其退出。返回子进程退出码；用户取消 UAC 返回 -1。
    /// </summary>
    private static async Task<int> RunElevatedAsync(string argument)
    {
        if (IsElevated)
        {
            // 已提权：直接在同一进程里同步执行，避免多起一个进程
            return await Task.Run(() => ExecuteCommandInline(argument)).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo
        {
            FileName        = ExePath,
            Arguments       = argument,
            UseShellExecute = true,   // 触发 runas 需要 ShellExecute
            Verb            = "runas", // 弹 UAC
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return -1;
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：用户拒绝了 UAC
            return -1;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VaultServiceManager] 提权失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 在已提权的当前进程内联执行管理子命令，返回退出码。
    /// 简单命令（无参数）走精确匹配；带参数的命令解析后转发到 <see cref="Program"/> 的
    /// 处理函数——保持「一处实现 + 一处转发」，避免与 Program.Main 出现两份参数解析。
    /// </summary>
    private static int ExecuteCommandInline(string argument)
    {
        try
        {
            switch (argument)
            {
                case "--install-vault-service":
                    return PicHarbor.Vault.ServiceInstaller.Install(out _)
                           && PicHarbor.Vault.ServiceInstaller.Start(out _) ? 0 : 1;
                case "--uninstall-vault-service":
                    return PicHarbor.Vault.ServiceInstaller.Uninstall(out _) ? 0 : 1;
                case "--purge-vault-data":
                    return PurgeData();
            }

            // 带参数的命令：按空格拆分为 args 数组，转发到 Program 的处理函数。
            string[] args = SplitArguments(argument);
            if (args.Length == 0) return 1;

            return args[0].ToLowerInvariant() switch
            {
                "--vault-enable"      => Program.RunVaultEnable(args),
                "--vault-begin-totp"  => Program.RunVaultBeginTotp(args),
                "--vault-verify-totp" => Program.RunVaultVerifyTotp(args),
                "--vault-change-password" => Program.RunVaultChangePassword(args),
                _                     => 1,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VaultServiceManager] 内联执行失败：{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 按空格拆分命令行，识别双引号包裹的参数段（引号内空格保留）。
    /// 我们自己在 <c>*ElevatedAsync</c> 里构造参数，故只需处理 <c>"..."</c> 这一种形式。
    /// </summary>
    private static string[] SplitArguments(string argument)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in argument)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return [.. parts];
    }

    private static int PurgeData()
    {
        try
        {
            string dir = PicHarbor.Vault.VaultStore.Directory;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
