using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PicHarbor.Gui.Config;

namespace PicHarbor.Gui.Remote;

/// <summary>The page framework is native; all its contents come from the signed script.</summary>
public sealed class RemoteMoreControl : UserControl
{
    private readonly StackPanel panel = new() { Margin = new Thickness(22) };
    private readonly Dictionary<string, string> values = new();
    private readonly List<JsonObject> files = new();
    private readonly Stack<JsonObject> parents = new();
    private JsonObject page = new();
    private bool loaded;
    public RemoteMoreControl()
    {
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { RemoteServices.ContentChanged -= Refresh; RemoteServices.ContentChanged += Refresh; if (IsVisible && !loaded) Refresh(); };
        Unloaded += (_, _) => RemoteServices.ContentChanged -= Refresh;
        IsVisibleChanged += (_, _) => { if (IsVisible && !loaded) Refresh(); };
    }
    private async void Refresh()
    {
        if (!IsVisible || parents.Count > 0 || values.Count > 0) return;
        loaded = true;
        var result = await RemoteServices.Runtime.CallAsync("more");
        if (result["page"] is JsonObject value) await SetPageAsync(value);
    }
    /// <summary>Renders an already verified page returned by the runtime.</summary>
    public Task SetPageAsync(JsonObject value)
    {
        loaded = true; page = value; panel.Children.Clear();
        if (parents.Count > 0) AddButton(panel, "返回", async () => { values.Clear(); await SetPageAsync(parents.Pop()); });
        panel.Children.Add(new TextBlock { Text = value["title"]?.GetValue<string>() ?? "更多", FontSize = 24, Margin = new Thickness(0, 0, 0, 22) });
        Render(panel, value["items"] as JsonArray ?? new());
        return Task.CompletedTask;
    }
    private static Button AddButton(Panel target, string title, Func<Task> action)
    {
        var b = new Controls.PclButton { Content = title, Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(15, 9, 15, 9), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch { MessageBox.Show("操作未完成，请稍后重试。", "PicHarbor"); } finally { b.IsEnabled = true; } };
        target.Children.Add(b); return b;
    }
    private void Render(Panel target, JsonArray items)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            string id = item["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(), title = item["title"]?.GetValue<string>() ?? "";
            switch (item["type"]?.GetValue<string>())
            {
                case "group":
                    target.Children.Add(new TextBlock { Text = title, FontSize = 17, Margin = new Thickness(0, 18, 0, 9) });
                    var group = new StackPanel(); target.Children.Add(group); Render(group, item["items"] as JsonArray ?? new()); break;
                case "text": case "result": target.Children.Add(new TextBlock { Text = item["text"]?.GetValue<string>() ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 12), FontSize = 15 }); break;
                case "input": case "multiline": case "number":
                    target.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 7) });
                    var input = new TextBox { Text = values.GetValueOrDefault(id, ""), MaxLength = 10000, Padding = new Thickness(10), AcceptsReturn = item["type"]!.GetValue<string>() == "multiline", TextWrapping = TextWrapping.Wrap, MinHeight = item["type"]!.GetValue<string>() == "multiline" ? 130 : 36 };
                    input.TextChanged += (_, _) => values[id] = input.Text; target.Children.Add(input); break;
                case "toggle": var check = new Controls.PclCheckBox { Content = title, IsChecked = values.GetValueOrDefault(id) == "true", Margin = new Thickness(0, 10, 0, 10) }; check.Checked += (_, _) => values[id] = "true"; check.Unchecked += (_, _) => values[id] = "false"; target.Children.Add(check); break;
                case "select":
                    target.Children.Add(new TextBlock { Text = title });
                    var select = new ComboBox { Margin = new Thickness(0, 8, 0, 12) };
                    foreach (var option in (item["options"] as JsonArray ?? new()).OfType<JsonObject>()) select.Items.Add(new ComboBoxItem { Content = option["title"]?.GetValue<string>() ?? "", Tag = option["value"]?.GetValue<string>() ?? "" });
                    select.SelectionChanged += (_, _) => values[id] = (select.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; target.Children.Add(select); break;
                case "attachments":
                    target.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 15, 0, 7) });
                    AddButton(target, "添加截图", () => SelectFileAsync(true)); AddButton(target, "诊断文件", () => SelectFileAsync(false));
                    foreach (var file in files.ToArray())
                    {
                        var row = new StackPanel { Orientation = Orientation.Horizontal };
                        if (file["kind"]!.GetValue<string>() == "screenshot") row.Children.Add(new Image { Source = new BitmapImage(new Uri(file["path"]!.GetValue<string>())), Width = 70, Height = 70, Margin = new Thickness(0, 0, 12, 0) });
                        else row.Children.Add(new TextBlock { Text = "诊断文件", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
                        AddButton(row, "移除", async () => { files.Remove(file); await SetPageAsync(page); }); target.Children.Add(row);
                    }
                    break;
                case "button": AddButton(target, title, async () =>
                {
                    if (item["url"]?.GetValue<string>() is string url && Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme == "https") { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return; }
                    if (item["capability"]?.GetValue<string>() == "feedback.submit")
                    {
                        var config = AppSettings.Load(); var data = new JsonObject(); foreach (var v in values) data[v.Key] = v.Value;
                        data["email"] = config.GooglePhotosAccountEmail;
                        data["account_ref"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Core.Remote.RemoteRuntime.Home + config.GooglePhotosAccountEmail)));
                        var result = await RemoteServices.Runtime.CallAsync("feedback", new JsonObject { ["values"] = data, ["files"] = new JsonArray(files.Select(f => (JsonNode)f.DeepClone()).ToArray()) });
                        if (result["ok"]?.GetValue<bool>() == true) { MessageBox.Show(result["queued"]?.GetValue<bool>() == true ? "反馈已保存在本机，联网后自动发送。" : "反馈已收到：" + result["id"]?.GetValue<string>(), "PicHarbor"); values.Clear(); files.Clear(); await SetPageAsync(page); }
                        else MessageBox.Show("请填写反馈内容，并检查附件后重试。", "PicHarbor");
                    }
                    else { var result = await RemoteServices.Runtime.CallAsync("action", new JsonObject { ["action"] = item["action"]?.GetValue<string>() ?? "", ["values"] = JsonSerializerNode(values) }); if (result["page"] is JsonObject next) { parents.Push(page); values.Clear(); await SetPageAsync(next); } }
                }); break;
                case "loading": target.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(0, 12, 0, 12) }); break;
            }
        }
    }
    private static JsonObject JsonSerializerNode(Dictionary<string, string> data) { var obj = new JsonObject(); foreach (var v in data) obj[v.Key] = v.Value; return obj; }
    private async Task SelectFileAsync(bool image)
    {
        string kind = image ? "screenshot" : "diagnostic";
        if (files.Count(f => f["kind"]!.GetValue<string>() == kind) >= (image ? 3 : 1)) return;
        var dialog = new OpenFileDialog { Filter = image ? "图片|*.png;*.jpg;*.jpeg" : "诊断文件|*.zip" };
        if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > (image ? 10 : 5) * 1024 * 1024) { MessageBox.Show("附件过大。", "PicHarbor"); return; }
        string path = Path.Combine(Path.GetTempPath(), "PicHarbor-" + Guid.NewGuid() + (image ? ".jpg" : ".zip"));
        if (image)
        {
            using var input = File.OpenRead(dialog.FileName);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var original = decoder.Frames[0]; double scale = Math.Min(1, 2048.0 / Math.Max(original.PixelWidth, original.PixelHeight));
            var bitmap = new TransformedBitmap(original, new ScaleTransform(scale, scale));
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path); encoder.Save(output);
        }
        else File.Copy(dialog.FileName, path);
        if (new FileInfo(path).Length > 5 * 1024 * 1024) { File.Delete(path); return; }
        files.Add(new JsonObject { ["path"] = path, ["kind"] = kind, ["mime"] = image ? "image/jpeg" : "application/zip" });
        await SetPageAsync(page);
    }
}
