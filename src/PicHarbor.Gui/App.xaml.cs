using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PicHarbor.Gui.Util;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 应用生命周期由代码显式管理：默认的 OnLastWindowClose 会在解锁窗口
        // 关闭、而主窗口尚未构造的空档里判定"最后一个窗口已关闭"并自动退出，
        // 表现为「输入正确密码后 PicHarbor 直接结束」。改为显式退出，
        // 由解锁/主窗口各自的路径决定何时 Shutdown。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.WriteAllText("crash.log", args.Exception.ToString() + "\nInner:\n" + args.Exception.InnerException?.ToString());
            }
            catch { }
            string detail = args.Exception.InnerException != null
                ? string.Format(GetString("FmtUnhandledCause", "{0}\n原因: {1}"), args.Exception.Message, args.Exception.InnerException.Message)
                : args.Exception.Message;
            MessageBox.Show(
                string.Format(GetString("FmtUnhandledException", "程序运行遇到异常:\n{0}"), detail),
                GetString("MsgBoxTitle", "提示"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        var cmdArgs = Environment.GetCommandLineArgs();
        int previewIdx = Array.FindIndex(cmdArgs, a => a.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIdx >= 0)
        {
            string outDir = (previewIdx + 1 < cmdArgs.Length) ? cmdArgs[previewIdx + 1] : ".";
            ExportPreviews(outDir);
            Shutdown(0);
            return;
        }

        _ = StartWithAppLockAsync();
    }

    /// <summary>
    /// 应用启动流程：先问 Vault 服务。未启用则直接进主窗口；
    /// 已启用则先显示解锁窗口——密码通过之前绝不构造会碰归档的逻辑。
    /// </summary>
    private async Task StartWithAppLockAsync()
    {
        var lockVm = new AppLockViewModel();
        await lockVm.RefreshStatusAsync();

        if (lockVm.RequiresUnlockOnStartup)
        {
            // 已启用且未解锁：先解锁
            var unlock = new UnlockWindow(lockVm);
            bool? unlocked = unlock.ShowDialog();
            if (unlocked != true || !lockVm.IsUnlocked)
            {
                Shutdown(0);
                return;
            }
        }

        MainWindow mainWindow;
        try
        {
            mainWindow = new MainWindow { AppLock = lockVm };
        }
        catch (Exception ex)
        {
            // 主窗口构造失败（例如 XAML/BAML 资源解析异常）：明确记录并退出，
            // 而不是让进程在没有窗口的情况下静默结束。
            try
            {
                File.WriteAllText("crash.log",
                    DateTime.Now.ToString("O") + "\n" + ex.ToString() + "\nInner:\n" + ex.InnerException?.ToString());
            }
            catch { }
            MessageBox.Show(
                string.Format(GetString("FmtUnhandledException", "程序运行遇到异常:\n{0}"), ex.Message),
                GetString("MsgBoxTitle", "提示"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MainWindow = mainWindow;

        // 显式退出模式：主窗口关闭即代表用户希望退出应用。
        mainWindow.Closed += (_, _) => Shutdown(0);

        // 把当前归档根注入应用锁，供「启用」时登记并锁定。
        // DestinationPath 会在主窗口 DataContext 就绪后可用，这里绑定其后续变化。
        if (mainWindow.DataContext is MainViewModel mainVm)
        {
            lockVm.ArchiveRoot = mainVm.DestinationPath;
            mainVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.DestinationPath))
                    lockVm.ArchiveRoot = mainVm.DestinationPath;
            };
        }

        mainWindow.Show();
    }

    private static void ExportPreviews(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var window = new MainWindow();
            window.Width = 1000;
            window.Height = 640;
            window.Show();

            if (window.DataContext is MainViewModel vm)
            {
                for (int i = 0; i < 4; i++)
                {
                    vm.SelectedTabIndex = i;
                    window.UpdateLayout();

                    // Wait for animations and WebView2 gallery to finish loading
                    int waitMs = (i == 1) ? 3500 : 500;
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
                    timer.Tick += (s, a) => { frame.Continue = false; timer.Stop(); };
                    timer.Start();
                    Dispatcher.PushFrame(frame);

                    int width = (int)window.ActualWidth;
                    int height = (int)window.ActualHeight;
                    if (width <= 0) width = 1000;
                    if (height <= 0) height = 640;

                    var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(window);

                    if (i == 1)
                    {
                        var galleryWeb = FindVisualChild<Controls.PclGalleryWebControl>(window);
                        if (galleryWeb?.AlbumWebView?.CoreWebView2 != null)
                        {
                            try
                            {
                                using var webMs = new MemoryStream();
                                var captureTask = galleryWeb.AlbumWebView.CoreWebView2.CapturePreviewAsync(
                                    Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,
                                    webMs);

                                var captureFrame = new DispatcherFrame();
                                captureTask.ContinueWith(_ => { captureFrame.Continue = false; });
                                Dispatcher.PushFrame(captureFrame);

                                if (webMs.Length > 0)
                                {
                                    webMs.Position = 0;
                                    var webBmp = new BitmapImage();
                                    webBmp.BeginInit();
                                    webBmp.StreamSource = webMs;
                                    webBmp.CacheOption = BitmapCacheOption.OnLoad;
                                    webBmp.EndInit();
                                    webBmp.Freeze();

                                    var dv = new DrawingVisual();
                                    using (var dc = dv.RenderOpen())
                                    {
                                        dc.DrawImage(rtb, new Rect(0, 0, width, height));
                                        var point = galleryWeb.TransformToAncestor(window).Transform(new Point(0, 0));
                                        dc.DrawImage(webBmp, new Rect(point.X, point.Y, galleryWeb.ActualWidth, galleryWeb.ActualHeight));
                                    }
                                    var compRtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                                    compRtb.Render(dv);
                                    rtb = compRtb;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Composite error: {ex.Message}");
                            }
                        }
                    }

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    string file = Path.Combine(outDir, $"pcl2_ui_preview_tab{i}.png");
                    using var stream = File.Create(file);
                    encoder.Save(stream);
                }

                // Render Android View
                vm.SelectedTabIndex = 0;
                vm.SelectedDeviceIndex = 1;
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_android.png"));

                // Render Backup Scope Modal with Android Scope & Filters
                vm.SelectedDeviceIndex = 1;
                vm.AndroidBackupVM.Albums.Clear();
                vm.AndroidBackupVM.Albums.Add(new AndroidAlbumOptionViewModel { DisplayName = "📷 相机胶卷 (Camera)", IsChecked = true, RemotePath = "/DCIM/Camera", DetailText = "1,420 项 · 5.8 GB" });
                vm.AndroidBackupVM.Albums.Add(new AndroidAlbumOptionViewModel { DisplayName = "📱 屏幕截图 (Screenshots)", IsChecked = true, RemotePath = "/Pictures/Screenshots", DetailText = "328 项 · 410 MB" });
                vm.AndroidBackupVM.Albums.Add(new AndroidAlbumOptionViewModel { DisplayName = "💬 微信相册 (WeiXin)", IsChecked = false, RemotePath = "/Pictures/WeiXin", DetailText = "512 项 · 1.2 GB" });
                vm.AndroidBackupVM.IsScopeDefault = true;
                vm.AndroidBackupVM.UpdateScopeSummarySentence();
                vm.OpenScopeModalCommand.Execute(null);
                WaitAnimation(350);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_scope_modal.png"));
                vm.CloseScopeModalCommand.Execute(null);
                WaitAnimation(350);

                // Render Detail Modal with DataGrid
                vm.BackupVM.DetailItems.Clear();
                var item1 = new TransferItemDetail { StatusText = "已完成", TargetPath = "Photos/2024/IMG_4521.HEIC", FileSizeText = "2.8 MB", Details = "哈希校验通过 · 增量归档完成" };
                var item2 = new TransferItemDetail { StatusText = "已完成", TargetPath = "Photos/2024/IMG_4522.MOV", FileSizeText = "14.2 MB", Details = "完整传输完成" };
                vm.BackupVM.DetailItems.Add(item1);
                vm.BackupVM.DetailItems.Add(item2);
                vm.SelectedDetailItem = item1;
                vm.BackupVM.DetailModalTitle = "📱 iPhone 备份 已传输文件明细 (双击查看照片)";
                vm.BackupVM.IsDetailModalOpen = true;
                WaitAnimation(350);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_detail_modal.png"));
                vm.BackupVM.IsDetailModalOpen = false;
                WaitAnimation(350);

                // Render Unified Manual Transfer Modal
                vm.OpenUnifiedManualModalCommand.Execute(null);
                vm.UnifiedManualSelectedItems.Clear();
                vm.UnifiedManualSelectedItems.Add(new ManualSelectedItemViewModel { RelativePath = "Camera/IMG_20240901_102030.jpg", CapturedAt = "2024-09-01 10:20:30", SizeText = "4.2 MB" });
                vm.UnifiedManualSelectedItems.Add(new ManualSelectedItemViewModel { RelativePath = "Camera/VID_20240902_153012.mp4", CapturedAt = "2024-09-02 15:30:12", SizeText = "38.5 MB" });
                WaitAnimation(350);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_unified_manual_modal.png"));
                vm.CloseUnifiedManualModalCommand.Execute(null);
                WaitAnimation(350);

                // Render Task Manager Overlay matching user scenario (completed previous task)
                vm.SelectedDeviceIndex = 1;
                vm.AndroidBackupVM.IsTransferring = false;
                vm.AndroidBackupVM.DetectedDeviceModel = "Pixel 8";
                vm.AndroidBackupVM.CopiedCount = 1;
                vm.AndroidBackupVM.SkippedCount = 16;
                vm.AndroidBackupVM.FailedCount = 0;
                vm.OpenTaskManagerCommand.Execute(null);
                window.AnimateOpenTaskManager(animate: false);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_task_manager.png"));
                vm.CloseTaskManagerCommand.Execute(null);
                window.AnimateCloseTaskManager(animate: false);

                // Render Task Manager Overlay empty / waiting state
                vm.AndroidBackupVM.CopiedCount = 0;
                vm.AndroidBackupVM.SkippedCount = 0;
                vm.AndroidBackupVM.FailedCount = 0;
                vm.BackupVM.CopiedCount = 0;
                vm.BackupVM.SkippedCount = 0;
                vm.BackupVM.FailedCount = 0;
                vm.OpenTaskManagerCommand.Execute(null);
                window.AnimateOpenTaskManager(animate: false);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_task_manager_empty.png"));
                vm.CloseTaskManagerCommand.Execute(null);
                window.AnimateCloseTaskManager(animate: false);
            }
            window.Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(outDir, "render_error.log"), ex.ToString()); } catch { }
        }
    }

    private static void SaveWindowSnapshot(Window window, string filePath)
    {
        int width = (int)window.ActualWidth;
        int height = (int)window.ActualHeight;
        if (width <= 0) width = 1000;
        if (height <= 0) height = 640;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    private static void WaitAnimation(int ms = 350)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (s, a) => { frame.Continue = false; timer.Stop(); };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var sub = FindVisualChild<T>(child);
            if (sub is not null) return sub;
        }
        return null;
    }

    public static string GetString(string key, string fallback = "") =>
        LocalizationService.GetString(key, fallback);

    public static void SwitchLanguage(string cultureCode) =>
        LocalizationService.SwitchLanguage(cultureCode);
}
