using Microsoft.Data.Sqlite;
using PicHarbor.Core.Android;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Android;

public sealed class AndroidSyncTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Open_journal_migrates_schema_to_v6()
    {
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(new RemoteFile("/DCIM/IMG_1001.JPG", 1024, null));
        }

        ReadUserVersion(dir.Path).ShouldBe(6);
    }

    [Fact]
    public void Android_manual_selections_can_be_managed()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        string devId = "Pixel_8";

        journal.AddAndroidManualSelection(devId, "2024-05/IMG_0001.JPG");
        journal.AddAndroidManualSelection(devId, "2024-05/IMG_0002.JPG");

        HashSet<string> selections = journal.GetAndroidManualSelections(devId);
        selections.Count.ShouldBe(2);
        selections.ShouldContain("2024-05/IMG_0001.JPG");
        selections.ShouldContain("2024-05/IMG_0002.JPG");

        journal.RemoveAndroidManualSelection(devId, "2024-05/IMG_0001.JPG");
        selections = journal.GetAndroidManualSelections(devId);
        selections.Count.ShouldBe(1);
        selections.ShouldContain("2024-05/IMG_0002.JPG");

        journal.ClearAndroidManualSelections(devId);
        journal.GetAndroidManualSelections(devId).ShouldBeEmpty();
    }

    [Fact]
    public void Android_sync_config_filters_entries_correctly()
    {
        var entry1 = new ManifestEntry("2024/01/IMG_1.JPG", 1000, "2024-01-15T10:00:00Z", "2024-01-15T10:00:00Z");
        var entry2 = new ManifestEntry("2024/06/IMG_2.JPG", 2000, "2024-06-20T10:00:00Z", "2024-06-20T10:00:00Z");

        // DateRange scope filter
        var dateConfig = new AndroidSyncConfig
        {
            ScopeMode = PicHarbor.Core.iPhone.IPhoneRestoreScopeMode.DateRange,
            DateFrom = new DateTime(2024, 6, 1),
            DateTo = new DateTime(2024, 6, 30)
        };
        dateConfig.IsEntryIncluded(entry1).ShouldBeFalse();
        dateConfig.IsEntryIncluded(entry2).ShouldBeTrue();

        // Subfolder scope filter
        var folderConfig = new AndroidSyncConfig
        {
            ScopeMode = PicHarbor.Core.iPhone.IPhoneRestoreScopeMode.Subfolder,
            SelectedSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024/06" }
        };
        folderConfig.IsEntryIncluded(entry1).ShouldBeFalse();
        folderConfig.IsEntryIncluded(entry2).ShouldBeTrue();

        // ManualSelection scope filter
        var manualConfig = new AndroidSyncConfig
        {
            ScopeMode = PicHarbor.Core.iPhone.IPhoneRestoreScopeMode.ManualSelection,
            ManualSelectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024/01/IMG_1.JPG" }
        };
        manualConfig.IsEntryIncluded(entry1).ShouldBeTrue();
        manualConfig.IsEntryIncluded(entry2).ShouldBeFalse();
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
