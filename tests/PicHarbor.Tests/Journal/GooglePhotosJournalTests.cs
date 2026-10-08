using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Journal;

public sealed class GooglePhotosJournalTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Schema_version_is_6_after_open()
    {
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(new RemoteFile("/DCIM/IMG_1.HEIC", 100, null));
        }

        using var conn = new SqliteConnection($"Data Source={System.IO.Path.Combine(dir.Path, TransferJournal.DatabaseFileName)}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        long version = (long)cmd.ExecuteScalar()!;
        version.ShouldBe(6);
    }

    [Fact]
    public void GooglePhotos_sync_records_can_be_saved_and_retrieved()
    {
        using var journal = TransferJournal.Open(dir.Path);

        journal.RecordGooglePhotosSync("2026/01/photo1.jpg", "mk_test_12345", DateTimeOffset.UtcNow, "album_abc", 1024);

        var syncedPaths = journal.GetGooglePhotosSyncedDestPaths();
        syncedPaths.Count.ShouldBe(1);
        syncedPaths.ShouldContain("2026/01/photo1.jpg");
    }

    [Fact]
    public void GooglePhotos_manual_selections_can_be_added_removed_and_cleared()
    {
        using var journal = TransferJournal.Open(dir.Path);

        journal.BatchAddGooglePhotosManualSelections(new[] { "folder/a.jpg", "folder/b.jpg", "c.png" });

        var selections = journal.GetGooglePhotosManualSelections();
        selections.Count.ShouldBe(3);
        selections.ShouldContain("folder/a.jpg");
        selections.ShouldContain("folder/b.jpg");
        selections.ShouldContain("c.png");

        journal.BatchRemoveGooglePhotosManualSelections(new[] { "folder/a.jpg" });
        selections = journal.GetGooglePhotosManualSelections();
        selections.Count.ShouldBe(2);
        selections.ShouldNotContain("folder/a.jpg");

        journal.ClearGooglePhotosManualSelections();
        selections = journal.GetGooglePhotosManualSelections();
        selections.ShouldBeEmpty();
    }
}
