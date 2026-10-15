using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Android;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Scope;
using PicHarbor.Core.Transfer;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public partial class AndroidSyncViewModel : ObservableObject
{
    private CancellationTokenSource? cts;

    [ObservableProperty]
    private string archivePath = "";


    [ObservableProperty]
    private string androidFtpHost = "192.168.1.100";

    [ObservableProperty]
    private int androidFtpPort = 2121;

    [ObservableProperty]
    private string androidFtpUser = "anonymous";

    [ObservableProperty]
    private string androidFtpPassword = "";

    /// <summary>安卓同步目标目录。<b>已固定为 <see cref="AndroidRemoteLayout.RootDirectory"/>，UI 只读。</b></summary>
    [ObservableProperty]
    private string androidTargetDir = AndroidRemoteLayout.RootDirectory;

    [ObservableProperty]
    private string androidDeviceId = "";

    [ObservableProperty]
    private AndroidRestoreMode restoreMode = AndroidRestoreMode.Default;

    public bool IsDefaultRestoreMode
    {
        get => RestoreMode == AndroidRestoreMode.Default;
        set
        {
            if (value && RestoreMode != AndroidRestoreMode.Default)
            {
                RestoreMode = AndroidRestoreMode.Default;
                OnPropertyChanged(nameof(IsDefaultRestoreMode));
                OnPropertyChanged(nameof(IsHistoricalIncrementalMode));
            }
        }
    }

    public bool IsHistoricalIncrementalMode
    {
        get => RestoreMode == AndroidRestoreMode.HistoricalIncremental;
        set
        {
            if (value && RestoreMode != AndroidRestoreMode.HistoricalIncremental)
            {
                RestoreMode = AndroidRestoreMode.HistoricalIncremental;
                OnPropertyChanged(nameof(IsDefaultRestoreMode));
                OnPropertyChanged(nameof(IsHistoricalIncrementalMode));
            }
        }
    }

    // --- Restore Scope Properties ---
    [ObservableProperty]
    private IPhoneRestoreScopeMode scopeMode = IPhoneRestoreScopeMode.All;

    public bool IsScopeAll
    {
        get => ScopeMode == IPhoneRestoreScopeMode.All;
        set
        {
            if (value && ScopeMode != IPhoneRestoreScopeMode.All)
            {
                ScopeMode = IPhoneRestoreScopeMode.All;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    public bool IsScopeManualSelection
    {
        get => ScopeMode == IPhoneRestoreScopeMode.ManualSelection;
        set
        {
            if (value && ScopeMode != IPhoneRestoreScopeMode.ManualSelection)
            {
                ScopeMode = IPhoneRestoreScopeMode.ManualSelection;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    private void NotifyScopeProperties()
    {
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeManualSelection));
    }

    [ObservableProperty]
    private DateTime? scopeDateFrom;

    [ObservableProperty]
    private DateTime? scopeDateTo;

    [ObservableProperty]
    private string scopeDateFromText = "";

    [ObservableProperty]
    private string scopeDateToText = "";

    partial void OnScopeDateFromChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (scopeDateFromText != formatted)
        {
            ScopeDateFromText = formatted;
        }
        RecalculateScopeSummary();
    }

    partial void OnScopeDateToChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (scopeDateToText != formatted)
        {
            ScopeDateToText = formatted;
        }
        RecalculateScopeSummary();
    }

    partial void OnScopeDateFromTextChanged(string value)
    {
        if (DateTime.TryParse(value, out var dt))
        {
            if (scopeDateFrom != dt.Date) scopeDateFrom = dt.Date;
        }
        else if (string.IsNullOrWhiteSpace(value) && scopeDateFrom != null)
        {
            scopeDateFrom = null;
        }
        RecalculateScopeSummary();
    }

    partial void OnScopeDateToTextChanged(string value)
    {
        if (DateTime.TryParse(value, out var dt))
        {
            if (scopeDateTo != dt.Date) scopeDateTo = dt.Date;
        }
        else if (string.IsNullOrWhiteSpace(value) && scopeDateTo != null)
        {
            scopeDateTo = null;
        }
        RecalculateScopeSummary();
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
        ScopeDateFromText = "";
        ScopeDateToText = "";
    }

    [ObservableProperty]
    private string scopeSummaryText = "📊 当前筛选结果: 预计恢复 0 项 (照片 0，视频 0)，约 0 B";

    [ObservableProperty]
    private int manualSelectionCount = 0;

    [ObservableProperty]
    private string manualSelectionCountText = "已选择 0 项媒体";

    [ObservableProperty]
    private string manualSelectionModalBtnText = "👁️ 查看/编辑清单 (0)";

    public bool HasManualSelections => ManualSelectionCount > 0;
    public bool HasNoManualSelections => ManualSelectionCount == 0;

    [ObservableProperty]
    private bool isManualModalOpen = false;

    /// <summary>Multi-level subfolder tree for the unified scope filter (checked set = active subfolder filter).</summary>
    public ObservableCollection<SubfolderNodeViewModel> FolderTree { get; } = new();

    /// <summary>Indicates whether any node in the folder tree is checked (= subfolder filter enabled).</summary>
    public bool HasSubfolderFilter => FolderTree.Any(HasCheckedDescendant);

    private ScopeSelectionPersistence? selectionPersist;

    /// <summary>Debounced journal persistence of the subfolder tree selection (kind: android_sync).</summary>
    private ScopeSelectionPersistence SelectionPersist => selectionPersist ??= new ScopeSelectionPersistence(
        () => ArchivePath,
        () => "android_sync",
        () => CurrentDeviceId,
        () => FolderTree);

    private static bool HasCheckedDescendant(SubfolderNodeViewModel node) =>
        node.IsChecked == true || node.Children.Any(HasCheckedDescendant);

    /// <summary>Inline policy for entries without capture date while the date filter is active (default: include).</summary>
    [ObservableProperty]
    private bool includeUndatedFiles = true;

    partial void OnIncludeUndatedFilesChanged(bool value) => RecalculateScopeSummary();

    /// <summary>Count of entries passing other conditions but lacking any capture date (only tracked while the date filter is active).</summary>
    [ObservableProperty]
    private int undatedCandidateCount = 0;

    partial void OnUndatedCandidateCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsUndatedHintVisible));
        OnPropertyChanged(nameof(UndatedHintText));
    }

    /// <summary>Indicates whether the inline undated-files hint row should be shown.</summary>
    public bool IsUndatedHintVisible => UndatedCandidateCount > 0;

    /// <summary>Formatted inline hint text for entries without capture date (refreshed with language).</summary>
    public string UndatedHintText => string.Format(App.GetString("ScopeUndatedHint", "发现 {0} 项无日期文件"), UndatedCandidateCount);

    /// <summary>Builds the unified combination filter from the folder tree, date range and undated policy.</summary>
    private ScopeFilterSet BuildScopeFilters(DateTime? dateFrom, DateTime? dateTo)
    {
        return new ScopeFilterSet
        {
            SelectedSubfolders = SubfolderTreeBuilder.CollectSelected(FolderTree),
            DateFrom = dateFrom,
            DateTo = dateTo,
            UndatedPolicy = IncludeUndatedFiles ? UndatedFilePolicy.Include : UndatedFilePolicy.Skip
        };
    }
    public ObservableCollection<ManualSelectedItemViewModel> ManualSelectedItems { get; } = new();

    // --- End Restore Scope Properties ---

    [ObservableProperty]
    private string testStatus = App.GetString("AndroidConnUntested", "未测试连接");

    [ObservableProperty]
    private bool isSyncing = false;

    [ObservableProperty]
    private double progressValue = 0;

    [ObservableProperty]
    private string progressText = App.GetString("MsgReady", "准备就绪");
    private readonly StickyText progressSticky = new();
    private readonly StickyText ftpTestSticky = new();

    [ObservableProperty]
    private int copiedCount = 0;

    [ObservableProperty]
    private int remainingFilesCount = 0;

    [ObservableProperty]
    private string speedText = "--";

    public double SpeedBytesPerSecond { get; private set; }

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

    // Auto-Completion Suggestions State
    public ObservableCollection<MediaPairSuggestion> MissingPairSuggestions { get; } = new();

    [ObservableProperty]
    private bool hasMissingPairSuggestions = false;

    [ObservableProperty]
    private string missingPairCountText = "";

    // Preflight Intercept Modal State
    [ObservableProperty]
    private bool isPreflightModalOpen = false;

    [ObservableProperty]
    private string preflightTitle = "";

    [ObservableProperty]
    private string preflightMessage = "";

    private PreflightCheckResult? pendingPreflightResult;

    public AndroidSyncViewModel()
    {
        LoadConfig();
    }

    /// <summary>
    /// 当前设备的同步标识：与备份侧保持一致——优先唯一码，回落 FTP 主机。
    /// 用于给 <c>android_sync_records</c> / <c>android_manual_selections</c> 分区。
    /// </summary>
    private string CurrentDeviceId =>
        string.IsNullOrWhiteSpace(AndroidDeviceId) ? AndroidFtpHost : AndroidDeviceId;

    private void LoadConfig()
    {
        var config = AppSettings.Load();
        AndroidFtpHost = config.AndroidFtpHost;
        AndroidFtpPort = config.AndroidFtpPort;
        AndroidFtpUser = config.AndroidFtpUser;
        AndroidFtpPassword = config.AndroidFtpPassword;
        AndroidTargetDir = AndroidRemoteLayout.Normalize(config.AndroidTargetDir);

        // 设备标识统一使用备份页的字段（AndroidDeviceId 已退役）。
        suppressDeviceIdSave = true;
        try
        {
            AndroidDeviceId = config.AndroidBackupDeviceId;
        }
        finally
        {
            suppressDeviceIdSave = false;
        }
    }

    public void SaveConfig()
    {
        var config = AppSettings.Load();
        config.AndroidFtpHost = AndroidFtpHost;
        config.AndroidFtpPort = AndroidFtpPort;
        config.AndroidFtpUser = AndroidFtpUser;
        config.AndroidFtpPassword = AndroidFtpPassword;
        config.AndroidTargetDir = AndroidRemoteLayout.RootDirectory;
        config.AndroidBackupDeviceId = AndroidDeviceId;
        AppSettings.Save(config);
    }

    /// <summary>加载配置期间抑制标识回写，避免启动时误标「已确认」。</summary>
    private bool suppressDeviceIdSave;

    partial void OnArchivePathChanged(string value)
    {
        LoadFolderTree();
        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();
    }

    partial void OnAndroidFtpHostChanged(string value)
    {
        SaveConfig();
        ScheduleAutoConnectionTest();
    }

    partial void OnAndroidFtpPortChanged(int value)
    {
        SaveConfig();
        ScheduleAutoConnectionTest();
    }

    partial void OnAndroidFtpUserChanged(string value)
    {
        SaveConfig();
        ScheduleAutoConnectionTest();
    }

    partial void OnAndroidFtpPasswordChanged(string value)
    {
        SaveConfig();
        ScheduleAutoConnectionTest();
    }

    partial void OnAndroidTargetDirChanged(string value)
    {
        // 目录已固定：任何偏离都收敛回常量，避免识别文件读写位置漂移。
        string normalized = AndroidRemoteLayout.Normalize(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            AndroidTargetDir = normalized;
            return;
        }

        SaveConfig();
        ScheduleAutoConnectionTest();
    }

    partial void OnAndroidDeviceIdChanged(string value)
    {
        if (suppressDeviceIdSave) return;

        SaveConfig();
        LoadManualSelectionsFromDb();
    }

    /// <summary>
    /// 目标设备身份与 PC 侧记录不一致、或设备端缺少识别文件而无法核对时的<b>阻塞确认</b>。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="AndroidSyncEngine.SyncAsync"/> 在后台线程调用，因此必须切回 UI 线程弹框。
    /// 之所以不静默继续：若文件实际传到 B 却记录成 A，下次回传给 A 会错误跳过这些文件，
    /// 而 A 手机上其实并没有——这是静默的数据不一致。
    /// </remarks>
    private bool ConfirmDeviceIdentity(DeviceIdentityResult identity)
    {
        bool confirmed = false;

        App.Current.Dispatcher.Invoke(() =>
        {
            string message = identity.Match == DeviceIdentityMatch.Mismatched
                ? string.Format(
                    App.GetString(
                        "FmtAndroidIdentityMismatch",
                        "目标设备标识与本地记录不一致。本机已确认：{0}；当前连到：{1}。可能是连错了 FTP 服务器，或这是一台不同的设备。继续将按【当前连到的设备】重新记录回传进度。是否继续？"),
                    identity.BaselineId,
                    identity.RemoteId ?? "-")
                : string.Format(
                    App.GetString(
                        "FmtAndroidIdentityUnverifiable",
                        "当前设备缺少识别文件，无法核对身份。本机已确认：{0}。可能是手机上的识别文件已被删除，或连到了另一台设备。继续将按本机已确认标识记录回传进度。是否继续？"),
                    identity.BaselineId);

            confirmed = MessageBox.Show(
                message,
                App.GetString("MsgBoxTitle", "提示"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        });

        return confirmed;
    }

    public HashSet<string> GetManualSelectionPathsSet()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            string devId = CurrentDeviceId;
            return journal.GetAndroidManualSelections(devId);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public Action? NavigateToSettingsAction { get; set; }

    [RelayCommand]
    private void OpenSettingsTab()
    {
        NavigateToSettingsAction?.Invoke();
    }

    public void LoadManualSelectionsFromDb()
    {
        ManualSelectedItems.Clear();
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            UpdateManualSelectionTexts();
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            string devId = CurrentDeviceId;
            var autofilledMap = journal.GetAndroidManualSelectionsWithAutofilled(devId);
            var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);
            var dict = PicHarbor.Core.Search.ArchiveRepository.BuildSelectionMap(manifest,ArchivePath);

            foreach (var (path, isAutofilled) in autofilledMap)
            {
                string normPath = path.Replace('\\', '/');
                if (dict.TryGetValue(normPath, out var entry))
                {
                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = normPath,
                        FullPath = PicHarbor.Core.Storage.ArchivePathResolver.Resolve(entry.ArchiveRoot ?? ArchivePath,entry.DestPath),
                        CapturedAt = entry.ExifDateTimeOriginalIso ?? entry.SourceMtimeIso ?? "N/A",
                        SizeBytes = entry.SizeBytes,
                        SizeText = FormatBytes(entry.SizeBytes),
                        IsAutofilled = isAutofilled
                    });
                }
                else
                {
                    string fullP = Path.Combine(ArchivePath, normPath);
                    long size = 0;
                    string sizeStr = "0 B";
                    string timeStr = "N/A";

                    if (File.Exists(fullP))
                    {
                        var fi = new FileInfo(fullP);
                        size = fi.Length;
                        sizeStr = FormatBytes(size);
                        timeStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                    }

                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = normPath,
                        FullPath = fullP,
                        CapturedAt = timeStr,
                        SizeBytes = size,
                        SizeText = sizeStr,
                        IsAutofilled = isAutofilled
                    });
                }
            }
        }
        catch
        {
        }

        UpdateManualSelectionTexts();
    }

    public void SyncWithUnifiedManualSelections(IEnumerable<ManualSelectedItemViewModel> items)
    {
        ManualSelectedItems.Clear();
        foreach (var item in items)
        {
            ManualSelectedItems.Add(item);
        }
        UpdateManualSelectionTexts();
        RecalculateScopeSummary();
    }

    public void UpdateManualSelectionTexts()
    {
        ManualSelectionCount = ManualSelectedItems.Count;
        OnPropertyChanged(nameof(HasManualSelections));
        OnPropertyChanged(nameof(HasNoManualSelections));
        ManualSelectionCountText = string.Format(App.GetString("ScopeManualCountText", "已选择 {0} 项媒体"), ManualSelectionCount);
        ManualSelectionModalBtnText = string.Format(App.GetString("ScopeViewEditListBtn", "👁️ 查看/编辑清单 ({0})"), ManualSelectionCount);
    }

    public void LoadFolderTree()
    {
        SelectionPersist.Flush();
        SelectionPersist.Invalidate();
        FolderTree.Clear();
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return;

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);
            var tree = SubfolderTreeBuilder.Build(manifest.Select(m => m.DestPath));
            SelectionPersist.RestoreInto(ArchivePath, tree);
            foreach (var root in tree)
            {
                root.OnCheckedChanged = () =>
                {
                    OnPropertyChanged(nameof(HasSubfolderFilter));
                    RecalculateScopeSummary();
                    SelectionPersist.ScheduleSave();
                };
                FolderTree.Add(root);
            }
            OnPropertyChanged(nameof(HasSubfolderFilter));
        }
        catch
        {
            SelectionPersist.Invalidate();
        }
    }

    public void RecalculateScopeSummary()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            ScopeSummaryText = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            UndatedCandidateCount = 0;
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);

            DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
            DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
            var manualPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
                ? ManualSelectedItems.Select(m => m.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            string devId = CurrentDeviceId;
            var config = new AndroidSyncConfig
            {
                ConfiguredDeviceId = devId,
                ScopeMode = ScopeMode,
                ManualSelectedPaths = manualPaths,
                Filters = BuildScopeFilters(dateFrom, dateTo)
            };
            var filters = config.Filters!;
            bool dateFilterActive = filters.HasDateFilter;
            int undated = 0;

            var filtered = manifest.Where(e =>
            {
                if (!config.IsEntryIncluded(e))
                {
                    return false;
                }

                if (dateFilterActive && ScopeFilterSet.ParseCaptureDate(e) is null)
                {
                    undated++;
                }

                return true;
            }).ToList();

            UndatedCandidateCount = undated;

            int photoCount = filtered.Count(e => IsPhotoExtension(Path.GetExtension(e.DestPath)));
            int videoCount = filtered.Count - photoCount;
            long totalBytes = filtered.Sum(e => e.SizeBytes);

            string template = App.GetString("ScopeSummaryText", "📊 当前筛选结果: 预计恢复 {0} 项 (照片 {1}，视频 {2})，约 {3}");
            ScopeSummaryText = string.Format(template, filtered.Count, photoCount, videoCount, FormatBytes(totalBytes));
        }
        catch
        {
            ScopeSummaryText = App.GetString("ScopeSummaryZero", "📊 当前筛选结果: 预计恢复 0 项");
        }
    }

    private static bool IsPhotoExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".heic" or ".png" or ".webp" or ".dng" or ".cr2" or ".nef" or ".arw";
    }

    [ObservableProperty]
    private string notificationMessage = "";

    public (int added, int ignored) ProcessPickedFiles(IEnumerable<string> rawPaths)
    {
        NotificationMessage = "";
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            NotificationMessage = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            return (0, 0);
        }

        int addedCount = 0;
        int ignoredCount = 0;
        string devId = CurrentDeviceId;

        try
        {
            using var journal = TransferJournal.Open(ArchivePath);
            foreach (string rawPath in rawPaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;

                if (!IsSubPathOf(ArchivePath, rawPath))
                {
                    ignoredCount++;
                    continue;
                }

                string relPath = GetRelativePath(ArchivePath, rawPath);
                if (!journal.IsFileArchivedAndDone(relPath))
                {
                    ignoredCount++;
                    continue;
                }

                if (journal.AddAndroidManualSelection(devId, relPath))
                {
                    addedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            NotificationMessage = string.Format(App.GetString("FmtProcessError", "❌ 过程出现错误: {0}"), ex.Message);
        }

        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();

        // Auto-completion if configured
        int autoAddedCount = 0;
        var appConfig = Config.AppSettings.Load();
        if (appConfig.AutoCompleteLivePhotoPair || appConfig.AutoCompleteAaeSidecar || appConfig.AutoCompleteRawJpg)
        {
            try
            {
                using var journal = TransferJournal.Open(ArchivePath);
                var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);
                var currentPaths = ManualSelectedItems.Select(m => m.RelativePath).ToList();
                var suggestions = MediaPairMatcher.FindMissingPairs(
                    currentPaths,
                    manifest,
                    appConfig.AutoCompleteLivePhotoPair,
                    appConfig.AutoCompleteAaeSidecar,
                    appConfig.AutoCompleteRawJpg);

                if (suggestions.Count > 0)
                {
                    var autoAdd = suggestions.Select(s => s.SelectionKey).ToList();
                    journal.BatchAddAndroidManualSelections(devId, autoAdd, isAutofilled: true);
                    autoAddedCount = autoAdd.Count;
                    LoadManualSelectionsFromDb();
                    RecalculateScopeSummary();
                }
            }
            catch { }
        }

        if (addedCount > 0 || ignoredCount > 0 || autoAddedCount > 0)
        {
            if (autoAddedCount > 0)
            {
                string sampleText = autoAddedCount == 1
                    ? App.GetString("FmtPairOne", "1 项配对")
                    : string.Format(App.GetString("FmtPairMany", "{0} 项配对"), autoAddedCount);
                NotificationMessage = string.Format(
                    App.GetString("MsgPickedWithAutoPairNotice", "✨ 成功挑选 {0} 项照片！已根据配置自动补全了 {1} 项 Live Photo/侧车配对文件（如 {2}）。在「查看/编辑清单」中已标注 [🪄 来自自动补全] 标记。"),
                    addedCount, autoAddedCount, sampleText);
            }
            else if (ignoredCount == 0)
            {
                NotificationMessage = string.Format(App.GetString("MsgPickedSuccess", "✅ 成功添加 {0} 项照片到挑选清单。"), addedCount);
            }
            else
            {
                NotificationMessage = string.Format(App.GetString("MsgPickedWithIgnored", "⚠️ 成功添加 {0} 项照片。（已自动忽略 {1} 项不在归档库中的文件）"), addedCount, ignoredCount);
            }
        }
        else if (string.IsNullOrEmpty(NotificationMessage))
        {
            NotificationMessage = App.GetString("MsgPickedAlreadyExists", "ℹ️ 选中的文件已存在于挑选清单中。");
        }

        CheckAutoPairSuggestions();
        return (addedCount, ignoredCount);
    }

    public void CheckAutoPairSuggestions()
    {
        MissingPairSuggestions.Clear();
        HasMissingPairSuggestions = false;
        MissingPairCountText = "";

        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath) || ManualSelectedItems.Count == 0)
            return;

        try
        {
            var config = Config.AppSettings.Load();
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);
            var currentPaths = ManualSelectedItems.Select(m => m.RelativePath).ToList();

            var suggestions = MediaPairMatcher.FindMissingPairs(
                currentPaths,
                manifest,
                config.AutoCompleteLivePhotoPair,
                config.AutoCompleteAaeSidecar,
                config.AutoCompleteRawJpg);

            foreach (var sug in suggestions)
            {
                MissingPairSuggestions.Add(sug);
            }

            HasMissingPairSuggestions = MissingPairSuggestions.Count > 0;
            if (HasMissingPairSuggestions)
            {
                MissingPairCountText = string.Format(App.GetString("ScopeAutoPairCountText", "🪄 一键补全缺失的配对文件 ({0}项)"), MissingPairSuggestions.Count);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void AutoFixMissingPairs()
    {
        if (MissingPairSuggestions.Count == 0 || string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return;

        try
        {
            using var journal = TransferJournal.Open(ArchivePath);
            string devId = CurrentDeviceId;
            var toAdd = MissingPairSuggestions.Select(s => s.SelectionKey).ToList();
            journal.BatchAddAndroidManualSelections(devId, toAdd, isAutofilled: true);
        }
        catch { }

        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();
        NotificationMessage = string.Format(App.GetString("MsgAutoPairSuccess", "✨ 智能补全成功！自动增加了 {0} 项 Live Photo/侧车配对文件。"), MissingPairSuggestions.Count);
        CheckAutoPairSuggestions();
    }

    [RelayCommand]
    private void PickFilesFromExplorer()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = App.GetString("TitlePickFilesFromExplorer", "从资源管理器挑选照片..."),
            Filter = App.GetString("FilterMediaFiles", "媒体文件|*.jpg;*.jpeg;*.heic;*.png;*.webp;*.mov;*.mp4;*.dng;*.cr2;*.nef;*.arw|所有文件|*.*")
        };

        if (!string.IsNullOrWhiteSpace(ArchivePath) && Directory.Exists(ArchivePath))
        {
            dialog.InitialDirectory = ArchivePath;
        }

        if (dialog.ShowDialog() == true)
        {
            ProcessPickedFiles(dialog.FileNames);
        }
    }

    [RelayCommand]
    private void PasteFilesFromClipboard()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>();
            ProcessPickedFiles(files);
        }
        else
        {
            NotificationMessage = App.GetString("MsgClipboardNoFiles", "⚠️ 剪贴板中没有可粘贴的文件路径。请在 Windows 资源管理器中复制 (Ctrl+C) 照片后重试。");
        }
    }

    private static bool IsSubPathOf(string rootPath, string candidatePath)
    {
        try
        {
            string fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullCandidate = Path.GetFullPath(candidatePath);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetRelativePath(string rootPath, string fullPath)
    {
        string rel = Path.GetRelativePath(rootPath, fullPath);
        return rel.Replace('\\', '/');
    }

    public Action? OpenManualModalAction { get; set; }

    [RelayCommand]
    private void OpenManualModal()
    {
        if (OpenManualModalAction != null)
        {
            OpenManualModalAction();
            return;
        }

        IsManualModalOpen = true;
        _ = LoadThumbnailsForManualItemsAsync();
    }

    [RelayCommand]
    private void CloseManualModal()
    {
        IsManualModalOpen = false;
    }

    [RelayCommand]
    private void RemoveManualItem(ManualSelectedItemViewModel item)
    {
        if (item is null) return;
        ManualSelectedItems.Remove(item);
        if (!string.IsNullOrWhiteSpace(ArchivePath) && Directory.Exists(ArchivePath))
        {
            try
            {
                using var journal = TransferJournal.Open(ArchivePath);
                string devId = CurrentDeviceId;
                journal.RemoveAndroidManualSelection(devId, item.RelativePath);
            }
            catch { }
        }
        UpdateManualSelectionTexts();
        RecalculateScopeSummary();
    }

    [RelayCommand]
    private void ClearAllManualSelections()
    {
        ManualSelectedItems.Clear();
        if (!string.IsNullOrWhiteSpace(ArchivePath) && Directory.Exists(ArchivePath))
        {
            try
            {
                using var journal = TransferJournal.Open(ArchivePath);
                string devId = CurrentDeviceId;
                journal.ClearAndroidManualSelections(devId);
            }
            catch { }
        }
        UpdateManualSelectionTexts();
        RecalculateScopeSummary();
    }

    private async Task LoadThumbnailsForManualItemsAsync()
    {
        var targets = ManualSelectedItems.Where(x => x.ThumbnailImage is null && File.Exists(x.FullPath)).ToList();
        foreach (ManualSelectedItemViewModel item in targets)
        {
            try
            {
                ImageSource? thumb = await SearchViewModel.CreateListThumbnailAsync(item.FullPath, CancellationToken.None)
                    .ConfigureAwait(false);
                if (thumb is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => item.ThumbnailImage = thumb);
                }
            }
            catch
            {
            }
        }
    }

    public void OnLanguageChanged()
    {
        if (progressSticky.HasValue)
        {
            ProgressText = progressSticky.Current;
        }

        if (ftpTestSticky.HasValue)
        {
            TestStatus = ftpTestSticky.Current;
        }
        else
        {
            TestStatus = App.GetString("AndroidConnUntested", "未测试连接");
        }

        UpdateManualSelectionTexts();
        OnPropertyChanged(nameof(UndatedHintText));
        RecalculateScopeSummary();
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
        if (IsSyncing)
        {
            return;
        }

        TestConnectionCommand.Execute(null);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestStatus = ftpTestSticky.Set("AndroidFtpTestRunning", "正在尝试连接 FTP...");
        AddLog($"[TEST] Connecting to FTP {AndroidFtpHost}:{AndroidFtpPort}...");
        try
        {
            bool ok = await SimpleFtpClient.TestConnectionAsync(
                AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, AndroidTargetDir);
            TestStatus = ok
                ? ftpTestSticky.Set("AndroidFtpTestOk", "✅ FTP 连接测试成功！")
                : ftpTestSticky.Set("AndroidFtpTestFailPlain", "❌ FTP 连接失败");
            AddLog($"[TEST SUCCESS] Successfully connected to FTP {AndroidFtpHost}:{AndroidFtpPort}. Target directory ready.");
        }
        catch (Exception ex)
        {
            TestStatus = ftpTestSticky.Set("FmtAndroidFtpTestFail", "❌ 连接失败: {0}", ex.Message);
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
        DetailModalTitle = App.GetString("AndroidFtpDetailCopied", "🤖 Android FTP 已传输 / 已复制文件明细 (Copied)");
        DetailItems = new ObservableCollection<TransferItemDetail>(copiedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowSkippedDetails()
    {
        DetailModalTitle = App.GetString("AndroidFtpDetailSkipped", "🤖 Android FTP 已跳过文件明细 (Skipped - 已存在/已同步)");
        DetailItems = new ObservableCollection<TransferItemDetail>(skippedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowFailedDetails()
    {
        DetailModalTitle = App.GetString("AndroidFtpDetailFailed", "⚠️ Android FTP 失败文件明细 (Failed)");
        DetailItems = new ObservableCollection<TransferItemDetail>(failedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void CloseDetailModal()
    {
        IsDetailModalOpen = false;
    }

    [RelayCommand]
    private void FixPreflightAndProceed()
    {
        IsPreflightModalOpen = false;
        if (pendingPreflightResult is not null)
        {
            try
            {
                using var journal = TransferJournal.Open(ArchivePath);
                var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);
                var currentPaths = ManualSelectedItems.Select(m => m.RelativePath).ToList();
                var config = Config.AppSettings.Load();

                var suggestions = MediaPairMatcher.FindMissingPairs(currentPaths, manifest, true, true, config.AutoCompleteRawJpg);
                if (suggestions.Count > 0)
                {
                    string devId = CurrentDeviceId;
                    journal.BatchAddAndroidManualSelections(devId, suggestions.Select(s => s.SelectionKey), isAutofilled: true);
                    LoadManualSelectionsFromDb();
                    RecalculateScopeSummary();
                }
            }
            catch { }
        }

        pendingPreflightResult = null;
        _ = ExecuteStartSyncInternalAsync();
    }

    [RelayCommand]
    private void IgnorePreflightAndProceed()
    {
        IsPreflightModalOpen = false;
        pendingPreflightResult = null;
        _ = ExecuteStartSyncInternalAsync();
    }

    [RelayCommand]
    private void CancelPreflightModal()
    {
        IsPreflightModalOpen = false;
        pendingPreflightResult = null;
        IsSyncing = false;
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        if (PicHarbor.Gui.Updates.UpdateWorkGate.IsInstalling) return;
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            TestStatus = ftpTestSticky.Set("AndroidArchiveMissing", "❌ PC 本地归档目录不存在！请先在主界面或设置中配置归档路径。");
            AddLog($"[ERROR] Local PC archive path directory not found: {ArchivePath}");
            return;
        }

        // Perform Stage 3 Preflight Sanity Check
        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(ArchivePath);
            DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
            DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
            var manualSelectedPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
                ? ManualSelectedItems.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            string devId = CurrentDeviceId;
            var configCheck = new AndroidSyncConfig
            {
                ConfiguredDeviceId = devId,
                ScopeMode = ScopeMode,
                ManualSelectedPaths = manualSelectedPaths,
                Filters = BuildScopeFilters(dateFrom, dateTo)
            };

            var targetFiles = manifest.Where(configCheck.IsEntryIncluded).Select(e => e.DestPath).ToList();
            var preflightResult = ExportPreflightCheck.ValidateBeforeExport(ArchivePath, targetFiles, manifest);

            if (!preflightResult.IsSuccess)
            {
                pendingPreflightResult = preflightResult;
                PreflightTitle = App.GetString("TitlePreflightIntercept", "⚠️ 恢复完整性校验拦截");

                var sb = new System.Text.StringBuilder();
                if (preflightResult.BrokenLivePhotoPairs.Count > 0)
                {
                    sb.AppendLine(string.Format(App.GetString("MsgPreflightBrokenPairs", "💡 检测到 {0} 项媒体缺失配对的 Live Photo 动态视频/侧车组件："), preflightResult.BrokenLivePhotoPairs.Count));
                    foreach (var item in preflightResult.BrokenLivePhotoPairs.Take(5))
                    {
                        sb.AppendLine($"  • {item}");
                    }
                    if (preflightResult.BrokenLivePhotoPairs.Count > 5)
                    {
                        sb.AppendLine(string.Format(App.GetString("FmtMoreItems", "  ... 等共 {0} 项"), preflightResult.BrokenLivePhotoPairs.Count));
                    }
                }

                if (preflightResult.MissingPhysicalFiles.Count > 0)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine(string.Format(App.GetString("MsgPreflightMissingFiles", "❌ 检测到 {0} 项文件在 PC 磁盘上已被移动或删除："), preflightResult.MissingPhysicalFiles.Count));
                    foreach (var item in preflightResult.MissingPhysicalFiles.Take(5))
                    {
                        sb.AppendLine($"  • {item}");
                    }
                }

                PreflightMessage = sb.ToString();
                IsPreflightModalOpen = true;
                return;
            }
        }
        catch { }

        await ExecuteStartSyncInternalAsync();
    }

    private async Task ExecuteStartSyncInternalAsync()
    {
        IsSyncing = true;
        CopiedCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        ProgressValue = 0;
        ProgressText = progressSticky.Set("AndroidSyncInit", "正在初始化 Android FTP 增量同步...");

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
                RemainingFilesCount = Math.Max(0, snapshot.TotalFiles - snapshot.ProcessedFiles);
                SpeedBytesPerSecond = snapshot.CurrentBytesPerSecond;
                SpeedText = snapshot.CurrentBytesPerSecond > 0 ? $"{snapshot.CurrentBytesPerSecond / 1024d / 1024d:F1} MB/s" : "--";
                TransferredSizeText = $"{FormatBytes(snapshot.ProcessedBytes)} / {FormatBytes(snapshot.TotalBytes)}";
                EtaText = snapshot.Eta is TimeSpan eta
                    ? UiText.Duration(eta)
                    : (percent >= 100 ? App.GetString("MsgSyncCompleted", "同步完成") : App.GetString("MsgCalculating", "计算中..."));
                ProgressText = progressSticky.Set(
                    "FmtAndroidSyncProgress",
                    "{0:F1}% ({1:N0}/{2:N0} 文件) - {3}",
                    percent,
                    snapshot.ProcessedFiles,
                    snapshot.TotalFiles,
                    FormatSpeed(snapshot.CurrentBytesPerSecond));
            });
        });

        AddLog($"[START] Starting standalone sync to Android device '{CurrentDeviceId}'...");

        DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
        DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
        var manualSelectedPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
            ? ManualSelectedItems.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        string devId = CurrentDeviceId;
        var config = new AndroidSyncConfig
        {
            ConfiguredDeviceId = devId,
            FtpHost = AndroidFtpHost,
            FtpPort = AndroidFtpPort,
            FtpUser = AndroidFtpUser,
            FtpPassword = AndroidFtpPassword,
            RemoteTargetDir = AndroidTargetDir,
            RestoreMode = RestoreMode,
            ScopeMode = ScopeMode,
            ManualSelectedPaths = manualSelectedPaths,
            Filters = BuildScopeFilters(dateFrom, dateTo)
        };

        try
        {
            var result = await AndroidSyncEngine.SyncAsync(
                ArchivePath,
                config,
                ConfirmDeviceIdentity,
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

            if (result.Aborted)
            {
                ProgressValue = 0;
                ProgressText = progressSticky.Set("AndroidSyncIdentityAborted", "已中止：未确认目标设备身份，未传输任何文件。");
                AddLog("[ABORTED] 设备身份未获确认，回传已中止。");
                return;
            }

            AndroidDeviceId = result.DeviceId;
            SaveConfig();

            CopiedCount = result.CopiedCount;
            SkippedCount = result.SkippedCount;
            FailedCount = result.FailedCount;
            ProgressValue = 100;
            ProgressText = progressSticky.Set("FmtAndroidSyncDone", "同步完成: 已传输 {0}，跳过 {1}，失败 {2}", result.CopiedCount, result.SkippedCount, result.FailedCount);
            AddLog($"[FINISHED] Android sync finished for device ID {result.DeviceId}. Transferred {result.CopiedCount:N0} files ({FormatBytes(result.BytesCopied)}).");
        }
        catch (OperationCanceledException)
        {
            ProgressText = progressSticky.Set("MsgSyncCancelled", "同步已取消");
            AddLog("[CANCELLED] Sync cancelled by user.");
        }
        catch (Exception ex)
        {
            ProgressText = progressSticky.Set("FmtAndroidSyncFailed", "同步失败: {0}", ex.Message);
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
                UiText.MissingFiles(missing.Count, missing),
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
