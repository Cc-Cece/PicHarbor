using GetAndSee.Core.Progress;
using GetAndSee.Core.Transfer;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Progress;

public sealed class TransferProgressTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void Average_is_streamed_bytes_over_elapsed_time()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(totalFiles: 10, totalBytes: 100_000, clock);

        progress.RecordBytes(2000);
        clock.Advance(TimeSpan.FromSeconds(2));

        progress.Snapshot().AverageBytesPerSecond.ShouldBe(1000d, 1d);
    }

    [Fact]
    public void Current_reflects_recent_window_rate()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(10, 1_000_000, clock);

        // Stream a steady 1000 B/s, sampling each second.
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(OneSecond);
            progress.RecordBytes(1000);
            progress.Snapshot();
        }

        progress.Snapshot().CurrentBytesPerSecond.ShouldBe(1000d, 50d);
    }

    [Fact]
    public void Eta_is_remaining_bytes_over_current_rate()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(10, 10_000, clock);

        clock.Advance(OneSecond);
        progress.RecordBytes(1000);
        progress.Snapshot();
        clock.Advance(OneSecond);
        progress.RecordBytes(1000);

        ProgressSnapshot snapshot = progress.Snapshot();

        // 8000 bytes remaining at ~1000 B/s → ~8s.
        snapshot.Eta.ShouldNotBeNull();
        snapshot.Eta.Value.TotalSeconds.ShouldBe(8d, 1d);
    }

    [Fact]
    public void Eta_is_null_before_any_bytes_move()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(10, 1000, clock);

        progress.Snapshot().Eta.ShouldBeNull();
    }

    [Fact]
    public void No_spike_before_minimum_window_span()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(10, 100_000, clock);

        // A burst within 100 ms is below the rolling-window minimum span; current must equal the
        // cumulative average, never a window-division-by-tiny-span spike.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        progress.RecordBytes(50_000);

        ProgressSnapshot snapshot = progress.Snapshot();
        snapshot.CurrentBytesPerSecond.ShouldBe(snapshot.AverageBytesPerSecond, 1d);
    }

    [Fact]
    public void Idle_gap_decays_current_toward_zero_without_spiking_or_going_negative()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(10, 1_000_000, clock);

        clock.Advance(OneSecond);
        progress.RecordBytes(3000);
        progress.Snapshot();
        clock.Advance(OneSecond);
        progress.RecordBytes(3000);
        progress.Snapshot();

        // Now go idle past the window length.
        clock.Advance(TimeSpan.FromSeconds(3));
        ProgressSnapshot snapshot = progress.Snapshot();

        snapshot.CurrentBytesPerSecond.ShouldBeGreaterThanOrEqualTo(0);
        snapshot.CurrentBytesPerSecond.ShouldBeLessThan(3000);
    }

    [Fact]
    public void Counts_and_processed_bytes_track_file_outcomes()
    {
        var clock = new FakeTimeProvider();
        var progress = new TransferProgress(3, 6000, clock);

        progress.StartFile("a", 1000);
        progress.RecordBytes(1000);
        progress.CompleteFile(CopyStatus.Copied);

        progress.StartFile("b", 2000);
        progress.RecordSkippedBytes(2000);
        progress.CompleteFile(CopyStatus.Skipped);

        progress.StartFile("c", 3000);
        progress.CompleteFile(CopyStatus.Failed);

        ProgressSnapshot snapshot = progress.Snapshot();
        snapshot.CopiedFiles.ShouldBe(1);
        snapshot.SkippedFiles.ShouldBe(1);
        snapshot.FailedFiles.ShouldBe(1);
        snapshot.ProcessedFiles.ShouldBe(3);
        snapshot.ProcessedBytes.ShouldBe(3000); // 1000 streamed + 2000 skipped
    }
}
