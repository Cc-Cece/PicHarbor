using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui;

public class SessionItemView
{
    public long Id { get; init; }
    public string FormattedStartedAt { get; init; } = string.Empty;
    public int FilesCount { get; init; }
    public string FormattedSize { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public class HistoryFileItemView
{
    public string FileName { get; init; } = string.Empty;
    public string DeviceSourcePath { get; init; } = string.Empty;
    public string DestPath { get; init; } = string.Empty;
    public string FormattedSize { get; init; } = string.Empty;
    public string FormattedTransferredAt { get; init; } = string.Empty;
}

public partial class DeviceBackupHistoryDialog : Window
{
    private readonly string destinationRoot;
    private readonly DeviceHistoryItem deviceItem;
    private readonly ObservableCollection<SessionItemView> sessions = new();
    private readonly List<HistoryFileItemView> allCurrentFiles = new();
    private readonly ObservableCollection<HistoryFileItemView> displayedFiles = new();

    public DeviceBackupHistoryDialog(string destinationRoot, DeviceHistoryItem deviceItem)
    {
        InitializeComponent();
        this.destinationRoot = destinationRoot;
        this.deviceItem = deviceItem;

        SessionsListBox.ItemsSource = sessions;
        FilesDataGrid.ItemsSource = displayedFiles;

        Loaded += DeviceBackupHistoryDialog_Loaded;
    }

    private async void DeviceBackupHistoryDialog_Loaded(object sender, RoutedEventArgs e)
    {
        DeviceNameText.Text = string.IsNullOrWhiteSpace(deviceItem.Name) ? "Unknown Device" : deviceItem.Name;
        DeviceModelText.Text = string.IsNullOrWhiteSpace(deviceItem.Model) ? "Generic Device" : deviceItem.Model;
        DeviceSerialText.Text = !string.IsNullOrWhiteSpace(deviceItem.HardwareSerial)
            ? deviceItem.HardwareSerial
            : (!string.IsNullOrWhiteSpace(deviceItem.Udid) ? deviceItem.Udid : "N/A");
        FirstSeenText.Text = deviceItem.LastSeen;

        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (string.IsNullOrWhiteSpace(destinationRoot) || !Directory.Exists(destinationRoot))
        {
            return;
        }

        try
        {
            // 1. Load summary
            var summary = await ArchiveRepository.GetDeviceBackupSummaryAsync(destinationRoot, deviceItem.Udid);
            TotalSessionsText.Text = $"{summary.TotalSessions} 次";
            TotalFilesAndSizeText.Text = $"{summary.TotalFiles:N0} 张 ({ByteSize.Humanize(summary.TotalBytes)})";

            // 2. Load sessions
            var sessionRecords = await ArchiveRepository.GetBackupSessionsAsync(destinationRoot, deviceItem.Udid);
            sessions.Clear();
            foreach (BackupSessionRecord s in sessionRecords)
            {
                sessions.Add(new SessionItemView
                {
                    Id = s.Id,
                    FormattedStartedAt = s.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    FilesCount = s.FilesCount,
                    FormattedSize = ByteSize.Humanize(s.TotalSizeBytes),
                    Status = s.Status == "Completed" ? "全部完成" : (s.Status == "Partial" ? "部分完成" : s.Status)
                });
            }

            if (sessions.Count == 0)
            {
                EmptySessionsNotice.Visibility = Visibility.Visible;
                SessionHeaderTitle.Text = "该设备尚未产生任何历史备份批次记录";
            }
            else
            {
                EmptySessionsNotice.Visibility = Visibility.Collapsed;
                SessionsListBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            SessionHeaderTitle.Text = $"加载失败: {ex.Message}";
        }
    }

    private async void SessionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionsListBox.SelectedItem is not SessionItemView selected)
        {
            allCurrentFiles.Clear();
            displayedFiles.Clear();
            EmptyFilesNotice.Visibility = Visibility.Visible;
            return;
        }

        SessionHeaderTitle.Text = $"批次: {selected.FormattedStartedAt}（共 {selected.FilesCount:N0} 张，{selected.FormattedSize}）";

        try
        {
            var items = await ArchiveRepository.GetBackupHistoryItemsAsync(destinationRoot, selected.Id);
            allCurrentFiles.Clear();
            foreach (BackupHistoryItemRecord item in items)
            {
                allCurrentFiles.Add(new HistoryFileItemView
                {
                    FileName = Path.GetFileName(item.DestPath),
                    DeviceSourcePath = item.DeviceSourcePath,
                    DestPath = item.DestPath,
                    FormattedSize = ByteSize.Humanize(item.FileSize),
                    FormattedTransferredAt = item.TransferredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            SessionHeaderTitle.Text = $"加载明细失败: {ex.Message}";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        displayedFiles.Clear();

        var filtered = string.IsNullOrEmpty(query)
            ? allCurrentFiles
            : allCurrentFiles.Where(f => f.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                      || f.DeviceSourcePath.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var item in filtered)
        {
            displayedFiles.Add(item);
        }

        EmptyFilesNotice.Visibility = displayedFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LocateFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        string fullPath = Path.Combine(destinationRoot, relativePath);
        if (File.Exists(fullPath))
        {
            Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
        }
        else
        {
            MessageBox.Show(this, $"该文件在电脑本地磁盘中已不存在：\n{fullPath}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void FilesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesDataGrid.SelectedItem is HistoryFileItemView item)
        {
            LocateFile(item.DestPath);
        }
    }

    private void LocateFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (FilesDataGrid.SelectedItem is HistoryFileItemView item)
        {
            LocateFile(item.DestPath);
        }
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (FilesDataGrid.SelectedItem is HistoryFileItemView item)
        {
            string fullPath = Path.Combine(destinationRoot, item.DestPath);
            Clipboard.SetText(fullPath);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
