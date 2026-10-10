using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Search;
using PicHarbor.Core.Storage;
using PicHarbor.Core.Util;
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

    private bool suppressSchemeSave;

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
    private string googlePhotosPythonPath = "python";

    public ObservableCollection<string> GooglePhotosQualityOptions { get; } = new()
    {
        "原画质不计配额（推荐）",
        "原画质（占用空间）",
        "压缩画质"
    };

    [ObservableProperty]
    private int selectedGooglePhotosQualityIndex = 0;

    [ObservableProperty]
    private bool googlePhotosSkipExistingFilenames = false;

    // Advanced Section Expansion State
    [ObservableProperty]
    private bool isBackupAdvancedExpanded = false;

    [ObservableProperty]
    private bool isGalleryAdvancedExpanded = false;

    [ObservableProperty]
    private bool isCloudAdvancedExpanded = false;

    public string BackupAdvancedButtonText => IsBackupAdvancedExpanded
        ? App.GetString("BtnCollapseAdvanced", "▲ 收起高级设置")
        : App.GetString("BtnExpandAdvanced", "▼ 展开高级设置");
    public string GalleryAdvancedButtonText => IsGalleryAdvancedExpanded
        ? App.GetString("BtnCollapseAdvanced", "▲ 收起高级设置")
        : App.GetString("BtnExpandAdvanced", "▼ 展开高级设置");
    public string CloudAdvancedButtonText => IsCloudAdvancedExpanded
        ? App.GetString("BtnCollapseAdvanced", "▲ 收起高级设置")
        : App.GetString("BtnExpandAdvanced", "▼ 展开高级设置");

    partial void OnIsBackupAdvancedExpandedChanged(bool value) => OnPropertyChanged(nameof(BackupAdvancedButtonText));
    partial void OnIsGalleryAdvancedExpandedChanged(bool value) => OnPropertyChanged(nameof(GalleryAdvancedButtonText));
    partial void OnIsCloudAdvancedExpandedChanged(bool value) => OnPropertyChanged(nameof(CloudAdvancedButtonText));

    [RelayCommand]
    private void ToggleBackupAdvanced() => IsBackupAdvancedExpanded = !IsBackupAdvancedExpanded;

    [RelayCommand]
    private void ToggleGalleryAdvanced() => IsGalleryAdvancedExpanded = !IsGalleryAdvancedExpanded;

    [RelayCommand]
    private void ToggleCloudAdvanced() => IsCloudAdvancedExpanded = !IsCloudAdvancedExpanded;

    [RelayCommand]
    private void BrowsePythonPath()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = App.GetString("PythonBrowseFilter", "Python 解释器 (python.exe)|python.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            Title = App.GetString("PythonBrowseTitle", "选择 Python 可执行文件路径")
        };
        if (dlg.ShowDialog() == true)
        {
            GooglePhotosPythonPath = dlg.FileName;
        }
    }

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
            CodecStatusText = App.GetString("CodecChecking", "正在检测 HEIF / HEVC 扩展…");
        }
        else if (HeifInstalled && HevcInstalled)
        {
            CodecStatusText = App.GetString("CodecReadyRestart", "HEIF 与 HEVC 扩展都已安装。若缩略图仍是占位图，请重启本软件。");
        }
        else if (!HeifInstalled && !HevcInstalled)
        {
            CodecStatusText = App.GetString("CodecMissingBoth", "尚未检测到 HEIF 图像扩展和 HEVC 视频扩展。");
        }
        else if (!HeifInstalled)
        {
            CodecStatusText = App.GetString("CodecMissingHeif", "尚未检测到 HEIF 图像扩展。");
        }
        else
        {
            CodecStatusText = App.GetString("CodecMissingHevc", "尚未检测到 HEVC 视频扩展。");
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

    [RelayCommand]
    private void OpenGitHub() => OpenUrl(AppLinks.GitHub);

    [RelayCommand]
    private void OpenOfficialWebsite() => OpenUrl(AppLinks.OfficialWebsite);

    [RelayCommand]
    private void OpenLicense() => OpenUrl(AppLinks.License);

    [RelayCommand]
    private void OpenAcknowledgements() => OpenUrl(AppLinks.Acknowledgements);

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open URL '{url}': {ex.Message}");
        }
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
        CopyFeedbackText = App.GetString("CopyFeedbackCopied", "已复制");
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
            SyncStatusMessage = syncStatus.Set("FmtOpenFolderFailed", "无法打开文件夹: {0}", ex.Message);
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
        GooglePhotosPythonPath = !string.IsNullOrWhiteSpace(config.GooglePhotosPythonPath) ? config.GooglePhotosPythonPath : "python";
        if (config.GooglePhotosStorageSaver)
        {
            SelectedGooglePhotosQualityIndex = 2;
        }
        else if (!config.GooglePhotosUnlimitedQuality)
        {
            SelectedGooglePhotosQualityIndex = 1;
        }
        else
        {
            SelectedGooglePhotosQualityIndex = 0;
        }
        GooglePhotosSkipExistingFilenames = config.GooglePhotosSkipExistingFilenames;
        RefreshIdleProxyText();
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
        config.GooglePhotosPythonPath = GooglePhotosPythonPath;
        config.GooglePhotosUnlimitedQuality = SelectedGooglePhotosQualityIndex == 0;
        config.GooglePhotosStorageSaver = SelectedGooglePhotosQualityIndex == 2;
        config.GooglePhotosSkipExistingFilenames = GooglePhotosSkipExistingFilenames;
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
    partial void OnSelectedSchemeChanged(string value)
    {
        if (!suppressSchemeSave)
        {
            SaveConfig();
        }
    }

    public void OnLanguageChanged()
    {
        RefreshSchemeLabels();
        RefreshQualityLabels();
        NotifyCodecStatus();
        RefreshIdleProxyText();
        OnPropertyChanged(nameof(BackupAdvancedButtonText));
        OnPropertyChanged(nameof(GalleryAdvancedButtonText));
        OnPropertyChanged(nameof(CloudAdvancedButtonText));
        RefreshRetentionLabels();
        ApplyDbMetricText();
        if (!string.IsNullOrEmpty(CopyFeedbackText))
        {
            CopyFeedbackText = App.GetString("CopyFeedbackCopied", "已复制");
        }

        if (syncStatus.HasValue)
        {
            SyncStatusMessage = syncStatus.Current;
        }

        if (syncDetail.HasValue)
        {
            SyncProgressDetailText = syncDetail.Current;
        }

        if (syncUpdated.HasValue)
        {
            SyncUpdatedText = syncUpdated.Current;
        }

        if (pruneStatus.HasValue)
        {
            PruneStatusMessage = pruneStatus.Current;
        }

        if (repairStatus.HasValue)
        {
            RepairStatusMessage = repairStatus.Current;
        }

        if (repairDetail.HasValue)
        {
            RepairProgressDetailText = repairDetail.Current;
        }
    }

    private void RefreshRetentionLabels()
    {
        int index = SelectedRetentionIndex;
        UiLists.Replace(
            RetentionPolicies,
            App.GetString("RetentionKeepAll", "永久保留所有历史（默认）"),
            App.GetString("RetentionPrune180Days", "清理 180 天前的冗余历史记录"),
            App.GetString("RetentionPrune90Days", "清理 90 天前的冗余历史记录"),
            App.GetString("RetentionPrune30Days", "清理 30 天前的冗余历史记录"),
            App.GetString("RetentionPruneAll", "清理所有历史备份流水记录"));
        if (index >= 0 && index < RetentionPolicies.Count && SelectedRetentionIndex != index)
        {
            SelectedRetentionIndex = index;
        }
    }

    private void ApplyDbMetricText()
    {
        DbMetricsSummaryText = dbMetricKind switch
        {
            "invalid" => App.GetString("DbMetricsInvalid", "归档目录无效或未就绪"),
            "failed" => string.Format(App.GetString("FmtDbMetricsFail", "读取数据库指标失败: {0}"), dbMetricError),
            "ready" => string.Format(
                App.GetString("FmtDbMetrics", "数据库实体: {0} (WAL: {1})  |  核心媒体索引: {2:N0} 条  |  历史追溯流水: {3:N0} 条"),
                DbSizeText,
                DbWalSizeText,
                CoreFilesCount,
                HistoryRecordsCount),
            _ => App.GetString("DbMetricsSummaryInitial", "点击刷新查看数据库体积与记录统计")
        };
    }

    private void RefreshQualityLabels()
    {
        int index = SelectedGooglePhotosQualityIndex is >= 0 and <= 2 ? SelectedGooglePhotosQualityIndex : 0;
        UiLists.Replace(
            GooglePhotosQualityOptions,
            App.GetString("QualityOriginalUnlimited", "原画质不计配额（推荐）"),
            App.GetString("QualityOriginalWithQuota", "原画质（占用空间）"),
            App.GetString("QualityStorageSaver", "压缩画质"));
        SelectedGooglePhotosQualityIndex = index;
    }

    private void RefreshSchemeLabels()
    {
        string token = SchemeToken(SelectedScheme);
        string[] labels =
        [
            "month (YYYY-MM)",
            "year (YYYY)",
            "day (YYYY-MM-DD)",
            "flat (" + App.GetString("SchemeFlatFolder", "单一目录") + ")"
        ];
        suppressSchemeSave = true;
        UiLists.Replace(OrganizeSchemes, labels);
        string match = labels.FirstOrDefault(label => SchemeToken(label) == token) ?? labels[0];
        if (!string.Equals(SelectedScheme, match, StringComparison.Ordinal))
        {
            SelectedScheme = match;
        }

        suppressSchemeSave = false;
    }

    private static string SchemeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "month";
        if (value.StartsWith("year-month", StringComparison.OrdinalIgnoreCase)) return "year-month";
        if (value.StartsWith("year", StringComparison.OrdinalIgnoreCase)) return "year";
        if (value.StartsWith("day", StringComparison.OrdinalIgnoreCase)) return "day";
        if (value.StartsWith("flat", StringComparison.OrdinalIgnoreCase)) return "flat";
        return "month";
    }

    private void RefreshIdleProxyText()
    {
        if (IsTestingProxy)
        {
            return;
        }

        ProxyStatusText = !string.IsNullOrWhiteSpace(GooglePhotosProxy)
            ? $"{GooglePhotosProxy} {App.GetString("ProxyConfiguredSuffix", "(已配置)")}"
            : App.GetString("ProxyDirect", "直连 (未配置代理)");
    }
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
    partial void OnGooglePhotosPythonPathChanged(string value) => SaveConfig();
    partial void OnSelectedGooglePhotosQualityIndexChanged(int value) => SaveConfig();
    partial void OnGooglePhotosSkipExistingFilenamesChanged(bool value) => SaveConfig();

    [RelayCommand(CanExecute = nameof(CanTestProxy))]
    private async Task TestProxyAsync()
    {
        if (IsTestingProxy) return;
        IsTestingProxy = true;
        ProxyStatusColor = "#3B82F6";
        ProxyStatusText = App.GetString("ProxyTesting", "⏳ 正在检测网络通道...");

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
                ProxyStatusText = string.Format(App.GetString("FmtProxyOk", "✅ 代理通道畅通 (延迟 {0} ms)"), sw.ElapsedMilliseconds);
            }
            else
            {
                ProxyStatusColor = "#EF4444";
                ProxyStatusText = string.Format(App.GetString("FmtProxyHttpStatus", "⚠️ 响应状态: {0}"), (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            ProxyStatusColor = "#EF4444";
            ProxyStatusText = string.Format(App.GetString("FmtConnFailed", "❌ 连接失败: {0}"), ex.Message);
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
            SyncStatusMessage = syncStatus.Set("MsgDirNotExist", "归档目录不存在，请先选择有效的备份目标路径。");
            ShowProgress = false;
            return;
        }

        string dbPath = TransferJournal.ResolveDatabasePath(ArchivePath);
        if (!File.Exists(LongPath.ToExtended(dbPath)))
        {
            SyncStatusMessage = syncStatus.Set("MsgDbNotFound", "未在归档目录中找到 picharbor.db 数据库，请先执行备份。");
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
        SyncProgressDetailText = syncDetail.Set("MsgReadingDb", "正在读取归档数据库...");
        SyncUpdatedText = syncUpdated.Set("MsgUpdatedCount", "已成功更新: {0} 项", 0);
        syncStatus.Clear();
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
                    SyncProgressDetailText = syncDetail.Set("MsgCompletedProgress", "已完成: {0} / {1} 文件", p.Processed.ToString("N0"), p.Total.ToString("N0"));
                    SyncUpdatedText = syncUpdated.Set("MsgUpdatedCount", "已成功更新: {0} 项", p.Updated.ToString("N0"));
                }
                else
                {
                    SyncProgressPercentage = 0;
                    SyncProgressOverlayText = "0.0% (0/0)";
                    SyncProgressDetailText = syncDetail.Set("MsgNoFilesToSync", "归档清单中未找到可同步的文件");
                    SyncUpdatedText = syncUpdated.Set("MsgUpdatedCount", "已成功更新: {0} 项", 0);
                }
            });

            int updated = await FileTimeSynchronizer.BatchSyncArchiveFileTimesAsync(
                ArchivePath,
                syncCreationTime: SyncExifToCreationTime,
                progress: progress);

            SyncStatusMessage = syncStatus.Set("MsgSyncSuccess", "✅ 同步完成！共检索 {0} 个归档文件，成功将 {1} 个文件的 EXIF 时间同步到 Windows 系统时间。", SyncProcessedCount.ToString("N0"), updated.ToString("N0"));
        }
        catch (Exception ex)
        {
            SyncStatusMessage = syncStatus.Set("MsgSyncError", "❌ 同步失败: {0}", ex.Message);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    // ==========================================
    // 数据库健康管理、冗余清理与自愈修复
    // ==========================================

    [ObservableProperty]
    private string dbSizeText = "0 B";

    [ObservableProperty]
    private string dbWalSizeText = "0 B";

    [ObservableProperty]
    private int coreFilesCount = 0;

    [ObservableProperty]
    private int historyRecordsCount = 0;

    [ObservableProperty]
    private int backupSessionsCount = 0;

    [ObservableProperty]
    private string dbMetricsSummaryText = "点击刷新查看数据库体积与记录统计";
    private readonly StickyText syncStatus = new();
    private readonly StickyText syncDetail = new();
    private readonly StickyText syncUpdated = new();
    private readonly StickyText pruneStatus = new();
    private readonly StickyText repairStatus = new();
    private readonly StickyText repairDetail = new();
    private string dbMetricKind = "initial";
    private string dbMetricError = "";

    public ObservableCollection<string> RetentionPolicies { get; } = new()
    {
        "永久保留所有历史（默认）",
        "清理 180 天前的冗余历史记录",
        "清理 90 天前的冗余历史记录",
        "清理 30 天前的冗余历史记录",
        "清理所有历史备份流水记录"
    };

    [ObservableProperty]
    private int selectedRetentionIndex = 0;

    [ObservableProperty]
    private string pruneStatusMessage = "";

    [ObservableProperty]
    private bool isPruning = false;

    [ObservableProperty]
    private bool isRepairing = false;

    [ObservableProperty]
    private double repairProgressPercentage = 0;

    [ObservableProperty]
    private string repairProgressOverlayText = "0.0%";

    [ObservableProperty]
    private string repairProgressDetailText = "";

    [ObservableProperty]
    private string repairStatusMessage = "";

    [ObservableProperty]
    private bool isRefreshingDbMetrics = false;

    [RelayCommand]
    public async Task RefreshDbMetricsAsync()
    {
        if (IsRefreshingDbMetrics) return;
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            dbMetricKind = "invalid";
            ApplyDbMetricText();
            return;
        }

        IsRefreshingDbMetrics = true;
        try
        {
            var metrics = await ArchiveRepository.GetDatabaseMetricsAsync(ArchivePath).ConfigureAwait(false);
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => ApplyDbMetrics(metrics));
            }
            else
            {
                ApplyDbMetrics(metrics);
            }
        }
        catch (Exception ex)
        {
            dbMetricKind = "failed";
            dbMetricError = ex.Message;
            ApplyDbMetricText();
        }
        finally
        {
            IsRefreshingDbMetrics = false;
        }
    }

    private void ApplyDbMetrics(DatabaseMetricsRecord metrics)
    {
        DbSizeText = ByteSize.Humanize(metrics.DatabaseSizeBytes);
        DbWalSizeText = ByteSize.Humanize(metrics.WalSizeBytes);
        CoreFilesCount = metrics.CoreFilesCount;
        HistoryRecordsCount = metrics.HistoryRecordsCount;
        BackupSessionsCount = metrics.BackupSessionsCount;
        dbMetricKind = "ready";
        ApplyDbMetricText();
    }

    [RelayCommand]
    public async Task PruneRedundantDataAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            PruneStatusMessage = pruneStatus.Set("PruneNoArchive", "未指定有效的归档目录。");
            return;
        }

        if (SelectedRetentionIndex == 0)
        {
            PruneStatusMessage = pruneStatus.Set("PruneKeepAll", "当前策略为【永久保留所有历史】，无需清理。若需清理请在下拉框选择保留期限。");
            return;
        }

        IsPruning = true;
        PruneStatusMessage = pruneStatus.Set("PruneRunning", "正在清理过期冗余审计数据并压缩数据库碎片...");

        try
        {
            DateTimeOffset cutoff;
            switch (SelectedRetentionIndex)
            {
                case 1: cutoff = DateTimeOffset.UtcNow.AddDays(-180); break;
                case 2: cutoff = DateTimeOffset.UtcNow.AddDays(-90); break;
                case 3: cutoff = DateTimeOffset.UtcNow.AddDays(-30); break;
                case 4: cutoff = DateTimeOffset.UtcNow.AddMinutes(1); break; // All finished
                default: cutoff = DateTimeOffset.UtcNow.AddDays(-90); break;
            }

            var (prunedSessions, prunedRecords) = await ArchiveRepository.PruneRedundantHistoryAsync(ArchivePath, cutoff);
            await RefreshDbMetricsAsync();
            PruneStatusMessage = pruneStatus.Set("FmtPruneDone", "✅ 清理完成！已安全清理 {0} 个过期批次共 {1:N0} 条历史流水记录，物理空间已整理并回收。", prunedSessions, prunedRecords);
        }
        catch (Exception ex)
        {
            PruneStatusMessage = pruneStatus.Set("FmtPruneFail", "❌ 清理失败: {0}", ex.Message);
        }
        finally
        {
            IsPruning = false;
        }
    }

    [RelayCommand]
    public async Task RepairDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            RepairStatusMessage = repairStatus.Set("PruneNoArchive", "未指定有效的归档目录。");
            return;
        }

        IsRepairing = true;
        RepairProgressPercentage = 0;
        RepairProgressOverlayText = "0.0%";
        RepairProgressDetailText = repairDetail.Set("RepairScanning", "正在扫描归档索引与磁盘文件真实性...");
        repairStatus.Clear();
        RepairStatusMessage = "";

        try
        {
            var progress = new Progress<(int Scanned, int Removed)>(p =>
            {
                RepairProgressDetailText = repairDetail.Set("FmtRepairProgress", "已比对: {0:N0} 项，发现并标记幽灵文件: {1:N0} 项", p.Scanned, p.Removed);
            });

            var (scanned, removed) = await ArchiveRepository.RepairDatabaseConsistencyAsync(ArchivePath, progress);
            await RefreshDbMetricsAsync();

            RepairProgressPercentage = 100;
            RepairProgressOverlayText = "100.0%";
            RepairStatusMessage = repairStatus.Set("FmtRepairDone", "✅ 自愈扫描修复完成！共深度比对 {0:N0} 个归档文件索引，安全剔除 {1:N0} 个磁盘已被手动删除的失效幽灵记录。数据库已恢复真实一致，物理碎片已收敛。", scanned, removed);
        }
        catch (Exception ex)
        {
            RepairStatusMessage = repairStatus.Set("FmtRepairFail", "❌ 修复失败: {0}", ex.Message);
        }
        finally
        {
            IsRepairing = false;
        }
    }
}
