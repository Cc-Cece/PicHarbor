namespace GetAndSee.Core.iPhone;

/// <summary>
/// Restore scope mode specifying which subset of PC archive media to export to the iPhone.
/// </summary>
public enum IPhoneRestoreScopeMode
{
    /// <summary>Export all completed media files in the archive (default full mirror).</summary>
    All,

    /// <summary>Export media files captured within a specified DateFrom/DateTo range.</summary>
    DateRange,

    /// <summary>Export media files from selected subfolders / album directories.</summary>
    Subfolder,

    /// <summary>Export media files manually selected by the user.</summary>
    ManualSelection,
}
