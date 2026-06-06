using System.Globalization;
using GetAndSee.Core.Device;
using GetAndSee.Core.Organize;
using Microsoft.Data.Sqlite;

namespace GetAndSee.Core.Journal;

/// <summary>
/// SQLite-backed transfer journal and manifest, stored at <c>&lt;dest&gt;/get-and-see.db</c> (visible
/// at the destination root, not hidden — brainstorm Session 3).
/// </summary>
/// <remarks>
/// <para>
/// The same file is both the in-progress journal (per-file <see cref="FileState"/>, enabling resume)
/// and the long-term manifest. A file's identity is its <b>source path + source size</b> (R17), so a
/// rename on disk never causes a re-copy and two same-named files are not confused.
/// </para>
/// <para>
/// A <c>manifest</c> SQL view exposes the user-facing columns of completed files and can be queried
/// with any sqlite3 tool.
/// </para>
/// </remarks>
public sealed class TransferJournal : IDisposable
{
    /// <summary>The journal/manifest filename written at the destination root.</summary>
    public const string DatabaseFileName = "get-and-see.db";

    private readonly SqliteConnection connection;
    private bool disposed;

    private TransferJournal(SqliteConnection connection) => this.connection = connection;

    /// <summary>Absolute path to the SQLite database file backing this journal.</summary>
    public string DatabasePath { get; private init; } = string.Empty;

    /// <summary>
    /// Opens (creating if necessary) the journal at <c>&lt;destinationRoot&gt;/get-and-see.db</c> and
    /// ensures the schema and <c>manifest</c> view exist.
    /// </summary>
    /// <param name="destinationRoot">The destination root directory.</param>
    /// <returns>An open journal. The caller owns and disposes it.</returns>
    public static TransferJournal Open(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        System.IO.Directory.CreateDirectory(destinationRoot);

        string databasePath = Path.Combine(destinationRoot, DatabaseFileName);
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var journal = new TransferJournal(connection) { DatabasePath = databasePath };
        journal.InitializeSchema();
        return journal;
    }

