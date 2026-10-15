using System.Windows;

namespace PicHarbor.Gui.Updates;

public partial class UpdatePermissionDialog : Window
{
    public bool SkipVersion => Skip.IsChecked == true;
    public bool DisableAutomatic => DisableAuto.IsChecked == true;

    public UpdatePermissionDialog(string version)
    {
        InitializeComponent();
        Explanation.Text = string.Format(App.GetString("UpdatePermissionExplanation"), version);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
