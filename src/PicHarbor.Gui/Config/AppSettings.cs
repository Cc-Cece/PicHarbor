using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Text.Json;

namespace PicHarbor.Gui.Config;

public sealed class AppConfig
{
    public Core.Updates.UpdateMode UpdateMode { get; set; } = Core.Updates.UpdateMode.Automatic;
    public Core.Updates.UpdateRegionMode UpdateRegionMode { get; set; } = Core.Updates.UpdateRegionMode.Automatic;
    public string SkippedUpdateVersion { get; set; } = "";
    public bool AppLockWasEnabled { get; set; }
    public bool RebackupDeletedPhotos { get; set; } = true;
    public bool RebackupQuestionAsked { get; set; }
    public bool StrictBackupVerification { get; set; }
    public bool FastBackupVerification { get; set; }
    public string ActiveLibraryRoot { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string PrimaryDriveLetter { get; set; } = string.Empty;
    // 归档文件夹名已改由归档库的 devices.archive_folder 持有（与设备身份同生共死），不再存配置。

    /// <summary>
    /// iPhone 设备显示名（用户可通过「重命名」修改）。为空时回落到设备自身报告的名字。
    /// </summary>
    public string IPhoneDeviceName { get; set; } = string.Empty;
    public bool GroupMediaByDevice { get; set; } = true;
    public string CurrentLanguage { get; set; } = "zh-CN";
    public string SelectedScheme { get; set; } = "month (YYYY-MM)";
    public bool SyncExifToLastWriteTime { get; set; } = true;
    public bool SyncExifToCreationTime { get; set; } = false;
    public int ReadTimeoutSeconds { get; set; } = 30;

    // Android FTP Sync Settings (Restore to Android)
    public bool EnableAndroidSync { get; set; } = false;
    public string AndroidFtpHost { get; set; } = "192.168.1.100";
    public int AndroidFtpPort { get; set; } = 2121;
    public string AndroidFtpUser { get; set; } = "anonymous";
    public string AndroidFtpPassword { get; set; } = "";

    /// <summary>
    /// 安卓同步目标目录。<b>已固定为 <c>/DCIM/PicHarbor/</c>，不再由用户配置。</b>
    /// 保留字段只为兼容既有配置文件；读写一律经 <c>AndroidRemoteLayout.Normalize</c> 收敛。
    /// </summary>
    public string AndroidTargetDir { get; set; } = Core.Android.AndroidRemoteLayout.RootDirectory;

    /// <summary>
    /// 已退役：旧的「同步页设备标识」字段。现统一使用 <see cref="AndroidBackupDeviceId"/>。
    /// 保留仅为兼容既有配置文件，加载时会自动迁移过去。
    /// </summary>
    public string AndroidDeviceId { get; set; } = "";

    // Android Backup Settings (Backup to PC)
    public string AndroidBackupDeviceName { get; set; } = "Pixel 8";
    public string AndroidBackupFtpHost { get; set; } = "192.168.1.100";
    public int AndroidBackupFtpPort { get; set; } = 2121;
    public string AndroidBackupFtpUser { get; set; } = "anonymous";
    public string AndroidBackupFtpPassword { get; set; } = "";
    public string AndroidBackupDeviceId { get; set; } = "";

    /// <summary>
    /// 安卓备份的设备标识是否已由用户确认（自动识别成功或人工输入）。
    /// 为 <c>true</c> 时不再重复提示核对，避免每次插线都弹窗。
    /// </summary>
    public bool AndroidBackupDeviceIdConfirmed { get; set; } = false;
    public int AndroidBackupMinFileSizeKb { get; set; } = 100;
    public bool AndroidBackupIgnoreSmallImages { get; set; } = true;
    public bool AndroidBackupIncludePhotos { get; set; } = true;
    public bool AndroidBackupIncludeVideos { get; set; } = true;

    // Auto-Completion Settings
    public bool AutoCompleteLivePhotoPair { get; set; } = true;
    public bool AutoCompleteAaeSidecar { get; set; } = true;
    public bool AutoCompleteRawJpg { get; set; } = false;

    // Google Photos Sync Settings
    public int GooglePhotosAuthMethod { get; set; } = 0;
    public string GooglePhotosOAuthCookie { get; set; } = string.Empty;
    public string GooglePhotosAccountEmail { get; set; } = string.Empty;
    public string GooglePhotosAuthData { get; set; } = string.Empty;
    public string GooglePhotosProxy { get; set; } = string.Empty;
    public int GooglePhotosAlbumMode { get; set; } = (int)Core.GooglePhotos.GooglePhotosAlbumMode.None;
    public string GooglePhotosCustomAlbumName { get; set; } = string.Empty;
    public string GooglePhotosAlbumId { get; set; } = string.Empty;
    public int GooglePhotosThreads { get; set; } = 3;
    public bool GooglePhotosUnlimitedQuality { get; set; } = true;
    public bool GooglePhotosStorageSaver { get; set; } = false;

    /// <summary>motion, applePair, or appleReconcile. New uploads only.</summary>
    public string GooglePhotosLiveStrategy { get; set; } = "applePair";
    public bool GooglePhotosSkipExistingFilenames { get; set; } = false;
    public string GooglePhotosGpmcPath { get; set; } = string.Empty;
    public int GooglePhotosTimeoutSeconds { get; set; } = 60;
    public int GooglePhotosAutoRetryAttempts { get; set; } = 3;
    public double GooglePhotosRetryDelaySeconds { get; set; } = 2.0;

    // Preview playback. Missing values stay muted.
    public bool PreviewMuted { get; set; } = true;

    // 应用锁：系统空闲 N 分钟后自动重新锁定（0 = 关闭）。
    public int IdleLockMinutes { get; set; } = 0;
}

public static class AppSettings
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PicHarbor");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config is not null)
                {
                    bool dirty = UnprotectCredentials(config);

                    if (!string.IsNullOrWhiteSpace(config.GooglePhotosGpmcPath) &&
                        config.GooglePhotosGpmcPath.Contains(@"Code\vscode\gpmc", StringComparison.OrdinalIgnoreCase))
                    {
                        config.GooglePhotosGpmcPath = string.Empty;
                        dirty = true;
                    }

                    // 一次性迁移：旧的同步页标识字段并入备份页字段（AndroidDeviceId 已退役）。
                    if (string.IsNullOrWhiteSpace(config.AndroidBackupDeviceId) &&
                        !string.IsNullOrWhiteSpace(config.AndroidDeviceId))
                    {
                        config.AndroidBackupDeviceId = config.AndroidDeviceId;
                        config.AndroidBackupDeviceIdConfirmed = true;
                        dirty = true;
                    }

                    // 目标目录已固定，历史/用户自填值一律收敛。
                    string normalizedDir = Core.Android.AndroidRemoteLayout.Normalize(config.AndroidTargetDir);
                    if (!string.Equals(normalizedDir, config.AndroidTargetDir, StringComparison.Ordinal))
                    {
                        config.AndroidTargetDir = normalizedDir;
                        dirty = true;
                    }

                    if (dirty)
                    {
                        Save(config);
                    }

                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load app settings: {ex.Message}");
            // Unreadable settings must not disable an existing privacy lock.
            return new AppConfig { AppLockWasEnabled = File.Exists(ConfigPath) };
        }

