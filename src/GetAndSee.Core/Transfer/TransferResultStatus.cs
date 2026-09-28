namespace GetAndSee.Core.Transfer;

/// <summary>
/// Status of an individual file transfer item.
/// </summary>
public enum TransferResultStatus
{
    /// <summary>Successfully copied.</summary>
    Copied,

    /// <summary>Skipped based on equivalence or history.</summary>
    Skipped,

    /// <summary>Transfer failed due to error.</summary>
    Failed,

    /// <summary>Transfer was cancelled before completion.</summary>
    Cancelled
}
