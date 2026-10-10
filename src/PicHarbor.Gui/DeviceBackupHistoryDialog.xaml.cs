using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Controls;
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
    private bool isClosing = false;

    public DeviceBackupHistoryDialog(string destinationRoot, DeviceHistoryItem deviceItem)
    {
        InitializeComponent();
        TotalSessionsText.Text = string.Format(App.GetString("FmtSessionCount", "{0} 次"), 0);
        TotalFilesAndSizeText.Text = string.Format(App.GetString("FmtFilesAndSize", "{0:N0} 张 ({1})"), 0, "0 B");
        this.destinationRoot = destinationRoot;
        this.deviceItem = deviceItem;

        SessionsListBox.ItemsSource = sessions;
        FilesDataGrid.ItemsSource = displayedFiles;

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseWithAnimation();
                e.Handled = true;
            }
        };

        Loaded += DeviceBackupHistoryDialog_Loaded;
    }

    private async void DeviceBackupHistoryDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. PCL2 Signature Elastic Pop-in Animation (Scale: 0.92 -> 1.0 with EaseOutBack, Opacity: 0 -> 1)
        PclAnimation.AnimateDouble(RootBorder, OpacityProperty, 1.0, 180, PclAnimation.EaseOutFluentWeak);
        PclAnimation.AnimateScale(RootScaleTransform, 1.0, 240, PclAnimation.EaseOutBack);

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
            TotalSessionsText.Text = string.Format(App.GetString("FmtSessionCount", "{0} 次"), summary.TotalSessions);
            TotalFilesAndSizeText.Text = string.Format(App.GetString("FmtFilesAndSize", "{0:N0} 张 ({1})"), summary.TotalFiles, ByteSize.Humanize(summary.TotalBytes));

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
                    Status = s.Status == "Completed"
                        ? App.GetString("HistStatusComplete", "全部完成")
                        : (s.Status == "Partial" ? App.GetString("HistStatusPartial", "部分完成") : s.Status)
                });
            }

            if (sessions.Count == 0)
            {
                EmptySessionsNotice.Visibility = Visibility.Visible;
                SessionHeaderTitle.Text = App.GetString("FmtHistNoSessions", "该设备尚未产生任何历史备份批次记录");
            }
            else
            {
                EmptySessionsNotice.Visibility = Visibility.Collapsed;
                SessionsListBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            SessionHeaderTitle.Text = string.Format(App.GetString("FmtHistLoadFailed", "加载失败: {0}"), ex.Message);
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

        SessionHeaderTitle.Text = string.Format(App.GetString("FmtHistBatch", "批次: {0}（共 {1:N0} 张，{2}）"), selected.FormattedStartedAt, selected.FilesCount, selected.FormattedSize);

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
            SessionHeaderTitle.Text = string.Format(App.GetString("FmtHistLoadItemsFailed", "加载明细失败: {0}"), ex.Message);
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
            MessageBox.Show(this, string.Format(App.GetString("FmtHistFileMissing", "该文件在电脑本地磁盘中已不存在：\n{0}"), fullPath), App.GetString("MsgBoxTitle", "提示"), MessageBoxButton.OK, MessageBoxImage.Information);
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
        CloseWithAnimation();
    }

    public void CloseWithAnimation()
    {
        if (isClosing) return;
        isClosing = true;

        PclAnimation.AnimateScale(RootScaleTransform, 0.94, 150, PclAnimation.EaseOutFluentMiddle);
        PclAnimation.AnimateDouble(RootBorder, OpacityProperty, 0.0, 140, PclAnimation.EaseOutFluentMiddle, onCompleted: Close);
    }
}
