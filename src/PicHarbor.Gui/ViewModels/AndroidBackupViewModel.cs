using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PicHarbor.Core.Android;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Preflight;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Scope;
using PicHarbor.Core.Storage;
using PicHarbor.Core.Transfer;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public enum AndroidBackupScopeMode
{
    Default,
    All,
    DateRange,
    Advanced
}

public sealed partial class AndroidAlbumOptionViewModel : ObservableObject
{
    public Action? OnCheckChanged { get; set; }

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string remotePath = string.Empty;

    [ObservableProperty]
    private string detailText = string.Empty;

    public int ListedFileCount { get; set; } = -1;

    public long ListedBytes { get; set; }

    private bool suppressCheckChanged = false;

    [ObservableProperty]
    private bool isChecked = false;

    [ObservableProperty]
    private bool isCustom = false;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!suppressCheckChanged)
        {
            OnCheckChanged?.Invoke();
        }
    }

    public void SetIsCheckedSilently(bool value)
    {
        if (IsChecked != value)
        {
            suppressCheckChanged = true;
            try
            {
                IsChecked = value;
            }
            finally
            {
                suppressCheckChanged = false;
            }
        }
    }
}

public partial class AndroidBackupViewModel : ObservableObject
{
    private readonly Action<string>? onPathChangedCallback;
    private CancellationTokenSource? backupCts;

