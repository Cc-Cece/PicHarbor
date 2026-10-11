// PicHarbor.Vault — 会话锁定监听
// 监听 Windows 会话锁定（WM_WTSSESSION_CHANGE: WTS_SESSION_LOCK）与注销，
// 锁定/注销时立即收回权限，不依赖界面进程存活。
//
// 服务进程没有消息泵，这里创建一个隐藏窗口接收会话通知消息。

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PicHarbor.Vault;

[SupportedOSPlatform("windows")]
public sealed partial class SessionLockWatcher : BackgroundService
{
    private readonly VaultState _state;
    private readonly ILogger<SessionLockWatcher> _logger;
    private SessionNotificationWindow? _window;
    private Thread? _pumpThread;

    public SessionLockWatcher(VaultState state, ILogger<SessionLockWatcher> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 在独立 STA 线程上创建隐藏窗口并跑消息泵
        _pumpThread = new Thread(() =>
        {
            try
            {
                _window = new SessionNotificationWindow(OnSessionLocked);
                Application.Run();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session notification pump failed; screen-lock revoke unavailable.");
            }
        })
        {
            IsBackground = true,
            Name = "VaultSessionNotification"
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();

        stoppingToken.Register(() =>
        {
            try { _window?.EndSession(); } catch { }
            try { Application.ExitThread(); } catch { }
        });

        return Task.CompletedTask;
    }

    private void OnSessionLocked()
    {
        _logger.LogInformation("Session locked/logged off: revoking access.");
        _state.ForceLock();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 隐藏窗口 + WTS 会话通知
    // ──────────────────────────────────────────────────────────────────────

    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_LOGOFF = 0x5;
    private const int WTS_REMOTE_DISCONNECT = 0x4;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    private const uint NOTIFY_FOR_ALL_SESSIONS = 0x1;

    private sealed class SessionNotificationWindow : NativeWindow
    {
        private readonly Action _onLocked;

        public SessionNotificationWindow(Action onLocked)
        {
            _onLocked = onLocked;

            CreateHandle(new CreateParams
            {
                Caption = "PicHarborVaultSessionHook",
                Style = 0,
                ExStyle = 0,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Parent = new IntPtr(-3)   // HWND_MESSAGE
            });

            WTSRegisterSessionNotification(Handle, NOTIFY_FOR_ALL_SESSIONS);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int evt = m.WParam.ToInt32();
                if (evt is WTS_SESSION_LOCK or WTS_SESSION_LOGOFF or WTS_REMOTE_DISCONNECT)
                    _onLocked();
            }
            base.WndProc(ref m);
        }

        public void EndSession()
        {
            try { WTSUnRegisterSessionNotification(Handle); } catch { }
            try { DestroyHandle(); } catch { }
        }
    }
}
