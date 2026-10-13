using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
using PicHarbor.Core.Search;
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
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private string detectedDeviceModel = "Pixel 8";

    /// <summary>
    /// 归档文件夹名。<b>首次备份时确定后不可更改</b>——它参与 manifest 的相对路径，
    /// 改动会让已有文件与新备份文件落进不同目录。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullDestinationPreview))]
    [NotifyPropertyChangedFor(nameof(DestinationSubtitle))]
    private string deviceSubdir = string.Empty;

    /// <summary>归档文件夹名是否已锁定（已写入配置）。</summary>
    private bool archiveFolderLocked;

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
    private void ResetDeviceSubdir()
    {
        // 已锁定（首次备份已确定）时不允许再改，否则文件会散到两个目录。
        if (archiveFolderLocked) return;
        DeviceSubdir = DetectedDeviceModel;
    }

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

        // 归档文件夹名一旦锁定就不再跟随显示名（已有文件在旧目录里）。
        if (!archiveFolderLocked)
        {
            DeviceSubdir = sanitized;
        }
    }

    /// <summary>
    /// 确保归档文件夹名已确定。
    /// </summary>
    /// <remarks>
    /// 判据顺序：<b>先按设备身份查归档库</b>（归档目录名与设备记录同生共死，是唯一权威），
    /// 库里没有才问用户。这样老归档不会因配置缺字段被重复询问，也不会把照片写进新目录。
    /// </remarks>
    private async Task<bool> EnsureArchiveFolderAsync()
    {
        if (archiveFolderLocked && !string.IsNullOrWhiteSpace(DeviceSubdir))
        {
            return true;
        }

        string uid = CurrentDeviceUid;
        bool archiveReady = !string.IsNullOrWhiteSpace(DestinationPath) && Directory.Exists(DestinationPath);

        if (archiveReady)
        {
            try
            {
                // ① 库里有明确记录 → 直接采用，不打扰用户。
                string? stored = await ArchiveRepository
                    .GetDeviceArchiveFolderAsync(DestinationPath, uid)
                    .ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(stored))
                {
                    DeviceSubdir = stored;
                    archiveFolderLocked = true;
                    AddLog($"[INFO] 归档文件夹沿用既有记录：{DeviceSubdir}");
                    return true;
                }

                // ② 已知设备、但库里没记归档目录名（升级上来的老记录）：
                //    旧版本用显示名当目录名，且目录确实存在 → 采用并回写库。
                string? storedName = await ArchiveRepository
                    .GetDeviceNameAsync(DestinationPath, uid)
                    .ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(storedName))
                {
                    string candidate = LibraryStorageService.SanitizeDeviceFolderName(storedName);
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        Directory.Exists(Path.Combine(DestinationPath, candidate)))
                    {
                        DeviceSubdir = candidate;
                        archiveFolderLocked = true;
                        await ArchiveRepository
                            .SetDeviceArchiveFolderAsync(DestinationPath, uid, candidate)
                            .ConfigureAwait(true);
                        AddLog($"[INFO] 归档文件夹沿用既有目录：{DeviceSubdir}");
                        return true;
                    }
                }

                // ③ 显示名对不上（用户改过显示名）→ 从已备份文件的相对路径推导真实归档目录名。
                string? inferred = await ArchiveRepository
                    .InferDeviceArchiveFolderFromHistoryAsync(DestinationPath, uid)
                    .ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(inferred))
                {
                    string inferredFolder = LibraryStorageService.SanitizeDeviceFolderName(inferred);
                    if (!string.IsNullOrWhiteSpace(inferredFolder) &&
                        Directory.Exists(Path.Combine(DestinationPath, inferredFolder)))
                    {
                        DeviceSubdir = inferredFolder;
                        archiveFolderLocked = true;
                        await ArchiveRepository
                            .SetDeviceArchiveFolderAsync(DestinationPath, uid, inferredFolder)
                            .ConfigureAwait(true);
                        AddLog($"[INFO] 归档文件夹由备份历史推导：{DeviceSubdir}");
                        return true;
                    }
                }
            }
            catch
            {
                // 读库失败时退回到询问用户。
            }
        }

        // ③ 库里查不到这台设备 → 真正的新设备，弹框确认（唯一一次机会）。
        string suggested = string.IsNullOrWhiteSpace(DeviceSubdir)
            ? (string.IsNullOrWhiteSpace(DetectedDeviceModel) ? "Android" : DetectedDeviceModel)
            : DeviceSubdir;

        var dialog = new ArchiveFolderDialog(suggested, name => ValidateArchiveFolderNameAsync(name, uid))
        {
            Owner = Application.Current?.MainWindow
        };

        dialog.ShowDialog();
        if (!dialog.Confirmed)
        {
            return false;
        }

        DeviceSubdir = LibraryStorageService.SanitizeDeviceFolderName(dialog.FolderName);
        archiveFolderLocked = true;
        SaveConfig();

        if (archiveReady)
        {
            try
            {
                await ArchiveRepository
                    .SetDeviceArchiveFolderAsync(DestinationPath, uid, DeviceSubdir)
                    .ConfigureAwait(true);
            }
            catch
            {
                // 写库失败不影响本次备份；下次会走 ② 兜底。
            }
        }

        AddLog($"[INFO] 归档文件夹已确定为 {DeviceSubdir}，之后不可更改。");
        return true;
    }

    /// <summary>归档文件夹名校验：与<b>其它</b>设备已占用的名字冲突时拒绝（按设备身份排除自己）。</summary>
    private async Task<string?> ValidateArchiveFolderNameAsync(string name, string uid)
    {
        string sanitized = LibraryStorageService.SanitizeDeviceFolderName(name);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return App.GetString("ArchiveFolderInvalid", "该名称不可用作文件夹名，请换一个。");
        }

        if (string.IsNullOrWhiteSpace(DestinationPath) || !Directory.Exists(DestinationPath))
        {
            return null;
        }

        try
        {
            bool taken = await ArchiveRepository
                .IsDeviceArchiveFolderTakenByOtherAsync(DestinationPath, sanitized, uid)
                .ConfigureAwait(true);

            return taken
                ? string.Format(App.GetString("FmtArchiveFolderTaken", "名称「{0}」已被另一台设备使用，请换一个。"), sanitized)
                : null;
        }
        catch
        {
            return null;
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
    private string androidDeviceId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private string connectionStatusText = "未测试连接";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroPillText))]
    private bool isConnectionOk = false;

    [ObservableProperty]
    private bool isTestingConnection = false;

    /// <summary>设备标识区域的状态 / 指引文案（为空则不显示该行）。</summary>
    [ObservableProperty]
    private string deviceIdentityStatusText = "";

    /// <summary>设备标识状态是否为警示（识别失败 / 需人工核对）而非成功。</summary>
    [ObservableProperty]
    private bool isDeviceIdentityWarning = false;

    /// <summary>
    /// 设备标识是否已确认（自动识别成功或用户手工填写）。
    /// 仅用于持久化，避免下次进入时重复打扰。
    /// </summary>
    private bool deviceIdConfirmed = false;

    /// <summary>加载配置期间抑制设备标识状态刷新，避免启动时弹出「已手动填写」提示。</summary>
    private bool suppressDeviceIdStatus = false;

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
            string model = DetectedDeviceModel;
            return IsConnectionOk
                ? string.IsNullOrWhiteSpace(model)
                    ? App.GetString("AndroidPillConnected", "已连接")
                    : string.Format(App.GetString("FmtAndroidPillConnected", "已连接 {0}"), model)
                : App.GetString("AndroidPillNotConnected", "未连接或参数错误");
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
        suppressDeviceIdStatus = true;
        try
        {
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

            if (!string.IsNullOrWhiteSpace(config.AndroidBackupDeviceName))
            {
                DetectedDeviceModel = config.AndroidBackupDeviceName;
            }
            // 归档文件夹名由归档库持有，首次备份前通过 EnsureArchiveFolderAsync 确定。

            AndroidDeviceId = config.AndroidBackupDeviceId;
            deviceIdConfirmed = config.AndroidBackupDeviceIdConfirmed;
            MinFileSizeKb = config.AndroidBackupMinFileSizeKb;
            IgnoreSmallImages = config.AndroidBackupIgnoreSmallImages;
            IncludePhotos = config.AndroidBackupIncludePhotos;
            IncludeVideos = config.AndroidBackupIncludeVideos;
        }
        finally
        {
            suppressDeviceIdStatus = false;
        }
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
        config.AndroidBackupDeviceName = DetectedDeviceModel;
        config.AndroidBackupDeviceId = AndroidDeviceId;
        config.AndroidBackupDeviceIdConfirmed = deviceIdConfirmed;
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

    // ── 自动连接检测：FTP 配置变化后防抖 800ms 自动测试（手动按钮保留，随时可重测）──
    private DispatcherTimer? autoConnTestTimer;

    private void ScheduleAutoConnectionTest()
    {
        if (autoConnTestTimer is null)
        {
            autoConnTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            autoConnTestTimer.Tick += AutoConnTestTimer_Tick;
        }

        autoConnTestTimer.Stop();
        autoConnTestTimer.Start();
    }

    private void AutoConnTestTimer_Tick(object? sender, EventArgs e)
    {
        autoConnTestTimer!.Stop();
        if (IsTransferring || IsTestingConnection)
        {
            return;
        }

        TestConnectionCommand.Execute(null);
    }

    partial void OnAndroidFtpHostChanged(string value) => ScheduleAutoConnectionTest();

    partial void OnAndroidFtpPortChanged(int value) => ScheduleAutoConnectionTest();

    partial void OnAndroidFtpUserChanged(string value) => ScheduleAutoConnectionTest();

    partial void OnAndroidFtpPasswordChanged(string value) => ScheduleAutoConnectionTest();

    /// <summary>把配对成功的设备标识落到本地配置与归档库（设备端写入由配对对话框完成）。</summary>
    private async Task ApplyDeviceIdentityAsync(string serial)
    {
        string oldUid = BuildCurrentDeviceUid();
        string newUid = $"android:{serial}";
        string displayOld = string.IsNullOrWhiteSpace(AndroidDeviceId) ? AndroidFtpHost : AndroidDeviceId;

        AndroidDeviceId = serial;
        deviceIdConfirmed = true;
        SaveConfig();

        bool migrated = false;
        if (!string.IsNullOrWhiteSpace(DestinationPath) && Directory.Exists(DestinationPath))
        {
            try
            {
                if (!string.Equals(oldUid, newUid, StringComparison.OrdinalIgnoreCase))
                {
                    migrated = await ArchiveRepository
                        .MigrateDeviceUdidAsync(DestinationPath, oldUid, newUid)
                        .ConfigureAwait(true);
                }

                // 迁移只改 udid；hardware_serial 列必须一并同步，
                // 否则设备历史弹窗仍把旧值判为占位值、显示「未识别」，与「识别成功」自相矛盾。
                await ArchiveRepository
                    .UpdateDeviceHardwareSerialAsync(DestinationPath, newUid, serial)
                    .ConfigureAwait(true);
            }
            catch
            {
                // 归档库写入失败不影响标识本身的保存。
            }
        }

        SetDeviceIdentityStatus("FmtAndroidIdSuccess", "已绑定设备标识：{0}", isWarning: false, serial);
        AddLog($"[INFO] Android 设备标识已设为 {serial}（原：{displayOld}）{(migrated ? "，既有备份历史已迁移至新标识" : string.Empty)}");

        // 用码查归档库：命中就把该设备的显示名填回来，避免用户重新起名。
        await TryFillDisplayNameAsync(serial).ConfigureAwait(true);
    }

    /// <summary>用唯一码查归档库，命中就把该设备的显示名填回界面。</summary>
    private async Task TryFillDisplayNameAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.IsNullOrWhiteSpace(DestinationPath) ||
            !Directory.Exists(DestinationPath))
        {
            return;
        }

        try
        {
            string? storedName = await ArchiveRepository
                .GetDeviceNameAsync(DestinationPath, $"android:{deviceId}")
                .ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(storedName))
            {
                DetectedDeviceModel = storedName;
                SaveConfig();
            }
        }
        catch
        {
            // 查不到不影响绑定结果。
        }
    }

    /// <summary>
    /// 读取手机端已有的唯一码并填入「设备标识」。
    /// </summary>
    /// <param name="silent">静默模式：失败不提示（用于打开弹窗时的自动尝试）。</param>
    public async Task ReadDeviceIdentityAsync(bool silent = false)
    {
        if (IsTransferring || string.IsNullOrWhiteSpace(AndroidFtpHost))
        {
            return;
        }

        if (!silent)
        {
            SetDeviceIdentityStatus("AndroidIdReading", "正在读取手机端标识...", isWarning: false);
        }

        try
        {
            using var ftp = new SimpleFtpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await ftp.ConnectAsync(AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, cts.Token).ConfigureAwait(true);

            DeviceIdentityResult identity = await AndroidDeviceDetector
                .ResolveIdentityAsync(ftp, AndroidRemoteLayout.RootDirectory, AndroidDeviceId, cts.Token)
                .ConfigureAwait(true);

            string baseline = AndroidDeviceId;
            string remoteId = identity.RemoteId ?? string.Empty;

            switch (identity.Match)
            {
                case DeviceIdentityMatch.Matched:
                    SetDeviceIdentityStatus("FmtAndroidIdReadOk", "已读取手机端标识：{0}", isWarning: false, remoteId);
                    await TryFillDisplayNameAsync(remoteId).ConfigureAwait(true);
                    break;

                case DeviceIdentityMatch.NoBaseline when !string.IsNullOrWhiteSpace(remoteId):
                    // 本机还没有记录：直接采用手机端的码。
                    AndroidDeviceId = remoteId;
                    deviceIdConfirmed = true;
                    SaveConfig();
                    await TryFillDisplayNameAsync(remoteId).ConfigureAwait(true);
                    SetDeviceIdentityStatus("FmtAndroidIdReadAdopted", "已读取并采用手机端标识：{0}", isWarning: false, remoteId);
                    break;

                case DeviceIdentityMatch.Mismatched:
                    SetDeviceIdentityStatus(
                        "FmtAndroidIdReadMismatch",
                        "手机端标识 {0} 与本机记录 {1} 不一致，请确认是否连错了设备。",
                        isWarning: true,
                        remoteId,
                        baseline);
                    break;

                default:
                    SetDeviceIdentityStatus("AndroidIdReadMissing", "手机端没有识别文件，需要先配对。", isWarning: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                SetDeviceIdentityStatus("FmtAndroidIdReadFailed", "读取手机端标识失败：{0}", isWarning: true, ex.Message);
            }
        }
    }

    /// <summary>
    /// 生成一个新唯一码并配对到当前手机：生成码 → 打开配对对话框（分步反馈 + 回读验证 + 覆盖保护）
    /// → 配对成功后落库。
    /// </summary>
    /// <remarks>
    /// 若当前<b>已有</b>设备标识，这个动作会换码（覆盖手机上的现有标识），必须先二次确认——
    /// 否则用户想"连接这台设备"却误点此按钮，会导致设备历史与新码脱钩。
    /// </remarks>
    [RelayCommand]
    private async Task GenerateAndBindDeviceIdAsync()
    {
        string current = (AndroidDeviceId ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(current) &&
            !await ConfirmReplaceIdentityAsync(current).ConfigureAwait(true))
        {
            return;
        }

        string generated = DeviceSerialGenerator.CreateInternalId();

        if (!RunPairing(generated))
        {
            SetDeviceIdentityStatus("AndroidIdPairNotCompleted", "配对未完成，设备标识未改动。", isWarning: true);
            return;
        }

        await ApplyDeviceIdentityAsync(generated).ConfigureAwait(true);
    }

    /// <summary>
    /// 换码前的二次确认。若该标识已在归档库中登记，明确告知它属于哪台设备、有多少历史批次，
    /// 以及换码会让历史脱钩；并提示"只是想连接这台设备的话不必换码"。
    /// </summary>
    private async Task<bool> ConfirmReplaceIdentityAsync(string current)
    {
        string? knownName = null;
        int sessions = 0;

        if (!string.IsNullOrWhiteSpace(DestinationPath) && Directory.Exists(DestinationPath))
        {
            try
            {
                string uid = $"android:{current}";
                knownName = await ArchiveRepository.GetDeviceNameAsync(DestinationPath, uid).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(knownName))
                {
                    var summary = await ArchiveRepository
                        .GetDeviceBackupSummaryAsync(DestinationPath, uid)
                        .ConfigureAwait(true);
                    sessions = summary.TotalSessions;
                }
            }
            catch
            {
                // 查不到就按未知设备处理。
            }
        }

        string message = string.IsNullOrWhiteSpace(knownName)
            ? string.Format(
                App.GetString(
                    "FmtAndroidIdReplaceUnknown",
                    "当前设备标识：{0}\n\n「生成并绑定」会创建一个【新的】唯一码并覆盖手机上的现有标识。\n\n确定继续吗？"),
                current)
            : string.Format(
                App.GetString(
                    "FmtAndroidIdReplaceKnown",
                    "当前设备标识：{0}\n该标识已在归档库中登记为设备「{1}」，有 {2} 个备份批次。\n\n「生成并绑定」会创建一个【新的】唯一码并覆盖手机上的现有标识，该设备的历史记录将与新码脱钩。\n\n如果只是想连接这台设备，请直接点「完成」——当前标识已经正确。\n\n确定要换码吗？"),
                current,
                knownName,
                sessions);

        return MessageBox.Show(
            message,
            App.GetString("MsgBoxTitle", "提示"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>打开配对对话框，返回是否达成配对。</summary>
    private bool RunPairing(string targetDeviceId)
    {
        var dialog = new AndroidPairingDialog(
            DetectedDeviceModel,
            targetDeviceId,
            AndroidFtpHost,
            AndroidFtpPort,
            AndroidFtpUser,
            AndroidFtpPassword)
        {
            Owner = Application.Current?.MainWindow
        };

        dialog.ShowDialog();
        return dialog.IsPaired;
    }

    /// <summary>输入框内容被手动修改即视为已确认。</summary>
    partial void OnAndroidDeviceIdChanged(string value)
    {
        if (suppressDeviceIdStatus || string.IsNullOrWhiteSpace(value)) return;

        deviceIdConfirmed = true;
        SaveConfig();
        SetDeviceIdentityStatus("AndroidIdManualAccepted", "已使用手动填写的设备标识。", isWarning: false);
    }

    /// <summary>与 FtpAndroidBackupClient 的 Device.Udid 保持一致。</summary>
    private string BuildCurrentDeviceUid() =>
        $"android:{(string.IsNullOrWhiteSpace(AndroidDeviceId) ? AndroidFtpHost : AndroidDeviceId)}";

    /// <summary>当前设备在归档库中的 udid。</summary>
    public string CurrentDeviceUid => BuildCurrentDeviceUid();

    private void SetDeviceIdentityStatus(string key, string fallback, bool isWarning, params object[] args)
    {
        IsDeviceIdentityWarning = isWarning;
        DeviceIdentityStatusText = args.Length == 0
            ? App.GetString(key, fallback)
            : string.Format(App.GetString(key, fallback), args);
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

            // 连接成功顺带读一次手机端标识，设备标识区域会给出核对结果。
            await ReadDeviceIdentityAsync().ConfigureAwait(true);
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

        if (!await EnsureArchiveFolderAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DestinationPath);
            if (!await PicHarbor.Gui.Util.BackupPreferences.EnsureArchiveProtectionAsync(DestinationPath))
            { AddLog(App.GetString("LibraryProtectionUnavailable","无法确认图库保护状态，请先解锁或修复应用锁。"));return; }
        }
        catch(Exception ex) { AddLog(ex.Message);return; }

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
                    deviceName: DetectedDeviceModel);

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
                journal.BindDevice(client.Device?.Udid);
                DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;

                long backupSessionId = 0;
                if (client.Device is not null)
                {
                    // hardware_serial 必须是「码」本身（如 PH-KAZ565GJ），而不是 udid（android:PH-...）——
                    // 否则设备历史弹窗会把它判成占位值、显示「未识别」。
                    string deviceSerial = client.Device.Udid.StartsWith("android:", StringComparison.OrdinalIgnoreCase)
                        ? client.Device.Udid["android:".Length..]
                        : client.Device.Udid;
                    string effectiveName = journal.RegisterOrUpdateDevice(client.Device.Udid, client.Device.Name, client.Device.ProductType, deviceSerial, "Android", runStartedAt);
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
                        fallbackJournals: fallbackJournals,
                        strictVerification: PicHarbor.Gui.Config.AppSettings.Load().StrictBackupVerification,
                        rebackupDeleted: PicHarbor.Gui.Config.AppSettings.Load().RebackupDeletedPhotos,
                        deletedPhotoDecision: PicHarbor.Gui.Util.BackupPreferences.DecideDeletedPhoto);

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
                    if(File.Exists(TransferJournal.ResolveDatabasePath(DestinationPath)))
                    {
                        try { var catalog=new LibraryCatalog(DestinationPath);catalog.ReadMedia();catalog.Save(); }
                        catch(Exception ex) { AddLog("[LIBRARY] "+ex.Message); }
                    }
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
