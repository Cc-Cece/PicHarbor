using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;

using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

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
    public bool IsVideo { get; set; }
    public bool IsLivePhoto { get; set; } = false;
    public int PixelWidth { get; set; } = 0;
    public int PixelHeight { get; set; } = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThumbnailLoaded))]
    private ImageSource? thumbnailImage;

    public bool IsThumbnailLoaded => ThumbnailImage is not null;

    [ObservableProperty]
    private bool isManualSelectedForIPhone = false;

    [ObservableProperty]
    private bool isManualSelectedForAndroid = false;

    [ObservableProperty]
    private bool isManualSelectedForGooglePhotos = false;

    [ObservableProperty]
    private bool isSyncedToGooglePhotos = false;

    public string FileName => System.IO.Path.GetFileName(RelativePath);

    public string FormatUpper
    {
        get
        {
            if (IsLivePhoto) return "LIVE";
            string ext = System.IO.Path.GetExtension(RelativePath).TrimStart('.').ToUpperInvariant();
            return string.IsNullOrEmpty(ext) ? (IsVideo ? "MP4" : "JPG") : ext;
        }
    }

    public string DisplayCapturedAt
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CapturedAt)) return "—";
            if (DateTime.TryParse(CapturedAt, out var dt))
            {
                return dt.ToString("yyyy-MM-dd HH:mm");
            }
            return CapturedAt.Replace('T', ' ');
        }
    }
}


public partial class GalleryRow : ObservableObject
{
    public ObservableCollection<MediaSearchResultItem> Items { get; set; } = new();
}

public partial class SearchViewModel : ObservableObject
{
    private CancellationTokenSource? thumbnailCts;
    private Dictionary<string, (string Camera, string Gps)> displayByPath = new(StringComparer.OrdinalIgnoreCase);
    private List<string> knownCameraModels = new();
    private string allCamerasLabel = "";
    private bool suppressSort;
    private bool resortRequested;
    private bool suppressFilterSearch;

    public event EventHandler? HitsUpdated;
    public IReadOnlyList<MediaSearchHit> GetCurrentHits() => currentHits;
    public IReadOnlyDictionary<string, (string Camera, string Gps)> GetDisplayMeta() => displayByPath;

    public IPhoneSyncViewModel? IPhoneSyncVM { get; set; }
    public AndroidSyncViewModel? AndroidSyncVM { get; set; }
    public GooglePhotosSyncViewModel? GooglePhotosVM { get; set; }

    public ObservableCollection<string> CameraModels { get; } = new();

    public ObservableCollection<string> SortModeLabels { get; } = new();

    public SearchViewModel()
    {
        RefreshSortLabels();
        RefreshCameraModelItems(knownCameraModels);
        RefreshPlaybackLabel();
        _ = LoadCameraModelsAsync();
    }

    public void OnLanguageChanged()
    {
        RefreshSortLabels();
        RefreshCameraModelItems(knownCameraModels);
        RefreshPlaybackLabel();
    }

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

    public ObservableCollection<string> FilterTypeLabels { get; } = new()
    {
        "全部类型",
        "照片",
        "视频",
        "实况照片",
        "截屏"
    };

    [ObservableProperty]
    private int selectedTypeIndex = 0;

    partial void OnSelectedTypeIndexChanged(int value)
    {
        if (suppressFilterSearch) return;
        SelectedType = value switch
        {
            1 => "photo (照片)",
            2 => "video (视频)",
            3 => "live (实况照片)",
            4 => "screenshot (截图)",
            _ => "All (全部)"
        };
    }

    public ObservableCollection<string> DatePresetLabels { get; } = new()
    {
        "全部时间",
        "本年",
        "去年",
        "近90天",
        "近30天",
        "自定义..."
    };

    [ObservableProperty]
    private int selectedDatePresetIndex = 0;

    public bool IsCustomDateRangeVisible => SelectedDatePresetIndex == 5;

