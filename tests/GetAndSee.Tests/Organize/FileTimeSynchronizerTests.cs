using GetAndSee.Core.Organize;
using Xunit;

namespace GetAndSee.Tests.Organize;

public sealed class FileTimeSynchronizerTests
{
    [Fact]
    public void TrySyncFileTime_UpdatesLastWriteTimeAndCreationTime()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            var targetTime = new DateTimeOffset(2026, 9, 25, 14, 30, 0, TimeSpan.Zero);
            bool result = FileTimeSynchronizer.TrySyncFileTime(tempFile, targetTime, syncCreationTime: true);

            Assert.True(result);

            DateTime actualLastWrite = File.GetLastWriteTimeUtc(tempFile);
            DateTime actualCreation = File.GetCreationTimeUtc(tempFile);

            Assert.Equal(targetTime.UtcDateTime, actualLastWrite);
            Assert.Equal(targetTime.UtcDateTime, actualCreation);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void TrySyncFileTime_NonExistentFile_ReturnsFalse()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), $"non-existent-{Guid.NewGuid():N}.tmp");
        var targetTime = DateTimeOffset.UtcNow;

        bool result = FileTimeSynchronizer.TrySyncFileTime(nonExistent, targetTime);
        Assert.False(result);
    }

    [Fact]
    public async Task BatchSyncArchiveFileTimesAsync_ReportsProgress()
    {
        using var tempDir = new GetAndSee.Tests.TestSupport.TempDirectory();
        using (var journal = GetAndSee.Core.Journal.TransferJournal.Open(tempDir.Path))
        {
            var file = new GetAndSee.Core.Device.RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 100, DateTimeOffset.UtcNow);
            journal.EnsurePending(file);
            string relPath = Path.Combine("2026-09", "IMG_0001.HEIC");
            string fullPath = Path.Combine(tempDir.Path, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "test");

            var metadata = new MediaMetadata(new DateTime(2026, 9, 25, 12, 0, 0), null, null, null, null);
            journal.MarkDone(file.Path, file.Size, relPath, metadata, DateTimeOffset.UtcNow);
        }

        var progressList = new List<FileTimeSyncProgress>();
        var progress = new Progress<FileTimeSyncProgress>(p => progressList.Add(p));

        int updated = await FileTimeSynchronizer.BatchSyncArchiveFileTimesAsync(
            tempDir.Path,
            syncCreationTime: true,
            progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
    }
}
