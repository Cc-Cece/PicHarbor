using PicHarbor.Core.Device;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.iPhone;

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
            AlbumMode = IPhoneAlbumMode.YearMonth
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
    public async Task ExportAsync_strictly_ignores_iPod_Photo_Cache()
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
            DeviceModel = "iPhone 15 Pro"
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        // iPod Photo Cache thumbnail file MUST be untouched!
        File.Exists(cacheFile).ShouldBeTrue();
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

    [Fact]
    public async Task ExportAsync_filters_by_DateRange_scope()
    {
        string file1 = Path.Combine(archiveDir.Path, "2023", "2023-01", "IMG_2023.JPG");
        string file2 = Path.Combine(archiveDir.Path, "2024", "2024-06", "IMG_2024.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(file1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(file2)!);
        File.WriteAllBytes(file1, new byte[] { 1 });
        File.WriteAllBytes(file2, new byte[] { 2 });

        using (var journal = TransferJournal.Open(archiveDir.Path))
        {
            var rf1 = new RemoteFile("/DCIM/IMG_2023.JPG", 1, new DateTimeOffset(2023, 1, 15, 0, 0, 0, TimeSpan.Zero));
            var rf2 = new RemoteFile("/DCIM/IMG_2024.JPG", 2, new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));

            journal.EnsurePending(rf1);
            journal.MarkDone(rf1.Path, rf1.Size, "2023/2023-01/IMG_2023.JPG", new MediaMetadata(rf1.ModifiedAt!.Value.DateTime, null, null, null, null), DateTimeOffset.UtcNow);

            journal.EnsurePending(rf2);
            journal.MarkDone(rf2.Path, rf2.Size, "2024/2024-06/IMG_2024.JPG", new MediaMetadata(rf2.ModifiedAt!.Value.DateTime, null, null, null, null), DateTimeOffset.UtcNow);

        }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            ScopeMode = IPhoneRestoreScopeMode.DateRange,
            DateFrom = new DateTime(2024, 1, 1),
            DateTo = new DateTime(2024, 12, 31)
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        result.CopiedCount.ShouldBe(1);
        string exportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        File.Exists(Path.Combine(exportRoot, "2024-06", "IMG_2024.JPG")).ShouldBeTrue();
        File.Exists(Path.Combine(exportRoot, "2023-01", "IMG_2023.JPG")).ShouldBeFalse();
    }

    [Fact]
    public async Task ExportAsync_filters_by_Subfolder_scope()
    {
        string file1 = Path.Combine(archiveDir.Path, "Vacation", "IMG_01.JPG");
        string file2 = Path.Combine(archiveDir.Path, "Work", "IMG_02.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(file1)!);
        Directory.CreateDirectory(Path.GetDirectoryName(file2)!);
        File.WriteAllBytes(file1, new byte[] { 1 });
        File.WriteAllBytes(file2, new byte[] { 2 });

        using (var journal = TransferJournal.Open(archiveDir.Path))
        {
            var rf1 = new RemoteFile("/DCIM/IMG_01.JPG", 1, null);
            var rf2 = new RemoteFile("/DCIM/IMG_02.JPG", 2, null);

            journal.EnsurePending(rf1);
            journal.MarkDone(rf1.Path, rf1.Size, "Vacation/IMG_01.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);

            journal.EnsurePending(rf2);
            journal.MarkDone(rf2.Path, rf2.Size, "Work/IMG_02.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            AlbumMode = IPhoneAlbumMode.Flat,
            ScopeMode = IPhoneRestoreScopeMode.Subfolder,
            SelectedSubfolders = new HashSet<string> { "Vacation" }
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        result.CopiedCount.ShouldBe(1);
        string exportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        File.Exists(Path.Combine(exportRoot, "IMG_01.JPG")).ShouldBeTrue();
        File.Exists(Path.Combine(exportRoot, "IMG_02.JPG")).ShouldBeFalse();
    }

    [Fact]
    public async Task ExportAsync_filters_by_ManualSelection_scope()
    {
        string file1 = Path.Combine(archiveDir.Path, "A.JPG");
        string file2 = Path.Combine(archiveDir.Path, "B.JPG");
        File.WriteAllBytes(file1, new byte[] { 1 });
        File.WriteAllBytes(file2, new byte[] { 2 });

        using (var journal = TransferJournal.Open(archiveDir.Path))
        {
            var rf1 = new RemoteFile("/DCIM/A.JPG", 1, null);
            var rf2 = new RemoteFile("/DCIM/B.JPG", 2, null);

            journal.EnsurePending(rf1);
            journal.MarkDone(rf1.Path, rf1.Size, "A.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);

            journal.EnsurePending(rf2);
            journal.MarkDone(rf2.Path, rf2.Size, "B.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);

            journal.AddManualSelection("iPhone 15 Pro", "A.JPG");
        }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "iPhone 15 Pro",
            AlbumMode = IPhoneAlbumMode.Flat,
            ScopeMode = IPhoneRestoreScopeMode.ManualSelection
        };

        var result = await IPhoneSyncEngine.ExportAsync(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        result.CopiedCount.ShouldBe(1);
        string exportRoot = Path.Combine(archiveDir.Path, ".AppleSync", "iPhone 15 Pro");
        File.Exists(Path.Combine(exportRoot, "A.JPG")).ShouldBeTrue();
        File.Exists(Path.Combine(exportRoot, "B.JPG")).ShouldBeFalse();
    }
}

