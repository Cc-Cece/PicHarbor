using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PicHarbor.Core.Remote;
using PicHarbor.Gui.Config;

namespace PicHarbor.Gui.Remote;

internal static class RemoteServices
{
    internal static readonly RemoteRuntime Runtime = new();
    internal static event Action? ContentChanged;
    private static System.Windows.Threading.DispatcherTimer? retryTimer;
    private static readonly string PolicyPath = Path.Combine(RemoteRuntime.Home, "native-policy.json");
    private static JsonObject policy = ReadPolicy();
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "CurrentLanguage", "GroupMediaByDevice", "SyncExifToLastWriteTime", "SyncExifToCreationTime", "AutoCompleteLivePhotoPair", "AutoCompleteAaeSidecar", "AutoCompleteRawJpg", "ReadTimeoutSeconds",
        "GooglePhotosThreads", "GooglePhotosUnlimitedQuality", "GooglePhotosStorageSaver", "GooglePhotosLiveStrategy", "GooglePhotosSkipExistingFilenames", "GooglePhotosTimeoutSeconds", "GooglePhotosAutoRetryAttempts", "GooglePhotosRetryDelaySeconds",
        "PreviewMuted", "AndroidBackupMinFileSizeKb", "AndroidBackupIgnoreSmallImages", "AndroidBackupIncludePhotos", "AndroidBackupIncludeVideos"
    };
    private static JsonObject ReadPolicy() { try { return JsonNode.Parse(File.ReadAllText(PolicyPath))?.AsObject() ?? new(); } catch { return new(); } }
    internal static bool Locked(string key) => policy["forced"]?.AsArray().Any(x => x?.GetValue<string>() == key) == true;
    internal static AppConfig Overlay(AppConfig config, bool personal = false)
    {
        if (policy[personal ? "personal" : "values"] is not JsonObject values) return config;
        foreach (string key in Keys.Where(Locked))
        {
            var property = typeof(AppConfig).GetProperty(key);
            if (property is null || values[key] is not JsonNode node) continue;
            try { property.SetValue(config, node.Deserialize(property.PropertyType)); } catch (JsonException) { }
        }
        return config;
    }
    internal static async Task StartAsync(Window window)
    {
        var start = await Runtime.CallAsync("startup");
        if (start["ui"] is JsonObject ui)
        {
            if (ui["items"] is JsonArray) { var tools = new RemoteMoreControl(); await tools.SetPageAsync(ui); new Window { Title = ui["title"]?.GetValue<string>() ?? "PicHarbor", Content = tools, Width = 560, Height = 600, Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog(); }
            else MessageBox.Show(window, ui["message"]?.GetValue<string>() ?? "", ui["title"]?.GetValue<string>() ?? "PicHarbor", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        await ResolveAsync();
        await Runtime.CallAsync("sync");
        await ResolveAsync();
        if (window.DataContext is ViewModels.MainViewModel vm) { vm.SettingsVM.ReloadRemotePreferences(); vm.CurrentLanguage = AppSettings.Load().CurrentLanguage; App.SwitchLanguage(vm.CurrentLanguage); }
        ContentChanged?.Invoke();
        retryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        retryTimer.Tick += async (_, _) => await Runtime.CallAsync("flush");
        retryTimer.Start();
        window.Closed += (_, _) => retryTimer?.Stop();
        window.PreviewMouseDown += GuardMouse;
        window.PreviewKeyDown += (_, e) => { if ((e.Key is Key.Space or Key.Enter) && Guard(e.OriginalSource as DependencyObject)) e.Handled = true; };
    }
    internal static async Task ResolveAsync()
    {
        var config = AppSettings.Load(); var values = new JsonObject();
        foreach (string key in Keys) values[key] = JsonSerializer.SerializeToNode(typeof(AppConfig).GetProperty(key)!.GetValue(config));
        var result = await Runtime.CallAsync("resolve", new JsonObject { ["scope"] = config.GooglePhotosAccountEmail, ["values"] = values });
        if (result["ok"]?.GetValue<bool>() != true) return;
        policy = result;
        Directory.CreateDirectory(RemoteRuntime.Home);
        string staging = PolicyPath + ".new"; await File.WriteAllTextAsync(staging, policy.ToJsonString()); File.Move(staging, PolicyPath, true);
        if (result["values"] is JsonObject resolved)
        {
            foreach (string key in Keys.Where(k => !Locked(k))) if (resolved[key] is JsonNode value)
                typeof(AppConfig).GetProperty(key)!.SetValue(config, value.Deserialize(typeof(AppConfig).GetProperty(key)!.PropertyType));
            AppSettings.Save(config);
        }
    }
    private static void GuardMouse(object sender, MouseButtonEventArgs e) { if (Guard(e.OriginalSource as DependencyObject)) e.Handled = true; }
    internal static bool Intercept(string key)
    {
        if (policy["hints"] is JsonObject hints && hints[key] is JsonObject hint)
        {
            string receipt = hint["id"]?.GetValue<string>() ?? "", scope = hint["scope"]?.GetValue<string>() ?? policy["scope"]?.GetValue<string>() ?? "";
            string message = hint["message"]?.GetValue<string>() ?? "此设置已由应用配置调整。";
            hints.Remove(key); File.WriteAllText(PolicyPath, policy.ToJsonString());
            _ = Runtime.CallAsync("hint", new JsonObject { ["scope"] = scope, ["receipt"] = receipt });
            MessageBox.Show(Application.Current.MainWindow, message, "PicHarbor", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        return Locked(key);
    }
    private static bool Guard(DependencyObject? child)
    {
        while (child is not null)
        {
            if (child is FrameworkElement element)
            {
                var entries = element.GetLocalValueEnumerator();
                while (entries.MoveNext())
                {
                    var expression = BindingOperations.GetBindingExpression(element, entries.Current.Property);
                    string? key = expression?.ParentBinding.Path?.Path.Split('.').LastOrDefault();
                    if (key == "SelectedGooglePhotosQualityIndex") key = Locked("GooglePhotosUnlimitedQuality") || (policy["hints"] as JsonObject)?.ContainsKey("GooglePhotosUnlimitedQuality") == true ? "GooglePhotosUnlimitedQuality" : "GooglePhotosStorageSaver";
                    if (key == "SelectedGooglePhotosLiveStrategyIndex") key = "GooglePhotosLiveStrategy";
                    if (key is null || !Keys.Contains(key)) continue;
                    if (policy["hints"] is JsonObject hints && hints[key] is JsonObject hint)
                    {
                        var receipt = hint["id"]?.GetValue<string>() ?? "";
                        string message = hint["message"]?.GetValue<string>() ?? "此设置已由应用配置调整。";
                        hints.Remove(key); File.WriteAllText(PolicyPath, policy.ToJsonString());
                        _ = Runtime.CallAsync("hint", new JsonObject { ["scope"] = hint["scope"]?.GetValue<string>() ?? policy["scope"]?.GetValue<string>(), ["receipt"] = receipt });
                        MessageBox.Show(Application.Current.MainWindow, message, "PicHarbor", MessageBoxButton.OK, MessageBoxImage.Information);
                        return true;
                    }
                    if (Locked(key)) return true;
                }
            }
            child = child is Visual || child is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : (child as FrameworkContentElement)?.Parent;
        }
        return false;
    }
}
