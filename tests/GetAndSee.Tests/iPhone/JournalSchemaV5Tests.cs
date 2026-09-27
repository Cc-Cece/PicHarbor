using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.iPhone;

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

        ReadUserVersion(dir.Path).ShouldBe(5);
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

        // Delete rec1
        journal.DeleteIPhoneExportedFile(rec1.DestPath);

        var listAfterDelete = journal.GetIPhoneExportedFiles("iPhone 15 Pro");
        listAfterDelete.Count.ShouldBe(1);
        listAfterDelete[0].DestPath.ShouldBe(rec2.DestPath);
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


    private static long ReadUserVersion(string destinationRoot)
    {
        string dbPath = Path.Combine(destinationRoot, TransferJournal.DatabaseFileName);
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
