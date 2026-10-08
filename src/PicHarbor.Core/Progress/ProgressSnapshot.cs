namespace PicHarbor.Core.Progress;

/// <summary>
/// An immutable point-in-time view of a transfer's progress, rendered by any
/// <see cref="IProgressReporter"/>.
/// </summary>
/// <param name="TotalFiles">Total files planned for this run.</param>
/// <param name="CopiedFiles">Files copied so far this run.</param>
/// <param name="SkippedFiles">Files skipped (already done) so far this run.</param>
/// <param name="FailedFiles">Files that failed so far this run.</param>
/// <param name="TotalBytes">Total bytes planned for this run.</param>
/// <param name="ProcessedBytes">Bytes accounted for so far (streamed this run plus skipped files' sizes).</param>
/// <param name="CurrentBytesPerSecond">Rolling (~3s) transfer rate — answers "is it moving right now?".</param>
/// <param name="AverageBytesPerSecond">Cumulative average transfer rate over the whole run.</param>
/// <param name="Eta">Estimated time remaining, or <see langword="null"/> when not yet computable.</param>
/// <param name="CurrentFileName">Name of the file currently being copied, or empty.</param>
/// <param name="CurrentFileCopiedBytes">Bytes streamed for the current file.</param>
/// <param name="CurrentFileTotalBytes">Total size of the current file.</param>
public sealed record ProgressSnapshot(
    int TotalFiles,
    int CopiedFiles,
    int SkippedFiles,
    int FailedFiles,
    long TotalBytes,
    long ProcessedBytes,
    double CurrentBytesPerSecond,
    double AverageBytesPerSecond,
    TimeSpan? Eta,
    string CurrentFileName,
    long CurrentFileCopiedBytes,
    long CurrentFileTotalBytes)
{
    /// <summary>Files processed so far (copied + skipped + failed).</summary>
    public int ProcessedFiles => CopiedFiles + SkippedFiles + FailedFiles;

    /// <summary>Fraction of total bytes processed in <c>[0, 1]</c> (0 when the total is unknown).</summary>
    public double ByteFraction => TotalBytes > 0 ? Math.Clamp((double)ProcessedBytes / TotalBytes, 0, 1) : 0;
}
