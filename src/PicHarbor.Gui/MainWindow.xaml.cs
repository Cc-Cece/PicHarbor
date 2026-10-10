using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PicHarbor.Gui.Util;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var topModal = Controls.PclModalHost.GetTopActiveModal();
            if (topModal != null)
            {
                topModal.Close();
                e.Handled = true;
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int index))
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SelectedTabIndex = index;
            }
        }
    }

    private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            if (!row.IsSelected)
            {
                if (FindParent<DataGrid>(row) is DataGrid dataGrid)
                {
                    dataGrid.SelectedItem = row.Item;
                }
            }
        }
    }

    private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            if (!item.IsSelected)
            {
                if (FindParent<ListBox>(item) is ListBox listBox)
                {
                    listBox.SelectedItem = item.DataContext;
                }
            }
        }
    }

    private void DataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is DataGridRow { DataContext: MediaSearchResultItem item })
            {
                if (DataContext is MainViewModel { SearchVM: { } searchVM })
                {
                    searchVM.OpenPreviewCommand.Execute(item);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DataGridRow_MouseDoubleClick error: {ex.Message}");
        }
    }

    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp",
        ".heic", ".heif", ".tif", ".tiff", ".dng", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".rw2", ".orf"
    };

    private string? ResolveDetailFilePath(TransferItemDetail? detail)
    {
        if (detail == null) return null;
        if (!string.IsNullOrEmpty(detail.FullPath) && File.Exists(detail.FullPath))
            return detail.FullPath;
        if (!string.IsNullOrEmpty(detail.TargetPath) && File.Exists(detail.TargetPath))
            return detail.TargetPath;
        if (!string.IsNullOrEmpty(detail.SourcePath) && File.Exists(detail.SourcePath))
            return detail.SourcePath;
        if (DataContext is MainViewModel mainVM && !string.IsNullOrEmpty(detail.TargetPath))
        {
            var dest = mainVM.DestinationPath;
            if (!string.IsNullOrEmpty(dest))
            {
                var candidate = Path.Combine(dest, detail.TargetPath);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private void DetailDataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void DetailDataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is DataGridRow { DataContext: TransferItemDetail detail })
            {
                var path = ResolveDetailFilePath(detail);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string ext = Path.GetExtension(path);
                    if (PreviewImageExtensions.Contains(ext) && DataContext is MainViewModel mainVM)
                    {
                        mainVM.OpenImageViewer(path);
                    }
                    else
                    {
                        ShellServices.OpenFiles(new[] { path });
                    }
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DetailDataGridRow_MouseDoubleClick error: {ex.Message}");
        }
    }

    private void DetailContextMenu_ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                ShellServices.ShowInExplorer(new[] { path });
            }
        }
    }

    private void DetailContextMenu_OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                ShellServices.OpenFiles(new[] { path });
            }
        }
    }

    private void DetailContextMenu_CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                ShellServices.CopyFilesToClipboard(new[] { path });
            }
        }
    }

    private void DetailContextMenu_Properties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                ShellServices.ShowProperties(new[] { path }, hwnd);
            }
        }
    }

    private void ModernListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: MediaSearchResultItem item })
            {
                if (!string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
                {
                    ShellServices.OpenFiles([item.FullPath]);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ModernListItem_MouseDoubleClick error: {ex.Message}");
        }
    }

    private void ModernListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void ListItem_ToggleIPhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            DataContext is MainViewModel { SearchVM: { } searchVM })
        {
            searchVM.ToggleIPhoneManualSelectionCommand.Execute(item);
        }
    }

    private void ListItem_ToggleAndroid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            DataContext is MainViewModel { SearchVM: { } searchVM })
        {
            searchVM.ToggleAndroidManualSelectionCommand.Execute(item);
        }
    }

    private void ListItem_Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            !string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
        {
            ShellServices.ShowInExplorer([item.FullPath]);
        }
    }

    private void ListItem_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            !string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
        {
            ShellServices.OpenFiles([item.FullPath]);
        }
    }


    protected override void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            base.OnKeyDown(e);
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (DataContext is MainViewModel mainVM)
            {

                if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    if (mainVM.SelectedTabIndex == 1 && mainVM.IPhoneSyncVM.IsScopeManualSelection)
                    {
                        if (mainVM.IPhoneSyncVM.PasteFilesFromClipboardCommand.CanExecute(null))
                        {
                            mainVM.IPhoneSyncVM.PasteFilesFromClipboardCommand.Execute(null);
                            e.Handled = true;
                        }
                    }
                    else if (mainVM.SelectedTabIndex == 2 && mainVM.AndroidSyncVM.IsScopeManualSelection)
                    {
                        if (mainVM.AndroidSyncVM.PasteFilesFromClipboardCommand.CanExecute(null))
                        {
                            mainVM.AndroidSyncVM.PasteFilesFromClipboardCommand.Execute(null);
                            e.Handled = true;
                        }
                    }
                    else if (mainVM.SelectedTabIndex == 3 && mainVM.GooglePhotosVM.IsScopeManualSelection)
                    {
                        if (mainVM.GooglePhotosVM.PasteFilesFromClipboardCommand.CanExecute(null))
                        {
                            mainVM.GooglePhotosVM.PasteFilesFromClipboardCommand.Execute(null);
                            e.Handled = true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnKeyDown error: {ex.Message}");
        }
    }

    private void ManualDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void UnifiedManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel mainVM)
                    {
                        mainVM.AddFilesToUnifiedManual(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UnifiedManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void IPhoneManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel { IPhoneSyncVM: { } vm })
                    {
                        vm.ProcessPickedFiles(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IPhoneManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void AndroidManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel { AndroidSyncVM: { } vm })
                    {
                        vm.ProcessPickedFiles(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AndroidManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void GooglePhotosManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel { GooglePhotosVM: { } vm })
                    {
                        vm.ProcessPickedFiles(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GooglePhotosManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void SearchResults_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CheckAndLoadMore(e);
    }

    private void Gallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CheckAndLoadMore(e);
    }

    private void CheckAndLoadMore(ScrollChangedEventArgs e)
    {
        try
        {
            if (e.VerticalChange > 0 && e.ExtentHeight > 0)
            {
                if (e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) < 300)
                {
                    if (DataContext is MainViewModel { SearchVM: { } searchVM })
                    {
                        if (searchVM.LoadMoreItemsCommand.CanExecute(null))
                        {
                            searchVM.LoadMoreItemsCommand.Execute(null);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScrollChanged error: {ex.Message}");
        }
    }

    private void DeviceHeroArea_MouseEnter(object sender, MouseEventArgs e)
    {
        if (IPhoneHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, HeightProperty, 30.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, OpacityProperty, 1.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
        }
        if (AndroidHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, HeightProperty, 30.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, OpacityProperty, 1.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
        }
    }

    private void DeviceHeroArea_MouseLeave(object sender, MouseEventArgs e)
    {
        if (IPhoneHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, HeightProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, OpacityProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
        }
        if (AndroidHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, HeightProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, OpacityProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
        }
    }

    private void IPhoneHero_MouseEnter(object sender, MouseEventArgs e) => DeviceHeroArea_MouseEnter(sender, e);
    private void IPhoneHero_MouseLeave(object sender, MouseEventArgs e) => DeviceHeroArea_MouseLeave(sender, e);
    private void AndroidHero_MouseEnter(object sender, MouseEventArgs e) => DeviceHeroArea_MouseEnter(sender, e);
    private void AndroidHero_MouseLeave(object sender, MouseEventArgs e) => DeviceHeroArea_MouseLeave(sender, e);

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }
}

