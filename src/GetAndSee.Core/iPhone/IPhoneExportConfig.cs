namespace GetAndSee.Core.iPhone;

/// <summary>
/// Configuration parameters for incremental export to the dedicated Apple Sync folder.
/// </summary>
public sealed class IPhoneExportConfig
{
    /// <summary>Target iPhone device model/identifier (e.g. "iPhone 15 Pro" or "iPhone").</summary>
    public string DeviceModel { get; set; } = "iPhone";

    /// <summary>
    /// Custom export folder path override. When null or empty, defaults to
    /// <c>&lt;pcDestinationRoot&gt;/.AppleSync/&lt;DeviceModel&gt;/</c>.
    /// </summary>
    public string? CustomSyncFolder { get; set; }

    /// <summary>Album organization mode (YearMonth or Flat).</summary>
    public IPhoneAlbumMode AlbumMode { get; set; } = IPhoneAlbumMode.YearMonth;

    /// <summary>Whether to clean up orphaned exported files when they are removed from the archive.</summary>
    public bool EnableMirrorDelete { get; set; } = true;

    /// <summary>
    /// Computes the effective export folder path based on <see cref="CustomSyncFolder"/> or the default
    /// <c>&lt;pcDestinationRoot&gt;/.AppleSync/&lt;DeviceModel&gt;/</c> pattern.
    /// </summary>
    public string GetEffectiveExportPath(string pcDestinationRoot)
    {
        if (!string.IsNullOrWhiteSpace(CustomSyncFolder))
        {
            return CustomSyncFolder;
        }

        string sanitizedModel = string.IsNullOrWhiteSpace(DeviceModel) ? "iPhone" : DeviceModel;
        return Path.Combine(pcDestinationRoot, ".AppleSync", sanitizedModel);
    }
}
