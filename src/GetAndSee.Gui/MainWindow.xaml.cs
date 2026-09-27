using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GetAndSee.Gui.ViewModels;

namespace GetAndSee.Gui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            base.OnKeyDown(e);
            if (DataContext is MainViewModel { SearchVM: { IsPreviewOpen: true } searchVM })
            {
                if (e.Key == Key.Escape)
                {
                    searchVM.ClosePreviewCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    searchVM.PrevPreviewCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    searchVM.NextPreviewCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnKeyDown error: {ex.Message}");
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
