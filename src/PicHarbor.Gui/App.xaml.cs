using System.Windows;

namespace PicHarbor.Gui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show($"程序运行遇到异常:\n{args.Exception.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    public static string GetString(string key, string fallback = "")
    {
        if (Current?.TryFindResource(key) is string value)
        {
            return value;
        }
        return fallback;
    }

    public static void SwitchLanguage(string cultureCode)
    {
        var app = (App)Current;
        var resourceDict = new ResourceDictionary
        {
            Source = new Uri($"Resources/StringResources.{cultureCode}.xaml", UriKind.Relative)
        };

        // Replace language resource dictionary
        var existingLangDict = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.Contains("StringResources"));

        if (existingLangDict != null)
        {
            app.Resources.MergedDictionaries.Remove(existingLangDict);
        }

        app.Resources.MergedDictionaries.Add(resourceDict);
    }
}
