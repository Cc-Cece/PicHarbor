using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;

namespace PicHarbor.Gui.ViewModels;

/// <summary>Row model for the sync-folder maintenance scan result.</summary>
public partial class SyncFolderScanItemViewModel : ObservableObject
{
    public string Folder { get; }
    public string? DeviceName { get; }
    public bool IsCurrent { get; }
    public int FileCount { get; }
    public long TotalBytes { get; }
    public int EquivalentCount { get; }
    public int MissingCount { get; }
    public string? ReconcileTarget { get; }

    public SyncFolderScanItemViewModel(IPhoneSyncFolderMaintenanceService.SyncFolderScanItem item)
    {
        Folder = item.Folder;
        DeviceName = item.DeviceName;
        IsCurrent = item.IsCurrent;
        FileCount = item.FileCount;
        TotalBytes = item.TotalBytes;
        EquivalentCount = item.EquivalentCount;
        MissingCount = item.MissingCount;
        ReconcileTarget = item.ReconcileTarget;
    }

    public string FileCountText => FileCount.ToString("N0");
    public string TotalSizeText => FormatByteSize(TotalBytes);
    public string EquivalentText => IsCurrent ? "-" : $"{EquivalentCount} / {FileCount}";
    public string MissingText => IsCurrent ? "-" : MissingCount.ToString("N0");
    public string DeviceDisplay => string.IsNullOrWhiteSpace(DeviceName) ? "-" : DeviceName!;
    public string FilesSummaryText => $"{FileCountText} · {TotalSizeText}";

    public string MergeInfoText => IsCurrent
        ? string.Empty
        : string.Format(App.GetString("FmtSyncItemMergeInfo", "等价 {0} / {1}，缺失 {2}"), EquivalentCount, FileCount, MissingCount);

    public string StatusText
    {
        get
        {
            if (IsCurrent) return App.GetString("SyncFolderStatusCurrent", "当前同步目录");
            if (FileCount == 0) return App.GetString("SyncFolderStatusEmpty", "空目录");
            if (MissingCount == 0) return App.GetString("SyncFolderStatusMerged", "已全部并入");
            return App.GetString("SyncFolderStatusMergeable", "可合并");
        }
    }

    public bool CanMerge => !IsCurrent && MissingCount > 0 && !string.IsNullOrEmpty(ReconcileTarget);
    public bool CanCleanup => !IsCurrent;
    public bool CanIgnore => !IsCurrent;

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
}

/// <summary>
/// ViewModel of the "iPhone sync folder maintenance" tool card in Settings → Built-in Tools:
/// scans for scattered sync folders, merges them into their device's current folder,
/// moves confirmed-clean folders to the Recycle Bin, and keeps an ignore list.
/// </summary>
public partial class SyncFolderMaintenanceViewModel : ObservableObject
{
    [ObservableProperty]
    private string archivePath = "";

    [ObservableProperty]
    private bool isBusy = false;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private double progressPercentage = 0;

    [ObservableProperty]
    private bool hasScanned = false;

    public ObservableCollection<SyncFolderScanItemViewModel> Items { get; } = new();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        await ScanCoreAsync(quiet: false);
    }

    private async Task ScanCoreAsync(bool quiet)
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            StatusMessage = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            return;
        }

        IsBusy = true;
        if (!quiet)
        {
            StatusMessage = App.GetString("MsgSyncScanning", "正在扫描同步目录…");
        }
        try
        {
            string path = ArchivePath;
            var items = await Task.Run(() => IPhoneSyncFolderMaintenanceService.ScanSyncFolders(path));

            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new SyncFolderScanItemViewModel(item));
            }

            HasScanned = true;
            StatusMessage = items.Count == 0
                ? App.GetString("MsgSyncScanNone", "未发现散落的同步目录。")
                : string.Format(App.GetString("MsgSyncScanDone", "扫描完成：发现 {0} 个同步目录。"), items.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(App.GetString("FmtSyncScanError", "❌ 扫描失败: {0}"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MergeItemAsync(SyncFolderScanItemViewModel? item)
    {
        if (item is null || IsBusy || item.IsCurrent || string.IsNullOrWhiteSpace(item.ReconcileTarget)) return;

        IsBusy = true;
        ProgressPercentage = 0;
        StatusMessage = App.GetString("MsgMergingNow", "正在并入旧同步目录内容…");
        try
        {
            string target = item.ReconcileTarget;
            var result = await Task.Run(() => IPhoneSyncFolderMaintenanceService.MergeSyncFolderAsync(
                item.Folder,
                target,
                new Progress<IPhoneSyncFolderMergeProgress>(p =>
                {
                    ProgressPercentage = p.TotalBytes > 0 ? p.ProcessedBytes * 100.0 / p.TotalBytes : 0;
                    StatusMessage = $"{p.ProcessedFiles} / {p.TotalFiles}  {p.CurrentFile}";
                })));

            ProgressPercentage = 100;
            StatusMessage = string.Format(
                App.GetString("FmtMergeDone", "合并完成：复制 {0} 项，已存在 {1} 项，失败 {2} 项"),
                result.CopiedCount, result.SkippedCount, result.FailedCount);
            await ScanCoreAsync(quiet: true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(App.GetString("FmtSyncMergeError", "❌ 合并失败: {0}"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanupItemAsync(SyncFolderScanItemViewModel? item)
    {
        if (item is null || IsBusy || item.IsCurrent) return;

        string warning = item.MissingCount > 0
            ? string.Format(
                App.GetString("MsgSyncCleanupWarnMissing", "该目录仍有 {0} 项文件尚未合并到当前同步目录。删除后这些文件将随目录一起移入回收站。确定继续？"),
                item.MissingCount)
            : string.Format(
                App.GetString("MsgSyncCleanupConfirm", "该目录的全部 {0} 项文件均已存在于当前同步目录。将其移入回收站？"),
                item.FileCount);

        var choice = MessageBox.Show(
            warning,
            App.GetString("AppTitle", "PicHarbor"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            string folder = item.Folder;
            await Task.Run(() => IPhoneSyncFolderMaintenanceService.CleanupToRecycleBin(folder));
            StatusMessage = App.GetString("MsgSyncCleanupDone", "已移入回收站。");
            await ScanCoreAsync(quiet: true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(App.GetString("FmtSyncCleanupBlocked", "❌ 清理被阻止: {0}"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task IgnoreItemAsync(SyncFolderScanItemViewModel? item)
    {
        if (item is null || IsBusy || item.IsCurrent) return;
        try
        {
            using var journal = TransferJournal.Open(ArchivePath);
            journal.AddIgnoredSyncFolder(item.Folder);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(App.GetString("FmtSyncIgnoreError", "❌ 记录忽略失败: {0}"), ex.Message);
            return;
        }

        StatusMessage = App.GetString("MsgSyncIgnored", "已忽略该目录，之后的扫描将不再列出。");
        await ScanCoreAsync(quiet: true);
    }
}