    partial void OnSelectedDatePresetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomDateRangeVisible));
        if (suppressFilterSearch) return;

        suppressFilterSearch = true;
        switch (value)
        {
            case 0:
                ClearDateRange();
                break;
            case 1:
                SetDatePresetThisYear();
                break;
            case 2:
                FilterToDate = new DateTime(DateTime.Today.Year - 1, 12, 31);
                FilterFromDate = new DateTime(DateTime.Today.Year - 1, 1, 1);
                break;
            case 3:
                SetDatePresetLast90Days();
                break;
            case 4:
                SetDatePresetLast30Days();
                break;
            case 5:
                break;
        }
        suppressFilterSearch = false;
    }

    [ObservableProperty]
    private string selectedType = "All (全部)";

    partial void OnSelectedTypeChanged(string value)
    {
    }

    [ObservableProperty]
    private int selectedViewCategory = 0;

    public bool IsCategoryAll
    {
        get => SelectedViewCategory == 0;
        set { if (value) SelectedViewCategory = 0; }
    }

    public bool IsCategoryVideo
    {
        get => SelectedViewCategory == 1;
        set { if (value) SelectedViewCategory = 1; }
    }

    public bool IsCategoryCamera
    {
        get => SelectedViewCategory == 2;
        set { if (value) SelectedViewCategory = 2; }
    }

    public bool IsCategoryManual
    {
        get => SelectedViewCategory == 3;
        set { if (value) SelectedViewCategory = 3; }
    }

    public bool IsCategoryExport
    {
        get => SelectedViewCategory == 4;
        set { if (value) SelectedViewCategory = 4; }
    }

    partial void OnSelectedViewCategoryChanged(int value)
    {
        OnPropertyChanged(nameof(IsCategoryAll));
        OnPropertyChanged(nameof(IsCategoryVideo));
        OnPropertyChanged(nameof(IsCategoryCamera));
        OnPropertyChanged(nameof(IsCategoryManual));
        OnPropertyChanged(nameof(IsCategoryExport));

        if (value == 4)
        {
            if (IPhoneSyncVM != null && IPhoneSyncVM.HasManualSelections)
            {
                IPhoneSyncVM.IsManualModalOpen = true;
            }
            else if (AndroidSyncVM != null && AndroidSyncVM.HasManualSelections)
            {
                AndroidSyncVM.IsManualModalOpen = true;
            }
            else if (IPhoneSyncVM != null)
            {
                IPhoneSyncVM.IsManualModalOpen = true;
            }
        }

        if (value == 1)
        {
            SelectedType = "video (视频)";
        }
        else
        {
            SelectedType = "All (全部)";
        }

        if (!suppressFilterSearch)
        {
            _ = SearchAsync();
        }
    }

    [ObservableProperty]
    private string cameraFilter = "";

    [ObservableProperty]
    private int cameraInputMode;

    [ObservableProperty]
    private string selectedCameraModel = "";

    partial void OnSelectedCameraModelChanged(string value)
    {
    }

    [ObservableProperty]
    private string cameraKeyword = "";

    public bool IsCameraPickMode
    {
        get => CameraInputMode == 0;
        set
        {
            if (value)
            {
                CameraInputMode = 0;
            }
        }
    }

    public bool IsCameraKeywordMode
    {
        get => CameraInputMode == 1;
        set
        {
            if (value)
            {
                CameraInputMode = 1;
            }
        }
    }

    partial void OnCameraInputModeChanged(int value)
    {
        if (value is not 0 and not 1)
        {
            CameraInputMode = 0;
            return;
        }

        OnPropertyChanged(nameof(IsCameraPickMode));
        OnPropertyChanged(nameof(IsCameraKeywordMode));
    }

    partial void OnArchivePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            knownCameraModels = new List<string>();
            RefreshCameraModelItems(knownCameraModels);
            return;
        }

        _ = LoadCameraModelsAsync();
    }

    [ObservableProperty]
    private int sortModeIndex;

    [ObservableProperty]
    private bool sortDescending;

    public string SortDirectionText => SortDescending
        ? App.GetString("SortDescending", "降序")
        : App.GetString("SortAscending", "升序");

    partial void OnSortModeIndexChanged(int value)
    {
        if (suppressSort)
        {
            return;
        }

        if (value is < 0 or > 4)
        {
            suppressSort = true;
            SortModeIndex = 0;
            suppressSort = false;
            return;
        }

        RequestResort();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        if (!suppressSort)
        {
            RequestResort();
        }
    }

    [RelayCommand]
    private void ToggleSortDirection()
    {
        SortDescending = !SortDescending;
    }

    [ObservableProperty]
    private string searchText = "";

    partial void OnSearchTextChanged(string value)
    {
        if (FileNameFilter != value)
        {
            FileNameFilter = value;
        }
    }

    [RelayCommand]
    public async Task ApplyFiltersAsync()
    {
        await SearchAsync();
    }

    [ObservableProperty]
    private string fileNameFilter = "";

    partial void OnFileNameFilterChanged(string value)
    {
        if (SearchText != value)
        {
            SearchText = value;
        }
    }

    [RelayCommand]
    private void ClearAllFilters()
    {
        suppressFilterSearch = true;
        SearchText = "";
        FileNameFilter = "";
        SelectedTypeIndex = 0;
        SelectedType = "All (全部)";
        SelectedDatePresetIndex = 0;
        ClearDateRange();
        SelectedCameraModel = allCamerasLabel;
        CameraKeyword = "";
        SelectedViewCategory = 0;
        suppressFilterSearch = false;
        _ = SearchAsync();
    }

    [ObservableProperty]
    private bool hasGpsOnly = false;

    [ObservableProperty]
    private bool includeHeic = true;

    [ObservableProperty]
    private bool isTableView = false;

    partial void OnIsTableViewChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMoreInTableView));
    }

    [ObservableProperty]
    private bool isGalleryView = true;

    [ObservableProperty]
    private string searchSummaryText = "";

    [ObservableProperty]
    private bool isSearching = false;

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ApplyFilterButtonText));
    }

    public string ApplyFilterButtonText => IsSearching ? "正在筛选..." : "应用筛选";

    [ObservableProperty]
    private bool isPreviewOpen = false;

    [ObservableProperty]
    private MediaSearchResultItem? previewItem;

    [ObservableProperty]
    private int previewIndex = -1;

    [ObservableProperty]
    private ImageSource? previewImageSource;

    [ObservableProperty]
    private bool isPreviewVideo;

    [ObservableProperty]
    private string previewVideoPath = "";

    [ObservableProperty]
    private bool isPreviewPlaying;

    [ObservableProperty]
    private bool isPreviewVideoFailed;

    [ObservableProperty]
    private string previewStatusText = "";

    [ObservableProperty]
    private string previewPlaybackLabel = "播放";

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

    partial void OnHasMoreItemsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMoreInTableView));
    }

    public bool CanLoadMoreInTableView => IsTableView && HasMoreItems;

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
        bool filterLiveOnly = SelectedType.Contains("live") || SelectedType.Contains("实况");
        MediaType? mediaType = filterLiveOnly ? null : ParseMediaType(SelectedType);
        string camera = ResolveCameraFilter();

        var criteria = new MediaSearchCriteria(
            From: fromDate,
            To: toDate,
            Type: mediaType,
            Camera: string.IsNullOrWhiteSpace(camera) ? null : camera,
            HasGps: HasGpsOnly,
            IncludeHeic: IncludeHeic,
            FileNameKeyword: string.IsNullOrWhiteSpace(FileNameFilter) ? null : FileNameFilter);

        IsSearching = true;
        SearchSummaryText = "正在检索 SQLite 数据库...";

        try
        {
            string archivePathCopy = ArchivePath;
            int category = SelectedViewCategory;
            var manualSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (category == 3)
            {
                var s1 = IPhoneSyncVM?.GetManualSelectionPathsSet();
                var s2 = AndroidSyncVM?.GetManualSelectionPathsSet();
                var s3 = GooglePhotosVM?.GetManualSelectionPathsSet();
                if (s1 != null) foreach (var p in s1) manualSet.Add(p);
                if (s2 != null) foreach (var p in s2) manualSet.Add(p);
                if (s3 != null) foreach (var p in s3) manualSet.Add(p);
            }

            var (hits, display, models) = await Task.Run(() =>
            {
                using var journal = TransferJournal.OpenReadOnly(archivePathCopy);
                IReadOnlyList<ManifestSearchRow> rows = journal.ReadSearchRows();
                var found = MediaSearch.Find(rows, criteria).ToList();

                // 1. Purge deleted or 0-byte ghost files on disk (prevents missing placeholders)
                if (!string.IsNullOrWhiteSpace(archivePathCopy) && Directory.Exists(archivePathCopy))
                {
                    found = found.Where(h =>
                    {
                        string p = Path.Combine(archivePathCopy, h.RelativePath);
                        if (!File.Exists(p)) return false;
                        try
                        {
                            return new FileInfo(p).Length > 0;
                        }
                        catch
                        {
                            return false;
                        }
                    }).ToList();
                }

                // 2. Filter Live Photos (must have both still and motion video)
                if (filterLiveOnly)
                {
                    var stillStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var videoStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in rows)
                    {
                        string ext = Path.GetExtension(r.RelativePath).ToLowerInvariant();
                        string stem = Path.ChangeExtension(r.RelativePath, null);
                        if (ext is ".mov" or ".mp4") videoStems.Add(stem);
                        else if (ext is ".heic" or ".jpg" or ".jpeg") stillStems.Add(stem);
                    }
                    var liveStems = stillStems.Intersect(videoStems).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    found = found.Where(h => liveStems.Contains(Path.ChangeExtension(h.RelativePath, null))).ToList();
                }

                // 3. Category: Manual Selection
                if (category == 3)
                {
                    found = manualSet.Count > 0
                        ? found.Where(h => manualSet.Contains(h.RelativePath)).ToList()
                        : new List<MediaSearchHit>();
                }

                return (found, BuildDisplay(rows), DistinctCameraModels(rows));
            }, ct).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                displayByPath = display;
                knownCameraModels = models;
                RefreshCameraModelItems(models);
                currentHits = hits;
                SortCurrentHits();
                loadedHitIndex = 0;
                SearchResults = new ObservableCollection<MediaSearchResultItem>();
                GalleryRows = new ObservableCollection<GalleryRow>();
                HasMoreItems = currentHits.Count > 0;
                SearchSummaryText = $"检索成功：共匹配 {currentHits.Count:N0} 项";
                HitsUpdated?.Invoke(this, EventArgs.Empty);
            });

            if (currentHits.Count == 0)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    SearchSummaryText = "检索成功：共匹配 0 项";
                    HasMoreItems = false;
                    HitsUpdated?.Invoke(this, EventArgs.Empty);
                });
            }
            else
            {
                await LoadMoreItemsAsync();
            }
        }
        catch (FileNotFoundException)
        {
            SearchSummaryText = "尚未发现 SQLite 归档数据库 (picharbor.db)。";
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
            var display = displayByPath;

            var manualSet = IPhoneSyncVM?.GetManualSelectionPathsSet() ?? new HashSet<string>();
            var androidManualSet = AndroidSyncVM?.GetManualSelectionPathsSet() ?? new HashSet<string>();
            var googleManualSet = GooglePhotosVM?.GetManualSelectionPathsSet() ?? new HashSet<string>();
            HashSet<string> googleSyncedSet = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(archivePathCopy) && Directory.Exists(archivePathCopy))
            {
                try
                {
                    using var j = TransferJournal.OpenReadOnly(archivePathCopy);
                    googleSyncedSet = j.GetGooglePhotosSyncedDestPaths();
                }
                catch { }
            }

            var (newItems, newGridRows) = await Task.Run(() =>
            {
                var list = new List<MediaSearchResultItem>(countToTake);
                for (int i = 0; i < countToTake; i++)
                {
                    MediaSearchHit hit = currentHits[startIndex + i];
                    string fullPath = Path.Combine(archivePathCopy, hit.RelativePath);
                    bool isVideo = hit.Type == MediaType.Video;
                    string cameraName = "";
                    string gps = "";
                    if (display.TryGetValue(hit.RelativePath, out var meta))
                    {
                        cameraName = meta.Camera ?? "";
                        gps = meta.Gps ?? "";
                    }

                    int pixelWidth = 0;
                    int pixelHeight = 0;
                    bool isLive = false;

                    if (File.Exists(fullPath))
                    {
                        var dims = ImageDimensionHelper.GetDimensions(fullPath);
                        if (dims.HasValue)
                        {
                            pixelWidth = dims.Value.Width;
                            pixelHeight = dims.Value.Height;
                        }

                        if (isVideo)
                        {
                            string? still = ImageDimensionHelper.FindLivePhotoStill(fullPath);
                            if (still != null)
                            {
                                isLive = true;
                                if (pixelWidth <= 0 || pixelHeight <= 0)
                                {
                                    var stillDims = ImageDimensionHelper.GetDimensions(still);
                                    if (stillDims.HasValue)
                                    {
                                        pixelWidth = stillDims.Value.Width;
                                        pixelHeight = stillDims.Value.Height;
                                    }
                                }
                            }
                        }
                        else
                        {
                            string? pairedVideo = ImageDimensionHelper.FindLivePhotoVideo(fullPath);
                            if (pairedVideo != null)
                            {
                                isLive = true;
                            }
                        }
                    }

                    // Adaptive fallback dimensions based on media type
                    if (pixelWidth <= 0 || pixelHeight <= 0)
                    {
                        if (hit.Type == MediaType.Screenshot)
                        {
                            // Modern mobile screenshots (iPhone 14/15/16 Pro: 1179x2556, standard 9:19.5 aspect ratio)
                            pixelWidth = 1179;
                            pixelHeight = 2556;
                        }
                        else if (isVideo)
                        {
                            pixelWidth = 1920;
                            pixelHeight = 1080;
                        }
                        else
                        {
                            pixelWidth = 4032;
                            pixelHeight = 3024;
                        }
                    }

                    list.Add(new MediaSearchResultItem
                    {
                        RelativePath = hit.RelativePath,
                        FullPath = fullPath,
                        CapturedAt = hit.CapturedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A",
                        SizeText = ByteSize.Humanize(hit.SizeBytes),
                        MediaType = hit.Type.ToString().ToLowerInvariant(),
                        MediaTypeIcon = GetMediaTypeIcon(hit.Type),
                        CameraModel = cameraName,
                        GpsCoordinates = gps,
                        HasThumbnail = isVideo || hit.Type is MediaType.Photo or MediaType.Screenshot,
                        IsVideo = isVideo,
                        IsLivePhoto = isLive,
                        PixelWidth = pixelWidth,
                        PixelHeight = pixelHeight,
                        IsManualSelectedForIPhone = manualSet.Contains(hit.RelativePath),
                        IsManualSelectedForAndroid = androidManualSet.Contains(hit.RelativePath),
                        IsManualSelectedForGooglePhotos = googleManualSet.Contains(hit.RelativePath),
                        IsSyncedToGooglePhotos = googleSyncedSet.Contains(hit.RelativePath)
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
                    SearchSummaryText = $"检索成功：共匹配 {total:N0} 项";
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
            if (resortRequested)
            {
                resortRequested = false;
                _ = ResortAndReloadAsync();
            }
        }
    }

    private void RequestResort()
    {
        if (currentHits.Count == 0)
        {
            return;
        }

        if (isLoadingMore)
        {
            resortRequested = true;
            return;
        }

        _ = ResortAndReloadAsync();
    }

    private async Task ResortAndReloadAsync()
    {
        thumbnailCts?.Cancel();
        thumbnailCts = new CancellationTokenSource();
        SortCurrentHits();
        loadedHitIndex = 0;
        SearchResults = new ObservableCollection<MediaSearchResultItem>();
        GalleryRows = new ObservableCollection<GalleryRow>();
        HasMoreItems = currentHits.Count > 0;
        if (HasMoreItems)
        {
            await LoadMoreItemsAsync();
        }
    }

    private void SortCurrentHits()
    {
        int sign = SortDescending ? -1 : 1;
        currentHits.Sort((left, right) =>
        {
            int compared = SortModeIndex switch
            {
                1 => string.Compare(
                    Path.GetFileName(left.RelativePath),
                    Path.GetFileName(right.RelativePath),
                    StringComparison.OrdinalIgnoreCase),
                2 => left.SizeBytes.CompareTo(right.SizeBytes),
                3 => left.Type.CompareTo(right.Type),
                4 => string.Compare(CameraOf(left), CameraOf(right), StringComparison.OrdinalIgnoreCase),
                _ => CompareCaptured(left.CapturedAt, right.CapturedAt, SortDescending)
            };

            if (SortModeIndex != 0)
            {
                compared *= sign;
            }

            return compared != 0
                ? compared
                : string.CompareOrdinal(left.RelativePath, right.RelativePath);
        });
    }

    private string CameraOf(MediaSearchHit hit)
    {
        return displayByPath.TryGetValue(hit.RelativePath, out var meta) ? meta.Camera ?? "" : "";
    }

    private static int CompareCaptured(DateTimeOffset? left, DateTimeOffset? right, bool descending)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        int compared = left.Value.CompareTo(right.Value);
        return descending ? -compared : compared;
    }

    private void RefreshSortLabels()
    {
        suppressSort = true;
        int selected = SortModeIndex is >= 0 and <= 4 ? SortModeIndex : 0;
        SortModeLabels.Clear();
        SortModeLabels.Add(App.GetString("SortByCaptured", "拍摄时间"));
        SortModeLabels.Add(App.GetString("SortByName", "文件名"));
        SortModeLabels.Add(App.GetString("SortBySize", "文件大小"));
        SortModeLabels.Add(App.GetString("SortByType", "媒体类型"));
        SortModeLabels.Add(App.GetString("SortByCamera", "拍摄型号"));
        SortModeIndex = selected;
        suppressSort = false;
        OnPropertyChanged(nameof(SortDirectionText));
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

                ImageSource? thumb = LoadResultThumbnail(item);
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
    public void PlayLiveVideo(MediaSearchResultItem? item)
    {
        if (item is null) return;
        string? videoPath = item.IsVideo ? item.FullPath : ImageDimensionHelper.FindLivePhotoVideo(item.FullPath);
        if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
        {
            var match = SearchResults.FirstOrDefault(x => string.Equals(x.FullPath, videoPath, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                OpenPreview(match);
                return;
            }

            int idx = SearchResults.IndexOf(item);
            PreviewIndex = idx >= 0 ? idx : 0;
            PreviewItem = item;
            IsPreviewOpen = true;
            IsPreviewVideo = true;
            PreviewVideoPath = videoPath;
            PreviewImageSource = item.ThumbnailImage;
            PreviewDetailsText = $"{Path.GetFileName(videoPath)} (实况动图) | 拍摄时间: {item.CapturedAt}";
            IsPreviewVideoFailed = false;
            PreviewStatusText = "";
            IsPreviewPlaying = false;
        }
    }

    [RelayCommand]
    private void ClosePreview()
    {
        IsPreviewOpen = false;
        IsPreviewVideo = false;
        PreviewVideoPath = "";
        IsPreviewPlaying = false;
        IsPreviewVideoFailed = false;
        PreviewStatusText = "";
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
        IsPreviewVideoFailed = false;
        PreviewStatusText = "";
        IsPreviewPlaying = false;

        if (item.IsVideo)
        {
            PreviewImageSource = item.ThumbnailImage;
            PreviewVideoPath = File.Exists(item.FullPath) ? item.FullPath : "";
            IsPreviewVideo = true;
            if (string.IsNullOrEmpty(PreviewVideoPath))
            {
                IsPreviewVideoFailed = true;
                PreviewStatusText = App.GetString("PreviewVideoMissing", "找不到视频文件。");
            }
            else
            {
                _ = UpgradeVideoPosterAsync(item, index);
            }

            return;
        }

        IsPreviewVideo = false;
        PreviewVideoPath = "";

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
    private async Task Open(object? selectedItems)
    {
        var (existing, _) = FilterSelectedPaths(selectedItems);
        if (existing.Count == 0)
        {
            return;
        }

        if (existing.Count == 1 && IsImageFileExtension(existing[0]))
        {
            await ShellServices.OpenImageWithNeighborsAsync(existing[0]);
            return;
        }

        ShellServices.OpenFiles(existing);
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

    [RelayCommand]
    private void AddToGooglePhotosSelection(object? parameter)
    {
        var items = ExtractMediaItems(parameter);
        if (items.Count == 0) return;

        var destPaths = items.Select(x => x.RelativePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (destPaths.Count == 0) return;

        string path = ArchivePath;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                using var journal = TransferJournal.Open(path);
                journal.BatchAddGooglePhotosManualSelections(destPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BatchAddGooglePhotosManualSelections error: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            item.IsManualSelectedForGooglePhotos = true;
        }

        GooglePhotosVM?.LoadManualSelectionsFromDb();
        GooglePhotosVM?.RecalculateScopeSummary();
    }

    [RelayCommand]
    private void RemoveFromGooglePhotosSelection(object? parameter)
    {
        var items = ExtractMediaItems(parameter);
        if (items.Count == 0) return;

        var destPaths = items.Select(x => x.RelativePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (destPaths.Count == 0) return;

        string path = ArchivePath;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            try
            {
                using var journal = TransferJournal.Open(path);
                journal.BatchRemoveGooglePhotosManualSelections(destPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BatchRemoveGooglePhotosManualSelections error: {ex.Message}");
            }
        }

        foreach (var item in items)
        {
            item.IsManualSelectedForGooglePhotos = false;
        }

        GooglePhotosVM?.LoadManualSelectionsFromDb();
        GooglePhotosVM?.RecalculateScopeSummary();
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

    public void AddItemsToManualSelection(string type, IReadOnlyList<string> relativePaths)
    {
        var targets = SearchResults.Where(x => relativePaths.Contains(x.RelativePath)).ToList();
        if (targets.Count == 0) return;

        if (type.Equals("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            AddToIPhoneSelection(targets);
        }
        else if (type.Equals("Android", StringComparison.OrdinalIgnoreCase))
        {
            AddToAndroidSelection(targets);
        }
        else if (type.Equals("Google", StringComparison.OrdinalIgnoreCase))
        {
            AddToGooglePhotosSelection(targets);
        }
    }

    public void RemoveItemsFromManualSelection(IReadOnlyList<string> relativePaths)
    {
        var targets = SearchResults.Where(x => relativePaths.Contains(x.RelativePath)).ToList();
        if (targets.Count == 0) return;

        RemoveFromIPhoneSelection(targets);
        RemoveFromAndroidSelection(targets);
        RemoveFromGooglePhotosSelection(targets);
    }

    public void ToggleItemsManualSelection(string type, IReadOnlyList<string> relativePaths)
    {
        var targets = SearchResults.Where(x => relativePaths.Contains(x.RelativePath)).ToList();
        if (targets.Count == 0) return;

        if (type.Equals("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            bool anyUnselected = targets.Any(x => !x.IsManualSelectedForIPhone);
            if (anyUnselected)
                AddToIPhoneSelection(targets);
            else
                RemoveFromIPhoneSelection(targets);
        }
        else if (type.Equals("Android", StringComparison.OrdinalIgnoreCase))
        {
            bool anyUnselected = targets.Any(x => !x.IsManualSelectedForAndroid);
            if (anyUnselected)
                AddToAndroidSelection(targets);
            else
                RemoveFromAndroidSelection(targets);
        }
    }

    [RelayCommand]
    public void ToggleIPhoneManualSelection(MediaSearchResultItem item)
    {
        if (item == null) return;
        ToggleItemsManualSelection("iPhone", new[] { item.RelativePath });
    }

    [RelayCommand]
    public void ToggleAndroidManualSelection(MediaSearchResultItem item)
    {
        if (item == null) return;
        ToggleItemsManualSelection("Android", new[] { item.RelativePath });
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = Application.Current.MainWindow;
        return window is not null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
    }

    private static readonly string[] LiveStillExtensions =
    [
        ".heic", ".heif", ".jpg", ".jpeg", ".png", ".webp"
    ];

    private static string? FindLivePhotoStill(string videoPath)
    {
        string? directory = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrEmpty(stem))
        {
            return null;
        }

        foreach (string extension in LiveStillExtensions)
        {
            string candidate = Path.Combine(directory, stem + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ImageSource? LoadResultThumbnail(MediaSearchResultItem item)
    {
        if (item.IsVideo)
        {
            return LoadVideoStill(item.FullPath, 200);
        }

        return LoadFrozenThumbnail(item.FullPath, 200) ?? ShellServices.GetShellThumbnail(item.FullPath, 200, 200, thumbnailOnly: true);
    }

    private async Task UpgradeVideoPosterAsync(MediaSearchResultItem item, int index)
    {
        try
        {
            ImageSource? poster = await Task.Run(() => LoadVideoStill(item.FullPath, 960)).ConfigureAwait(false);
            if (poster is null || Application.Current is null)
            {
                return;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (PreviewIndex == index && ReferenceEquals(PreviewItem, item))
                {
                    PreviewImageSource = poster;
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpgradeVideoPoster error: {ex.Message}");
        }
    }

    /// <summary>
    /// Live Photo videos share a folder and file stem with a still. HEIC/HEIF stills are decoded by the
    /// shell so orientation matches Explorer; WPF's bitmap decoder often shows those files blank or rotated.
    /// </summary>
    private static ImageSource? LoadVideoStill(string videoPath, int decodeWidth)
    {
        string? still = FindLivePhotoStill(videoPath);
        if (still is not null)
        {
            ImageSource? fromStill = IsHeif(still)
                ? ShellServices.GetShellThumbnail(still, decodeWidth, decodeWidth, thumbnailOnly: true) ?? LoadFrozenThumbnail(still, decodeWidth)
                : LoadFrozenThumbnail(still, decodeWidth) ?? ShellServices.GetShellThumbnail(still, decodeWidth, decodeWidth, thumbnailOnly: true);
            if (fromStill is not null)
            {
                return fromStill;
            }
        }

        return ShellServices.GetShellThumbnail(videoPath, decodeWidth, decodeWidth, thumbnailOnly: true);
    }

    private static bool IsHeif(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsPreviewPlayingChanged(bool value) => RefreshPlaybackLabel();

    private void RefreshPlaybackLabel()
    {
        PreviewPlaybackLabel = IsPreviewPlaying
            ? App.GetString("PreviewPause", "暂停")
            : App.GetString("PreviewPlay", "播放");
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

    private static bool IsImageFileExtension(string path) => ShellServices.IsPicture(path);

    private async Task LoadCameraModelsAsync()
    {
        string path = ArchivePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            if (Application.Current is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => RefreshCameraModelItems(knownCameraModels));
            }

            return;
        }

        try
        {
            var models = await Task.Run(() =>
            {
                using var journal = TransferJournal.OpenReadOnly(path);
                return DistinctCameraModels(journal.ReadSearchRows());
            }).ConfigureAwait(false);

            if (!string.Equals(path, ArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            knownCameraModels = models;
            if (Application.Current is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (string.Equals(path, ArchivePath, StringComparison.OrdinalIgnoreCase))
                    {
                        RefreshCameraModelItems(knownCameraModels);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LoadCameraModels error: {ex.Message}");
        }
    }

    private string ResolveCameraFilter()
    {
        if (CameraInputMode == 0)
        {
            if (string.IsNullOrWhiteSpace(SelectedCameraModel) ||
                string.Equals(SelectedCameraModel, allCamerasLabel, StringComparison.Ordinal))
            {
                return "";
            }

            return SelectedCameraModel.Trim();
        }

        return CameraKeyword.Trim();
    }

    private void RefreshCameraModelItems(IReadOnlyList<string> models)
    {
        allCamerasLabel = App.GetString("CameraAllModels", "全部型号");
        string previous = SelectedCameraModel;
        bool keepSpecific = !string.IsNullOrEmpty(previous)
            && !string.Equals(previous, allCamerasLabel, StringComparison.Ordinal)
            && models.Contains(previous, StringComparer.Ordinal);

        CameraModels.Clear();
        CameraModels.Add(allCamerasLabel);
        foreach (string model in models)
        {
            if (!string.Equals(model, allCamerasLabel, StringComparison.Ordinal))
            {
                CameraModels.Add(model);
            }
        }

        suppressFilterSearch = true;
        SelectedCameraModel = keepSpecific ? previous : allCamerasLabel;
        suppressFilterSearch = false;
    }

    private static Dictionary<string, (string Camera, string Gps)> BuildDisplay(IReadOnlyList<ManifestSearchRow> rows)
    {
        var display = new Dictionary<string, (string Camera, string Gps)>(rows.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ManifestSearchRow row in rows)
        {
            display[row.RelativePath] = (row.CameraModel?.Trim() ?? "", FormatGps(row));
        }

        return display;
    }

    private static List<string> DistinctCameraModels(IReadOnlyList<ManifestSearchRow> rows)
    {
        return rows
            .Select(row => row.CameraModel?.Trim())
            .Where(model => !string.IsNullOrEmpty(model))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string FormatGps(ManifestSearchRow row)
    {
        if (row.GpsLatitude is not double latitude || row.GpsLongitude is not double longitude)
        {
            return "";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{latitude:0.#####}, {longitude:0.#####}");
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
