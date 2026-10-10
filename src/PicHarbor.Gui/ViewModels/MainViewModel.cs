using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Device;
using PicHarbor.Core.Storage;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer deviceProbeTimer;

    [ObservableProperty]
    private int selectedTabIndex = 0;

    [ObservableProperty]
    private int backupSubTabIndex = 0;

    [ObservableProperty]
    private int syncSubTabIndex = 0;

    [ObservableProperty]
    private int settingsSubTabIndex = 0;

    [ObservableProperty]
    private int selectedDeviceIndex = 0; // 0 for iPhone, 1 for Android

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isScopeModalOpen = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isRenameModalOpen = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isAndroidFtpModalOpen = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isImageViewerOpen = false;

    [ObservableProperty]
    private string viewerImageTitle = "";

    [ObservableProperty]
    private string viewerImageDetails = "";

    [ObservableProperty]
    private System.Windows.Media.ImageSource? viewerImageSource;

    [ObservableProperty]
    private string viewerImagePath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    private bool isUnifiedManualModalOpen = false;

    public ObservableCollection<ManualSelectedItemViewModel> UnifiedManualSelectedItems => IPhoneSyncVM.ManualSelectedItems;

    public string UnifiedManualSelectionCountText =>
        string.Format(App.GetString("ScopeManualCountText", "已选 {0} 项媒体"), UnifiedManualSelectedItems.Count);

    [ObservableProperty]
    private bool isTaskManagerOpen = false;

    [ObservableProperty]
    private string deviceCustomName = "";

    [ObservableProperty]
    private bool isDeviceConnected = false;

    [ObservableProperty]
    private string deviceStatusText = "未检测到 iPhone (USB/AFC)";

    public static string DefaultArchivePath =>
        LibraryStorageService.GetLibraryRootForDrive(LibraryStorageService.GetDefaultDriveLetter());

    [ObservableProperty]
    private string destinationPath = DefaultArchivePath;

    [ObservableProperty]
    private string currentLanguage = "zh-CN";

    public BackupViewModel BackupVM { get; }
    public AndroidBackupViewModel AndroidBackupVM { get; }
    public IPhoneSyncViewModel IPhoneSyncVM { get; } = new();
    public AndroidSyncViewModel AndroidSyncVM { get; } = new();
    public GooglePhotosSyncViewModel GooglePhotosVM { get; } = new();
    public StatusViewModel StatusVM { get; } = new();
    public SearchViewModel SearchVM { get; } = new();
    public ReorganizeViewModel ReorganizeVM { get; } = new();
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel()
    {
        BackupVM = new BackupViewModel(OnDestinationPathUpdatedFromBackup);
        AndroidBackupVM = new AndroidBackupViewModel(OnDestinationPathUpdatedFromBackup);
        SettingsVM = new SettingsViewModel(OnDestinationPathUpdatedFromBackup);

        var config = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(config.DestinationPath))
        {
            destinationPath = config.DestinationPath;
        }
        if (!string.IsNullOrWhiteSpace(config.CurrentLanguage))
        {
            currentLanguage = config.CurrentLanguage;
            App.SwitchLanguage(config.CurrentLanguage);
        }

        // Link SearchVM to IPhoneSyncVM, AndroidSyncVM and GooglePhotosVM for manual selection coordination
        SearchVM.IPhoneSyncVM = IPhoneSyncVM;
        SearchVM.AndroidSyncVM = AndroidSyncVM;
        SearchVM.GooglePhotosVM = GooglePhotosVM;

        // Wire navigation callbacks
        BackupVM.NavigateToSettingsAction = () => SelectedTabIndex = 3;
        AndroidBackupVM.NavigateToSettingsAction = () => SelectedTabIndex = 3;
        IPhoneSyncVM.NavigateToSettingsAction = () => SelectedTabIndex = 3;
        AndroidSyncVM.NavigateToSettingsAction = () => SelectedTabIndex = 3;
        GooglePhotosVM.NavigateToSettingsAction = () =>
        {
            SelectedTabIndex = 3;
            SettingsSubTabIndex = 2; // Jump directly to Engine & Network sub-tab
        };
        GooglePhotosVM.NavigateToSearchAction = () => SelectedTabIndex = 1;

        // Keep Android FTP connection parameters synchronized between Backup and Sync
        AndroidBackupVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AndroidBackupViewModel.AndroidFtpHost)) AndroidSyncVM.AndroidFtpHost = AndroidBackupVM.AndroidFtpHost;
            if (e.PropertyName == nameof(AndroidBackupViewModel.AndroidFtpPort)) AndroidSyncVM.AndroidFtpPort = AndroidBackupVM.AndroidFtpPort;
            if (e.PropertyName == nameof(AndroidBackupViewModel.AndroidFtpUser)) AndroidSyncVM.AndroidFtpUser = AndroidBackupVM.AndroidFtpUser;
            if (e.PropertyName == nameof(AndroidBackupViewModel.AndroidFtpPassword)) AndroidSyncVM.AndroidFtpPassword = AndroidBackupVM.AndroidFtpPassword;
        };
        AndroidSyncVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AndroidSyncViewModel.AndroidFtpHost)) AndroidBackupVM.AndroidFtpHost = AndroidSyncVM.AndroidFtpHost;
            if (e.PropertyName == nameof(AndroidSyncViewModel.AndroidFtpPort)) AndroidBackupVM.AndroidFtpPort = AndroidSyncVM.AndroidFtpPort;
            if (e.PropertyName == nameof(AndroidSyncViewModel.AndroidFtpUser)) AndroidBackupVM.AndroidFtpUser = AndroidSyncVM.AndroidFtpUser;
            if (e.PropertyName == nameof(AndroidSyncViewModel.AndroidFtpPassword)) AndroidBackupVM.AndroidFtpPassword = AndroidSyncVM.AndroidFtpPassword;
        };
        SettingsVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.GooglePhotosProxy)) GooglePhotosVM.Proxy = SettingsVM.GooglePhotosProxy;
        };

        // Wire modal notification updates
        void NotifyModalChanged()
        {
            OnPropertyChanged(nameof(IsDetailModalOpen));
            OnPropertyChanged(nameof(ActiveDetailModalTitle));
            OnPropertyChanged(nameof(ActiveDetailItems));
            OnPropertyChanged(nameof(IsAnyModalOpen));
        }

        void NotifyTaskChanged()
        {
            OnPropertyChanged(nameof(IsAnyTaskRunning));
            OnPropertyChanged(nameof(ActiveTaskTitle));
            OnPropertyChanged(nameof(ActiveTaskProgressPercentage));
            OnPropertyChanged(nameof(ActiveTaskProgressPercentText));
            OnPropertyChanged(nameof(ActiveTaskSpeedText));
            OnPropertyChanged(nameof(ActiveTaskRemainingFilesText));
            OnPropertyChanged(nameof(ActiveTaskCurrentFileName));
            OnPropertyChanged(nameof(ActiveTaskStep2Status));
            OnPropertyChanged(nameof(ActiveTaskStep2Text));
            OnPropertyChanged(nameof(ActiveTaskStep3Status));
            OnPropertyChanged(nameof(ActiveTaskStep4Status));
            OnPropertyChanged(nameof(ActiveTaskCopiedCount));
            OnPropertyChanged(nameof(ActiveTaskSkippedCount));
            OnPropertyChanged(nameof(ActiveTaskFailedCount));
        }

        BackupVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(BackupViewModel.IsDetailModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(BackupViewModel.IsTransferring) or nameof(BackupViewModel.ProgressPercentage) or nameof(BackupViewModel.SpeedText) or nameof(BackupViewModel.RemainingFilesCount) or nameof(BackupViewModel.CurrentFileName) or nameof(BackupViewModel.CopiedCount) or nameof(BackupViewModel.SkippedCount) or nameof(BackupViewModel.FailedCount))
                NotifyTaskChanged();
        };
        AndroidBackupVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AndroidBackupViewModel.IsDetailModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(AndroidBackupViewModel.IsTransferring) or nameof(AndroidBackupViewModel.ProgressPercentage) or nameof(AndroidBackupViewModel.SpeedText) or nameof(AndroidBackupViewModel.RemainingFilesCount) or nameof(AndroidBackupViewModel.CurrentFileName) or nameof(AndroidBackupViewModel.CopiedCount) or nameof(AndroidBackupViewModel.SkippedCount) or nameof(AndroidBackupViewModel.FailedCount))
                NotifyTaskChanged();
        };
        GooglePhotosVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(GooglePhotosSyncViewModel.IsDetailModalOpen) or nameof(GooglePhotosSyncViewModel.IsManualModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(GooglePhotosSyncViewModel.IsSyncing) or nameof(GooglePhotosSyncViewModel.ProgressValue) or nameof(GooglePhotosSyncViewModel.SpeedText) or nameof(GooglePhotosSyncViewModel.RemainingFilesCount) or nameof(GooglePhotosSyncViewModel.CurrentFile) or nameof(GooglePhotosSyncViewModel.UploadedCount) or nameof(GooglePhotosSyncViewModel.SkippedCount) or nameof(GooglePhotosSyncViewModel.FailedCount))
                NotifyTaskChanged();
        };
        AndroidSyncVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(AndroidSyncViewModel.IsDetailModalOpen) or nameof(AndroidSyncViewModel.IsPreflightModalOpen) or nameof(AndroidSyncViewModel.IsManualModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(AndroidSyncViewModel.IsSyncing) or nameof(AndroidSyncViewModel.ProgressValue) or nameof(AndroidSyncViewModel.SpeedText) or nameof(AndroidSyncViewModel.RemainingFilesCount) or nameof(AndroidSyncViewModel.CopiedCount) or nameof(AndroidSyncViewModel.SkippedCount) or nameof(AndroidSyncViewModel.FailedCount))
                NotifyTaskChanged();
        };
        IPhoneSyncVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(IPhoneSyncViewModel.IsPreflightModalOpen) or nameof(IPhoneSyncViewModel.IsManualModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(IPhoneSyncViewModel.IsExporting) or nameof(IPhoneSyncViewModel.ProgressValue) or nameof(IPhoneSyncViewModel.SpeedText) or nameof(IPhoneSyncViewModel.RemainingFilesCount))
                NotifyTaskChanged();
        };

        // Wire unified manual selection modal across all devices
        AndroidBackupVM.OpenManualModalAction = () => IsUnifiedManualModalOpen = true;
        IPhoneSyncVM.OpenManualModalAction = () => IsUnifiedManualModalOpen = true;
        AndroidSyncVM.OpenManualModalAction = () => IsUnifiedManualModalOpen = true;
        GooglePhotosVM.OpenManualModalAction = () => IsUnifiedManualModalOpen = true;
        IPhoneSyncVM.ManualSelectedItems.CollectionChanged += (s, e) => OnPropertyChanged(nameof(UnifiedManualSelectionCountText));

        // Sync initial destination path across all sub-ViewModels
        SyncDestinationPath(DestinationPath);


        deviceProbeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        deviceProbeTimer.Tick += async (s, e) => await ProbeDeviceStatusAsync();
        deviceProbeTimer.Start();

        // Initial probe
        _ = ProbeDeviceStatusAsync();
    }

    partial void OnDestinationPathChanged(string value)
    {
        SyncDestinationPath(value);
        SaveConfig();
    }

    private void SaveConfig()
    {
        var config = AppSettings.Load();
        config.DestinationPath = DestinationPath;
        config.CurrentLanguage = CurrentLanguage;
        AppSettings.Save(config);
    }

    private void OnDestinationPathUpdatedFromBackup(string newPath)
    {
        if (DestinationPath != newPath)
        {
            DestinationPath = newPath;
        }
    }

    private void SyncDestinationPath(string path)
    {
        BackupVM.DestinationPath = path;
        AndroidBackupVM.DestinationPath = path;
        IPhoneSyncVM.ArchivePath = path;
        AndroidSyncVM.ArchivePath = path;
        GooglePhotosVM.ArchivePath = path;
        StatusVM.DatabasePath = path;
        SearchVM.ArchivePath = path;
        ReorganizeVM.ArchivePath = path;
        SettingsVM.ArchivePath = path;

        // Auto refresh stats and search if directory exists
        if (Directory.Exists(path))
        {
            _ = StatusVM.RefreshStatsCommand.ExecuteAsync(null);
            _ = SearchVM.SearchCommand.ExecuteAsync(null);
        }
    }

    private async Task ProbeDeviceStatusAsync()
    {
        DeviceProbeResult result = await DeviceDetector.ProbeAsync(TimeSpan.FromSeconds(1));
        if (result.Status == DeviceProbeStatus.Connected && result.Device is { } device)
        {
            IsDeviceConnected = true;
            string name = string.IsNullOrWhiteSpace(device.Name) ? "iPhone" : device.Name;
            string model = string.IsNullOrWhiteSpace(device.ProductType) ? "" : $" ({device.ProductType})";
            DeviceStatusText = string.Format(App.GetString("MsgConnected", "已连接: {0}{1}"), name, model);
            if (!string.IsNullOrWhiteSpace(device.Name))
            {
                IPhoneSyncVM.DeviceModel = device.Name;
                BackupVM.SetDetectedDevice(device.Name);
            }
        }
        else if (result.Status == DeviceProbeStatus.TrustRequired)
        {
            IsDeviceConnected = false;
            DeviceStatusText = App.GetString("MsgTrustRequired", "请解锁 iPhone 并点击“信任此电脑”");
        }
        else if (result.Status == DeviceProbeStatus.DriverServiceUnavailable)
        {
            IsDeviceConnected = false;
            DeviceStatusText = App.GetString("MsgDriverUnavailable", "Apple 驱动服务未运行 (请开启 iTunes)");
        }
        else
        {
            IsDeviceConnected = false;
            DeviceStatusText = App.GetString("DeviceDisconnected", "未检测到 iPhone (USB/AFC)");
        }
    }

    [RelayCommand]
    private void SwitchLanguage(string culture)
    {
        CurrentLanguage = culture;
        App.SwitchLanguage(culture);
        IPhoneSyncVM.OnLanguageChanged();
        AndroidSyncVM.OnLanguageChanged();
        GooglePhotosVM.OnLanguageChanged();
        SearchVM.OnLanguageChanged();
        SaveConfig();
        _ = ProbeDeviceStatusAsync();
    }

    public bool IsDetailModalOpen
    {
        get =>
            BackupVM.IsDetailModalOpen ||
            AndroidBackupVM.IsDetailModalOpen ||
            GooglePhotosVM.IsDetailModalOpen ||
            AndroidSyncVM.IsDetailModalOpen;
        set
        {
            if (!value)
            {
                CloseActiveDetailModal();
            }
        }
    }

    [ObservableProperty]
    private TransferItemDetail? selectedDetailItem;

    public string ActiveDetailModalTitle
    {
        get
        {
            if (AndroidBackupVM.IsDetailModalOpen) return AndroidBackupVM.DetailModalTitle;
            if (GooglePhotosVM.IsDetailModalOpen) return GooglePhotosVM.DetailModalTitle;
            if (AndroidSyncVM.IsDetailModalOpen) return AndroidSyncVM.DetailModalTitle;
            return BackupVM.DetailModalTitle;
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<TransferItemDetail> ActiveDetailItems
    {
        get
        {
            if (AndroidBackupVM.IsDetailModalOpen) return AndroidBackupVM.DetailItems;
            if (GooglePhotosVM.IsDetailModalOpen) return GooglePhotosVM.DetailItems;
            if (AndroidSyncVM.IsDetailModalOpen) return AndroidSyncVM.DetailItems;
            return BackupVM.DetailItems;
        }
    }

    public bool IsAnyModalOpen =>
        IsDetailModalOpen ||
        IsScopeModalOpen ||
        IsRenameModalOpen ||
        IsAndroidFtpModalOpen ||
        IsImageViewerOpen ||
        IsUnifiedManualModalOpen ||
        IPhoneSyncVM.IsPreflightModalOpen ||
        AndroidSyncVM.IsPreflightModalOpen ||
        IPhoneSyncVM.IsManualModalOpen ||
        AndroidSyncVM.IsManualModalOpen ||
        GooglePhotosVM.IsManualModalOpen;

    [RelayCommand]
    public void CloseActiveDetailModal()
    {
        BackupVM.CloseDetailModalCommand.Execute(null);
        AndroidBackupVM.CloseDetailModalCommand.Execute(null);
        GooglePhotosVM.CloseDetailModalCommand.Execute(null);
        AndroidSyncVM.CloseDetailModalCommand.Execute(null);
        OnPropertyChanged(nameof(IsDetailModalOpen));
        OnPropertyChanged(nameof(ActiveDetailModalTitle));
        OnPropertyChanged(nameof(ActiveDetailItems));
        OnPropertyChanged(nameof(IsAnyModalOpen));
    }

    [RelayCommand]
    private void OpenScopeModal()
    {
        IsScopeModalOpen = true;
        if (SelectedDeviceIndex == 1)
        {
            AndroidBackupVM.UpdateScopeSummarySentence();
            if (AndroidBackupVM.Albums.Count == 0 && !AndroidBackupVM.IsScanningAlbums)
            {
                _ = AndroidBackupVM.ScanAlbumsCommand.ExecuteAsync(null);
            }
        }
    }

    [RelayCommand]
    private void CloseScopeModal() => IsScopeModalOpen = false;

    // Image Viewer Commands
    [RelayCommand]
    public void CloseImageViewer()
    {
        IsImageViewerOpen = false;
        ViewerImageSource = null;
        ViewerImagePath = "";
    }

    [RelayCommand]
    public void OpenViewerInExplorer()
    {
        if (!string.IsNullOrEmpty(ViewerImagePath) && File.Exists(ViewerImagePath))
        {
            ShellServices.ShowInExplorer(new[] { ViewerImagePath });
        }
    }

    [RelayCommand]
    public void OpenViewerInExternalApp()
    {
        if (!string.IsNullOrEmpty(ViewerImagePath) && File.Exists(ViewerImagePath))
        {
            ShellServices.OpenFiles(new[] { ViewerImagePath });
        }
    }

    public void OpenImageViewer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            ViewerImagePath = path;
            ViewerImageTitle = Path.GetFileName(path);
            var fi = new FileInfo(path);
            ViewerImageDetails = $"{ByteSize.Humanize(fi.Length)} · {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            ViewerImageSource = bitmap;
            IsImageViewerOpen = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenImageViewer error: {ex.Message}");
            ShellServices.OpenFiles(new[] { path });
        }
    }

    // Unified Manual Selection Modal Commands
    [RelayCommand]
    public void OpenUnifiedManualModal()
    {
        IPhoneSyncVM.LoadManualSelectionsFromDb();
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
        OnPropertyChanged(nameof(UnifiedManualSelectedItems));
        IsUnifiedManualModalOpen = true;
    }

    [RelayCommand]
    public void CloseUnifiedManualModal() => IsUnifiedManualModalOpen = false;

    [RelayCommand]
    public void ClearUnifiedManualItems()
    {
        IPhoneSyncVM.ClearAllManualSelectionsCommand.Execute(null);
        AndroidSyncVM.ClearAllManualSelectionsCommand.Execute(null);
        GooglePhotosVM.ClearAllManualSelectionsCommand.Execute(null);
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
    }

    [RelayCommand]
    public void RemoveUnifiedManualItem(ManualSelectedItemViewModel? item)
    {
        if (item is null) return;
        IPhoneSyncVM.RemoveManualItemCommand.Execute(item);
        AndroidSyncVM.RemoveManualItemCommand.Execute(item);
        GooglePhotosVM.RemoveManualItemCommand.Execute(item);
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
    }

    [RelayCommand]
    public void PickFilesForUnifiedManual()
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "媒体文件|*.jpg;*.jpeg;*.png;*.heic;*.mp4;*.mov;*.dng;*.raw|所有文件|*.*"
        };
        if (ofd.ShowDialog() == true && ofd.FileNames.Length > 0)
        {
            AddFilesToUnifiedManual(ofd.FileNames);
        }
    }

    public void AddFilesToUnifiedManual(IEnumerable<string> filePaths)
    {
        var relPaths = new List<string>();
        foreach (var p in filePaths)
        {
            string rel = !string.IsNullOrWhiteSpace(DestinationPath) && p.StartsWith(DestinationPath, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(DestinationPath, p).Replace('\\', '/')
                : Path.GetFileName(p);
            relPaths.Add(rel);
        }

        SearchVM?.AddItemsToManualSelection(relPaths);
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
        OnPropertyChanged(nameof(UnifiedManualSelectedItems));
    }

    [RelayCommand]
    public void OpenRenameModal()
    {
        DeviceCustomName = SelectedDeviceIndex == 0 ? BackupVM.DeviceSubdir : AndroidBackupVM.DetectedDeviceModel;
        IsRenameModalOpen = true;
    }

    [RelayCommand]
    public void SaveRenameModal()
    {
        if (!string.IsNullOrWhiteSpace(DeviceCustomName))
        {
            string clean = DeviceCustomName.Trim();
            if (SelectedDeviceIndex == 0)
            {
                BackupVM.DeviceSubdir = clean;
                BackupVM.SetDetectedDevice(clean);
                IPhoneSyncVM.DeviceModel = clean;
            }
            else
            {
                AndroidBackupVM.DetectedDeviceModel = clean;
            }
        }
        IsRenameModalOpen = false;
    }

    [RelayCommand]
    public void CloseRenameModal() => IsRenameModalOpen = false;

    [RelayCommand]
    public async Task RefreshDeviceAsync()
    {
        if (SelectedDeviceIndex == 0)
        {
            await ProbeDeviceStatusAsync();
        }
        else
        {
            await AndroidBackupVM.TestConnectionCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    public void NavigateToAndroidSync()
    {
        SelectedTabIndex = 2;
        SyncSubTabIndex = 1;
    }

    [RelayCommand]
    public void OpenAndroidFtpModal() => IsAndroidFtpModalOpen = true;

    [RelayCommand]
    public void CloseAndroidFtpModal() => IsAndroidFtpModalOpen = false;

    [RelayCommand]
    public void OpenTaskManager()
    {
        OnPropertyChanged(nameof(ActiveTaskTitle));
        OnPropertyChanged(nameof(ActiveTaskProgressPercentage));
        OnPropertyChanged(nameof(ActiveTaskProgressPercentText));
        OnPropertyChanged(nameof(ActiveTaskSpeedText));
        OnPropertyChanged(nameof(ActiveTaskRemainingFilesText));
        OnPropertyChanged(nameof(ActiveTaskCurrentFileName));
        OnPropertyChanged(nameof(ActiveTaskStep2Status));
        OnPropertyChanged(nameof(ActiveTaskStep2Text));
        OnPropertyChanged(nameof(ActiveTaskStep3Status));
        OnPropertyChanged(nameof(ActiveTaskStep4Status));
        OnPropertyChanged(nameof(ActiveTaskCopiedCount));
        OnPropertyChanged(nameof(ActiveTaskSkippedCount));
        OnPropertyChanged(nameof(ActiveTaskFailedCount));
        IsTaskManagerOpen = true;
    }

    [RelayCommand]
    public void CloseTaskManager() => IsTaskManagerOpen = false;

    public int ActiveTaskCopiedCount
    {
        get
        {
            if (AndroidBackupVM.IsTransferring || (SelectedDeviceIndex == 1 && !BackupVM.IsTransferring)) return AndroidBackupVM.CopiedCount;
            if (GooglePhotosVM.IsSyncing) return GooglePhotosVM.UploadedCount;
            if (AndroidSyncVM.IsSyncing) return AndroidSyncVM.CopiedCount;
            return BackupVM.CopiedCount;
        }
    }

    public int ActiveTaskSkippedCount
    {
        get
        {
            if (AndroidBackupVM.IsTransferring || (SelectedDeviceIndex == 1 && !BackupVM.IsTransferring)) return AndroidBackupVM.SkippedCount;
            if (GooglePhotosVM.IsSyncing) return GooglePhotosVM.SkippedCount;
            if (AndroidSyncVM.IsSyncing) return AndroidSyncVM.SkippedCount;
            return BackupVM.SkippedCount;
        }
    }

    public int ActiveTaskFailedCount
    {
        get
        {
            if (AndroidBackupVM.IsTransferring || (SelectedDeviceIndex == 1 && !BackupVM.IsTransferring)) return AndroidBackupVM.FailedCount;
            if (GooglePhotosVM.IsSyncing) return GooglePhotosVM.FailedCount;
            if (AndroidSyncVM.IsSyncing) return AndroidSyncVM.FailedCount;
            return BackupVM.FailedCount;
        }
    }

    [RelayCommand]
    public void ShowActiveCopiedDetails()
    {
        if (AndroidBackupVM.IsTransferring || (SelectedDeviceIndex == 1 && !BackupVM.IsTransferring))
        {
            AndroidBackupVM.ShowCopiedDetailsCommand.Execute(null);
        }
        else if (GooglePhotosVM.IsSyncing)
        {
            GooglePhotosVM.ShowUploadedDetailsCommand.Execute(null);
        }
        else if (AndroidSyncVM.IsSyncing)
        {
            AndroidSyncVM.ShowCopiedDetailsCommand.Execute(null);
        }
        else
        {
            BackupVM.ShowCopiedDetailsCommand.Execute(null);
        }
        OnPropertyChanged(nameof(IsDetailModalOpen));
        OnPropertyChanged(nameof(ActiveDetailModalTitle));
        OnPropertyChanged(nameof(ActiveDetailItems));
        OnPropertyChanged(nameof(IsAnyModalOpen));
    }

    [RelayCommand]
    public void ShowActiveSkippedDetails()
    {
        if (AndroidBackupVM.IsTransferring || (SelectedDeviceIndex == 1 && !BackupVM.IsTransferring))
        {
            AndroidBackupVM.ShowSkippedDetailsCommand.Execute(null);
        }
        else if (GooglePhotosVM.IsSyncing)
        {
            GooglePhotosVM.ShowSkippedDetailsCommand.Execute(null);
        }
        else if (AndroidSyncVM.IsSyncing)
        {
            AndroidSyncVM.ShowSkippedDetailsCommand.Execute(null);
        }
        else
        {
            BackupVM.ShowSkippedDetailsCommand.Execute(null);
        }
        OnPropertyChanged(nameof(IsDetailModalOpen));
        OnPropertyChanged(nameof(ActiveDetailModalTitle));
        OnPropertyChanged(nameof(ActiveDetailItems));
        OnPropertyChanged(nameof(IsAnyModalOpen));
    }

    [RelayCommand]
    public void ShowActiveFailedDetails()
    {
        if (AndroidBackupVM.IsTransferring || (SelectedDeviceIndex == 1 && !BackupVM.IsTransferring))
        {
            AndroidBackupVM.ShowFailedDetailsCommand.Execute(null);
        }
        else if (GooglePhotosVM.IsSyncing)
        {
            GooglePhotosVM.ShowFailedDetailsCommand.Execute(null);
        }
        else if (AndroidSyncVM.IsSyncing)
        {
            AndroidSyncVM.ShowFailedDetailsCommand.Execute(null);
        }
        else
        {
            BackupVM.ShowFailedDetailsCommand.Execute(null);
        }
        OnPropertyChanged(nameof(IsDetailModalOpen));
        OnPropertyChanged(nameof(ActiveDetailModalTitle));
        OnPropertyChanged(nameof(ActiveDetailItems));
        OnPropertyChanged(nameof(IsAnyModalOpen));
    }

    [RelayCommand]
    public void CancelActiveTask()
    {
        if (AndroidBackupVM.IsTransferring) AndroidBackupVM.CancelBackupCommand.Execute(null);
        if (BackupVM.IsTransferring) BackupVM.StopBackupCommand.Execute(null);
        if (GooglePhotosVM.IsSyncing) GooglePhotosVM.CancelSyncCommand.Execute(null);
        if (IPhoneSyncVM.IsExporting) IPhoneSyncVM.CancelExportCommand.Execute(null);
        if (AndroidSyncVM.IsSyncing) AndroidSyncVM.CancelSyncCommand.Execute(null);
    }

    public bool IsAnyTaskRunning =>
        BackupVM.IsTransferring ||
        AndroidBackupVM.IsTransferring ||
        IPhoneSyncVM.IsExporting ||
        AndroidSyncVM.IsSyncing ||
        GooglePhotosVM.IsSyncing;

    public string ActiveTaskTitle
    {
        get
        {
            if (AndroidBackupVM.IsTransferring)
                return $"{(string.IsNullOrWhiteSpace(AndroidBackupVM.DetectedDeviceModel) ? "Android 设备" : AndroidBackupVM.DetectedDeviceModel)} 增量备份";
            if (BackupVM.IsTransferring)
                return $"{(string.IsNullOrWhiteSpace(BackupVM.DetectedDeviceModel) ? "iPhone 设备" : BackupVM.DetectedDeviceModel)} 增量备份";
            if (GooglePhotosVM.IsSyncing)
                return "Google 相册上传与同步";
            if (IPhoneSyncVM.IsExporting)
                return "iPhone 照片导出与回传";
            if (AndroidSyncVM.IsSyncing)
                return "Android 照片回传与同步";
            return "传输任务";
        }
    }

    public double ActiveTaskProgressPercentage
    {
        get
        {
            if (AndroidBackupVM.IsTransferring) return AndroidBackupVM.ProgressPercentage;
            if (BackupVM.IsTransferring) return BackupVM.ProgressPercentage;
            if (GooglePhotosVM.IsSyncing) return GooglePhotosVM.ProgressValue;
            if (IPhoneSyncVM.IsExporting) return IPhoneSyncVM.ProgressValue;
            if (AndroidSyncVM.IsSyncing) return AndroidSyncVM.ProgressValue;
            return 0;
        }
    }

    public string ActiveTaskProgressPercentText => $"{ActiveTaskProgressPercentage:F2} %";

    public string ActiveTaskSpeedText
    {
        get
        {
            if (AndroidBackupVM.IsTransferring) return string.IsNullOrWhiteSpace(AndroidBackupVM.SpeedText) ? "0.0 MB/s" : AndroidBackupVM.SpeedText;
            if (BackupVM.IsTransferring) return string.IsNullOrWhiteSpace(BackupVM.SpeedText) ? "0.0 MB/s" : BackupVM.SpeedText;
            if (GooglePhotosVM.IsSyncing) return string.IsNullOrWhiteSpace(GooglePhotosVM.SpeedText) ? "--" : GooglePhotosVM.SpeedText;
            if (IPhoneSyncVM.IsExporting) return string.IsNullOrWhiteSpace(IPhoneSyncVM.SpeedText) ? "--" : IPhoneSyncVM.SpeedText;
            if (AndroidSyncVM.IsSyncing) return string.IsNullOrWhiteSpace(AndroidSyncVM.SpeedText) ? "--" : AndroidSyncVM.SpeedText;
            return "0.0 MB/s";
        }
    }

    public string ActiveTaskRemainingFilesText
    {
        get
        {
            if (AndroidBackupVM.IsTransferring) return AndroidBackupVM.RemainingFilesCount > 0 ? AndroidBackupVM.RemainingFilesCount.ToString("N0") : (AndroidBackupVM.TotalFilesCount > 0 ? "0" : "--");
            if (BackupVM.IsTransferring) return BackupVM.RemainingFilesCount > 0 ? BackupVM.RemainingFilesCount.ToString("N0") : (BackupVM.TotalFilesCount > 0 ? "0" : "--");
            if (GooglePhotosVM.IsSyncing) return GooglePhotosVM.RemainingFilesCount > 0 ? GooglePhotosVM.RemainingFilesCount.ToString("N0") : "--";
            if (IPhoneSyncVM.IsExporting) return IPhoneSyncVM.RemainingFilesCount > 0 ? IPhoneSyncVM.RemainingFilesCount.ToString("N0") : "--";
            if (AndroidSyncVM.IsSyncing) return AndroidSyncVM.RemainingFilesCount > 0 ? AndroidSyncVM.RemainingFilesCount.ToString("N0") : "--";
            return "--";
        }
    }

    public string ActiveTaskCurrentFileName
    {
        get
        {
            if (AndroidBackupVM.IsTransferring) return AndroidBackupVM.CurrentFileName;
            if (BackupVM.IsTransferring) return BackupVM.CurrentFileName;
            if (GooglePhotosVM.IsSyncing) return GooglePhotosVM.CurrentFile ?? "";
            return "";
        }
    }

    public string ActiveTaskStep2Status => $"{ActiveTaskProgressPercentage:F0}%";

    public string ActiveTaskStep2Text
    {
        get
        {
            string fn = ActiveTaskCurrentFileName;
            if (!string.IsNullOrWhiteSpace(fn) && fn != "Ready" && fn != "Idle" && fn != "In progress..." && fn != "进行中...")
            {
                return $"传输媒体文件 ({fn})";
            }
            return "传输媒体文件";
        }
    }

    public string ActiveTaskStep3Status => ActiveTaskProgressPercentage >= 95 ? "✓" : (ActiveTaskProgressPercentage >= 80 ? $"{ActiveTaskProgressPercentage:F0}%" : "•••");
    public string ActiveTaskStep4Status => ActiveTaskProgressPercentage >= 100 ? "✓" : "•••";

    [RelayCommand]
    private void NavigateToSettings()
    {
        SelectedTabIndex = 3;
    }
}
