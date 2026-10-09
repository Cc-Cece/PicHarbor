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
            MessageBox.Show($"程序运行遇到异常:\n{args.Exception.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

                    // Wait 500ms for PCL2 cascading entrance animations to finish
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += (s, a) => { frame.Continue = false; timer.Stop(); };
                    timer.Start();
                    Dispatcher.PushFrame(frame);

                    int width = (int)window.ActualWidth;
                    int height = (int)window.ActualHeight;
                    if (width <= 0) width = 1000;
                    if (height <= 0) height = 640;

                    var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(window);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    string file = Path.Combine(outDir, $"pcl2_ui_preview_tab{i}.png");
                    using var stream = File.Create(file);
                    encoder.Save(stream);
                }
            }
            window.Close();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(outDir, "render_error.log"), ex.ToString()); } catch { }
        }
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
