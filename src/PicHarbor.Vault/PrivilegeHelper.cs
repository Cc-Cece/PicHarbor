// PicHarbor.Vault — 进程特权启用工具
//
// Windows 服务（LocalSystem）的令牌里带有 SeSecurityPrivilege / SeTakeOwnershipPrivilege，
// 但出于安全默认处于「禁用」状态。任何需要读写他人对象安全描述符的操作
// （例如给新建的命名管道设置自定义 DACL）都必须先显式启用 SeSecurityPrivilege，
// 否则会抛 UnauthorizedAccessException。
//
// 这正是早期「服务一启动就停止」的根因之一：PipeServer 调 SetAccessControl 时
// 特权未启用而抛异常。此处提供可复用的启用/禁用封装。

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
internal static partial class PrivilegeHelper
{
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY             = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED    = 0x0002;

    private const uint ERROR_SUCCESS          = 0;
    private const uint ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int  HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint               PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(
        string? lpSystemName, string lpName, out LUID lpLuid);

    [LibraryImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 在当前进程令牌中启用指定特权。返回是否成功启用。
    /// <para>
    /// 若令牌中根本不含该特权（例如未提权的普通用户没有 SeSecurityPrivilege），
    /// 返回 <c>false</c> 而不是抛异常——调用方应据此走降级路径。
    /// </para>
    /// </summary>
    /// <param name="privilegeName">如 "SeSecurityPrivilege"、"SeTakeOwnershipPrivilege"。</param>
    public static bool TryEnable(string privilegeName)
    {
        try
        {
            return EnableCore(privilegeName, enable: true);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在使用完毕后把特权恢复为禁用状态（减小攻击面）。</summary>
    public static void Disable(string privilegeName)
    {
        try { EnableCore(privilegeName, enable: false); } catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 以「临时启用」的形式执行一段操作，结束后恢复原状。
    /// 返回操作是否成功执行（特权启用失败时返回 false，不执行 action）。
    /// </summary>
    public static bool RunWithPrivilege(string privilegeName, Action action)
    {
        if (!TryEnable(privilegeName)) return false;
        try
        {
            action();
            return true;
        }
        finally
        {
            Disable(privilegeName);
        }
    }

    /// <summary>检查当前令牌是否已包含（无论启用与否）指定特权。</summary>
    public static bool HasPrivilege(string privilegeName)
    {
        if (!LookupPrivilegeValue(null, privilegeName, out LUID luid)) return false;

        IntPtr token = IntPtr.Zero;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;

        try
        {
            return HasPrivilegeInToken(token, luid);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool EnableCore(string privilegeName, bool enable)
    {
        if (!OpenProcessToken(GetCurrentProcess(),
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed");

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid       = luid,
                    Attributes = enable ? SE_PRIVILEGE_ENABLED : 0
                }
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed");

            // AdjustTokenPrivileges 成功但未全部赋值时 GetLastError 返回 ERROR_NOT_ALL_ASSIGNED
            uint last = (uint)Marshal.GetLastWin32Error();
            if (last == ERROR_NOT_ALL_ASSIGNED) return false;

            return last == ERROR_SUCCESS;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ── 令牌内特权查询（用于诊断日志） ──

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES_FULL
    {
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    private static bool HasPrivilegeInToken(IntPtr token, LUID required)
    {
        // TokenPrivileges = 3
        GetTokenInformation(token, 3, IntPtr.Zero, 0, out uint needed);
        if (needed == 0) return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, 3, buffer, needed, out _)) return false;

            uint count = (uint)Marshal.ReadInt32(buffer);
            IntPtr cursor = IntPtr.Add(buffer, sizeof(uint));
            int entrySize = Marshal.SizeOf<LUID_AND_ATTRIBUTES_FULL>();

            for (uint i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<LUID_AND_ATTRIBUTES_FULL>(cursor);
                if (entry.Luid.LowPart == required.LowPart && entry.Luid.HighPart == required.HighPart)
                    return true;
                cursor = IntPtr.Add(cursor, entrySize);
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // 供诊断：判断当前进程是否具备「可设置安全描述符」的能力
    public static (bool hasSecurity, bool hasTakeOwnership, bool isSystem) DescribeCurrentContext()
    {
        using var id = WindowsIdentity.GetCurrent();
        bool isSystem = id.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        return (HasPrivilege("SeSecurityPrivilege"),
                HasPrivilege("SeTakeOwnershipPrivilege"),
                isSystem);
    }
}
