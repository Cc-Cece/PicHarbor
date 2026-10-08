using PicHarbor.Core.Transfer;

namespace PicHarbor.Core.Progress;

/// <summary>
/// An <see cref="IProgressReporter"/> implementation designed for GUI/event-driven consumers.
/// Periodically samples <see cref="TransferProgress.Snapshot"/> and notifies via an <see cref="IProgress{ProgressSnapshot}"/>
/// or <see cref="ProgressChanged"/> event.
/// </summary>
public sealed class ObservableProgressReporter : IProgressReporter
{
    private readonly IProgress<ProgressSnapshot>? progressTarget;
    private readonly TimeSpan sampleInterval;
    private readonly Timer? timer;
    private TransferProgress? transferProgress;
    private bool disposed;

    /// <summary>Occurs when progress is updated.</summary>
    public event EventHandler<ProgressSnapshot>? ProgressChanged;

    /// <summary>Occurs when an individual file completes.</summary>
    public event EventHandler<CopyResult>? FileCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservableProgressReporter"/> class.
    /// </summary>
    /// <param name="progressTarget">Optional standard .NET <see cref="IProgress{T}"/> target.</param>
    /// <param name="sampleInterval">Optional polling interval for snapshots (default 250ms).</param>
    public ObservableProgressReporter(IProgress<ProgressSnapshot>? progressTarget = null, TimeSpan? sampleInterval = null)
    {
        this.progressTarget = progressTarget;
        this.sampleInterval = sampleInterval ?? TimeSpan.FromMilliseconds(250);
        timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    public void Start(TransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        transferProgress = progress;

        // Push initial snapshot
        PublishSnapshot();

        // Start timer
        timer?.Change(sampleInterval, sampleInterval);
    }

    /// <inheritdoc/>
    public void OnFileCompleted(CopyResult result)
    {
        PublishSnapshot();
        FileCompleted?.Invoke(this, result);
    }

    private void OnTimerTick(object? state)
    {
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        if (transferProgress is null || disposed)
        {
            return;
        }

        ProgressSnapshot snapshot = transferProgress.Snapshot();
        progressTarget?.Report(snapshot);
        ProgressChanged?.Invoke(this, snapshot);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            timer?.Dispose();
            // Publish final snapshot before disposal
            PublishSnapshot();
        }
    }
}
