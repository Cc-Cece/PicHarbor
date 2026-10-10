using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PicHarbor.Core.GooglePhotos;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public partial class GooglePhotosSyncViewModel : ObservableObject
{
    private CancellationTokenSource? cts;

    [ObservableProperty]
    private string archivePath = "";

    [ObservableProperty]
    private GooglePhotosAuthMethod authMethod = GooglePhotosAuthMethod.OAuthCookie;

    public bool IsAuthOAuthCookie
    {
        get => AuthMethod == GooglePhotosAuthMethod.OAuthCookie;
        set
        {
            if (value && AuthMethod != GooglePhotosAuthMethod.OAuthCookie)
            {
                AuthMethod = GooglePhotosAuthMethod.OAuthCookie;
                NotifyAuthMethodProperties();
                UpdateConnectionStatusDisplay();
                SaveConfig();
            }
        }
    }

    public bool IsAuthAndroidData
    {
        get => AuthMethod == GooglePhotosAuthMethod.AndroidAuthData;
        set
        {
            if (value && AuthMethod != GooglePhotosAuthMethod.AndroidAuthData)
            {
                AuthMethod = GooglePhotosAuthMethod.AndroidAuthData;
                NotifyAuthMethodProperties();
                UpdateConnectionStatusDisplay();
                SaveConfig();
            }
        }
    }

    private void NotifyAuthMethodProperties()
    {
        OnPropertyChanged(nameof(IsAuthOAuthCookie));
        OnPropertyChanged(nameof(IsAuthAndroidData));
    }

    [ObservableProperty]
    private string oAuthCookie = "";

    [ObservableProperty]
    private string authData = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyDisplayText))]
    private string proxy = "";

    public string ProxyDisplayText => !string.IsNullOrWhiteSpace(Proxy) ? $"{Proxy} (已配置)" : "直连 (未配置代理)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotTestingProxy))]
    private bool isTestingProxy = false;

    public bool IsNotTestingProxy => !IsTestingProxy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxyStatusText))]
    private string proxyStatusText = "";

    public bool HasProxyStatusText => !string.IsNullOrWhiteSpace(ProxyStatusText);

    [ObservableProperty]
    private string proxyStatusColor = "#93C5FD";

    [ObservableProperty]
    private string accountEmail = "未登录 / 未设置凭据";

    [ObservableProperty]
    private string connectionStatusText = "未配置凭据";

    [ObservableProperty]
    private bool isConnected = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotTestingConnection))]
    private bool isTestingConnection = false;

    public bool IsNotTestingConnection => !IsTestingConnection;

    [ObservableProperty]
    private string testStatusText = "";

    // --- Scope Properties ---
    [ObservableProperty]
    private GooglePhotosScopeMode scopeMode = GooglePhotosScopeMode.All;

    public bool IsScopeAll
    {
        get => ScopeMode == GooglePhotosScopeMode.All;
        set
        {
            if (value && ScopeMode != GooglePhotosScopeMode.All)
            {
                ScopeMode = GooglePhotosScopeMode.All;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    public bool IsScopeDateRange
    {
        get => ScopeMode == GooglePhotosScopeMode.DateRange;
        set
        {
            if (value && ScopeMode != GooglePhotosScopeMode.DateRange)
            {
                ScopeMode = GooglePhotosScopeMode.DateRange;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    public bool IsScopeSubfolder
    {
        get => ScopeMode == GooglePhotosScopeMode.Subfolder;
        set
        {
            if (value && ScopeMode != GooglePhotosScopeMode.Subfolder)
            {
                ScopeMode = GooglePhotosScopeMode.Subfolder;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    public bool IsScopeManualSelection
    {
        get => ScopeMode == GooglePhotosScopeMode.ManualSelection;
        set
        {
            if (value && ScopeMode != GooglePhotosScopeMode.ManualSelection)
            {
                ScopeMode = GooglePhotosScopeMode.ManualSelection;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    public bool IsScopeCustomTarget
    {
        get => ScopeMode == GooglePhotosScopeMode.CustomTarget;
        set
        {
            if (value && ScopeMode != GooglePhotosScopeMode.CustomTarget)
            {
                ScopeMode = GooglePhotosScopeMode.CustomTarget;
                NotifyScopeProperties();
                RecalculateScopeSummary();
            }
        }
    }

    private void NotifyScopeProperties()
    {
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeDateRange));
        OnPropertyChanged(nameof(IsScopeSubfolder));
        OnPropertyChanged(nameof(IsScopeManualSelection));
        OnPropertyChanged(nameof(IsScopeCustomTarget));
    }

    [ObservableProperty]
    private DateTime? scopeDateFrom;

    [ObservableProperty]
    private DateTime? scopeDateTo;

    [ObservableProperty]
    private string scopeDateFromText = "";

    [ObservableProperty]
    private string scopeDateToText = "";

    partial void OnScopeDateFromChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (scopeDateFromText != formatted) scopeDateFromText = formatted;
        RecalculateScopeSummary();
    }

    partial void OnScopeDateToChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (scopeDateToText != formatted) scopeDateToText = formatted;
        RecalculateScopeSummary();
    }

    partial void OnScopeDateFromTextChanged(string value)
    {
        if (DateTime.TryParse(value, out var dt))
        {
            if (scopeDateFrom != dt.Date) scopeDateFrom = dt.Date;
        }
        else if (string.IsNullOrWhiteSpace(value) && scopeDateFrom != null)
        {
            scopeDateFrom = null;
        }
        RecalculateScopeSummary();
    }

    partial void OnScopeDateToTextChanged(string value)
    {
        if (DateTime.TryParse(value, out var dt))
        {
            if (scopeDateTo != dt.Date) scopeDateTo = dt.Date;
        }
        else if (string.IsNullOrWhiteSpace(value) && scopeDateTo != null)
        {
            scopeDateTo = null;
        }
        RecalculateScopeSummary();
    }

    [RelayCommand]
    private void SetDatePresetLast30Days()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = DateTime.Today.AddDays(-30);
    }

    [RelayCommand]
    private void SetDatePresetLast90Days()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = DateTime.Today.AddDays(-90);
    }

    [RelayCommand]
    private void SetDatePresetLast1Year()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = DateTime.Today.AddYears(-1);
    }

    [RelayCommand]
    private void SetDatePresetThisYear()
    {
        ScopeDateTo = DateTime.Today;
        ScopeDateFrom = new DateTime(DateTime.Today.Year, 1, 1);
    }

    [RelayCommand]
    private void ClearDateRange()
    {
        ScopeDateFrom = null;
        ScopeDateTo = null;
        ScopeDateFromText = "";
        ScopeDateToText = "";
    }

    [ObservableProperty]
    private string customTargetPath = "";

    partial void OnCustomTargetPathChanged(string value)
    {
        RecalculateScopeSummary();
    }

    [ObservableProperty]
    private string scopeSummaryText = "📊 当前筛选结果: 预计上传 0 项，约 0 B";

    [ObservableProperty]
    private int manualSelectionCount = 0;

    [ObservableProperty]
    private string manualSelectionCountText = "已选择 0 项媒体";

    [ObservableProperty]
    private string manualSelectionModalBtnText = "👁️ 查看/编辑上传清单 (0)";

    public bool HasManualSelections => ManualSelectionCount > 0;
    public bool HasNoManualSelections => ManualSelectionCount == 0;

    [ObservableProperty]
    private bool isManualModalOpen = false;

    public ObservableCollection<SubfolderOptionViewModel> Subfolders { get; } = new();
    public ObservableCollection<ManualSelectedItemViewModel> ManualSelectedItems { get; } = new();

    // --- Album Mode Properties ---
    [ObservableProperty]
    private GooglePhotosAlbumMode albumMode = GooglePhotosAlbumMode.None;

    public bool IsAlbumNone
    {
        get => AlbumMode == GooglePhotosAlbumMode.None;
        set
        {
            if (value && AlbumMode != GooglePhotosAlbumMode.None)
            {
                AlbumMode = GooglePhotosAlbumMode.None;
                NotifyAlbumProperties();
                SaveConfig();
            }
        }
    }

    public bool IsAlbumAuto
    {
        get => AlbumMode == GooglePhotosAlbumMode.AutoParentDir;
        set
        {
            if (value && AlbumMode != GooglePhotosAlbumMode.AutoParentDir)
            {
                AlbumMode = GooglePhotosAlbumMode.AutoParentDir;
                NotifyAlbumProperties();
                SaveConfig();
            }
        }
    }

    public bool IsAlbumCustom
    {
        get => AlbumMode == GooglePhotosAlbumMode.CustomName;
        set
        {
            if (value && AlbumMode != GooglePhotosAlbumMode.CustomName)
            {
                AlbumMode = GooglePhotosAlbumMode.CustomName;
                NotifyAlbumProperties();
                SaveConfig();
            }
        }
    }

    public bool IsAlbumId
    {
        get => AlbumMode == GooglePhotosAlbumMode.AlbumId;
        set
        {
            if (value && AlbumMode != GooglePhotosAlbumMode.AlbumId)
            {
                AlbumMode = GooglePhotosAlbumMode.AlbumId;
                NotifyAlbumProperties();
                SaveConfig();
            }
        }
    }

    private void NotifyAlbumProperties()
    {
        OnPropertyChanged(nameof(IsAlbumNone));
        OnPropertyChanged(nameof(IsAlbumAuto));
        OnPropertyChanged(nameof(IsAlbumCustom));
        OnPropertyChanged(nameof(IsAlbumId));
    }

    [ObservableProperty]
    private string customAlbumName = "";

    [ObservableProperty]
    private string albumId = "";

    // --- Advanced Options ---
    [ObservableProperty]
    private int threads = 3;

    [ObservableProperty]
    private bool unlimitedQuality = true;

    [ObservableProperty]
    private bool storageSaver = false;

    [ObservableProperty]
    private bool skipExistingFilenames = true;

    [ObservableProperty]
    private string pythonPath = "python";

    [ObservableProperty]
    private string gpmcPath = @"C:\Users\Kanbara\Code\vscode\gpmc";

    [ObservableProperty]
    private int timeoutSeconds = 60;

    [ObservableProperty]
    private int autoRetryAttempts = 3;

    [ObservableProperty]
    private double retryDelaySeconds = 2.0;

    [ObservableProperty]
    private bool isAdvancedExpanded = false;

    // --- Live Transfer State ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSyncing))]
    [NotifyPropertyChangedFor(nameof(CanRetryFailed))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(IsFreshIdle))]
    private bool isSyncing = false;

    public bool IsNotSyncing => !IsSyncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(IsFreshIdle))]
    [NotifyPropertyChangedFor(nameof(ShowPauseBanner))]
    private bool isPaused = false;

    private bool isPauseRequested = false;

    public bool CanPause => IsSyncing;
    public bool CanResume => IsPaused && !IsSyncing;
    public bool IsFreshIdle => !IsSyncing && !IsPaused;
    public bool ShowPauseBanner => IsPaused;

    [ObservableProperty]
    private string pausedBannerTitle = "";

    [ObservableProperty]
    private string pausedBannerSubtitle = "";

    private List<string> activePlanCandidateFiles = [];
    private int activePlanTotalFiles = 0;
    private long activePlanTotalBytes = 0;
    private long activePlanUploadedBytes = 0;

    private void UpdatePausedBanner()
    {
        PausedBannerTitle = App.GetString("GooglePhotosPausedBannerTitle", "⏸️ 当前方案已暂停");
        int totalProcessed = UploadedCount + SkippedCount;
        int totalFiles = activePlanTotalFiles > 0 ? activePlanTotalFiles : (totalProcessed + FailedCount);
        PausedBannerSubtitle = string.Format(
            App.GetString("GooglePhotosPausedBannerSubtitle", "已完成 {0}/{1} 项 (失败 {2} 项)。您可以点击【继续上传】恢复剩余进度，或点击【重试失败文件】重新上传失败项。"),
            totalProcessed,
            totalFiles,
            FailedCount);
    }

    private void TerminateCurrentPlan()
    {
        IsPaused = false;
        activePlanCandidateFiles.Clear();
        activePlanTotalFiles = 0;
        activePlanTotalBytes = 0;
        activePlanUploadedBytes = 0;
        uploadedDetails.Clear();
        skippedDetails.Clear();
        failedDetails.Clear();
        DetailItems.Clear();
        UploadedCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        ProgressValue = 0;
        TransferredSizeText = "0 B / 0 B";
        SpeedText = "--";
        EtaText = "--";
        CurrentFile = "--";
        CurrentPhase = "--";
        CurrentFilePercent = 0;
    }

    [ObservableProperty]
    private double progressValue = 0;

    [ObservableProperty]
    private string progressText = "准备就绪";

    [ObservableProperty]
    private int uploadedCount = 0;

    [ObservableProperty]
    private int skippedCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetryFailed))]
    private int failedCount = 0;

    public bool CanRetryFailed => FailedCount > 0 && IsNotSyncing;

    [ObservableProperty]
    private bool isDetailModalOpen = false;

    [ObservableProperty]
    private string detailModalTitle = "";

    [ObservableProperty]
    private ObservableCollection<TransferItemDetail> detailItems = new();

    private readonly List<TransferItemDetail> uploadedDetails = new();
    private readonly List<TransferItemDetail> skippedDetails = new();
    private readonly List<TransferItemDetail> failedDetails = new();

    private enum GooglePhotosDetailViewType { None, Uploaded, Skipped, Failed }
    private GooglePhotosDetailViewType currentDetailView = GooglePhotosDetailViewType.None;

    [RelayCommand]
    private void ShowUploadedDetails()
    {
        currentDetailView = GooglePhotosDetailViewType.Uploaded;
        DetailModalTitle = App.GetString("GooglePhotosModalTitleUploaded", "☁️ Google 相册 已上传文件明细 (Uploaded)");
        DetailItems = new ObservableCollection<TransferItemDetail>(uploadedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowSkippedDetails()
    {
        currentDetailView = GooglePhotosDetailViewType.Skipped;
        DetailModalTitle = App.GetString("GooglePhotosModalTitleSkipped", "☁️ Google 相册 已跳过文件明细 (Skipped - 云端已存在)");
        DetailItems = new ObservableCollection<TransferItemDetail>(skippedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowFailedDetails()
    {
        currentDetailView = GooglePhotosDetailViewType.Failed;
        DetailModalTitle = App.GetString("GooglePhotosModalTitleFailed", "⚠️ Google 相册 失败文件明细 (Failed)");
        DetailItems = new ObservableCollection<TransferItemDetail>(failedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void CloseDetailModal()
    {
        currentDetailView = GooglePhotosDetailViewType.None;
        IsDetailModalOpen = false;
    }

    [ObservableProperty]
    private string transferredSizeText = "0 B / 0 B";

    [ObservableProperty]
    private string speedText = "--";

    [ObservableProperty]
    private int remainingFilesCount = 0;

    [ObservableProperty]
    private string etaText = "--";

    [ObservableProperty]
    private string currentFile = "--";

    [ObservableProperty]
    private string currentPhase = "--";

    [ObservableProperty]
    private double currentFilePercent = 0;

    public ObservableCollection<string> LogEntries { get; } = new()
    {
        "[INFO] Google 相册 (gpmc) 增量上传模块已准备就绪。",
        "[INFO] 请在“配置凭据”中填入 auth_data 并验证连接，即可将媒体增量同步至 Google 相册。"
    };

    public Action? NavigateToSearchAction { get; set; }
    public Action? NavigateToSettingsAction { get; set; }

    public GooglePhotosSyncViewModel()
    {
        LoadConfig();
    }

    public void OnLanguageChanged()
    {
        RecalculateScopeSummary();
        UpdateConnectionStatusDisplay();
        if (IsPaused)
        {
            UpdatePausedBanner();
        }
    }

    private void LoadConfig()
    {
        var config = AppSettings.Load();
        AuthMethod = (GooglePhotosAuthMethod)config.GooglePhotosAuthMethod;
        OAuthCookie = config.GooglePhotosOAuthCookie;
        AuthData = config.GooglePhotosAuthData;
        Proxy = config.GooglePhotosProxy;
        if (!string.IsNullOrWhiteSpace(config.GooglePhotosAccountEmail))
        {
            AccountEmail = config.GooglePhotosAccountEmail;
        }
        AlbumMode = (GooglePhotosAlbumMode)config.GooglePhotosAlbumMode;
        CustomAlbumName = config.GooglePhotosCustomAlbumName;
        AlbumId = config.GooglePhotosAlbumId;
        Threads = config.GooglePhotosThreads > 0 ? config.GooglePhotosThreads : 3;
        UnlimitedQuality = config.GooglePhotosUnlimitedQuality;
        StorageSaver = config.GooglePhotosStorageSaver;
        SkipExistingFilenames = config.GooglePhotosSkipExistingFilenames;
        PythonPath = !string.IsNullOrWhiteSpace(config.GooglePhotosPythonPath) ? config.GooglePhotosPythonPath : "python";
        GpmcPath = config.GooglePhotosGpmcPath;
        TimeoutSeconds = config.GooglePhotosTimeoutSeconds > 0 ? config.GooglePhotosTimeoutSeconds : 60;
        AutoRetryAttempts = config.GooglePhotosAutoRetryAttempts > 0 ? config.GooglePhotosAutoRetryAttempts : 3;
        RetryDelaySeconds = config.GooglePhotosRetryDelaySeconds > 0 ? config.GooglePhotosRetryDelaySeconds : 2.0;

        NotifyAuthMethodProperties();
        NotifyAlbumProperties();
        if (string.IsNullOrWhiteSpace(config.GooglePhotosAccountEmail))
        {
            TryExtractEmailFromAuthData();
        }
        else
        {
            IsConnected = !string.IsNullOrWhiteSpace(OAuthCookie) || !string.IsNullOrWhiteSpace(AuthData);
            UpdateConnectionStatusDisplay();
        }
    }

    public void SaveConfig()
    {
        var config = AppSettings.Load();
        config.GooglePhotosAuthMethod = (int)AuthMethod;
        config.GooglePhotosOAuthCookie = OAuthCookie;
        config.GooglePhotosAccountEmail = AccountEmail;
        config.GooglePhotosAuthData = AuthData;
        config.GooglePhotosProxy = Proxy;
        config.GooglePhotosAlbumMode = (int)AlbumMode;
        config.GooglePhotosCustomAlbumName = CustomAlbumName;
        config.GooglePhotosAlbumId = AlbumId;
        config.GooglePhotosThreads = Threads;
        config.GooglePhotosUnlimitedQuality = UnlimitedQuality;
        config.GooglePhotosStorageSaver = StorageSaver;
        config.GooglePhotosSkipExistingFilenames = SkipExistingFilenames;
        config.GooglePhotosPythonPath = PythonPath;
        config.GooglePhotosGpmcPath = GpmcPath;
        config.GooglePhotosTimeoutSeconds = TimeoutSeconds;
        config.GooglePhotosAutoRetryAttempts = AutoRetryAttempts;
        config.GooglePhotosRetryDelaySeconds = RetryDelaySeconds;
        AppSettings.Save(config);
    }

    partial void OnOAuthCookieChanged(string? oldValue, string newValue)
    {
        if (!string.IsNullOrWhiteSpace(oldValue) && !string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            AuthData = string.Empty;
            IsConnected = false;
        }
        SaveConfig();
        UpdateConnectionStatusDisplay();
    }

    partial void OnAuthDataChanged(string value)
    {
        SaveConfig();
        TryExtractEmailFromAuthData();
    }

    partial void OnAccountEmailChanged(string value) => SaveConfig();
    partial void OnProxyChanged(string value) => SaveConfig();
    partial void OnCustomAlbumNameChanged(string value) => SaveConfig();
    partial void OnAlbumIdChanged(string value) => SaveConfig();
    partial void OnThreadsChanged(int value) => SaveConfig();
    partial void OnUnlimitedQualityChanged(bool value) => SaveConfig();
    partial void OnStorageSaverChanged(bool value) => SaveConfig();
    partial void OnSkipExistingFilenamesChanged(bool value) => SaveConfig();
    partial void OnPythonPathChanged(string value) => SaveConfig();
    partial void OnGpmcPathChanged(string value) => SaveConfig();
    partial void OnTimeoutSecondsChanged(int value) => SaveConfig();
    partial void OnAutoRetryAttemptsChanged(int value) => SaveConfig();
    partial void OnRetryDelaySecondsChanged(double value) => SaveConfig();

    partial void OnArchivePathChanged(string value)
    {
        LoadSubfolders();
        LoadManualSelectionsFromDb();
        RecalculateScopeSummary();
    }

    private void TryExtractEmailFromAuthData()
    {
        if (string.IsNullOrWhiteSpace(AuthData))
        {
            if (string.IsNullOrWhiteSpace(OAuthCookie))
            {
                AccountEmail = "未配置凭据";
                IsConnected = false;
            }
            UpdateConnectionStatusDisplay();
            return;
        }

        try
        {
            foreach (var part in AuthData.Split('&'))
            {
                if (part.StartsWith("Email=", StringComparison.OrdinalIgnoreCase))
                {
                    string email = part.Substring("Email=".Length).Replace("%40", "@");
                    AccountEmail = email;
                    IsConnected = true;
                    UpdateConnectionStatusDisplay();
                    return;
                }
            }
        }
        catch { }

        AccountEmail = "已配置凭据 (未检测到明文邮箱)";
        IsConnected = true;
        UpdateConnectionStatusDisplay();
    }

    private void UpdateConnectionStatusDisplay()
    {
        bool hasCredential = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
            ? (!string.IsNullOrWhiteSpace(OAuthCookie) || !string.IsNullOrWhiteSpace(AuthData))
            : !string.IsNullOrWhiteSpace(AuthData);

        if (IsConnected)
        {
            ConnectionStatusText = $"● 账号已就绪: {AccountEmail}";
        }
        else if (hasCredential)
        {
            ConnectionStatusText = "● 凭据已输入 (待测试验证)";
        }
        else
        {
            ConnectionStatusText = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                ? "未配置 Cookie (请填入 oauth_token)"
                : "未配置凭据 (请填入 auth_data)";
        }
    }

    [RelayCommand]
    private async Task LoginViaWebViewAsync()
    {
        try
        {
            var loginWindow = new GoogleLoginWindow(Proxy)
            {
                Owner = Application.Current?.MainWindow
            };

            bool? result = loginWindow.ShowDialog();
            if (result == true && !string.IsNullOrWhiteSpace(loginWindow.ExtractedToken))
            {
                OAuthCookie = loginWindow.ExtractedToken;
                SaveConfig();
                LogEntries.Add("[INFO] 已通过内置浏览器成功捕获 OAuth Token Cookie，正在自动测试连接...");
                await TestConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[ERROR] 打开登录窗口失败: {ex.Message}");
            MessageBox.Show($"打开登录窗口失败:\n{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenEmbeddedSetupUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://accounts.google.com/EmbeddedSetup",
                UseShellExecute = true
            });
            LogEntries.Add("[INFO] 已在默认浏览器打开 Google Embedded Setup (https://accounts.google.com/EmbeddedSetup)。");
        }
        catch (Exception ex)
        {
            LogEntries.Add($"[ERROR] 打开浏览器失败: {ex.Message}");
            MessageBox.Show("无法自动打开浏览器，请手动访问:\nhttps://accounts.google.com/EmbeddedSetup", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (IsTestingProxy) return;

        IsTestingProxy = true;
        ProxyStatusColor = "#93C5FD";
        ProxyStatusText = App.GetString("GooglePhotosTestingProxy", "⏳ 正在测试网络代理连通性并探测 Google 服务器...");
        LogEntries.Add($"[INFO] 正在测试代理连通性: {(string.IsNullOrWhiteSpace(Proxy) ? "直连" : Proxy)}");

        try
        {
            var result = await GooglePhotosSyncEngine.TestProxyAsync(Proxy);
            if (result.Success)
            {
                ProxyStatusColor = "#10B981";
                ProxyStatusText = $"✅ {result.Message}";
                LogEntries.Add($"[INFO] {result.Message}");

                // If user entered only port (e.g. 7890), auto-normalize to http://127.0.0.1:7890 for clarity
                if (!string.IsNullOrWhiteSpace(result.NormalizedProxy) &&
                    !string.IsNullOrWhiteSpace(Proxy) &&
                    Proxy != result.NormalizedProxy &&
                    int.TryParse(Proxy.Trim(), out _))
                {
                    Proxy = result.NormalizedProxy;
                    SaveConfig();
                }
            }
            else
            {
                ProxyStatusColor = "#EF4444";
                ProxyStatusText = $"❌ {result.Message}";
                LogEntries.Add($"[ERROR] {result.Message}");
            }
        }
        catch (Exception ex)
        {
            ProxyStatusColor = "#EF4444";
            ProxyStatusText = $"❌ 代理测试异常: {ex.Message}";
            LogEntries.Add($"[ERROR] 代理测试异常: {ex.Message}");
        }
        finally
        {
            IsTestingProxy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        string credential = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
            ? (!string.IsNullOrWhiteSpace(OAuthCookie) ? OAuthCookie : AuthData)
            : AuthData;
        if (string.IsNullOrWhiteSpace(credential))
        {
            TestStatusText = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                ? "❌ 请先填入从浏览器获取的 oauth_token Cookie。"
                : "❌ 请先在凭据输入框填入 auth_data。";
            return;
        }

        IsTestingConnection = true;
        TestStatusText = "⏳ 正在连接 Google API 验证凭据与网络...";

        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = AuthMethod,
            OAuthTokenCookie = OAuthCookie,
            AuthData = AuthData,
            Proxy = Proxy,
            TimeoutSeconds = TimeoutSeconds,
            PythonPath = PythonPath,
            GpmcPath = GpmcPath
        };

        try
        {
            var result = await GooglePhotosSyncEngine.TestAuthAsync(config);
            if (result.Success)
            {
                IsConnected = true;
                if (!string.IsNullOrWhiteSpace(result.Email))
                {
                    AccountEmail = result.Email;
                }
                if (!string.IsNullOrWhiteSpace(result.ExchangedAuthData))
                {
                    AuthData = result.ExchangedAuthData;
                }
                SaveConfig();
                TestStatusText = $"✅ 验证成功! 账号: {AccountEmail}";
                UpdateConnectionStatusDisplay();
                LogEntries.Add($"[INFO] 凭据测试成功，Google 账号: {AccountEmail}");
            }
            else
            {
                TestStatusText = $"❌ 验证失败: {result.ErrorMessage}";
                LogEntries.Add($"[ERROR] 凭据测试失败: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            TestStatusText = $"❌ 验证出错: {ex.Message}";
            LogEntries.Add($"[ERROR] 验证出错: {ex.Message}");
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        OAuthCookie = string.Empty;
        AuthData = string.Empty;
        AccountEmail = "未登录 / 未设置凭据";
        IsConnected = false;
        ConnectionStatusText = "未配置凭据";
        TestStatusText = string.Empty;

        var config = AppSettings.Load();
        config.GooglePhotosOAuthCookie = string.Empty;
        config.GooglePhotosAuthData = string.Empty;
        config.GooglePhotosAccountEmail = string.Empty;
        AppSettings.Save(config);

        LogEntries.Add("[INFO] 已成功注销 Google 账号凭据。");
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        NavigateToSettingsAction?.Invoke();
    }

    [RelayCommand]
    private void BrowseCustomTarget()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要上传的文件夹"
        };
        if (dialog.ShowDialog() == true)
        {
            CustomTargetPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowsePythonPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Python 解释器 (python.exe)",
            Filter = "Python (python*.exe)|python*.exe|All Executables (*.exe)|*.exe|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            PythonPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void BrowseGpmcPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 gpmc 仓库根目录"
        };
        if (dialog.ShowDialog() == true)
        {
            GpmcPath = dialog.FolderName;
        }
    }

    public void LoadSubfolders()
    {
        Subfolders.Clear();
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath)) return;

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = journal.ReadManifest();
            var folderGroups = manifest
                .Select(m => Path.GetDirectoryName(m.DestPath.Replace('\\', '/'))?.Replace('\\', '/'))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d)
                .ToList();

            foreach (var folder in folderGroups)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                var opt = new SubfolderOptionViewModel
                {
                    FolderName = folder,
                    RelativePath = folder,
                    IsChecked = true,
                    OnCheckedChanged = RecalculateScopeSummary
                };
                Subfolders.Add(opt);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load subfolders: {ex.Message}");
        }
    }

    public void LoadManualSelectionsFromDb()
    {
        ManualSelectedItems.Clear();
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            ManualSelectionCount = 0;
            ManualSelectionCountText = "已选择 0 项媒体";
            ManualSelectionModalBtnText = string.Format(App.GetString("GooglePhotosViewEditListBtn", "👁️ 查看/编辑上传清单 ({0})"), 0);
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manualPaths = journal.GetGooglePhotosManualSelections();
            var manifest = journal.ReadManifest().ToDictionary(m => m.DestPath, StringComparer.OrdinalIgnoreCase);

            foreach (var path in manualPaths)
            {
                if (manifest.TryGetValue(path, out var entry))
                {
                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = entry.DestPath,
                        FullPath = Path.Combine(ArchivePath, entry.DestPath),
                        CapturedAt = ParseCaptureDate(entry)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A",
                        SizeBytes = entry.SizeBytes,
                        SizeText = FormatSize(entry.SizeBytes)
                    });
                }
                else
                {
                    string fullPath = Path.Combine(ArchivePath, path);
                    long size = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                    ManualSelectedItems.Add(new ManualSelectedItemViewModel
                    {
                        RelativePath = path,
                        FullPath = fullPath,
                        CapturedAt = "N/A",
                        SizeBytes = size,
                        SizeText = FormatSize(size)
                    });
                }
            }

            ManualSelectionCount = ManualSelectedItems.Count;
            ManualSelectionCountText = $"已选择 {ManualSelectionCount} 项媒体";
            ManualSelectionModalBtnText = string.Format(App.GetString("GooglePhotosViewEditListBtn", "👁️ 查看/编辑上传清单 ({0})"), ManualSelectionCount);
            OnPropertyChanged(nameof(HasManualSelections));
            OnPropertyChanged(nameof(HasNoManualSelections));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load manual selections: {ex.Message}");
        }
    }

    public HashSet<string> GetManualSelectionPathsSet()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            return journal.GetGooglePhotosManualSelections();
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void RecalculateScopeSummary()
    {
        if (ScopeMode == GooglePhotosScopeMode.CustomTarget)
        {
            if (string.IsNullOrWhiteSpace(CustomTargetPath))
            {
                ScopeSummaryText = "📊 请选择外部目录或文件路径";
            }
            else if (Directory.Exists(CustomTargetPath))
            {
                var files = Directory.GetFiles(CustomTargetPath, "*.*", SearchOption.AllDirectories);
                long size = files.Sum(f => new FileInfo(f).Length);
                ScopeSummaryText = $"📊 外部目录: 共 {files.Length:N0} 个文件，约 {FormatSize(size)}";
            }
            else if (File.Exists(CustomTargetPath))
            {
                long size = new FileInfo(CustomTargetPath).Length;
                ScopeSummaryText = $"📊 外部单文件: 1 个文件，约 {FormatSize(size)}";
            }
            else
            {
                ScopeSummaryText = "❌ 指定的路径不存在";
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath))
        {
            ScopeSummaryText = "📊 归档路径无效";
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(ArchivePath);
            var manifest = journal.ReadManifest();
            var checkedFolders = Subfolders.Where(s => s.IsChecked).Select(s => s.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manualPaths = journal.GetGooglePhotosManualSelections();

            var filtered = manifest.Where(item =>
            {
                if (MediaTypeClassifier.Classify(item.DestPath) == MediaType.Other) return false;
                return ScopeMode switch
                {
                    GooglePhotosScopeMode.All => true,
                    GooglePhotosScopeMode.DateRange => MatchesDate(item, ScopeDateFrom, ScopeDateTo?.AddDays(1).AddTicks(-1)),
                    GooglePhotosScopeMode.Subfolder => checkedFolders.Any(f => item.DestPath.StartsWith(f, StringComparison.OrdinalIgnoreCase)),
                    GooglePhotosScopeMode.ManualSelection => manualPaths.Contains(item.DestPath),
                    _ => true
                };
            }).ToList();

            long totalBytes = filtered.Sum(f => f.SizeBytes);
            int photos = filtered.Count(f => IsPhoto(f.DestPath));
            int videos = filtered.Count(f => !IsPhoto(f.DestPath));

            ScopeSummaryText = $"📊 预计上传 {filtered.Count:N0} 项 (照片 {photos:N0}，视频 {videos:N0})，约 {FormatSize(totalBytes)}";
        }
        catch (Exception ex)
        {
            ScopeSummaryText = $"📊 统计出错: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearManualSelections()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath)) return;
        try
        {
            using var journal = TransferJournal.Open(ArchivePath);
            journal.ClearGooglePhotosManualSelections();
            LoadManualSelectionsFromDb();
            RecalculateScopeSummary();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to clear manual selections: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearAllManualSelections() => ClearManualSelections();

    [RelayCommand]
    private void PickFilesFromExplorer()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = App.GetString("TitlePickFilesFromExplorer", "从资源管理器挑选照片..."),
            Filter = App.GetString("FilterMediaFiles", "媒体文件|*.jpg;*.jpeg;*.heic;*.png;*.webp;*.mov;*.mp4;*.dng;*.cr2;*.nef;*.arw|所有文件|*.*")
        };

        if (!string.IsNullOrWhiteSpace(ArchivePath) && Directory.Exists(ArchivePath))
        {
            dialog.InitialDirectory = ArchivePath;
        }

        if (dialog.ShowDialog() == true)
        {
            ProcessPickedFiles(dialog.FileNames);
        }
    }

    [RelayCommand]
    private void RemoveManualItem(ManualSelectedItemViewModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath)) return;
        try
        {
            using var journal = TransferJournal.Open(ArchivePath);
            journal.BatchRemoveGooglePhotosManualSelections(new[] { item.RelativePath });
            journal.CascadeRemoveOrphanedGooglePhotosAutofills();
            LoadManualSelectionsFromDb();
            RecalculateScopeSummary();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove manual item: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenManualModal()
    {
        LoadManualSelectionsFromDb();
        IsManualModalOpen = true;
    }

    [RelayCommand]
    private void CloseManualModal()
    {
        IsManualModalOpen = false;
        RecalculateScopeSummary();
    }

    [RelayCommand]
    private void GoToSearch()
    {
        NavigateToSearchAction?.Invoke();
    }

    public void ProcessPickedFiles(string[] files)
    {
        if (files == null || files.Length == 0 || string.IsNullOrWhiteSpace(ArchivePath) || !Directory.Exists(ArchivePath)) return;
        try
        {
            var relPaths = new List<string>();
            foreach (var f in files)
            {
                if (f.StartsWith(ArchivePath, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = Path.GetRelativePath(ArchivePath, f).Replace('\\', '/');
                    relPaths.Add(rel);
                }
            }

            if (relPaths.Count > 0)
            {
                using var journal = TransferJournal.Open(ArchivePath);
                journal.BatchAddGooglePhotosManualSelections(relPaths);
                LoadManualSelectionsFromDb();
                RecalculateScopeSummary();
                LogEntries.Add($"[INFO] 手动添加了 {relPaths.Count} 个文件到上传清单。");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessPickedFiles error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PasteFilesFromClipboard()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var dropList = Clipboard.GetFileDropList();
            var files = new string[dropList.Count];
            dropList.CopyTo(files, 0);
            ProcessPickedFiles(files);
        }
    }

    [RelayCommand]
    private async Task StartSyncAsync()
    {
        if (IsSyncing) return;

        // Check if there is an existing paused plan
        if (IsPaused)
        {
            int totalDone = UploadedCount + SkippedCount;
            int totalFiles = activePlanTotalFiles > 0 ? activePlanTotalFiles : (totalDone + FailedCount);
            string msg = string.Format(
                App.GetString("GooglePhotosConfirmTerminateAndStartNew",
                    "检测到当前已有未完成的上传方案（已完成 {0}/{1} 项，失败 {2} 项）。\n\n是否结束当前方案并开始新的上传方案？"),
                totalDone,
                totalFiles,
                FailedCount);
            string title = App.GetString("GooglePhotosConfirmTerminateTitle", "方案切换确认");

            if (MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return; // User canceled; retain paused progress!
            }

            TerminateCurrentPlan();
            LogEntries.Add("[INFO] 已结束前一个暂停方案，正在初始化新方案...");
        }

        string credential = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
            ? (!string.IsNullOrWhiteSpace(AuthData) ? AuthData : OAuthCookie)
            : AuthData;
        if (string.IsNullOrWhiteSpace(credential))
        {
            string msg = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                ? "请先配置 Google oauth_token Cookie 凭据后方可上传。"
                : "请先配置 Google auth_data 凭据后方可上传。";
            MessageBox.Show(msg, "凭据未设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = AuthMethod,
            OAuthTokenCookie = OAuthCookie,
            AuthData = AuthData,
            Proxy = Proxy,
            AlbumMode = AlbumMode,
            CustomAlbumName = CustomAlbumName,
            AlbumId = AlbumId,
            Threads = Threads,
            UnlimitedQuality = UnlimitedQuality,
            StorageSaver = StorageSaver,
            SkipExistingFilenames = SkipExistingFilenames,
            PythonPath = PythonPath,
            GpmcPath = GpmcPath,
            TimeoutSeconds = TimeoutSeconds,
            AutoRetryAttempts = AutoRetryAttempts,
            RetryDelaySeconds = RetryDelaySeconds,
            ScopeMode = ScopeMode,
            DateRangeStart = ScopeDateFrom,
            DateRangeEnd = ScopeDateTo?.AddDays(1).AddTicks(-1),
            SelectedSubfolder = Subfolders.FirstOrDefault(s => s.IsChecked)?.RelativePath,
            ManualSelectedPaths = GetManualSelectionPathsSet(),
            CustomTargetPath = CustomTargetPath
        };

        try
        {
            var (candidates, customDir, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(ArchivePath, config);
            activePlanCandidateFiles = candidates;
            activePlanTotalFiles = candidates.Count;
            activePlanTotalBytes = totalBytes;
            activePlanUploadedBytes = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"解析上传目标文件失败: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (activePlanCandidateFiles.Count == 0 && config.ScopeMode != GooglePhotosScopeMode.CustomTarget)
        {
            MessageBox.Show("当前选定范围内未找到符合条件的媒体文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsSyncing = true;
        IsPaused = false;
        isPauseRequested = false;
        ProgressValue = 0;
        UploadedCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        uploadedDetails.Clear();
        skippedDetails.Clear();
        failedDetails.Clear();
        DetailItems.Clear();
        ProgressText = "正在准备上传...";
        SpeedText = "--";
        EtaText = "--";
        CurrentFile = "--";
        CurrentPhase = "启动中...";
        CurrentFilePercent = 0;

        cts = new CancellationTokenSource();

        var progressTarget = new Progress<GooglePhotosProgressSnapshot>(s =>
        {
            ProgressValue = s.OverallPercent;
            UploadedCount = s.UploadedFiles;
            SkippedCount = s.SkippedFiles;
            FailedCount = s.FailedFiles;
            RemainingFilesCount = Math.Max(0, s.TotalFiles - (s.UploadedFiles + s.SkippedFiles + s.FailedFiles));
            activePlanUploadedBytes = s.UploadedBytes;
            TransferredSizeText = $"{FormatSize(s.UploadedBytes)} / {FormatSize(s.TotalBytes)}";
            SpeedText = s.SpeedBytesPerSecond > 0 ? $"{FormatSize((long)s.SpeedBytesPerSecond)}/s" : "--";
            CurrentFile = s.CurrentFile;
            CurrentPhase = s.CurrentPhase;
            CurrentFilePercent = s.CurrentFilePercent;
            ProgressText = $"进度: {s.OverallPercent:F1}% ({s.UploadedFiles + s.SkippedFiles + s.FailedFiles}/{s.TotalFiles})";

            if (s.SpeedBytesPerSecond > 1024 && s.TotalBytes > s.UploadedBytes)
            {
                long remainBytes = s.TotalBytes - s.UploadedBytes;
                double seconds = remainBytes / s.SpeedBytesPerSecond;
                var ts = TimeSpan.FromSeconds(seconds);
                EtaText = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}时{ts.Minutes}分" : $"{ts.Minutes}分{ts.Seconds}秒";
            }
        });

        try
        {
            string effectiveAlbumName = config.AlbumMode switch
            {
                GooglePhotosAlbumMode.CustomName => !string.IsNullOrWhiteSpace(config.CustomAlbumName) ? config.CustomAlbumName : "Google Photos",
                GooglePhotosAlbumMode.AlbumId => !string.IsNullOrWhiteSpace(config.AlbumId) ? config.AlbumId : "Google Photos",
                GooglePhotosAlbumMode.AutoParentDir => "Google Photos (智能相册)",
                _ => "Google Photos"
            };

            var result = await Task.Run(async () =>
            {
                return await GooglePhotosSyncEngine.SyncAsync(
                    ArchivePath,
                    config,
                    progressTarget,
                    msg => Application.Current?.Dispatcher.Invoke(() => LogEntries.Add(msg)),
                    evt => HandleItemProcessed(evt, effectiveAlbumName, isRetry: false),
                    cts.Token
                );
            });

            ProgressText = $"同步完成: 上传 {result.UploadedCount}, 跳过 {result.SkippedCount}, 失败 {result.FailedCount}";
            ProgressValue = 100;
            IsPaused = false;
        }
        catch (OperationCanceledException)
        {
            if (isPauseRequested)
            {
                IsPaused = true;
                CurrentPhase = App.GetString("GooglePhotosPausedPhase", "已暂停");
                UpdatePausedBanner();
                ProgressText = $"已暂停上传 ({UploadedCount + SkippedCount}/{activePlanTotalFiles} 项已处理)";
                LogEntries.Add($"[INFO] 上传已成功暂停。进度已保留 ({UploadedCount} 已上传, {SkippedCount} 已跳过, {FailedCount} 失败)。");
                LogEntries.Add("[INFO] 您可以随时点击【继续上传】恢复剩余进度，或点击【重试失败文件】重新上传失败项。");
            }
            else
            {
                ProgressText = "用户已停止上传。";
                CurrentPhase = "已停止";
                LogEntries.Add("[INFO] 上传已停止。");
            }
        }
        catch (Exception ex)
        {
            ProgressText = $"同步失败: {ex.Message}";
            CurrentPhase = "失败";
            LogEntries.Add($"[ERROR] 同步失败: {ex.Message}");
            MessageBox.Show($"上传出错: {ex.Message}", "Google 相册同步错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSyncing = false;
            isPauseRequested = false;
            cts?.Dispose();
            cts = null;
            if (!IsPaused && CurrentPhase is "启动中..." or "正在准备...")
            {
                CurrentPhase = "已结束";
            }
        }
    }

    [RelayCommand]
    private async Task ResumeSyncAsync()
    {
        if (IsSyncing || !IsPaused) return;

        string credential = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
            ? (!string.IsNullOrWhiteSpace(AuthData) ? AuthData : OAuthCookie)
            : AuthData;
        if (string.IsNullOrWhiteSpace(credential))
        {
            string msg = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                ? "请先配置 Google oauth_token Cookie 凭据后方可上传。"
                : "请先配置 Google auth_data 凭据后方可上传。";
            MessageBox.Show(msg, "凭据未设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var completedSet = uploadedDetails
            .Select(u => u.FullPath)
            .Concat(skippedDetails.Select(s => s.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var remainingFiles = activePlanCandidateFiles
            .Where(f => !completedSet.Contains(f) && File.Exists(f))
            .ToList();

        if (remainingFiles.Count == 0)
        {
            MessageBox.Show("当前方案的所有目标文件均已上传或跳过，无需继续上传。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            IsPaused = false;
            ProgressText = $"全部已完成: 上传 {UploadedCount}, 跳过 {SkippedCount}, 失败 {FailedCount}";
            ProgressValue = 100;
            return;
        }

        IsSyncing = true;
        IsPaused = false;
        isPauseRequested = false;
        CurrentPhase = App.GetString("GooglePhotosResumingPhase", "继续上传中...");
        ProgressText = $"正在继续上传，剩余 {remainingFiles.Count} 项...";
        SpeedText = "--";
        EtaText = "--";
        CurrentFile = "--";
        CurrentFilePercent = 0;

        cts = new CancellationTokenSource();

        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = AuthMethod,
            OAuthTokenCookie = OAuthCookie,
            AuthData = AuthData,
            Proxy = Proxy,
            AlbumMode = AlbumMode,
            CustomAlbumName = CustomAlbumName,
            AlbumId = AlbumId,
            Threads = Threads,
            UnlimitedQuality = UnlimitedQuality,
            StorageSaver = StorageSaver,
            SkipExistingFilenames = SkipExistingFilenames,
            PythonPath = PythonPath,
            GpmcPath = GpmcPath,
            TimeoutSeconds = TimeoutSeconds,
            AutoRetryAttempts = AutoRetryAttempts,
            RetryDelaySeconds = RetryDelaySeconds,
            ExplicitTargetFiles = remainingFiles,
            BaseUploadedFiles = UploadedCount,
            BaseSkippedFiles = SkippedCount,
            BaseFailedFiles = FailedCount,
            BaseUploadedBytes = activePlanUploadedBytes,
            SessionTotalFiles = activePlanTotalFiles,
            SessionTotalBytes = activePlanTotalBytes
        };

        var progressTarget = new Progress<GooglePhotosProgressSnapshot>(s =>
        {
            ProgressValue = s.OverallPercent;
            UploadedCount = s.UploadedFiles;
            SkippedCount = s.SkippedFiles;
            FailedCount = s.FailedFiles;
            RemainingFilesCount = Math.Max(0, s.TotalFiles - (s.UploadedFiles + s.SkippedFiles + s.FailedFiles));
            activePlanUploadedBytes = s.UploadedBytes;
            TransferredSizeText = $"{FormatSize(s.UploadedBytes)} / {FormatSize(s.TotalBytes)}";
            SpeedText = s.SpeedBytesPerSecond > 0 ? $"{FormatSize((long)s.SpeedBytesPerSecond)}/s" : "--";
            CurrentFile = s.CurrentFile;
            CurrentPhase = s.CurrentPhase;
            CurrentFilePercent = s.CurrentFilePercent;
            ProgressText = $"进度: {s.OverallPercent:F1}% ({s.UploadedFiles + s.SkippedFiles + s.FailedFiles}/{s.TotalFiles})";

            if (s.SpeedBytesPerSecond > 1024 && s.TotalBytes > s.UploadedBytes)
            {
                long remainBytes = s.TotalBytes - s.UploadedBytes;
                double seconds = remainBytes / s.SpeedBytesPerSecond;
                var ts = TimeSpan.FromSeconds(seconds);
                EtaText = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}时{ts.Minutes}分" : $"{ts.Minutes}分{ts.Seconds}秒";
            }
        });

        try
        {
            string effectiveAlbumName = config.AlbumMode switch
            {
                GooglePhotosAlbumMode.CustomName => !string.IsNullOrWhiteSpace(config.CustomAlbumName) ? config.CustomAlbumName : "Google Photos",
                GooglePhotosAlbumMode.AlbumId => !string.IsNullOrWhiteSpace(config.AlbumId) ? config.AlbumId : "Google Photos",
                GooglePhotosAlbumMode.AutoParentDir => "Google Photos (智能相册)",
                _ => "Google Photos"
            };

            var result = await Task.Run(async () =>
            {
                return await GooglePhotosSyncEngine.SyncAsync(
                    ArchivePath,
                    config,
                    progressTarget,
                    msg => Application.Current?.Dispatcher.Invoke(() => LogEntries.Add(msg)),
                    evt => HandleItemProcessed(evt, effectiveAlbumName, isRetry: false),
                    cts.Token
                );
            });

            ProgressText = $"同步完成: 上传 {result.UploadedCount}, 跳过 {result.SkippedCount}, 失败 {result.FailedCount}";
            ProgressValue = 100;
            IsPaused = false;
        }
        catch (OperationCanceledException)
        {
            if (isPauseRequested)
            {
                IsPaused = true;
                CurrentPhase = App.GetString("GooglePhotosPausedPhase", "已暂停");
                UpdatePausedBanner();
                ProgressText = $"已暂停上传 ({UploadedCount + SkippedCount}/{activePlanTotalFiles} 项已处理)";
                LogEntries.Add($"[INFO] 上传已成功暂停。进度已保留 ({UploadedCount} 已上传, {SkippedCount} 已跳过, {FailedCount} 失败)。");
                LogEntries.Add("[INFO] 您可以随时点击【继续上传】恢复剩余进度，或点击【重试失败文件】重新上传失败项。");
            }
            else
            {
                ProgressText = "用户已停止上传。";
                CurrentPhase = "已停止";
                LogEntries.Add("[INFO] 上传已停止。");
            }
        }
        catch (Exception ex)
        {
            ProgressText = $"继续上传失败: {ex.Message}";
            CurrentPhase = "失败";
            LogEntries.Add($"[ERROR] 继续上传失败: {ex.Message}");
            MessageBox.Show($"上传出错: {ex.Message}", "Google 相册同步错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSyncing = false;
            isPauseRequested = false;
            cts?.Dispose();
            cts = null;
            if (!IsPaused && CurrentPhase is "继续上传中...")
            {
                CurrentPhase = "已结束";
            }
        }
    }

    [RelayCommand]
    private void PauseSync()
    {
        if (cts != null && !cts.IsCancellationRequested)
        {
            isPauseRequested = true;
            ProgressText = "正在暂停上传...";
            CurrentPhase = App.GetString("GooglePhotosPausingPhase", "正在暂停...");
            LogEntries.Add("[INFO] 正在暂停上传，等待底层任务安全挂起...");
            cts.Cancel();
        }
    }

    [RelayCommand]
    private void TerminateSession()
    {
        if (IsSyncing) return;

        string msg = string.Format(
            App.GetString("GooglePhotosConfirmTerminatePlan",
                "确定要结束当前未完成的方案吗？\n当前方案进度将被清除（已完成 {0} 项，失败 {1} 项）。"),
            UploadedCount + SkippedCount,
            FailedCount);

        string title = App.GetString("GooglePhotosConfirmTerminateTitle", "方案切换确认");

        if (MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            TerminateCurrentPlan();
            LogEntries.Add("[INFO] 用户已手动结束当前方案。");
            ProgressText = "方案已结束。已就绪新任务。";
            CurrentPhase = "--";
        }
    }

    [RelayCommand]
    private async Task RetryFailedItemsAsync()
    {
        if (IsSyncing) return;

        if (failedDetails.Count == 0)
        {
            MessageBox.Show(
                App.GetString("GooglePhotosNoFailedToRetry", "暂无失败文件需要重试。"),
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string credential = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
            ? (!string.IsNullOrWhiteSpace(AuthData) ? AuthData : OAuthCookie)
            : AuthData;
        if (string.IsNullOrWhiteSpace(credential))
        {
            string msg = AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                ? "请先配置 Google oauth_token Cookie 凭据后方可上传。"
                : "请先配置 Google auth_data 凭据后方可上传。";
            MessageBox.Show(msg, "凭据未设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var retryTargetFiles = failedDetails
            .Select(f => f.FullPath)
            .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (retryTargetFiles.Count == 0)
        {
            MessageBox.Show(
                "所有记录的失败文件在本地磁盘上均已不存在，无法重试。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool wasPaused = IsPaused;
        IsSyncing = true;
        isPauseRequested = false;
        ProgressValue = 0;
        ProgressText = $"正在重试 {retryTargetFiles.Count} 个失败文件...";
        SpeedText = "--";
        EtaText = "--";
        CurrentFile = "--";
        CurrentPhase = "准备重试...";
        CurrentFilePercent = 0;

        cts = new CancellationTokenSource();

        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = AuthMethod,
            OAuthTokenCookie = OAuthCookie,
            AuthData = AuthData,
            Proxy = Proxy,
            AlbumMode = AlbumMode,
            CustomAlbumName = CustomAlbumName,
            AlbumId = AlbumId,
            Threads = Threads,
            UnlimitedQuality = UnlimitedQuality,
            StorageSaver = StorageSaver,
            SkipExistingFilenames = false,
            PythonPath = PythonPath,
            GpmcPath = GpmcPath,
            TimeoutSeconds = TimeoutSeconds,
            AutoRetryAttempts = AutoRetryAttempts,
            RetryDelaySeconds = RetryDelaySeconds,
            ExplicitTargetFiles = retryTargetFiles
        };

        var progressTarget = new Progress<GooglePhotosProgressSnapshot>(s =>
        {
            ProgressValue = s.OverallPercent;
            RemainingFilesCount = Math.Max(0, s.TotalFiles - (s.UploadedFiles + s.SkippedFiles + s.FailedFiles));
            TransferredSizeText = $"{FormatSize(s.UploadedBytes)} / {FormatSize(s.TotalBytes)}";
            SpeedText = s.SpeedBytesPerSecond > 0 ? $"{FormatSize((long)s.SpeedBytesPerSecond)}/s" : "--";
            CurrentFile = s.CurrentFile;
            CurrentPhase = s.CurrentPhase;
            CurrentFilePercent = s.CurrentFilePercent;
            ProgressText = $"重试进度: {s.OverallPercent:F1}% ({s.UploadedFiles + s.SkippedFiles + s.FailedFiles}/{s.TotalFiles})";

            if (s.SpeedBytesPerSecond > 1024 && s.TotalBytes > s.UploadedBytes)
            {
                long remainBytes = s.TotalBytes - s.UploadedBytes;
                double seconds = remainBytes / s.SpeedBytesPerSecond;
                var ts = TimeSpan.FromSeconds(seconds);
                EtaText = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}时{ts.Minutes}分" : $"{ts.Minutes}分{ts.Seconds}秒";
            }
        });

        try
        {
            string effectiveAlbumName = config.AlbumMode switch
            {
                GooglePhotosAlbumMode.CustomName => !string.IsNullOrWhiteSpace(config.CustomAlbumName) ? config.CustomAlbumName : "Google Photos",
                GooglePhotosAlbumMode.AlbumId => !string.IsNullOrWhiteSpace(config.AlbumId) ? config.AlbumId : "Google Photos",
                GooglePhotosAlbumMode.AutoParentDir => "Google Photos (智能相册)",
                _ => "Google Photos"
            };

            var result = await Task.Run(async () =>
            {
                return await GooglePhotosSyncEngine.SyncAsync(
                    ArchivePath,
                    config,
                    progressTarget,
                    msg => Application.Current?.Dispatcher.Invoke(() => LogEntries.Add(msg)),
                    evt => HandleItemProcessed(evt, effectiveAlbumName, isRetry: true),
                    cts.Token
                );
            });

            ProgressText = $"重试完成: 成功上传 {result.UploadedCount}, 跳过 {result.SkippedCount}, 仍失败 {result.FailedCount}";
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "重试操作已停止。";
            CurrentPhase = "已停止";
            LogEntries.Add("[INFO] 重试操作已停止。");
        }
        catch (Exception ex)
        {
            ProgressText = $"重试失败: {ex.Message}";
            CurrentPhase = "失败";
            LogEntries.Add($"[ERROR] 重试异常: {ex.Message}");
            MessageBox.Show($"重试出错: {ex.Message}", "Google 相册重试错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSyncing = false;
            cts?.Dispose();
            cts = null;
            if (wasPaused)
            {
                IsPaused = true;
                UpdatePausedBanner();
                CurrentPhase = App.GetString("GooglePhotosPausedPhase", "已暂停");
                ProgressText = $"已暂停 ({UploadedCount + SkippedCount}/{activePlanTotalFiles} 项已处理，剩余失败 {FailedCount} 项)";
            }
            else if (CurrentPhase is "准备重试...")
            {
                CurrentPhase = "已结束";
            }
        }
    }

    [RelayCommand]
    private void CancelSync()
    {
        PauseSync();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogEntries.Clear();
    }

    private static bool IsPhoto(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".heic" or ".dng" or ".webp" or ".gif" or ".bmp" or ".tiff";
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{(double)bytes / (1024 * 1024 * 1024):F2} GB",
            >= 1024L * 1024 => $"{(double)bytes / (1024 * 1024):F1} MB",
            >= 1024L => $"{(double)bytes / 1024:F0} KB",
            _ => $"{bytes} B"
        };
    }

    private static bool MatchesDate(ManifestEntry entry, DateTime? dateFrom, DateTime? dateEnd)
    {
        var captureDate = ParseCaptureDate(entry);
        if (!captureDate.HasValue) return true;
        if (dateFrom.HasValue && captureDate.Value.LocalDateTime < dateFrom.Value) return false;
        if (dateEnd.HasValue && captureDate.Value.LocalDateTime > dateEnd.Value) return false;
        return true;
    }

    private static DateTimeOffset? ParseCaptureDate(ManifestEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ExifDateTimeOriginalIso) &&
            DateTimeOffset.TryParse(entry.ExifDateTimeOriginalIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exifDt))
        {
            return exifDt;
        }

        if (!string.IsNullOrEmpty(entry.SourceMtimeIso) &&
            DateTimeOffset.TryParse(entry.SourceMtimeIso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mtimeDt))
        {
            return mtimeDt;
        }

        return null;
    }

    private void HandleItemProcessed(GooglePhotosProgressEvent evt, string? albumName, bool isRetry = false)
    {
        string fullPath = evt.Path;
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            try { fullPath = Path.GetFullPath(fullPath); } catch { }
        }
        else
        {
            fullPath = evt.Filename;
        }

        string sourcePath = fullPath;
        if (!string.IsNullOrWhiteSpace(ArchivePath) && fullPath.StartsWith(ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                sourcePath = Path.GetRelativePath(ArchivePath, fullPath).Replace('\\', '/');
            }
            catch { }
        }

        string target = !string.IsNullOrWhiteSpace(albumName) && albumName != "AUTO"
            ? albumName
            : "Google Photos";

        string phase = evt.Phase.ToLowerInvariant();
        string statusText;
        string details;

        if (phase is "completed" or "complete")
        {
            statusText = App.GetString("StatusUploaded", "已上传");
            details = App.GetString("GooglePhotosDetailUploadedSuccess", "成功上传至 Google 相册");
        }
        else if (phase == "skipped")
        {
            statusText = App.GetString("StatusSkipped", "已跳过");
            details = App.GetString("GooglePhotosDetailSkippedExists", "云端已存在相同文件 (跳过)");
        }
        else
        {
            statusText = App.GetString("StatusFailed", "失败");
            details = !string.IsNullOrWhiteSpace(evt.Error)
                ? evt.Error
                : App.GetString("StatusFailed", "上传失败");
        }

        var item = new TransferItemDetail
        {
            SourcePath = sourcePath,
            TargetPath = target,
            FullPath = fullPath,
            FileSizeText = FormatSize(evt.BytesTotal),
            StatusText = statusText,
            Details = details
        };

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (phase is "completed" or "complete")
            {
                var existingFailed = failedDetails.FirstOrDefault(f =>
                    string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (existingFailed != null)
                {
                    failedDetails.Remove(existingFailed);
                    if (IsDetailModalOpen && currentDetailView == GooglePhotosDetailViewType.Failed)
                    {
                        DetailItems.Remove(existingFailed);
                    }
                    FailedCount = failedDetails.Count;
                }

                uploadedDetails.Add(item);
                if (isRetry)
                {
                    UploadedCount++;
                }
                if (IsDetailModalOpen && currentDetailView == GooglePhotosDetailViewType.Uploaded)
                {
                    DetailItems.Add(item);
                }
            }
            else if (phase == "skipped")
            {
                var existingFailed = failedDetails.FirstOrDefault(f =>
                    string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (existingFailed != null)
                {
                    failedDetails.Remove(existingFailed);
                    if (IsDetailModalOpen && currentDetailView == GooglePhotosDetailViewType.Failed)
                    {
                        DetailItems.Remove(existingFailed);
                    }
                    FailedCount = failedDetails.Count;
                }

                skippedDetails.Add(item);
                if (isRetry)
                {
                    SkippedCount++;
                }
                if (IsDetailModalOpen && currentDetailView == GooglePhotosDetailViewType.Skipped)
                {
                    DetailItems.Add(item);
                }
            }
            else if (phase is "error" or "failed")
            {
                var existingFailed = failedDetails.FirstOrDefault(f =>
                    string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (existingFailed != null)
                {
                    existingFailed.Details = details;
                }
                else
                {
                    failedDetails.Add(item);
                    if (isRetry)
                    {
                        FailedCount = failedDetails.Count;
                    }
                    if (IsDetailModalOpen && currentDetailView == GooglePhotosDetailViewType.Failed)
                    {
                        DetailItems.Add(item);
                    }
                }
            }
        });
    }

    private (List<string> existing, List<string> missing) FilterDetailPaths(object? parameter)
    {
        var existing = new List<string>();
        var missing = new List<string>();

        if (parameter is null) return (existing, missing);

        var items = new List<TransferItemDetail>();
        if (parameter is TransferItemDetail singleItem)
        {
            items.Add(singleItem);
        }
        else if (parameter is System.Collections.IEnumerable enumerable)
        {
            foreach (var element in enumerable)
            {
                if (element is TransferItemDetail item)
                {
                    items.Add(item);
                }
            }
        }

        foreach (var item in items)
        {
            string candidate = item.FullPath;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = item.SourcePath;
            }
            if (!string.IsNullOrWhiteSpace(candidate) && !Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(ArchivePath))
            {
                candidate = Path.Combine(ArchivePath, candidate);
            }

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                if (File.Exists(candidate))
                {
                    existing.Add(candidate);
                }
                else
                {
                    missing.Add(candidate);
                }
            }
        }

        if (missing.Count > 0)
        {
            MessageBox.Show(
                $"有 {missing.Count} 个选中的文件在磁盘上不存在:\n{string.Join(Environment.NewLine, missing.Take(3))}{(missing.Count > 3 ? "\n..." : "")}",
                App.GetString("MsgFileNotFound", "文件不存在"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return (existing, missing);
    }

    [RelayCommand]
    private void OpenDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.OpenFiles(existing);
        }
    }

    [RelayCommand]
    private void OpenWithDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            IntPtr hwnd = GetMainWindowHandle();
            ShellServices.OpenWith(existing[0], hwnd);
        }
    }

    [RelayCommand]
    private void CopyDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.CopyFilesToClipboard(existing);
        }
    }

    [RelayCommand]
    private void CopyPathDetail(object? selectedItems)
    {
        var (existing, missing) = FilterDetailPaths(selectedItems);
        var allTargeted = existing.Concat(missing).ToList();
        if (allTargeted.Count > 0)
        {
            ShellServices.CopyPathsToClipboard(allTargeted);
        }
    }

    [RelayCommand]
    private void ShowInExplorerDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            ShellServices.ShowInExplorer(existing);
        }
    }

    [RelayCommand]
    private void PropertiesDetail(object? selectedItems)
    {
        var (existing, _) = FilterDetailPaths(selectedItems);
        if (existing.Count > 0)
        {
            IntPtr hwnd = GetMainWindowHandle();
            ShellServices.ShowProperties(existing, hwnd);
        }
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = Application.Current.MainWindow;
        return window is not null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
    }
}
