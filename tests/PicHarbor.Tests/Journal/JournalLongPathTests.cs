using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Summary;
using PicHarbor.Core.Util;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Journal;

/// <summary>
/// Long-path (R6 / #39) coverage for the journal and summary. A deep destination whose
/// <c>get-and-see.db</c> path exceeds the legacy 260-char MAX_PATH must still open, migrate, and
/// round-trip — including the WAL/SHM sidecars SQLite derives from the DataSource — and the read-only
/// reopen (the <c>status</c> path) and the <c>summary.txt</c> write must work at the same depth,
/// rather than crashing with an unhandled <c>SQLITE_CANTOPEN</c>.
/// </summary>
public sealed class JournalLongPathTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Opens_migrates_and_round_trips_a_row_when_the_db_path_exceeds_260_chars()
    {
        string deepRoot = CreateDeepRoot();
        try
        {
            RemoteFile file = new("/DCIM/100APPLE/IMG_LONG.HEIC", 100, null);

            // Open creates + migrates the schema at a depth where the DB path is past MAX_PATH; the
            // MarkDone write exercises the -wal sidecar (WAL journal mode) at the same depth.
            using (TransferJournal journal = TransferJournal.Open(deepRoot))
            {
                journal.EnsurePending(file);
                journal.MarkInProgress(file.Path, file.Size, DateTimeOffset.UtcNow);
                journal.MarkDone(
                    file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_LONG.HEIC"),
                    MediaMetadata.Empty, DateTimeOffset.UtcNow);

                journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
                journal.ReadManifest().Count.ShouldBe(1);
            }

            // The DB really landed past MAX_PATH (only reachable via the \\?\ prefix) — without the fix
            // the Open above would have thrown SQLITE_CANTOPEN before any of the asserts ran.
            string databasePath = TransferJournal.ResolveDatabasePath(deepRoot);
            databasePath.Length.ShouldBeGreaterThan(260, "the test must exercise a DB path beyond MAX_PATH");
            File.Exists(LongPath.ToExtended(databasePath)).ShouldBeTrue();

            // Read-only reopen (the status verb) must find and open it at depth too.
            using TransferJournal readOnly = TransferJournal.OpenReadOnly(deepRoot);
            readOnly.ReadManifest().Count.ShouldBe(1);
        }
        finally
        {
            DeleteLongArtifacts(deepRoot);
        }
    }

    [Fact]
    public void Writes_summary_txt_when_the_destination_path_exceeds_260_chars()
    {
        string deepRoot = CreateDeepRoot();
        try
        {
            new SummaryWriter().Write(
                deepRoot,
                [],
                [],
                new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow);

            string summaryPath = Path.Combine(deepRoot, SummaryWriter.FileName);
            summaryPath.Length.ShouldBeGreaterThan(260, "the test must exercise a summary path beyond MAX_PATH");
            File.Exists(LongPath.ToExtended(summaryPath)).ShouldBeTrue();
            File.ReadAllText(LongPath.ToExtended(summaryPath)).ShouldContain("PicHarbor archive at");
        }
        finally
        {
            DeleteLongArtifacts(deepRoot);
        }
    }

    /// <summary>
    /// Creates a destination root under the temp dir that stays below MAX_PATH (so the non-prefixed
    /// directory creation inside <see cref="TransferJournal.Open"/> works) while
    /// <c>&lt;root&gt;/get-and-see.db</c> lands past 260 chars — the exact #39 boundary (root &lt; 260,
    /// DB path &gt; 260).
    /// </summary>
    private string CreateDeepRoot()
    {
        const int targetDatabasePathLength = 275;
        int suffixLength = 1 + TransferJournal.MetadataFolderName.Length + 1 + TransferJournal.DatabaseFileName.Length;
        int padLength = targetDatabasePathLength - suffixLength - temp.Path.Length - 1;
        padLength.ShouldBeGreaterThan(0, "the temp path is too long to build the long-path fixture");

        string deepRoot = Path.Combine(temp.Path, new string('d', padLength));
        deepRoot.Length.ShouldBeLessThan(260, "the root itself must stay under MAX_PATH so it can be created");
        Directory.CreateDirectory(deepRoot);
        return deepRoot;
    }

    private static void DeleteLongArtifacts(string deepRoot)
    {
        // Microsoft.Data.Sqlite pools connections, so the db file handle can outlive the journal's
        // Dispose; release the pool before deleting or the file is still "in use".
        SqliteConnection.ClearAllPools();

        // The DB, its -wal/-shm sidecars, and summary.txt exceed MAX_PATH, so the non-prefixed recursive
        // TempDirectory cleanup cannot remove them. Delete them via the \\?\ prefix first; the < 260
        // directories remain for TempDirectory to clean. Best-effort, like the rest of the codebase.
        string[] names =
        [
            TransferJournal.DatabaseFileName,
            TransferJournal.DatabaseFileName + "-wal",
            TransferJournal.DatabaseFileName + "-shm",
            Path.Combine(TransferJournal.MetadataFolderName, TransferJournal.DatabaseFileName),
            Path.Combine(TransferJournal.MetadataFolderName, TransferJournal.DatabaseFileName + "-wal"),
            Path.Combine(TransferJournal.MetadataFolderName, TransferJournal.DatabaseFileName + "-shm"),
            SummaryWriter.FileName,
        ];
        foreach (string name in names)
        {
            string extended = LongPath.ToExtended(Path.Combine(deepRoot, name));
            try
            {
                if (File.Exists(extended))
                {
                    File.Delete(extended);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of long-path test artifacts.
            }
        }
    }
}
