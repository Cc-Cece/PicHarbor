using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PicHarbor.Vault;

public static partial class ServiceInstaller
{
    public static string StageVerifiedUpdate(string executable) => ServiceImageStore.Prepare(executable, singleFileOnly: true);
    public static void ReleaseStagedUpdate(string executable) => ServiceImageStore.DeleteDeployment(executable);

    /// <summary>Preserves the registered service identity/start settings and retains the old image for rollback.</summary>
    public sealed class ImageUpdate : IDisposable
    {
        private readonly string originalImage;
        private readonly bool wasRunning;
        private readonly string replacement;
        private bool changed;
        private bool committed;

        public ImageUpdate(string executable)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName)
                ?? throw new InvalidOperationException("Vault service is not installed.");
            originalImage = key.GetValue("ImagePath") as string ?? throw new InvalidDataException("Missing service ImagePath.");
            using var controller = new ServiceController(ServiceName);
            wasRunning = controller.Status != ServiceControllerStatus.Stopped;
            // A new immutable deployment directory is created without touching the currently running image.
            replacement = ServiceImageStore.Prepare(executable);
        }

        public void Apply()
        {
            changed = true; // A failed stop can still have changed the service's running state.
            if (!Stop(out string error)) throw new IOException(error);
            SetImage($"\"{replacement}\" {ServiceArgs}");
            if (wasRunning && !Start(out error)) throw new IOException(error);
        }

        public void Commit() => committed = true;

        public void Dispose()
        {
            if (committed) return;
            if (changed)
            {
                if (!Stop(out string error)) throw new IOException("Vault rollback stop failed: " + error);
                SetImage(originalImage);
                if (wasRunning && !Start(out error)) throw new IOException("Vault rollback start failed: " + error);
                changed = false;
            }
            ServiceImageStore.DeleteDeployment(replacement);
        }

        public bool RequiresRunningValidation => wasRunning;
        public string OriginalImage => originalImage;

        private static void SetImage(string image)
        {
            IntPtr manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenService(manager, ServiceName, SERVICE_CHANGE_CONFIG);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    const uint noChange = 0xffffffff;
                    if (!ChangeServiceConfig(service, noChange, noChange, noChange, image, null, IntPtr.Zero, null, null, null, null))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
    }
}
