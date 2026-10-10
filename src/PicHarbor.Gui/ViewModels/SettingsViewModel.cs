using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Storage;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly Action<string>? onArchivePathChanged;

    [ObservableProperty]
    private ObservableCollection<LibraryVolumeInfo> availableVolumes = new();

    [ObservableProperty]
    private LibraryVolumeInfo? selectedVolume;

    [ObservableProperty]
    private string primaryDriveLetter = "";

    [ObservableProperty]
    private string archivePath = "";

    [ObservableProperty]
    private bool groupMediaByDevice = true;

    [ObservableProperty]
    private string discoveredArchivesSummary = "";

    public ObservableCollection<string> OrganizeSchemes { get; } = new()
    {
        "month (YYYY-MM)",
        "year (YYYY)",
        "day (YYYY-MM-DD)",
        "flat (单一目录)"
    };

    [ObservableProperty]
    private string selectedScheme = "month (YYYY-MM)";

    [ObservableProperty]
    private bool syncExifToLastWriteTime = true;

    [ObservableProperty]
    private bool syncExifToCreationTime = false;

    [ObservableProperty]
    private int readTimeoutSeconds = 30;

    [ObservableProperty]
    private bool autoCompleteLivePhotoPair = true;

    [ObservableProperty]
    private bool autoCompleteAaeSidecar = true;

    [ObservableProperty]
    private bool autoCompleteRawJpg = false;

    [ObservableProperty]
    private bool disablePcSleepNotice = true;

    // Performance & Network Properties
    [ObservableProperty]
    private string googlePhotosProxy = "";

    [ObservableProperty]
    private string proxyStatusText = "";

    [ObservableProperty]
    private string proxyStatusColor = "#10B981";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestProxyCommand))]
    private bool isTestingProxy = false;

    public bool CanTestProxy => !IsTestingProxy;

    [ObservableProperty]
    private int googlePhotosThreads = 3;

    [ObservableProperty]
    private int googlePhotosTimeoutSeconds = 60;

    [ObservableProperty]
    private int googlePhotosAutoRetryAttempts = 3;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool isSyncing;

    public bool CanSync => !IsSyncing;

    [ObservableProperty]
    private bool showProgress;

    [ObservableProperty]
    private double syncProgressPercentage;

    [ObservableProperty]
    private int syncProcessedCount;

    [ObservableProperty]
    private int syncTotalCount;

    [ObservableProperty]
    private int syncUpdatedCount;

    [ObservableProperty]
    private string syncProgressOverlayText = "";

    [ObservableProperty]
    private string syncProgressDetailText = "";

    [ObservableProperty]
    private string syncUpdatedText = "";

    [ObservableProperty]
    private string syncStatusMessage = "";

    [ObservableProperty]
    private bool heifInstalled;

    [ObservableProperty]
    private bool hevcInstalled;

    [ObservableProperty]
    private bool codecProbeCompleted;

    [ObservableProperty]
    private string codecStatusText = "正在检测 HEIF / HEVC 扩展…";

    [ObservableProperty]
    private string copyFeedbackText = "";

    public bool CodecReady => CodecProbeCompleted && HeifInstalled && HevcInstalled;

    public bool CodecMissing => CodecProbeCompleted && !(HeifInstalled && HevcInstalled);

    private CancellationTokenSource? copyNoticeCts;

    public SettingsViewModel(Action<string>? onArchivePathChanged = null)
    {
        this.onArchivePathChanged = onArchivePathChanged;
        LoadConfig();
        RefreshVolumes();
        _ = RecheckCodecs();
    }

    partial void OnHeifInstalledChanged(bool value) => NotifyCodecStatus();

    partial void OnHevcInstalledChanged(bool value) => NotifyCodecStatus();

    partial void OnCodecProbeCompletedChanged(bool value) => NotifyCodecStatus();

    private void NotifyCodecStatus()
    {
        if (!CodecProbeCompleted)
        {
            CodecStatusText = "正在检测 HEIF / HEVC 扩展…";
        }
        else if (HeifInstalled && HevcInstalled)
        {
            CodecStatusText = "HEIF 与 HEVC 扩展都已安装。若缩略图仍是占位图，请重启本软件。";
        }
        else if (!HeifInstalled && !HevcInstalled)
        {
            CodecStatusText = "尚未检测到 HEIF 图像扩展和 HEVC 视频扩展。";
        }
        else if (!HeifInstalled)
        {
            CodecStatusText = "尚未检测到 HEIF 图像扩展。";
        }
        else
        {
            CodecStatusText = "尚未检测到 HEVC 视频扩展。";
        }

        OnPropertyChanged(nameof(CodecReady));
        OnPropertyChanged(nameof(CodecMissing));
    }

    [RelayCommand]
    private async Task RecheckCodecs()
    {
        CodecProbeCompleted = false;
        AppleCodecStatus status = await Task.Run(AppleCodecProbe.Probe);
        HeifInstalled = status.HeifInstalled;
        HevcInstalled = status.HevcInstalled;
        CodecProbeCompleted = true;
    }

    [RelayCommand]
    private void OpenHeifStore() => OpenUrl(AppleCodecLinks.HeifStoreUrl);

    [RelayCommand]
    private void OpenHevcPaidStore() => OpenUrl(AppleCodecLinks.HevcPaidStoreUrl);

    [RelayCommand]
    private void OpenHevcFreeStore() => OpenUrl(AppleCodecLinks.HevcFreeStoreUrl);

    [RelayCommand]
    private void OpenPackageDownload() => OpenUrl(AppleCodecLinks.PackageDownloadUrl);

    [RelayCommand]
    private void OpenLinkGenerator() => OpenUrl(AppleCodecLinks.LinkGeneratorUrl);

    [RelayCommand]
    private Task CopyPackagePassword() => CopyTextAsync(AppleCodecLinks.PackagePassword);

    [RelayCommand]
    private Task CopyHeifProductLink() => CopyTextAsync(AppleCodecLinks.HeifStoreUrl);

    [RelayCommand]
    private Task CopyHevcProductLink() => CopyTextAsync(AppleCodecLinks.HevcFreeStoreUrl);

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private async Task CopyTextAsync(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy codec text failed: {ex.Message}");
            return;
        }

        copyNoticeCts?.Cancel();
        copyNoticeCts = new CancellationTokenSource();
        CancellationToken token = copyNoticeCts.Token;
        CopyFeedbackText = "已复制";
        try
        {
            await Task.Delay(2000, token);
            CopyFeedbackText = "";
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public void RefreshVolumes()
    {
        AvailableVolumes.Clear();
        var vols = LibraryStorageService.DiscoverVolumes(PrimaryDriveLetter);
        foreach (var v in vols)
        {
            AvailableVolumes.Add(v);
        }

        if (AvailableVolumes.Count > 0)
        {
            var match = AvailableVolumes.FirstOrDefault(v => string.Equals(v.DriveLetter, PrimaryDriveLetter, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableVolumes.FirstOrDefault(v => v.HasExistingLibrary)
                        ?? AvailableVolumes[0];

            SelectedVolume = match;
            PrimaryDriveLetter = match.DriveLetter;
            ArchivePath = LibraryStorageService.GetLibraryRootForDrive(PrimaryDriveLetter);
        }

        var otherExisting = AvailableVolumes
            .Where(v => v.HasExistingLibrary && !string.Equals(v.DriveLetter, PrimaryDriveLetter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (otherExisting.Count > 0)
        {
            DiscoveredArchivesSummary = string.Format(
                App.GetString("MsgDiscoveredOtherArchives", "已自动识别归档卷: {0} (跨盘增量防重已自动生效)"),
                string.Join(", ", otherExisting.Select(v => $"{v.DriveLetter} [{v.RootPath}]")));
        }
        else
        {
            DiscoveredArchivesSummary = App.GetString("MsgNoOtherArchives", "未检测到其他盘符的归档卷");
        }
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(ArchivePath))
            {
                Directory.CreateDirectory(ArchivePath);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = ArchivePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SyncStatusMessage = $"无法打开文件夹: {ex.Message}";
        }
    }

    private void LoadConfig()
    {
        var config = Config.AppSettings.Load();
        PrimaryDriveLetter = string.IsNullOrWhiteSpace(config.PrimaryDriveLetter)
            ? LibraryStorageService.GetDefaultDriveLetter()
            : config.PrimaryDriveLetter;
        ArchivePath = LibraryStorageService.GetLibraryRootForDrive(PrimaryDriveLetter);
        GroupMediaByDevice = config.GroupMediaByDevice;
        SelectedScheme = string.IsNullOrWhiteSpace(config.SelectedScheme) ? "month (YYYY-MM)" : config.SelectedScheme;
        SyncExifToLastWriteTime = config.SyncExifToLastWriteTime;
        SyncExifToCreationTime = config.SyncExifToCreationTime;
        ReadTimeoutSeconds = config.ReadTimeoutSeconds;
        AutoCompleteLivePhotoPair = config.AutoCompleteLivePhotoPair;
        AutoCompleteAaeSidecar = config.AutoCompleteAaeSidecar;
        AutoCompleteRawJpg = config.AutoCompleteRawJpg;
        GooglePhotosProxy = config.GooglePhotosProxy;
        GooglePhotosThreads = config.GooglePhotosThreads > 0 ? config.GooglePhotosThreads : 3;
        GooglePhotosTimeoutSeconds = config.GooglePhotosTimeoutSeconds > 0 ? config.GooglePhotosTimeoutSeconds : 60;
        GooglePhotosAutoRetryAttempts = config.GooglePhotosAutoRetryAttempts > 0 ? config.GooglePhotosAutoRetryAttempts : 3;
        ProxyStatusText = !string.IsNullOrWhiteSpace(GooglePhotosProxy) ? $"{GooglePhotosProxy} (已配置)" : "直连 (未配置代理)";
    }

    public void SaveConfig()
    {
        var config = Config.AppSettings.Load();
        config.PrimaryDriveLetter = PrimaryDriveLetter;
        config.DestinationPath = ArchivePath;
        config.GroupMediaByDevice = GroupMediaByDevice;
        config.SelectedScheme = SelectedScheme;
        config.SyncExifToLastWriteTime = SyncExifToLastWriteTime;
        config.SyncExifToCreationTime = SyncExifToCreationTime;
        config.ReadTimeoutSeconds = ReadTimeoutSeconds;
        config.AutoCompleteLivePhotoPair = AutoCompleteLivePhotoPair;
        config.AutoCompleteAaeSidecar = AutoCompleteAaeSidecar;
        config.AutoCompleteRawJpg = AutoCompleteRawJpg;
        config.GooglePhotosProxy = GooglePhotosProxy;
        config.GooglePhotosThreads = GooglePhotosThreads;
        config.GooglePhotosTimeoutSeconds = GooglePhotosTimeoutSeconds;
        config.GooglePhotosAutoRetryAttempts = GooglePhotosAutoRetryAttempts;
        Config.AppSettings.Save(config);
    }

    partial void OnSelectedVolumeChanged(LibraryVolumeInfo? value)
    {
        if (value is null)
        {
            return;
        }

        PrimaryDriveLetter = value.DriveLetter;
        ArchivePath = LibraryStorageService.GetLibraryRootForDrive(PrimaryDriveLetter);
        SaveConfig();
        onArchivePathChanged?.Invoke(ArchivePath);
    }

    partial void OnGroupMediaByDeviceChanged(bool value) => SaveConfig();
    partial void OnSelectedSchemeChanged(string value) => SaveConfig();
    partial void OnSyncExifToLastWriteTimeChanged(bool value) => SaveConfig();
    partial void OnSyncExifToCreationTimeChanged(bool value) => SaveConfig();
    partial void OnReadTimeoutSecondsChanged(int value) => SaveConfig();
    partial void OnAutoCompleteLivePhotoPairChanged(bool value) => SaveConfig();
    partial void OnAutoCompleteAaeSidecarChanged(bool value) => SaveConfig();
    partial void OnAutoCompleteRawJpgChanged(bool value) => SaveConfig();
    partial void OnGooglePhotosProxyChanged(string value) => SaveConfig();
    partial void OnGooglePhotosThreadsChanged(int value) => SaveConfig();
    partial void OnGooglePhotosTimeoutSecondsChanged(int value) => SaveConfig();
    partial void OnGooglePhotosAutoRetryAttemptsChanged(int value) => SaveConfig();

    [RelayCommand(CanExecute = nameof(CanTestProxy))]
    private async Task TestProxyAsync()
    {
        if (IsTestingProxy) return;
        IsTestingProxy = true;
        ProxyStatusColor = "#3B82F6";
        ProxyStatusText = "⏳ 正在检测网络通道...";

        try
        {
            var sw = Stopwatch.StartNew();
            var handler = new System.Net.Http.SocketsHttpHandler();
            if (!string.IsNullOrWhiteSpace(GooglePhotosProxy))
            {
                handler.Proxy = new System.Net.WebProxy(GooglePhotosProxy);
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }

            using var client = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            var response = await client.GetAsync("https://www.google.com/generate_204").ConfigureAwait(false);
            sw.Stop();
            if (response.IsSuccessStatusCode)
            {
                ProxyStatusColor = "#10B981";
                ProxyStatusText = $"✅ 代理通道畅通 (延迟 {sw.ElapsedMilliseconds} ms)";
            }
            else
            {
                ProxyStatusColor = "#EF4444";
                ProxyStatusText = $"⚠️ 响应状态: {(int)response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            ProxyStatusColor = "#EF4444";
            ProxyStatusText = $"❌ 连接失败: {ex.Message}";
        }
        finally
        {
            IsTestingProxy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            SyncStatusMessage = App.GetString("MsgDirNotExist", "归档目录不存在，请先选择有效的备份目标路径。");
            ShowProgress = false;
            return;
        }

        string dbPath = Path.Combine(ArchivePath, TransferJournal.DatabaseFileName);
        if (!File.Exists(dbPath))
        {
            SyncStatusMessage = App.GetString("MsgDbNotFound", "未在归档目录中找到 picharbor.db 数据库，请先执行备份。");
            ShowProgress = false;
            return;
        }

        IsSyncing = true;
        ShowProgress = true;
        SyncProcessedCount = 0;
        SyncTotalCount = 0;
        SyncUpdatedCount = 0;
        SyncProgressPercentage = 0;
        SyncProgressOverlayText = "0.0% (0/0)";
        SyncProgressDetailText = App.GetString("MsgReadingDb", "正在读取归档数据库...");
        SyncUpdatedText = string.Format(App.GetString("MsgUpdatedCount", "已成功更新: {0} 项"), 0);
        SyncStatusMessage = "";

        try
        {
            var progress = new Progress<FileTimeSyncProgress>(p =>
            {
                SyncProcessedCount = p.Processed;
                SyncTotalCount = p.Total;
                SyncUpdatedCount = p.Updated;

                if (p.Total > 0)
                {
                    double pct = (double)p.Processed / p.Total * 100.0;
                    SyncProgressPercentage = pct;
                    SyncProgressOverlayText = $"{pct:F1}% ({p.Processed:N0}/{p.Total:N0})";
                    SyncProgressDetailText = string.Format(App.GetString("MsgCompletedProgress", "已完成: {0} / {1} 文件"), p.Processed.ToString("N0"), p.Total.ToString("N0"));
                    SyncUpdatedText = string.Format(App.GetString("MsgUpdatedCount", "已成功更新: {0} 项"), p.Updated.ToString("N0"));
                }
                else
                {
                    SyncProgressPercentage = 0;
                    SyncProgressOverlayText = "0.0% (0/0)";
                    SyncProgressDetailText = App.GetString("MsgNoFilesToSync", "归档清单中未找到可同步的文件");
                    SyncUpdatedText = string.Format(App.GetString("MsgUpdatedCount", "已成功更新: {0} 项"), 0);
                }
            });

            int updated = await FileTimeSynchronizer.BatchSyncArchiveFileTimesAsync(
                ArchivePath,
                syncCreationTime: SyncExifToCreationTime,
                progress: progress);

            SyncStatusMessage = string.Format(App.GetString("MsgSyncSuccess", "✅ 同步完成！共检索 {0} 个归档文件，成功将 {1} 个文件的 EXIF 时间同步到 Windows 系统时间。"), SyncProcessedCount.ToString("N0"), updated.ToString("N0"));
        }
        catch (Exception ex)
        {
            SyncStatusMessage = string.Format(App.GetString("MsgSyncError", "❌ 同步失败: {0}"), ex.Message);
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
