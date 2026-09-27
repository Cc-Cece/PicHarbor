namespace GetAndSee.Core.iPhone;

/// <summary>
/// Summary results of an iPhone sync export operation.
/// </summary>
public sealed record IPhoneSyncResult(
    int TotalArchivedCount,
    int CopiedCount,
    int SkippedCount,
    int DeletedCount,
    int FailedCount,
    long TotalCopiedSizeBytes,
    TimeSpan Elapsed)
{
    /// <summary>The absolute path of the effective export folder.</summary>
    public string ExportedFolder { get; init; } = string.Empty;
}
