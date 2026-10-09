using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PicHarbor.Gui.Util;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// Embedded Web Photo Album Control using sweet-album with PCL2 styling.
/// Bridges SQLite and SearchViewModel items with justified layout and fast thumbnail streaming.
/// </summary>
public partial class PclGalleryWebControl : UserControl
{
    private bool isInitialized = false;
    private bool isInitializing = false;
    private bool isWebReady = false;
    private readonly DispatcherTimer pushDebounceTimer;
    private static readonly ConcurrentDictionary<string, byte[]> ThumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private static CoreWebView2Environment? sharedEnvironment;
    private static readonly SemaphoreSlim envLock = new(1, 1);
    private SearchViewModel? currentSearchVM;

    public PclGalleryWebControl()
    {
        InitializeComponent();

        pushDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        pushDebounceTimer.Tick += PushDebounceTimer_Tick;

        DataContextChanged += PclGalleryWebControl_DataContextChanged;
    }

    private void PclGalleryWebControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachSearchViewModel();
    }

    private void AttachSearchViewModel()
    {
        SearchViewModel? vm = null;
        if (DataContext is MainViewModel main)
        {
            vm = main.SearchVM;
        }
        else if (DataContext is SearchViewModel directVM)
        {
            vm = directVM;
        }

        if (ReferenceEquals(currentSearchVM, vm)) return;

        if (currentSearchVM != null)
        {
            currentSearchVM.SearchResults.CollectionChanged -= SearchResults_CollectionChanged;
            currentSearchVM.PropertyChanged -= CurrentSearchVM_PropertyChanged;
        }

        currentSearchVM = vm;

        if (currentSearchVM != null)
        {
            currentSearchVM.SearchResults.CollectionChanged += SearchResults_CollectionChanged;
            currentSearchVM.PropertyChanged += CurrentSearchVM_PropertyChanged;

            if (isWebReady)
            {
                TriggerPush();
            }
        }
    }

    private void SearchResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TriggerPush();
    }

    private void CurrentSearchVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.IsGalleryView) && currentSearchVM?.IsGalleryView == true)
        {
            TriggerPush();
        }
    }

    private void TriggerPush()
    {
        pushDebounceTimer.Stop();
        pushDebounceTimer.Start();
    }

    private void PushDebounceTimer_Tick(object? sender, EventArgs e)
    {
        pushDebounceTimer.Stop();
        if (isWebReady && currentSearchVM != null)
        {
            PushPhotosToWeb(currentSearchVM.SearchResults);
        }
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        AttachSearchViewModel();

        if (isInitialized || isInitializing)
        {
            return;
        }

        isInitializing = true;
        try
        {
            await InitializeWebViewAsync();
            isInitialized = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PclGalleryWebControl] Init failed: {ex.Message}");
            ShowError(ex.Message);
        }
        finally
        {
            isInitializing = false;
        }
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        pushDebounceTimer.Stop();
    }

    private static async Task<CoreWebView2Environment> GetOrCreateSharedEnvironmentAsync()
    {
        if (sharedEnvironment != null) return sharedEnvironment;
        await envLock.WaitAsync();
        try
        {
            if (sharedEnvironment != null) return sharedEnvironment;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userDataDir = Path.Combine(localAppData, "PicHarbor", "WebView2", "Gallery");
            Directory.CreateDirectory(userDataDir);
            try
            {
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataDir);
                return sharedEnvironment;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x800700AA) || ex.Message.Contains("0x800700AA"))
            {
                string fallbackDir = Path.Combine(Path.GetTempPath(), "PicHarbor_WV2_" + Environment.ProcessId);
                Directory.CreateDirectory(fallbackDir);
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, fallbackDir);
                return sharedEnvironment;
            }
        }
        finally
        {
            envLock.Release();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        string webAssetsDir = ResolveWebAssetsPath();
        if (!Directory.Exists(webAssetsDir) || !File.Exists(Path.Combine(webAssetsDir, "index.html")))
        {
            ShowError($"找不到图库资源目录: {webAssetsDir}");
            return;
        }

        if (AlbumWebView.CoreWebView2 == null)
        {
            try
            {
                var env = await GetOrCreateSharedEnvironmentAsync();
                await AlbumWebView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x800700AA) || ex.Message.Contains("0x800700AA"))
            {
                string fallbackDir = Path.Combine(Path.GetTempPath(), "PicHarbor_WV2_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(fallbackDir);
                var fallbackEnv = await CoreWebView2Environment.CreateAsync(null, fallbackDir);
                await AlbumWebView.EnsureCoreWebView2Async(fallbackEnv);
            }
        }

        if (AlbumWebView.CoreWebView2 is null)
        {
            ShowError("无法创建 CoreWebView2 实例");
            return;
        }

        var core = AlbumWebView.CoreWebView2;
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        // Virtual host mapping for sweet-album assets
        core.SetVirtualHostNameToFolderMapping(
            "gallery.local",
            webAssetsDir,
            CoreWebView2HostResourceAccessKind.Allow);

        // Intercept local thumbnail and image stream requests
        core.AddWebResourceRequestedFilter("https://media.gallery.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += CoreWebView2_WebResourceRequested;

        // Bridge messages from JS to C#
        core.WebMessageReceived += CoreWebView2_WebMessageReceived;

        core.Navigate("https://gallery.local/index.html");
    }

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.Host.Equals("media.gallery.local", StringComparison.OrdinalIgnoreCase)) return;

            var query = HttpUtility.ParseQueryString(uri.Query);
            string? filePath = query["path"];
            string ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();
            bool isVideo = ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".3gp";

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                byte[] placeholder = GetPlaceholderTileBytes(Path.GetFileName(filePath ?? "Media"), isVideo);
                e.Response = AlbumWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(placeholder),
                    200,
                    "OK",
                    "Content-Type: image/svg+xml\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=3600");
                return;
            }

            if (uri.AbsolutePath.Equals("/thumb", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? thumbBytes = GetOrCreateThumbnailBytes(filePath);
                if (thumbBytes != null)
                {
                    var ms = new MemoryStream(thumbBytes);
                    e.Response = AlbumWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        ms,
                        200,
                        "OK",
                        "Content-Type: image/jpeg\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400");
                }
                else
                {
                    byte[] placeholder = GetPlaceholderTileBytes(Path.GetFileName(filePath), isVideo);
                    e.Response = AlbumWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(placeholder),
                        200,
                        "OK",
                        "Content-Type: image/svg+xml\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=3600");
                }
            }
            else if (uri.AbsolutePath.Equals("/image", StringComparison.OrdinalIgnoreCase))
            {
                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp")
                {
                    var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    string mime = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        ".gif" => "image/gif",
                        ".bmp" => "image/bmp",
                        _ => "application/octet-stream"
                    };
                    e.Response = AlbumWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        fs,
                        200,
                        "OK",
                        $"Content-Type: {mime}\r\nAccess-Control-Allow-Origin: *");
                }
                else
                {
                    // HEIC / Video stills: decode high-res preview via ShellServices
                    byte[]? highResBytes = GetHighResPreviewBytes(filePath);
                    if (highResBytes != null)
                    {
                        var ms = new MemoryStream(highResBytes);
                        e.Response = AlbumWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                            ms,
                            200,
                            "OK",
                            "Content-Type: image/jpeg\r\nAccess-Control-Allow-Origin: *");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebResourceRequested error] {ex.Message}");
        }
    }

    private byte[]? GetOrCreateThumbnailBytes(string filePath)
    {
        if (ThumbnailCache.TryGetValue(filePath, out var cached))
        {
            return cached;
        }

        try
        {
            ImageSource? img = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isVideo = ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".3gp";

            if (isVideo)
            {
                // First: Check if this video is a Live Photo companion video (e.g. IMG_1234.MOV with IMG_1234.HEIC/JPG)
                string? pairedStill = ImageDimensionHelper.FindLivePhotoStill(filePath);
                if (pairedStill != null)
                {
                    img = LoadFrozenBitmap(pairedStill, 240) ?? ShellServices.GetShellThumbnail(pairedStill, 240, 240, thumbnailOnly: false);
                }

                img ??= ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false);
            }
            else if (ext is ".heic" or ".heif")
            {
                img = ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false);
            }
            else
            {
                img = LoadFrozenBitmap(filePath, 240);
                img ??= ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false);
            }

            if (img is BitmapSource bs)
            {
                using var ms = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(bs));
                encoder.Save(ms);
                byte[] bytes = ms.ToArray();

                if (ThumbnailCache.Count < 2000)
                {
                    ThumbnailCache[filePath] = bytes;
                }
                return bytes;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Thumbnail gen error] {filePath}: {ex.Message}");
        }

        return null;
    }

    private static byte[] GetPlaceholderTileBytes(string fileName, bool isVideo = false)
    {
        string safeName = System.Security.SecurityElement.Escape(fileName);
        string svg = isVideo ? $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="400" height="225" viewBox="0 0 400 225">
              <defs>
                <linearGradient id="gv" x1="0%" y1="0%" x2="100%" y2="100%">
                  <stop offset="0%" stop-color="#1E293B" />
                  <stop offset="100%" stop-color="#0F172A" />
                </linearGradient>
              </defs>
              <rect width="100%" height="100%" fill="url(#gv)" rx="6" />
              <circle cx="200" cy="95" r="28" fill="#0B5BCB" opacity="0.9" />
              <polygon points="193,82 214,95 193,108" fill="#FFFFFF" />
              <text x="200" y="150" font-family="-apple-system, Segoe UI, sans-serif" font-size="13" font-weight="600" fill="#E2E8F0" text-anchor="middle">
                {safeName}
              </text>
              <text x="200" y="172" font-family="-apple-system, Segoe UI, sans-serif" font-size="11" fill="#94A3B8" text-anchor="middle">
                视频媒体
              </text>
            </svg>
            """ : $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="400" height="300" viewBox="0 0 400 300">
              <defs>
                <linearGradient id="g" x1="0%" y1="0%" x2="100%" y2="100%">
                  <stop offset="0%" stop-color="#EAF2FE" />
                  <stop offset="100%" stop-color="#D5E6FD" />
                </linearGradient>
              </defs>
              <rect width="100%" height="100%" fill="url(#g)" rx="6" />
              <g transform="translate(180, 110)" stroke="#0B5BCB" stroke-width="2.2" fill="none" stroke-linecap="round" stroke-linejoin="round">
                <rect x="0" y="0" width="40" height="32" rx="4" />
                <circle cx="20" cy="16" r="6" />
                <circle cx="32" cy="7" r="1.5" fill="#0B5BCB" />
              </g>
              <text x="200" y="172" font-family="-apple-system, Segoe UI, sans-serif" font-size="12" font-weight="600" fill="#343D4A" text-anchor="middle">
                {safeName}
              </text>
            </svg>
            """;
        return System.Text.Encoding.UTF8.GetBytes(svg);
    }

    private byte[]? GetHighResPreviewBytes(string filePath)
    {
        try
        {
            var img = ShellServices.GetShellThumbnail(filePath, 1920, 1920, thumbnailOnly: false);
            if (img is BitmapSource bs)
            {
                using var ms = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(bs));
                encoder.Save(ms);
                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HighResPreview gen error] {filePath}: {ex.Message}");
        }
        return null;
    }

    private static BitmapSource? LoadFrozenBitmap(string filePath, int decodeWidth)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string rawJson = e.WebMessageAsJson;
            var node = JsonNode.Parse(rawJson);
            if (node is not JsonObject obj) return;

            string action = obj["action"]?.GetValue<string>() ?? string.Empty;

            switch (action)
            {
                case "ready":
                    Dispatcher.Invoke(() =>
                    {
                        isWebReady = true;
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        if (currentSearchVM != null)
                        {
                            PushPhotosToWeb(currentSearchVM.SearchResults);
                        }
                    });
                    break;

                case "loadMore":
                    Dispatcher.Invoke(() =>
                    {
                        if (currentSearchVM?.LoadMoreItemsCommand.CanExecute(null) == true)
                        {
                            currentSearchVM.LoadMoreItemsCommand.Execute(null);
                        }
                    });
                    break;

                case "addManualSelection":
                    {
                        string type = obj["type"]?.GetValue<string>() ?? "iPhone";
                        var ids = obj["ids"]?.AsArray().Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        if (ids != null && ids.Count > 0 && currentSearchVM != null)
                        {
                            Dispatcher.Invoke(() => currentSearchVM.AddItemsToManualSelection(type, ids!));
                        }
                    }
                    break;

                case "removeManualSelection":
                    {
                        var ids = obj["ids"]?.AsArray().Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        if (ids != null && ids.Count > 0 && currentSearchVM != null)
                        {
                            Dispatcher.Invoke(() => currentSearchVM.RemoveItemsFromManualSelection(ids!));
                        }
                    }
                    break;

                case "toggleManualSelection":
                    {
                        string type = obj["type"]?.GetValue<string>() ?? "iPhone";
                        var ids = obj["ids"]?.AsArray().Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        if (ids != null && ids.Count > 0 && currentSearchVM != null)
                        {
                            Dispatcher.Invoke(() => currentSearchVM.ToggleItemsManualSelection(type, ids!));
                        }
                    }
                    break;

                case "revealInExplorer":
                    {
                        string? path = obj["path"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            Dispatcher.Invoke(() => ShellServices.ShowInExplorer(new[] { path }));
                        }
                    }
                    break;

                case "openWith":
                    {
                        string? path = obj["path"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            Dispatcher.Invoke(() => ShellServices.OpenWith(path, GetMainWindowHandle()));
                        }
                    }
                    break;

                case "copyFile":
                    {
                        string? path = obj["path"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            Dispatcher.Invoke(() => ShellServices.CopyFilesToClipboard(new[] { path }));
                        }
                    }
                    break;

                case "showProperties":
                    {
                        string? path = obj["path"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            Dispatcher.Invoke(() => ShellServices.ShowProperties(new[] { path }, GetMainWindowHandle()));
                        }
                    }
                    break;

                case "openPreview":
                    {
                        string? path = obj["path"]?.GetValue<string>();
                        string? id = obj["id"]?.GetValue<string>();
                        bool forceVideo = obj["forceVideo"]?.GetValue<bool>() ?? false;
                        Dispatcher.Invoke(() =>
                        {
                            if (currentSearchVM == null) return;
                            MediaSearchResultItem? target = null;
                            if (!string.IsNullOrEmpty(path))
                            {
                                target = currentSearchVM.SearchResults.FirstOrDefault(x => string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase));
                            }
                            if (target == null && !string.IsNullOrEmpty(id))
                            {
                                target = currentSearchVM.SearchResults.FirstOrDefault(x => string.Equals(x.RelativePath, id, StringComparison.OrdinalIgnoreCase));
                            }

                            if (target != null)
                            {
                                if (forceVideo && !target.IsVideo)
                                {
                                    currentSearchVM.PlayLiveVideoCommand.Execute(target);
                                }
                                else if (currentSearchVM.OpenPreviewCommand.CanExecute(target))
                                {
                                    currentSearchVM.OpenPreviewCommand.Execute(target);
                                }
                            }
                            else if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            {
                                var transient = new MediaSearchResultItem
                                {
                                    FullPath = path,
                                    RelativePath = Path.GetFileName(path),
                                    IsVideo = true
                                };
                                currentSearchVM.OpenPreviewCommand.Execute(transient);
                            }
                        });
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebMessageReceived error] {ex.Message}");
        }
    }

    private void PushPhotosToWeb(IEnumerable<MediaSearchResultItem> items)
    {
        if (!isWebReady || AlbumWebView.CoreWebView2 == null) return;

        var photosList = new List<object>();

        foreach (var item in items)
        {
            string dateStr = item.CapturedAt;
            if (string.IsNullOrWhiteSpace(dateStr))
            {
                dateStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            int w = item.PixelWidth > 0 ? item.PixelWidth : (item.MediaType == "screenshot" ? 1179 : (item.IsVideo ? 1920 : 4032));
            int h = item.PixelHeight > 0 ? item.PixelHeight : (item.MediaType == "screenshot" ? 2556 : (item.IsVideo ? 1080 : 3024));

            photosList.Add(new
            {
                id = item.RelativePath,
                width = w,
                height = h,
                takenAt = dateStr,
                thumbUrl = $"https://media.gallery.local/thumb?path={Uri.EscapeDataString(item.FullPath)}",
                url = $"https://media.gallery.local/image?path={Uri.EscapeDataString(item.FullPath)}",
                fullPath = item.FullPath,
                relativePath = item.RelativePath,
                isVideo = item.IsVideo,
                isLivePhoto = item.IsLivePhoto,
                isPendingIPhone = item.IsManualSelectedForIPhone,
                isPendingAndroid = item.IsManualSelectedForAndroid,
                isGooglePhotos = item.IsManualSelectedForGooglePhotos,
                sizeText = item.SizeText,
                cameraModel = item.CameraModel
            });
        }

        var payload = new
        {
            action = "setPhotos",
            photos = photosList
        };

        string json = JsonSerializer.Serialize(payload);
        AlbumWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void FallbackToList_Click(object sender, RoutedEventArgs e)
    {
        if (currentSearchVM != null)
        {
            currentSearchVM.IsTableView = true;
            currentSearchVM.IsGalleryView = false;
        }
    }

    private void ShowError(string details)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorOverlay.Visibility = Visibility.Visible;
            ErrorDetails.Text = details;
        });
    }

    private static string ResolveWebAssetsPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string path = Path.Combine(baseDir, "WebAssets", "gallery");
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "index.html")))
        {
            return path;
        }

        string devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "WebAssets", "gallery"));
        if (Directory.Exists(devPath) && File.Exists(Path.Combine(devPath, "index.html")))
        {
            return devPath;
        }

        return path;
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = Application.Current?.MainWindow;
        return window is not null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
    }
}
