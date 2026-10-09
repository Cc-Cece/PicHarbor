using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Device;
using PicHarbor.Gui.Config;

using PicHarbor.Core.Storage;

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

        BackupVM.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(BackupViewModel.IsDetailModalOpen)) NotifyModalChanged(); };
        AndroidBackupVM.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(AndroidBackupViewModel.IsDetailModalOpen)) NotifyModalChanged(); };
        GooglePhotosVM.PropertyChanged += (s, e) => { if (e.PropertyName is nameof(GooglePhotosSyncViewModel.IsDetailModalOpen) or nameof(GooglePhotosSyncViewModel.IsManualModalOpen)) NotifyModalChanged(); };
        AndroidSyncVM.PropertyChanged += (s, e) => { if (e.PropertyName is nameof(AndroidSyncViewModel.IsDetailModalOpen) or nameof(AndroidSyncViewModel.IsPreflightModalOpen) or nameof(AndroidSyncViewModel.IsManualModalOpen)) NotifyModalChanged(); };
        IPhoneSyncVM.PropertyChanged += (s, e) => { if (e.PropertyName is nameof(IPhoneSyncViewModel.IsPreflightModalOpen) or nameof(IPhoneSyncViewModel.IsManualModalOpen)) NotifyModalChanged(); };

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
    private void OpenScopeModal() => IsScopeModalOpen = true;

    [RelayCommand]
    private void CloseScopeModal() => IsScopeModalOpen = false;

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
    private void NavigateToSettings()
    {
        SelectedTabIndex = 3;
    }
}
