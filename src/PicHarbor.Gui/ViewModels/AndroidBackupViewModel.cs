using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PicHarbor.Core.Android;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Preflight;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Transfer;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public sealed partial class AndroidAlbumOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string remotePath = string.Empty;

    [ObservableProperty]
    private string detailText = string.Empty;

    [ObservableProperty]
    private bool isChecked = false;

    [ObservableProperty]
    private bool isCustom = false;
}

public partial class AndroidBackupViewModel : ObservableObject
{
    private readonly Action<string>? onPathChangedCallback;
    private CancellationTokenSource? backupCts;

    [ObservableProperty]
    private string destinationPath = MainViewModel.DefaultArchivePath;

    [ObservableProperty]
    private string selectedScheme = "month (YYYY-MM)";

    // FTP Connection Properties
    [ObservableProperty]
    private string androidFtpHost = "192.168.1.100";

    [ObservableProperty]
    private int androidFtpPort = 2121;

    [ObservableProperty]
    private string androidFtpUser = "anonymous";

    [ObservableProperty]
    private string androidFtpPassword = "";

    [ObservableProperty]
    private string androidDeviceName = "Pixel 8";

    [ObservableProperty]
    private string androidDeviceId = "";

    [ObservableProperty]
    private string connectionStatusText = "未测试连接";

    [ObservableProperty]
    private bool isConnectionOk = false;

    [ObservableProperty]
    private bool isTestingConnection = false;

    // Filter and Scope Properties
    [ObservableProperty]
    private bool ignoreSmallImages = true;

    [ObservableProperty]
    private int minFileSizeKb = 100;

    [ObservableProperty]
    private bool includePhotos = true;

    [ObservableProperty]
    private bool includeVideos = true;

    [ObservableProperty]
    private DateTime? scopeDateFrom = null;

    [ObservableProperty]
    private DateTime? scopeDateTo = null;

    [ObservableProperty]
    private bool isAdvancedPanelOpen = false;

    [ObservableProperty]
    private bool isScanningAlbums = false;

    [ObservableProperty]
    private string scanStatusText = "";

    [ObservableProperty]
    private string customAlbumPath = "";

    public ObservableCollection<AndroidAlbumOptionViewModel> Albums { get; } = new();

    // Progress & Execution Properties
    [ObservableProperty]
    private bool isTransferring = false;

    [ObservableProperty]
    private double progressPercentage = 0;

    [ObservableProperty]
    private string currentFileName = "Ready";

    [ObservableProperty]
    private string speedText = "0.0 MB/s";

    [ObservableProperty]
    private int copiedCount = 0;

    [ObservableProperty]
    private int skippedCount = 0;

    [ObservableProperty]
    private int failedCount = 0;

    [ObservableProperty]
    private string transferredSizeText = "0 B / 0 B";

    [ObservableProperty]
    private string etaText = "--";

    [ObservableProperty]
    private string progressText = "准备就绪";

    // Modal Details Properties
    [ObservableProperty]
    private bool isDetailModalOpen = false;

    [ObservableProperty]
    private string detailModalTitle = "";

    [ObservableProperty]
    private ObservableCollection<TransferItemDetail> detailItems = new();

    private readonly List<TransferItemDetail> copiedDetails = new();
    private readonly List<TransferItemDetail> skippedDetails = new();
    private readonly List<TransferItemDetail> failedDetails = new();

    public ObservableCollection<string> OrganizeSchemes { get; } = new()
    {
        "month (YYYY-MM)",
        "year-month (YYYY\\YYYY-MM)",
        "year (YYYY)",
        "flat"
    };

    public ObservableCollection<string> LogEntries { get; } = new()
    {
        "[INFO] PicHarbor Android 备份引擎就绪。",
        "[INFO] 请填写手机 FTP 连接参数，选择相册后点击“开始增量备份”。"
    };

    public AndroidBackupViewModel() : this(null) { }

    public AndroidBackupViewModel(Action<string>? onPathChangedCallback)
    {
        this.onPathChangedCallback = onPathChangedCallback;
        LoadConfig();
        InitDefaultAlbums();
    }

