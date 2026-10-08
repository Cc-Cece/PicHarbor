using System.Runtime.InteropServices;

namespace PicHarbor.Core.Transfer;

/// <summary>
/// The production <see cref="IProcessTerminator"/>: a finalizer-skipping hard exit via the Win32
/// <c>TerminateProcess</c> on the current process pseudo-handle.
/// </summary>
/// <remarks>
/// <para>
/// On the #45 disconnect path the process must end <b>without</b> running finalizers. <c>Environment.Exit</c>
/// is unsafe there because it runs finalizers — the <c>AfcReadStream</c> finalizer (<c>afc_file_close</c>)
/// and the <c>AfcClientHandle</c> SafeHandle <i>critical</i> finalizer (<c>afc_client_free</c>) would each
/// re-enter the busy-spinning native layer on the dead transport, so the exit itself would hang.
/// <c>TerminateProcess</c> tells the OS to reap every thread and handle immediately; no managed or critical
/// finalizer runs, so nothing re-enters native device code.
/// </para>
/// <para>
/// Windows-only (the tool ships <c>win-x64</c>); guarded by <see cref="OperatingSystem.IsWindows"/>. On any
/// other OS — where the native AFC layer is never loaded, so no native spin can occur — it falls back to
/// <see cref="Environment.Exit(int)"/> so the process still stops.
/// </para>
/// </remarks>
public sealed partial class TerminateProcessTerminator : IProcessTerminator
{
    /// <inheritdoc />
    public void Terminate(int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            // GetCurrentProcess returns the (-1) pseudo-handle; TerminateProcess never returns control to
            // managed code here — the OS tears the process down, skipping all finalizers.
            TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));
            return;
        }

        // Non-Windows is not a supported target; there is no native AFC spin to avoid, so a plain exit is
        // safe and guarantees the process still stops.
        Environment.Exit(exitCode);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();
}
