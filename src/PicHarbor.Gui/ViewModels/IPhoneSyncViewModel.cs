using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Progress;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public partial class SubfolderOptionViewModel : ObservableObject
{
    public string FolderName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;

    [ObservableProperty]
    private bool isChecked = true;

    public Action? OnCheckedChanged { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        OnCheckedChanged?.Invoke();
    }
}

public partial class ManualSelectedItemViewModel : ObservableObject
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    public string FileName => string.IsNullOrWhiteSpace(RelativePath) ? "" : Path.GetFileName(RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public string FormatUpper
    {
        get
        {
            string ext = Path.GetExtension(FileName);
            return string.IsNullOrEmpty(ext) ? "" : ext.TrimStart('.').ToUpperInvariant();
        }
    }
    public string CapturedAt { get; set; } = string.Empty;
    public string SizeText { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string DisplayCapturedAt
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CapturedAt) || CapturedAt == "N/A") return "N/A";
            if (DateTime.TryParse(CapturedAt, out var dt))
            {
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            return CapturedAt.Replace('T', ' ');
        }
    }

    [ObservableProperty]
    private bool isAutofilled = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailLoaded))]
    private ImageSource? thumbnailImage;

    public bool IsThumbnailLoaded => ThumbnailImage is not null;
}

public partial class IPhoneSyncViewModel : ObservableObject
{
    private CancellationTokenSource? cts;

    [ObservableProperty]
    private string archivePath = "";

    [ObservableProperty]
    private string deviceModel = "iPhone 15 Pro";

    [ObservableProperty]
    private string customSyncFolder = "";

    [ObservableProperty]
    private IPhoneAlbumMode albumMode = IPhoneAlbumMode.YearMonth;

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
    private bool isExporting = false;

    [ObservableProperty]
    private bool hasExported = false;

    [ObservableProperty]
    private double progressValue = 0;

    [ObservableProperty]
    private string progressText = App.GetString("MsgReady", "准备就绪");
    private readonly StickyText progressSticky = new();

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
    private int deletedCount = 0;

    [ObservableProperty]
    private int failedCount = 0;

    [ObservableProperty]
    private string transferredSizeText = "0 B";

    // Confirmation Modal State
    [ObservableProperty]
    private bool isModalOpen = false;

    [ObservableProperty]
    private string modalTitle = "";

    [ObservableProperty]
    private string modalMessage = "";

    [ObservableProperty]
    private string modalConfirmText = App.GetString("CloseBtn", "确认");

    private Action? pendingConfirmAction;
    private Action? pendingCancelAction;

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

    public ObservableCollection<string> LogEntries { get; } = new()
    {
        "[INFO] 恢复到 iPhone (Apple Devices 官方同步) 模块已准备就绪。",
        "[INFO] 导出的物理文件将保存在专有同步目录中，可直接在 Apple Devices 软件中一键同步。"
    };

    public string EffectiveSyncFolder => string.IsNullOrWhiteSpace(CustomSyncFolder)
        ? (string.IsNullOrWhiteSpace(ArchivePath) ? "" : Path.Combine(ArchivePath, ".AppleSync", string.IsNullOrWhiteSpace(DeviceModel) ? "iPhone" : DeviceModel))
        : CustomSyncFolder;

    public bool IsYearMonthMode
    {
        get => AlbumMode == IPhoneAlbumMode.YearMonth;
        set
        {
            if (value && AlbumMode != IPhoneAlbumMode.YearMonth)
            {
                RequestAlbumModeChange(IPhoneAlbumMode.YearMonth);
            }
        }
    }

    public bool IsFlatMode
    {
        get => AlbumMode == IPhoneAlbumMode.Flat;
        set
        {
            if (value && AlbumMode != IPhoneAlbumMode.Flat)
            {
                RequestAlbumModeChange(IPhoneAlbumMode.Flat);
            }
        }
    }

    partial void OnArchivePathChanged(string value)
    {
        LoadSubfolders();
        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();
        OnPropertyChanged(nameof(EffectiveSyncFolder));
    }

    partial void OnDeviceModelChanged(string value)
    {
        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();
        OnPropertyChanged(nameof(EffectiveSyncFolder));
    }

    public HashSet<string> GetManualSelectionPathsSet()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            return journal.GetManualSelections(DeviceModel);
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
            ManualSelectionsReloaded?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var autofilledMap = journal.GetManualSelectionsWithAutofilled(DeviceModel);
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
                        SizeText = FormatByteSize(entry.SizeBytes),
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
                        sizeStr = FormatByteSize(size);
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
        ManualSelectionsReloaded?.Invoke(this, EventArgs.Empty);
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