    public Action? NavigateToSettingsAction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullDestinationPreview))]
    [NotifyPropertyChangedFor(nameof(TargetDriveSummary))]
    [NotifyPropertyChangedFor(nameof(DestinationSubtitle))]
    private string destinationPath = MainViewModel.DefaultArchivePath;

    [ObservableProperty]
    private string detectedDeviceModel = "Pixel 8";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullDestinationPreview))]
    [NotifyPropertyChangedFor(nameof(DestinationSubtitle))]
    private string deviceSubdir = "Pixel 8";

    public string FullDestinationPreview =>
        Path.Combine(DestinationPath, LibraryStorageService.SanitizeDeviceFolderName(DeviceSubdir));

    public string DestinationSubtitle =>
        string.Format(App.GetString("FmtTargetPath", "目标: {0}"), FullDestinationPreview);

    public string TransferringSubtitle =>
        string.Format(App.GetString("FmtTransferring", "正在传输 · {0}"), SpeedText);

    public string TargetDriveSummary
    {
        get
        {
            string? root = Path.GetPathRoot(DestinationPath);
            if (string.IsNullOrEmpty(root)) return DestinationPath;
            try
            {
                var d = new DriveInfo(root);
                if (d.IsReady)
                {
                    double freeGb = Math.Round(d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                    return string.Format(App.GetString("FmtDriveSummary", "{0} ({1}) - 可用: {2:F1} GB"), root.TrimEnd('\\'), DestinationPath, freeGb);
                }
            }
            catch { }
            return DestinationPath;
        }
    }

    public void RefreshDriveSpace()
    {
        OnPropertyChanged(nameof(TargetDriveSummary));
    }

    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DestinationSubtitle));
        OnPropertyChanged(nameof(TransferringSubtitle));
        OnPropertyChanged(nameof(HeroPillText));
        RefreshDriveSpace();
        RefreshAlbumLabels();
        ApplyConnectionStatus();
        ApplyScanStatus();
        UpdateScopeSummarySentence();
        ApplyFileStatus();
        ApplyEta();
        if (!IsTransferring && CopiedCount == 0 && SkippedCount == 0 && FailedCount == 0)
        {
            ProgressText = progressSticky.Set("MsgReady", "准备就绪");
        }
        else if (progressSticky.HasValue)
        {
            ProgressText = progressSticky.Current;
        }
    }

    [RelayCommand]
    private void NavigateToSettings() => NavigateToSettingsAction?.Invoke();

    [RelayCommand]
    private void ResetDeviceSubdir() => DeviceSubdir = DetectedDeviceModel;

    [RelayCommand]
    private void OpenDestinationFolder()
    {
        string target = Directory.Exists(FullDestinationPreview) ? FullDestinationPreview : DestinationPath;
        if (!string.IsNullOrWhiteSpace(target))
        {
            try
            {
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch { }
        }
    }

    public void SetDetectedDevice(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        string sanitized = LibraryStorageService.SanitizeDeviceFolderName(model);
        DetectedDeviceModel = sanitized;
        if (string.IsNullOrWhiteSpace(DeviceSubdir) ||
            DeviceSubdir.Equals("Pixel 8", StringComparison.OrdinalIgnoreCase) ||
            DeviceSubdir.Equals("Android", StringComparison.OrdinalIgnoreCase))
        {
            DeviceSubdir = sanitized;
        }
    }

    [ObservableProperty]
    private string selectedScheme = "month (YYYY-MM)";

    // FTP Connection Properties
    [ObservableProperty]
    private string androidFtpHost = "192.168.1.100";

    [ObservableProperty]
    private int androidFtpPort = 2121;

    [ObservableProperty]
    private string androidFtpUser = "anonymous";

    [ObservableProperty]
    private string androidFtpPassword = "";

    [ObservableProperty]
    private string androidDeviceName = "Pixel 8";

    [ObservableProperty]
    private string androidDeviceId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private string connectionStatusText = "未测试连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private bool isConnectionOk = false;

    [ObservableProperty]
    private bool isTestingConnection = false;

    // Filter and Scope Properties
    [ObservableProperty]
    private bool ignoreSmallImages = true;

    partial void OnIgnoreSmallImagesChanged(bool value) => UpdateScopeSummarySentence();

    [ObservableProperty]
    private int minFileSizeKb = 100;

    partial void OnMinFileSizeKbChanged(int value) => UpdateScopeSummarySentence();

    [ObservableProperty]
    private bool includePhotos = true;

    partial void OnIncludePhotosChanged(bool value) => UpdateScopeSummarySentence();

    [ObservableProperty]
    private bool includeVideos = true;

    partial void OnIncludeVideosChanged(bool value) => UpdateScopeSummarySentence();

    [ObservableProperty]
    private DateTime? scopeDateFrom = null;

    partial void OnScopeDateFromChanged(DateTime? value) => UpdateScopeSummarySentence();

    [ObservableProperty]
    private DateTime? scopeDateTo = null;

    partial void OnScopeDateToChanged(DateTime? value) => UpdateScopeSummarySentence();

    [ObservableProperty]
    private AndroidBackupScopeMode scopeMode = AndroidBackupScopeMode.Default;

    private bool suppressCheckChanged = false;

    public bool IsScopeDefault
    {
        get => ScopeMode == AndroidBackupScopeMode.Default;
        set
        {
            if (value && ScopeMode != AndroidBackupScopeMode.Default)
            {
                ScopeMode = AndroidBackupScopeMode.Default;
                ApplyScopeMode(ScopeMode);
                NotifyScopeProperties();
            }
        }
    }

    public bool IsScopeAll
    {
        get => ScopeMode == AndroidBackupScopeMode.All;
        set
        {
            if (value && ScopeMode != AndroidBackupScopeMode.All)
            {
                ScopeMode = AndroidBackupScopeMode.All;
                ApplyScopeMode(ScopeMode);
                NotifyScopeProperties();
            }
        }
    }

    public bool IsScopeDateRange
    {
        get => ScopeMode == AndroidBackupScopeMode.DateRange;
        set
        {
            if (value && ScopeMode != AndroidBackupScopeMode.DateRange)
            {
                ScopeMode = AndroidBackupScopeMode.DateRange;
                ApplyScopeMode(ScopeMode);
                NotifyScopeProperties();
            }
        }
    }

    public bool IsScopeAdvanced
    {
        get => ScopeMode == AndroidBackupScopeMode.Advanced;
        set
        {
            if (value && ScopeMode != AndroidBackupScopeMode.Advanced)
            {
                ScopeMode = AndroidBackupScopeMode.Advanced;
                ApplyScopeMode(ScopeMode);
                NotifyScopeProperties();
            }
        }
    }

    private void NotifyScopeProperties()
    {
        OnPropertyChanged(nameof(IsScopeDefault));
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeDateRange));
        OnPropertyChanged(nameof(IsScopeAdvanced));
    }

    [ObservableProperty]
    private string scopeSummarySentence = "";

    public string HeroPillText
    {
        get
        {
            if (IsTransferring)
            {
                return string.Format(App.GetString("FmtBackupRunning", "备份中 ({0:F0}%)"), ProgressPercentage);
            }
            if (CopiedCount > 0 || SkippedCount > 0 || FailedCount > 0)
            {
                if (FailedCount > 0)
                {
                    return string.Format(App.GetString("FmtBackupDoneFailed", "完成 ({0}传输, {1}失败)"), CopiedCount, FailedCount);
                }

                return string.Format(App.GetString("FmtBackupDoneSkipped", "完成 ({0}传输, {1}跳过)"), CopiedCount, SkippedCount);
            }
            return IsConnectionOk
                ? App.GetString("AndroidConnectedReady", "已连接 · 准备就绪")
                : App.GetString("MsgReady", "准备就绪");
        }
    }

    private static bool IsCameraOrScreenshots(string path)
    {
        string p = path.ToLowerInvariant();
        return p.Contains("camera") || p.Contains("dcim") || p.Contains("screenshot");
    }

    private void HandleAlbumCheckChanged()
    {
        if (suppressCheckChanged) return;
        if (ScopeMode != AndroidBackupScopeMode.Advanced)
        {
            ScopeMode = AndroidBackupScopeMode.Advanced;
            NotifyScopeProperties();
        }
        UpdateScopeSummarySentence();
    }

    private void ApplyScopeMode(AndroidBackupScopeMode mode)
    {
        suppressCheckChanged = true;
        try
        {
            switch (mode)
            {
                case AndroidBackupScopeMode.Default:
                    foreach (var album in Albums)
                    {
                        album.SetIsCheckedSilently(IsCameraOrScreenshots(album.RemotePath));
                    }
                    break;
                case AndroidBackupScopeMode.All:
                    foreach (var album in Albums)
                    {
                        album.SetIsCheckedSilently(true);
                    }
                    break;
                case AndroidBackupScopeMode.DateRange:
                    if (!Albums.Any(a => a.IsChecked))
                    {
                        foreach (var album in Albums)
                        {
                            album.SetIsCheckedSilently(IsCameraOrScreenshots(album.RemotePath));
                        }
                    }
                    break;
                case AndroidBackupScopeMode.Advanced:
                    break;
            }
        }
        finally
        {
            suppressCheckChanged = false;
        }
        UpdateScopeSummarySentence();
    }

    public void UpdateScopeSummarySentence()
    {
        var checkedAlbums = Albums.Where(a => a.IsChecked).ToList();
        string albumPart;
        if (checkedAlbums.Count == 0)
        {
            albumPart = App.GetString("AlbumNoneSelected", "未选择任何相册");
        }
        else if (checkedAlbums.Count == Albums.Count && Albums.Count > 0)
        {
            albumPart = string.Format(App.GetString("FmtAlbumAllCount", "全部 {0} 个相册"), checkedAlbums.Count);
        }
        else
        {
            var names = checkedAlbums.Take(2).Select(a =>
            {
                string name = a.DisplayName;
                int parenIdx = name.IndexOf('(');
                if (parenIdx > 0) name = name.Substring(0, parenIdx);
                return name.Replace("📷", "").Replace("📱", "").Replace("💬", "").Replace("📥", "").Replace("📁", "").Trim();
            }).ToList();

            if (checkedAlbums.Count > 2)
            {
                albumPart = string.Format(
                    App.GetString("FmtAlbumListed", "{0} 等 {1} 个相册"),
                    string.Join(App.GetString("AlbumNameSeparator", "、"), names),
                    checkedAlbums.Count);
            }
            else
            {
                albumPart = string.Join(App.GetString("AlbumNameJoiner", " 与 "), names);
            }
        }

        string typePart = (IncludePhotos, IncludeVideos) switch
        {
            (true, true) => App.GetString("TypePhotoAndVideo", "照片与视频"),
            (true, false) => App.GetString("TypePhotoOnly", "仅照片"),
            (false, true) => App.GetString("TypeVideoOnly", "仅视频"),
            _ => App.GetString("TypeNoMedia", "无媒体类型")
        };

        string datePart = "";
        if (IsScopeDateRange)
        {
            if (ScopeDateFrom.HasValue && ScopeDateTo.HasValue)
                datePart = string.Format(App.GetString("FmtDateBetween", "在 {0:yyyy-MM-dd} 至 {1:yyyy-MM-dd} 期间的"), ScopeDateFrom.Value, ScopeDateTo.Value);
            else if (ScopeDateFrom.HasValue)
                datePart = string.Format(App.GetString("FmtDateFrom", "从 {0:yyyy-MM-dd} 起的"), ScopeDateFrom.Value);
            else if (ScopeDateTo.HasValue)
                datePart = string.Format(App.GetString("FmtDateUntil", "截至 {0:yyyy-MM-dd} 的"), ScopeDateTo.Value);
            else
                datePart = App.GetString("DateCustomRange", "按指定日期范围的");
        }

        string filterPart = "";
        if (IgnoreSmallImages)
        {
            filterPart = string.Format(App.GetString("FmtFilterSmall", "，已过滤小于 {0} KB 的小图与临时缓存"), MinFileSizeKb);
        }

        if (checkedAlbums.Count == 0)
        {
            ScopeSummarySentence = App.GetString("ScopeNoAlbum", "⚠️ 当前未勾选任何相册，请至少勾选一个相册进行备份。");
        }
        else if (IsScopeDateRange)
        {
            ScopeSummarySentence = string.Format(App.GetString("FmtScopeBackupRange", "💡 本次范围：将备份 {0} {1} 中的{2}{3}。"), datePart, albumPart, typePart, filterPart);
        }
        else
        {
            ScopeSummarySentence = string.Format(App.GetString("FmtScopeBackupSmart", "💡 本次范围：将智能备份 {0} 中的全部{1}{2}。"), albumPart, typePart, filterPart);
        }
    }

    public string ScopeDateFromText
    {
        get => ScopeDateFrom?.ToString("yyyy-MM-dd") ?? string.Empty;
        set
        {
            if (DateTime.TryParse(value, out DateTime dt))
            {
                ScopeDateFrom = dt;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                ScopeDateFrom = null;
            }
            OnPropertyChanged(nameof(ScopeDateFromText));
        }
    }

    public string ScopeDateToText
    {
        get => ScopeDateTo?.ToString("yyyy-MM-dd") ?? string.Empty;
        set
        {
            if (DateTime.TryParse(value, out DateTime dt))
            {
                ScopeDateTo = dt;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                ScopeDateTo = null;
            }
            OnPropertyChanged(nameof(ScopeDateToText));
        }
    }

    public Action? OpenManualModalAction { get; set; }

    [RelayCommand]
    private void OpenManualModal() => OpenManualModalAction?.Invoke();

    [ObservableProperty]
    private bool isAdvancedPanelOpen = false;

    [ObservableProperty]
    private bool isScanningAlbums = false;

    [ObservableProperty]
    private string scanStatusText = "";

    [ObservableProperty]
    private string customAlbumPath = "";

    public ObservableCollection<AndroidAlbumOptionViewModel> Albums { get; } = new();

    // Progress & Execution Properties
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private bool isTransferring = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private double progressPercentage = 0;

    [ObservableProperty]
    private string currentFileName = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferringSubtitle))]
    private string speedText = "0.0 MB/s";

    [ObservableProperty]
    private int plannedItemCount;

    public double SpeedBytesPerSecond { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private int copiedCount = 0;

    [ObservableProperty]
    private int remainingFilesCount = 0;

    [ObservableProperty]
    private int totalFilesCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private int skippedCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private int failedCount = 0;

    [ObservableProperty]
    private string transferredSizeText = "0 B / 0 B";

    [ObservableProperty]
    private string etaText = "--";

    [ObservableProperty]
    private string progressText = App.GetString("MsgReady", "准备就绪");
    private readonly StickyText progressSticky = new();
    private string? fileStatusKey;
    private string? etaKey;
    private TimeSpan? etaSpan;

    // Modal Details Properties
    [ObservableProperty]
    private bool isDetailModalOpen = false;

    [ObservableProperty]
    private string detailModalTitle = "";

    [ObservableProperty]
    private ObservableCollection<TransferItemDetail> detailItems = new();

    private readonly List<TransferItemDetail> copiedDetails = new();
    private readonly List<TransferItemDetail> skippedDetails = new();
    private readonly List<TransferItemDetail> failedDetails = new();

    public ObservableCollection<string> OrganizeSchemes { get; } = new()
    {
        "month (YYYY-MM)",
        "year-month (YYYY\\YYYY-MM)",
        "year (YYYY)",
        "flat"
    };

    public ObservableCollection<string> LogEntries { get; } = new()
    {
        "[INFO] PicHarbor Android 备份引擎就绪。",
        "[INFO] 请填写手机 FTP 连接参数，选择相册后点击“开始增量备份”。"
    };

    public AndroidBackupViewModel() : this(null) { }

    public AndroidBackupViewModel(Action<string>? onPathChangedCallback)
    {
        this.onPathChangedCallback = onPathChangedCallback;
        LoadConfig();
        InitDefaultAlbums();
    }

    private void InitDefaultAlbums()
    {
        Albums.Clear();
        Albums.Add(MakePresetAlbum("/DCIM/Camera", "AlbumCamera", "📷 相机胶卷 (Camera)", "AlbumCameraDetail", "核心拍摄原片与视频", true));
        Albums.Add(MakePresetAlbum("/Pictures/Screenshots", "AlbumScreenshots", "📱 屏幕截图 (Screenshots)", "AlbumScreenshotsDetail", "系统截图目录", true));
        Albums.Add(MakePresetAlbum("/Pictures/WeiXin", "AlbumWeChat", "💬 微信相册 (WeChat)", "AlbumWeChatDetail", "微信保存的图片与视频", false));
        Albums.Add(MakePresetAlbum("/Download", "AlbumDownloads", "📥 下载内容 (Download)", "AlbumDownloadsDetail", "浏览器和下载内容", false));
        UpdateScopeSummarySentence();
    }

    private AndroidAlbumOptionViewModel MakePresetAlbum(string remotePath, string nameKey, string nameFallback, string detailKey, string detailFallback, bool isChecked)
    {
        return new AndroidAlbumOptionViewModel
        {
            DisplayName = App.GetString(nameKey, nameFallback),
            RemotePath = remotePath,
            DetailText = App.GetString(detailKey, detailFallback),
            IsChecked = isChecked,
            IsCustom = false,
            OnCheckChanged = HandleAlbumCheckChanged
        };
    }

    private void RefreshAlbumLabels()
    {
        foreach (AndroidAlbumOptionViewModel album in Albums)
        {
            if (LocalizeAlbumName(album.RemotePath) is string name)
            {
                album.DisplayName = name;
            }

            if (album.ListedFileCount >= 0)
            {
                album.DetailText = string.Format(App.GetString("FmtAlbumCount", "{0:N0} 项 · {1}"), album.ListedFileCount, ByteSize.Humanize(album.ListedBytes));
            }
            else if (album.IsCustom)
            {
                album.DetailText = App.GetString("AlbumCustomDetail", "自定义添加目录");
            }
            else if (LocalizeAlbumDetail(album.RemotePath) is string detail)
            {
                album.DetailText = detail;
            }
        }
    }

    private static string? LocalizeAlbumName(string remotePath) => AlbumKind(remotePath) switch
    {
        "camera" => App.GetString("AlbumCamera", "📷 相机胶卷 (Camera)"),
        "screenshots" => App.GetString("AlbumScreenshots", "📱 屏幕截图 (Screenshots)"),
        "screenrecord" => App.GetString("AlbumScreenRecord", "📹 屏幕录制 (Screen Recordings)"),
        "wechat" => App.GetString("AlbumWeChat", "💬 微信相册 (WeChat)"),
        "qq" => App.GetString("AlbumQq", "🐧 QQ相册 (QQ)"),
        "douyin" => App.GetString("AlbumDouyin", "🎵 抖音/TikTok"),
        "download" => App.GetString("AlbumDownloads", "📥 下载内容 (Download)"),
        "raw" => App.GetString("AlbumRaw", "🎞️ RAW 原片 (Raw)"),
        "lightroom" => App.GetString("AlbumLightroom", "🎨 Lightroom 导出"),
        "wallpapers" => App.GetString("AlbumWallpapers", "🖼️ 壁纸 (Wallpapers)"),
        _ => null
    };

    private static string? LocalizeAlbumDetail(string remotePath) => AlbumKind(remotePath) switch
    {
        "camera" => App.GetString("AlbumCameraDetail", "核心拍摄原片与视频"),
        "screenshots" => App.GetString("AlbumScreenshotsDetail", "系统截图目录"),
        "wechat" => App.GetString("AlbumWeChatDetail", "微信保存的图片与视频"),
        "download" => App.GetString("AlbumDownloadsDetail", "浏览器和下载内容"),
        _ => null
    };

    private static string AlbumKind(string remotePath)
    {
        string norm = remotePath.TrimEnd('/', '\\').Replace('\\', '/');
        if (norm.EndsWith("/DCIM/Camera", StringComparison.OrdinalIgnoreCase)) return "camera";
        if (norm.EndsWith("/Screenshots", StringComparison.OrdinalIgnoreCase) || norm.EndsWith("/ScreenCapture", StringComparison.OrdinalIgnoreCase)) return "screenshots";
        if (norm.EndsWith("/ScreenRecorder", StringComparison.OrdinalIgnoreCase)) return "screenrecord";
        if (norm.EndsWith("/WeiXin", StringComparison.OrdinalIgnoreCase) || norm.EndsWith("/WeChat", StringComparison.OrdinalIgnoreCase)) return "wechat";
        if (norm.EndsWith("/QQ", StringComparison.OrdinalIgnoreCase)) return "qq";
        if (norm.EndsWith("/Douyin", StringComparison.OrdinalIgnoreCase) || norm.EndsWith("/TikTok", StringComparison.OrdinalIgnoreCase)) return "douyin";
        if (norm.EndsWith("/Download", StringComparison.OrdinalIgnoreCase)) return "download";
        if (norm.EndsWith("/Raw", StringComparison.OrdinalIgnoreCase)) return "raw";
        if (norm.EndsWith("/Lightroom", StringComparison.OrdinalIgnoreCase)) return "lightroom";
        if (norm.EndsWith("/Wallpapers", StringComparison.OrdinalIgnoreCase)) return "wallpapers";
        return "";
    }

    private string connectionKey = "AndroidConnUntested";
    private string connectionFallback = "未测试连接";
    private object[] connectionArgs = Array.Empty<object>();
    private string scanKey = "";
    private string scanFallback = "";
    private object[] scanArgs = Array.Empty<object>();

    private void SetConnection(string key, string fallback, params object[] args)
    {
        connectionKey = key;
        connectionFallback = fallback;
        connectionArgs = args;
        ApplyConnectionStatus();
    }

    private void ApplyConnectionStatus()
    {
        ConnectionStatusText = connectionArgs.Length == 0
            ? App.GetString(connectionKey, connectionFallback)
            : string.Format(App.GetString(connectionKey, connectionFallback), connectionArgs);
    }

    private void SetFileStatus(string key, string fallback)
    {
        fileStatusKey = key;
        CurrentFileName = App.GetString(key, fallback);
    }

    private void ApplyFileStatus()
    {
        if (fileStatusKey is not null)
        {
            CurrentFileName = App.GetString(fileStatusKey, "");
        }
    }

    private void SetEtaKey(string key, string fallback)
    {
        etaKey = key;
        etaSpan = null;
        EtaText = App.GetString(key, fallback);
    }

    private void SetEtaSpan(TimeSpan span)
    {
        etaKey = null;
        etaSpan = span;
        EtaText = UiText.Duration(span);
    }

    private void ApplyEta()
    {
        if (etaKey is not null)
        {
            EtaText = App.GetString(etaKey, "");
        }
        else if (etaSpan is TimeSpan span)
        {
            EtaText = UiText.Duration(span);
        }
    }

    private void SetScan(string key, string fallback, params object[] args)
    {
        scanKey = key;
        scanFallback = fallback;
        scanArgs = args;
        ApplyScanStatus();
    }

    private void ApplyScanStatus()
    {
        if (string.IsNullOrEmpty(scanKey))
        {
            return;
        }

        ScanStatusText = scanArgs.Length == 0
            ? App.GetString(scanKey, scanFallback)
            : string.Format(App.GetString(scanKey, scanFallback), scanArgs);
    }

    private void LoadConfig()
    {
        var config = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(config.DestinationPath))
        {
            DestinationPath = config.DestinationPath;
        }
        if (!string.IsNullOrWhiteSpace(config.SelectedScheme))
        {
            SelectedScheme = config.SelectedScheme;
        }
        AndroidFtpHost = config.AndroidBackupFtpHost;
        AndroidFtpPort = config.AndroidBackupFtpPort;
        AndroidFtpUser = config.AndroidBackupFtpUser;
        AndroidFtpPassword = config.AndroidBackupFtpPassword;
        AndroidDeviceName = config.AndroidBackupDeviceName;
        AndroidDeviceId = config.AndroidBackupDeviceId;
        MinFileSizeKb = config.AndroidBackupMinFileSizeKb;
        IgnoreSmallImages = config.AndroidBackupIgnoreSmallImages;
        IncludePhotos = config.AndroidBackupIncludePhotos;
        IncludeVideos = config.AndroidBackupIncludeVideos;
    }

    public void SaveConfig()
    {
        var config = AppSettings.Load();
        config.DestinationPath = DestinationPath;
        config.SelectedScheme = SelectedScheme;
        config.AndroidBackupFtpHost = AndroidFtpHost;
        config.AndroidBackupFtpPort = AndroidFtpPort;
        config.AndroidBackupFtpUser = AndroidFtpUser;
        config.AndroidBackupFtpPassword = AndroidFtpPassword;
        config.AndroidBackupDeviceName = AndroidDeviceName;
        config.AndroidBackupDeviceId = AndroidDeviceId;
        config.AndroidBackupMinFileSizeKb = MinFileSizeKb;
        config.AndroidBackupIgnoreSmallImages = IgnoreSmallImages;
        config.AndroidBackupIncludePhotos = IncludePhotos;
        config.AndroidBackupIncludeVideos = IncludeVideos;
        AppSettings.Save(config);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = App.GetString("AndroidBrowseTitle", "选择备份归档目标目录"),
            InitialDirectory = Directory.Exists(DestinationPath) ? DestinationPath : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog() == true)
        {
            DestinationPath = dialog.FolderName;
            onPathChangedCallback?.Invoke(DestinationPath);
            SaveConfig();
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        SetConnection("AndroidConnTesting", "正在连接 FTP 服务器...");
        AddLog($"[INFO] 正在测试连接到 {AndroidFtpHost}:{AndroidFtpPort}...");

        try
        {
            using var client = new SimpleFtpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, cts.Token).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsConnectionOk = true;
                SetConnection("FmtAndroidConnOk", "✅ 连接成功 ({0}:{1})", AndroidFtpHost, AndroidFtpPort);
                AddLog($"[SUCCESS] 成功连接至 Android FTP 服务器。");
                SaveConfig();
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsConnectionOk = false;
                SetConnection("FmtAndroidConnFail", "❌ 连接失败: {0}", ex.Message);
                AddLog($"[ERROR] 连接失败: {ex.Message}");
            });
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private async Task ScanAlbumsAsync()
    {
        IsScanningAlbums = true;
        SetScan("AndroidScanRunning", "正在扫描手机相册并进行智能过滤...");
        AddLog("[INFO] 开始深度扫描手机公共相册目录 (DCIM, Pictures, Download, Movies)...");

        try
        {
            using var client = new SimpleFtpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await client.ConnectAsync(AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, cts.Token).ConfigureAwait(false);

            var filterOptions = new AndroidBackupFilterOptions
            {
                MinFileSizeBytes = MinFileSizeKb * 1024L,
                IgnoreSmallImages = IgnoreSmallImages,
                IncludePhotos = IncludePhotos,
                IncludeVideos = IncludeVideos
            };

            var discovered = await AndroidAlbumDiscovery.DiscoverAlbumsAsync(client, filterOptions, cts.Token).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Preserve checked status of existing albums
                var checkedPaths = Albums.Where(a => a.IsChecked).Select(a => a.RemotePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

                suppressCheckChanged = true;
                try
                {
                    Albums.Clear();
                    foreach (var item in discovered)
                    {
                        bool isChecked = checkedPaths.Contains(item.RemotePath) || item.IsDefaultSelected;
                        Albums.Add(new AndroidAlbumOptionViewModel
                        {
                            DisplayName = LocalizeAlbumName(item.RemotePath) ?? item.DisplayName,
                            RemotePath = item.RemotePath,
                            ListedFileCount = item.FileCount,
                            ListedBytes = item.TotalBytes,
                            DetailText = string.Format(App.GetString("FmtAlbumCount", "{0:N0} 项 · {1}"), item.FileCount, ByteSize.Humanize(item.TotalBytes)),
                            IsChecked = isChecked,
                            IsCustom = false,
                            OnCheckChanged = HandleAlbumCheckChanged
                        });
                    }
                }
                finally
                {
                    suppressCheckChanged = false;
                }

                SetScan("FmtAndroidScanDone", "✅ 扫描完成：识别出 {0} 个有效相册 (纯小图/缓存目录已自动排除)", Albums.Count);
                AddLog($"[SUCCESS] 识别到 {Albums.Count} 个有效相册。");
                UpdateScopeSummarySentence();
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SetScan("FmtAndroidScanFail", "❌ 扫描失败: {0}", ex.Message);
                AddLog($"[ERROR] 相册扫描出错: {ex.Message}");
            });
        }
        finally
        {
            IsScanningAlbums = false;
        }
    }

    [RelayCommand]
    private void SelectAllAlbums()
    {
        suppressCheckChanged = true;
        try
        {
            foreach (var a in Albums) a.SetIsCheckedSilently(true);
        }
        finally
        {
            suppressCheckChanged = false;
        }
        if (ScopeMode != AndroidBackupScopeMode.All && ScopeMode != AndroidBackupScopeMode.Advanced)
        {
            ScopeMode = AndroidBackupScopeMode.All;
            NotifyScopeProperties();
        }
        UpdateScopeSummarySentence();
    }

    [RelayCommand]
    private void DeselectAllAlbums()
    {
        suppressCheckChanged = true;
        try
        {
            foreach (var a in Albums) a.SetIsCheckedSilently(false);
        }
        finally
        {
            suppressCheckChanged = false;
        }
        if (ScopeMode != AndroidBackupScopeMode.Advanced)
        {
            ScopeMode = AndroidBackupScopeMode.Advanced;
            NotifyScopeProperties();
        }
        UpdateScopeSummarySentence();
    }

    [RelayCommand]
    private void AddCustomAlbum()
    {
        string path = CustomAlbumPath.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!path.StartsWith('/')) path = "/" + path;

        if (!Albums.Any(a => a.RemotePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Albums.Add(new AndroidAlbumOptionViewModel
            {
                DisplayName = $"📁 {Path.GetFileName(path.TrimEnd('/'))}",
                RemotePath = path,
                DetailText = App.GetString("AlbumCustomDetail", "自定义添加目录"),
                IsChecked = true,
                IsCustom = true,
                OnCheckChanged = HandleAlbumCheckChanged
            });
            CustomAlbumPath = "";
            AddLog($"[INFO] 已添加自定义相册目录: {path}");
            if (ScopeMode != AndroidBackupScopeMode.Advanced)
            {
                ScopeMode = AndroidBackupScopeMode.Advanced;
                NotifyScopeProperties();
            }
            UpdateScopeSummarySentence();
        }
    }

    [RelayCommand]
    private void RemoveAlbum(AndroidAlbumOptionViewModel album)
    {
        if (album != null)
        {
            Albums.Remove(album);
            UpdateScopeSummarySentence();
        }
    }

    [RelayCommand]
    private void SetDatePresetLast30Days()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = DateTime.Today.AddDays(-30);
    }

    [RelayCommand]
    private void SetDatePresetLast90Days()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = DateTime.Today.AddDays(-90);
    }

    [RelayCommand]
    private void SetDatePresetLast1Year()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = DateTime.Today.AddYears(-1);
    }

    [RelayCommand]
    private void SetDatePresetThisYear()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = new DateTime(DateTime.Today.Year, 1, 1);
    }

    [RelayCommand]
    private void ClearDateRange()
    {
        ScopeDateFrom = null;
        ScopeDateTo = null;
    }

    [RelayCommand]
    private void ShowCopiedDetails()
    {
        DetailModalTitle = App.GetString("AndroidDetailCopied", "🤖 Android 备份 已传输文件明细 (Copied)");
        DetailItems = new ObservableCollection<TransferItemDetail>(copiedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowSkippedDetails()
    {
        DetailModalTitle = App.GetString("AndroidDetailSkipped", "🤖 Android 备份 已跳过文件明细 (Skipped - 增量已存在)");
        DetailItems = new ObservableCollection<TransferItemDetail>(skippedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowFailedDetails()
    {
        DetailModalTitle = App.GetString("AndroidDetailFailed", "⚠️ Android 备份 失败文件明细 (Failed)");
        DetailItems = new ObservableCollection<TransferItemDetail>(failedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void CloseDetailModal()
    {
        IsDetailModalOpen = false;
    }

    [RelayCommand]
    private void CancelBackup()
    {
        if (backupCts != null && !backupCts.IsCancellationRequested)
        {
            AddLog("[WARN] 用户请求取消备份操作...");
            backupCts.Cancel();
        }
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (IsTransferring) return;

        var selectedAlbums = Albums.Where(a => a.IsChecked).Select(a => a.RemotePath).ToList();
        if (selectedAlbums.Count == 0)
        {
            MessageBox.Show(App.GetString("AndroidPickAlbumWarning", "请至少勾选一个要备份的相册文件夹。"), App.GetString("MsgBoxTitle", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveConfig();

        IsTransferring = true;
        backupCts = new CancellationTokenSource();
        var ct = backupCts.Token;

        copiedDetails.Clear();
        skippedDetails.Clear();
        failedDetails.Clear();
        CopiedCount = 0;
        RemainingFilesCount = 0;
        TotalFilesCount = 0;
        PlannedItemCount = 0;
        SpeedBytesPerSecond = 0;
        SkippedCount = 0;
        FailedCount = 0;
        ProgressPercentage = 0;
        SetFileStatus("StatusScanning", "正在扫描文件...");
        ProgressText = progressSticky.Set("AndroidBackupInit", "正在扫描 Android 设备上的媒体文件...");
        SpeedText = "0.0 MB/s";
        EtaText = "--";

        AddLog($"[INFO] 开始 Android 增量备份任务 -> 目标: {DestinationPath}");

        var preflight = new PreflightChecks();

        await Task.Run(async () =>
        {
            try
            {
                using var client = new FtpAndroidBackupClient(
                    AndroidFtpHost,
                    AndroidFtpPort,
                    AndroidFtpUser,
                    AndroidFtpPassword,
                    deviceId: string.IsNullOrWhiteSpace(AndroidDeviceId) ? AndroidFtpHost : AndroidDeviceId,
                    deviceName: AndroidDeviceName);

                await client.ConnectAsync(ct).ConfigureAwait(false);

                var filterOptions = new AndroidBackupFilterOptions
                {
                    MinFileSizeBytes = MinFileSizeKb * 1024L,
                    IgnoreSmallImages = IgnoreSmallImages,
                    IncludePhotos = IncludePhotos,
                    IncludeVideos = IncludeVideos
                };

                DateTime? fromDate = IsScopeDateRange ? ScopeDateFrom : null;
                DateTime? toDate = IsScopeDateRange ? ScopeDateTo : null;

                AddLog("[INFO] 正在枚举选定相册中的媒体文件...");
                var files = await client.EnumerateFilesAsync(selectedAlbums, filterOptions, fromDate, toDate, ct).ConfigureAwait(false);
                int itemCount = LivePhotoDetector.CountDisplayedItems(files.Select(f => f.Path));
                await Application.Current.Dispatcher.InvokeAsync(() => PlannedItemCount = itemCount);

                if (files.Count == 0)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressText = progressSticky.Set("AndroidBackupNone", "未发现需要备份的媒体文件。");
                        SetFileStatus("StatusDoneShort", "完成");
                        AddLog("[INFO] 未发现符合筛选条件的媒体文件。");
                    });
                    return;
                }

                long totalBytes = files.Sum(f => f.Size);
                AddLog($"[INFO] 共发现 {itemCount:N0} 张（文件 {files.Count:N0}），总计 {ByteSize.Humanize(totalBytes)}。");

                preflight.EnsureDestinationWritable(DestinationPath);
                preflight.EnsureSufficientFreeSpace(DestinationPath, totalBytes);

                OrganizeScheme scheme = ParseSchemeToken(SelectedScheme);
                using var journal = TransferJournal.Open(DestinationPath);
                DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;

                long backupSessionId = 0;
                if (client.Device is not null)
                {
                    string effectiveName = journal.RegisterOrUpdateDevice(client.Device.Udid, client.Device.Name, client.Device.ProductType, client.Device.Udid, "Android", runStartedAt);
                    backupSessionId = journal.BeginBackupSession(client.Device.Udid, effectiveName, client.Device.ProductType, runStartedAt);
                }

                foreach (var file in files)
                {
                    journal.EnsurePending(file);
                }

                var progressModel = new TransferProgress(files.Count, totalBytes);
                var progressTarget = new Progress<ProgressSnapshot>(UpdateSnapshotUI);
                using var reporter = new ObservableProgressReporter(progressTarget, TimeSpan.FromMilliseconds(200));
                reporter.FileCompleted += (s, result) =>
                {
                    AddLog($"[{result.Status.ToString().ToUpper()}] {result.RelativeDestPath ?? result.File.Path}");
                };

                reporter.Start(progressModel);

                var fallbackRoots = LibraryStorageService.FindAllExistingLibraryRoots()
                    .Where(root => !string.Equals(root, DestinationPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var fallbackJournals = new List<TransferJournal>();
                foreach (string fRoot in fallbackRoots)
                {
                    try { fallbackJournals.Add(TransferJournal.OpenReadOnly(fRoot)); } catch { }
                }

                try
                {
                    using var copier = new FileCopier(
                        client, journal, new DateFolderOrganizer(), new ExifMetadataExtractor(), DestinationPath, scheme,
                        readTimeout: TimeSpan.FromSeconds(30), onBytesStreamed: progressModel.RecordBytes, verifyHash: false,
                        deviceSubfolder: DeviceSubdir,
                        fallbackJournals: fallbackJournals);

                    copier.CleanStaging();

                    int copied = 0, skipped = 0, failed = 0;
                    long bytesCopied = 0;
                    var stopwatch = Stopwatch.StartNew();

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        progressModel.StartFile(DateFolderOrganizer.ExtractFileName(file.Path), file.Size);

                        CopyResult result = await copier.CopyAsync(file, ct).ConfigureAwait(false);

                        if (result.Status == CopyStatus.Skipped)
                        {
                            progressModel.RecordSkippedBytes(file.Size);
                        }

                        progressModel.CompleteFile(result.Status);
                        reporter.OnFileCompleted(result);

                        string? relPath = result.RelativeDestPath;
                        if (string.IsNullOrWhiteSpace(relPath))
                        {
                            relPath = journal.GetDestRelativePath(file.Path, file.Size)
                                      ?? new DateFolderOrganizer().GetRelativeDestination(file, null, scheme, DeviceSubdir);
                        }

                        string fullDestPath = !string.IsNullOrWhiteSpace(relPath)
                            ? Path.Combine(DestinationPath, relPath)
                            : string.Empty;

                        var detailItem = new TransferItemDetail
                        {
                            SourcePath = file.Path,
                            TargetPath = relPath ?? string.Empty,
                            FullPath = fullDestPath,
                            FileSizeText = ByteSize.Humanize(file.Size),
                            StatusText = result.Status.ToString(),
                            Details = result.Error ?? (result.Status == CopyStatus.Skipped ? App.GetString("MsgStatusSkipped", "已存在 / 增量跳过") : App.GetString("MsgStatusSuccess", "成功"))
                        };

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            switch (result.Status)
                            {
                                case CopyStatus.Copied:
                                    copied++;
                                    bytesCopied += result.BytesCopied;
                                    copiedDetails.Add(detailItem);
                                    if (backupSessionId > 0 && !string.IsNullOrWhiteSpace(detailItem.TargetPath))
                                    {
                                        journal.RecordHistoryItem(backupSessionId, file.Path, detailItem.TargetPath, file.Size, DateTimeOffset.UtcNow, Path.GetExtension(file.Path).TrimStart('.'));
                                    }
                                    break;
                                case CopyStatus.Skipped:
                                    skipped++;
                                    skippedDetails.Add(detailItem);
                                    break;
                                case CopyStatus.Failed:
                                    failed++;
                                    failedDetails.Add(detailItem);
                                    break;
                            }

                            CopiedCount = copied;
                            SkippedCount = skipped;
                            FailedCount = failed;
                        });
                    }

                    stopwatch.Stop();
                    if (backupSessionId > 0)
                    {
                        journal.CompleteBackupSession(backupSessionId, copied, bytesCopied, failed > 0 ? "Partial" : "Completed");
                    }
                    journal.RecordRun(runStartedAt, DateTimeOffset.UtcNow, "copy", copied, skipped, failed, exitCode: 0, client.Device?.Udid);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressPercentage = 100;
                        ProgressText = progressSticky.Set("FmtAndroidBackupDone", "🎉 增量备份完成: {0} 已传输, {1} 增量跳过, {2} 失败", copied, skipped, failed);
                        SetFileStatus("AndroidEtaDone", "已完成");
                        SpeedText = "0.0 MB/s";
                        SetEtaKey("AndroidEtaDone", "已完成");
                        AddLog($"[SUMMARY] 备份完成！传输: {copied} 项 ({ByteSize.Humanize(bytesCopied)})，跳过: {skipped} 项，耗时: {stopwatch.Elapsed:mm\\:ss}");
                    });
                }
                finally
                {
                    foreach (var fj in fallbackJournals)
                    {
                        fj.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressText = progressSticky.Set("AndroidBackupCancelled", "⚠️ 备份已由用户取消。");
                    SetFileStatus("StatusCancelledShort", "已取消");
                    AddLog("[WARN] 备份过程被用户取消。");
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressText = progressSticky.Set("FmtAndroidBackupError", "❌ 备份出错: {0}", ex.Message);
                    SetFileStatus("StatusAborted", "异常中断");
                    AddLog($"[ERROR] 备份异常: {ex.Message}");
                });
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsTransferring = false;
                });
            }
        }, ct);
    }

    private void UpdateSnapshotUI(ProgressSnapshot snapshot)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ProgressPercentage = snapshot.ByteFraction * 100;
            CopiedCount = snapshot.CopiedFiles;
            SkippedCount = snapshot.SkippedFiles;
            FailedCount = snapshot.FailedFiles;
            RemainingFilesCount = Math.Max(0, snapshot.TotalFiles - snapshot.ProcessedFiles);
            TotalFilesCount = snapshot.TotalFiles;
            TransferredSizeText = $"{ByteSize.Humanize(snapshot.ProcessedBytes)} / {ByteSize.Humanize(snapshot.TotalBytes)}";
            if (snapshot.Eta.HasValue)
            {
                SetEtaSpan(snapshot.Eta.Value);
            }
            else if (snapshot.ByteFraction >= 1.0)
            {
                SetEtaKey("MsgBackupCompleted", "备份完成");
            }
            else
            {
                SetEtaKey("MsgCalculating", "计算中...");
            }
            int items = PlannedItemCount > 0 ? PlannedItemCount : snapshot.TotalFiles;
            ProgressText = progressSticky.Set(
                "FmtBackupProgressShort",
                "{0:F1}% · {1:N0} 张（文件 {2:N0}/{3:N0}）",
                snapshot.ByteFraction * 100,
                items,
                snapshot.ProcessedFiles,
                snapshot.TotalFiles);

            SpeedBytesPerSecond = snapshot.CurrentBytesPerSecond;
            double mbps = snapshot.CurrentBytesPerSecond / 1024d / 1024d;
            SpeedText = $"{mbps:F1} MB/s";
            if (string.IsNullOrWhiteSpace(snapshot.CurrentFileName))
            {
                SetFileStatus("StatusInProgress", "进行中...");
            }
            else
            {
                fileStatusKey = null;
                CurrentFileName = snapshot.CurrentFileName;
            }
        });
    }

    private void AddLog(string line)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
        if (Application.Current != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Add(entry);
                if (LogEntries.Count > 1000) LogEntries.RemoveAt(0);
            });
        }
    }

    private static OrganizeScheme ParseSchemeToken(string token) => token switch
    {
        "month (YYYY-MM)" => OrganizeScheme.Month,
        "year-month (YYYY\\YYYY-MM)" => OrganizeScheme.YearMonth,
        "year (YYYY)" => OrganizeScheme.Year,
        "flat" => OrganizeScheme.Flat,
        _ => OrganizeScheme.Month
    };
}
