using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GetAndSee.Gui.ViewModels;

namespace GetAndSee.Gui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer previewClock;
    private bool suppressSeekCallback;
    private bool previewUserSeeking;
    private string? attemptedVideoPath;
    private bool previewOpened;
    private bool markingPreviewFailure;

    public MainWindow()
    {
        InitializeComponent();
        previewClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        previewClock.Tick += (_, _) => UpdatePreviewClock();
        if (DataContext is MainViewModel main)
        {
            main.SearchVM.PropertyChanged += SearchVm_PropertyChanged;
        }

        Closed += (_, _) => StopPreviewPlayback();
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

    private void DetailDataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is DataGridRow { DataContext: TransferItemDetail detail })
            {
                if (DataContext is MainViewModel mainVM)
                {
                    if (mainVM.BackupVM.IsDetailModalOpen)
                    {
                        mainVM.BackupVM.OpenDetailCommand.Execute(detail);
                    }
                    else if (mainVM.AndroidSyncVM.IsDetailModalOpen)
                    {
                        mainVM.AndroidSyncVM.OpenDetailCommand.Execute(detail);
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

    private void GalleryCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement { DataContext: MediaSearchResultItem item })
                {
                    if (DataContext is MainViewModel { SearchVM: { } searchVM })
                    {
                        searchVM.OpenPreviewCommand.Execute(item);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GalleryCard_MouseLeftButtonDown error: {ex.Message}");
        }
    }

    private void LightboxImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        OpenPreviewItemExternal(e);
    }

    private void PreviewPlayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        OpenPreviewItemExternal(e);
    }

    private void OpenPreviewItemExternal(MouseButtonEventArgs e)
    {
        try
        {
            if (e.ClickCount == 2)
            {
                if (DataContext is MainViewModel { SearchVM: { PreviewItem: not null } searchVM })
                {
                    searchVM.OpenCommand.Execute(new[] { searchVM.PreviewItem });
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LightboxImage_MouseLeftButtonDown error: {ex.Message}");
        }
    }

    private void SearchVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.PreviewVideoPath)
            or nameof(SearchViewModel.IsPreviewVideo)
            or nameof(SearchViewModel.IsPreviewOpen))
        {
            SyncPreviewPlayer();
        }
    }

    private void SyncPreviewPlayer()
    {
        if (DataContext is not MainViewModel { SearchVM: { } search })
        {
            return;
        }

        StopPreviewPlayback();
        if (!search.IsPreviewOpen || !search.IsPreviewVideo || string.IsNullOrWhiteSpace(search.PreviewVideoPath))
        {
            return;
        }

        if (!File.Exists(search.PreviewVideoPath))
        {
            return;
        }

        try
        {
            attemptedVideoPath = search.PreviewVideoPath;
            previewOpened = false;
            search.IsPreviewVideoFailed = false;
            search.PreviewStatusText = "";
            PreviewPlayer.Source = new Uri(search.PreviewVideoPath, UriKind.Absolute);
            PreviewPlayer.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Preview playback error: {ex.Message}");
            MarkPreviewVideoFailed(search);
        }
    }

    private void StopPreviewPlayback()
    {
        previewClock.Stop();
        previewUserSeeking = false;
        attemptedVideoPath = null;
        previewOpened = false;
        suppressSeekCallback = true;
        PreviewSeek.Value = 0;
        suppressSeekCallback = false;
        PreviewClockText.Text = "0:00 / 0:00";
        PreviewPlayer.Visibility = Visibility.Collapsed;
        try
        {
            PreviewPlayer.Stop();
            PreviewPlayer.Source = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stop preview error: {ex.Message}");
        }

        if (DataContext is MainViewModel { SearchVM: { } search })
        {
            search.IsPreviewPlaying = false;
        }
    }

    private void PreviewPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SearchVM: { } search })
        {
            return;
        }

        if (PreviewPlayer.Source is not Uri uri ||
            !string.Equals(uri.LocalPath, attemptedVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        previewOpened = true;
        search.IsPreviewVideoFailed = false;
        search.PreviewStatusText = "";
        PreviewPlayer.Visibility = Visibility.Visible;
        search.IsPreviewPlaying = true;
        previewClock.Start();
        UpdatePreviewClock();
    }

    private void PreviewPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (markingPreviewFailure || previewOpened || DataContext is not MainViewModel { SearchVM: { } search })
        {
            return;
        }

        if (PreviewPlayer.Source is not Uri uri ||
            !string.Equals(uri.LocalPath, attemptedVideoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine($"Preview media failed: {e.ErrorException?.Message}");
        MarkPreviewVideoFailed(search);
    }

    private void PreviewPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!previewOpened)
        {
            return;
        }

        PreviewPlayer.Position = TimeSpan.Zero;
        PreviewPlayer.Play();
    }

    private void PreviewPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { SearchVM: { } search } || !search.IsPreviewVideo)
        {
            return;
        }

        if (search.IsPreviewPlaying)
        {
            PreviewPlayer.Pause();
            search.IsPreviewPlaying = false;
            previewClock.Stop();
            return;
        }

        if (PreviewPlayer.Source is null)
        {
            SyncPreviewPlayer();
            return;
        }

        PreviewPlayer.Play();
        search.IsPreviewPlaying = true;
        previewClock.Start();
    }

    private void PreviewSeek_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        previewUserSeeking = true;
    }

    private void PreviewSeek_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        previewUserSeeking = false;
        SeekPreview(PreviewSeek.Value);
    }

    private void PreviewSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSeekCallback || !previewUserSeeking)
        {
            return;
        }

        SeekPreview(e.NewValue);
    }

    private void SeekPreview(double seconds)
    {
        if (!PreviewPlayer.NaturalDuration.HasTimeSpan || double.IsNaN(seconds))
        {
            return;
        }

        TimeSpan duration = PreviewPlayer.NaturalDuration.TimeSpan;
        if (seconds < 0)
        {
            seconds = 0;
        }

        if (seconds > duration.TotalSeconds)
        {
            seconds = duration.TotalSeconds;
        }

        PreviewPlayer.Position = TimeSpan.FromSeconds(seconds);
        UpdatePreviewClock();
    }

    private void UpdatePreviewClock()
    {
        if (!PreviewPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        TimeSpan duration = PreviewPlayer.NaturalDuration.TimeSpan;
        TimeSpan position = PreviewPlayer.Position;
        if (!previewUserSeeking)
        {
            suppressSeekCallback = true;
            PreviewSeek.Maximum = Math.Max(duration.TotalSeconds, 0.001);
            PreviewSeek.Value = Math.Clamp(position.TotalSeconds, 0, PreviewSeek.Maximum);
            suppressSeekCallback = false;
        }

        PreviewClockText.Text = $"{FormatClock(position)} / {FormatClock(duration)}";
    }

    private static string FormatClock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void MarkPreviewVideoFailed(SearchViewModel search)
    {
        if (markingPreviewFailure)
        {
            return;
        }

        markingPreviewFailure = true;
        previewClock.Stop();
        PreviewPlayer.Visibility = Visibility.Collapsed;
        try
        {
            PreviewPlayer.Stop();
            PreviewPlayer.Source = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stop failed preview error: {ex.Message}");
        }

        attemptedVideoPath = null;
        search.IsPreviewPlaying = false;
        search.IsPreviewVideoFailed = true;
        search.PreviewStatusText = App.GetString("PreviewVideoFailed", "无法播放此视频，已显示配套静图。");
        markingPreviewFailure = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            base.OnKeyDown(e);
            if (DataContext is MainViewModel mainVM)
            {
                if (mainVM.SearchVM is { IsPreviewOpen: true } searchVM)
                {
                    if (e.Key == Key.Escape)
                    {
                        searchVM.ClosePreviewCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Space && searchVM.IsPreviewVideo && Keyboard.FocusedElement is not (TextBox or Button or Slider))
                    {
                        PreviewPlayPause_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Left)
                    {
                        searchVM.PrevPreviewCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Right)
                    {
                        searchVM.NextPreviewCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                }

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

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }
}
