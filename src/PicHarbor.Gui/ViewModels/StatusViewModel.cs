using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;

namespace PicHarbor.Gui.ViewModels;

public class DeviceHistoryItem
{
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Udid { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
    public string? HardwareSerial { get; set; }
    public string? DeviceType { get; set; }
}

public partial class StatusViewModel : ObservableObject
{
    [ObservableProperty]
    private int totalFiles = 0;

    [ObservableProperty]
    private int itemCount = 0;

    [ObservableProperty]
    private string totalSizeText = "0 B";

    [ObservableProperty]
    private int photosCount = 0;

    [ObservableProperty]
    private int videosCount = 0;

    [ObservableProperty]
    private string lastBackupTime = "N/A";

    [ObservableProperty]
    private string databasePath = MainViewModel.DefaultArchivePath;

    public ObservableCollection<DeviceHistoryItem> Devices { get; } = new();

    [RelayCommand]
    public void OpenDeviceHistory(DeviceHistoryItem? item)
    {
        if (item is null) return;
        var dialog = new DeviceBackupHistoryDialog(DatabasePath, item);
        dialog.Owner = Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) || !Directory.Exists(DatabasePath))
        {
            return;
        }

        try
        {
            ArchiveSummaryStats stats = await ArchiveRepository.GetStatsAsync(DatabasePath).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TotalFiles = stats.TotalFiles;
                ItemCount = stats.ItemCount;
                TotalSizeText = ByteSize.Humanize(stats.TotalBytes);
                PhotosCount = stats.PhotosCount;
                VideosCount = stats.VideosCount;
                LastBackupTime = stats.LastBackupTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";

                Devices.Clear();
                foreach (DeviceRecord dev in stats.Devices)
                {
                    Devices.Add(new DeviceHistoryItem
                    {
                        Name = dev.Name ?? "Unknown Device",
                        Model = dev.Model ?? "Unknown Model",
                        Udid = dev.Udid,
                        LastSeen = dev.LastSeen?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A",
                        HardwareSerial = dev.HardwareSerial,
                        DeviceType = dev.DeviceType
                    });
                }
            });
        }
        catch (FileNotFoundException)
        {
            // Archive database not created yet
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading archive stats: {ex.Message}");
        }
    }
}
