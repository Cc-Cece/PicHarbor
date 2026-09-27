using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Journal;

public sealed class JournalSchemaV3Tests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Brand_new_archive_has_no_recorded_scheme()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        // A fresh database has no files at migration time, so it is NOT stamped — the first copy records
        // the chosen/default scheme instead.
        journal.GetOrganizeScheme().ShouldBeNull();
    }

    [Theory]
    [InlineData(OrganizeScheme.Month)]
    [InlineData(OrganizeScheme.YearMonth)]
    [InlineData(OrganizeScheme.Year)]
    [InlineData(OrganizeScheme.Flat)]
    public void Set_and_get_organize_scheme_round_trips(OrganizeScheme scheme)
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetOrganizeScheme(scheme);

        journal.GetOrganizeScheme().ShouldBe(scheme);
    }

    [Fact]
    public void Set_organize_scheme_overwrites_a_previous_value()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.SetOrganizeScheme(OrganizeScheme.Year);
        journal.SetOrganizeScheme(OrganizeScheme.Flat);

        journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.Flat);
    }

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
    public void V2_archive_with_existing_files_is_stamped_year_month_and_stays_byte_stable()
    {
        // Seed an archive whose one done file lives in the original nested year-month layout.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_1.HEIC", 100, new DateTimeOffset(2024, 8, 15, 9, 0, 0, TimeSpan.Zero));
        string nestedDest = Path.Combine("2024", "2024-08", "IMG_1.HEIC");
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(file);
            journal.MarkDone(file.Path, file.Size, nestedDest, MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        // Simulate a real v1.0/v2 database (no organize_scheme recorded yet).
        JournalFixtures.DowngradeToV2(dir.Path);

        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            // The migration stamps the only pre-v3 layout, so the archive is recorded as nested.
            journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.YearMonth);

            // The existing file is untouched (still done — a resume would skip it, not re-copy it)...
            journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
            journal.ReadManifest().ShouldHaveSingleItem().DestPath.ShouldBe(nestedDest);

            // ...and it still resolves to the SAME path under the recorded scheme (byte-stable).
            new DateFolderOrganizer()
                .GetRelativeDestination(file, MediaMetadata.Empty, journal.GetOrganizeScheme()!.Value)
                .ShouldBe(nestedDest);
        }
    }

    [Fact]
    public void OpenReadOnly_on_a_pre_v3_database_returns_null_scheme_without_creating_settings()
    {
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(new RemoteFile("/DCIM/IMG_1.HEIC", 100, null));
        }

        JournalFixtures.DowngradeToV2(dir.Path);

        using (TransferJournal readOnly = TransferJournal.OpenReadOnly(dir.Path))
        {
            // A read-only open must not migrate; with no settings table it degrades to "no recorded scheme".
            readOnly.GetOrganizeScheme().ShouldBeNull();
        }

        // Confirm the read-only open created no settings table (still pre-v3 on disk).
        SettingsTableExists(dir.Path).ShouldBeFalse();
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

    private static bool SettingsTableExists(string root)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, TransferJournal.DatabaseFileName) }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'settings';";
        return (long)command.ExecuteScalar()! > 0;
    }
}
