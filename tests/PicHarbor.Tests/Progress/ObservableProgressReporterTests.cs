using PicHarbor.Core.Progress;
using Xunit;

namespace PicHarbor.Tests.Progress;

public sealed class ObservableProgressReporterTests
{
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public void Start_PublishesInitialSnapshot()
    {
        var progress = new TransferProgress(totalFiles: 10, totalBytes: 1000);
        ProgressSnapshot? receivedSnapshot = null;
        var progressTarget = new DirectProgress<ProgressSnapshot>(s => receivedSnapshot = s);

        using var reporter = new ObservableProgressReporter(progressTarget, TimeSpan.FromMilliseconds(50));
        reporter.Start(progress);

        Assert.NotNull(receivedSnapshot);
        Assert.Equal(10, receivedSnapshot.TotalFiles);
        Assert.Equal(1000, receivedSnapshot.TotalBytes);
    }

    [Fact]
    public void ProgressChangedEvent_FiresOnSnapshotPublish()
    {
        var progress = new TransferProgress(totalFiles: 5, totalBytes: 500);
        ProgressSnapshot? eventSnapshot = null;

        using var reporter = new ObservableProgressReporter(sampleInterval: TimeSpan.FromMilliseconds(50));
        reporter.ProgressChanged += (_, snapshot) => eventSnapshot = snapshot;
        reporter.Start(progress);

        Assert.NotNull(eventSnapshot);
        Assert.Equal(5, eventSnapshot.TotalFiles);
    }
}
