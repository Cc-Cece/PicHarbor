// PicHarbor.Vault — 入口点
// 同一 exe 三种角色：
//   install / uninstall  —— 服务自注册（需管理员）
//   uninstall-data       —— 删除服务私有数据（需管理员）
//   status               —— 打印当前状态
//   --vault-service      —— 服务模式主体（由 Windows 服务控制管理器拉起）
//   无参数（默认）        —— 前台运行（调试用）

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PicHarbor.Vault;

string[] cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? command = cmdArgs.FirstOrDefault();

switch (command)
{
    case "install":
        return RunInstall();

    case "uninstall":
        return RunUninstall();

    case "uninstall-data":
        return RunUninstallData();

    case "status":
        return RunStatus();
}

// 服务模式或前台调试模式
return await RunHostAsync(cmdArgs);

// ──────────────────────────────────────────────────────────────────────────────

static int RunInstall()
{
    Console.WriteLine("正在安装 PicHarbor Vault 服务…");

    if (!ServiceInstaller.Install(out string msg))
    {
        Console.Error.WriteLine(msg);
        return 1;
    }
    Console.WriteLine(msg);

    if (!ServiceInstaller.Start(out string startMsg))
    {
        Console.Error.WriteLine(startMsg);
        return 1;
    }
    Console.WriteLine(startMsg);
    Console.WriteLine("完成。");
    return 0;
}

static int RunUninstall()
{
    Console.WriteLine("正在卸载 PicHarbor Vault 服务…");
    if (!ServiceInstaller.Uninstall(out string msg))
    {
        Console.Error.WriteLine(msg);
        return 1;
    }
    Console.WriteLine(msg);
    Console.WriteLine("完成。");
    return 0;
}

static int RunUninstallData()
{
    // 彻底移除：删服务数据目录（含密码校验值、登记根、TOTP 状态）
    string dir = VaultStore.Directory;
    if (!Directory.Exists(dir))
    {
        Console.WriteLine("服务数据目录不存在，无需清理。");
        return 0;
    }

    try
    {
        Directory.Delete(dir, recursive: true);
        Console.WriteLine($"已删除服务数据目录：{dir}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"删除服务数据目录失败：{ex.Message}");
        return 1;
    }
}

static int RunStatus()
{
    bool installed = ServiceInstaller.IsInstalled();
    bool hasPassword = PasswordStore.HasPassword;
    var roots = RootStore.Load();

    Console.WriteLine($"服务已安装：{installed}");
    Console.WriteLine($"已设置应用密码：{hasPassword}");
    Console.WriteLine($"已登记根数量：{roots.Count}");
    foreach (string r in roots) Console.WriteLine($"  - {r}");
    return 0;
}

static async Task<int> RunHostAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // 某个 BackgroundService 抛异常时不要直接停整个 Host，否则服务会立刻停。
    builder.Services.Configure<HostOptions>(o =>
    {
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    });

    builder.Services.AddSingleton<VaultState>();
    builder.Services.AddHostedService<PipeServer>();
    builder.Services.AddHostedService<SessionLockWatcher>();

    // 以 Windows 服务方式运行（由服务控制管理器拉起时生效）
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = ServiceInstaller.ServiceName;
    });

    var host = builder.Build();

    // PipeServer performs startup protection once while keeping status requests available.

    await host.RunAsync();
    return 0;
}
