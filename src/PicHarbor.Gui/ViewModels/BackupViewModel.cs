using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Device;
using PicHarbor.Core.Errors;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Preflight;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Scope;
using PicHarbor.Core.Summary;
using PicHarbor.Core.Transfer;
using PicHarbor.Core.Util;
using PicHarbor.Gui.Util;

namespace PicHarbor.Gui.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly Action<string>? onPathChangedCallback;
    private CancellationTokenSource? backupCts;

    [ObservableProperty]
    private string destinationPath = MainViewModel.DefaultArchivePath;

    [ObservableProperty]
    private string selectedScheme = "month (YYYY-MM)";

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
    private string progressText = App.GetString("MsgReady", "准备就绪");

    [ObservableProperty]
    private bool isDetailModalOpen = false;

    [ObservableProperty]
    private string detailModalTitle = "";

    [ObservableProperty]
    private ObservableCollection<TransferItemDetail> detailItems = new();

    private readonly List<TransferItemDetail> copiedDetails = new();
    private readonly List<TransferItemDetail> skippedDetails = new();
    private readonly List<TransferItemDetail> failedDetails = new();

    [RelayCommand]
    private void ShowCopiedDetails()
    {
        DetailModalTitle = "📱 iPhone 备份 已传输 / 已复制文件明细 (Copied)";
        DetailItems = new ObservableCollection<TransferItemDetail>(copiedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowSkippedDetails()
    {
        DetailModalTitle = "📱 iPhone 备份 已跳过文件明细 (Skipped - 已存在/已增量归档)";
        DetailItems = new ObservableCollection<TransferItemDetail>(skippedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void ShowFailedDetails()
    {
        DetailModalTitle = "⚠️ iPhone 备份 失败文件明细 (Failed)";
        DetailItems = new ObservableCollection<TransferItemDetail>(failedDetails);
        IsDetailModalOpen = true;
    }

    [RelayCommand]
    private void CloseDetailModal()
    {
        IsDetailModalOpen = false;
    }

    // --- Scope Properties ---
    [ObservableProperty]
    private ScopeMode scopeMode = ScopeMode.All;

    public bool IsScopeAll
    {
        get => ScopeMode == ScopeMode.All;
        set
        {
            if (value && ScopeMode != ScopeMode.All)
            {
                ScopeMode = ScopeMode.All;
                NotifyScopeProperties();
            }
        }
    }

    public bool IsScopeDateRange
    {
        get => ScopeMode == ScopeMode.Date;
        set
        {
            if (value && ScopeMode != ScopeMode.Date)
            {
                ScopeMode = ScopeMode.Date;
                NotifyScopeProperties();
            }
        }
    }

    public bool IsScopeSubfolder
    {
        get => ScopeMode == ScopeMode.Folder;
        set
        {
            if (value && ScopeMode != ScopeMode.Folder)
            {
                ScopeMode = ScopeMode.Folder;
                NotifyScopeProperties();
            }
        }
    }

    public bool IsScopeManualSelection
    {
        get => ScopeMode == ScopeMode.Manual;
        set
        {
            if (value && ScopeMode != ScopeMode.Manual)
            {
                ScopeMode = ScopeMode.Manual;
                NotifyScopeProperties();
            }
        }
    }

    private void NotifyScopeProperties()
    {
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeDateRange));
        OnPropertyChanged(nameof(IsScopeSubfolder));
        OnPropertyChanged(nameof(IsScopeManualSelection));
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
        if (scopeDateFromText != formatted)
        {
            ScopeDateFromText = formatted;
        }
    }

    partial void OnScopeDateToChanged(DateTime? value)
    {
        string formatted = value?.ToString("yyyy-MM-dd") ?? "";
        if (scopeDateToText != formatted)
        {
            ScopeDateToText = formatted;
        }
    }

    public ObservableCollection<SubfolderOptionViewModel> Subfolders { get; } = new();

    [RelayCommand]
    private void ClearDateRange()
    {
        ScopeDateFrom = null;
        ScopeDateTo = null;
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
    private async Task RefreshSubfoldersAsync()
    {
        try
        {
            using var client = new AfcIPhoneClient(TimeSpan.FromSeconds(10));
            await client.ConnectAsync().ConfigureAwait(false);
            var childNames = await client.ListDirectoryAsync("/DCIM/").ConfigureAwait(false);

            var existingChecked = Subfolders.Where(s => s.IsChecked).Select(s => s.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool hadItems = Subfolders.Count > 0;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Subfolders.Clear();
                foreach (var name in childNames)
                {
                    if (name.StartsWith(".")) continue;
                    bool isChecked = !hadItems || existingChecked.Contains(name);
                    Subfolders.Add(new SubfolderOptionViewModel
                    {
                        FolderName = name,
                        RelativePath = name,
                        IsChecked = isChecked
                    });
                }
            });
        }
        catch (Exception ex)
        {
            AddLog($"[INFO] Subfolder scan info: {ex.Message}");
        }
    }

    [ObservableProperty]
    private bool enableAndroidSync = false;

    [ObservableProperty]
    private string androidDeviceName = "Pixel 8";

    [ObservableProperty]
    private string androidFtpHost = "192.168.1.100";

    [ObservableProperty]
    private int androidFtpPort = 2121;

    [ObservableProperty]
    private string androidFtpUser = "anonymous";

    [ObservableProperty]
    private string androidFtpPassword = "";

    [ObservableProperty]
    private string androidTargetDir = "/DCIM/PicHarbor/iPhone/";

    [ObservableProperty]
    private string androidDeviceId = "";

    [ObservableProperty]
    private string androidTestStatus = "";

    public ObservableCollection<string> OrganizeSchemes { get; } = new()
    {
        "month (YYYY-MM)",
        "year-month (YYYY\\YYYY-MM)",
        "year (YYYY)",
        "flat"
    };

    public ObservableCollection<string> LogEntries { get; } = new()
    {
        "[INFO] PicHarbor Core v1.0 Ready.",
        "[INFO] Select destination path and click 'Start Incremental Backup'."
    };

    public BackupViewModel() : this(null) { }

    public BackupViewModel(Action<string>? onPathChangedCallback)
    {
        this.onPathChangedCallback = onPathChangedCallback;
        LoadConfig();
    }

    private void LoadConfig()
    {
        var config = Config.AppSettings.Load();
        EnableAndroidSync = config.EnableAndroidSync;
        AndroidDeviceName = config.AndroidDeviceName;
        AndroidFtpHost = config.AndroidFtpHost;
        AndroidFtpPort = config.AndroidFtpPort;
        AndroidFtpUser = config.AndroidFtpUser;
        AndroidFtpPassword = config.AndroidFtpPassword;
        AndroidTargetDir = config.AndroidTargetDir;
        AndroidDeviceId = config.AndroidDeviceId;
    }

    public void SaveAndroidConfig()
    {
        var config = Config.AppSettings.Load();
        config.EnableAndroidSync = EnableAndroidSync;
        config.AndroidDeviceName = AndroidDeviceName;
        config.AndroidFtpHost = AndroidFtpHost;
        config.AndroidFtpPort = AndroidFtpPort;
        config.AndroidFtpUser = AndroidFtpUser;
        config.AndroidFtpPassword = AndroidFtpPassword;
        config.AndroidTargetDir = AndroidTargetDir;
        config.AndroidDeviceId = AndroidDeviceId;
        Config.AppSettings.Save(config);
    }

    partial void OnSelectedSchemeChanged(string value) => SaveAndroidConfig();
    partial void OnEnableAndroidSyncChanged(bool value) => SaveAndroidConfig();
    partial void OnAndroidDeviceNameChanged(string value) => SaveAndroidConfig();
    partial void OnAndroidFtpHostChanged(string value) => SaveAndroidConfig();
    partial void OnAndroidFtpPortChanged(int value) => SaveAndroidConfig();
    partial void OnAndroidFtpUserChanged(string value) => SaveAndroidConfig();
    partial void OnAndroidFtpPasswordChanged(string value) => SaveAndroidConfig();
    partial void OnAndroidTargetDirChanged(string value) => SaveAndroidConfig();
    partial void OnAndroidDeviceIdChanged(string value) => SaveAndroidConfig();

    partial void OnDestinationPathChanged(string value)
    {
        onPathChangedCallback?.Invoke(value);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 iPhone 备份目标保存目录",
            InitialDirectory = Directory.Exists(DestinationPath) ? DestinationPath : string.Empty
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            DestinationPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (IsTransferring) return;

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            AddLog("[ERROR] Destination path cannot be empty.");
            return;
        }

        IsTransferring = true;
        backupCts = new CancellationTokenSource();
        CancellationToken ct = backupCts.Token;

        ProgressPercentage = 0;
        CopiedCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        TransferredSizeText = "0 B / 0 B";
        EtaText = "--";
        ProgressText = "正在初始化 iPhone 增量备份...";

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            copiedDetails.Clear();
            skippedDetails.Clear();
            failedDetails.Clear();
        });

        AddLog($"[START] Initializing backup to {DestinationPath}...");

        try
        {
            await Task.Run(async () =>
            {
                var preflight = new PreflightChecks();
                await preflight.EnsureDriverServiceReachableAsync(ct).ConfigureAwait(false);

                using var client = new AfcIPhoneClient(TimeSpan.FromSeconds(30));
                AddLog("[INFO] Connecting to iPhone via AFC...");
                await client.ConnectAsync(ct).ConfigureAwait(false);

                DeviceInfo? device = client.Device;
                AddLog($"[INFO] Connected: {device?.Name ?? "iPhone"} ({device?.ProductType ?? "iOS"})");

                AddLog("[INFO] Scanning /DCIM/ ...");
                var enumerator = new DcimEnumerator(client);
                var files = new List<RemoteFile>();
                long totalBytes = 0;

                await foreach (RemoteFile file in enumerator.EnumerateAsync(cancellationToken: ct).ConfigureAwait(false))
                {
                    files.Add(file);
                }

                DateTime? dateFrom = DateTime.TryParse(ScopeDateFromText, out var df) ? df : null;
                DateTime? dateTo = DateTime.TryParse(ScopeDateToText, out var dt) ? dt : null;

                // Discover subfolders from scanned media
                var discoveredFolders = files.Select(f => IPhoneBackupScopeResolver.GetSubfolderName(f.Path))
                                             .Where(name => !string.IsNullOrWhiteSpace(name))
                                             .Distinct(StringComparer.OrdinalIgnoreCase)
                                             .ToList();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var existingChecked = Subfolders.Where(s => s.IsChecked).Select(s => s.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    bool hadItems = Subfolders.Count > 0;
                    foreach (var folder in discoveredFolders)
                    {
                        if (!Subfolders.Any(s => s.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                        {
                            Subfolders.Add(new SubfolderOptionViewModel
                            {
                                FolderName = folder,
                                RelativePath = folder,
                                IsChecked = !hadItems || existingChecked.Contains(folder)
                            });
                        }
                    }
                });

                var selectedSubfolders = ScopeMode == ScopeMode.Folder
                    ? Subfolders.Where(s => s.IsChecked).Select(s => s.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;

                var criteria = new IPhoneBackupScopeCriteria
                {
                    ScopeMode = ScopeMode,
                    DateFrom = dateFrom,
                    DateTo = dateTo,
                    SelectedSubfolders = selectedSubfolders
                };

                files = IPhoneBackupScopeResolver.Filter(files, criteria);

                totalBytes = files.Sum(f => f.Size);
                AddLog($"[INFO] Found {files.Count:N0} files matching scope mode ({ByteSize.Humanize(totalBytes)}).");

                preflight.EnsureDestinationWritable(DestinationPath);
                preflight.EnsureSufficientFreeSpace(DestinationPath, totalBytes);

                OrganizeScheme scheme = ParseSchemeToken(SelectedScheme);
                using var journal = TransferJournal.Open(DestinationPath);
                DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;

                if (device is not null)
                {
                    journal.UpsertDevice(device.Udid, device.Name, device.ProductType, runStartedAt);
                }

                foreach (RemoteFile file in files)
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

                DisconnectEscapeHatch escapeHatch = new(DestinationPath, new TerminateProcessTerminator());
                using var copier = new FileCopier(
                    client, journal, new DateFolderOrganizer(), new ExifMetadataExtractor(), DestinationPath, scheme,
                    readTimeout: TimeSpan.FromSeconds(30), onBytesStreamed: progressModel.RecordBytes, verifyHash: false,
                    onDisconnect: escapeHatch.Activate);

                copier.CleanStaging();

                int copied = 0, skipped = 0, failed = 0;
                long bytesCopied = 0;
                var stopwatch = Stopwatch.StartNew();

                foreach (RemoteFile file in files)
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
                        FileSizeText = FormatBytes(file.Size),
                        StatusText = result.Status.ToString(),
                        Details = result.Error ?? (result.Status == CopyStatus.Skipped ? App.GetString("MsgStatusSkipped", "已存在 / 增量跳过") : App.GetString("MsgStatusSuccess", "成功"))
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
                    });
                }

                stopwatch.Stop();
                journal.RecordRun(runStartedAt, DateTimeOffset.UtcNow, "copy-gui", copied, skipped, failed, 0, device?.Udid);

                new SummaryWriter().Write(
                    DestinationPath, journal.ReadManifest(), journal.ReadDevices(), journal.ReadRunsSummary(), DateTimeOffset.UtcNow);

                AddLog($"[SUCCESS] Completed: {copied} copied, {skipped} skipped, {failed} failed in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AddLog("[WARNING] Backup stopped by user. Progress saved in SQLite manifest.");
        }
        catch (PreflightException ex)
        {
            AddLog($"[ERROR] Preflight failed: {ex.Message}");
        }
        catch (DeviceException ex)
        {
            AddLog($"[ERROR] Device error: {ex.Message}");
        }
        catch (Exception ex)
        {
            AddLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            IsTransferring = false;
            SpeedText = "0.0 MB/s";
            CurrentFileName = "Idle";
        }
    }

    [RelayCommand]
    private void StopBackup()
    {
        if (!IsTransferring) return;

        backupCts?.Cancel();
        AddLog("[CANCELING] Stopping transfer cleanly...");
    }

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (IsTransferring) return;

        AddLog("[DRY RUN] Starting preview scan...");
        try
        {
            await Task.Run(async () =>
            {
                using var client = new AfcIPhoneClient(TimeSpan.FromSeconds(10));
                await client.ConnectAsync().ConfigureAwait(false);
                var enumerator = new DcimEnumerator(client);
                var organizer = new DateFolderOrganizer();
                OrganizeScheme scheme = ParseSchemeToken(SelectedScheme);

                int count = 0;
                await foreach (RemoteFile file in enumerator.EnumerateAsync().ConfigureAwait(false))
                {
                    count++;
                    string relative = organizer.GetRelativeDestination(file, null, scheme);
                    if (count <= 10)
                    {
                        AddLog($"[DRY RUN] Would copy: {file.Path} -> {relative}");
                    }
                }

                AddLog($"[DRY RUN] Total {count:N0} files scanned. No files written.");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddLog($"[DRY RUN ERROR] {ex.Message}");
        }
    }

    private void UpdateSnapshotUI(ProgressSnapshot snapshot)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ProgressPercentage = snapshot.ByteFraction * 100;
            CopiedCount = snapshot.CopiedFiles;
            SkippedCount = snapshot.SkippedFiles;
            FailedCount = snapshot.FailedFiles;
            TransferredSizeText = $"{FormatBytes(snapshot.ProcessedBytes)} / {FormatBytes(snapshot.TotalBytes)}";
            EtaText = snapshot.Eta is TimeSpan eta
                ? FormatTimeSpan(eta)
                : (snapshot.ByteFraction >= 1.0 ? App.GetString("MsgBackupCompleted", "备份完成") : App.GetString("MsgCalculating", "计算中..."));
            ProgressText = $"{snapshot.ByteFraction * 100:F1}% ({snapshot.ProcessedFiles:N0}/{snapshot.TotalFiles:N0} 文件) - {FormatSpeed(snapshot.CurrentBytesPerSecond)}";

            double mbps = snapshot.CurrentBytesPerSecond / 1024d / 1024d;
            SpeedText = $"{mbps:F1} MB/s";
            CurrentFileName = string.IsNullOrWhiteSpace(snapshot.CurrentFileName)
                ? "In progress..."
                : snapshot.CurrentFileName;
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double dblSByte = bytes;
        while (dblSByte >= 1024 && i < suffixes.Length - 1)
        {
            dblSByte /= 1024;
            i++;
        }
        return $"{dblSByte:0.##} {suffixes[i]}";
    }

    private static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}小时{span.Minutes:D2}分{span.Seconds:D2}秒";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes:D2}分{span.Seconds:D2}秒";
        }
        return $"{span.Seconds}秒";
    }

    private void AddLog(string message)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogEntries.Add(message);
            if (LogEntries.Count > 500)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    [RelayCommand]
    private async Task TestAndroidConnectionAsync()
    {
        AndroidTestStatus = "正在测试 FTP 连接...";
        try
        {
            await Task.Run(async () =>
            {
                using var ftp = new PicHarbor.Core.Android.SimpleFtpClient();
                await ftp.ConnectAsync(AndroidFtpHost, AndroidFtpPort, AndroidFtpUser, AndroidFtpPassword).ConfigureAwait(false);
                string verifiedId = await PicHarbor.Core.Android.AndroidDeviceDetector.IdentifyOrPairDeviceAsync(
                    ftp, AndroidTargetDir, AndroidDeviceId, AndroidDeviceName).ConfigureAwait(false);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AndroidDeviceId = verifiedId;
                    AndroidTestStatus = $"连接成功！设备 ID: {verifiedId}";
                    SaveAndroidConfig();
                });
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AndroidTestStatus = $"连接失败: {ex.Message}";
        }
    }

    private static OrganizeScheme ParseSchemeToken(string schemeText)
    {
        if (schemeText.StartsWith("year-month")) return OrganizeScheme.YearMonth;
        if (schemeText.StartsWith("year")) return OrganizeScheme.Year;
        if (schemeText.StartsWith("flat")) return OrganizeScheme.Flat;
        return OrganizeScheme.Month;
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
                candidate = item.TargetPath;
            }
            if (!string.IsNullOrWhiteSpace(candidate) && !Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(DestinationPath))
            {
                candidate = Path.Combine(DestinationPath, candidate);
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
