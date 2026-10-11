// PicHarbor.Vault — 进程监视器
// 用句柄（不用 PID）监视 GUI 进程，进程退出即触发收回。

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public sealed partial class ProcessWatcher : IDisposable
{
    private SafeWaitHandle? _handle;
    private RegisteredWaitHandle? _registration;
    private readonly Action _onExited;
    private bool _disposed;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    private const uint SYNCHRONIZE = 0x00100000;

    public ProcessWatcher(int pid, Action onExited)
    {
        _onExited = onExited;

        SafeWaitHandle handle = OpenProcess(SYNCHRONIZE, false, pid);
        if (handle.IsInvalid)
        {
            // 进程已不存在，立刻触发
            Task.Run(onExited);
            return;
        }

        _handle = handle;
        // 注册等待：进程句柄 signaled → 回调
        _registration = ThreadPool.RegisterWaitForSingleObject(
            new System.Threading.ManualResetEvent(false) { SafeWaitHandle = handle },
            (_, _) => onExited(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration?.Unregister(null);
        _handle?.Dispose();
    }
}
