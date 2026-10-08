using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Search;
using Xunit;

namespace PicHarbor.Tests.Search;

public sealed class ArchiveRepositoryTests : IDisposable
{
    private readonly string tempDir;

    public ArchiveRepositoryTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"gas-repo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(tempDir))
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup in test teardown
            }
        }
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectAggregates()
    {
        using (var journal = TransferJournal.Open(tempDir))
        {
            journal.UpsertDevice("test-udid-123", "iPhone 15", "iPhone16,1", DateTimeOffset.UtcNow);

            var remoteFile1 = new RemoteFile("/DCIM/100APPLE/IMG_0001.JPG", 1024, DateTimeOffset.UtcNow);
            var remoteFile2 = new RemoteFile("/DCIM/100APPLE/IMG_0002.MOV", 2048, DateTimeOffset.UtcNow);

            journal.EnsurePending(remoteFile1);
            journal.EnsurePending(remoteFile2);

            journal.MarkDone(remoteFile1.Path, remoteFile1.Size, "2026-09/IMG_0001.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);
            journal.MarkDone(remoteFile2.Path, remoteFile2.Size, "2026-09/IMG_0002.MOV", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        ArchiveSummaryStats stats = await ArchiveRepository.GetStatsAsync(tempDir, TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.TotalFiles);
        Assert.Equal(3072, stats.TotalBytes);
        Assert.Equal(1, stats.PhotosCount);
        Assert.Equal(1, stats.VideosCount);
        Assert.Single(stats.Devices);
        Assert.Equal("iPhone 15", stats.Devices[0].Name);
    }

    [Fact]
    public async Task SearchAsync_ReturnsFilteredHits()
    {
        using (var journal = TransferJournal.Open(tempDir))
        {
            var remoteFile = new RemoteFile("/DCIM/100APPLE/IMG_0001.JPG", 1024, DateTimeOffset.UtcNow);
            journal.EnsurePending(remoteFile);
            journal.MarkDone(remoteFile.Path, remoteFile.Size, "2026-09/IMG_0001.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        IReadOnlyList<MediaSearchHit> hits = await ArchiveRepository.SearchAsync(
            tempDir,
            new MediaSearchCriteria(Type: MediaType.Photo),
            TestContext.Current.CancellationToken);

        Assert.Single(hits);
        Assert.Equal("2026-09/IMG_0001.JPG", hits[0].RelativePath);
    }
}
