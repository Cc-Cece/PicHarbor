using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Journal;

public sealed class JournalSchemaV2Tests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Records_and_reads_devices_updating_last_seen()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.UpsertDevice("udid-1", "Sample iPhone", "iPhone13,3", DateTimeOffset.UtcNow);
        journal.UpsertDevice("udid-1", "Sample iPhone", "iPhone13,3", DateTimeOffset.UtcNow.AddMinutes(1));

        IReadOnlyList<DeviceRecord> devices = journal.ReadDevices();
        devices.Count.ShouldBe(1);
        devices[0].Name.ShouldBe("Sample iPhone");
        devices[0].Model.ShouldBe("iPhone13,3");
    }

    [Fact]
    public void Records_runs_and_summarizes_history()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        DateTimeOffset t0 = DateTimeOffset.Parse("2026-06-01T10:00:00Z");
        journal.RecordRun(t0, t0.AddMinutes(30), "copy", 100, 0, 0, 0, "udid-1");
        DateTimeOffset t1 = DateTimeOffset.Parse("2026-06-02T10:00:00Z");
        journal.RecordRun(t1, t1.AddMinutes(10), "copy", 5, 95, 0, 0, "udid-1");

        RunsSummary summary = journal.ReadRunsSummary();
        summary.TotalRuns.ShouldBe(2);
        summary.FirstRunAt!.Value.UtcDateTime.ShouldBe(t0.UtcDateTime);
        summary.LatestRunAt!.Value.UtcDateTime.ShouldBe(t1.UtcDateTime);
        summary.LastCopied.ShouldBe(5);
        summary.LastSkipped.ShouldBe(95);
        summary.LastElapsed.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Empty_history_reports_zero_runs_and_no_devices()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.ReadRunsSummary().TotalRuns.ShouldBe(0);
        journal.ReadDevices().ShouldBeEmpty();
    }

    [Fact]
    public void Status_style_summary_reflects_recorded_runs_and_devices()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        journal.UpsertDevice("udid-1", "iPhone", "iPhone13,3", DateTimeOffset.UtcNow);
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-06-01T10:00:00Z");
        journal.RecordRun(t0, t0.AddMinutes(30), "copy", 100, 0, 0, 0, "udid-1");

        // This is exactly what `status` renders from the journal (no device attached).
        string summary = new SummaryWriter().Build(
            dir.Path, journal.ReadManifest(), journal.ReadDevices(), journal.ReadRunsSummary(), DateTimeOffset.UtcNow);

        summary.ShouldContain("iPhone (iPhone13,3)");
        summary.ShouldContain("Runs: 1");
        summary.ShouldContain("100 copied");
    }

    [Fact]
    public void Migrates_pre_v2_database_in_place_preserving_files()
    {
        var file = new RemoteFile("/DCIM/IMG_1.HEIC", 100, null);
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(file);
            journal.MarkDone(
                file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
                MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        // Simulate a Sprint 1 (pre-v2) database: drop the new tables and reset the schema version.
        string dbPath = Path.Combine(dir.Path, TransferJournal.DatabaseFileName);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
        {
            connection.Open();
            Execute(connection, "DROP TABLE devices;");
            Execute(connection, "DROP TABLE runs;");
            Execute(connection, "PRAGMA user_version = 0;");
        }

        // Re-open → migration recreates the tables in place; existing files data is untouched.
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.ReadManifest().Count.ShouldBe(1);
            journal.ReadDevices().ShouldBeEmpty();
            Should.NotThrow(() =>
                journal.RecordRun(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "copy", 0, 1, 0, 0, null));
            journal.ReadRunsSummary().TotalRuns.ShouldBe(1);
        }
    }

    [Fact]
    public void OpenReadOnly_reads_existing_archive()
    {
        var file = new RemoteFile("/DCIM/IMG_1.HEIC", 100, null);
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.UpsertDevice("udid-1", "iPhone", "iPhone13,3", DateTimeOffset.UtcNow);
            journal.EnsurePending(file);
            journal.MarkDone(
                file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
                MediaMetadata.Empty, DateTimeOffset.UtcNow);
            journal.RecordRun(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "copy", 1, 0, 0, 0, "udid-1");
        }

        // status opens read-only (WAL database, cleanly closed) and reads without writing.
        using TransferJournal readOnly = TransferJournal.OpenReadOnly(dir.Path);
        readOnly.ReadManifest().Count.ShouldBe(1);
        readOnly.ReadDevices().Count.ShouldBe(1);
        readOnly.ReadRunsSummary().TotalRuns.ShouldBe(1);
    }

    [Fact]
    public void OpenReadOnly_tolerates_pre_v2_database_without_creating_tables()
    {
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(new RemoteFile("/DCIM/IMG_1.HEIC", 100, null));
        }

        // Simulate a Sprint 1 (pre-v2) database: drop the v2 tables.
        string dbPath = Path.Combine(dir.Path, TransferJournal.DatabaseFileName);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
        {
            connection.Open();
            Execute(connection, "DROP TABLE devices;");
            Execute(connection, "DROP TABLE runs;");
            Execute(connection, "PRAGMA user_version = 0;");
        }

        // Read-only open must not migrate; the readers degrade gracefully to empty/zero.
        using (TransferJournal readOnly = TransferJournal.OpenReadOnly(dir.Path))
        {
            readOnly.ReadDevices().ShouldBeEmpty();
            readOnly.ReadRunsSummary().TotalRuns.ShouldBe(0);
        }

        // Confirm the read-only open created no tables (still pre-v2 on disk).
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('devices', 'runs');";
            ((long)command.ExecuteScalar()!).ShouldBe(0);
        }
    }

    [Fact]
    public void OpenReadOnly_throws_when_no_database_exists()
    {
        using var empty = new TempDirectory();
        Should.Throw<FileNotFoundException>(() => TransferJournal.OpenReadOnly(empty.Path));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
