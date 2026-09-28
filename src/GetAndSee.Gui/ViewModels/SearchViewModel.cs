using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Search;
using GetAndSee.Core.Util;

using GetAndSee.Gui.Util;

namespace GetAndSee.Gui.ViewModels;

public partial class MediaSearchResultItem : ObservableObject
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string CapturedAt { get; set; } = string.Empty;
    public string SizeText { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string MediaTypeIcon { get; set; } = "🖼️";
    public string CameraModel { get; set; } = string.Empty;
    public string GpsCoordinates { get; set; } = string.Empty;
    public bool HasThumbnail { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailLoaded))]
    private ImageSource? thumbnailImage;

    public bool IsThumbnailLoaded => ThumbnailImage is not null;

    [ObservableProperty]
    private bool isManualSelectedForIPhone = false;

    [ObservableProperty]
    private bool isManualSelectedForAndroid = false;
}


public partial class GalleryRow : ObservableObject
{
    public ObservableCollection<MediaSearchResultItem> Items { get; set; } = new();
}

public partial class SearchViewModel : ObservableObject
{
    private CancellationTokenSource? thumbnailCts;

    public IPhoneSyncViewModel? IPhoneSyncVM { get; set; }
    public AndroidSyncViewModel? AndroidSyncVM { get; set; }

    [ObservableProperty]
    private string archivePath = MainViewModel.DefaultArchivePath;


    [ObservableProperty]
    private DateTime? filterFromDate;

    [ObservableProperty]
    private DateTime? filterToDate;

    [ObservableProperty]
    private string filterFrom = "";

    [ObservableProperty]
    private string filterTo = "";

