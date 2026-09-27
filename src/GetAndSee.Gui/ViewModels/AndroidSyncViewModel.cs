using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Android;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Transfer;
using GetAndSee.Gui.Config;
using GetAndSee.Gui.Util;

namespace GetAndSee.Gui.ViewModels;

public partial class AndroidSyncViewModel : ObservableObject
{
    private CancellationTokenSource? cts;

    [ObservableProperty]
    private string archivePath = "";

    [ObservableProperty]
    private string androidDeviceName = "Pixel 8";

    [ObservableProperty]
    private string androidFtpHost = "192.168.1.100";

    [ObservableProperty]
    private int androidFtpPort = 2121;

    [ObservableProperty]
    private string androidFtpUser = "anonymous";

    [ObservableProperty]
    private string androidFtpPassword = "";

    [ObservableProperty]
    private string androidTargetDir = "/DCIM/GetAndSee/iPhone/";

    [ObservableProperty]
    private string androidDeviceId = "";

    [ObservableProperty]
    private string testStatus = "";

    [ObservableProperty]
    private bool isSyncing = false;

    [ObservableProperty]
    private double progressValue = 0;

    [ObservableProperty]
    private string progressText = App.GetString("MsgReady", "准备就绪");

    [ObservableProperty]
    private int copiedCount = 0;

    [ObservableProperty]
    private int skippedCount = 0;

    [ObservableProperty]
    private int failedCount = 0;

    [ObservableProperty]
    private string transferredSizeText = "0 B / 0 B";

    [ObservableProperty]
    private string etaText = "--";

    public ObservableCollection<string> LogEntries { get; } = new()
    {
        "[INFO] Android / Pixel 局域网 FTP 增量同步模块已准备就绪。",
        "[INFO] 请设置 Android FTP 服务器 IP、端口及目标路径，点击“测试连接”或“开始同步”。"
    };

    public AndroidSyncViewModel()
    {
        LoadConfig();
    }

    private void LoadConfig()
    {
        var config = AppSettings.Load();
        AndroidDeviceName = config.AndroidDeviceName;
        AndroidFtpHost = config.AndroidFtpHost;
        AndroidFtpPort = config.AndroidFtpPort;
        AndroidFtpUser = config.AndroidFtpUser;
        AndroidFtpPassword = config.AndroidFtpPassword;
        AndroidTargetDir = config.AndroidTargetDir;
        AndroidDeviceId = config.AndroidDeviceId;
    }

    public void SaveConfig()
    {
        var config = AppSettings.Load();
        config.AndroidDeviceName = AndroidDeviceName;
        config.AndroidFtpHost = AndroidFtpHost;
        config.AndroidFtpPort = AndroidFtpPort;
        config.AndroidFtpUser = AndroidFtpUser;
        config.AndroidFtpPassword = AndroidFtpPassword;
        config.AndroidTargetDir = AndroidTargetDir;
        config.AndroidDeviceId = AndroidDeviceId;
        AppSettings.Save(config);
    }