        return new AppConfig();
    }

    public static bool LoadPreviewMuted() => Load().PreviewMuted;

    public static void SavePreviewMuted(bool muted)
    {
        try
        {
            AppConfig config = Load();

            if (config.PreviewMuted == muted && File.Exists(ConfigPath))
            {
                return;
            }

            config.PreviewMuted = muted;
            Save(config);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save preview mute: {ex.Message}");
        }
    }

    private const string ProtectedPrefix = "dpapi:";
    private static string Protect(string value) => string.IsNullOrEmpty(value) ? value :
        ProtectedPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    private static string Unprotect(string value) => value.StartsWith(ProtectedPrefix, StringComparison.Ordinal) ?
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[ProtectedPrefix.Length..]), null, DataProtectionScope.CurrentUser)) : value;
    private static void ProtectCredentials(AppConfig config)
    {
        config.AndroidFtpPassword = Protect(config.AndroidFtpPassword);
        config.AndroidBackupFtpPassword = Protect(config.AndroidBackupFtpPassword);
        config.GooglePhotosOAuthCookie = Protect(config.GooglePhotosOAuthCookie);
        config.GooglePhotosAuthData = Protect(config.GooglePhotosAuthData);
    }
    private static bool UnprotectCredentials(AppConfig config)
    {
        bool migrate = new[] {config.AndroidFtpPassword,config.AndroidBackupFtpPassword,config.GooglePhotosOAuthCookie,config.GooglePhotosAuthData}
            .Any(v => !string.IsNullOrEmpty(v) && !v.StartsWith(ProtectedPrefix, StringComparison.Ordinal));
        config.AndroidFtpPassword = Unprotect(config.AndroidFtpPassword);
        config.AndroidBackupFtpPassword = Unprotect(config.AndroidBackupFtpPassword);
        config.GooglePhotosOAuthCookie = Unprotect(config.GooglePhotosOAuthCookie);
        config.GooglePhotosAuthData = Unprotect(config.GooglePhotosAuthData);
        return migrate;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var stored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, JsonOptions))!;
            ProtectCredentials(stored);
            string json = JsonSerializer.Serialize(stored, JsonOptions);
            string temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save app settings: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 读取「系统空闲 N 分钟后自动锁定」配置（0 = 关闭）；非法值回退为 0。
    /// </summary>
    public static int LoadIdleLockMinutes()
    {
        int value = Load().IdleLockMinutes;
        return value < 0 ? 0 : value;
    }

    /// <summary>
    /// 保存自动锁定分钟数。<b>必须走「读-改-写」</b>：<see cref="Save"/> 是整文件覆盖，
    /// 直接 new 一个 <see cref="AppConfig"/> 盲存会抹掉语言、目录、OAuth 等全部设置。
    /// 0 = 关闭。
    /// </summary>
    public static void SaveIdleLockMinutes(int minutes)
    {
        try
        {
            AppConfig config = Load();
            int normalized = minutes < 0 ? 0 : minutes;
            if (config.IdleLockMinutes == normalized && File.Exists(ConfigPath)) return;

            config.IdleLockMinutes = normalized;
            Save(config);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save idle lock minutes: {ex.Message}");
        }
    }
}
