namespace PicHarbor.Core.iPhone;

/// <summary>
/// Specifies how photos/videos are organized into subfolders inside the dedicated Apple Sync folder.
/// </summary>
public enum IPhoneAlbumMode
{
    /// <summary>
    /// Files are automatically placed into YYYY-MM subfolders (e.g. 2024-05/).
    /// Apple Devices will turn each subfolder into an independent album on the iPhone.
    /// </summary>
    YearMonth,

    /// <summary>
    /// All files are flattened directly inside the root of the sync folder without subdirectories.
    /// Files appear directly in the global "From My Mac" photo library on the iPhone.
    /// </summary>
    Flat,
}