            var config = new IPhoneExportConfig
            {
                DeviceModel = DeviceModel,
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
            ScopeSummaryText = string.Format(template, filtered.Count, photoCount, videoCount, FormatByteSize(totalBytes));
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

                if (journal.AddManualSelection(DeviceModel, relPath))
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
                    journal.BatchAddManualSelections(DeviceModel, autoAdd, isAutofilled: true);
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
            var toAdd = MissingPairSuggestions.Select(s => s.SuggestedRelativePath).ToList();
            journal.BatchAddManualSelections(DeviceModel, toAdd, isAutofilled: true);
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

    public event EventHandler? ManualSelectionsReloaded;

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
                journal.RemoveManualSelection(DeviceModel, item.RelativePath);
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
                journal.ClearManualSelections(DeviceModel);
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

    public void RequestAlbumModeChange(IPhoneAlbumMode newMode)
    {
        if (AlbumMode == newMode) return;

        ShowConfirmationModal(
            App.GetString("MsgAlbumModeWarningTitle", "ℹ️ 确认更改相册组织结构"),
            App.GetString("MsgAlbumModeWarningBody", "更改相册结构将重新整理同步文件夹下的目录形态，下一次在 Apple Devices 同步时，iPhone 上的相册分类将会相应重组。是否确认更改？"),
            () =>
            {
                AlbumMode = newMode;
                OnPropertyChanged(nameof(IsYearMonthMode));
                OnPropertyChanged(nameof(IsFlatMode));
                AddLog($"[INFO] 相册建立模式已更改为: {newMode}");
            },
            () =>
            {
                OnPropertyChanged(nameof(IsYearMonthMode));
                OnPropertyChanged(nameof(IsFlatMode));
            });
    }



    public void RequestSyncPathChange(string newPath)
    {
        if (string.Equals(CustomSyncFolder, newPath, StringComparison.OrdinalIgnoreCase)) return;

        bool hasPreviousSync = HasExported || CheckPreviousSyncExists();
        if (hasPreviousSync)
        {
            ShowConfirmationModal(
                App.GetString("MsgPathWarningTitle", "⚠️ 高危操作警告：更改 iPhone 同步目录"),
                App.GetString("MsgPathWarningBody", "检测到该 iPhone 先前已与原有目录进行过同步。在 Apple Devices 中将同步源切换到新目录后，iPhone 上原先通过电脑同步的所有照片将被系统自动移除（手机本机拍摄的照片不受影响）。\n是否确认更改同步路径？"),
                () =>
                {
                    CustomSyncFolder = newPath;
                    OnPropertyChanged(nameof(EffectiveSyncFolder));
                    AddLog($"[WARN] 用户确认更换同步目录: {newPath}");
                },
                () => { });
        }
        else
        {
            CustomSyncFolder = newPath;
            OnPropertyChanged(nameof(EffectiveSyncFolder));
        }
    }

    private bool CheckPreviousSyncExists()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath)) return false;
        try
        {
            using var journal = TransferJournal.Open(ArchivePath);
            return journal.GetIPhoneDevice(DeviceModel) is not null;
        }
        catch
        {
            return false;
        }
    }

    private void ShowConfirmationModal(string title, string message, Action onConfirm, Action? onCancel = null)
    {
        ModalTitle = title;
        ModalMessage = message;
        pendingConfirmAction = onConfirm;
        pendingCancelAction = onCancel;
        IsModalOpen = true;
    }

    [RelayCommand]
    private void ConfirmModal()
    {
        IsModalOpen = false;
        var action = pendingConfirmAction;
        pendingConfirmAction = null;
        pendingCancelAction = null;
        action?.Invoke();
    }

    [RelayCommand]
    private void CancelModal()
    {
        IsModalOpen = false;
        var action = pendingCancelAction;
        pendingConfirmAction = null;
        pendingCancelAction = null;
        action?.Invoke();
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
                    journal.BatchAddManualSelections(DeviceModel, suggestions.Select(s => s.SuggestedRelativePath), isAutofilled: true);
                    LoadManualSelectionsFromDb();
                    RecalculateScopeSummary();
                }
            }
            catch { }
        }

        pendingPreflightResult = null;
        _ = ExecuteStartExportInternalAsync();
    }

    [RelayCommand]
    private void IgnorePreflightAndProceed()
    {
        IsPreflightModalOpen = false;
        pendingPreflightResult = null;
        _ = ExecuteStartExportInternalAsync();
    }

    [RelayCommand]
    private void CancelPreflightModal()
    {
        IsPreflightModalOpen = false;
        pendingPreflightResult = null;
        IsExporting = false;
    }

    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            MessageBox.Show(
                App.GetString("MsgDirNotExist", "归档目录不存在，请先选择有效的备份目标路径。"),
                App.GetString("AppTitle", "PicHarbor"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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

            var configCheck = new IPhoneExportConfig
            {
                DeviceModel = DeviceModel,
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

        await ExecuteStartExportInternalAsync();
    }

    private async Task ExecuteStartExportInternalAsync()
    {
        IsExporting = true;
        ProgressValue = 0;
        ProgressText = progressSticky.Set("MsgCalculating", "计算中...");
        CopiedCount = 0;
        SkippedCount = 0;
        DeletedCount = 0;
        FailedCount = 0;

        cts = new CancellationTokenSource();
        AddLog("[INFO] 开始整理并准备 iPhone 专有同步目录...");

        DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
        DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;
        var selectedSubfolders = ScopeMode == IPhoneRestoreScopeMode.Subfolder
            ? Subfolders.Where(s => s.IsChecked).Select(s => s.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var manualSelectedPaths = ScopeMode == IPhoneRestoreScopeMode.ManualSelection
            ? ManualSelectedItems.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var config = new IPhoneExportConfig
        {
            DeviceModel = DeviceModel,
            CustomSyncFolder = string.IsNullOrWhiteSpace(CustomSyncFolder) ? null : CustomSyncFolder,
            AlbumMode = AlbumMode,
            ScopeMode = ScopeMode,
            DateFrom = dateFrom,
            DateTo = dateTo,
            SelectedSubfolders = selectedSubfolders,
            ManualSelectedPaths = manualSelectedPaths
        };

        var progress = new Progress<ProgressSnapshot>(s =>
        {
            ProgressValue = s.ByteFraction * 100;
            ProgressText = $"{s.ProcessedFiles} / {s.TotalFiles} ({s.ByteFraction * 100:F1}%)";
            CopiedCount = s.CopiedFiles;
            SkippedCount = s.SkippedFiles;
            FailedCount = s.FailedFiles;
            RemainingFilesCount = Math.Max(0, s.TotalFiles - s.ProcessedFiles);
            SpeedBytesPerSecond = s.CurrentBytesPerSecond;
            SpeedText = s.CurrentBytesPerSecond > 0 ? $"{s.CurrentBytesPerSecond / 1024d / 1024d:F1} MB/s" : "--";
            TransferredSizeText = FormatByteSize(s.ProcessedBytes);
        });

        try
        {
            var result = await Task.Run(() => IPhoneSyncEngine.ExportAsync(
                ArchivePath,
                config,
                progress,
                msg => Application.Current.Dispatcher.Invoke(() => AddLog(msg)),
                cts.Token));

            DeletedCount = result.DeletedCount;
            HasExported = true;
            ProgressValue = 100;
            ProgressText = progressSticky.Set("MsgBackupCompleted", "整理完成");
            AddLog($"[SUCCESS] 🎉 同步文件夹已就绪！有效路径: {result.ExportedFolder}");
            AddLog("[INFO] 请点击【步骤 2: 打开 Apple Devices 软件】，在 Apple Devices 左侧选择「照片」并选定该文件夹完成同步。");
        }
        catch (OperationCanceledException)
        {
            ProgressText = progressSticky.Set("MsgSyncCancelled", "同步已取消");
            AddLog("[WARN] 同步准备已被用户取消。");
        }
        catch (Exception ex)
        {
            ProgressText = progressSticky.Set("SyncErrorShort", "出错");
            AddLog($"[ERROR] ❌ 导出过程发生错误: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void CancelExport()
    {
        cts?.Cancel();
    }

    [RelayCommand]
    private void BrowseSyncFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = App.GetString("IPhoneSyncBrowseTitle", "选择 iPhone 专用同步目录"),
            InitialDirectory = Directory.Exists(EffectiveSyncFolder) ? EffectiveSyncFolder : ArchivePath
        };

        if (dialog.ShowDialog() == true)
        {
            RequestSyncPathChange(dialog.FolderName);
        }
    }

    [RelayCommand]
    private void OpenAppleDevices()
    {
        try
        {
            Process.Start(new ProcessStartInfo("AppleDevices.exe") { UseShellExecute = true });
            AddLog("[INFO] 正在唤起 Apple Devices 官方应用...");
        }
        catch (Exception ex)
        {
            AddLog($"[WARN] 无法启动 Apple Devices ({ex.Message})，改为打开 Microsoft Store 页面。");
            try
            {
                Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NP83LWLPZ9U") { UseShellExecute = true });
            }
            catch (Exception storeEx)
            {
                AddLog($"[ERROR] 无法打开 Apple Devices 的商店页面: {storeEx.Message}");
            }
        }
    }

    [RelayCommand]
    private void OpenSyncFolder()
    {
        string folder = EffectiveSyncFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            AddLog($"[INFO] 已在资源管理器中打开同步目录: {folder}");
        }
        catch (Exception ex)
        {
            AddLog($"[ERROR] 打开同步目录失败: {ex.Message}");
        }
    }

    private void AddLog(string log)
    {
        LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {log}");
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {units[order]}";
    }

    public void OnLanguageChanged()
    {
        if (!IsExporting && progressSticky.HasValue)
        {
            ProgressText = progressSticky.Current;
        }

        OnPropertyChanged(nameof(ModalConfirmText));
        UpdateManualSelectionTexts();
        RecalculateScopeSummary();
    }
}
