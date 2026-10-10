using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.WriteAllText("crash.log", args.Exception.ToString() + "\nInner:\n" + args.Exception.InnerException?.ToString());
            }
            catch { }
            string detail = args.Exception.InnerException != null
                ? $"{args.Exception.Message}\n原因: {args.Exception.InnerException.Message}"
                : args.Exception.Message;
            MessageBox.Show($"程序运行遇到异常:\n{detail}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var mainWindow = new MainWindow();
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

                // Render Android FTP Connection Modal
                vm.OpenAndroidFtpModalCommand.Execute(null);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_ftp_modal.png"));
                vm.CloseAndroidFtpModalCommand.Execute(null);

                var waitFrame = new DispatcherFrame();
                var waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                waitTimer.Tick += (s, a) => { waitFrame.Continue = false; waitTimer.Stop(); };
                waitTimer.Start();
                Dispatcher.PushFrame(waitFrame);
                window.UpdateLayout();

                // Render Task Manager Overlay matching Image 2
                vm.AndroidBackupVM.IsTransferring = true;
                vm.AndroidBackupVM.ProgressPercentage = 70.0;
                vm.AndroidBackupVM.SpeedText = "12.2 MB/s";
                vm.AndroidBackupVM.RemainingFilesCount = 794;
                vm.AndroidBackupVM.CurrentFileName = "IMG_20240901_102030.jpg";
                vm.OpenTaskManagerCommand.Execute(null);
                window.UpdateLayout();
                SaveWindowSnapshot(window, Path.Combine(outDir, "pcl2_ui_preview_task_manager.png"));
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

    public static string GetString(string key, string fallback = "")
    {
        if (Current?.TryFindResource(key) is string value)
        {
            return value;
        }
        return fallback;
    }

    public static void SwitchLanguage(string cultureCode)
    {
        var app = (App)Current;
        var resourceDict = new ResourceDictionary
        {
            Source = new Uri($"Resources/StringResources.{cultureCode}.xaml", UriKind.Relative)
        };

        // Replace language resource dictionary
        var existingLangDict = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.Contains("StringResources"));

        if (existingLangDict != null)
        {
            app.Resources.MergedDictionaries.Remove(existingLangDict);
        }

        app.Resources.MergedDictionaries.Add(resourceDict);
    }
}
