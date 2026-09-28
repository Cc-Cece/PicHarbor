namespace GetAndSee.Core.Transfer;

/// <summary>
/// Specifies the reason why a file transfer operation was skipped.
/// </summary>
public enum SkipReason
{
    /// <summary>Not skipped (file was copied or failed).</summary>
    None,

    /// <summary>Skipped because an equivalent file already exists at the target location.</summary>
    CurrentTargetEquivalent,

    /// <summary>Skipped because the file was previously restored/transferred according to device-specific journal history.</summary>
    HistoricalIncremental
}
