using GetAndSee.Core.Android;
using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Android;

public sealed class AndroidSyncTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Open_journal_migrates_schema_to_v5()
    {
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(new RemoteFile("/DCIM/IMG_1001.JPG", 1024, null));
        }

        ReadUserVersion(dir.Path).ShouldBe(5);
    }

    [Fact]
    public void Android_device_record_can_be_upserted_and_read()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.UpsertAndroidDevice("DEV-123456", "Pixel 8 Pro", DateTimeOffset.UtcNow);

        IReadOnlyList<AndroidDeviceRecord> devices = journal.ReadAndroidDevices();
        devices.ShouldHaveSingleItem();
        devices[0].DeviceId.ShouldBe("DEV-123456");
        devices[0].Name.ShouldBe("Pixel 8 Pro");
    }

    [Fact]
    public void Android_sync_records_can_be_added_and_queried()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        string deviceId = "DEV-999";

        journal.RecordAndroidSync("2024/2024-08/IMG_0001.JPG", deviceId, DateTimeOffset.UtcNow);
        journal.RecordAndroidSync("2024/2024-08/IMG_0002.JPG", deviceId, DateTimeOffset.UtcNow);

        HashSet<string> synced = journal.GetAndroidSyncedDestPaths(deviceId);
        synced.Count.ShouldBe(2);
        synced.ShouldContain("2024/2024-08/IMG_0001.JPG");
        synced.ShouldContain("2024/2024-08/IMG_0002.JPG");

        HashSet<string> syncedOtherDevice = journal.GetAndroidSyncedDestPaths("OTHER_DEV");
        syncedOtherDevice.ShouldBeEmpty();
    }

    private static long ReadUserVersion(string root)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, TransferJournal.DatabaseFileName) }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
