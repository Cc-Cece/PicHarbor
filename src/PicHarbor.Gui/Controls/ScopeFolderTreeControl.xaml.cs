using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// Reusable three-state folder tree picker for the unified scope filter (iPhone / Android / Google
/// Photos). Pure presentation: selection semantics live in <see cref="SubfolderNodeViewModel"/>;
/// the host reads back the checked paths via <c>SubfolderTreeBuilder.CollectSelected</c>.
/// </summary>
public partial class ScopeFolderTreeControl : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(ObservableCollection<SubfolderNodeViewModel>),
            typeof(ScopeFolderTreeControl),
            new PropertyMetadata(null));

    public ScopeFolderTreeControl()
    {
        InitializeComponent();
    }

    /// <summary>Root nodes of the folder tree.</summary>
    public ObservableCollection<SubfolderNodeViewModel>? ItemsSource
    {
        get => (ObservableCollection<SubfolderNodeViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
}