    private void InitDefaultAlbums()
    {
        Albums.Clear();
        Albums.Add(new AndroidAlbumOptionViewModel
        {
            DisplayName = "📷 相机胶卷 (Camera)",
            RemotePath = "/DCIM/Camera",
            DetailText = "核心拍摄原片与视频",
            IsChecked = true,
            IsCustom = false
        });
        Albums.Add(new AndroidAlbumOptionViewModel
        {
            DisplayName = "📱 屏幕截图 (Screenshots)",
            RemotePath = "/Pictures/Screenshots",
            DetailText = "系统截图目录",
            IsChecked = true,
            IsCustom = false
        });
        Albums.Add(new AndroidAlbumOptionViewModel
        {
            DisplayName = "💬 微信相册 (WeChat)",
            RemotePath = "/Pictures/WeiXin",
            DetailText = "微信保存的图片与视频",
            IsChecked = false,
            IsCustom = false
        });
        Albums.Add(new AndroidAlbumOptionViewModel
        {
            DisplayName = "📥 下载内容 (Download)",
            RemotePath = "/Download",
            DetailText = "浏览器和下载内容",
            IsChecked = false,
            IsCustom = false
        });
    }

    private void LoadConfig()
    {
        var config = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(config.DestinationPath))
        {
            DestinationPath = config.DestinationPath;
        }
        if (!string.IsNullOrWhiteSpace(config.SelectedScheme))
        {
            SelectedScheme = config.SelectedScheme;
        }
        AndroidFtpHost = config.AndroidBackupFtpHost;
        AndroidFtpPort = config.AndroidBackupFtpPort;
        AndroidFtpUser = config.AndroidBackupFtpUser;
        AndroidFtpPassword = config.AndroidBackupFtpPassword;
        AndroidDeviceName = config.AndroidBackupDeviceName;
        AndroidDeviceId = config.AndroidBackupDeviceId;
        MinFileSizeKb = config.AndroidBackupMinFileSizeKb;
        IgnoreSmallImages = config.AndroidBackupIgnoreSmallImages;
        IncludePhotos = config.AndroidBackupIncludePhotos;
        IncludeVideos = config.AndroidBackupIncludeVideos;
    }

    public void SaveConfig()
    {
        var config = AppSettings.Load();
        config.DestinationPath = DestinationPath;
        config.SelectedScheme = SelectedScheme;
        config.AndroidBackupFtpHost = AndroidFtpHost;
        config.AndroidBackupFtpPort = AndroidFtpPort;
        config.AndroidBackupFtpUser = AndroidFtpUser;
        config.AndroidBackupFtpPassword = AndroidFtpPassword;
        config.AndroidBackupDeviceName = AndroidDeviceName;
        config.AndroidBackupDeviceId = AndroidDeviceId;
        config.AndroidBackupMinFileSizeKb = MinFileSizeKb;
        config.AndroidBackupIgnoreSmallImages = IgnoreSmallImages;
        config.AndroidBackupIncludePhotos = IncludePhotos;
        config.AndroidBackupIncludeVideos = IncludeVideos;
        AppSettings.Save(config);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择备份归档目标目录",
            InitialDirectory = Directory.Exists(DestinationPath) ? DestinationPath : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog() == true)
        {
            DestinationPath = dialog.FolderName;
            onPathChangedCallback?.Invoke(DestinationPath);
            SaveConfig();
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionStatusText = "正在连接 FTP 服务器...";
        AddLog($"[INFO] 正在测试连接到 {AndroidFtpHost}:{AndroidFtpPort}...");

        try
        {
            using var client = new SimpleFtpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, cts.Token).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsConnectionOk = true;
                ConnectionStatusText = $"✅ 连接成功 ({AndroidFtpHost}:{AndroidFtpPort})";
                AddLog($"[SUCCESS] 成功连接至 Android FTP 服务器。");
                SaveConfig();
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsConnectionOk = false;
                ConnectionStatusText = $"❌ 连接失败: {ex.Message}";
                AddLog($"[ERROR] 连接失败: {ex.Message}");
            });
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    private async Task ScanAlbumsAsync()
    {
        IsScanningAlbums = true;
        ScanStatusText = "正在扫描手机相册并进行智能过滤...";
        AddLog("[INFO] 开始深度扫描手机公共相册目录 (DCIM, Pictures, Download, Movies)...");

        try
        {
            using var client = new SimpleFtpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await client.ConnectAsync(AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword, cts.Token).ConfigureAwait(false);

            var filterOptions = new AndroidBackupFilterOptions
            {
                MinFileSizeBytes = MinFileSizeKb * 1024L,
                IgnoreSmallImages = IgnoreSmallImages,
                IncludePhotos = IncludePhotos,
                IncludeVideos = IncludeVideos
            };

            var discovered = await AndroidAlbumDiscovery.DiscoverAlbumsAsync(client, filterOptions, cts.Token).ConfigureAwait(false);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Preserve checked status of existing albums
                var checkedPaths = Albums.Where(a => a.IsChecked).Select(a => a.RemotePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

                Albums.Clear();
                foreach (var item in discovered)
                {
                    bool isChecked = checkedPaths.Contains(item.RemotePath) || item.IsDefaultSelected;
                    Albums.Add(new AndroidAlbumOptionViewModel
                    {
                        DisplayName = item.DisplayName,
                        RemotePath = item.RemotePath,
                        DetailText = $"{item.FileCount:N0} 项 · {ByteSize.Humanize(item.TotalBytes)}",
                        IsChecked = isChecked,
                        IsCustom = false
                    });
                }

                ScanStatusText = $"✅ 扫描完成：识别出 {Albums.Count} 个有效相册 (纯小图/缓存目录已自动排除)";
                AddLog($"[SUCCESS] 识别到 {Albums.Count} 个有效相册。");
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ScanStatusText = $"❌ 扫描失败: {ex.Message}";
                AddLog($"[ERROR] 相册扫描出错: {ex.Message}");
            });
        }
        finally
        {
            IsScanningAlbums = false;
        }
    }

    [RelayCommand]
    private void SelectAllAlbums()
    {
        foreach (var a in Albums) a.IsChecked = true;
    }

    [RelayCommand]
    private void DeselectAllAlbums()
    {
        foreach (var a in Albums) a.IsChecked = false;
    }

    [RelayCommand]
    private void AddCustomAlbum()
    {
        string path = CustomAlbumPath.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!path.StartsWith('/')) path = "/" + path;

        if (!Albums.Any(a => a.RemotePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Albums.Add(new AndroidAlbumOptionViewModel
            {
                DisplayName = $"📁 {Path.GetFileName(path.TrimEnd('/'))}",
                RemotePath = path,
                DetailText = "自定义添加目录",
                IsChecked = true,
                IsCustom = true
            });
            CustomAlbumPath = "";
            AddLog($"[INFO] 已添加自定义相册目录: {path}");
        }
    }

    [RelayCommand]
    private void RemoveAlbum(AndroidAlbumOptionViewModel album)
    {
        if (album != null)
        {
            Albums.Remove(album);
        }
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
    }

    [RelayCommand]
    private void ShowCopiedDetails()
    {
        DetailModalTitle = "🤖 Android 备份 已传输文件明细 (Copied)";
        DetailItems = new ObservableCollection<TransferItemDetail>(copiedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowSkippedDetails()
    {
        DetailModalTitle = "🤖 Android 备份 已跳过文件明细 (Skipped - 增量已存在)";
        DetailItems = new ObservableCollection<TransferItemDetail>(skippedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowFailedDetails()
    {
        DetailModalTitle = "⚠️ Android 备份 失败文件明细 (Failed)";
        DetailItems = new ObservableCollection<TransferItemDetail>(failedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void CloseDetailModal()
    {
        IsDetailModalOpen = false;
    }

    [RelayCommand]
    private void CancelBackup()
    {
        if (backupCts != null && !backupCts.IsCancellationRequested)
        {
            AddLog("[WARN] 用户请求取消备份操作...");
            backupCts.Cancel();
        }
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (IsTransferring) return;

        var selectedAlbums = Albums.Where(a => a.IsChecked).Select(a => a.RemotePath).ToList();
        if (selectedAlbums.Count == 0)
        {
            MessageBox.Show("请至少勾选一个要备份的相册文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveConfig();

        IsTransferring = true;
        backupCts = new CancellationTokenSource();
        var ct = backupCts.Token;

        copiedDetails.Clear();
        skippedDetails.Clear();
        failedDetails.Clear();
        CopiedCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        ProgressPercentage = 0;
        CurrentFileName = "正在扫描文件...";
        ProgressText = "正在扫描 Android 设备上的媒体文件...";
        SpeedText = "0.0 MB/s";
        EtaText = "--";

        AddLog($"[INFO] 开始 Android 增量备份任务 -> 目标: {DestinationPath}");

        var preflight = new PreflightChecks();

        await Task.Run(async () =>
        {
            try
            {
                using var client = new FtpAndroidBackupClient(
                    AndroidFtpHost,
                    AndroidFtpPort,
                    AndroidFtpUser,
                    AndroidFtpPassword,
                    deviceId: string.IsNullOrWhiteSpace(AndroidDeviceId) ? AndroidFtpHost : AndroidDeviceId,
                    deviceName: AndroidDeviceName);

                await client.ConnectAsync(ct).ConfigureAwait(false);

                var filterOptions = new AndroidBackupFilterOptions
                {
                    MinFileSizeBytes = MinFileSizeKb * 1024L,
                    IgnoreSmallImages = IgnoreSmallImages,
                    IncludePhotos = IncludePhotos,
                    IncludeVideos = IncludeVideos
                };

                AddLog("[INFO] 正在枚举选定相册中的媒体文件...");
                var files = await client.EnumerateFilesAsync(selectedAlbums, filterOptions, ScopeDateFrom, ScopeDateTo, ct).ConfigureAwait(false);

                if (files.Count == 0)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ProgressText = "未发现需要备份的媒体文件。";
                        CurrentFileName = "完成";
                        AddLog("[INFO] 未发现符合筛选条件的媒体文件。");
                    });
                    return;
                }

                long totalBytes = files.Sum(f => f.Size);
                AddLog($"[INFO] 共发现 {files.Count:N0} 个媒体文件，总计 {ByteSize.Humanize(totalBytes)}。");

                preflight.EnsureDestinationWritable(DestinationPath);
                preflight.EnsureSufficientFreeSpace(DestinationPath, totalBytes);

                OrganizeScheme scheme = ParseSchemeToken(SelectedScheme);
                using var journal = TransferJournal.Open(DestinationPath);
                DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;

                if (client.Device is not null)
                {
                    journal.UpsertDevice(client.Device.Udid, client.Device.Name, client.Device.ProductType, runStartedAt);
                }

                foreach (var file in files)
                {
                    journal.EnsurePending(file);
                }

                var progressModel = new TransferProgress(files.Count, totalBytes);
                var progressTarget = new Progress<ProgressSnapshot>(UpdateSnapshotUI);
                using var reporter = new ObservableProgressReporter(progressTarget, TimeSpan.FromMilliseconds(200));
                reporter.FileCompleted += (s, result) =>
                {
                    AddLog($"[{result.Status.ToString().ToUpper()}] {result.RelativeDestPath ?? result.File.Path}");
                };

                reporter.Start(progressModel);

                using var copier = new FileCopier(
                    client, journal, new DateFolderOrganizer(), new ExifMetadataExtractor(), DestinationPath, scheme,
                    readTimeout: TimeSpan.FromSeconds(30), onBytesStreamed: progressModel.RecordBytes, verifyHash: false);

                copier.CleanStaging();

                int copied = 0, skipped = 0, failed = 0;
                long bytesCopied = 0;
                var stopwatch = Stopwatch.StartNew();

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    progressModel.StartFile(DateFolderOrganizer.ExtractFileName(file.Path), file.Size);

                    CopyResult result = await copier.CopyAsync(file, ct).ConfigureAwait(false);

                    if (result.Status == CopyStatus.Skipped)
                    {
                        progressModel.RecordSkippedBytes(file.Size);
                    }

                    progressModel.CompleteFile(result.Status);
                    reporter.OnFileCompleted(result);

                    string? relPath = result.RelativeDestPath;
                    if (string.IsNullOrWhiteSpace(relPath))
                    {
                        relPath = journal.GetDestRelativePath(file.Path, file.Size)
                                  ?? new DateFolderOrganizer().GetRelativeDestination(file, null, scheme);
                    }

                    string fullDestPath = !string.IsNullOrWhiteSpace(relPath)
                        ? Path.Combine(DestinationPath, relPath)
                        : string.Empty;

                    var detailItem = new TransferItemDetail
                    {
                        SourcePath = file.Path,
                        TargetPath = relPath ?? string.Empty,
                        FullPath = fullDestPath,
                        FileSizeText = ByteSize.Humanize(file.Size),
                        StatusText = result.Status.ToString(),
                        Details = result.Error ?? (result.Status == CopyStatus.Skipped ? "已存在 / 增量跳过" : "成功")
                    };

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        switch (result.Status)
                        {
                            case CopyStatus.Copied:
                                copied++;
                                bytesCopied += result.BytesCopied;
                                copiedDetails.Add(detailItem);
                                break;
                            case CopyStatus.Skipped:
                                skipped++;
                                skippedDetails.Add(detailItem);
                                break;
                            case CopyStatus.Failed:
                                failed++;
                                failedDetails.Add(detailItem);
                                break;
                        }

                        CopiedCount = copied;
                        SkippedCount = skipped;
                        FailedCount = failed;
                    });
                }

                stopwatch.Stop();

                journal.RecordRun(runStartedAt, DateTimeOffset.UtcNow, "copy", copied, skipped, failed, exitCode: 0, client.Device?.Udid);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressPercentage = 100;
                    ProgressText = $"🎉 增量备份完成: {copied} 已传输, {skipped} 增量跳过, {failed} 失败";
                    CurrentFileName = "备份结束";
                    SpeedText = "0.0 MB/s";
                    EtaText = "已完成";
                    AddLog($"[SUMMARY] 备份完成！传输: {copied} 项 ({ByteSize.Humanize(bytesCopied)})，跳过: {skipped} 项，耗时: {stopwatch.Elapsed:mm\\:ss}");
                });
            }
            catch (OperationCanceledException)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressText = "⚠️ 备份已由用户取消。";
                    CurrentFileName = "已取消";
                    AddLog("[WARN] 备份过程被用户取消。");
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ProgressText = $"❌ 备份出错: {ex.Message}";
                    CurrentFileName = "异常中断";
                    AddLog($"[ERROR] 备份异常: {ex.Message}");
                });
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsTransferring = false;
                });
            }
        }, ct);
    }

    private void UpdateSnapshotUI(ProgressSnapshot snapshot)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ProgressPercentage = snapshot.ByteFraction * 100;
            CopiedCount = snapshot.CopiedFiles;
            SkippedCount = snapshot.SkippedFiles;
            FailedCount = snapshot.FailedFiles;
            TransferredSizeText = $"{ByteSize.Humanize(snapshot.ProcessedBytes)} / {ByteSize.Humanize(snapshot.TotalBytes)}";
            EtaText = snapshot.Eta.HasValue
                ? $"{snapshot.Eta.Value.Minutes}m {snapshot.Eta.Value.Seconds}s"
                : (snapshot.ByteFraction >= 1.0 ? "备份完成" : "计算中...");
            ProgressText = $"{snapshot.ByteFraction * 100:F1}% ({snapshot.ProcessedFiles:N0}/{snapshot.TotalFiles:N0} 文件)";

            double mbps = snapshot.CurrentBytesPerSecond / 1024d / 1024d;
            SpeedText = $"{mbps:F1} MB/s";
            CurrentFileName = string.IsNullOrWhiteSpace(snapshot.CurrentFileName)
                ? "进行中..."
                : snapshot.CurrentFileName;
        });
    }

    private void AddLog(string line)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
        if (Application.Current != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LogEntries.Add(entry);
                if (LogEntries.Count > 1000) LogEntries.RemoveAt(0);
            });
        }
    }

    private static OrganizeScheme ParseSchemeToken(string token) => token switch
    {
        "month (YYYY-MM)" => OrganizeScheme.Month,
        "year-month (YYYY\\YYYY-MM)" => OrganizeScheme.YearMonth,
        "year (YYYY)" => OrganizeScheme.Year,
        "flat" => OrganizeScheme.Flat,
        _ => OrganizeScheme.Month
    };
}
