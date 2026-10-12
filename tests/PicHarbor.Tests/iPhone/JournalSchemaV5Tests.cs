using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.iPhone;

public sealed class JournalSchemaV5Tests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Schema_version_is_5_after_open()
    {
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(new RemoteFile("/DCIM/IMG_1.HEIC", 100, null));
        }

        ReadUserVersion(dir.Path).ShouldBe(TransferJournal.SchemaVersion);
    }

    [Fact]
    public void IPhone_exported_files_can_be_upserted_queried_and_deleted()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        var rec1 = new IPhoneExportedFileRecord("2024/2024-05/IMG_0001.HEIC", "2024-05/IMG_0001.HEIC", "iPhone 15 Pro", DateTimeOffset.UtcNow, 1024);
        var rec2 = new IPhoneExportedFileRecord("2024/2024-05/IMG_0002.MOV", "2024-05/IMG_0002.MOV", "iPhone 15 Pro", DateTimeOffset.UtcNow, 2048);

        journal.UpsertIPhoneExportedFile(rec1);
        journal.UpsertIPhoneExportedFile(rec2);

        var list = journal.GetIPhoneExportedFiles("iPhone 15 Pro");
        list.Count.ShouldBe(2);
        list.ShouldContain(r => r.DestPath == rec1.DestPath);
        list.ShouldContain(r => r.DestPath == rec2.DestPath);

        // Delete rec1（「已导出」按设备记账，删除必须带设备身份）
        journal.DeleteIPhoneExportedFile(new IPhoneDeviceRef(null, "iPhone 15 Pro"), rec1.DestPath);

        var listAfterDelete = journal.GetIPhoneExportedFiles("iPhone 15 Pro");
        listAfterDelete.Count.ShouldBe(1);
        listAfterDelete[0].DestPath.ShouldBe(rec2.DestPath);
    }

    [Fact]
    public void Two_devices_with_the_same_display_name_do_not_collide()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        var deviceA = new IPhoneDeviceRef("00008120-AAAAAAAAAAAAAAAA", "iPhone 15 Pro");
        var deviceB = new IPhoneDeviceRef("00008120-BBBBBBBBBBBBBBBB", "iPhone 15 Pro");
        var now = DateTimeOffset.UtcNow;

        journal.UpsertIPhoneDevice(deviceA, @"D:\A\.AppleSync\iPhone 15 Pro", now);
        journal.UpsertIPhoneDevice(deviceB, @"D:\B\.AppleSync\iPhone 15 Pro", now);

        journal.GetIPhoneDevice(deviceA)!.SyncFolder.ShouldBe(@"D:\A\.AppleSync\iPhone 15 Pro");
        journal.GetIPhoneDevice(deviceB)!.SyncFolder.ShouldBe(@"D:\B\.AppleSync\iPhone 15 Pro");

        // 「已导出」按设备独立记账：A 导出过不代表 B 导出过。
        journal.UpsertIPhoneExportedFile(new IPhoneExportedFileRecord(
            "2024/IMG_1.HEIC", "IMG_1.HEIC", "iPhone 15 Pro", now, 100, null, deviceA.Udid));

        journal.GetIPhoneExportedFiles(deviceA).Count.ShouldBe(1);
        journal.GetIPhoneExportedFiles(deviceB).ShouldBeEmpty();
    }

    [Fact]
    public void Legacy_iphone_rows_are_migrated_to_udid_primary_key()
    {
        string dbPath = TransferJournal.ResolveDatabasePath(dir.Path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);

        // 用迁移前的旧 schema 建库并塞入 legacy 行（udid 为空）。
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE iphone_devices (
                    device_model TEXT PRIMARY KEY, sync_folder TEXT NOT NULL, last_exported TEXT NOT NULL);
                CREATE TABLE iphone_manual_selections (
                    device_model TEXT NOT NULL, dest_path TEXT NOT NULL, added_at TEXT NOT NULL,
                    is_autofilled INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (device_model, dest_path));
                CREATE TABLE iphone_exported_files (
                    dest_path TEXT PRIMARY KEY, exported_path TEXT NOT NULL, device_model TEXT NOT NULL,
                    exported_at TEXT NOT NULL, file_size INTEGER NOT NULL, sha256 TEXT);
                INSERT INTO iphone_devices VALUES ('iPhone 15 Pro', 'D:\A', '2026-01-01T00:00:00Z');
                INSERT INTO iphone_manual_selections VALUES ('iPhone 15 Pro', 'p/1.jpg', '2026-01-01T00:00:00Z', 0);
                INSERT INTO iphone_exported_files VALUES ('p/1.jpg', '1.jpg', 'iPhone 15 Pro', '2026-01-01T00:00:00Z', 10, NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        using TransferJournal journal = TransferJournal.Open(dir.Path);

        // 迁移后仍可按显示名（legacy 键）读到历史
        var legacyRef = new IPhoneDeviceRef(null, "iPhone 15 Pro");
        journal.GetIPhoneDevice(legacyRef)!.SyncFolder.ShouldBe(@"D:\A");
        journal.GetManualSelections(legacyRef).ShouldContain("p/1.jpg");
        journal.GetIPhoneExportedFiles(legacyRef).Count.ShouldBe(1);

        // 设备带真实 udid 连上后：legacy 行被改写，历史记录完整保留
        var realRef = new IPhoneDeviceRef("00008120-000204C12E92201E", "iPhone 15 Pro");
        journal.GetIPhoneDevice(realRef)!.SyncFolder.ShouldBe(@"D:\A");
        journal.GetManualSelections(realRef).ShouldContain("p/1.jpg");
        journal.GetIPhoneExportedFiles(realRef).Count.ShouldBe(1);
    }

    [Fact]
    public void IPhone_device_record_can_be_upserted_and_retrieved()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.GetIPhoneDevice("iPhone 15 Pro").ShouldBeNull();

        var now = DateTimeOffset.UtcNow;
        journal.UpsertIPhoneDevice("iPhone 15 Pro", @"D:\Photos\.AppleSync\iPhone 15 Pro", now);

        var dev = journal.GetIPhoneDevice("iPhone 15 Pro");
        dev.ShouldNotBeNull();
        dev.DeviceModel.ShouldBe("iPhone 15 Pro");
        dev.SyncFolder.ShouldBe(@"D:\Photos\.AppleSync\iPhone 15 Pro");
    }

    [Fact]
    public void IPhone_manual_selections_can_be_added_queried_batch_modified_and_cleared()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.GetManualSelections("iPhone 15 Pro").ShouldBeEmpty();

        journal.AddManualSelection("iPhone 15 Pro", "2024/2024-05/IMG_0001.HEIC");
        journal.AddManualSelection("iPhone 15 Pro", "2024/2024-05/IMG_0002.JPG");

        var selections = journal.GetManualSelections("iPhone 15 Pro");
        selections.Count.ShouldBe(2);
        selections.ShouldContain("2024/2024-05/IMG_0001.HEIC");
        selections.ShouldContain("2024/2024-05/IMG_0002.JPG");

        // Single Remove
        journal.RemoveManualSelection("iPhone 15 Pro", "2024/2024-05/IMG_0001.HEIC");
        journal.GetManualSelections("iPhone 15 Pro").Count.ShouldBe(1);

        // Batch Add
        journal.BatchAddManualSelections("iPhone 15 Pro", new[] { "2024/2024-05/IMG_0003.JPG", "2024/2024-05/IMG_0004.JPG" });
        journal.GetManualSelections("iPhone 15 Pro").Count.ShouldBe(3);

        // Batch Remove
        journal.BatchRemoveManualSelections("iPhone 15 Pro", new[] { "2024/2024-05/IMG_0002.JPG", "2024/2024-05/IMG_0003.JPG" });
        var remaining = journal.GetManualSelections("iPhone 15 Pro");
        remaining.Count.ShouldBe(1);
        remaining.ShouldContain("2024/2024-05/IMG_0004.JPG");

        // Clear All
        journal.ClearManualSelections("iPhone 15 Pro");
        journal.GetManualSelections("iPhone 15 Pro").ShouldBeEmpty();
    }

    [Fact]
    public void IsFileArchivedAndDone_returns_true_only_for_completed_transfers()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.EnsurePending(new RemoteFile("/DCIM/IMG_1.HEIC", 100, null));
        journal.IsFileArchivedAndDone("2024/2024-05/IMG_1.HEIC").ShouldBeFalse();

        journal.MarkDone("/DCIM/IMG_1.HEIC", 100, "2024/2024-05/IMG_1.HEIC", new PicHarbor.Core.Organize.MediaMetadata(null, null, null, null, null), DateTimeOffset.UtcNow);
        journal.IsFileArchivedAndDone("2024/2024-05/IMG_1.HEIC").ShouldBeTrue();
        journal.IsFileArchivedAndDone("2024/2024-05/NON_EXISTENT.HEIC").ShouldBeFalse();
    }



    private static long ReadUserVersion(string destinationRoot)
    {
        string dbPath = TransferJournal.ResolveDatabasePath(destinationRoot);
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
