namespace GetAndSee.Core.Transfer;

/// <summary>
/// Detailed result for a single file transfer attempt.
/// </summary>
public sealed record TransferResult(
    string SourcePath,
    string TargetPath,
    long SizeBytes,
    TransferResultStatus Status,
    SkipReason SkipReason = SkipReason.None,
    string? ErrorMessage = null);
