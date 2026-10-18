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

        using var conn = new SqliteConnection($"Data Source={TransferJournal.ResolveDatabasePath(dir.Path)}");
        conn.Open(); conn.CreateFunction("picharbor_writer_version",()=>8L);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        long version = (long)cmd.ExecuteScalar()!;
        version.ShouldBe(TransferJournal.SchemaVersion);
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
    public void Batch_records_report_committed_progress_and_preserve_upsert_fields()
    {
        using var journal = TransferJournal.Open(dir.Path);
        var at = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 600)
            .Select(i => new GooglePhotosSyncRecord($"#media/{i}", $"key-{i}", at, "album", i)).ToArray();
        var progress = new List<int>();
        journal.RecordGooglePhotosSyncBatch(records, saved =>
        {
            // Observe from another connection: reported rows must already be committed.
            using var reader = TransferJournal.OpenReadOnly(dir.Path);
            reader.GetGooglePhotosSyncedDestPaths().Count.ShouldBe(saved);
            progress.Add(saved);
        }, TestContext.Current.CancellationToken);
        progress.ShouldBe([256, 512, 600]);
        journal.RecordGooglePhotosSyncBatch([records[0] with { MediaKey = null, AlbumName = null, FileSize = 42 }],
            cancellationToken: TestContext.Current.CancellationToken);
        using var connection = new SqliteConnection($"Data Source={TransferJournal.ResolveDatabasePath(dir.Path)};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_key,album_name,file_size FROM google_photos_sync_records WHERE dest_path='#media/0';";
        using var row = command.ExecuteReader();
        row.Read().ShouldBeTrue();
        row.IsDBNull(0).ShouldBeTrue();
        row.IsDBNull(1).ShouldBeTrue();
        row.GetInt64(2).ShouldBe(42);
        journal.GetGooglePhotosSyncedDestPaths().Count.ShouldBe(600);
    }

    [Fact]
    public void Batch_cancellation_preserves_committed_records_and_can_resume()
    {
        using var journal = TransferJournal.Open(dir.Path);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var records = Enumerable.Range(0, 600)
            .Select(i => new GooglePhotosSyncRecord($"photo/{i}", "key", DateTimeOffset.UtcNow, null, i)).ToArray();
        Should.Throw<OperationCanceledException>(() => journal.RecordGooglePhotosSyncBatch(records,
            _ => cancellation.Cancel(), cancellation.Token));
        journal.GetGooglePhotosSyncedDestPaths().Count.ShouldBe(256);
        journal.RecordGooglePhotosSyncBatch(records, cancellationToken: TestContext.Current.CancellationToken);
        journal.GetGooglePhotosSyncedDestPaths().Count.ShouldBe(600);
    }

    [Fact]
    public void Batch_failure_rolls_back_only_the_current_batch()
    {
        using var journal = TransferJournal.Open(dir.Path);
        var records = Enumerable.Range(0, 600)
            .Select(i => new GooglePhotosSyncRecord($"photo/{i}", "key", DateTimeOffset.UtcNow, null, i)).ToArray();
        records[300] = records[300] with { DestPath = "" };
        Should.Throw<ArgumentException>(() => journal.RecordGooglePhotosSyncBatch(records,
            cancellationToken: TestContext.Current.CancellationToken));
        journal.GetGooglePhotosSyncedDestPaths().Count.ShouldBe(256);
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
