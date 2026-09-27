using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.iPhone;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.iPhone;

public sealed class IPhoneSyncEngineTests : IDisposable
{
    private readonly TempDirectory archiveDir = new();

    public void Dispose() => archiveDir.Dispose();

    [Fact]
    public async Task ExportAsync_exports_files_incrementally_and_preserves_live_photo_pairing()
    {
        // Setup archive files: HEIC + MOV (Live Photo pair)
        string imgPath = Path.Combine(archiveDir.Path, "2024", "2024-05", "IMG_0001.HEIC");
        string movPath = Path.Combine(archiveDir.Path, "2024", "2024-05", "IMG_0001.MOV");
        Directory.CreateDirectory(Path.GetDirectoryName(imgPath)!);

        File.WriteAllBytes(imgPath, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(movPath, new byte[] { 5, 6, 7, 8, 9 });

        DateTimeOffset captureDate = new(2024, 5, 20, 14, 30, 0, TimeSpan.Zero);

        using (var journal = TransferJournal.Open(archiveDir.Path))
        {
            var rf1 = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 4, captureDate);
            var rf2 = new RemoteFile("/DCIM/100APPLE/IMG_0001.MOV", 5, captureDate);

            journal.EnsurePending(rf1);
            journal.MarkDone(rf1.Path, rf1.Size, "2024/2024-05/IMG_0001.HEIC", new MediaMetadata(captureDate.DateTime, null, null, null, null), DateTimeOffset.UtcNow);

            journal.EnsurePending(rf2);
            journal.MarkDone(rf2.Path, rf2.Size, "2024/2024-05/IMG_0001.MOV", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            AlbumMode = IPhoneAlbumMode.YearMonth,
            EnableMirrorDelete = true
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        result.TotalArchivedCount.ShouldBe(2);
        result.CopiedCount.ShouldBe(2);
        result.SkippedCount.ShouldBe(0);

        string expectedExportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        File.Exists(Path.Combine(expectedExportRoot, "2024-05", "IMG_0001.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(expectedExportRoot, "2024-05", "IMG_0001.MOV")).ShouldBeTrue();

        // Run second export: should skip both files
        var secondResult = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);
        secondResult.CopiedCount.ShouldBe(0);
        secondResult.SkippedCount.ShouldBe(2);
    }

    [Fact]
    public async Task ExportAsync_strictly_ignores_iPod_Photo_Cache_during_mirror_delete()
    {
        // Create archive with 1 file
        string imgPath = Path.Combine(archiveDir.Path, "IMG_0002.JPG");
        File.WriteAllBytes(imgPath, new byte[] { 10, 20, 30 });

        using (var journal = TransferJournal.Open(archiveDir.Path))
        {
            var rf = new RemoteFile("/DCIM/100APPLE/IMG_0002.JPG", 3, null);
            journal.EnsurePending(rf);
            journal.MarkDone(rf.Path, rf.Size, "IMG_0002.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        string exportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        string cacheDir = Path.Combine(exportRoot, "iPod Photo Cache", "F00");
        Directory.CreateDirectory(cacheDir);

        string cacheFile = Path.Combine(cacheDir, "Thumbnail01.ithmb");
        File.WriteAllBytes(cacheFile, new byte[] { 99, 98, 97 });

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            EnableMirrorDelete = true
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        // iPod Photo Cache thumbnail file MUST be untouched!
        File.Exists(cacheFile).ShouldBeTrue();
    }

    [Fact]
    public async Task ExportAsync_deletes_orphaned_files_when_mirror_delete_enabled()
    {
        string exportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        string orphanFile = Path.Combine(exportRoot, "2023-01", "OldPhoto.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanFile)!);
        File.WriteAllBytes(orphanFile, new byte[] { 1, 1, 1 });

        // Database has 0 files
        using (var journal = TransferJournal.Open(archiveDir.Path)) { }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            EnableMirrorDelete = true
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        result.DeletedCount.ShouldBe(1);
        File.Exists(orphanFile).ShouldBeFalse();
    }

    [Fact]
    public async Task ExportAsync_supports_flat_album_mode()
    {
        string imgPath = Path.Combine(archiveDir.Path, "2024", "2024-05", "IMG_0003.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(imgPath)!);
        File.WriteAllBytes(imgPath, new byte[] { 7, 8, 9 });

        using (var journal = TransferJournal.Open(archiveDir.Path))
        {
            var rf = new RemoteFile("/DCIM/100APPLE/IMG_0003.JPG", 3, null);
            journal.EnsurePending(rf);
            journal.MarkDone(rf.Path, rf.Size, "2024/2024-05/IMG_0003.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            AlbumMode = IPhoneAlbumMode.Flat
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        string exportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        File.Exists(Path.Combine(exportRoot, "IMG_0003.JPG")).ShouldBeTrue();
    }
}
