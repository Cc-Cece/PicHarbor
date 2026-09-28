using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Android;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.iPhone;
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

    public bool IsScopeDateRange
    {
        get => ScopeMode == IPhoneRestoreScopeMode.DateRange;
        set
        {
            if (value && ScopeMode != IPhoneRestoreScopeMode.DateRange)
            {
                ScopeMode = IPhoneRestoreScopeMode.DateRange;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    public bool IsScopeSubfolder
    {
        get => ScopeMode == IPhoneRestoreScopeMode.Subfolder;
        set
        {
            if (value && ScopeMode != IPhoneRestoreScopeMode.Subfolder)
            {
                ScopeMode = IPhoneRestoreScopeMode.Subfolder;
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
        OnPropertyChanged(nameof(IsScopeDateRange));
        OnPropertyChanged(nameof(IsScopeSubfolder));
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

    public ObservableCollection<SubfolderOptionViewModel> Subfolders { get; } = new();
    public ObservableCollection<ManualSelectedItemViewModel> ManualSelectedItems { get; } = new();

    // --- End Restore Scope Properties ---

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

    partial void OnArchivePathChanged(string value)
    {
        LoadSubfolders();
        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();
    }

    partial void OnAndroidDeviceNameChanged(string value) => SaveConfig();
    partial void OnAndroidFtpHostChanged(string value) => SaveConfig();
    partial void OnAndroidFtpPortChanged(int value) => SaveConfig();
    partial void OnAndroidFtpUserChanged(string value) => SaveConfig();
    partial void OnAndroidFtpPasswordChanged(string value) => SaveConfig();
    partial void OnAndroidTargetDirChanged(string value) => SaveConfig();
    partial void OnAndroidDeviceIdChanged(string value)
    {
        SaveConfig();
        LoadManualSelectionsFromDb();
    }

    public HashSet<string> GetManualSelectionPathsSet()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
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
            string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
            var autofilledMap = journal.GetAndroidManualSelectionsWithAutofilled(devId);
            var manifest = journal.ReadManifest();
            var dict = manifest.ToDictionary(m => m.DestPath.Replace('\\', '/'), m => m, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, isAutofilled) in autofilledMap)
            {
                string normPath = path.Replace('\\', '/');
                if (dict.TryGetValue(normPath, out var entry))
                {
                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = entry.DestPath.Replace('\\', '/'),
                        FullPath = Path.Combine(ArchivePath, entry.DestPath),
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

    public void UpdateManualSelectionTexts()
    {
        ManualSelectionCount = ManualSelectedItems.Count;
        OnPropertyChanged(nameof(HasManualSelections));
        OnPropertyChanged(nameof(HasNoManualSelections));
        ManualSelectionCountText = string.Format(App.GetString("ScopeManualCountText", "已选择 {0} 项媒体"), ManualSelectionCount);
        ManualSelectionModalBtnText = string.Format(App.GetString("ScopeViewEditListBtn", "👁️ 查看/编辑清单 ({0})"), ManualSelectionCount);
    }

    public void LoadSubfolders()
    {
        Subfolders.Clear();
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return;

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = journal.ReadManifest();
            var rootDirs = manifest
                .Select(m =>
                {
                    string norm = m.DestPath.Replace('\\', '/');
                    int idx = norm.IndexOf('/');
                    return idx > 0 ? norm.Substring(0, idx) : norm;
                })
                .Where(d => !string.IsNullOrWhiteSpace(d) && !d.StartsWith("."))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d);

            foreach (var dir in rootDirs)
            {
                Subfolders.Add(new SubfolderOptionViewModel
                {
                    FolderName = dir,
                    RelativePath = dir,
                    IsChecked = true,
                    OnCheckedChanged = () => RecalculateScopeSummary()
                });
            }
        }
        catch
        {
        }
    }

    public void RecalculateScopeSummary()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            ScopeSummaryText = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = journal.ReadManifest();

            DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
            DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
            var selectedFolders = ScopeMode == IPhoneRestoreScopeMode.Subfolder
                ? Subfolders.Where(s => s.IsChecked).Select(s => s.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var manualPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
                ? ManualSelectedItems.Select(m => m.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
            var config = new AndroidSyncConfig
            {
                DeviceName = AndroidDeviceName,
                ConfiguredDeviceId = devId,
                ScopeMode = ScopeMode,
                DateFrom = dateFrom,
                DateTo = dateTo,
                SelectedSubfolders = selectedFolders,
                ManualSelectedPaths = manualPaths
            };

            var filtered = manifest.Where(config.IsEntryIncluded).ToList();

            int photoCount = filtered.Count(e => IsPhotoExtension(Path.GetExtension(e.DestPath)));
            int videoCount = filtered.Count - photoCount;
            long totalBytes = filtered.Sum(e => e.SizeBytes);

            string template = App.GetString("ScopeSummaryText", "📊 当前筛选结果: 预计恢复 {0} 项 (照片 {1}，视频 {2})，约 {3}");
            ScopeSummaryText = string.Format(template, filtered.Count, photoCount, videoCount, FormatBytes(totalBytes));
        }
        catch
        {
            ScopeSummaryText = "📊 当前筛选结果: 预计恢复 0 项";
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
        string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;

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
            NotificationMessage = $"❌ 过程出现错误: {ex.Message}";
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
                var manifest = journal.ReadManifest();
                var currentPaths = ManualSelectedItems.Select(m => m.RelativePath).ToList();
                var suggestions = MediaPairMatcher.FindMissingPairs(
                    currentPaths,
                    manifest,
                    appConfig.AutoCompleteLivePhotoPair,
                    appConfig.AutoCompleteAaeSidecar,
                    appConfig.AutoCompleteRawJpg);

                if (suggestions.Count > 0)
                {
                    var autoAdd = suggestions.Select(s => s.SuggestedRelativePath).ToList();
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
                string sampleText = autoAddedCount == 1 ? "1 项配对" : $"{autoAddedCount} 项配对";
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
            var manifest = journal.ReadManifest();
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
            string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
            var toAdd = MissingPairSuggestions.Select(s => s.SuggestedRelativePath).ToList();
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

    [RelayCommand]
    private void OpenManualModal()
    {
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
                string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
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
                string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
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
        foreach (var item in targets)
        {
            try
            {
                ImageSource? thumb = await Task.Run(() => LoadFrozenThumbnail(item.FullPath, 120)).ConfigureAwait(false);
                if (thumb is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => item.ThumbnailImage = thumb);
                }
            }
            catch { }
        }
    }

    private static ImageSource? LoadFrozenThumbnail(string filePath, int decodeWidth = 120)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            var shellThumb = GetAndSee.Gui.Util.ShellServices.GetShellThumbnail(filePath, decodeWidth, decodeWidth);
            if (shellThumb is not null)
                return shellThumb;
        }
        catch { }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(ProgressText));
        UpdateManualSelectionTexts();
        RecalculateScopeSummary();
    }

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
    private void FixPreflightAndProceed()
    {
        IsPreflightModalOpen = false;
        if (pendingPreflightResult is not null)
        {
            try
            {
                using var journal = TransferJournal.Open(ArchivePath);
                var manifest = journal.ReadManifest();
                var currentPaths = ManualSelectedItems.Select(m => m.RelativePath).ToList();
                var config = Config.AppSettings.Load();

                var suggestions = MediaPairMatcher.FindMissingPairs(currentPaths, manifest, true, true, config.AutoCompleteRawJpg);
                if (suggestions.Count > 0)
                {
                    string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
                    journal.BatchAddAndroidManualSelections(devId, suggestions.Select(s => s.SuggestedRelativePath), isAutofilled: true);
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
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            TestStatus = "❌ PC 本地归档目录不存在！请先在主界面或设置中配置归档路径。";
            AddLog($"[ERROR] Local PC archive path directory not found: {ArchivePath}");
            return;
        }

        // Perform Stage 3 Preflight Sanity Check
        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = journal.ReadManifest();
            DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
            DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
            var selectedSubfolders = ScopeMode == IPhoneRestoreScopeMode.Subfolder
                ? Subfolders.Where(s => s.IsChecked).Select(s => s.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var manualSelectedPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
                ? ManualSelectedItems.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;

            string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
            var configCheck = new AndroidSyncConfig
            {
                DeviceName = AndroidDeviceName,
                ConfiguredDeviceId = devId,
                ScopeMode = ScopeMode,
                DateFrom = dateFrom,
                DateTo = dateTo,
                SelectedSubfolders = selectedSubfolders,
                ManualSelectedPaths = manualSelectedPaths
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
                        sb.AppendLine($"  ... 等共 {preflightResult.BrokenLivePhotoPairs.Count} 项");
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

        DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
        DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
        var selectedSubfolders = ScopeMode == IPhoneRestoreScopeMode.Subfolder
            ? Subfolders.Where(s => s.IsChecked).Select(s => s.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var manualSelectedPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
            ? ManualSelectedItems.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        string devId = string.IsNullOrWhiteSpace(AndroidDeviceId) ? (string.IsNullOrWhiteSpace(AndroidDeviceName) ? "Android Device" : AndroidDeviceName) : AndroidDeviceId;
        var config = new AndroidSyncConfig
        {
            DeviceName = AndroidDeviceName,
            ConfiguredDeviceId = devId,
            FtpHost = AndroidFtpHost,
            FtpPort = AndroidFtpPort,
            FtpUser = AndroidFtpUser,
            FtpPassword = AndroidFtpPassword,
            RemoteTargetDir = AndroidTargetDir,
            RestoreMode = RestoreMode,
            ScopeMode = ScopeMode,
            DateFrom = dateFrom,
            DateTo = dateTo,
            SelectedSubfolders = selectedSubfolders,
            ManualSelectedPaths = manualSelectedPaths
        };

        try
        {
            var result = await AndroidSyncEngine.SyncAsync(
                ArchivePath,
                config,
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
