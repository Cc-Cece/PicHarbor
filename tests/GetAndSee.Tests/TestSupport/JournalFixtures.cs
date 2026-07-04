using GetAndSee.Core.Journal;
using Microsoft.Data.Sqlite;

namespace GetAndSee.Tests.TestSupport;

/// <summary>Helpers that shape an existing journal into an older on-disk schema for migration tests.</summary>
internal static class JournalFixtures
{
    /// <summary>
    /// Rewrites a closed v3 database back to its v2 (pre-<c>organize_scheme</c>) shape — drops the
    /// <c>settings</c> table and resets <c>PRAGMA user_version</c> to 2 — so re-opening exercises the
    /// v2→v3 migration exactly as a real v1.0/v2 archive would.
    /// </summary>
    /// <param name="destinationRoot">The archive root containing the database.</param>
    public static void DowngradeToV2(string destinationRoot)
    {
        string databasePath = Path.Combine(destinationRoot, TransferJournal.DatabaseFileName);
        // The journal pools its connection; release it so this out-of-band edit is not blocked.
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString);
        connection.Open();
        Execute(connection, "DROP TABLE IF EXISTS settings;");
        Execute(connection, "PRAGMA user_version = 2;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
