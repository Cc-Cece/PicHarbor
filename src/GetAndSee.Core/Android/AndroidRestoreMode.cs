namespace GetAndSee.Core.Android;

/// <summary>
/// Specifies the restore mode for Android operations.
/// </summary>
public enum AndroidRestoreMode
{
    /// <summary>
    /// Default restore: Filters by Scope and checks Current Target State for equivalence.
    /// Does NOT read history to skip, but writes successful transfers to history.
    /// </summary>
    Default,

    /// <summary>
    /// Historical Incremental restore: Filters by Scope and checks device-specific history records.
    /// Skips files already successfully transferred for the active device_id, and writes successful transfers to history.
    /// </summary>
    HistoricalIncremental
}
