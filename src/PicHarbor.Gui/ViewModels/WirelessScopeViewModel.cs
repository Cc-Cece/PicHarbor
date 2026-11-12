using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Link;

namespace PicHarbor.Gui.ViewModels;

public partial class WirelessAlbumRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public bool IsDefault { get; init; }
    public Action? Edited { get; init; }
    [ObservableProperty] private bool isChecked;
    partial void OnIsCheckedChanged(bool value) => Edited?.Invoke();
}

public partial class WirelessLinkViewModel
{
    private bool loadingScope;
    private bool previewPending;
    private DispatcherTimer? scopeDebounce;
    private LinkCatalogReport? lastCatalogReport;
    private readonly Dictionary<string, LinkScopeSpec> savedScopes = LoadScopes();
    private readonly Dictionary<string, string> deviceStates = new();
    private static string ScopeFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "link", "scopes.json");
    public ObservableCollection<WirelessAlbumRow> Albums { get; } = new();
    public ObservableCollection<string> TransferLog { get; } = new();
    [ObservableProperty] private WirelessPeerRow? selectedPeer;
    [ObservableProperty] private bool isConnectionInfoOpen;
    [ObservableProperty] private bool isScopeOpen;
    [ObservableProperty] private bool isLogOpen;
    [ObservableProperty] private int scopeModeIndex;
    [ObservableProperty] private bool dateEnabled;
    [ObservableProperty] private bool includePhotos = true;
    [ObservableProperty] private bool includeVideos = true;
    [ObservableProperty] private bool ignoreSmallImages = true;
    [ObservableProperty] private int minImageKb = 100;
    [ObservableProperty] private bool filterCache = true;
    [ObservableProperty] private string scopeSummary = "请选择设备。";
    [ObservableProperty] private string selectedDeviceStatus = "";
    public string ArchiveDirectory => archivePath;
    public string BackupSubtitle => !HasArchive ? App.GetString("PairArchiveMissing", "请先在设置中配置归档目录")
        : SelectedPeer is not null && host?.TransferStatus(SelectedPeer.PairingId) is { } state ? state : archivePath;
    public bool CanUseScopeV2 => SelectedPeer?.ScopeV2 == true;
    public string CapabilityHint => CanUseScopeV2 ? "手机确认后才会传输；手机主动发送采用手机选择。" : "旧版手机仅支持全量和日期范围；更新手机端可启用相册和其他过滤器。";
    public bool CanStartSelected => Listening && SelectedPeer?.CanAsk == true && HasArchive && !HasOffer && !IsScopeOpen
        && BuildSelection().Validate() is null && (!CanUseScopeV2 || !previewPending && host?.Catalog(SelectedPeer.PairingId) is { Error: null, Count: > 0 });

    partial void OnSelectedDeviceStatusChanged(string value)
    {
        OnPropertyChanged(nameof(BackupSubtitle));
        if (loadingScope || string.IsNullOrWhiteSpace(value)) return;
        TransferLog.Add(DateTime.Now.ToString("HH:mm:ss") + " " + SelectedPeer?.Title + " · " + value);
        if (TransferLog.Count > 1000) TransferLog.RemoveAt(0);
    }
    partial void OnSelectedPeerChanged(WirelessPeerRow? value)
    {
        if (loadingScope) return;
        scopeDebounce?.Stop();
        loadingScope = true;
        try
        {
            var key = value?.ArchiveId ?? value?.PairingId ?? "";
            var spec = savedScopes.GetValueOrDefault(key) ?? new LinkScopeSpec();
            if (value?.ScopeV2 != true) spec = new LinkScopeSpec { Mode = "all", IgnoreSmallImages = false, FilterCache = false };
            ScopeModeIndex = Array.IndexOf(new[] { "default", "all", "date", "advanced" }, spec.Mode);
            DateEnabled = spec.DateEnabled; IncludePhotos = spec.Photos; IncludeVideos = spec.Videos;
            IgnoreSmallImages = spec.IgnoreSmallImages; MinImageKb = spec.MinImageKb; FilterCache = spec.FilterCache;
            ScopeFrom = DateTime.TryParseExact(spec.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ? from : null;
            ScopeTo = DateTime.TryParseExact(spec.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ? to : null;
            Albums.Clear(); lastCatalogReport = null; previewPending = true;
            SelectedDeviceStatus = value is null ? "请选择设备。" : deviceStates.GetValueOrDefault(value.PairingId, value.Detail);
        }
        finally { loadingScope = false; }
        NotifySelection();
        RequestPreview();
    }
    partial void OnScopeModeIndexChanged(int value)
    {
        if (loadingScope) return;
        loadingScope = true;
        try
        {
            if (value == 2) DateEnabled = true;
            if (value is 0 or 1 or 2) foreach (var album in Albums) album.IsChecked = value != 0 || album.IsDefault;
        }
        finally { loadingScope = false; }
        ScopeEdited();
    }
    partial void OnDateEnabledChanged(bool value) => ScopeEdited();
    partial void OnIncludePhotosChanged(bool value) => ScopeEdited();
    partial void OnIncludeVideosChanged(bool value) => ScopeEdited();
    partial void OnIgnoreSmallImagesChanged(bool value) => ScopeEdited();
    partial void OnMinImageKbChanged(int value) => ScopeEdited();
    partial void OnFilterCacheChanged(bool value) => ScopeEdited();
    partial void OnScopeFromChanged(DateTime? value) => ScopeEdited();
    partial void OnScopeToChanged(DateTime? value) => ScopeEdited();
    partial void OnIsScopeOpenChanged(bool value) => NotifySelection();
    private LinkScopeSpec BuildSelection() => new()
    {
        Mode = new[] { "default", "all", "date", "advanced" }[Math.Clamp(ScopeModeIndex, 0, 3)],
        Albums = Albums.Where(album => album.IsChecked).Select(album => album.Id).ToArray(),
        DateEnabled = DateEnabled, From = ScopeFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        To = ScopeTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Photos = IncludePhotos, Videos = IncludeVideos,
        IgnoreSmallImages = IgnoreSmallImages, MinImageKb = MinImageKb, FilterCache = FilterCache,
    };
    private void ScopeEdited()
    {
        if (loadingScope || SelectedPeer is null) return;
        var spec = BuildSelection();
        // Retain saved album IDs until the phone returns its refreshed directory list.
        savedScopes[SelectedPeer.ArchiveId ?? SelectedPeer.PairingId] = spec;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScopeFile)!);
            string temporary = ScopeFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(savedScopes));
            File.Move(temporary, ScopeFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = ex.Message; }
        previewPending = true;
        ScopeSummary = spec.Validate() ?? "正在等待手机计算范围…";
        NotifySelection();
        scopeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        scopeDebounce.Stop(); scopeDebounce.Tick -= ScopeDebounceTick; scopeDebounce.Tick += ScopeDebounceTick; scopeDebounce.Start();
    }
    private void ScopeDebounceTick(object? sender, EventArgs e) { scopeDebounce?.Stop(); RequestPreview(); }
    private void RequestPreview()
    {
        if (SelectedPeer is not { } peer || host is null) return;
        var spec = BuildSelection();
        if (!peer.ScopeV2)
        {
            ScopeSummary = spec.DateEnabled ? $"日期：{spec.From ?? "不限"} 至 {spec.To ?? "不限"}。数量由手机确认。" : "全部可访问媒体；数量由手机确认。";
            NotifySelection(); return;
        }
        if (spec.Mode == "advanced" && Albums.Count == 0)
        {
            var key = peer.ArchiveId ?? peer.PairingId;
            if (savedScopes.TryGetValue(key, out var saved)) spec = saved;
        }
        if (spec.Validate() is { } error) { ScopeSummary = error; return; }
        previewPending = true;
        host.RequestCatalog(peer.PairingId, spec);
        ScopeSummary = "正在等待手机计算范围…";
        NotifySelection();
    }
    private void UpdateCatalog()
    {
        if (SelectedPeer is null) return;
        if (host?.TransferStatus(SelectedPeer.PairingId) is { } phase) SelectedDeviceStatus = phase;
        var report = host?.Catalog(SelectedPeer.PairingId);
        if (scopeDebounce?.IsEnabled == true) { NotifySelection(); return; }
        if (report is null || ReferenceEquals(report, lastCatalogReport)) { NotifySelection(); return; }
        lastCatalogReport = report;
        previewPending = false;
        var key = SelectedPeer.ArchiveId ?? SelectedPeer.PairingId;
        var spec = savedScopes.GetValueOrDefault(key) ?? BuildSelection();
        loadingScope = true;
        try
        {
            Albums.Clear();
            foreach (var album in report.Albums)
            {
                Albums.Add(new WirelessAlbumRow
                {
                    Id = album.Id, Name = album.Name, Detail = $"{album.Count:N0} 项", IsDefault = album.IsDefault,
                    IsChecked = spec.Mode == "default" ? album.IsDefault : spec.Mode == "advanced" ? spec.Albums.Contains(album.Id) : true,
                    Edited = () => { if (!loadingScope) { ScopeModeIndex = 3; ScopeEdited(); } },
                });
            }
        }
        finally { loadingScope = false; }
        string label = $"{App.GetString("PairMode" + spec.Mode, spec.Mode)} · {(spec.Photos && spec.Videos ? "照片与视频" : spec.Photos ? "照片（含实况）" : "视频")}";
        if (spec.DateEnabled) label += $" · {spec.From ?? "不限"} 至 {spec.To ?? "不限"}";
        label += $" · {Albums.Count(album => album.IsChecked)} 个相册 · 小图过滤 {(spec.IgnoreSmallImages ? spec.MinImageKb + " KB" : "关闭")} · 缓存过滤 {(spec.FilterCache ? "开启" : "关闭")}";
        ScopeSummary = report.Error ?? $"{label}\n{(report.Count == 0 ? "没有符合条件的媒体" : $"预计 {report.Count:N0} 项 · {report.Bytes / 1048576.0:F1} MB")}" +
            (report.UnknownSize > 0 ? $" · {report.UnknownSize} 项大小未知，予以保留" : "") +
            (report.UnknownDate > 0 ? $" · {report.UnknownDate} 项无日期，已排除" : "") + $" · 时区 {report.TimeZone}";
        NotifySelection();
    }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(CanUseScopeV2)); OnPropertyChanged(nameof(CapabilityHint));
        OnPropertyChanged(nameof(CanStartSelected)); OnPropertyChanged(nameof(ArchiveDirectory)); OnPropertyChanged(nameof(BackupSubtitle));
    }
    private static Dictionary<string, LinkScopeSpec> LoadScopes()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, LinkScopeSpec>>(File.ReadAllText(ScopeFile)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    [RelayCommand] private void RefreshPeers() { Refresh(); RequestPreview(); }
    [RelayCommand] private void StartSelected() { if (CanStartSelected) AskPeer(SelectedPeer?.PairingId); }
    [RelayCommand] private void OpenConnectionInfo() { IsConnectionInfoOpen = true; if (!Enabled) Enabled = true; if (!ShowPairing) TogglePairing(); }
    [RelayCommand] private void RenewPairing() { ShowPairing = false; TogglePairing(); }
    [RelayCommand] private void CloseConnectionInfo() { IsConnectionInfoOpen = false; ShowPairing = false; }
    [RelayCommand] private void OpenWirelessScope() { IsScopeOpen = true; RequestPreview(); }
    [RelayCommand] private void CloseWirelessScope() { IsScopeOpen = false; }
    [RelayCommand] private void OpenWirelessLog() => IsLogOpen = true;
    [RelayCommand] private void CloseWirelessLog() => IsLogOpen = false;
    [RelayCommand] private void SelectAlbums(string? selected)
    {
        loadingScope = true;
        try { ScopeModeIndex = 3; foreach (var album in Albums) album.IsChecked = selected == "all"; }
        finally { loadingScope = false; }
        ScopeEdited();
    }
    [RelayCommand] private void DatePreset(string? preset)
    {
        DateEnabled = true;
        // Calendar presets use the connected phone's time zone.
        var report = SelectedPeer is null ? null : host?.Catalog(SelectedPeer.PairingId);
        DateTime today;
        try { today = report is null ? DateTime.Today : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, report.TimeZone).Date; }
        catch (TimeZoneNotFoundException) { today = DateTime.Today; }
        catch (InvalidTimeZoneException) { today = DateTime.Today; }
        ScopeTo = today;
        ScopeFrom = preset switch { "30" => today.AddDays(-30), "90" => today.AddDays(-90), "year" => today.AddYears(-1), "this" => new DateTime(today.Year, 1, 1), _ => null };
        if (preset == "clear") { ScopeFrom = null; ScopeTo = null; DateEnabled = false; }
    }
}
