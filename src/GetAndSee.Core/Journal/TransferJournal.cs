using System.Globalization;
using GetAndSee.Core.Device;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Util;
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

    /// <summary>Current journal schema version (bumped when tables are added; migrated in place).</summary>
    public const long SchemaVersion = 3;

    /// <summary>The <c>settings</c> key under which an archive's <c>organize_scheme</c> token is stored.</summary>
    private const string OrganizeSchemeKey = "organize_scheme";

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
        // Create the destination root through the \\?\ long-path form so a deep root is created on a
        // stock Windows machine (LongPathsEnabled=0); the DataSource below is already prefixed (R6 / #39).
        System.IO.Directory.CreateDirectory(LongPath.ToExtended(destinationRoot));

        string databasePath = Path.Combine(destinationRoot, DatabaseFileName);
        // Route the SQLite DataSource through the \\?\ long-path prefix so a deep destination root —
        // and the -wal/-shm sidecars SQLite derives from it — can exceed the legacy MAX_PATH limit,
        // exactly as the media copy path already does (R6 / #39). DatabasePath keeps the clean,
        // user-facing form for display and diagnostics.
        var builder = new SqliteConnectionStringBuilder { DataSource = LongPath.ToExtended(databasePath) };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var journal = new TransferJournal(connection) { DatabasePath = databasePath };
        journal.InitializeSchema();
        return journal;
    }

    /// <summary>
    /// Opens an existing journal at <c>&lt;destinationRoot&gt;/get-and-see.db</c> <b>read-only</b> for
    /// inspection (e.g. the <c>status</c> verb). The database is never created, migrated, or written
    /// to — the connection uses <see cref="SqliteOpenMode.ReadOnly"/>.
    /// </summary>
    /// <param name="destinationRoot">The destination root directory containing an existing database.</param>
    /// <returns>An open read-only journal. The caller owns and disposes it.</returns>
    /// <exception cref="FileNotFoundException">Thrown when no database exists at the destination.</exception>
    public static TransferJournal OpenReadOnly(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string databasePath = Path.Combine(destinationRoot, DatabaseFileName);
        // Probe and open through the \\?\ long-path prefix: a non-prefixed File.Exists on a > 260-char
        // path silently returns false (so status would wrongly report "no archive"), and the SQLite open
        // would otherwise crash on a deep destination (R6 / #39).
        string extendedDatabasePath = LongPath.ToExtended(databasePath);
        if (!File.Exists(extendedDatabasePath))
        {
            throw new FileNotFoundException("No get-and-see database found.", databasePath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = extendedDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        // No InitializeSchema/Migrate: a read-only inspection must not create or alter anything.
        return new TransferJournal(connection) { DatabasePath = databasePath };
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

        Migrate();
    }

    /// <summary>
    /// Applies additive schema migrations in place using <c>PRAGMA user_version</c>. v1 (version 0/1) gains
    /// the <c>devices</c> and <c>runs</c> tables; v2 gains the <c>settings</c> table. Existing <c>files</c>
    /// data is never touched.
    /// </summary>
    /// <remarks>
    /// The v2→v3 step is the migration-safety crux: a <b>pre-existing</b> archive (any file already
    /// journaled) is stamped <c>organize_scheme = 'year-month'</c> — the only layout that existed before
    /// v3 — so its files keep resolving to their existing paths and a resume is byte-stable. A brand-new
    /// database (no <c>files</c> rows yet at migration time) is left unstamped, so the first <c>copy</c>
    /// records the chosen (or default) scheme instead.
    /// </remarks>
    private void Migrate()
    {
        long version = QueryUserVersion();
        if (version >= SchemaVersion)
        {
            return;
        }

        if (version < 2)
        {
            Execute(
                """
                CREATE TABLE IF NOT EXISTS devices (
                    udid       TEXT PRIMARY KEY,
                    name       TEXT,
                    model      TEXT,
                    first_seen TEXT,
                    last_seen  TEXT
                );
                """);
            Execute(
                """
                CREATE TABLE IF NOT EXISTS runs (
                    id          INTEGER PRIMARY KEY,
                    started_at  TEXT NOT NULL,
                    finished_at TEXT,
                    command     TEXT,
                    copied      INTEGER NOT NULL DEFAULT 0,
                    skipped     INTEGER NOT NULL DEFAULT 0,
                    failed      INTEGER NOT NULL DEFAULT 0,
                    exit_code   INTEGER,
                    device_udid TEXT
                );
                """);
        }

        if (version < 3)
        {
            Execute(
                """
                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);

            // Byte-stability for existing nested archives: stamp the only pre-v3 layout so resume never
            // re-shuffles already-copied files. A fresh database has no files here and stays unstamped.
            if (FilesTableHasAnyRow())
            {
                using SqliteCommand stamp = CreateCommand(
                    "INSERT OR IGNORE INTO settings (key, value) VALUES ($key, $value);");
                stamp.Parameters.AddWithValue("$key", OrganizeSchemeKey);
                stamp.Parameters.AddWithValue("$value", OrganizeSchemes.YearMonthToken);
                stamp.ExecuteNonQuery();
            }
        }

        Execute($"PRAGMA user_version = {SchemaVersion};");
    }

    private bool FilesTableHasAnyRow()
    {
        using SqliteCommand command = CreateCommand("SELECT EXISTS(SELECT 1 FROM files);");
        return command.ExecuteScalar() is long present && present != 0;
    }

    /// <summary>
    /// Returns the archive's recorded folder-layout scheme, or <see langword="null"/> when none is recorded
    /// (a brand-new archive, or a pre-v3 database opened read-only without the <c>settings</c> table).
    /// </summary>
    /// <returns>The recorded <see cref="OrganizeScheme"/>, or <see langword="null"/>.</returns>
    public OrganizeScheme? GetOrganizeScheme()
    {
        if (!TableExists("settings"))
        {
            // A pre-v3 database opened read-only has no settings table yet.
            return null;
        }

        using SqliteCommand command = CreateCommand("SELECT value FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", OrganizeSchemeKey);
        return OrganizeSchemes.TryParse(command.ExecuteScalar() as string);
    }

    /// <summary>Records the archive's folder-layout scheme, replacing any previously recorded value.</summary>
    /// <param name="scheme">The scheme to persist.</param>
    public void SetOrganizeScheme(OrganizeScheme scheme)
    {
        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """);
        command.Parameters.AddWithValue("$key", OrganizeSchemeKey);
        command.Parameters.AddWithValue("$value", OrganizeSchemes.ToToken(scheme));
        command.ExecuteNonQuery();
    }

    private long QueryUserVersion()
    {
        using SqliteCommand command = CreateCommand("PRAGMA user_version;");
        return command.ExecuteScalar() is long value ? value : 0;
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

    /// <summary>
    /// Reads the user-facing rows of the <c>manifest</c> view (completed files only) for summary
    /// generation.
    /// </summary>
    /// <returns>All completed file entries.</returns>
    public IReadOnlyList<ManifestEntry> ReadManifest()
    {
        var rows = new List<ManifestEntry>();
        using SqliteCommand command = CreateCommand(
            "SELECT dest_path, size_bytes, exif_datetime_original, source_mtime FROM manifest;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ManifestEntry(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Reads the columns the <c>search</c> command filters on from the <c>manifest</c> view (completed
    /// files only): the relative destination path, a resolved capture date (EXIF <c>DateTimeOriginal</c>
    /// when present, else the source modified time), size, camera, and GPS. Read-only.
    /// </summary>
    /// <returns>One row per completed file.</returns>
    public IReadOnlyList<ManifestSearchRow> ReadSearchRows()
    {
        List<ManifestSearchRow> rows = new();
        using SqliteCommand command = CreateCommand(
            """
            SELECT dest_path, size_bytes, exif_datetime_original, source_mtime,
                   camera_make, camera_model, gps_latitude, gps_longitude
            FROM manifest;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? exif = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? mtime = reader.IsDBNull(3) ? null : reader.GetString(3);
            rows.Add(new ManifestSearchRow(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                ParseIsoOrNull(exif) ?? ParseIsoOrNull(mtime),
                reader.GetInt64(1),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7)));
        }

        return rows;
    }

    /// <summary>Inserts or updates the device's record, refreshing its last-seen time (R: multi-device story).</summary>
    /// <param name="udid">Device UDID.</param>
    /// <param name="name">Device name, if known.</param>
    /// <param name="model">Device product type/model, if known.</param>
    /// <param name="seenAt">UTC time the device was seen.</param>
    public void UpsertDevice(string udid, string? name, string? model, DateTimeOffset seenAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO devices (udid, name, model, first_seen, last_seen)
            VALUES ($udid, $name, $model, $seen, $seen)
            ON CONFLICT(udid) DO UPDATE SET
                name = COALESCE(excluded.name, devices.name),
                model = COALESCE(excluded.model, devices.model),
                last_seen = excluded.last_seen;
            """);
        command.Parameters.AddWithValue("$udid", udid);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", IsoUtc(seenAt)!);
        command.ExecuteNonQuery();
    }

    /// <summary>Records a completed (or stopped) run for the archive's audit trail.</summary>
    /// <param name="startedAt">UTC run start.</param>
    /// <param name="finishedAt">UTC run end.</param>
    /// <param name="command">The command line / verb that ran.</param>
    /// <param name="copied">Files copied this run.</param>
    /// <param name="skipped">Files skipped this run.</param>
    /// <param name="failed">Files failed this run.</param>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="deviceUdid">UDID of the device used, if any.</param>
    public void RecordRun(
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        string command,
        int copied,
        int skipped,
        int failed,
        int exitCode,
        string? deviceUdid)
    {
        using SqliteCommand cmd = CreateCommand(
            """
            INSERT INTO runs (started_at, finished_at, command, copied, skipped, failed, exit_code, device_udid)
            VALUES ($started, $finished, $command, $copied, $skipped, $failed, $exit, $udid);
            """);
        cmd.Parameters.AddWithValue("$started", IsoUtc(startedAt)!);
        cmd.Parameters.AddWithValue("$finished", IsoUtc(finishedAt)!);
        cmd.Parameters.AddWithValue("$command", command);
        cmd.Parameters.AddWithValue("$copied", copied);
        cmd.Parameters.AddWithValue("$skipped", skipped);
        cmd.Parameters.AddWithValue("$failed", failed);
        cmd.Parameters.AddWithValue("$exit", exitCode);
        cmd.Parameters.AddWithValue("$udid", (object?)deviceUdid ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads all known devices, most-recently-seen first.</summary>
    /// <returns>The device records.</returns>
    public IReadOnlyList<DeviceRecord> ReadDevices()
    {
        var rows = new List<DeviceRecord>();
        if (!TableExists("devices"))
        {
            // A pre-v2 database opened read-only has no devices table yet.
            return rows;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT udid, name, model FROM devices ORDER BY last_seen DESC;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DeviceRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>Summarizes the run history (count, first/latest run, and the latest run's outcome).</summary>
    /// <returns>A <see cref="RunsSummary"/>; <see cref="RunsSummary.TotalRuns"/> is 0 when none recorded.</returns>
    public RunsSummary ReadRunsSummary()
    {
        if (!TableExists("runs"))
        {
            // A pre-v2 database opened read-only has no runs table yet.
            return new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero);
        }

        int total = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? latest = null;
        using (SqliteCommand command = CreateCommand("SELECT COUNT(*), MIN(started_at), MAX(started_at) FROM runs;"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                total = (int)reader.GetInt64(0);
                first = reader.IsDBNull(1) ? null : ParseIso(reader.GetString(1));
                latest = reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2));
            }
        }

        if (total == 0)
        {
            return new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero);
        }

        int copied = 0, skipped = 0, failed = 0;
        TimeSpan elapsed = TimeSpan.Zero;
        using (SqliteCommand command = CreateCommand(
            "SELECT copied, skipped, failed, started_at, finished_at FROM runs ORDER BY id DESC LIMIT 1;"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                copied = (int)reader.GetInt64(0);
                skipped = (int)reader.GetInt64(1);
                failed = (int)reader.GetInt64(2);
                DateTimeOffset? started = reader.IsDBNull(3) ? null : ParseIso(reader.GetString(3));
                DateTimeOffset? finished = reader.IsDBNull(4) ? null : ParseIso(reader.GetString(4));
                if (started is not null && finished is not null)
                {
                    elapsed = finished.Value - started.Value;
                }
            }
        }

        return new RunsSummary(total, first, latest, copied, skipped, failed, elapsed);
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

    private bool TableExists(string name)
    {
        using SqliteCommand command = CreateCommand(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;");
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static string? IsoUtc(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? IsoWall(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseIso(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ParseIsoOrNull(string? value) =>
        value is null ? null : ParseIso(value);

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

/// <summary>A single completed-file row from the <c>manifest</c> view.</summary>
/// <param name="DestPath">Destination path relative to the root.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="ExifDateTimeOriginalIso">EXIF capture time (ISO 8601), or <see langword="null"/>.</param>
/// <param name="SourceMtimeIso">Source modified time (ISO 8601 UTC), or <see langword="null"/>.</param>
public sealed record ManifestEntry(
    string DestPath,
    long SizeBytes,
    string? ExifDateTimeOriginalIso,
    string? SourceMtimeIso);

/// <summary>A completed-file row projected for the <c>search</c> command's filters.</summary>
/// <param name="RelativePath">Destination path relative to the archive root.</param>
/// <param name="CapturedAt">Resolved capture date (EXIF original, else source mtime), or <see langword="null"/>.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="CameraMake">EXIF camera make, or <see langword="null"/>.</param>
/// <param name="CameraModel">EXIF camera model, or <see langword="null"/>.</param>
/// <param name="GpsLatitude">EXIF GPS latitude, or <see langword="null"/>.</param>
/// <param name="GpsLongitude">EXIF GPS longitude, or <see langword="null"/>.</param>
public sealed record ManifestSearchRow(
    string RelativePath,
    DateTimeOffset? CapturedAt,
    long SizeBytes,
    string? CameraMake,
    string? CameraModel,
    double? GpsLatitude,
    double? GpsLongitude);

/// <summary>A device recorded in the journal's <c>devices</c> table.</summary>
/// <param name="Udid">Device UDID.</param>
/// <param name="Name">Device name, if known.</param>
/// <param name="Model">Device product type/model, if known.</param>
public sealed record DeviceRecord(string Udid, string? Name, string? Model);

/// <summary>Summary of the journal's run history, including the latest run's outcome.</summary>
/// <param name="TotalRuns">Number of recorded runs.</param>
/// <param name="FirstRunAt">Start time of the first run, or <see langword="null"/>.</param>
/// <param name="LatestRunAt">Start time of the latest run, or <see langword="null"/>.</param>
/// <param name="LastCopied">Files copied in the latest run.</param>
/// <param name="LastSkipped">Files skipped in the latest run.</param>
/// <param name="LastFailed">Files failed in the latest run.</param>
/// <param name="LastElapsed">Wall-clock duration of the latest run.</param>
public sealed record RunsSummary(
    int TotalRuns,
    DateTimeOffset? FirstRunAt,
    DateTimeOffset? LatestRunAt,
    int LastCopied,
    int LastSkipped,
    int LastFailed,
    TimeSpan LastElapsed);
