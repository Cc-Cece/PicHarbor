using PicHarbor.Core.Android;
using PicHarbor.Core.Device;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Scope;
using PicHarbor.Core.Transfer;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Transfer;

public sealed class V3SemanticRefactorTests : IDisposable
{
    private readonly TempDirectory tempDir = new();

    public void Dispose() => tempDir.Dispose();

    [Fact]
    public void FileEquivalenceResolver_correctly_evaluates_equivalence()
    {
        string srcPath = Path.Combine(tempDir.Path, "source.txt");
        string tgtPath = Path.Combine(tempDir.Path, "target.txt");
        string diffPath = Path.Combine(tempDir.Path, "diff.txt");

        File.WriteAllBytes(srcPath, new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllBytes(tgtPath, new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllBytes(diffPath, new byte[] { 9, 8, 7 }); // different size

        // 1. Same size & mtime => Equivalent
        var res1 = FileEquivalenceResolver.ResolveLocalFile(srcPath, tgtPath);
        res1.ShouldBe(FileEquivalenceResult.Equivalent);

        // 2. Different size => NotEquivalent
        var res2 = FileEquivalenceResolver.ResolveLocalFile(srcPath, diffPath);
        res2.ShouldBe(FileEquivalenceResult.NotEquivalent);

        // 3. Target missing => NotEquivalent
        var res3 = FileEquivalenceResolver.ResolveLocalFile(srcPath, Path.Combine(tempDir.Path, "nonexistent.txt"));
        res3.ShouldBe(FileEquivalenceResult.NotEquivalent);

        // 4. Same size but missing mtime & hash check disabled => Unknown
        var srcId = new FileIdentity(srcPath, 5, null);
        var tgtId = new FileIdentity(tgtPath, 5, null);
        var res4 = FileEquivalenceResolver.Resolve(srcId, tgtId, allowHashCheck: false);
        res4.ShouldBe(FileEquivalenceResult.Unknown);
        res4.ShouldNotBe(FileEquivalenceResult.Equivalent);
    }

    [Fact]
    public async Task Backup_recopies_file_if_deleted_from_target_archive()
    {
        string archivePath = Path.Combine(tempDir.Path, "Archive");
        Directory.CreateDirectory(archivePath);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0001.JPG", 100, now);

        // First run: mark done in journal and create target file
        using (var journal = TransferJournal.Open(archivePath))
        {
            journal.EnsurePending(file);
            journal.MarkDone(file.Path, file.Size, "2024/2024-05/IMG_0001.JPG", MediaMetadata.Empty, now);
        }

        string destFile = Path.Combine(archivePath, "2024", "2024-05", "IMG_0001.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        File.WriteAllBytes(destFile, new byte[100]);

        // Second run: file exists in target archive => skip!
        using (var journal = TransferJournal.Open(archivePath))
        {
            var sourceId = new FileIdentity(file.Path, file.Size, file.ModifiedAt);
            var targetInfo = new FileInfo(destFile);
            var targetId = new FileIdentity(destFile, targetInfo.Length, targetInfo.LastWriteTimeUtc);
            FileEquivalenceResolver.Resolve(sourceId, targetId).ShouldBe(FileEquivalenceResult.Equivalent);
        }

        // Delete target file from archive
        File.Delete(destFile);

        // Third run: target file deleted => FileEquivalenceResolver returns NotEquivalent => Needs copy!
        var targetInfoAfterDelete = new FileInfo(destFile);
        var targetIdAfterDelete = new FileIdentity(destFile, targetInfoAfterDelete.Exists ? targetInfoAfterDelete.Length : 0, null);
        FileEquivalenceResolver.Resolve(new FileIdentity(file.Path, file.Size, file.ModifiedAt), targetIdAfterDelete).ShouldBe(FileEquivalenceResult.NotEquivalent);
    }

    [Fact]
    public async Task iPhoneRestore_uses_CurrentTargetState_not_journal_history()
    {
        string archiveRoot = Path.Combine(tempDir.Path, "PCArchive");
        string srcFile = Path.Combine(archiveRoot, "2024", "2024-05", "IMG_100.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFile)!);
        File.WriteAllBytes(srcFile, new byte[] { 1, 2, 3, 4 });

        using (var journal = TransferJournal.Open(archiveRoot))
        {
            var rf = new RemoteFile("/DCIM/IMG_100.JPG", 4, DateTimeOffset.UtcNow);
            journal.EnsurePending(rf);
            journal.MarkDone(rf.Path, rf.Size, "2024/2024-05/IMG_100.JPG", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        var config = new IPhoneExportConfig
        {
            DeviceModel = "TestPhone",
            AlbumMode = IPhoneAlbumMode.Flat
        };

        // 1st Restore
        var res1 = await IPhoneSyncEngine.ExportAsync(archiveRoot, config, cancellationToken: TestContext.Current.CancellationToken);
        res1.CopiedCount.ShouldBe(1);
        res1.SkippedCount.ShouldBe(0);

        string syncDir = config.GetEffectiveExportPath(archiveRoot);
        string exportedFile = Path.Combine(syncDir, "IMG_100.JPG");
        File.Exists(exportedFile).ShouldBeTrue();

        // 2nd Restore: target file exists and matches => Skip
        var res2 = await IPhoneSyncEngine.ExportAsync(archiveRoot, config, cancellationToken: TestContext.Current.CancellationToken);
        res2.CopiedCount.ShouldBe(0);
        res2.SkippedCount.ShouldBe(1);

        // Delete exported file from sync folder
        File.Delete(exportedFile);

        // 3rd Restore: file deleted from sync folder => Restores again despite journal history!
        var res3 = await IPhoneSyncEngine.ExportAsync(archiveRoot, config, cancellationToken: TestContext.Current.CancellationToken);
        res3.CopiedCount.ShouldBe(1);
        res3.SkippedCount.ShouldBe(0);
        File.Exists(exportedFile).ShouldBeTrue();
    }

    [Fact]
    public void AndroidRestore_Default_vs_HistoricalIncremental_semantics()
    {
        string archiveRoot = Path.Combine(tempDir.Path, "AndroidTestArchive");
        Directory.CreateDirectory(archiveRoot);

        using var journal = TransferJournal.Open(archiveRoot);

        string deviceA = "Device_A";
        string deviceB = "Device_B";
        string relPath = "2024/2024-01/PHOTO.JPG";

        // Record history for Device A
        journal.UpsertAndroidDevice(deviceA, "Pixel A", DateTimeOffset.UtcNow);
        journal.RecordAndroidSync(relPath, deviceA, DateTimeOffset.UtcNow);

        // 1. Historical Incremental on Device A => Skips because deviceA history contains relPath
        var syncedA = journal.GetAndroidSyncedDestPaths(deviceA);
        syncedA.Contains(relPath).ShouldBeTrue();

        // 2. Historical Incremental on Device B => Does NOT skip because history is device-specific!
        var syncedB = journal.GetAndroidSyncedDestPaths(deviceB);
        syncedB.Contains(relPath).ShouldBeFalse();
    }
}
