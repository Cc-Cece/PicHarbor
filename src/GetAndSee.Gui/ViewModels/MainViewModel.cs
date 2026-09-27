using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Device;
using GetAndSee.Gui.Config;

namespace GetAndSee.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer deviceProbeTimer;

    [ObservableProperty]
    private int selectedTabIndex = 0;

    [ObservableProperty]
    private bool isDeviceConnected = false;

    [ObservableProperty]
    private string deviceStatusText = "未检测到 iPhone (USB/AFC)";

    public static string DefaultArchivePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GetAndSeeArchive");

    [ObservableProperty]
    private string destinationPath = DefaultArchivePath;

    [ObservableProperty]
    private string currentLanguage = "zh-CN";

    public BackupViewModel BackupVM { get; }
    public IPhoneSyncViewModel IPhoneSyncVM { get; } = new();
    public AndroidSyncViewModel AndroidSyncVM { get; } = new();
    public StatusViewModel StatusVM { get; } = new();
    public SearchViewModel SearchVM { get; } = new();
    public ReorganizeViewModel ReorganizeVM { get; } = new();
    public SettingsViewModel SettingsVM { get; } = new();

    public MainViewModel()
    {
        BackupVM = new BackupViewModel(OnDestinationPathUpdatedFromBackup);
        
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
        IPhoneSyncVM.ArchivePath = path;
        AndroidSyncVM.ArchivePath = path;
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
        SaveConfig();
        _ = ProbeDeviceStatusAsync();
    }
}
