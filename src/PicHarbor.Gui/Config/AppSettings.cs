using System.IO;
using System.Text.Json;

namespace PicHarbor.Gui.Config;

public sealed class AppConfig
{
    public string DestinationPath { get; set; } = string.Empty;
    public string PrimaryDriveLetter { get; set; } = string.Empty;
    public string IPhoneCustomSubdir { get; set; } = string.Empty;
    public string AndroidCustomSubdir { get; set; } = string.Empty;
    public bool GroupMediaByDevice { get; set; } = true;
    public string CurrentLanguage { get; set; } = "zh-CN";
    public string SelectedScheme { get; set; } = "month (YYYY-MM)";
    public bool SyncExifToLastWriteTime { get; set; } = true;
    public bool SyncExifToCreationTime { get; set; } = false;
    public int ReadTimeoutSeconds { get; set; } = 30;

    // Android FTP Sync Settings (Restore to Android)
    public bool EnableAndroidSync { get; set; } = false;
    public string AndroidDeviceName { get; set; } = "Pixel 8";
    public string AndroidFtpHost { get; set; } = "192.168.1.100";
    public int AndroidFtpPort { get; set; } = 2121;
    public string AndroidFtpUser { get; set; } = "anonymous";
    public string AndroidFtpPassword { get; set; } = "";
    public string AndroidTargetDir { get; set; } = "/DCIM/PicHarbor/iPhone/";
    public string AndroidDeviceId { get; set; } = "";

    // Android Backup Settings (Backup to PC)
    public string AndroidBackupDeviceName { get; set; } = "Pixel 8";
    public string AndroidBackupFtpHost { get; set; } = "192.168.1.100";
    public int AndroidBackupFtpPort { get; set; } = 2121;
    public string AndroidBackupFtpUser { get; set; } = "anonymous";
    public string AndroidBackupFtpPassword { get; set; } = "";
    public string AndroidBackupDeviceId { get; set; } = "";
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
    public int GooglePhotosAlbumMode { get; set; } = 0;
    public string GooglePhotosCustomAlbumName { get; set; } = string.Empty;
    public string GooglePhotosAlbumId { get; set; } = string.Empty;
    public int GooglePhotosThreads { get; set; } = 3;
    public bool GooglePhotosUnlimitedQuality { get; set; } = true;
    public bool GooglePhotosStorageSaver { get; set; } = false;
    public bool GooglePhotosSkipExistingFilenames { get; set; } = false;
    public string GooglePhotosPythonPath { get; set; } = "python";
    public string GooglePhotosGpmcPath { get; set; } = string.Empty;
    public int GooglePhotosTimeoutSeconds { get; set; } = 60;
    public int GooglePhotosAutoRetryAttempts { get; set; } = 3;
    public double GooglePhotosRetryDelaySeconds { get; set; } = 2.0;

    // Preview playback. Missing values stay muted.
    public bool PreviewMuted { get; set; } = true;
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
                    if (!string.IsNullOrWhiteSpace(config.GooglePhotosGpmcPath) &&
                        config.GooglePhotosGpmcPath.Contains(@"Code\vscode\gpmc", StringComparison.OrdinalIgnoreCase))
                    {
                        config.GooglePhotosGpmcPath = string.Empty;
                        Save(config);
                    }
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load app settings: {ex.Message}");
        }

        return new AppConfig();
    }

    public static bool LoadPreviewMuted() => Load().PreviewMuted;

    public static void SavePreviewMuted(bool muted)
    {
        try
        {
            AppConfig? config;
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config is null)
                {
                    return;
                }
            }
            else
            {
                config = new AppConfig();
            }

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

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save app settings: {ex.Message}");
        }
    }
}
