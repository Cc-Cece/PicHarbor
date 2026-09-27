using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Progress;
using GetAndSee.Core.iPhone;

namespace GetAndSee.Gui.ViewModels;

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
    public string CapturedAt { get; set; } = string.Empty;
    public string SizeText { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

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

    [ObservableProperty]
    private bool enableMirrorDelete = true;

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
    private string scopeDateFromText = "";

    [ObservableProperty]
    private string scopeDateToText = "";

    partial void OnScopeDateFromTextChanged(string value) => RecalculateScopeSummary();
    partial void OnScopeDateToTextChanged(string value) => RecalculateScopeSummary();

    [ObservableProperty]
    private string scopeSummaryText = "📊 当前筛选结果: 预计恢复 0 项 (照片 0，视频 0)，约 0 B";

    [ObservableProperty]
    private int manualSelectionCount = 0;

    [ObservableProperty]
    private string manualSelectionCountText = "已选择 0 项媒体";

    [ObservableProperty]
    private string manualSelectionModalBtnText = "👁️ 查看/编辑清单 (0)";

    public bool HasManualSelections => ManualSelectionCount > 0;

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

    [ObservableProperty]
    private int copiedCount = 0;

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
            var set = journal.GetManualSelections(DeviceModel);
            var manifest = journal.ReadManifest();
            var dict = manifest.ToDictionary(m => m.DestPath, m => m, StringComparer.OrdinalIgnoreCase);

            foreach (var path in set)
            {
                if (dict.TryGetValue(path, out var entry))
                {
                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = entry.DestPath,
                        FullPath = Path.Combine(ArchivePath, entry.DestPath),
                        CapturedAt = entry.ExifDateTimeOriginalIso ?? entry.SourceMtimeIso ?? "N/A",
                        SizeBytes = entry.SizeBytes,
                        SizeText = FormatByteSize(entry.SizeBytes)
                    });
                }
                else
                {
                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = path,
                        FullPath = Path.Combine(ArchivePath, path),
                        CapturedAt = "N/A",
                        SizeBytes = 0,
                        SizeText = "0 B"
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
                ? ManualSelectedItems.Select(m => m.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
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
            ScopeSummaryText = "📊 当前筛选结果: 预计恢复 0 项";
        }
    }

    private static bool IsPhotoExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".heic" or ".png" or ".webp" or ".dng" or ".cr2" or ".nef" or ".arw";
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

    public void RequestMirrorDeleteToggle(bool newValue)
    {
        if (newValue)
        {
            EnableMirrorDelete = true;
            return;
        }

        ShowConfirmationModal(
            App.GetString("MsgMirrorDeleteWarningTitle", "ℹ️ 关闭镜像删除提示"),
            App.GetString("MsgMirrorDeleteWarningBody", "关闭后，归档中已删除的照片仍将保留在同步文件夹中，同步到 iPhone 的照片将不会自动移除。是否确认？"),
            () =>
            {
                EnableMirrorDelete = false;
                AddLog("[INFO] 镜像删除选项已关闭。");
            },
            () =>
            {
                OnPropertyChanged(nameof(EnableMirrorDelete));
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
    private async Task StartExportAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            MessageBox.Show(
                App.GetString("MsgDirNotExist", "归档目录不存在，请先选择有效的备份目标路径。"),
                App.GetString("AppTitle", "GetAndSee"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        IsExporting = true;
        ProgressValue = 0;
        ProgressText = App.GetString("MsgCalculating", "计算中...");
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
            EnableMirrorDelete = EnableMirrorDelete,
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
            ProgressText = App.GetString("MsgBackupCompleted", "整理完成");
            AddLog($"[SUCCESS] 🎉 同步文件夹已就绪！有效路径: {result.ExportedFolder}");
            AddLog("[INFO] 请点击【步骤 2: 打开 Apple Devices 软件】，在 Apple Devices 左侧选择「照片」并选定该文件夹完成同步。");
        }
        catch (OperationCanceledException)
        {
            ProgressText = App.GetString("MsgSyncCancelled", "同步已取消");
            AddLog("[WARN] 同步准备已被用户取消。");
        }
        catch (Exception ex)
        {
            ProgressText = "出错";
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
            Title = "选择 iPhone 专用同步目录",
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
            Process.Start(new ProcessStartInfo("ms-x-appledevices:") { UseShellExecute = true });
            AddLog("[INFO] 正在唤起 Apple Devices 官方应用...");
        }
        catch (Exception ex)
        {
            AddLog($"[WARN] 无法直接唤起 Apple Devices 协议 ({ex.Message})，已在资源管理器中打开同步文件夹。");
            OpenSyncFolder();
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
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ModalConfirmText));
        UpdateManualSelectionTexts();
        RecalculateScopeSummary();
    }
}
