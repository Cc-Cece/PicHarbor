using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
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
    private CancellationTokenSource? manualThumbnailCts;

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

    public ObservableCollection<TransferTaskCardViewModel> TaskCards { get; } = new();

    public bool HasVisibleTaskCard => TaskCards.Any(card => card.IsShown);

    public bool ShowTaskBall => SelectedTabIndex == 0 || HasVisibleTaskCard;

    public string TotalSpeedText
    {
        get
        {
            double bytes = 0;
            if (BackupVM.IsTransferring) bytes += BackupVM.SpeedBytesPerSecond;
            if (AndroidBackupVM.IsTransferring) bytes += AndroidBackupVM.SpeedBytesPerSecond;
            if (GooglePhotosVM.IsSyncing) bytes += GooglePhotosVM.SpeedBytesPerSecond;
            if (IPhoneSyncVM.IsExporting) bytes += IPhoneSyncVM.SpeedBytesPerSecond;
            if (AndroidSyncVM.IsSyncing) bytes += AndroidSyncVM.SpeedBytesPerSecond;
            return FormatTotalSpeed(bytes);
        }
    }

    public string TotalRemainingFilesText
    {
        get
        {
            int remaining = 0;
            bool any = false;
            if (BackupVM.IsTransferring)
            {
                any = true;
                remaining += Math.Max(0, BackupVM.RemainingFilesCount);
            }
            if (AndroidBackupVM.IsTransferring)
            {
                any = true;
                remaining += Math.Max(0, AndroidBackupVM.RemainingFilesCount);
            }
            if (GooglePhotosVM.IsSyncing)
            {
                any = true;
                remaining += Math.Max(0, GooglePhotosVM.RemainingFilesCount);
            }
            if (IPhoneSyncVM.IsExporting)
            {
                any = true;
                remaining += Math.Max(0, IPhoneSyncVM.RemainingFilesCount);
            }
            if (AndroidSyncVM.IsSyncing)
            {
                any = true;
                remaining += Math.Max(0, AndroidSyncVM.RemainingFilesCount);
            }

            return any ? remaining.ToString("N0") : "--";
        }
    }

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
        SettingsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.HeifInstalled)
                or nameof(SettingsViewModel.HevcInstalled)
                or nameof(SettingsViewModel.CodecProbeCompleted))
            {
                SearchVM.ShowAppleCodecNotice = SettingsVM.CodecMissing;
            }
        };

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
            if (e.PropertyName == nameof(SettingsViewModel.GooglePhotosPythonPath)) GooglePhotosVM.PythonPath = SettingsVM.GooglePhotosPythonPath;
            if (e.PropertyName == nameof(SettingsViewModel.GooglePhotosThreads)) GooglePhotosVM.Threads = SettingsVM.GooglePhotosThreads;
            if (e.PropertyName == nameof(SettingsViewModel.GooglePhotosTimeoutSeconds)) GooglePhotosVM.TimeoutSeconds = SettingsVM.GooglePhotosTimeoutSeconds;
            if (e.PropertyName == nameof(SettingsViewModel.GooglePhotosAutoRetryAttempts)) GooglePhotosVM.AutoRetryAttempts = SettingsVM.GooglePhotosAutoRetryAttempts;
            if (e.PropertyName == nameof(SettingsViewModel.SelectedGooglePhotosQualityIndex))
            {
                GooglePhotosVM.UnlimitedQuality = SettingsVM.SelectedGooglePhotosQualityIndex == 0;
                GooglePhotosVM.StorageSaver = SettingsVM.SelectedGooglePhotosQualityIndex == 2;
            }
            if (e.PropertyName == nameof(SettingsViewModel.GooglePhotosSkipExistingFilenames))
            {
                GooglePhotosVM.SkipExistingFilenames = SettingsVM.GooglePhotosSkipExistingFilenames;
            }
        };

        CreateTaskCards();



        BackupVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(BackupViewModel.IsDetailModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(BackupViewModel.IsTransferring) or nameof(BackupViewModel.ProgressPercentage) or nameof(BackupViewModel.SpeedText) or nameof(BackupViewModel.RemainingFilesCount) or nameof(BackupViewModel.CurrentFileName) or nameof(BackupViewModel.CopiedCount) or nameof(BackupViewModel.SkippedCount) or nameof(BackupViewModel.FailedCount))
            {
                NotifyTaskChanged();
                if (e.PropertyName == nameof(BackupViewModel.IsTransferring) && !BackupVM.IsTransferring)
                {
                    OnTransferFinished();
                }
            }
        };
        AndroidBackupVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AndroidBackupViewModel.IsDetailModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(AndroidBackupViewModel.IsTransferring) or nameof(AndroidBackupViewModel.ProgressPercentage) or nameof(AndroidBackupViewModel.SpeedText) or nameof(AndroidBackupViewModel.RemainingFilesCount) or nameof(AndroidBackupViewModel.CurrentFileName) or nameof(AndroidBackupViewModel.CopiedCount) or nameof(AndroidBackupViewModel.SkippedCount) or nameof(AndroidBackupViewModel.FailedCount))
            {
                NotifyTaskChanged();
                if (e.PropertyName == nameof(AndroidBackupViewModel.IsTransferring) && !AndroidBackupVM.IsTransferring)
                {
                    OnTransferFinished();
                }
            }
        };
        GooglePhotosVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(GooglePhotosSyncViewModel.IsDetailModalOpen) or nameof(GooglePhotosSyncViewModel.IsManualModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(GooglePhotosSyncViewModel.IsSyncing) or nameof(GooglePhotosSyncViewModel.ProgressValue) or nameof(GooglePhotosSyncViewModel.ProgressText) or nameof(GooglePhotosSyncViewModel.SpeedText) or nameof(GooglePhotosSyncViewModel.RemainingFilesCount) or nameof(GooglePhotosSyncViewModel.CurrentFile) or nameof(GooglePhotosSyncViewModel.UploadedCount) or nameof(GooglePhotosSyncViewModel.SkippedCount) or nameof(GooglePhotosSyncViewModel.FailedCount))
            {
                NotifyTaskChanged();
                if (e.PropertyName == nameof(GooglePhotosSyncViewModel.IsSyncing) && !GooglePhotosVM.IsSyncing)
                {
                    OnTransferFinished();
                }
            }
        };
        AndroidSyncVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(AndroidSyncViewModel.IsDetailModalOpen) or nameof(AndroidSyncViewModel.IsPreflightModalOpen) or nameof(AndroidSyncViewModel.IsManualModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(AndroidSyncViewModel.IsSyncing) or nameof(AndroidSyncViewModel.ProgressValue) or nameof(AndroidSyncViewModel.ProgressText) or nameof(AndroidSyncViewModel.SpeedText) or nameof(AndroidSyncViewModel.RemainingFilesCount) or nameof(AndroidSyncViewModel.CopiedCount) or nameof(AndroidSyncViewModel.SkippedCount) or nameof(AndroidSyncViewModel.FailedCount))
            {
                NotifyTaskChanged();
                if (e.PropertyName == nameof(AndroidSyncViewModel.IsSyncing) && !AndroidSyncVM.IsSyncing)
                {
                    OnTransferFinished();
                }
            }
        };
        IPhoneSyncVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(IPhoneSyncViewModel.IsPreflightModalOpen) or nameof(IPhoneSyncViewModel.IsManualModalOpen)) NotifyModalChanged();
            if (e.PropertyName is nameof(IPhoneSyncViewModel.IsExporting) or nameof(IPhoneSyncViewModel.ProgressValue) or nameof(IPhoneSyncViewModel.ProgressText) or nameof(IPhoneSyncViewModel.SpeedText) or nameof(IPhoneSyncViewModel.RemainingFilesCount) or nameof(IPhoneSyncViewModel.CopiedCount) or nameof(IPhoneSyncViewModel.SkippedCount) or nameof(IPhoneSyncViewModel.FailedCount))
            {
                NotifyTaskChanged();
                if (e.PropertyName == nameof(IPhoneSyncViewModel.IsExporting) && !IPhoneSyncVM.IsExporting)
                {
                    OnTransferFinished();
                }
            }
        };

        // Wire unified manual selection modal across all devices
        AndroidBackupVM.OpenManualModalAction = () => OpenUnifiedManualModal();
        IPhoneSyncVM.OpenManualModalAction = () => OpenUnifiedManualModal();
        AndroidSyncVM.OpenManualModalAction = () => OpenUnifiedManualModal();
        GooglePhotosVM.OpenManualModalAction = () => OpenUnifiedManualModal();
        IPhoneSyncVM.ManualSelectedItems.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
            IPhoneSyncVM.UpdateManualSelectionTexts();
            AndroidSyncVM.SyncWithUnifiedManualSelections(IPhoneSyncVM.ManualSelectedItems);
            GooglePhotosVM.SyncWithUnifiedManualSelections(IPhoneSyncVM.ManualSelectedItems);
            SearchVM?.NotifyManualSelectionsChanged();
        };
        IPhoneSyncVM.ManualSelectionsReloaded += (_, _) => RequestUnifiedManualThumbnails();

        // Initial sync of selections from db
        IPhoneSyncVM.LoadManualSelectionsFromDb();
        AndroidSyncVM.SyncWithUnifiedManualSelections(IPhoneSyncVM.ManualSelectedItems);
        GooglePhotosVM.SyncWithUnifiedManualSelections(IPhoneSyncVM.ManualSelectedItems);

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

    private void OnTransferFinished()
    {
        _ = StatusVM.RefreshStatsCommand.ExecuteAsync(null);
        BackupVM.RefreshDriveSpace();
        AndroidBackupVM.RefreshDriveSpace();
        if (SelectedTabIndex == 3 && SettingsSubTabIndex == 3)
        {
            _ = SettingsVM.RefreshDbMetricsCommand.ExecuteAsync(null);
        }
    }

    public void NotifyModalChanged()
    {
        OnPropertyChanged(nameof(IsDetailModalOpen));
        OnPropertyChanged(nameof(ActiveDetailModalTitle));
        OnPropertyChanged(nameof(ActiveDetailItems));
        OnPropertyChanged(nameof(IsAnyModalOpen));
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowTaskBall));
        if (value == 0)
        {
            _ = StatusVM.RefreshStatsCommand.ExecuteAsync(null);
            BackupVM.RefreshDriveSpace();
            AndroidBackupVM.RefreshDriveSpace();
        }
        else if (value == 3 && SettingsSubTabIndex == 3)
        {
            _ = SettingsVM.RefreshDbMetricsCommand.ExecuteAsync(null);
        }
    }

    partial void OnBackupSubTabIndexChanged(int value)
    {
        _ = StatusVM.RefreshStatsCommand.ExecuteAsync(null);
        BackupVM.RefreshDriveSpace();
        AndroidBackupVM.RefreshDriveSpace();
    }

    partial void OnSettingsSubTabIndexChanged(int value)
    {
        if (value == 3 && SelectedTabIndex == 3)
        {
            _ = SettingsVM.RefreshDbMetricsCommand.ExecuteAsync(null);
        }
    }

    partial void OnSelectedDeviceIndexChanged(int value)
    {
        _ = StatusVM.RefreshStatsCommand.ExecuteAsync(null);
        BackupVM.RefreshDriveSpace();
        AndroidBackupVM.RefreshDriveSpace();
    }

    public void NotifyTaskChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(NotifyTaskChanged);
            return;
        }

        RefreshTaskCards();
        OnPropertyChanged(nameof(IsAnyTaskRunning));
        OnPropertyChanged(nameof(TotalSpeedText));
        OnPropertyChanged(nameof(TotalRemainingFilesText));
        OnPropertyChanged(nameof(HasVisibleTaskCard));
        OnPropertyChanged(nameof(ShowTaskBall));
        OnPropertyChanged(nameof(HasPreviousTaskRun));
        OnPropertyChanged(nameof(HasActiveOrPreviousTask));
        OnPropertyChanged(nameof(ShowEmptyTaskWaitingPrompt));
        OnPropertyChanged(nameof(ActiveTaskTitle));
        OnPropertyChanged(nameof(ActiveTaskProgressPercentage));
        OnPropertyChanged(nameof(ActiveTaskProgressPercentText));
        OnPropertyChanged(nameof(ActiveTaskSpeedText));
        OnPropertyChanged(nameof(ActiveTaskRemainingFilesText));
        OnPropertyChanged(nameof(ActiveTaskCurrentFileName));
        OnPropertyChanged(nameof(ActiveTaskStep1Status));
        OnPropertyChanged(nameof(ActiveTaskStep1Text));
        OnPropertyChanged(nameof(ActiveTaskStep2Status));
        OnPropertyChanged(nameof(ActiveTaskStep2Text));
        OnPropertyChanged(nameof(ActiveTaskStep3Status));
        OnPropertyChanged(nameof(ActiveTaskStep3Text));
        OnPropertyChanged(nameof(ActiveTaskStep4Status));
        OnPropertyChanged(nameof(ActiveTaskStep4Text));
        OnPropertyChanged(nameof(ActiveTaskCopiedCount));
        OnPropertyChanged(nameof(ActiveTaskSkippedCount));
        OnPropertyChanged(nameof(ActiveTaskFailedCount));
    }

    [RelayCommand]
    private void OpenCodecSettings()
    {
        SelectedTabIndex = 3;
        SettingsSubTabIndex = 1;
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
            BackupVM.RefreshDriveSpace();
            AndroidBackupVM.RefreshDriveSpace();
            if (SelectedTabIndex == 3 && SettingsSubTabIndex == 3)
            {
                _ = SettingsVM.RefreshDbMetricsCommand.ExecuteAsync(null);
            }
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
        AndroidSyncVM.SyncWithUnifiedManualSelections(IPhoneSyncVM.ManualSelectedItems);
        GooglePhotosVM.SyncWithUnifiedManualSelections(IPhoneSyncVM.ManualSelectedItems);
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
        OnPropertyChanged(nameof(UnifiedManualSelectedItems));
        SearchVM?.NotifyManualSelectionsChanged();
        IsUnifiedManualModalOpen = true;
        RequestUnifiedManualThumbnails();
    }

    [RelayCommand]
    public void CloseUnifiedManualModal()
    {
        IsUnifiedManualModalOpen = false;
        manualThumbnailCts?.Cancel();
    }

    private void RequestUnifiedManualThumbnails()
    {
        if (!IsUnifiedManualModalOpen)
        {
            return;
        }

        manualThumbnailCts?.Cancel();
        manualThumbnailCts = new CancellationTokenSource();
        _ = FillUnifiedManualThumbnailsAsync(manualThumbnailCts.Token);
    }

    private async Task FillUnifiedManualThumbnailsAsync(CancellationToken cancellationToken)
    {
        List<ManualSelectedItemViewModel> pending = new();
        foreach (ManualSelectedItemViewModel item in IPhoneSyncVM.ManualSelectedItems.ToList())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (item.ThumbnailImage is not null || string.IsNullOrWhiteSpace(item.FullPath))
            {
                continue;
            }

            ImageSource? known = SearchVM.FindLoadedThumbnail(item.FullPath);
            if (known is not null)
            {
                item.ThumbnailImage = known;
                continue;
            }

            if (File.Exists(item.FullPath))
            {
                pending.Add(item);
            }
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 6),
            CancellationToken = cancellationToken
        };

        try
        {
            await Parallel.ForEachAsync(pending, parallelOptions, async (item, token) =>
            {
                ImageSource? thumb = await SearchViewModel.CreateListThumbnailAsync(item.FullPath, token).ConfigureAwait(false);
                if (thumb is null || token.IsCancellationRequested || Application.Current is null)
                {
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        item.ThumbnailImage = thumb;
                    }
                }, DispatcherPriority.Background);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public void ClearUnifiedManualItems()
    {
        IPhoneSyncVM.ClearAllManualSelectionsCommand.Execute(null);
        AndroidSyncVM.ClearAllManualSelectionsCommand.Execute(null);
        GooglePhotosVM.ClearAllManualSelectionsCommand.Execute(null);
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
        SearchVM?.NotifyManualSelectionsChanged();
    }

    [RelayCommand]
    public void RemoveUnifiedManualItem(ManualSelectedItemViewModel? item)
    {
        if (item is null) return;
        IPhoneSyncVM.RemoveManualItemCommand.Execute(item);
        AndroidSyncVM.RemoveManualItemCommand.Execute(item);
        GooglePhotosVM.RemoveManualItemCommand.Execute(item);
        OnPropertyChanged(nameof(UnifiedManualSelectionCountText));
        SearchVM?.NotifyManualSelectionsChanged();
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
        NotifyTaskChanged();
        IsTaskManagerOpen = true;
    }

    private void CreateTaskCards()
    {
        TaskCards.Add(MakeCard(
            "iphone-backup",
            true,
            () => { if (BackupVM.IsTransferring) BackupVM.StopBackupCommand.Execute(null); },
            () => BackupVM.ShowCopiedDetailsCommand.Execute(null),
            () => BackupVM.ShowSkippedDetailsCommand.Execute(null),
            () => BackupVM.ShowFailedDetailsCommand.Execute(null)));
        TaskCards.Add(MakeCard(
            "android-backup",
            true,
            () => { if (AndroidBackupVM.IsTransferring) AndroidBackupVM.CancelBackupCommand.Execute(null); },
            () => AndroidBackupVM.ShowCopiedDetailsCommand.Execute(null),
            () => AndroidBackupVM.ShowSkippedDetailsCommand.Execute(null),
            () => AndroidBackupVM.ShowFailedDetailsCommand.Execute(null)));
        TaskCards.Add(MakeCard(
            "google",
            true,
            () => { if (GooglePhotosVM.IsSyncing) GooglePhotosVM.CancelSyncCommand.Execute(null); },
            () => GooglePhotosVM.ShowUploadedDetailsCommand.Execute(null),
            () => GooglePhotosVM.ShowSkippedDetailsCommand.Execute(null),
            () => GooglePhotosVM.ShowFailedDetailsCommand.Execute(null)));
        TaskCards.Add(MakeCard(
            "iphone-sync",
            false,
            () => { if (IPhoneSyncVM.IsExporting) IPhoneSyncVM.CancelExportCommand.Execute(null); },
            null,
            null,
            null));
        TaskCards.Add(MakeCard(
            "android-sync",
            true,
            () => { if (AndroidSyncVM.IsSyncing) AndroidSyncVM.CancelSyncCommand.Execute(null); },
            () => AndroidSyncVM.ShowCopiedDetailsCommand.Execute(null),
            () => AndroidSyncVM.ShowSkippedDetailsCommand.Execute(null),
            () => AndroidSyncVM.ShowFailedDetailsCommand.Execute(null)));
    }

    private TransferTaskCardViewModel MakeCard(
        string key,
        bool hasDetails,
        Action stop,
        Action? copied,
        Action? skipped,
        Action? failed)
    {
        TransferTaskCardViewModel card = null!;
        card = new TransferTaskCardViewModel(
            key,
            hasDetails,
            stop,
            copied is null ? null : () => { copied(); NotifyModalChanged(); },
            skipped is null ? null : () => { skipped(); NotifyModalChanged(); },
            failed is null ? null : () => { failed(); NotifyModalChanged(); },
            () => DismissTaskCard(card));
        return card;
    }

    private void DismissTaskCard(TransferTaskCardViewModel card)
    {
        if (card.IsRunning)
        {
            return;
        }

        card.Dismissed = true;
        card.IsShown = false;
        OnPropertyChanged(nameof(HasVisibleTaskCard));
        OnPropertyChanged(nameof(ShowTaskBall));
    }

    private void RefreshTaskCards()
    {
        if (TaskCards.Count < 5)
        {
            return;
        }

        string iphoneName = string.IsNullOrWhiteSpace(BackupVM.DetectedDeviceModel) ? "iPhone" : BackupVM.DetectedDeviceModel;
        string androidName = string.IsNullOrWhiteSpace(AndroidBackupVM.DetectedDeviceModel) ? "Android" : AndroidBackupVM.DetectedDeviceModel;
        ApplyCard(TaskCards[0], BackupVM.IsTransferring, $"{iphoneName} 备份", BackupVM.ProgressPercentage, BackupVM.ProgressText, BackupVM.SpeedText, BackupVM.CopiedCount, BackupVM.SkippedCount, BackupVM.FailedCount, "已复制");
        ApplyCard(TaskCards[1], AndroidBackupVM.IsTransferring, $"{androidName} 备份", AndroidBackupVM.ProgressPercentage, AndroidBackupVM.ProgressText, AndroidBackupVM.SpeedText, AndroidBackupVM.CopiedCount, AndroidBackupVM.SkippedCount, AndroidBackupVM.FailedCount, "已复制");
        ApplyCard(TaskCards[2], GooglePhotosVM.IsSyncing, "Google 相册", GooglePhotosVM.ProgressValue, GooglePhotosVM.ProgressText, GooglePhotosVM.SpeedText, GooglePhotosVM.UploadedCount, GooglePhotosVM.SkippedCount, GooglePhotosVM.FailedCount, "已上传");
        ApplyCard(TaskCards[3], IPhoneSyncVM.IsExporting, "同步到 iPhone", IPhoneSyncVM.ProgressValue, IPhoneSyncVM.ProgressText, IPhoneSyncVM.SpeedText, IPhoneSyncVM.CopiedCount, IPhoneSyncVM.SkippedCount, IPhoneSyncVM.FailedCount, "已复制");
        ApplyCard(TaskCards[4], AndroidSyncVM.IsSyncing, "同步到 Android", AndroidSyncVM.ProgressValue, AndroidSyncVM.ProgressText, AndroidSyncVM.SpeedText, AndroidSyncVM.CopiedCount, AndroidSyncVM.SkippedCount, AndroidSyncVM.FailedCount, "已复制");
        OnPropertyChanged(nameof(HasVisibleTaskCard));
        OnPropertyChanged(nameof(ShowTaskBall));
    }

    private static void ApplyCard(
        TransferTaskCardViewModel card,
        bool running,
        string title,
        double progress,
        string progressText,
        string speedText,
        int copied,
        int skipped,
        int failed,
        string copiedLabel)
    {
        if (running)
        {
            card.HasRun = true;
            card.Dismissed = false;
        }

        card.IsRunning = running;
        card.IsShown = running || (card.HasRun && !card.Dismissed);
        card.Title = title;
        card.Progress = progress;
        card.ProgressText = string.IsNullOrWhiteSpace(progressText) ? "" : progressText;
        card.SpeedText = running ? (string.IsNullOrWhiteSpace(speedText) ? "--" : speedText) : "--";
        card.StateText = running ? "进行中" : failed > 0 ? "已结束，有失败" : "已结束";
        card.ResultText = $"{copiedLabel} {copied:N0} · 已跳过 {skipped:N0} · 失败 {failed:N0}";
    }

    private static string FormatTotalSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0.0 MB/s";
        }

        double megabytes = bytesPerSecond / 1024d / 1024d;
        if (megabytes >= 0.05)
        {
            return $"{megabytes:F1} MB/s";
        }

        return $"{bytesPerSecond / 1024d:F0} KB/s";
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

    public bool HasPreviousTaskRun =>
        ActiveTaskCopiedCount > 0 ||
        ActiveTaskSkippedCount > 0 ||
        ActiveTaskFailedCount > 0;

    public bool ShowEmptyTaskWaitingPrompt => !IsAnyTaskRunning && !HasPreviousTaskRun;

    public bool HasActiveOrPreviousTask => IsAnyTaskRunning || HasPreviousTaskRun;

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
            if (IsAnyTaskRunning)
            {
                if (AndroidBackupVM.IsTransferring)
                    return $"{(string.IsNullOrWhiteSpace(AndroidBackupVM.DetectedDeviceModel) ? "Android 设备" : AndroidBackupVM.DetectedDeviceModel)} 增量备份 (进行中)";
                if (BackupVM.IsTransferring)
                    return $"{(string.IsNullOrWhiteSpace(BackupVM.DetectedDeviceModel) ? "iPhone 设备" : BackupVM.DetectedDeviceModel)} 增量备份 (进行中)";
                if (GooglePhotosVM.IsSyncing)
                    return "Google 相册上传与同步 (进行中)";
                if (IPhoneSyncVM.IsExporting)
                    return "iPhone 照片导出与回传 (进行中)";
                if (AndroidSyncVM.IsSyncing)
                    return "Android 照片回传与同步 (进行中)";
                return "传输任务 (进行中)";
            }

            if (HasPreviousTaskRun)
            {
                if (SelectedDeviceIndex == 1 || AndroidBackupVM.CopiedCount > 0 || AndroidBackupVM.SkippedCount > 0)
                {
                    string model = string.IsNullOrWhiteSpace(AndroidBackupVM.DetectedDeviceModel) ? "Android 设备" : AndroidBackupVM.DetectedDeviceModel;
                    return $"{model} 增量备份 (已完成)";
                }
                string iphoneModel = string.IsNullOrWhiteSpace(BackupVM.DetectedDeviceModel) ? "iPhone 设备" : BackupVM.DetectedDeviceModel;
                return $"{iphoneModel} 增量备份 (已完成)";
            }

            return "等待开启新任务";
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
            if (HasPreviousTaskRun) return 100.0;
            return 0;
        }
    }

    public string ActiveTaskProgressPercentText
    {
        get
        {
            if (IsAnyTaskRunning)
                return $"{ActiveTaskProgressPercentage:F2} %";
            if (HasPreviousTaskRun)
                return "100.00 %";
            return "--";
        }
    }

    public string ActiveTaskSpeedText
    {
        get
        {
            if (AndroidBackupVM.IsTransferring) return string.IsNullOrWhiteSpace(AndroidBackupVM.SpeedText) ? "0.0 MB/s" : AndroidBackupVM.SpeedText;
            if (BackupVM.IsTransferring) return string.IsNullOrWhiteSpace(BackupVM.SpeedText) ? "0.0 MB/s" : BackupVM.SpeedText;
            if (GooglePhotosVM.IsSyncing) return string.IsNullOrWhiteSpace(GooglePhotosVM.SpeedText) ? "--" : GooglePhotosVM.SpeedText;
            if (IPhoneSyncVM.IsExporting) return string.IsNullOrWhiteSpace(IPhoneSyncVM.SpeedText) ? "--" : IPhoneSyncVM.SpeedText;
            if (AndroidSyncVM.IsSyncing) return string.IsNullOrWhiteSpace(AndroidSyncVM.SpeedText) ? "--" : AndroidSyncVM.SpeedText;
            if (HasPreviousTaskRun) return "已完成";
            return "--";
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
            if (HasPreviousTaskRun) return "0";
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

    public string ActiveTaskStep1Status => (IsAnyTaskRunning || HasPreviousTaskRun) ? "✓" : "•••";

    public string ActiveTaskStep1Text => (IsAnyTaskRunning || HasPreviousTaskRun) ? "扫描媒体与相册清单 (完成)" : "扫描媒体与相册清单";

    public string ActiveTaskStep2Status
    {
        get
        {
            if (IsAnyTaskRunning) return $"{ActiveTaskProgressPercentage:F0}%";
            if (HasPreviousTaskRun) return "✓";
            return "•••";
        }
    }

    public string ActiveTaskStep2Text
    {
        get
        {
            if (IsAnyTaskRunning)
            {
                string fn = ActiveTaskCurrentFileName;
                if (!string.IsNullOrWhiteSpace(fn) && fn != "Ready" && fn != "Idle" && fn != "In progress..." && fn != "进行中...")
                {
                    return $"传输媒体文件 ({fn})";
                }
                return "传输媒体文件";
            }
            if (HasPreviousTaskRun) return "传输媒体文件 (全部传输完毕)";
            return "传输媒体文件 (等待启动)";
        }
    }

    public string ActiveTaskStep3Status
    {
        get
        {
            if (IsAnyTaskRunning) return ActiveTaskProgressPercentage >= 95 ? "✓" : (ActiveTaskProgressPercentage >= 80 ? $"{ActiveTaskProgressPercentage:F0}%" : "•••");
            if (HasPreviousTaskRun) return "✓";
            return "•••";
        }
    }

    public string ActiveTaskStep3Text => HasPreviousTaskRun ? "EXIF 拍摄时间与元数据校准 (完成)" : "EXIF 拍摄时间与元数据校准";

    public string ActiveTaskStep4Status
    {
        get
        {
            if (IsAnyTaskRunning) return ActiveTaskProgressPercentage >= 100 ? "✓" : "•••";
            if (HasPreviousTaskRun) return "✓";
            return "•••";
        }
    }

    public string ActiveTaskStep4Text => HasPreviousTaskRun ? "更新 SQLite 媒体库索引 (完成)" : "更新 SQLite 媒体库索引";

    [RelayCommand]
    private void NavigateToSettings()
    {
        SelectedTabIndex = 3;
    }
}
