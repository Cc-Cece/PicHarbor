namespace GetAndSee.Core.GooglePhotos;

/// <summary>
/// Strategy for assigning uploaded media into albums in Google Photos.
/// </summary>
public enum GooglePhotosAlbumMode
{
    /// <summary>Do not assign to any album (stream only).</summary>
    None = 0,

    /// <summary>Automatically create/use albums named after the immediate parent directory (e.g. 2024-08).</summary>
    AutoParentDir = 1,

    /// <summary>Add uploaded media to a custom album name.</summary>
    CustomName = 2,

    /// <summary>Add uploaded media to an existing album by its media key (ID).</summary>
    AlbumId = 3
}

/// <summary>
/// Scope of files to upload to Google Photos.
/// </summary>
public enum GooglePhotosScopeMode
{
    /// <summary>Upload all files in the archive.</summary>
    All = 0,

    /// <summary>Upload files within a specific capture date range.</summary>
    DateRange = 1,

    /// <summary>Upload files inside a specific subfolder (e.g. month folder).</summary>
    Subfolder = 2,

    /// <summary>Upload manually selected files from the media search grid/gallery.</summary>
    ManualSelection = 3,

    /// <summary>Upload a custom external folder or single file from disk.</summary>
    CustomTarget = 4
}
