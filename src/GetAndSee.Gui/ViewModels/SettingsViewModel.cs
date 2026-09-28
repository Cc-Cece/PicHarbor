using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;

namespace GetAndSee.Gui.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string archivePath = MainViewModel.DefaultArchivePath;

    [ObservableProperty]
    private bool syncExifToLastWriteTime = true;

    [ObservableProperty]
    private bool syncExifToCreationTime = false;

    [ObservableProperty]
    private int readTimeoutSeconds = 30;

    [ObservableProperty]
    private bool autoCompleteLivePhotoPair = true;

    [ObservableProperty]
    private bool autoCompleteAaeSidecar = true;

    [ObservableProperty]
    private bool autoCompleteRawJpg = false;

    [ObservableProperty]
    private bool disablePcSleepNotice = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool isSyncing;

    public bool CanSync => !IsSyncing;

    [ObservableProperty]
    private bool showProgress;

    [ObservableProperty]
    private double syncProgressPercentage;

    [ObservableProperty]
    private int syncProcessedCount;

    [ObservableProperty]
    private int syncTotalCount;

    [ObservableProperty]
    private int syncUpdatedCount;

    [ObservableProperty]
    private string syncProgressOverlayText = "";

    [ObservableProperty]
    private string syncProgressDetailText = "";

    [ObservableProperty]
    private string syncUpdatedText = "";

    [ObservableProperty]
    private string syncStatusMessage = "";

    public SettingsViewModel()
    {
        LoadConfig();
    }

    private void LoadConfig()
    {
        var config = Config.AppSettings.Load();
        SyncExifToLastWriteTime = config.SyncExifToLastWriteTime;
        SyncExifToCreationTime = config.SyncExifToCreationTime;
        ReadTimeoutSeconds = config.ReadTimeoutSeconds;
        AutoCompleteLivePhotoPair = config.AutoCompleteLivePhotoPair;
        AutoCompleteAaeSidecar = config.AutoCompleteAaeSidecar;
        AutoCompleteRawJpg = config.AutoCompleteRawJpg;
    }

    public void SaveConfig()
    {
        var config = Config.AppSettings.Load();
        config.SyncExifToLastWriteTime = SyncExifToLastWriteTime;
        config.SyncExifToCreationTime = SyncExifToCreationTime;
        config.ReadTimeoutSeconds = ReadTimeoutSeconds;
        config.AutoCompleteLivePhotoPair = AutoCompleteLivePhotoPair;
        config.AutoCompleteAaeSidecar = AutoCompleteAaeSidecar;
        config.AutoCompleteRawJpg = AutoCompleteRawJpg;
        Config.AppSettings.Save(config);
    }

    partial void OnSyncExifToLastWriteTimeChanged(bool value) => SaveConfig();
    partial void OnSyncExifToCreationTimeChanged(bool value) => SaveConfig();
    partial void OnReadTimeoutSecondsChanged(int value) => SaveConfig();
    partial void OnAutoCompleteLivePhotoPairChanged(bool value) => SaveConfig();
    partial void OnAutoCompleteAaeSidecarChanged(bool value) => SaveConfig();
    partial void OnAutoCompleteRawJpgChanged(bool value) => SaveConfig();

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncNowAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            SyncStatusMessage = App.GetString("MsgDirNotExist", "归档目录不存在，请先选择有效的备份目标路径。");
            ShowProgress = false;
            return;
        }

        string dbPath = Path.Combine(ArchivePath, TransferJournal.DatabaseFileName);
        if (!File.Exists(dbPath))
        {
            SyncStatusMessage = App.GetString("MsgDbNotFound", "未在归档目录中找到 get-and-see.db 数据库，请先执行备份。");
            ShowProgress = false;
            return;
        }

        IsSyncing = true;
        ShowProgress = true;
        SyncProcessedCount = 0;
        SyncTotalCount = 0;
        SyncUpdatedCount = 0;
        SyncProgressPercentage = 0;
        SyncProgressOverlayText = "0.0% (0/0)";
        SyncProgressDetailText = App.GetString("MsgReadingDb", "正在读取归档数据库...");
        SyncUpdatedText = string.Format(App.GetString("MsgUpdatedCount", "已成功更新: {0} 项"), 0);
        SyncStatusMessage = "";

        try
        {
            var progress = new Progress<FileTimeSyncProgress>(p =>
            {
                SyncProcessedCount = p.Processed;
                SyncTotalCount = p.Total;
                SyncUpdatedCount = p.Updated;

                if (p.Total > 0)
                {
                    double pct = (double)p.Processed / p.Total * 100.0;
                    SyncProgressPercentage = pct;
                    SyncProgressOverlayText = $"{pct:F1}% ({p.Processed:N0}/{p.Total:N0})";
                    SyncProgressDetailText = string.Format(App.GetString("MsgCompletedProgress", "已完成: {0} / {1} 文件"), p.Processed.ToString("N0"), p.Total.ToString("N0"));
                    SyncUpdatedText = string.Format(App.GetString("MsgUpdatedCount", "已成功更新: {0} 项"), p.Updated.ToString("N0"));
                }
                else
                {
                    SyncProgressPercentage = 0;
                    SyncProgressOverlayText = "0.0% (0/0)";
                    SyncProgressDetailText = App.GetString("MsgNoFilesToSync", "归档清单中未找到可同步的文件");
                    SyncUpdatedText = string.Format(App.GetString("MsgUpdatedCount", "已成功更新: {0} 项"), 0);
                }
            });

            int updated = await FileTimeSynchronizer.BatchSyncArchiveFileTimesAsync(
                ArchivePath,
                syncCreationTime: SyncExifToCreationTime,
                progress: progress);

            SyncStatusMessage = string.Format(App.GetString("MsgSyncSuccess", "✅ 同步完成！共检索 {0} 个归档文件，成功将 {1} 个文件的 EXIF 时间同步到 Windows 系统时间。"), SyncProcessedCount.ToString("N0"), updated.ToString("N0"));
        }
        catch (Exception ex)
        {
            SyncStatusMessage = string.Format(App.GetString("MsgSyncError", "❌ 同步失败: {0}"), ex.Message);
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