    private void InitializeSchema()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS files (
                id                     INTEGER PRIMARY KEY,
                source_path            TEXT    NOT NULL,
                source_size            INTEGER NOT NULL,
                source_mtime           TEXT,
                dest_path              TEXT,
                state                  TEXT    NOT NULL,
                error_message          TEXT,
                started_at             TEXT,
                finished_at            TEXT,
                exif_datetime_original TEXT,
                gps_latitude           REAL,
                gps_longitude          REAL,
                camera_make            TEXT,
                camera_model           TEXT,
                copied_at              TEXT,
                sha256                 TEXT
            );
            """);
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_files_identity ON files (source_path, source_size);");
        Execute("CREATE INDEX IF NOT EXISTS ix_files_state ON files (state);");
        Execute(
            """
            CREATE VIEW IF NOT EXISTS manifest AS
            SELECT
                source_path,
                dest_path,
                source_size AS size_bytes,
                source_mtime,
                exif_datetime_original,
                gps_latitude,
                gps_longitude,
                camera_make,
                camera_model,
                copied_at,
                sha256
            FROM files
            WHERE state = 'done';
            """);
    }

    /// <summary>
    /// Ensures a <see cref="FileState.Pending"/> row exists for the file. If a row already exists
    /// (any state), it is left untouched so resume logic is preserved.
    /// </summary>
    /// <param name="file">The enumerated device file.</param>
    public void EnsurePending(RemoteFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using SqliteCommand command = CreateCommand(
            """
            INSERT OR IGNORE INTO files (source_path, source_size, source_mtime, state)
            VALUES ($path, $size, $mtime, 'pending');
            """);
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$mtime", (object?)IsoUtc(file.ModifiedAt) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the recorded state of a file, or <see langword="null"/> if it is not journaled.</summary>
    /// <param name="sourcePath">Device source path.</param>
    /// <param name="sourceSize">Source size in bytes.</param>
    /// <returns>The file's <see cref="FileState"/>, or <see langword="null"/>.</returns>
    public FileState? GetState(string sourcePath, long sourceSize)
    {
        using SqliteCommand command = CreateCommand(
            "SELECT state FROM files WHERE source_path = $path AND source_size = $size;");
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$size", sourceSize);
        return FileStateText.FromText(command.ExecuteScalar() as string);
    }

    /// <summary>Marks a file as <see cref="FileState.InProgress"/> and stamps its start time.</summary>
    /// <param name="sourcePath">Device source path.</param>
    /// <param name="sourceSize">Source size in bytes.</param>
    /// <param name="startedAt">UTC start time.</param>
    public void MarkInProgress(string sourcePath, long sourceSize, DateTimeOffset startedAt)
    {
        using SqliteCommand command = CreateCommand(
            """
            UPDATE files
            SET state = 'in_progress', started_at = $started, error_message = NULL
            WHERE source_path = $path AND source_size = $size;
            """);
        command.Parameters.AddWithValue("$started", IsoUtc(startedAt)!);
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$size", sourceSize);
        command.ExecuteNonQuery();
    }

    /// <summary>Marks a file <see cref="FileState.Done"/>, recording its destination path and metadata.</summary>
    /// <param name="sourcePath">Device source path.</param>
    /// <param name="sourceSize">Source size in bytes.</param>
    /// <param name="relativeDestPath">Destination path relative to the destination root.</param>
    /// <param name="metadata">Extracted media metadata.</param>
    /// <param name="copiedAt">UTC completion time.</param>
    /// <param name="sha256">Optional SHA-256 hex digest (null unless hash verification ran).</param>
    public void MarkDone(
        string sourcePath,
        long sourceSize,
        string relativeDestPath,
        MediaMetadata metadata,
        DateTimeOffset copiedAt,
        string? sha256 = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        using SqliteCommand command = CreateCommand(
            """
            UPDATE files SET
                state = 'done',
                dest_path = $dest,
                exif_datetime_original = $exif,
                gps_latitude = $lat,
                gps_longitude = $lon,
                camera_make = $make,
                camera_model = $model,
                copied_at = $copied,
                finished_at = $copied,
                sha256 = $sha,
                error_message = NULL
            WHERE source_path = $path AND source_size = $size;
            """);
        command.Parameters.AddWithValue("$dest", relativeDestPath);
        command.Parameters.AddWithValue("$exif", (object?)IsoWall(metadata.DateTimeOriginal) ?? DBNull.Value);
        command.Parameters.AddWithValue("$lat", (object?)metadata.GpsLatitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$lon", (object?)metadata.GpsLongitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$make", (object?)metadata.CameraMake ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)metadata.CameraModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$copied", IsoUtc(copiedAt)!);
        command.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$size", sourceSize);
        command.ExecuteNonQuery();
    }

    /// <summary>Marks a file <see cref="FileState.Failed"/> with an error message.</summary>
    /// <param name="sourcePath">Device source path.</param>
    /// <param name="sourceSize">Source size in bytes.</param>
    /// <param name="error">A short failure description.</param>
    /// <param name="finishedAt">UTC time of failure.</param>
    public void MarkFailed(string sourcePath, long sourceSize, string error, DateTimeOffset finishedAt)
    {
        using SqliteCommand command = CreateCommand(
            """
            UPDATE files
            SET state = 'failed', error_message = $error, finished_at = $finished
            WHERE source_path = $path AND source_size = $size;
            """);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$finished", IsoUtc(finishedAt)!);
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$size", sourceSize);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the set of destination paths already recorded in the journal (case-insensitive),
    /// used by the copier to disambiguate filename collisions across runs.
    /// </summary>
    /// <returns>A case-insensitive set of relative destination paths.</returns>
    public IReadOnlySet<string> GetUsedDestPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM files WHERE dest_path IS NOT NULL;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(reader.GetString(0));
            }
        }

        return result;
    }

    /// <summary>Counts files in each state.</summary>
    /// <returns>A snapshot of per-state counts.</returns>
    public JournalCounts CountByState()
    {
        long pending = 0;
        long inProgress = 0;
        long done = 0;
        long failed = 0;

        using SqliteCommand command = CreateCommand(
            "SELECT state, COUNT(*) FROM files GROUP BY state;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            long count = reader.GetInt64(1);
            switch (FileStateText.FromText(reader.GetString(0)))
            {
                case FileState.Pending: pending = count; break;
                case FileState.InProgress: inProgress = count; break;
                case FileState.Done: done = count; break;
                case FileState.Failed: failed = count; break;
            }
        }

        return new JournalCounts(pending, inProgress, done, failed);
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    private SqliteCommand CreateCommand(string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static string? IsoUtc(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? IsoWall(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Closes the underlying SQLite connection.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        connection.Dispose();
    }
}

/// <summary>Per-state file counts from the journal.</summary>
/// <param name="Pending">Files not yet started.</param>
/// <param name="InProgress">Files mid-copy.</param>
/// <param name="Done">Files completed and verified.</param>
/// <param name="Failed">Files that failed and may be retried.</param>
public sealed record JournalCounts(long Pending, long InProgress, long Done, long Failed);
