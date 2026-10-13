// PicHarbor.Vault — 服务自注册
// 让同一个 exe 自己完成服务的安装与卸载，无需外部 sc.exe 脚本。
// 只应由提权后的进程调用（install / uninstall 都需要管理员）。

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public static partial class ServiceInstaller
{
    public const string ServiceName        = "PicHarborVault";
    public const string ServiceDisplayName = "PicHarbor Vault";
    public const string ServiceDescription =
        "Protects PicHarbor photo archives by managing directory access control. " +
        "Runs as LocalSystem.";

    /// <summary>服务主体启动参数：同一 exe 以服务模式运行。</summary>
    private const string ServiceArgs = "--vault-service";

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateService(
        IntPtr hSCManager, string lpServiceName, string? lpDisplayName,
        uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(
        IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword,
        string? lpDisplayName);

    [LibraryImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(IntPtr hService);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr hService, uint dwControl, ref ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr hSCObject);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint   dwServiceType;
        public uint   dwCurrentState;
        public uint   dwControlsAccepted;
        public uint   dwWin32ExitCode;
        public uint   dwServiceSpecificExitCode;
        public uint   dwCheckPoint;
        public uint   dwWaitHint;
    }

    // 访问权限
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    private const uint SC_MANAGER_CONNECT        = 0x0001;
    private const uint SERVICE_QUERY_STATUS      = 0x0004;
    private const uint SERVICE_CHANGE_CONFIG     = 0x0002;
    private const uint SERVICE_START             = 0x0010;
    private const uint SERVICE_STOP              = 0x0020;
    private const uint SERVICE_ALL_ACCESS        = 0xF01FF;

    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START        = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL      = 0x00000001;
    private const uint SERVICE_CONTROL_STOP      = 0x00000001;
    private const uint SERVICE_STOPPED           = 0x00000001;

    /// <summary>
    /// 安装（或更新）服务。必须以管理员身份运行。
    /// 服务设为自动启动、非延迟启动，账户为 LocalSystem。
    /// </summary>
    public static bool Install(out string message)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            message = "无法确定当前可执行文件路径。";
            return false;
        }

        string binPath;

        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            message = $"打开服务管理器失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}（需要管理员权限）";
            return false;
        }

        try
        {
            // Stop first so an existing deployment is not overwritten while its image is loaded.
            if(IsInstalled() && !Stop(out message)) return false;
            try { exePath=ServiceImageStore.Prepare(exePath); }
            catch(Exception ex) { message=$"无法准备受保护的服务程序：{ex.Message}";return false; }
            binPath=$"\"{exePath}\" {ServiceArgs}";
            // 若已存在则更新配置，否则创建
            IntPtr existing = OpenService(scm, ServiceName, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_STATUS);
            if (existing != IntPtr.Zero)
            {
                bool ok = ChangeServiceConfig(
                    existing,
                    SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                    binPath, null, IntPtr.Zero, null, "LocalSystem", null, ServiceDisplayName);
                int configError = ok ? 0 : Marshal.GetLastWin32Error();
                CloseServiceHandle(existing);

                if (!ok)
                {
                    message = $"更新服务配置失败：{new Win32Exception(configError).Message}";
                    return false;
                }
                // ChangeServiceConfig 不会让已运行进程重新加载二进制。
                // 必须确认旧进程停止，调用方随后 Start 才会运行当前 exe。
                if (!Stop(out message)) return false;
                message = "服务配置已更新，旧服务已停止，等待启动新版本。";
            }
            else
            {
                IntPtr svc = CreateService(
                    scm, ServiceName, ServiceDisplayName,
                    SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                    binPath, null, IntPtr.Zero, null, "LocalSystem", null);
                if (svc == IntPtr.Zero)
                {
                    message = $"创建服务失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return false;
                }
                CloseServiceHandle(svc);
                message = "服务创建成功。";
            }

            using(var serviceKey=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+ServiceName,writable:true))
                serviceKey?.SetValue("Environment",new[]{"DOTNET_BUNDLE_EXTRACT_BASE_DIR="+Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"PicHarbor","Service","BundleCache")},Microsoft.Win32.RegistryValueKind.MultiString);
            return true;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>启动服务（安装后调用）。幂等：已运行则返回成功。</summary>
    public static bool Start(out string message)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            message = $"打开服务管理器失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        try
        {
            IntPtr svc = OpenService(scm, ServiceName, SERVICE_START | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                message = $"打开服务失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            try
            {
                if (StartService(svc, 0, IntPtr.Zero))
                {
                    message = "服务已启动。";
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                // 1056 = ERROR_SERVICE_ALREADY_RUNNING
                if (err == 1056)
                {
                    message = "服务已在运行。";
                    return true;
                }

                message = $"启动服务失败：{new Win32Exception(err).Message}";
                return false;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>等待服务完全停止；更新配置后必须调用，不能把 1056 当作更新成功。</summary>
    public static bool Stop(out string message)
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                if (controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            message = "服务已停止。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"停止旧服务失败，未启动新版本：{ex.Message}";
            return false;
        }
    }

    /// <summary>停止并删除服务。必须以管理员身份运行。</summary>
    public static bool Uninstall(out string message)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            message = $"打开服务管理器失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}（需要管理员权限）";
            return false;
        }

        try
        {
            IntPtr svc = OpenService(scm, ServiceName, SERVICE_STOP | SERVICE_QUERY_STATUS | SERVICE_ALL_ACCESS);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                // 1060 = ERROR_SERVICE_DOES_NOT_EXIST
                if (err == 1060)
                {
                    message = "服务未安装。";
                    return true;
                }
                message = $"打开服务失败：{new Win32Exception(err).Message}";
                return false;
            }

            try
            {
                // 先尝试停止（失败也无妨——可能本就在停止中）
                var status = new ServiceStatus { dwServiceType = SERVICE_WIN32_OWN_PROCESS };
                ControlService(svc, SERVICE_CONTROL_STOP, ref status);

                // 等一小会儿让服务退出
                for (int i = 0; i < 20; i++)
                {
                    var s = new ServiceStatus { dwServiceType = SERVICE_WIN32_OWN_PROCESS };
                    if (QueryStatus(svc, ref s) && s.dwCurrentState == SERVICE_STOPPED) break;
                    Thread.Sleep(250);
                }

                if (!DeleteService(svc))
                {
                    message = $"删除服务失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    return false;
                }

                message = "服务已删除。";
                return true;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr hService, ref ServiceStatus lpServiceStatus);

    private static bool QueryStatus(IntPtr svc, ref ServiceStatus status)
        => QueryServiceStatus(svc, ref status);

    /// <summary>查询服务是否已注册。</summary>
    public static bool IsInstalled()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            CloseServiceHandle(svc);
            return true;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
