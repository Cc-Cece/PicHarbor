using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PicHarbor.Core;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Config;
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
    private static readonly string DiskCacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PicHarbor", "Cache", "Thumbnails");
    // Real 240px frames in this cache start around 2 KB. The 631-byte files are 1×1 JPEGs
    // that Windows returned before the HEVC thumbnail handler could decode, and any length > 0
    // used to count as a hit, so they were never generated again.
    private const int MinimumThumbnailBytes = 1024;
    private const string MediaUrlRevision = "3";
    private const string PlaceholderResponseHeaders =
        "Content-Type: image/svg+xml\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store";
    private static int diskCacheScrubbed;
    private static CoreWebView2Environment? sharedEnvironment;
    private static readonly SemaphoreSlim envLock = new(1, 1);
    private SearchViewModel? currentSearchVM;
    private string? lastPushedFingerprint;
    private readonly DispatcherTimer playerClock;
    private bool nativePlayerOpen;
    private PreviewKind previewKind;
    private bool nativeClosing;
    private bool pendingPlay;
    private bool playing;
    private bool clockWriting;
    private string? currentVideoPath;
    private string? currentStillPath;
    private string? currentItemPath;
    // HEIC decode stays off the UI thread. A finish from the previous item must not replace the still.
    private int stillLoadGeneration;
    private readonly List<PreviewEntry> previewEntries = new();
    private int previewIndex = -1;
    private double viewScale = 1;
    private int mediaPixelWidth;
    private int mediaPixelHeight;
    private double panX;
    private double panY;
    private double rotationDegrees;
    private bool panning;
    private bool panMoved;
    private Point panStart;
    private double panStartX;
    private double panStartY;
    private const double ZoomStep = 1.25;
    private const double MinViewScale = 0.2;
    private const double MaxViewScale = 8;
    private Window? playerKeyWindow;
    private SystemVideoFramePlayer? framePlayer;
    private bool previewMuted = true;
    private bool fullStillReady;
    private readonly DispatcherTimer previewSpinnerTimer;

    public PclGalleryWebControl()
    {
        InitializeComponent();
        previewMuted = AppSettings.LoadPreviewMuted();
        ApplyMuteChrome();
        LocalizationService.LanguageChanged += OnAppLanguageChanged;
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= OnAppLanguageChanged;

        pushDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        pushDebounceTimer.Tick += PushDebounceTimer_Tick;

        playerClock = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        playerClock.Tick += PlayerClock_Tick;

        previewSpinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        previewSpinnerTimer.Tick += PreviewSpinnerTimer_Tick;

        PclModalHost.HasAnyModalOpenChanged += (_, _) =>
        {
            Dispatcher.Invoke(ApplyWebViewVisibility);
        };

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
            if (main.IsAnyModalOpen || nativePlayerOpen)
            {
                ApplyWebViewVisibility();
            }
        }
        else if (DataContext is SearchViewModel directVM)
        {
            vm = directVM;
        }

        if (ReferenceEquals(currentSearchVM, vm)) return;

        if (currentSearchVM != null)
        {
            currentSearchVM.HitsUpdated -= CurrentSearchVM_HitsUpdated;
            currentSearchVM.PropertyChanged -= CurrentSearchVM_PropertyChanged;
        }

        currentSearchVM = vm;

        if (currentSearchVM != null)
        {
            currentSearchVM.HitsUpdated += CurrentSearchVM_HitsUpdated;
            currentSearchVM.PropertyChanged += CurrentSearchVM_PropertyChanged;

            if (isWebReady)
            {
                TriggerPush();
            }
        }
    }

    private void CurrentSearchVM_HitsUpdated(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => CurrentSearchVM_HitsUpdated(sender, e));
            return;
        }
        TriggerPush();
    }

    private void CurrentSearchVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => CurrentSearchVM_PropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName == nameof(SearchViewModel.IsGalleryView))
        {
            if (currentSearchVM?.IsGalleryView == true)
            {
                TriggerPush();
            }
            else
            {
                CloseNativePlayer();
            }
        }
        else if (e.PropertyName == nameof(SearchViewModel.IsSearching))
        {
            if (currentSearchVM?.IsSearching == true)
            {
                _ = AlbumWebView.CoreWebView2?.ExecuteScriptAsync("if (window.setGalleryLoading) window.setGalleryLoading(true);");
            }
            else
            {
                _ = AlbumWebView.CoreWebView2?.ExecuteScriptAsync("if (window.setGalleryLoading) window.setGalleryLoading(false);");
                TriggerPush();
            }
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
            PushPhotosToWeb();
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
        CloseNativePlayer();
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
            var options = new CoreWebView2EnvironmentOptions
            {
                // Live Photo and iPhone video are HEVC. WebView2 uses the installed Media Foundation decoder.
                AdditionalBrowserArguments = "--enable-features=PlatformHEVCDecoderSupport"
            };
            try
            {
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
                return sharedEnvironment;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x800700AA) || ex.Message.Contains("0x800700AA"))
            {
                string fallbackDir = Path.Combine(Path.GetTempPath(), "PicHarbor_WV2_" + Environment.ProcessId);
                Directory.CreateDirectory(fallbackDir);
                sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, fallbackDir, options);
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
            ShowError(string.Format(App.GetString("FmtGalleryAssetsMissing", "找不到图库资源目录: {0}"), webAssetsDir));
            return;
        }

        AlbumWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF8, 0xFA, 0xFC);
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
                var fallbackEnv = await CoreWebView2Environment.CreateAsync(
                    null,
                    fallbackDir,
                    new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = "--enable-features=PlatformHEVCDecoderSupport"
                    });
                await AlbumWebView.EnsureCoreWebView2Async(fallbackEnv);
            }
        }

        if (AlbumWebView.CoreWebView2 is null)
        {
            ShowError(App.GetString("GalleryNoWebView", "无法创建 CoreWebView2 实例"));
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

        core.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess || isWebReady)
            {
                return;
            }

            if (core.Source?.Contains("gallery.local", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            ShowError(App.GetString("GalleryPageNotLoaded", "图库页面没有载入完成"));
        };
        core.Navigate($"https://gallery.local/index.html?v=8&lang={GalleryLocaleCode}");
    }

    private static string GetThumbnailCacheKey(string filePath)
    {
        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(filePath).Ticks; } catch { }
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{filePath.ToLowerInvariant()}_{ticks}"));
        return Convert.ToHexString(hash);
    }

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = sharedEnvironment ?? AlbumWebView.CoreWebView2?.Environment;
        if (env == null) return;

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
                e.Response = env.CreateWebResourceResponse(
                    new MemoryStream(placeholder),
                    200,
                    "OK",
                    PlaceholderResponseHeaders);
                return;
            }

            string? rangeHeader = null;
            try
            {
                rangeHeader = e.Request.Headers.GetHeader("Range");
            }
            catch { }

            var deferral = e.GetDeferral();
            var dispatcher = Dispatcher;

            ThreadPool.QueueUserWorkItem(async _ =>
            {
                byte[]? thumbBytes = null;
                byte[]? placeholderBytes = null;
                byte[]? highResBytes = null;
                Stream? fileStream = null;
                string? fileMime = null;
                bool isThumb = uri.AbsolutePath.Equals("/thumb", StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (isThumb)
                    {
                        thumbBytes = await GetOrCreateThumbnailBytesAsync(filePath).ConfigureAwait(false);
                        if (thumbBytes == null)
                        {
                            placeholderBytes = GetPlaceholderTileBytes(Path.GetFileName(filePath), isVideo);
                        }
                    }
                    else if (uri.AbsolutePath.Equals("/image", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp")
                        {
                            fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fileMime = ext switch
                            {
                                ".jpg" or ".jpeg" => "image/jpeg",
                                ".png" => "image/png",
                                ".webp" => "image/webp",
                                ".gif" => "image/gif",
                                ".bmp" => "image/bmp",
                                _ => "application/octet-stream"
                            };
                        }
                        else if (isVideo)
                        {
                            fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            fileMime = ext switch
                            {
                                ".webm" => "video/webm",
                                ".ogv" or ".ogg" => "video/ogg",
                                _ => "video/mp4" // MP4, MOV, M4V all use ISO-BMFF container; Chromium decodes MOV when served as video/mp4
                            };
                        }
                        else
                        {
                            highResBytes = GetHighResPreviewBytes(filePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebResourceRequested bg worker error] {ex.Message}");
                }

                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (isThumb)
                        {
                            if (thumbBytes != null)
                            {
                                var ms = new MemoryStream(thumbBytes);
                                e.Response = env.CreateWebResourceResponse(
                                    ms,
                                    200,
                                    "OK",
                                    "Content-Type: image/jpeg\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400");
                            }
                            else if (placeholderBytes != null)
                            {
                                var ms = new MemoryStream(placeholderBytes);
                                e.Response = env.CreateWebResourceResponse(
                                    ms,
                                    200,
                                    "OK",
                                    PlaceholderResponseHeaders);
                            }
                        }
                        else
                        {
                            if (fileStream != null && fileMime != null)
                            {
                                if (isVideo && !string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes="))
                                {
                                    long totalLength = fileStream.Length;
                                    long start = 0;
                                    long end = totalLength - 1;
                                    string rangeSpec = rangeHeader.Substring("bytes=".Length).Trim();
                                    int dash = rangeSpec.IndexOf('-');
                                    if (dash >= 0)
                                    {
                                        string sStr = rangeSpec.Substring(0, dash);
                                        string eStr = rangeSpec.Substring(dash + 1);
                                        if (!string.IsNullOrEmpty(sStr) && long.TryParse(sStr, out var sVal))
                                        {
                                            start = Math.Clamp(sVal, 0, totalLength - 1);
                                            if (!string.IsNullOrEmpty(eStr) && long.TryParse(eStr, out var eVal))
                                            {
                                                end = Math.Clamp(eVal, start, totalLength - 1);
                                            }
                                        }
                                        else if (!string.IsNullOrEmpty(eStr) && long.TryParse(eStr, out var suffixVal))
                                        {
                                            start = Math.Max(0, totalLength - suffixVal);
                                            end = totalLength - 1;
                                        }
                                    }

                                    long chunkLength = end - start + 1;
                                    fileStream.Seek(start, SeekOrigin.Begin);
                                    var sliceStream = new BoundedStream(fileStream, chunkLength);
                                    string headers = $"Content-Type: {fileMime}\r\nContent-Range: bytes {start}-{end}/{totalLength}\r\nContent-Length: {chunkLength}\r\nAccept-Ranges: bytes\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400";
                                    e.Response = env.CreateWebResourceResponse(
                                        sliceStream,
                                        206,
                                        "Partial Content",
                                        headers);
                                }
                                else
                                {
                                    string headers = isVideo
                                        ? $"Content-Type: {fileMime}\r\nAccept-Ranges: bytes\r\nContent-Length: {fileStream.Length}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400"
                                        : $"Content-Type: {fileMime}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400";
                                    e.Response = env.CreateWebResourceResponse(
                                        fileStream,
                                        200,
                                        "OK",
                                        headers);
                                }
                            }
                            else if (highResBytes != null)
                            {
                                var ms = new MemoryStream(highResBytes);
                                e.Response = env.CreateWebResourceResponse(
                                    ms,
                                    200,
                                    "OK",
                                    "Content-Type: image/jpeg\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: public, max-age=86400");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WebResourceRequested dispatcher error] {ex.Message}");
                    }
                    finally
                    {
                        deferral.Complete();
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebResourceRequested sync error] {ex.Message}");
        }
    }

    private static string GalleryMediaUrl(string kind, string path)
        => $"https://media.gallery.local/{kind}?path={Uri.EscapeDataString(path)}&v={MediaUrlRevision}";

    private static bool IsUsableThumbnail(byte[]? bytes)
        => bytes is { Length: >= MinimumThumbnailBytes };

    private static void ScrubTinyThumbnailCache()
    {
        if (Interlocked.Exchange(ref diskCacheScrubbed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(DiskCacheFolder))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(DiskCacheFolder, "*.jpg"))
            {
                try
                {
                    if (new FileInfo(file).Length < MinimumThumbnailBytes)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static async Task<byte[]?> GetOrCreateThumbnailBytesAsync(string filePath)
    {
        ScrubTinyThumbnailCache();

        if (ThumbnailCache.TryGetValue(filePath, out var cached))
        {
            if (IsUsableThumbnail(cached))
            {
                return cached;
            }

            ThumbnailCache.TryRemove(filePath, out _);
        }

        string cacheKey = GetThumbnailCacheKey(filePath);
        string diskCacheFile = Path.Combine(DiskCacheFolder, cacheKey + ".jpg");

        // Tier 2: Check persistent disk cache
        try
        {
            if (File.Exists(diskCacheFile))
            {
                byte[] diskBytes = await File.ReadAllBytesAsync(diskCacheFile).ConfigureAwait(false);
                if (IsUsableThumbnail(diskBytes) && !ShellServices.IsJpegShellFileIcon(diskBytes, filePath))
                {
                    if (ThumbnailCache.Count < 3000)
                    {
                        ThumbnailCache[filePath] = diskBytes;
                    }
                    return diskBytes;
                }

                try { File.Delete(diskCacheFile); } catch { }
            }
        }
        catch { }

        // Tier 3: Extract & Generate thumbnail
        try
        {
            ImageSource? img = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isVideo = ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".3gp";

            if (isVideo)
            {
                string? pairedStill = ImageDimensionHelper.FindLivePhotoStill(filePath);
                if (pairedStill != null)
                {
                    string stillExt = Path.GetExtension(pairedStill);
                    bool heifStill = stillExt.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                        || stillExt.Equals(".heif", StringComparison.OrdinalIgnoreCase);
                    img = heifStill
                        ? ShellServices.DecodeHeifStill(pairedStill, 240) ?? ShellServices.GetShellThumbnail(pairedStill, 240, 240, thumbnailOnly: false)
                        : LoadFrozenBitmap(pairedStill, 240) ?? ShellServices.GetShellThumbnail(pairedStill, 240, 240, thumbnailOnly: false);
                }

                if (img == null)
                {
                    try
                    {
                        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                        using var thumb = await storageFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 240, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                        if (thumb != null && thumb.Size > 0)
                        {
                            using var winrtStream = thumb.AsStreamForRead();
                            using var ms = new MemoryStream();
                            await winrtStream.CopyToAsync(ms).ConfigureAwait(false);
                            byte[] rawBytes = ms.ToArray();
                            if (IsUsableThumbnail(rawBytes) && !ShellServices.IsJpegShellFileIcon(rawBytes, filePath))
                            {
                                if (ThumbnailCache.Count < 3000) ThumbnailCache[filePath] = rawBytes;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        Directory.CreateDirectory(DiskCacheFolder);
                                        await File.WriteAllBytesAsync(diskCacheFile, rawBytes).ConfigureAwait(false);
                                    }
                                    catch { }
                                });
                                return rawBytes;
                            }
                        }
                    }
                    catch { }

                    img = ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false)
                        ?? await ShellServices.GetVideoFrameAsync(filePath, 240).ConfigureAwait(false);
                }
            }
            else if (ext is ".heic" or ".heif")
            {
                img = ShellServices.DecodeHeifStill(filePath, 240)
                    ?? ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false);
            }
            else
            {
                img = LoadFrozenBitmap(filePath, 240);
                img ??= ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false);
            }

            if (img is BitmapSource bs && bs.PixelWidth >= 8 && bs.PixelHeight >= 8)
            {
                using var ms = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
                encoder.Frames.Add(BitmapFrame.Create(bs));
                encoder.Save(ms);
                byte[] bytes = ms.ToArray();

                if (ThumbnailCache.Count < 3000)
                {
                    ThumbnailCache[filePath] = bytes;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        Directory.CreateDirectory(DiskCacheFolder);
                        await File.WriteAllBytesAsync(diskCacheFile, bytes).ConfigureAwait(false);
                    }
                    catch { }
                });

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
        string videoLabel = System.Security.SecurityElement.Escape(App.GetString("GalleryPlaceholderVideo", "视频媒体"));
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
                {videoLabel}
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
            string ext = Path.GetExtension(filePath);
            bool heif = ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);
            var img = heif
                ? ShellServices.DecodeHeifStill(filePath, 1920) ?? ShellServices.GetShellThumbnail(filePath, 1920, 1920, thumbnailOnly: false)
                : ShellServices.GetShellThumbnail(filePath, 1920, 1920, thumbnailOnly: false);
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
                        PostGalleryLocale();
                        if (currentSearchVM != null)
                        {
                            PushPhotosToWeb();
                        }

                        ApplyWebViewVisibility();
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
                        string type = obj["type"]?.GetValue<string>() ?? "Unified";
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
                        string type = obj["type"]?.GetValue<string>() ?? "Unified";
                        var ids = obj["ids"]?.AsArray().Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        if (ids != null && ids.Count > 0 && currentSearchVM != null)
                        {
                            Dispatcher.Invoke(() => currentSearchVM.ToggleItemsManualSelection("Unified", ids!));
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

                case "playNative":
                case "openNativePreview":
                    {
                        int index = ReadJsonInt(obj, "index");
                        string stillPath = obj["path"]?.GetValue<string>() ?? "";
                        Dispatcher.Invoke(() => OpenNativePreview(index, stillPath));
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

                            if (target == null && currentSearchVM.GetCurrentHits().Count > 0)
                            {
                                var hit = currentSearchVM.GetCurrentHits().FirstOrDefault(h =>
                                    (!string.IsNullOrEmpty(path) && string.Equals(Path.Combine(currentSearchVM.ArchivePath, h.RelativePath), path, StringComparison.OrdinalIgnoreCase)) ||
                                    (!string.IsNullOrEmpty(id) && string.Equals(h.RelativePath, id, StringComparison.OrdinalIgnoreCase)));
                                if (hit != null)
                                {
                                    string full = Path.Combine(currentSearchVM.ArchivePath, hit.RelativePath);
                                    string ext = Path.GetExtension(full).ToLowerInvariant();
                                    bool isVid = hit.Type == MediaType.Video || ext is ".mp4" or ".mov" or ".m4v" or ".avi" or ".mkv";
                                    target = new MediaSearchResultItem
                                    {
                                        FullPath = full,
                                        RelativePath = hit.RelativePath,
                                        CapturedAt = hit.CapturedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                        SizeText = ByteSize.Humanize(hit.SizeBytes),
                                        MediaType = hit.Type.ToString().ToLowerInvariant(),
                                        IsVideo = isVid,
                                        HasThumbnail = true
                                    };
                                }
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

    private void PushPhotosToWeb()
    {
        if (!isWebReady || AlbumWebView.CoreWebView2 == null || currentSearchVM == null) return;

        var hits = currentSearchVM.GetCurrentHits();
        string firstId = hits.Count > 0 ? hits[0].RelativePath : (currentSearchVM.SearchResults.Count > 0 ? currentSearchVM.SearchResults[0].RelativePath : "");
        string lastId = hits.Count > 0 ? hits[^1].RelativePath : (currentSearchVM.SearchResults.Count > 0 ? currentSearchVM.SearchResults[^1].RelativePath : "");
        int totalCount = hits.Count > 0 ? hits.Count : currentSearchVM.SearchResults.Count;
        int selCount = (currentSearchVM.IPhoneSyncVM?.GetManualSelectionPathsSet().Count ?? 0)
                     + (currentSearchVM.AndroidSyncVM?.GetManualSelectionPathsSet().Count ?? 0)
                     + (currentSearchVM.GooglePhotosVM?.GetManualSelectionPathsSet().Count ?? 0);
        string archivePath = currentSearchVM.ArchivePath;
        string fingerprint = $"{totalCount}:{firstId}:{lastId}:{selCount}:{archivePath}:{currentSearchVM.SortModeIndex}:{currentSearchVM.SortDescending}";

        if (fingerprint == lastPushedFingerprint)
        {
            return;
        }
        lastPushedFingerprint = fingerprint;

        var photosList = new List<object>(totalCount);
        var entries = new List<PreviewEntry>(Math.Max(totalCount, 0));

        if (hits.Count > 0)
        {
            var display = currentSearchVM.GetDisplayMeta();
            var iphoneSet = currentSearchVM.IPhoneSyncVM?.GetManualSelectionPathsSet() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var androidSet = currentSearchVM.AndroidSyncVM?.GetManualSelectionPathsSet() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var googleSet = currentSearchVM.GooglePhotosVM?.GetManualSelectionPathsSet() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hit in hits)
            {
                string rel = hit.RelativePath;
                string full = Path.Combine(archivePath, rel);
                string ext = Path.GetExtension(rel).TrimStart('.').ToUpperInvariant();
                bool isVideo = hit.Type == MediaType.Video || ext is "MP4" or "MOV" or "M4V" or "AVI" or "MKV";
                bool isLive = currentSearchVM.IsLivePhotoRelativePath(rel);
                string? liveVideoFullPath = isLive && !isVideo ? currentSearchVM.GetLiveVideoFullPath(rel) : null;
                string? liveVideoUrl = string.IsNullOrEmpty(liveVideoFullPath)
                    ? null
                    : GalleryMediaUrl("image", liveVideoFullPath);

                if (string.IsNullOrWhiteSpace(ext)) ext = isVideo ? "MP4" : "JPG";

                string dateStr = hit.CapturedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                int w = 0, h = 0;
                if (ImageDimensionHelper.TryGetCachedDimensions(full, out var cachedDims))
                {
                    w = cachedDims.Width;
                    h = cachedDims.Height;
                }
                else
                {
                    if (hit.Type == MediaType.Screenshot)
                    {
                        w = 1179;
                        h = 2556;
                    }
                    else if (isVideo)
                    {
                        w = 1920;
                        h = 1080;
                    }
                    else
                    {
                        w = 4032;
                        h = 3024;
                    }
                }

                string camera = display.TryGetValue(rel, out var meta) ? meta.Camera : "";

                photosList.Add(new
                {
                    id = rel,
                    width = w,
                    height = h,
                    takenAt = dateStr,
                    thumbUrl = GalleryMediaUrl("thumb", full),
                    url = GalleryMediaUrl("image", full),
                    fullPath = full,
                    relativePath = rel,
                    isVideo = isVideo,
                    isLivePhoto = isLive,
                    liveVideoUrl,
                    liveVideoPath = liveVideoFullPath,
                    format = ext,
                    mediaType = hit.Type.ToString().ToLowerInvariant(),
                    isPending = iphoneSet.Contains(rel) || androidSet.Contains(rel) || googleSet.Contains(rel),
                    isPendingIPhone = iphoneSet.Contains(rel) || androidSet.Contains(rel) || googleSet.Contains(rel),
                    isPendingAndroid = iphoneSet.Contains(rel) || androidSet.Contains(rel) || googleSet.Contains(rel),
                    isGooglePhotos = googleSet.Contains(rel),
                    sizeText = ByteSize.Humanize(hit.SizeBytes),
                    cameraModel = camera
                });
                entries.Add(new PreviewEntry
                {
                    ItemId = rel,
                    FullPath = full,
                    IsVideo = isVideo,
                    LiveVideoPath = liveVideoFullPath
                });
            }
        }
        else
        {
            foreach (var item in currentSearchVM.SearchResults)
            {
                string dateStr = item.CapturedAt;
                if (string.IsNullOrWhiteSpace(dateStr))
                {
                    dateStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }

                int w = item.PixelWidth > 0 ? item.PixelWidth : (item.MediaType == "screenshot" ? 1179 : (item.IsVideo ? 1920 : 4032));
                int h = item.PixelHeight > 0 ? item.PixelHeight : (item.MediaType == "screenshot" ? 2556 : (item.IsVideo ? 1080 : 3024));

                string ext = Path.GetExtension(item.FullPath ?? "").TrimStart('.').ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(ext)) ext = item.IsVideo ? "MP4" : "JPG";
                string? fallbackLiveVideo = item.IsLivePhoto && !item.IsVideo
                    ? currentSearchVM.GetLiveVideoFullPath(item.RelativePath ?? "")
                    : null;
                string? fallbackLiveVideoUrl = string.IsNullOrEmpty(fallbackLiveVideo)
                    ? null
                    : GalleryMediaUrl("image", fallbackLiveVideo);

                photosList.Add(new
                {
                    id = item.RelativePath,
                    width = w,
                    height = h,
                    takenAt = dateStr,
                    thumbUrl = GalleryMediaUrl("thumb", item.FullPath ?? ""),
                    url = GalleryMediaUrl("image", item.FullPath ?? ""),
                    fullPath = item.FullPath,
                    relativePath = item.RelativePath,
                    isVideo = item.IsVideo,
                    isLivePhoto = item.IsLivePhoto,
                    liveVideoUrl = fallbackLiveVideoUrl,
                    liveVideoPath = fallbackLiveVideo,
                    format = ext,
                    mediaType = item.MediaType,
                    isPending = item.IsManualSelectedForIPhone || item.IsManualSelectedForAndroid || item.IsManualSelectedForGooglePhotos,
                    isPendingIPhone = item.IsManualSelectedForIPhone || item.IsManualSelectedForAndroid || item.IsManualSelectedForGooglePhotos,
                    isPendingAndroid = item.IsManualSelectedForIPhone || item.IsManualSelectedForAndroid || item.IsManualSelectedForGooglePhotos,
                    isGooglePhotos = item.IsManualSelectedForGooglePhotos,
                    sizeText = item.SizeText,
                    cameraModel = item.CameraModel
                });
                entries.Add(new PreviewEntry
                {
                    ItemId = item.RelativePath ?? "",
                    FullPath = item.FullPath ?? "",
                    IsVideo = item.IsVideo,
                    LiveVideoPath = fallbackLiveVideo
                });
            }
        }

        ReplacePreviewEntries(entries);

        var payload = new
        {
            action = "setPhotos",
            photos = photosList,
            sortMode = currentSearchVM.SortModeIndex,
            sortDescending = currentSearchVM.SortDescending,
            locale = GalleryLocaleCode
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

    private void OnAppLanguageChanged(string _)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnAppLanguageChanged(_));
            return;
        }

        ApplyMuteChrome();
        PostGalleryLocale();
    }

    private static string GalleryLocaleCode => LocalizationService.CurrentLanguageCode switch
    {
        "en-US" => "en",
        "zh-HK" => "zh-HK",
        _ => "zh-CN"
    };

    private void PostGalleryLocale()
    {
        if (!isWebReady || AlbumWebView.CoreWebView2 == null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            action = "setLocale",
            locale = GalleryLocaleCode
        });
        AlbumWebView.CoreWebView2.PostWebMessageAsJson(json);
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

    /// <summary>
    /// WebView2 owns an HWND, so the native player can only be seen while that control is collapsed.
    /// </summary>
    private void ApplyWebViewVisibility()
    {
        if (nativePlayerOpen)
        {
            AlbumWebView.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (!isWebReady)
        {
            AlbumWebView.Visibility = Visibility.Hidden;
            if (ErrorOverlay.Visibility != Visibility.Visible)
            {
                LoadingOverlay.Visibility = Visibility.Visible;
            }

            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;

        bool modal = PclModalHost.HasAnyModalOpen
            || DataContext is MainViewModel main && main.IsAnyModalOpen;
        AlbumWebView.Visibility = modal ? Visibility.Hidden : Visibility.Visible;
    }

    private void OpenNativePreview(int index, string? path)
    {
        if (previewEntries.Count == 0)
        {
            return;
        }

        bool indexMatches = index >= 0
            && index < previewEntries.Count
            && (string.IsNullOrWhiteSpace(path)
                || string.Equals(previewEntries[index].FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (!indexMatches)
        {
            index = string.IsNullOrWhiteSpace(path)
                ? -1
                : previewEntries.FindIndex(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0)
        {
            return;
        }

        ShowPreviewEntry(index);
    }

    private void ShowPreviewEntry(int index)
    {
        if ((uint)index >= (uint)previewEntries.Count)
        {
            return;
        }

        PreviewEntry entry = previewEntries[index];
        previewIndex = index;
        currentItemPath = entry.FullPath;
        ResetView();

        bool video = entry.IsVideo && File.Exists(entry.FullPath);
        bool live = !entry.IsVideo
            && !string.IsNullOrWhiteSpace(entry.LiveVideoPath)
            && File.Exists(entry.LiveVideoPath);
        if (video)
        {
            ShowPreviewSurface(PreviewKind.Video, entry.FullPath, entry.FullPath);
        }
        else if (live)
        {
            ShowPreviewSurface(PreviewKind.Live, entry.FullPath, entry.LiveVideoPath!);
        }
        else
        {
            ShowPreviewSurface(PreviewKind.Still, entry.FullPath, "");
        }

        if (index >= previewEntries.Count - 8
            && currentSearchVM?.LoadMoreItemsCommand.CanExecute(null) == true)
        {
            currentSearchVM.LoadMoreItemsCommand.Execute(null);
        }
    }

    private void ShowAdjacent(int delta)
    {
        ShowPreviewEntry(previewIndex + delta);
    }

    private void ShowPreviewSurface(PreviewKind kind, string itemPath, string videoPath)
    {
        bool motion = kind != PreviewKind.Still;
        StopPlayerMedia();
        nativePlayerOpen = true;
        previewKind = kind;
        currentVideoPath = motion ? videoPath : null;
        int loadGeneration = ++stillLoadGeneration;
        currentStillPath = kind == PreviewKind.Video ? null : itemPath;
        pendingPlay = motion;
        playing = false;
        PlayerStatus.Visibility = Visibility.Collapsed;
        fullStillReady = false;
        ArmPreviewSpinner();

        PlayerStill.Source = kind == PreviewKind.Video ? null : TryCachedThumbnail(itemPath);
        PlayerStill.Visibility = kind == PreviewKind.Video ? Visibility.Collapsed : Visibility.Visible;
        PlayerFrames.Source = null;
        PlayerFrames.Visibility = Visibility.Collapsed;
        PlayerReplay.Visibility = Visibility.Collapsed;
        bool videoBar = kind == PreviewKind.Video;
        PlayerTransport.Visibility = videoBar ? Visibility.Visible : Visibility.Collapsed;
        PlayerTools.Visibility = videoBar ? Visibility.Collapsed : Visibility.Visible;
        PlayerPlayGlyph.Text = "\uE769";
        if (kind == PreviewKind.Video)
        {
            clockWriting = true;
            PlayerScrub.Minimum = 0;
            PlayerScrub.Maximum = 1;
            PlayerScrub.Value = 0;
            clockWriting = false;
            PlayerTime.Text = "0:00 / 0:00";
        }

        ApplyMediaLayout();
        ApplyMuteChrome();
        UpdatePreviewChrome();
        NativePlayer.Visibility = Visibility.Visible;
        ApplyWebViewVisibility();
        HookWindowKeys();
        if (kind != PreviewKind.Video)
        {
            BeginStillLoad(itemPath, loadGeneration);
        }

        if (motion)
        {
            framePlayer = new SystemVideoFramePlayer(Dispatcher, previewMuted);
            framePlayer.Opened += FramePlayer_Opened;
            framePlayer.FrameUpdated += FramePlayer_FrameUpdated;
            framePlayer.Ended += FramePlayer_Ended;
            framePlayer.Failed += FramePlayer_Failed;
            framePlayer.Open(videoPath);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusNativePlayer));
    }

    private void BeginStillLoad(string still, int generation)
    {
        _ = Task.Run(() =>
        {
            BitmapSource? full = LoadPlayerStill(still);
            if (full != null && !full.IsFrozen)
            {
                full.Freeze();
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (generation != stillLoadGeneration || !nativePlayerOpen)
                {
                    return;
                }

                if (!string.Equals(currentStillPath, still, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (full == null)
                {
                    HidePreviewSpinner();
                    if (PlayerStill.Source == null)
                    {
                        PlayerStatus.Visibility = Visibility.Visible;
                    }

                    return;
                }

                fullStillReady = true;
                HidePreviewSpinner();
                PlayerStatus.Visibility = Visibility.Collapsed;
                PlayerStill.Source = full;
                PlayerStill.Visibility = Visibility.Visible;
                if (PlayerFrames.Visibility != Visibility.Visible)
                {
                    ApplyMediaLayout();
                }
            });
        });
    }

    private void CloseNativePlayer()
    {
        if (!nativePlayerOpen && NativePlayer.Visibility != Visibility.Visible)
        {
            return;
        }

        stillLoadGeneration++;
        nativePlayerOpen = false;
        previewKind = PreviewKind.None;
        previewIndex = -1;
        currentVideoPath = null;
        currentStillPath = null;
        currentItemPath = null;
        ResetView();
        StopPlayerMedia();
        PlayerStill.Source = null;
        PlayerFrames.Source = null;
        PlayerFrames.Visibility = Visibility.Collapsed;
        PlayerReplay.Visibility = Visibility.Collapsed;
        PlayerTransport.Visibility = Visibility.Collapsed;
        PlayerTools.Visibility = Visibility.Visible;
        PlayerStatus.Visibility = Visibility.Collapsed;
        HidePreviewSpinner();
        NativePlayer.Visibility = Visibility.Collapsed;
        UnhookWindowKeys();
        ApplyWebViewVisibility();
    }

    private void StopPlayerMedia()
    {
        nativeClosing = true;
        pendingPlay = false;
        playing = false;
        playerClock.Stop();
        SystemVideoFramePlayer? current = framePlayer;
        framePlayer = null;
        current?.Dispose();
        nativeClosing = false;
    }

    private void ArmPreviewSpinner()
    {
        PlayerLoading.Visibility = Visibility.Collapsed;
        previewSpinnerTimer.Stop();
        previewSpinnerTimer.Start();
    }

    private void HidePreviewSpinner()
    {
        previewSpinnerTimer.Stop();
        PlayerLoading.Visibility = Visibility.Collapsed;
    }

    private void PreviewSpinnerTimer_Tick(object? sender, EventArgs e)
    {
        previewSpinnerTimer.Stop();
        if (!nativePlayerOpen || IsPreviewVisualReady())
        {
            return;
        }

        PlayerLoading.Visibility = Visibility.Visible;
    }

    private bool IsPreviewVisualReady()
    {
        if (PlayerFrames.Visibility == Visibility.Visible && PlayerFrames.Source != null)
        {
            return true;
        }

        return fullStillReady;
    }

    private void FocusNativePlayer()
    {
        if (!nativePlayerOpen)
        {
            return;
        }

        NativePlayer.Focus();
        Keyboard.Focus(NativePlayer);
    }

    private static BitmapSource? LoadPlayerStill(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif")
        {
            return ShellServices.DecodeHeifStill(path, 1920);
        }

        return LoadFrozenBitmap(path, 0);
    }

    private void TogglePlayback()
    {
        if (!nativePlayerOpen || previewKind != PreviewKind.Video)
        {
            return;
        }

        if (framePlayer == null)
        {
            return;
        }

        if (playing)
        {
            framePlayer.Pause();
            playing = false;
            PlayerPlayGlyph.Text = "\uE768";
            return;
        }

        if (framePlayer.Duration > TimeSpan.Zero
            && framePlayer.Duration - framePlayer.Position <= TimeSpan.FromMilliseconds(80))
        {
            framePlayer.Position = TimeSpan.Zero;
        }

        framePlayer.Play();
        playing = true;
        PlayerPlayGlyph.Text = "\uE769";
    }

    private void ReplayLive()
    {
        if (!nativePlayerOpen || previewKind != PreviewKind.Live)
        {
            return;
        }

        PlayerReplay.Visibility = Visibility.Collapsed;
        PlayerFrames.Visibility = Visibility.Visible;
        if (framePlayer == null)
        {
            return;
        }

        framePlayer.Position = TimeSpan.Zero;
        framePlayer.Play();
        playing = true;
    }

    private void SeekTo(TimeSpan position)
    {
        if (!nativePlayerOpen || previewKind != PreviewKind.Video || nativeClosing)
        {
            return;
        }

        if (framePlayer == null)
        {
            return;
        }

        if (framePlayer.Duration > TimeSpan.Zero)
        {
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }
            else if (position > framePlayer.Duration)
            {
                position = framePlayer.Duration;
            }
        }

        framePlayer.Position = position;
    }

    private void HookWindowKeys()
    {
        Window? window = Window.GetWindow(this);
        if (window == null || ReferenceEquals(playerKeyWindow, window))
        {
            return;
        }

        UnhookWindowKeys();
        playerKeyWindow = window;
        playerKeyWindow.PreviewKeyDown += PlayerHost_PreviewKeyDown;
    }

    private void UnhookWindowKeys()
    {
        if (playerKeyWindow == null)
        {
            return;
        }

        playerKeyWindow.PreviewKeyDown -= PlayerHost_PreviewKeyDown;
        playerKeyWindow = null;
    }

    private void PlayerHost_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!nativePlayerOpen)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseNativePlayer();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox || e.OriginalSource is Slider)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            ShowAdjacent(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            ShowAdjacent(1);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.OemPlus or Key.Add)
        {
            ZoomAt(StageCenter(), ZoomStep);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.OemMinus or Key.Subtract)
        {
            ZoomAt(StageCenter(), 1 / ZoomStep);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.D0 or Key.NumPad0)
        {
            FitView();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.D1 or Key.NumPad1)
        {
            ActualSize();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Space)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source || !IsInsidePlayer(source))
        {
            return;
        }

        if (previewKind == PreviewKind.Live)
        {
            ReplayLive();
        }
        else if (previewKind == PreviewKind.Video)
        {
            TogglePlayback();
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private bool IsInsidePlayer(DependencyObject source)
    {
        return ReferenceEquals(source, NativePlayer) || NativePlayer.IsAncestorOf(source);
    }

    private void PlayerClock_Tick(object? sender, EventArgs e)
    {
        if (!nativePlayerOpen || previewKind != PreviewKind.Video || clockWriting || nativeClosing)
        {
            return;
        }

        if (framePlayer == null || framePlayer.Duration <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan duration = framePlayer.Duration;
        TimeSpan position = framePlayer.Position;

        clockWriting = true;
        PlayerScrub.Maximum = Math.Max(duration.TotalSeconds, 0.1);
        PlayerScrub.Value = Math.Clamp(position.TotalSeconds, PlayerScrub.Minimum, PlayerScrub.Maximum);
        clockWriting = false;
        PlayerTime.Text = $"{FormatClock(position)} / {FormatClock(duration)}";
    }

    private void FramePlayer_Opened(TimeSpan duration)
    {
        if (!nativePlayerOpen || framePlayer == null)
        {
            return;
        }

        if (previewKind == PreviewKind.Video && duration > TimeSpan.Zero)
        {
            clockWriting = true;
            PlayerScrub.Maximum = Math.Max(duration.TotalSeconds, 0.1);
            clockWriting = false;
            playerClock.Start();
        }

        if (!pendingPlay)
        {
            return;
        }

        pendingPlay = false;
        framePlayer.Play();
        playing = true;
        if (previewKind == PreviewKind.Video)
        {
            PlayerPlayGlyph.Text = "\uE769";
        }
    }

    private void FramePlayer_FrameUpdated()
    {
        if (!nativePlayerOpen || framePlayer?.Frame == null)
        {
            return;
        }

        bool firstFrame = PlayerFrames.Visibility != Visibility.Visible
            || !ReferenceEquals(PlayerFrames.Source, framePlayer.Frame);
        if (!ReferenceEquals(PlayerFrames.Source, framePlayer.Frame))
        {
            PlayerFrames.Source = framePlayer.Frame;
        }

        PlayerFrames.Visibility = Visibility.Visible;
        if (firstFrame)
        {
            HidePreviewSpinner();
            ApplyMediaLayout();
        }
    }

    private void FramePlayer_Ended()
    {
        if (!nativePlayerOpen || nativeClosing)
        {
            return;
        }

        playing = false;
        framePlayer?.Pause();
        if (previewKind == PreviewKind.Live)
        {
            PlayerFrames.Visibility = Visibility.Collapsed;
            PlayerReplay.Visibility = Visibility.Visible;
            ApplyMediaLayout();
            return;
        }

        if (framePlayer != null && framePlayer.Duration > TimeSpan.Zero)
        {
            clockWriting = true;
            PlayerScrub.Value = PlayerScrub.Maximum;
            clockWriting = false;
            TimeSpan duration = framePlayer.Duration;
            PlayerTime.Text = $"{FormatClock(duration)} / {FormatClock(duration)}";
        }

        PlayerPlayGlyph.Text = "\uE768";
    }

    private void FramePlayer_Failed()
    {
        if (nativeClosing || !nativePlayerOpen)
        {
            return;
        }

        string? path = currentVideoPath;
        CloseNativePlayer();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            ShellServices.OpenWith(path, GetMainWindowHandle());
        }
    }

    private static BitmapSource? TryCachedThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !ThumbnailCache.TryGetValue(path, out byte[]? jpeg) || jpeg.Length < MinimumThumbnailBytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void NativePlayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!nativePlayerOpen || e.Handled || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (IsOverElement(PlayerPrev, e) || IsOverElement(PlayerNext, e) || IsOverElement(PlayerClose, e))
        {
            return;
        }

        Point stagePoint = e.GetPosition(PlayerStage);
        bool insideStage = stagePoint.X >= 0 && stagePoint.Y >= 0
            && stagePoint.X <= PlayerStage.ActualWidth
            && stagePoint.Y <= PlayerStage.ActualHeight;
        if (insideStage && IsOverMedia(stagePoint))
        {
            return;
        }

        CloseNativePlayer();
        e.Handled = true;
    }

    /// <summary>
    /// True when the wheel should zoom the open preview instead of scrolling the page.
    /// The page scroller sees the wheel first.
    /// </summary>
    internal bool PreviewConsumesWheel(DependencyObject source)
    {
        if (!nativePlayerOpen || NativePlayer.Visibility != Visibility.Visible)
        {
            return false;
        }

        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, NativePlayer))
            {
                return true;
            }
        }

        return false;
    }

    private void NativePlayer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!nativePlayerOpen)
        {
            return;
        }

        ZoomAt(e.GetPosition(PlayerStage), e.Delta > 0 ? ZoomStep : 1 / ZoomStep);
        e.Handled = true;
    }

    private void NativePlayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!nativePlayerOpen)
        {
            return;
        }

        ApplyMediaLayout();
    }

    private void PlayerStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!nativePlayerOpen || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Point point = e.GetPosition(PlayerStage);
        if (!IsOverMedia(point))
        {
            return;
        }

        if (e.ClickCount >= 2 && previewKind == PreviewKind.Still)
        {
            if (Math.Abs(viewScale - 1) < 0.02 && Math.Abs(panX) < 0.5 && Math.Abs(panY) < 0.5)
            {
                ActualSize();
            }
            else
            {
                FitView();
            }

            e.Handled = true;
            return;
        }

        panStart = point;
        panStartX = panX;
        panStartY = panY;
        panMoved = false;
        panning = true;
        PlayerStage.CaptureMouse();
        e.Handled = true;
    }

    private void PlayerStage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!panning)
        {
            return;
        }

        Point point = e.GetPosition(PlayerStage);
        double dx = point.X - panStart.X;
        double dy = point.Y - panStart.Y;
        if (!panMoved && dx * dx + dy * dy < 16)
        {
            return;
        }

        panMoved = true;
        panX = panStartX + dx;
        panY = panStartY + dy;
        ApplyMediaTransform();
    }

    private void PlayerStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!panning)
        {
            return;
        }

        bool moved = panMoved;
        panning = false;
        if (PlayerStage.IsMouseCaptured)
        {
            PlayerStage.ReleaseMouseCapture();
        }

        if (!moved)
        {
            if (previewKind == PreviewKind.Live && PlayerFrames.Visibility != Visibility.Visible)
            {
                ReplayLive();
            }
            else if (previewKind == PreviewKind.Video)
            {
                TogglePlayback();
            }
        }

        e.Handled = true;
    }

    private void PlayerStage_LostMouseCapture(object sender, MouseEventArgs e)
    {
        panning = false;
    }

    private void PlayerChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void PlayerClose_Click(object sender, RoutedEventArgs e)
    {
        CloseNativePlayer();
        e.Handled = true;
    }

    private void PlayerReplay_Click(object sender, RoutedEventArgs e)
    {
        ReplayLive();
        e.Handled = true;
    }

    private void PlayerPrev_Click(object sender, RoutedEventArgs e)
    {
        ShowAdjacent(1);
        e.Handled = true;
    }

    private void PlayerNext_Click(object sender, RoutedEventArgs e)
    {
        ShowAdjacent(-1);
        e.Handled = true;
    }

    private void PlayerRotateLeft_Click(object sender, RoutedEventArgs e)
    {
        RotateBy(-90);
        e.Handled = true;
    }

    private void PlayerRotateRight_Click(object sender, RoutedEventArgs e)
    {
        RotateBy(90);
        e.Handled = true;
    }

    private void PlayerZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomAt(StageCenter(), 1 / ZoomStep);
        e.Handled = true;
    }

    private void PlayerZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ZoomAt(StageCenter(), ZoomStep);
        e.Handled = true;
    }

    private void PlayerActualSize_Click(object sender, RoutedEventArgs e)
    {
        ActualSize();
        e.Handled = true;
    }

    private void PlayerFit_Click(object sender, RoutedEventArgs e)
    {
        FitView();
        e.Handled = true;
    }

    private void PlayerOpenWith_Click(object sender, RoutedEventArgs e)
    {
        OpenCurrentWithSystem();
        e.Handled = true;
    }

    private void PlayerReveal_Click(object sender, RoutedEventArgs e)
    {
        RevealCurrentInExplorer();
        e.Handled = true;
    }

    private void NativePlayer_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!nativePlayerOpen || Resources["PlayerContextMenu"] is not ContextMenu menu)
        {
            return;
        }

        bool hasFile = !string.IsNullOrWhiteSpace(currentItemPath) && File.Exists(currentItemPath);
        bool hasId = CurrentPreviewEntry() is { ItemId.Length: > 0 } && currentSearchVM != null;
        var commands = menu.Items.OfType<MenuItem>().ToArray();
        if (commands.Length >= 6)
        {
            commands[0].IsEnabled = hasId;
            commands[1].IsEnabled = hasId;
            commands[2].IsEnabled = hasFile;
            commands[3].IsEnabled = hasFile;
            commands[4].IsEnabled = hasFile;
            commands[5].IsEnabled = hasFile;
        }

        menu.PlacementTarget = NativePlayer;
        Dispatcher.BeginInvoke(() =>
        {
            if (nativePlayerOpen)
            {
                menu.IsOpen = true;
            }
        });
        e.Handled = true;
    }

    private void PlayerMenuAddPending_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPreviewEntry() is { ItemId.Length: > 0 } entry && currentSearchVM != null)
        {
            currentSearchVM.AddItemsToManualSelection("Unified", new[] { entry.ItemId });
        }

        e.Handled = true;
    }

    private void PlayerMenuRemovePending_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPreviewEntry() is { ItemId.Length: > 0 } entry && currentSearchVM != null)
        {
            currentSearchVM.RemoveItemsFromManualSelection(new[] { entry.ItemId });
        }

        e.Handled = true;
    }

    private void PlayerMenuReveal_Click(object sender, RoutedEventArgs e)
    {
        RevealCurrentInExplorer();
        e.Handled = true;
    }

    private void PlayerMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenCurrentWithSystem();
        e.Handled = true;
    }

    private void PlayerMenuCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(currentItemPath) && File.Exists(currentItemPath))
        {
            ShellServices.CopyFilesToClipboard(new[] { currentItemPath });
        }

        e.Handled = true;
    }

    private void PlayerMenuProperties_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(currentItemPath) && File.Exists(currentItemPath))
        {
            ShellServices.ShowProperties(new[] { currentItemPath }, GetMainWindowHandle());
        }

        e.Handled = true;
    }

    private PreviewEntry? CurrentPreviewEntry()
    {
        if (previewIndex < 0 || previewIndex >= previewEntries.Count)
        {
            return null;
        }

        return previewEntries[previewIndex];
    }

    private void OpenCurrentWithSystem()
    {
        if (!string.IsNullOrWhiteSpace(currentItemPath) && File.Exists(currentItemPath))
        {
            ShellServices.OpenWith(currentItemPath, GetMainWindowHandle());
        }
    }

    private void RevealCurrentInExplorer()
    {
        if (!string.IsNullOrWhiteSpace(currentItemPath) && File.Exists(currentItemPath))
        {
            ShellServices.ShowInExplorer(new[] { currentItemPath });
        }
    }

    private void PlayerPlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
        e.Handled = true;
    }

    private void PlayerMute_Click(object sender, RoutedEventArgs e)
    {
        previewMuted = !previewMuted;
        framePlayer?.SetMuted(previewMuted);
        ApplyMuteChrome();
        AppSettings.SavePreviewMuted(previewMuted);
        e.Handled = true;
    }

    private void ApplyMuteChrome()
    {
        string glyph = previewMuted ? "\uE74F" : "\uE767";
        string tip = previewMuted
            ? App.GetString("PlayerUnmute", "取消静音")
            : App.GetString("PlayerMute", "静音");
        PlayerToolsMuteGlyph.Text = glyph;
        PlayerTransportMuteGlyph.Text = glyph;
        PlayerToolsMute.ToolTip = tip;
        PlayerTransportMute.ToolTip = tip;
        PlayerToolsMute.Visibility = previewKind == PreviewKind.Live
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PlayerScrub_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (clockWriting || !nativePlayerOpen || previewKind != PreviewKind.Video)
        {
            return;
        }

        SeekTo(TimeSpan.FromSeconds(e.NewValue));
    }

    private void ReplacePreviewEntries(List<PreviewEntry> entries)
    {
        previewEntries.Clear();
        previewEntries.AddRange(entries);
        if (!nativePlayerOpen)
        {
            return;
        }

        int found = string.IsNullOrWhiteSpace(currentItemPath)
            ? -1
            : previewEntries.FindIndex(entry => string.Equals(entry.FullPath, currentItemPath, StringComparison.OrdinalIgnoreCase));
        if (found < 0)
        {
            CloseNativePlayer();
            return;
        }

        previewIndex = found;
        UpdatePreviewChrome();
    }

    private void UpdatePreviewChrome()
    {
        int count = previewEntries.Count;
        PlayerCounter.Text = previewIndex >= 0 && count > 0 ? $"{previewIndex + 1} / {count}" : "";
        PlayerNext.IsEnabled = previewIndex > 0;
        PlayerPrev.IsEnabled = previewIndex >= 0 && previewIndex < count - 1;
    }

    private void ResetView()
    {
        viewScale = 1;
        panX = 0;
        panY = 0;
        rotationDegrees = 0;
        panning = false;
        panMoved = false;
        if (PlayerStage.IsMouseCaptured)
        {
            PlayerStage.ReleaseMouseCapture();
        }
    }

    private void ApplyMediaLayout()
    {
        BitmapSource? shown = null;
        if (PlayerFrames.Visibility == Visibility.Visible
            && PlayerFrames.Source is BitmapSource frames
            && frames.PixelWidth > 0
            && frames.PixelHeight > 0)
        {
            shown = frames;
        }
        else if (PlayerStill.Visibility == Visibility.Visible
            && PlayerStill.Source is BitmapSource still
            && still.PixelWidth > 0
            && still.PixelHeight > 0)
        {
            shown = still;
        }

        if (shown == null)
        {
            mediaPixelWidth = 0;
            mediaPixelHeight = 0;
            PlayerMedia.ClearValue(WidthProperty);
            PlayerMedia.ClearValue(HeightProperty);
        }
        else
        {
            mediaPixelWidth = shown.PixelWidth;
            mediaPixelHeight = shown.PixelHeight;
        }

        ApplyMediaTransform();
    }

    private void ApplyMediaTransform()
    {
        MediaScale.ScaleX = viewScale;
        MediaScale.ScaleY = viewScale;
        MediaRotate.Angle = rotationDegrees;
        MediaPan.X = panX;
        MediaPan.Y = panY;
        if (!TryFittedBox(out double boxWidth, out double boxHeight, out double fit))
        {
            return;
        }

        if (double.IsNaN(PlayerMedia.Width) || Math.Abs(PlayerMedia.Width - boxWidth) > 0.5)
        {
            PlayerMedia.Width = boxWidth;
        }

        if (double.IsNaN(PlayerMedia.Height) || Math.Abs(PlayerMedia.Height - boxHeight) > 0.5)
        {
            PlayerMedia.Height = boxHeight;
        }

        PlayerZoomText.Text = $"{Math.Round(viewScale * fit * 100)}%";
    }

    // The box is already contained in the stage, including after a quarter turn.
    // Scale stays at 1 for that fit, so ClipToBounds does not cut the unscaled picture.
    private bool TryFittedBox(out double boxWidth, out double boxHeight, out double fit)
    {
        boxWidth = 0;
        boxHeight = 0;
        fit = 1;
        if (mediaPixelWidth <= 1 || mediaPixelHeight <= 1)
        {
            return false;
        }

        double stageWidth = PlayerStage.ActualWidth;
        double stageHeight = PlayerStage.ActualHeight;
        if (stageWidth <= 1 || stageHeight <= 1)
        {
            return false;
        }

        bool quarterTurn = Math.Abs(rotationDegrees % 180) == 90;
        fit = quarterTurn
            ? Math.Min(stageWidth / mediaPixelHeight, stageHeight / mediaPixelWidth)
            : Math.Min(stageWidth / mediaPixelWidth, stageHeight / mediaPixelHeight);
        if (fit <= 0 || double.IsInfinity(fit))
        {
            return false;
        }

        boxWidth = mediaPixelWidth * fit;
        boxHeight = mediaPixelHeight * fit;
        return true;
    }

    private void ZoomAt(Point stagePoint, double factor)
    {
        if (!nativePlayerOpen || factor <= 0)
        {
            return;
        }

        double next = Math.Clamp(viewScale * factor, MinViewScale, MaxViewScale);
        if (Math.Abs(next - viewScale) < 0.0001)
        {
            return;
        }

        double ratio = next / viewScale;
        double centerX = PlayerStage.ActualWidth / 2;
        double centerY = PlayerStage.ActualHeight / 2;
        double offsetX = stagePoint.X - centerX - panX;
        double offsetY = stagePoint.Y - centerY - panY;
        panX = stagePoint.X - centerX - offsetX * ratio;
        panY = stagePoint.Y - centerY - offsetY * ratio;
        viewScale = next;
        ApplyMediaTransform();
    }

    private void FitView()
    {
        viewScale = 1;
        panX = 0;
        panY = 0;
        ApplyMediaTransform();
    }

    private void ActualSize()
    {
        if (!TryFittedBox(out _, out _, out double fit) || fit <= 0.0001)
        {
            return;
        }

        viewScale = Math.Clamp(1 / fit, MinViewScale, MaxViewScale);
        panX = 0;
        panY = 0;
        ApplyMediaTransform();
    }

    private void RotateBy(double delta)
    {
        rotationDegrees = (rotationDegrees + delta) % 360;
        if (rotationDegrees < 0)
        {
            rotationDegrees += 360;
        }

        panX = 0;
        panY = 0;
        ApplyMediaTransform();
    }

    private Point StageCenter() => new(PlayerStage.ActualWidth / 2, PlayerStage.ActualHeight / 2);

    private bool IsOverMedia(Point stagePoint)
    {
        if (mediaPixelWidth <= 1 || mediaPixelHeight <= 1 || PlayerMedia.ActualWidth <= 0)
        {
            return false;
        }

        try
        {
            GeneralTransform transform = PlayerStage.TransformToVisual(PlayerMedia);
            Point local = transform.Transform(stagePoint);
            return local.X >= 0 && local.Y >= 0
                && local.X <= PlayerMedia.ActualWidth
                && local.Y <= PlayerMedia.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsOverElement(FrameworkElement element, MouseEventArgs e)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        Point point = e.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static int ReadJsonInt(JsonObject obj, string name)
    {
        if (obj[name] is JsonValue value && value.TryGetValue(out int parsed))
        {
            return parsed;
        }

        return -1;
    }

    private static string FormatClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
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

    private enum PreviewKind
    {
        None,
        Still,
        Live,
        Video
    }

    private sealed class PreviewEntry
    {
        public required string ItemId { get; init; }
        public required string FullPath { get; init; }
        public required bool IsVideo { get; init; }
        public string? LiveVideoPath { get; init; }
    }

    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;
        private long _position;

        public BoundedStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, _remaining);
            int read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            _position += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
