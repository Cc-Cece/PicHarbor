using System.IO;
using System.Text.Json;

namespace GetAndSee.Gui.Config;

public sealed class AppConfig
{
    public string DestinationPath { get; set; } = string.Empty;
    public string CurrentLanguage { get; set; } = "zh-CN";
    public string SelectedScheme { get; set; } = "month (YYYY-MM)";
    public bool SyncExifToLastWriteTime { get; set; } = true;
    public bool SyncExifToCreationTime { get; set; } = false;
    public int ReadTimeoutSeconds { get; set; } = 30;

    // Android FTP Sync Settings
    public bool EnableAndroidSync { get; set; } = false;
    public string AndroidDeviceName { get; set; } = "Pixel 8";
    public string AndroidFtpHost { get; set; } = "192.168.1.100";
    public int AndroidFtpPort { get; set; } = 2121;
    public string AndroidFtpUser { get; set; } = "anonymous";
    public string AndroidFtpPassword { get; set; } = "";
    public string AndroidTargetDir { get; set; } = "/DCIM/GetAndSee/iPhone/";
    public string AndroidDeviceId { get; set; } = "";

    // Auto-Completion Settings
    public bool AutoCompleteLivePhotoPair { get; set; } = true;
    public bool AutoCompleteAaeSidecar { get; set; } = true;
    public bool AutoCompleteRawJpg { get; set; } = false;
}

public static class AppSettings
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GetAndSee");

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
