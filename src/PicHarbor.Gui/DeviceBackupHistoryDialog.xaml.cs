using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PicHarbor.Core.Android;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Controls;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui;

public class SessionItemView
{
    public long Id { get; init; }
    public string? StorageId { get; init; }
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

    /// <summary>序列号正常态的画刷，用于从「未识别」红色恢复。</summary>
    private Brush? serialNormalBrush;

    public DeviceBackupHistoryDialog(string destinationRoot, DeviceHistoryItem deviceItem)
    {
        InitializeComponent();
        serialNormalBrush = DeviceSerialText.Foreground;
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

        // 「重新绑定到手机」只对安卓有意义（iPhone 的标识是 UDID，不存在设备端识别文件）。
        if (!string.Equals(deviceItem.DeviceType, "Android", StringComparison.OrdinalIgnoreCase))
        {
            RebindPhoneButton.Visibility = Visibility.Collapsed;
        }

        // 只有通过合理性校验的值才当作真实标识展示；旧版本的 android:{主机名} 等占位值显示为「未识别」。
        if (DeviceSerialValidator.IsUsable(deviceItem.HardwareSerial))
        {
            DeviceSerialText.Text = deviceItem.HardwareSerial!.Trim();
        }
        else
        {
            DeviceSerialText.Text = App.GetString("HistSerialUnknown", "未识别");
            DeviceSerialText.Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
            SerialHintText.Text = App.GetString("HistSerialUnknownHint", "该设备尚未记录有效的硬件序列号，可点「修正标识」手动填写。");
            SerialHintText.Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
            SerialHintText.Visibility = Visibility.Visible;
        }

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
                    StorageId = s.StorageId,
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
            var items = await ArchiveRepository.GetBackupHistoryItemsAsync(destinationRoot, selected.Id,selected.StorageId);
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

    private void FixIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        SerialEditBox.Text = DeviceSerialValidator.IsUsable(deviceItem.HardwareSerial)
            ? deviceItem.HardwareSerial!.Trim()
            : string.Empty;

        SerialDisplayRow.Visibility = Visibility.Collapsed;
        SerialEditRow.Visibility = Visibility.Visible;
        SerialHintText.Visibility = Visibility.Collapsed;
        SerialEditBox.Focus();
        SerialEditBox.SelectAll();
    }

    private void SerialCancelButton_Click(object sender, RoutedEventArgs e)
    {
        SerialEditRow.Visibility = Visibility.Collapsed;
        SerialDisplayRow.Visibility = Visibility.Visible;
        RestoreSerialHint();
    }

    /// <summary>生成 PC 侧内部自定义标识，填入编辑框。</summary>
    private void RandomSerialButton_Click(object sender, RoutedEventArgs e)
    {
        SerialEditBox.Text = DeviceSerialGenerator.CreateInternalId();
        ShowSerialHint(App.GetString("HistRandomGenerated", "已生成内部自定义标识，确认无误后点「保存」。"), isError: false);
    }

    /// <summary>
    /// 重新配对到手机：把当前记录的唯一码写回设备端固定目录，并回读验证。
    /// 用于「手机端识别文件意外丢失」的场景——无需改动标识本身。
    /// </summary>
    private void RebindPhoneButton_Click(object sender, RoutedEventArgs e)
    {
        string serial = (deviceItem.HardwareSerial ?? string.Empty).Trim();
        if (!DeviceSerialValidator.IsUsable(serial))
        {
            ShowSerialHint(App.GetString("HistRebindNoSerial", "当前设备标识不可用，请先点「修正标识」填入有效标识。"), isError: true);
            return;
        }

        AppConfig config = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(config.AndroidBackupFtpHost))
        {
            ShowSerialHint(App.GetString("HistRebindNoFtp", "尚未配置安卓 FTP 连接，请先在「同步到 Android」页填写 FTP 参数。"), isError: true);
            return;
        }

        var dialog = new AndroidPairingDialog(
            deviceItem.Name,
            serial,
            config.AndroidBackupFtpHost,
            config.AndroidBackupFtpPort,
            config.AndroidBackupFtpUser,
            config.AndroidBackupFtpPassword)
        {
            Owner = this
        };

        dialog.ShowDialog();

