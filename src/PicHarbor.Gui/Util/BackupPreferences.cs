using System.Windows;
using System.IO;
using PicHarbor.Core.Device;
using PicHarbor.Gui.Config;

namespace PicHarbor.Gui.Util;

internal static class BackupPreferences
{
    internal static async Task<bool> EnsureArchiveProtectionAsync(string root)
    {
        if(!AppSettings.Load().AppLockWasEnabled) return true;
        var status=await PicHarbor.Gui.Vault.VaultClient.QueryStatusAsync();
        if(status is null || !status.IsUnlocked || status.ProtocolVersion<2 || status.ProtectionErrors.Count>0) return false;
        if(!status.RegisteredRoots.Contains(Path.GetFullPath(root).TrimEnd('\\'),StringComparer.OrdinalIgnoreCase))
        {
            var response=await PicHarbor.Gui.Vault.VaultClient.RegisterRootAsync(root);
            return response?.Ok==true;
        }
        return true;
    }
    private static readonly object DecisionLock = new();

    internal static bool? DecideDeletedPhoto(RemoteFile _)
    {
        lock (DecisionLock)
        {
            var config = AppSettings.Load();
            if (config.RebackupQuestionAsked) return config.RebackupDeletedPhotos;
            var answer = Application.Current.Dispatcher.Invoke(() => MessageBox.Show(
                App.GetString("RebackupQuestion", "是否重新备份已删除的照片？后续可在设置修改。"),
                "PicHarbor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, config.RebackupDeletedPhotos ? MessageBoxResult.Yes : MessageBoxResult.No));
            if (answer == MessageBoxResult.Cancel) return null;
            config.RebackupDeletedPhotos = answer == MessageBoxResult.Yes;
            config.RebackupQuestionAsked = true;
            AppSettings.Save(config);
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Application.Current.MainWindow?.DataContext is PicHarbor.Gui.ViewModels.MainViewModel main)
                    main.SettingsVM.RebackupDeletedPhotos = config.RebackupDeletedPhotos;
            });
            return config.RebackupDeletedPhotos;
        }
    }
}