    partial void OnFilterFromDateChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (filterFrom != formatted)
        {
            FilterFrom = formatted;
        }
    }

    partial void OnFilterToDateChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (filterTo != formatted)
        {
            FilterTo = formatted;
        }
    }

    partial void OnFilterFromChanged(string value)
    {
        if (DateTime.TryParse(value, out var dt))
        {
            if (filterFromDate != dt.Date) filterFromDate = dt.Date;
        }
        else if (string.IsNullOrWhiteSpace(value) && filterFromDate != null)
        {
            filterFromDate = null;
        }
    }

    partial void OnFilterToChanged(string value)
    {
        if (DateTime.TryParse(value, out var dt))
        {
            if (filterToDate != dt.Date) filterToDate = dt.Date;
        }
        else if (string.IsNullOrWhiteSpace(value) && filterToDate != null)
        {
            filterToDate = null;
        }
    }

    [RelayCommand]
    private void SetDatePresetLast30Days()
    {
        FilterToDate = DateTime.Today;
        FilterFromDate = DateTime.Today.AddDays(-30);
    }

    [RelayCommand]
    private void SetDatePresetLast90Days()
    {
        FilterToDate = DateTime.Today;
        FilterFromDate = DateTime.Today.AddDays(-90);
    }

    [RelayCommand]
    private void SetDatePresetLast1Year()
    {
        FilterToDate = DateTime.Today;
        FilterFromDate = DateTime.Today.AddYears(-1);
    }

    [RelayCommand]
    private void SetDatePresetThisYear()
    {
        FilterToDate = DateTime.Today;
        FilterFromDate = new DateTime(DateTime.Today.Year, 1, 1);
    }

    [RelayCommand]
    private void ClearDateRange()
    {
        FilterFromDate = null;
        FilterToDate = null;
        FilterFrom = "";
        FilterTo = "";
    }

    [ObservableProperty]
    private string selectedType = "All (全部)";

    [ObservableProperty]
    private string cameraFilter = "";

    [ObservableProperty]
    private string fileNameFilter = "";

    [ObservableProperty]
    private bool hasGpsOnly = false;

    [ObservableProperty]
    private bool includeHeic = false;

    [ObservableProperty]
    private bool isTableView = true;

    [ObservableProperty]
    private bool isGalleryView = false;

    [ObservableProperty]
    private string searchSummaryText = "";

    [ObservableProperty]
    private bool isSearching = false;

    [ObservableProperty]
    private bool isPreviewOpen = false;

    [ObservableProperty]
    private MediaSearchResultItem? previewItem;

    [ObservableProperty]
    private int previewIndex = -1;

    [ObservableProperty]
    private ImageSource? previewImageSource;

    [ObservableProperty]
    private string previewDetailsText = "";

    [ObservableProperty]
    private bool hasPrevItem = false;

    [ObservableProperty]
    private bool hasNextItem = false;

    public ObservableCollection<string> MediaTypes { get; } = new()
    {
        "All (全部)",
        "photo (照片)",
        "video (视频)",
        "screenshot (截图)",
        "other (其他)"
    };

    [ObservableProperty]
    private ObservableCollection<MediaSearchResultItem> searchResults = new();

    [ObservableProperty]
    private ObservableCollection<GalleryRow> galleryRows = new();

    [ObservableProperty]
    private bool hasMoreItems = false;

    private const int BatchSize = 1000;
    private List<MediaSearchHit> currentHits = new();
    private int loadedHitIndex = 0;
    private bool isLoadingMore = false;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            SearchSummaryText = App.GetString("MsgTargetDirNotExist", "目标归档路径不存在");
            SearchResults = new();
            GalleryRows = new();
            HasMoreItems = false;
            return;
        }

        thumbnailCts?.Cancel();
        thumbnailCts = new CancellationTokenSource();
        CancellationToken ct = thumbnailCts.Token;

        DateTimeOffset? fromDate = DateTimeOffset.TryParse(FilterFrom, CultureInfo.InvariantCulture, out var f) ? f : null;
        DateTimeOffset? toDate = DateTimeOffset.TryParse(FilterTo, CultureInfo.InvariantCulture, out var t) ? t : null;
        MediaType? mediaType = ParseMediaType(SelectedType);

        var criteria = new MediaSearchCriteria(
            From: fromDate,
            To: toDate,
            Type: mediaType,
            Camera: string.IsNullOrWhiteSpace(CameraFilter) ? null : CameraFilter,
            HasGps: HasGpsOnly,
            IncludeHeic: IncludeHeic,
            FileNameKeyword: string.IsNullOrWhiteSpace(FileNameFilter) ? null : FileNameFilter);

        IsSearching = true;
        SearchSummaryText = "正在检索 SQLite 数据库...";

        try
        {
            var hits = await Task.Run(() =>
            {
                using var journal = TransferJournal.OpenReadOnly(ArchivePath);
                IReadOnlyList<ManifestSearchRow> rows = journal.ReadSearchRows();
                return MediaSearch.Find(rows, criteria);
            }, ct).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                currentHits = hits.ToList();
                loadedHitIndex = 0;
                SearchResults = new ObservableCollection<MediaSearchResultItem>();
                GalleryRows = new ObservableCollection<GalleryRow>();
                HasMoreItems = currentHits.Count > 0;
            });

            if (currentHits.Count == 0)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    SearchSummaryText = "检索成功：共匹配 0 项";
                    HasMoreItems = false;
                });
            }
            else
            {
                await LoadMoreItemsAsync();
            }
        }
        catch (FileNotFoundException)
        {
            SearchSummaryText = "尚未发现 SQLite 归档数据库 (get-and-see.db)。";
            SearchResults = new();
            GalleryRows = new();
            HasMoreItems = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Search error: {ex.Message}");
            SearchSummaryText = $"检索错误: {ex.Message}";
            HasMoreItems = false;
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public async Task LoadMoreItemsAsync()
    {
        if (isLoadingMore || currentHits.Count == 0 || loadedHitIndex >= currentHits.Count)
        {
            HasMoreItems = loadedHitIndex < currentHits.Count;
            return;
        }

        isLoadingMore = true;
        CancellationToken ct = thumbnailCts?.Token ?? CancellationToken.None;

        try
        {
            int countToTake = Math.Min(currentHits.Count - loadedHitIndex, BatchSize);
            int startIndex = loadedHitIndex;
            string archivePathCopy = ArchivePath;

            var manualSet = IPhoneSyncVM?.GetManualSelectionPathsSet() ?? new HashSet<string>();
            var androidManualSet = AndroidSyncVM?.GetManualSelectionPathsSet() ?? new HashSet<string>();

            var (newItems, newGridRows) = await Task.Run(() =>
            {
                var list = new List<MediaSearchResultItem>(countToTake);
                for (int i = 0; i < countToTake; i++)
                {
                    MediaSearchHit hit = currentHits[startIndex + i];
                    string fullPath = Path.Combine(archivePathCopy, hit.RelativePath);
                    bool isImage = IsImageFileExtension(hit.RelativePath);

                    list.Add(new MediaSearchResultItem
                    {
                        RelativePath = hit.RelativePath,
                        FullPath = fullPath,
                        CapturedAt = hit.CapturedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A",
                        SizeText = ByteSize.Humanize(hit.SizeBytes),
                        MediaType = hit.Type.ToString().ToLowerInvariant(),
                        MediaTypeIcon = GetMediaTypeIcon(hit.Type),
                        CameraModel = "Manifest",
                        GpsCoordinates = "Manifest",
                        HasThumbnail = isImage,
                        IsManualSelectedForIPhone = manualSet.Contains(hit.RelativePath),
                        IsManualSelectedForAndroid = androidManualSet.Contains(hit.RelativePath)
                    });
                }


                var gridRows = new List<GalleryRow>();
                const int itemsPerRow = 5;
                for (int i = 0; i < list.Count; i += itemsPerRow)
                {
                    var rowItems = list.Skip(i).Take(itemsPerRow).ToList();
                    var gRow = new GalleryRow();
                    foreach (var item in rowItems)
                    {
                        gRow.Items.Add(item);
                    }
                    gridRows.Add(gRow);
                }

                return (list, gridRows);
            }, ct).ConfigureAwait(false);

            loadedHitIndex += countToTake;
            bool remaining = loadedHitIndex < currentHits.Count;
            int total = currentHits.Count;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var item in newItems)
                {
                    SearchResults.Add(item);
                }
                foreach (var gRow in newGridRows)
                {
                    GalleryRows.Add(gRow);
                }

                HasMoreItems = remaining;
                if (remaining)
                {
                    SearchSummaryText = $"检索成功：共匹配 {total:N0} 项 (已加载 {loadedHitIndex:N0} 项，向下滚动自动加载)";
                }
                else
                {
                    SearchSummaryText = $"检索成功：共匹配 {total:N0} 项 (已全加载)";
                }
            });

            _ = LoadThumbnailsInBackgroundAsync(newItems, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LoadMoreItems error: {ex.Message}");
        }
        finally
        {
            isLoadingMore = false;
        }
    }

    private async Task LoadThumbnailsInBackgroundAsync(List<MediaSearchResultItem> items, CancellationToken ct)
    {
        var targetItems = items.Where(x => x.HasThumbnail && x.ThumbnailImage is null).ToList();
        if (targetItems.Count == 0) return;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 6),
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(targetItems, parallelOptions, async (item, token) =>
            {
                if (token.IsCancellationRequested) return;

                if (!File.Exists(item.FullPath)) return;

                ImageSource? thumb = LoadFrozenThumbnail(item.FullPath, 200);
                if (thumb is not null && !token.IsCancellationRequested)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.ThumbnailImage = thumb;
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Expected on new search cancellation
        }
    }

    [RelayCommand]
    private void OpenPreview(MediaSearchResultItem? item)
    {
        if (item is null)
        {
            if (SearchResults.Count > 0) item = SearchResults[0];
            else return;
        }
        int idx = SearchResults.IndexOf(item);
        if (idx < 0) idx = 0;
        SetPreviewIndex(idx);
    }

    [RelayCommand]
    private void ClosePreview()
    {
        IsPreviewOpen = false;
        PreviewItem = null;
        PreviewImageSource = null;
    }

    [RelayCommand]
    private void PrevPreview()
    {
        if (PreviewIndex > 0)
        {
            SetPreviewIndex(PreviewIndex - 1);
        }
    }

    [RelayCommand]
    private void NextPreview()
    {
        if (PreviewIndex < SearchResults.Count - 1)
        {
            SetPreviewIndex(PreviewIndex + 1);
        }
    }

    private void SetPreviewIndex(int index)
    {
        if (index < 0 || index >= SearchResults.Count) return;

        PreviewIndex = index;
        MediaSearchResultItem item = SearchResults[index];
        PreviewItem = item;
        IsPreviewOpen = true;

        HasPrevItem = index > 0;
        HasNextItem = index < SearchResults.Count - 1;

        PreviewDetailsText = $"{item.RelativePath} | 拍摄时间: {item.CapturedAt} | 大小: {item.SizeText}";

        if (File.Exists(item.FullPath))
        {
            try
            {
                using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();
                PreviewImageSource = bitmap;
            }
            catch
            {
                PreviewImageSource = item.ThumbnailImage;
            }
        }
        else
        {
            PreviewImageSource = item.ThumbnailImage;
        }
    }

    [RelayCommand]
    private void OpenFolder(MediaSearchResultItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(ArchivePath)) return;

        string fullPath = Path.Combine(ArchivePath, item.RelativePath);
        if (File.Exists(fullPath))
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenFolder error: {ex.Message}");
            }
        }
    }

    private static (List<string> existing, List<string> missing) FilterSelectedPaths(object? parameter)
    {
        var existing = new List<string>();
        var missing = new List<string>();

        if (parameter is null) return (existing, missing);

        try
        {
            var items = new List<MediaSearchResultItem>();
            if (parameter is MediaSearchResultItem singleItem)
            {
                items.Add(singleItem);
            }
            else if (parameter is GalleryRow singleRow)
            {
                items.AddRange(singleRow.Items);
            }
            else if (parameter is System.Collections.IEnumerable enumerable)
            {
                foreach (var element in enumerable)
                {
                    if (element is MediaSearchResultItem item)
                    {
                        items.Add(item);
                    }
                    else if (element is GalleryRow row)
                    {
                        items.AddRange(row.Items);
                    }
                }
            }

            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.FullPath))
                {
                    if (File.Exists(item.FullPath))
                    {
                        existing.Add(item.FullPath);
                    }
                    else
                    {
                        missing.Add(item.FullPath);
                    }
                }
            }

            if (missing.Count > 0)
            {
                MessageBox.Show(
                    $"有 {missing.Count} 个选中的文件在磁盘上已被删除或移走:\n{string.Join(Environment.NewLine, missing.Take(3))}{(missing.Count > 3 ? "\n..." : "")}",
                    App.GetString("MsgFileNotFound", "文件不存在"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FilterSelectedPaths error: {ex.Message}");
        }

        return (existing, missing);
    }

    [RelayCommand]
    private void Open(object? selectedItems)
    {
        var (existing, _) = FilterSelectedPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.OpenFiles(existing);
        }
    }

    [RelayCommand]
    private void OpenWith(object? selectedItems)
    {
        var (existing, _) = FilterSelectedPaths(selectedItems);
        if (existing.Count > 0)
        {
            IntPtr hwnd = GetMainWindowHandle();
            ShellServices.OpenWith(existing[0], hwnd);
        }
    }

    [RelayCommand]
    private void Copy(object? selectedItems)
    {
        var (existing, _) = FilterSelectedPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.CopyFilesToClipboard(existing);
        }
    }

    [RelayCommand]
    private void CopyPath(object? selectedItems)
    {
        var (existing, missing) = FilterSelectedPaths(selectedItems);
        var allTargeted = existing.Concat(missing).ToList();
        if (allTargeted.Count > 0)
        {
            ShellServices.CopyPathsToClipboard(allTargeted);
        }
    }

    [RelayCommand]
    private void ShowInExplorer(object? selectedItems)
    {
        var (existing, _) = FilterSelectedPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.ShowInExplorer(existing);
        }
    }

    [RelayCommand]
    private void Properties(object? selectedItems)
    {
        var (existing, _) = FilterSelectedPaths(selectedItems);
        if (existing.Count > 0)
        {
            IntPtr hwnd = GetMainWindowHandle();
            ShellServices.ShowProperties(existing, hwnd);
        }
    }

    [RelayCommand]
    private void AddToIPhoneSelection(object? parameter)
    {
        var items = ExtractMediaItems(parameter);
        if (items.Count == 0) return;

        var destPaths = items.Select(x => x.RelativePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (destPaths.Count == 0) return;

        string path = ArchivePath;
        string deviceModel = IPhoneSyncVM?.DeviceModel ?? "iPhone 15 Pro";

        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                using var journal = TransferJournal.Open(path);
                journal.BatchAddManualSelections(deviceModel, destPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BatchAddManualSelections error: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            item.IsManualSelectedForIPhone = true;
        }

        IPhoneSyncVM?.LoadManualSelectionsFromDb();
        IPhoneSyncVM?.RecalculateScopeSummary();
    }

    [RelayCommand]
    private void RemoveFromIPhoneSelection(object? parameter)
    {
        var items = ExtractMediaItems(parameter);
        if (items.Count == 0) return;

        var destPaths = items.Select(x => x.RelativePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (destPaths.Count == 0) return;

        string path = ArchivePath;
        string deviceModel = IPhoneSyncVM?.DeviceModel ?? "iPhone 15 Pro";

        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                using var journal = TransferJournal.Open(path);
                journal.BatchRemoveManualSelections(deviceModel, destPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BatchRemoveManualSelections error: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            item.IsManualSelectedForIPhone = false;
        }

        IPhoneSyncVM?.LoadManualSelectionsFromDb();
        IPhoneSyncVM?.RecalculateScopeSummary();
    }

    [RelayCommand]
    private void AddToAndroidSelection(object? parameter)
    {
        var items = ExtractMediaItems(parameter);
        if (items.Count == 0) return;

        var destPaths = items.Select(x => x.RelativePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (destPaths.Count == 0) return;

        string path = ArchivePath;
        string deviceId = AndroidSyncVM?.AndroidDeviceId ?? "Android Device";
        if (string.IsNullOrWhiteSpace(deviceId)) deviceId = "Android Device";

        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                using var journal = TransferJournal.Open(path);
                journal.BatchAddAndroidManualSelections(deviceId, destPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BatchAddAndroidManualSelections error: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            item.IsManualSelectedForAndroid = true;
        }

        AndroidSyncVM?.LoadManualSelectionsFromDb();
        AndroidSyncVM?.RecalculateScopeSummary();
    }

    [RelayCommand]
    private void RemoveFromAndroidSelection(object? parameter)
    {
        var items = ExtractMediaItems(parameter);
        if (items.Count == 0) return;

        var destPaths = items.Select(x => x.RelativePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (destPaths.Count == 0) return;

        string path = ArchivePath;
        string deviceId = AndroidSyncVM?.AndroidDeviceId ?? "Android Device";
        if (string.IsNullOrWhiteSpace(deviceId)) deviceId = "Android Device";

        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                using var journal = TransferJournal.Open(path);
                journal.BatchRemoveAndroidManualSelections(deviceId, destPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BatchRemoveAndroidManualSelections error: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            item.IsManualSelectedForAndroid = false;
        }

        AndroidSyncVM?.LoadManualSelectionsFromDb();
        AndroidSyncVM?.RecalculateScopeSummary();
    }

    private static List<MediaSearchResultItem> ExtractMediaItems(object? parameter)
    {
        var list = new List<MediaSearchResultItem>();
        if (parameter is MediaSearchResultItem single)
        {
            list.Add(single);
        }
        else if (parameter is System.Collections.IEnumerable collection)
        {
            foreach (var obj in collection)
            {
                if (obj is MediaSearchResultItem item)
                    list.Add(item);
            }
        }
        return list;
    }


    private static IntPtr GetMainWindowHandle()
    {
        var window = Application.Current.MainWindow;
        return window is not null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
    }

    private static ImageSource? LoadFrozenThumbnail(string filePath, int decodeWidth = 200)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

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

    private static bool IsImageFileExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp" or ".heic" or ".dng";
    }

    private static string GetMediaTypeIcon(MediaType type) => type switch
    {
        MediaType.Photo => "📷",
        MediaType.Video => "🎬",
        MediaType.Screenshot => "📱",
        _ => "📁"
    };

    private static MediaType? ParseMediaType(string selected)
    {
        if (selected.StartsWith("photo")) return MediaType.Photo;
        if (selected.StartsWith("video")) return MediaType.Video;
        if (selected.StartsWith("screenshot")) return MediaType.Screenshot;
        if (selected.StartsWith("other")) return MediaType.Other;
        return null;
    }
}