        if (dialog.IsPaired)
        {
            ShowSerialHint(
                string.Format(
                    App.GetString("FmtHistRebindOk", "已把设备标识写入手机：{0}"),
                    $"{AndroidRemoteLayout.RootDirectory}{AndroidDeviceDetector.SerialFileName}"),
                isError: false);
        }
    }

    private async void SerialSaveButton_Click(object sender, RoutedEventArgs e)
    {
        string value = SerialEditBox.Text?.Trim() ?? string.Empty;

        DeviceSerialRejection rejection = DeviceSerialValidator.Validate(value);
        if (rejection != DeviceSerialRejection.None)
        {
            ShowSerialHint(
                string.Format(
                    App.GetString("FmtHistSerialInvalid", "标识不可用（{0}）。{1}"),
                    DescribeSerialRejection(rejection),
                    App.GetString("HistSerialFixPlaceholder", "请输入手机「设置 → 关于手机 → 全部参数 → 状态信息 → 序列号」中的值，或自定义一个稳定标识。")),
                isError: true);
            return;
        }

        try
        {
            bool taken = await ArchiveRepository
                .IsHardwareSerialTakenByOtherAsync(destinationRoot, value, deviceItem.Udid)
                .ConfigureAwait(true);

            if (taken)
            {
                ShowSerialHint(App.GetString("HistSerialDuplicate", "该标识已被归档库中的其它设备使用，请换一个。"), isError: true);
                return;
            }

            bool isAndroid = string.Equals(deviceItem.DeviceType, "Android", StringComparison.OrdinalIgnoreCase);
            string oldUdid = deviceItem.Udid;
            string targetUdid = oldUdid;

            // 安卓侧设备标识即身份：改动它必须迁移 UDID，否则下次备份会新建一条记录、历史脱钩。
            if (isAndroid)
            {
                targetUdid = $"{DeviceSerialValidator.SyntheticUdidPrefix}{value}";
                if (!string.Equals(targetUdid, oldUdid, StringComparison.OrdinalIgnoreCase))
                {
                    bool migrated = await ArchiveRepository
                        .MigrateDeviceUdidAsync(destinationRoot, oldUdid, targetUdid)
                        .ConfigureAwait(true);

                    if (!migrated)
                    {
                        ShowSerialHint(App.GetString("HistSerialUdidTaken", "该标识已对应另一条设备记录，无法合并，请换一个。"), isError: true);
                        return;
                    }

                    deviceItem.Udid = targetUdid;
                }
            }

            bool updated = await ArchiveRepository
                .UpdateDeviceHardwareSerialAsync(destinationRoot, targetUdid, value)
                .ConfigureAwait(true);

            deviceItem.HardwareSerial = value;
            DeviceSerialText.Text = value;
            if (serialNormalBrush is not null)
            {
                DeviceSerialText.Foreground = serialNormalBrush;
            }

            if (isAndroid)
            {
                SyncAndroidBackupConfig(oldUdid, value);
            }

            ShowSerialHint(
                updated
                    ? string.Format(App.GetString("FmtHistSerialFixed", "设备标识已更新为：{0}"), value)
                    : App.GetString("HistSerialNotPersisted", "已更新当前显示，但归档库中未找到该设备记录，未能持久化。"),
                isError: !updated);

            SerialEditRow.Visibility = Visibility.Collapsed;
            SerialDisplayRow.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowSerialHint(string.Format(App.GetString("FmtHistSerialFailed", "保存失败：{0}"), ex.Message), isError: true);
        }
    }

    /// <summary>
    /// 安卓设备的标识同时也是备份页的连接配置。仅当该记录正是当前配置指向的设备时才同步，
    /// 避免多台安卓设备之间互相覆盖。
    /// </summary>
    private static void SyncAndroidBackupConfig(string oldUdid, string newSerial)
    {
        try
        {
            string oldId = oldUdid.StartsWith(DeviceSerialValidator.SyntheticUdidPrefix, StringComparison.OrdinalIgnoreCase)
                ? oldUdid[DeviceSerialValidator.SyntheticUdidPrefix.Length..]
                : oldUdid;

            AppConfig config = AppSettings.Load();
            if (!string.IsNullOrWhiteSpace(config.AndroidBackupDeviceId) &&
                !string.Equals(config.AndroidBackupDeviceId, oldId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            config.AndroidBackupDeviceId = newSerial;
            config.AndroidBackupDeviceIdConfirmed = true;
            AppSettings.Save(config);
        }
        catch
        {
            // 配置同步失败不影响归档库内的修正结果。
        }
    }

    private void RestoreSerialHint()
    {
        if (DeviceSerialValidator.IsUsable(deviceItem.HardwareSerial))
        {
            SerialHintText.Visibility = Visibility.Collapsed;
            return;
        }

        SerialHintText.Text = App.GetString("HistSerialUnknownHint", "该设备尚未记录有效的硬件序列号，可点「修正标识」手动填写。");
        SerialHintText.Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
        SerialHintText.Visibility = Visibility.Visible;
    }

    private void ShowSerialHint(string text, bool isError)
    {
        SerialHintText.Text = text;
        SerialHintText.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(0xE2, 0x4B, 0x4A)
            : Color.FromRgb(0x10, 0xB9, 0x81));
        SerialHintText.Visibility = Visibility.Visible;
    }

    private static string DescribeSerialRejection(DeviceSerialRejection rejection) => rejection switch
    {
        DeviceSerialRejection.NotProvided => App.GetString("AndroidIdRejNotProvided", "设备未提供序列号"),
        DeviceSerialRejection.Duplicate => App.GetString("AndroidIdRejDuplicate", "该标识已被其它设备使用"),
        _ => App.GetString("AndroidIdRejMalformed", "格式不可信，可能是占位串或端口路径"),
    };

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
