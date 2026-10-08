namespace PicHarbor.Core.Summary;

/// <summary>Aggregate counters for a single run, used in the console summary and <c>summary.txt</c>.</summary>
/// <param name="Enumerated">Total files discovered on the device this run.</param>
/// <param name="Copied">Files copied this run.</param>
/// <param name="Skipped">Files skipped because the journal already had them done.</param>
/// <param name="Failed">Files that failed this run.</param>
/// <param name="BytesCopied">Total bytes copied this run.</param>
/// <param name="Elapsed">Wall-clock duration of the run.</param>
public sealed record RunStats(
    int Enumerated,
    int Copied,
    int Skipped,
    int Failed,
    long BytesCopied,
    TimeSpan Elapsed);