    partial void OnAndroidDeviceNameChanged(string value) => SaveConfig();
    partial void OnAndroidFtpHostChanged(string value) => SaveConfig();
    partial void OnAndroidFtpPortChanged(int value) => SaveConfig();
    partial void OnAndroidFtpUserChanged(string value) => SaveConfig();
    partial void OnAndroidFtpPasswordChanged(string value) => SaveConfig();
    partial void OnAndroidTargetDirChanged(string value) => SaveConfig();
    partial void OnAndroidDeviceIdChanged(string value) => SaveConfig();

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestStatus = "正在尝试连接 FTP...";
        AddLog($"[TEST] Connecting to FTP {AndroidFtpHost}:{AndroidFtpPort}...");
        try
        {
            bool ok = await SimpleFtpClient.TestConnectionAsync(
                AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, AndroidTargetDir);
            TestStatus = ok ? "✅ FTP 连接测试成功！" : "❌ FTP 连接失败";
            AddLog($"[TEST SUCCESS] Successfully connected to FTP {AndroidFtpHost}:{AndroidFtpPort}. Target directory ready.");
        }
        catch (Exception ex)
        {
            TestStatus = $"❌ 连接失败: {ex.Message}";
            AddLog($"[TEST ERROR] Failed to connect: {ex.Message}");
        }
    }

    [ObservableProperty]
    private bool isDetailModalOpen = false;

    [ObservableProperty]
    private string detailModalTitle = "";

    [ObservableProperty]
    private ObservableCollection<TransferItemDetail> detailItems = new();

    private readonly List<TransferItemDetail> copiedDetails = new();
    private readonly List<TransferItemDetail> skippedDetails = new();
    private readonly List<TransferItemDetail> failedDetails = new();

    [RelayCommand]
    private void ShowCopiedDetails()
    {
        DetailModalTitle = "🤖 Android FTP 已传输 / 已复制文件明细 (Copied)";
        DetailItems = new ObservableCollection<TransferItemDetail>(copiedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowSkippedDetails()
    {
        DetailModalTitle = "🤖 Android FTP 已跳过文件明细 (Skipped - 已存在/已同步)";
        DetailItems = new ObservableCollection<TransferItemDetail>(skippedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowFailedDetails()
    {
        DetailModalTitle = "⚠️ Android FTP 失败文件明细 (Failed)";
        DetailItems = new ObservableCollection<TransferItemDetail>(failedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void CloseDetailModal()
    {
        IsDetailModalOpen = false;
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            TestStatus = "❌ PC 本地归档目录不存在！请先在主界面或设置中配置归档路径。";
            AddLog($"[ERROR] Local PC archive path directory not found: {ArchivePath}");
            return;
        }

        IsSyncing = true;
        CopiedCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        ProgressValue = 0;
        ProgressText = "正在初始化 Android FTP 增量同步...";

        copiedDetails.Clear();
        skippedDetails.Clear();
        failedDetails.Clear();

        cts = new CancellationTokenSource();

        var progressHandler = new Progress<ProgressSnapshot>(snapshot =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                double percent = snapshot.ByteFraction * 100;
                ProgressValue = percent;
                CopiedCount = snapshot.CopiedFiles;
                SkippedCount = snapshot.SkippedFiles;
                FailedCount = snapshot.FailedFiles;
                TransferredSizeText = $"{FormatBytes(snapshot.ProcessedBytes)} / {FormatBytes(snapshot.TotalBytes)}";
                EtaText = snapshot.Eta is TimeSpan eta
                    ? FormatTimeSpan(eta)
                    : (percent >= 100 ? App.GetString("MsgSyncCompleted", "同步完成") : App.GetString("MsgCalculating", "计算中..."));
                ProgressText = $"{percent:F1}% ({snapshot.ProcessedFiles:N0}/{snapshot.TotalFiles:N0} 文件) - {FormatSpeed(snapshot.CurrentBytesPerSecond)}";
            });
        });

        AddLog($"[START] Starting standalone sync to Android device '{AndroidDeviceName}'...");

        try
        {
            var result = await AndroidSyncEngine.SyncAsync(
                ArchivePath,
                AndroidFtpHost,
                AndroidFtpPort,
                AndroidFtpUser,
                AndroidFtpPassword,
                AndroidTargetDir,
                AndroidDeviceName,
                AndroidDeviceId,
                progressHandler,
                msg => App.Current.Dispatcher.Invoke(() => AddLog(msg)),
                (srcPath, targetPath, size, status, details) =>
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        var item = new TransferItemDetail
                        {
                            SourcePath = srcPath,
                            TargetPath = targetPath,
                            FullPath = srcPath,
                            FileSizeText = FormatBytes(size),
                            StatusText = status.ToString(),
                            Details = details ?? string.Empty
                        };
                        switch (status)
                        {
                            case CopyStatus.Copied:
                                copiedDetails.Add(item);
                                break;
                            case CopyStatus.Skipped:
                                skippedDetails.Add(item);
                                break;
                            case CopyStatus.Failed:
                                failedDetails.Add(item);
                                break;
                        }
                    });
                },
                cts.Token);

            AndroidDeviceId = result.DeviceId;
            SaveConfig();

            CopiedCount = result.CopiedCount;
            SkippedCount = result.SkippedCount;
            FailedCount = result.FailedCount;
            ProgressValue = 100;
            ProgressText = $"同步完成: 已传输 {result.CopiedCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}";
            AddLog($"[FINISHED] Android sync finished for device ID {result.DeviceId}. Transferred {result.CopiedCount:N0} files ({FormatBytes(result.BytesCopied)}).");
        }
        catch (OperationCanceledException)
        {
            ProgressText = App.GetString("MsgSyncCancelled", "同步已取消");
            AddLog("[CANCELLED] Sync cancelled by user.");
        }
        catch (Exception ex)
        {
            ProgressText = $"同步失败: {ex.Message}";
            AddLog($"[ERROR] Android sync failed: {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
            cts?.Dispose();
            cts = null;
        }
    }

    [RelayCommand]
    private void CancelSync()
    {
        cts?.Cancel();
        AddLog("[USER] Requesting sync cancellation...");
    }

    private void AddLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogEntries.Add($"[{timestamp}] {message}");
        if (LogEntries.Count > 1000)
        {
            LogEntries.RemoveAt(0);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double dblSByte = bytes;
        while (dblSByte >= 1024 && i < suffixes.Length - 1)
        {
            dblSByte /= 1024;
            i++;
        }
        return $"{dblSByte:0.##} {suffixes[i]}";
    }

    private static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}小时{span.Minutes:D2}分{span.Seconds:D2}秒";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes:D2}分{span.Seconds:D2}秒";
        }
        return $"{span.Seconds}秒";
    }

    private (List<string> existing, List<string> missing) FilterDetailPaths(object? parameter)
    {
        var existing = new List<string>();
        var missing = new List<string>();

        if (parameter is null) return (existing, missing);

        var items = new List<TransferItemDetail>();
        if (parameter is TransferItemDetail singleItem)
        {
            items.Add(singleItem);
        }
        else if (parameter is System.Collections.IEnumerable enumerable)
        {
            foreach (var element in enumerable)
            {
                if (element is TransferItemDetail item)
                {
                    items.Add(item);
                }
            }
        }

foreach (var item in items)
        {
            string candidate = item.FullPath;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = item.SourcePath;
            }
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = item.TargetPath;
            }
            if (!string.IsNullOrWhiteSpace(candidate) && !Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(ArchivePath))
            {
                candidate = Path.Combine(ArchivePath, candidate);
            }

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (File.Exists(candidate))
                {
                    existing.Add(candidate);
                }
                else
                {
                    missing.Add(candidate);
                }
            }
        }

        if (missing.Count > 0)
        {
            MessageBox.Show(
                $"有 {missing.Count} 个选中的文件在磁盘上不存在:\n{string.Join(Environment.NewLine, missing.Take(3))}{(missing.Count > 3 ? "\n..." : "")}",
                App.GetString("MsgFileNotFound", "文件不存在"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return (existing, missing);
    }

    [RelayCommand]
    private void OpenDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.OpenFiles(existing);
        }
    }

    [RelayCommand]
    private void OpenWithDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            IntPtr hwnd = GetMainWindowHandle();
            ShellServices.OpenWith(existing[0], hwnd);
        }
    }

    [RelayCommand]
    private void CopyDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.CopyFilesToClipboard(existing);
        }
    }

    [RelayCommand]
    private void CopyPathDetail(object? selectedItems)
    {
        var (existing, missing) = FilterDetailPaths(selectedItems);
        var allTargeted = existing.Concat(missing).ToList();
        if (allTargeted.Count > 0)
        {
            ShellServices.CopyPathsToClipboard(allTargeted);
        }
    }

    [RelayCommand]
    private void ShowInExplorerDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.ShowInExplorer(existing);
        }
    }

    [RelayCommand]
    private void PropertiesDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            IntPtr hwnd = GetMainWindowHandle();
            ShellServices.ShowProperties(existing, hwnd);
        }
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = Application.Current.MainWindow;
        return window is not null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
    }
}
