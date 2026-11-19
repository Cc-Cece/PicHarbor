using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace PicHarbor.Gui.Remote;

/// <summary>Native presentation; durable installation receipts live in the shared runtime.</summary>
internal static class RemoteDialog
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);

    internal static async Task ShowAsync(JsonObject candidate, Window? owner = null)
    {
        string key = candidate["id"]?.GetValue<string>() + ":" + (candidate["revision"]?.GetValue<int>() ?? 1);
        if (!Pending.Add(key)) return;
        await Gate.WaitAsync();
        try
        {
            var prepared = await RemoteServices.Runtime.CallAsync("dialog_prepare", new JsonObject { ["dialog"] = candidate.DeepClone() });
            if (prepared["dialog"] is not JsonObject dialog) return;
            var window = new Window
            {
                Title = dialog["title"]?.GetValue<string>() ?? "PicHarbor",
                Width = 460,
                MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 80),
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Owner = owner ?? Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            var layout = new StackPanel { Margin = new Thickness(24) };
            layout.Children.Add(new ScrollViewer { MaxHeight = Math.Max(120, window.MaxHeight - 160), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = dialog["message"]?.GetValue<string>() ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 15 } });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            foreach (var descriptor in (dialog["buttons"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var button = new Controls.PclButton { Content = descriptor["title"]?.GetValue<string>() ?? "OK", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), IsDefault = descriptor["role"]?.GetValue<string>() != "cancel" };
                button.Click += async (_, _) =>
                {
                    actions.IsEnabled = false;
                    try
                    {
                        var completed = await RemoteServices.Runtime.CallAsync("dialog_complete", new JsonObject { ["dialog"] = dialog.DeepClone(), ["button"] = descriptor["id"]?.GetValue<string>() });
                        if (completed["ok"]?.GetValue<bool>() != true) return;
                        window.Close();
                        if (descriptor["url"]?.GetValue<string>() is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { actions.IsEnabled = true; }
                };
                actions.Children.Add(button);
            }
            layout.Children.Add(actions); window.Content = layout;
            window.ShowDialog();
        }
        finally { Gate.Release(); Pending.Remove(key); }
    }
}
