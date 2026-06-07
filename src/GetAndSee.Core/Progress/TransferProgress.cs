using GetAndSee.Core.Transfer;

namespace GetAndSee.Core.Progress;

/// <summary>
/// Thread-safe, UI-agnostic model of a transfer's live progress, including a rolling-window current
/// speed and an ETA (#10). The copy loop feeds it cheap updates; any reporter reads
/// <see cref="Snapshot"/> on its own schedule.
/// </summary>
/// <remarks>
/// <para>
/// The hot path (<see cref="RecordBytes"/>, called per chunk) is a single interlocked add — no locks,
/// no allocation, no device I/O — so the live readout cannot measurably slow the copy. Current speed
/// is derived by sampling the monotonic byte counter when <see cref="Snapshot"/> is called (the
/// dashboard does this at ≤4 Hz), which keeps the streaming thread free of timing work.
/// </para>
/// <para>
/// Time is read through an injectable <see cref="TimeProvider"/> so the speed/ETA math is
/// deterministically unit-testable with a fake clock.
/// </para>
/// </remarks>
public sealed class TransferProgress
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinSpanForCurrent = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider clock;
    private readonly long startTimestamp;
    private readonly Lock sync = new();
    private readonly Queue<(long Timestamp, long Bytes)> window = new();

    private long streamedBytes;
    private long skippedBytes;
    private long currentFileCopied;
    private long currentFileTotal;
    private string currentFileName = string.Empty;
    private int copiedFiles;
    private int skippedFiles;
    private int failedFiles;

    /// <summary>Creates a progress model for a run of the given size.</summary>
    /// <param name="totalFiles">Total files planned.</param>
    /// <param name="totalBytes">Total bytes planned.</param>
    /// <param name="clock">Time source; defaults to <see cref="TimeProvider.System"/>.</param>
    public TransferProgress(int totalFiles, long totalBytes, TimeProvider? clock = null)
    {
        TotalFiles = totalFiles;
        TotalBytes = totalBytes;
        this.clock = clock ?? TimeProvider.System;
        startTimestamp = this.clock.GetTimestamp();
    }

    /// <summary>Total files planned for this run.</summary>
    public int TotalFiles { get; }

    /// <summary>Total bytes planned for this run.</summary>
    public long TotalBytes { get; }

    /// <summary>Records bytes streamed for the current file (hot path — interlocked, lock-free).</summary>
    /// <param name="delta">Bytes streamed since the last call.</param>
    public void RecordBytes(long delta)
    {
        if (delta <= 0)
        {
            return;
        }

        Interlocked.Add(ref streamedBytes, delta);
        Interlocked.Add(ref currentFileCopied, delta);
    }

    /// <summary>Marks the start of a new file (resets the per-file byte counter).</summary>
    /// <param name="name">Display name of the file.</param>
    /// <param name="fileBytes">Total size of the file in bytes.</param>
    public void StartFile(string name, long fileBytes)
    {
        lock (sync)
        {
            currentFileName = name;
            Interlocked.Exchange(ref currentFileTotal, fileBytes);
            Interlocked.Exchange(ref currentFileCopied, 0);
        }
    }

    /// <summary>Accounts for a skipped (already-done) file's bytes in the overall progress.</summary>
    /// <param name="fileBytes">Size of the skipped file in bytes.</param>
    public void RecordSkippedBytes(long fileBytes)
    {
        if (fileBytes > 0)
        {
            Interlocked.Add(ref skippedBytes, fileBytes);
        }
    }

    /// <summary>Records a file's terminal status into the running counts.</summary>
    /// <param name="status">The file's copy outcome.</param>
    public void CompleteFile(CopyStatus status)
    {
        lock (sync)
        {
            switch (status)
            {
                case CopyStatus.Copied:
                    copiedFiles++;
                    break;
                case CopyStatus.Skipped:
                    skippedFiles++;
                    break;
                case CopyStatus.Failed:
                    failedFiles++;
                    break;
            }
        }
    }

    /// <summary>Computes a current snapshot, including rolling current speed, average speed, and ETA.</summary>
    /// <returns>An immutable <see cref="ProgressSnapshot"/>.</returns>
    public ProgressSnapshot Snapshot()
    {
        long now = clock.GetTimestamp();
        long streamed = Interlocked.Read(ref streamedBytes);
        long skipped = Interlocked.Read(ref skippedBytes);
        long processedBytes = streamed + skipped;
        double elapsedSeconds = clock.GetElapsedTime(startTimestamp, now).TotalSeconds;
        double average = elapsedSeconds > 0 ? streamed / elapsedSeconds : 0;

        double current;
        int copied, skippedCount, failed;
        long fileCopied, fileTotal;
        string fileName;
        lock (sync)
        {
            window.Enqueue((now, streamed));
            while (window.Count > 1 && clock.GetElapsedTime(window.Peek().Timestamp, now) > WindowDuration)
            {
                window.Dequeue();
            }

            (long oldestTimestamp, long oldestBytes) = window.Peek();
            double spanSeconds = clock.GetElapsedTime(oldestTimestamp, now).TotalSeconds;

            // Use the rolling window once it spans a meaningful interval; before that, fall back to the
            // cumulative average so a slow start does not produce a misleading 0 or a spike.
            current = spanSeconds >= MinSpanForCurrent.TotalSeconds
                ? Math.Max(0, (streamed - oldestBytes) / spanSeconds)
                : average;

            copied = copiedFiles;
            skippedCount = skippedFiles;
            failed = failedFiles;
            fileCopied = Interlocked.Read(ref currentFileCopied);
            fileTotal = Interlocked.Read(ref currentFileTotal);
            fileName = currentFileName;
        }

        long remaining = Math.Max(0, TotalBytes - processedBytes);
        double rate = current > 1 ? current : average;
        TimeSpan? eta = rate > 1 && remaining > 0 ? TimeSpan.FromSeconds(remaining / rate) : null;

        return new ProgressSnapshot(
            TotalFiles,
            copied,
            skippedCount,
            failed,
            TotalBytes,
            processedBytes,
            current,
            average,
            eta,
            fileName,
            fileCopied,
            fileTotal);
    }
}
