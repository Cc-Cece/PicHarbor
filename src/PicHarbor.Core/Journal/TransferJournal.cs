using System.Globalization;
using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.Journal;

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
    /// <summary>Hidden metadata package folder inside the archive destination root.</summary>
    public const string MetadataFolderName = ".picharbor";

    /// <summary>The journal/manifest filename written at the destination root or inside .picharbor.</summary>
    public const string DatabaseFileName = "picharbor.db";

    /// <summary>Legacy database filename for backwards compatibility.</summary>
    public const string LegacyDatabaseFileName = "get-and-see.db";

    /// <summary>Current journal schema version (bumped when tables are added; migrated in place).</summary>
    public const long SchemaVersion = 7;

    /// <summary>The <c>settings</c> key under which an archive's <c>organize_scheme</c> token is stored.</summary>
    private const string OrganizeSchemeKey = "organize_scheme";

    /// <summary>
    /// The <c>settings</c> key holding an in-flight <c>reorganize</c>'s target scheme token. It is present
    /// only while a layout migration is underway (a crash/resume marker) and cleared when the migration
    /// completes cleanly; it is a settings key only, so it needs no schema-version bump.
    /// </summary>
    private const string ReorganizeTargetKey = "reorganize_target";

    private readonly SqliteConnection connection;
    private bool disposed;

    private TransferJournal(SqliteConnection connection) => this.connection = connection;

    /// <summary>Absolute path to the SQLite database file backing this journal.</summary>
    public string DatabasePath { get; private init; } = string.Empty;

    /// <summary>
    /// Resolves the effective database path at destination root, checking for .picharbor/picharbor.db first,
    /// then root picharbor.db, then legacy get-and-see.db. Defaults to .picharbor/picharbor.db.
    /// </summary>
    public static string ResolveDatabasePath(string destinationRoot)
    {
        string modernPath = Path.Combine(destinationRoot, MetadataFolderName, DatabaseFileName);
        if (File.Exists(LongPath.ToExtended(modernPath)))
        {
            return modernPath;
        }

        string picharborPath = Path.Combine(destinationRoot, DatabaseFileName);
        if (File.Exists(LongPath.ToExtended(picharborPath)))
        {
            return picharborPath;
        }

        string legacyPath = Path.Combine(destinationRoot, LegacyDatabaseFileName);
        if (File.Exists(LongPath.ToExtended(legacyPath)))
        {
            return legacyPath;
        }

        return modernPath;
    }

    /// <summary>
    /// Ensures the hidden metadata folder (<c>.picharbor</c>) exists at destination root.
    /// </summary>
    public static void EnsureMetadataDirectory(string destinationRoot)
    {
        string modernDir = Path.Combine(destinationRoot, MetadataFolderName);
        string extendedDir = LongPath.ToExtended(modernDir);
        if (!System.IO.Directory.Exists(extendedDir))
        {
            System.IO.Directory.CreateDirectory(extendedDir);
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dirInfo = new DirectoryInfo(extendedDir);
                if (!dirInfo.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }
            }
        }
        catch
        {
            // Best effort hidden attribute
        }
    }

    private static void MoveSidecarIfExists(string source, string target)
    {
        string extSrc = LongPath.ToExtended(source);
        if (File.Exists(extSrc))
        {
            string extTarget = LongPath.ToExtended(target);
            if (File.Exists(extTarget))
            {
                File.Delete(extTarget);
            }
            File.Move(extSrc, extTarget);
        }
    }

    /// <summary>
    /// Opens (creating if necessary) the journal at <c>&lt;destinationRoot&gt;/.picharbor/picharbor.db</c> and
    /// ensures the schema and <c>manifest</c> view exist.
    /// </summary>
    /// <param name="destinationRoot">The destination root directory.</param>
    /// <returns>An open journal. The caller owns and disposes it.</returns>
    public static TransferJournal Open(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        System.IO.Directory.CreateDirectory(LongPath.ToExtended(destinationRoot));

        string modernPath = Path.Combine(destinationRoot, MetadataFolderName, DatabaseFileName);
        string databasePath = ResolveDatabasePath(destinationRoot);

        // If modern path does not exist yet but root database exists, smoothly migrate into .picharbor/
        if (!File.Exists(LongPath.ToExtended(modernPath)) &&
            File.Exists(LongPath.ToExtended(databasePath)) &&
            !string.Equals(databasePath, modernPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EnsureMetadataDirectory(destinationRoot);
                File.Move(LongPath.ToExtended(databasePath), LongPath.ToExtended(modernPath));
                MoveSidecarIfExists(databasePath + "-wal", modernPath + "-wal");
                MoveSidecarIfExists(databasePath + "-shm", modernPath + "-shm");
                databasePath = modernPath;
            }
            catch
            {
                // Best-effort migration; keep using databasePath if move is locked
            }
        }
        else if (string.Equals(databasePath, modernPath, StringComparison.OrdinalIgnoreCase))
        {
            EnsureMetadataDirectory(destinationRoot);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = LongPath.ToExtended(databasePath) };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var journal = new TransferJournal(connection) { DatabasePath = databasePath };
        journal.InitializeSchema();
        return journal;
    }

    /// <summary>
    /// Opens an existing journal at <c>&lt;destinationRoot&gt;/picharbor.db</c> <b>read-only</b> for
    /// inspection (e.g. the <c>status</c> verb). The database is never created, migrated, or written
    /// to — the connection uses <see cref="SqliteOpenMode.ReadOnly"/>.
    /// </summary>
    /// <param name="destinationRoot">The destination root directory containing an existing database.</param>
    /// <returns>An open read-only journal. The caller owns and disposes it.</returns>
    /// <exception cref="FileNotFoundException">Thrown when no database exists at the destination.</exception>
    public static TransferJournal OpenReadOnly(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string databasePath = ResolveDatabasePath(destinationRoot);
        // Probe and open through the \\?\ long-path prefix: a non-prefixed File.Exists on a > 260-char
        // path silently returns false (so status would wrongly report "no archive"), and the SQLite open
        // would otherwise crash on a deep destination (R6 / #39).
        string extendedDatabasePath = LongPath.ToExtended(databasePath);
        if (!File.Exists(extendedDatabasePath))
        {
            throw new FileNotFoundException("No PicHarbor database found.", databasePath);
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
        Execute(
            """
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS android_devices (
                device_id    TEXT PRIMARY KEY,
                name         TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                last_seen_at TEXT NOT NULL
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS android_sync_records (
                dest_path TEXT NOT NULL,
                device_id TEXT NOT NULL,
                synced_at TEXT NOT NULL,
                PRIMARY KEY (dest_path, device_id)
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS iphone_exported_files (
                dest_path       TEXT PRIMARY KEY,
                exported_path   TEXT NOT NULL,
                device_model    TEXT NOT NULL,
                exported_at     TEXT NOT NULL,
                file_size       INTEGER NOT NULL,
                sha256          TEXT
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS iphone_devices (
                device_model    TEXT PRIMARY KEY,
                sync_folder     TEXT NOT NULL,
                last_exported   TEXT NOT NULL
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS iphone_manual_selections (
                device_model   TEXT NOT NULL,
                dest_path      TEXT NOT NULL,
                added_at       TEXT NOT NULL,
                is_autofilled  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (device_model, dest_path)
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS android_manual_selections (
                device_id      TEXT NOT NULL,
                dest_path      TEXT NOT NULL,
                added_at       TEXT NOT NULL,
                is_autofilled  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (device_id, dest_path)
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS google_photos_sync_records (
                dest_path       TEXT PRIMARY KEY,
                media_key       TEXT,
                uploaded_at     TEXT NOT NULL,
                album_name      TEXT,
                file_size       INTEGER NOT NULL
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS google_photos_manual_selections (
                dest_path      TEXT PRIMARY KEY,
                added_at       TEXT NOT NULL,
                is_autofilled  INTEGER NOT NULL DEFAULT 0
            );
            """);

        EnsureColumnExists("iphone_manual_selections", "is_autofilled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists("android_manual_selections", "is_autofilled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists("google_photos_manual_selections", "is_autofilled", "INTEGER NOT NULL DEFAULT 0");

        Execute(
            """
            CREATE TABLE IF NOT EXISTS backup_sessions (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                device_uid       TEXT NOT NULL,
                device_name      TEXT,
                device_model     TEXT,
                started_at       TEXT NOT NULL,
                finished_at      TEXT,
                files_count      INTEGER NOT NULL DEFAULT 0,
                total_size_bytes INTEGER NOT NULL DEFAULT 0,
                status           TEXT NOT NULL DEFAULT 'InFlight',
                error_message    TEXT
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_sessions_device ON backup_sessions (device_uid);");
        Execute("CREATE INDEX IF NOT EXISTS ix_sessions_started ON backup_sessions (started_at);");

        Execute(
            """
            CREATE TABLE IF NOT EXISTS backup_history_records (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id         INTEGER NOT NULL,
                device_source_path TEXT NOT NULL,
                dest_path          TEXT NOT NULL,
                file_size          INTEGER NOT NULL,
                transferred_at     TEXT NOT NULL,
                media_type         TEXT
            );
            """);
        Execute("CREATE INDEX IF NOT EXISTS ix_history_session ON backup_history_records (session_id);");

        EnsureColumnExists("devices", "hardware_serial", "TEXT");
        EnsureColumnExists("devices", "device_type", "TEXT");

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

        if (version < 4)
        {
            Execute(
                """
                CREATE TABLE IF NOT EXISTS android_devices (
                    device_id    TEXT PRIMARY KEY,
                    name         TEXT NOT NULL,
                    created_at   TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                """);
            Execute(
                """
                CREATE TABLE IF NOT EXISTS android_sync_records (
                    dest_path TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    synced_at TEXT NOT NULL,
                    PRIMARY KEY (dest_path, device_id)
                );
                """);
        }

        if (version < 5)
        {
            Execute(
                """
                CREATE TABLE IF NOT EXISTS iphone_exported_files (
                    dest_path       TEXT PRIMARY KEY,
                    exported_path   TEXT NOT NULL,
                    device_model    TEXT NOT NULL,
                    exported_at     TEXT NOT NULL,
                    file_size       INTEGER NOT NULL,
                    sha256          TEXT
                );
                """);
            Execute(
                """
                CREATE TABLE IF NOT EXISTS iphone_devices (
                    device_model    TEXT PRIMARY KEY,
                    sync_folder     TEXT NOT NULL,
                    last_exported   TEXT NOT NULL
                );
                """);
            Execute(
                """
                CREATE TABLE IF NOT EXISTS iphone_manual_selections (
                    device_model   TEXT NOT NULL,
                    dest_path      TEXT NOT NULL,
                    added_at       TEXT NOT NULL,
                    PRIMARY KEY (device_model, dest_path)
                );
                """);
        }

        if (version < 6)
        {
            Execute(
                """
                CREATE TABLE IF NOT EXISTS android_manual_selections (
                    device_id      TEXT NOT NULL,
                    dest_path      TEXT NOT NULL,
                    added_at       TEXT NOT NULL,
                    PRIMARY KEY (device_id, dest_path)
                );
                """);
        }

        if (version < 7)
        {
            Execute(
                """
                CREATE TABLE IF NOT EXISTS backup_sessions (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    device_uid       TEXT NOT NULL,
                    device_name      TEXT,
                    device_model     TEXT,
                    started_at       TEXT NOT NULL,
                    finished_at      TEXT,
                    files_count      INTEGER NOT NULL DEFAULT 0,
                    total_size_bytes INTEGER NOT NULL DEFAULT 0,
                    status           TEXT NOT NULL DEFAULT 'InFlight',
                    error_message    TEXT
                );
                """);
            Execute("CREATE INDEX IF NOT EXISTS ix_sessions_device ON backup_sessions (device_uid);");
            Execute("CREATE INDEX IF NOT EXISTS ix_sessions_started ON backup_sessions (started_at);");

            Execute(
                """
                CREATE TABLE IF NOT EXISTS backup_history_records (
                    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id         INTEGER NOT NULL,
                    device_source_path TEXT NOT NULL,
                    dest_path          TEXT NOT NULL,
                    file_size          INTEGER NOT NULL,
                    transferred_at     TEXT NOT NULL,
                    media_type         TEXT
                );
                """);
            Execute("CREATE INDEX IF NOT EXISTS ix_history_session ON backup_history_records (session_id);");

            EnsureColumnExists("devices", "hardware_serial", "TEXT");
            EnsureColumnExists("devices", "device_type", "TEXT");

            BackfillLegacySessionsIfEmpty();
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

    /// <summary>
    /// Returns the target scheme of an in-flight <c>reorganize</c> (the <c>reorganize_target</c> marker), or
    /// <see langword="null"/> when no layout migration is underway. Used to resume an interrupted reorganize
    /// and to let <c>copy</c> refuse an archive that is mid-migration.
    /// </summary>
    /// <returns>The in-flight target <see cref="OrganizeScheme"/>, or <see langword="null"/>.</returns>
    public OrganizeScheme? GetReorganizeTarget()
    {
        if (!TableExists("settings"))
        {
            // A pre-v3 database has no settings table, hence never an in-flight reorganize.
            return null;
        }

        using SqliteCommand command = CreateCommand("SELECT value FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", ReorganizeTargetKey);
        return OrganizeSchemes.TryParse(command.ExecuteScalar() as string);
    }

    /// <summary>Records that a <c>reorganize</c> toward <paramref name="target"/> is in progress (crash/resume marker).</summary>
    /// <param name="target">The layout the reorganize is migrating the archive to.</param>
    public void SetReorganizeTarget(OrganizeScheme target)
    {
        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """);
        command.Parameters.AddWithValue("$key", ReorganizeTargetKey);
        command.Parameters.AddWithValue("$value", OrganizeSchemes.ToToken(target));
        command.ExecuteNonQuery();
    }

    /// <summary>Clears the in-flight <c>reorganize</c> marker once a layout migration has completed cleanly.</summary>
    public void ClearReorganizeTarget()
    {
        using SqliteCommand command = CreateCommand("DELETE FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", ReorganizeTargetKey);
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

    /// <summary>Returns the recorded destination path for a file, or null if not yet set.</summary>
    public string? GetDestRelativePath(string sourcePath, long sourceSize)
    {
        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM files WHERE source_path = $path AND source_size = $size;");
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$size", sourceSize);
        return command.ExecuteScalar() as string;
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

    /// <summary>
    /// Updates a completed file's recorded destination path after <c>reorganize</c> moves it on disk — a
    /// single <c>UPDATE</c> so the manifest always points at the file's real on-disk location. The file's
    /// <c>done</c> state and every other column are untouched; only <c>dest_path</c> changes.
    /// </summary>
    /// <param name="sourcePath">Device source path (part of the file's identity).</param>
    /// <param name="sourceSize">Source size in bytes (part of the file's identity).</param>
    /// <param name="newRelativeDestPath">The new destination path, relative to the archive root.</param>
    public void UpdateDestPath(string sourcePath, long sourceSize, string newRelativeDestPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(newRelativeDestPath);
        using SqliteCommand command = CreateCommand(
            "UPDATE files SET dest_path = $dest WHERE source_path = $path AND source_size = $size;");
        command.Parameters.AddWithValue("$dest", newRelativeDestPath);
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

    /// <summary>
    /// Reads every completed file with the inputs <c>reorganize</c> needs to re-place it: its identity
    /// (<c>source_path</c> + <c>source_size</c>), current <c>dest_path</c>, and the two stored timestamps
    /// that resolve its folder date — in a <b>deterministic order</b> (by <c>source_path</c>) so
    /// collision-suffix assignment is reproducible across runs and interruptions. Read-only.
    /// </summary>
    /// <returns>One entry per completed file, ordered by source path.</returns>
    public IReadOnlyList<ReorganizeEntry> EnumerateDoneForReorganize()
    {
        List<ReorganizeEntry> rows = new();
        using SqliteCommand command = CreateCommand(
            """
            SELECT source_path, source_size, dest_path, exif_datetime_original, source_mtime
            FROM files
            WHERE state = 'done' AND dest_path IS NOT NULL
            ORDER BY source_path, source_size;
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ReorganizeEntry(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                ParseWallOrNull(reader.IsDBNull(3) ? null : reader.GetString(3)),
                ParseIsoOrNull(reader.IsDBNull(4) ? null : reader.GetString(4))));
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

        bool hasSerial = ColumnExists("devices", "hardware_serial");
        bool hasType = ColumnExists("devices", "device_type");
        string sql = (hasSerial && hasType)
            ? "SELECT udid, name, model, last_seen, hardware_serial, device_type FROM devices ORDER BY last_seen DESC;"
            : "SELECT udid, name, model, last_seen FROM devices ORDER BY last_seen DESC;";

        using SqliteCommand command = CreateCommand(sql);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DeviceRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : ParseIsoOrNull(reader.GetString(3)),
                (hasSerial && !reader.IsDBNull(4)) ? reader.GetString(4) : null,
                (hasType && !reader.IsDBNull(5)) ? reader.GetString(5) : null));
        }

        return rows;
    }

    /// <summary>
    /// Registers a new device or updates an existing device, applying automatic renaming and deduplication.
    /// If the device was renamed on the phone, updates name immediately (forced sync).
    /// If another device shares the same name, automatically appends suffix '(2)', '(3)' etc. (forced disambiguation).
    /// </summary>
    public string RegisterOrUpdateDevice(
        string udid,
        string? name,
        string? model,
        string? hardwareSerial,
        string? deviceType,
        DateTimeOffset seenAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        string effectiveName = string.IsNullOrWhiteSpace(name) ? "Unknown Device" : name.Trim();

        // Check if device already exists
        using (SqliteCommand checkCmd = CreateCommand("SELECT name FROM devices WHERE udid = $udid;"))
        {
            checkCmd.Parameters.AddWithValue("$udid", udid);
            object? existingNameObj = checkCmd.ExecuteScalar();
            if (existingNameObj != null)
            {
                // Existing device: force sync name to latest, update serial and last_seen
                using SqliteCommand updateCmd = CreateCommand(
                    """
                    UPDATE devices
                    SET name = $name,
                        model = COALESCE($model, model),
                        hardware_serial = COALESCE($serial, hardware_serial),
                        device_type = COALESCE($type, device_type),
                        last_seen = $seen
                    WHERE udid = $udid;
                    """);
                updateCmd.Parameters.AddWithValue("$udid", udid);
                updateCmd.Parameters.AddWithValue("$name", effectiveName);
                updateCmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$serial", (object?)hardwareSerial ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$type", (object?)deviceType ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("$seen", IsoUtc(seenAt)!);
                updateCmd.ExecuteNonQuery();
                return effectiveName;
            }
        }

        // New device: check for naming collisions with other devices
        string disambiguatedName = effectiveName;
        int suffix = 2;
        while (true)
        {
            using SqliteCommand collisionCmd = CreateCommand(
                "SELECT COUNT(*) FROM devices WHERE name = $name AND udid != $udid;");
            collisionCmd.Parameters.AddWithValue("$name", disambiguatedName);
            collisionCmd.Parameters.AddWithValue("$udid", udid);
            long count = (long)(collisionCmd.ExecuteScalar() ?? 0L);
            if (count == 0)
            {
                break;
            }
            disambiguatedName = $"{effectiveName} ({suffix++})";
        }

        using SqliteCommand insertCmd = CreateCommand(
            """
            INSERT INTO devices (udid, name, model, hardware_serial, device_type, first_seen, last_seen)
            VALUES ($udid, $name, $model, $serial, $type, $seen, $seen);
            """);
        insertCmd.Parameters.AddWithValue("$udid", udid);
        insertCmd.Parameters.AddWithValue("$name", disambiguatedName);
        insertCmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$serial", (object?)hardwareSerial ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$type", (object?)deviceType ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$seen", IsoUtc(seenAt)!);
        insertCmd.ExecuteNonQuery();

        return disambiguatedName;
    }

    /// <summary>Starts a new backup session batch for a device.</summary>
    public long BeginBackupSession(string deviceUid, string? deviceName, string? deviceModel, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUid);
        using SqliteCommand cmd = CreateCommand(
            """
            INSERT INTO backup_sessions (device_uid, device_name, device_model, started_at, status)
            VALUES ($uid, $name, $model, $started, 'InFlight');
            SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("$uid", deviceUid);
        cmd.Parameters.AddWithValue("$name", (object?)deviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)deviceModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", IsoUtc(startedAt)!);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Records an individual file transfer within a backup session batch (Redundant audit data).</summary>
    public void RecordHistoryItem(
        long sessionId,
        string deviceSourcePath,
        string destPath,
        long fileSize,
        DateTimeOffset transferredAt,
        string? mediaType)
    {
        using SqliteCommand cmd = CreateCommand(
            """
            INSERT INTO backup_history_records (session_id, device_source_path, dest_path, file_size, transferred_at, media_type)
            VALUES ($session, $src, $dest, $size, $transferred, $type);
            """);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$src", deviceSourcePath);
        cmd.Parameters.AddWithValue("$dest", destPath);
        cmd.Parameters.AddWithValue("$size", fileSize);
        cmd.Parameters.AddWithValue("$transferred", IsoUtc(transferredAt)!);
        cmd.Parameters.AddWithValue("$type", (object?)mediaType ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Completes or aborts a backup session batch.</summary>
    public void CompleteBackupSession(
        long sessionId,
        int filesCount,
        long totalSizeBytes,
        string status = "Completed",
        string? errorMessage = null,
        DateTimeOffset? finishedAt = null)
    {
        DateTimeOffset end = finishedAt ?? DateTimeOffset.UtcNow;
        using SqliteCommand cmd = CreateCommand(
            """
            UPDATE backup_sessions
            SET finished_at = $finished,
                files_count = $count,
                total_size_bytes = $size,
                status = $status,
                error_message = $err
            WHERE id = $id;
            """);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$finished", IsoUtc(end)!);
        cmd.Parameters.AddWithValue("$count", filesCount);
        cmd.Parameters.AddWithValue("$size", totalSizeBytes);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$err", (object?)errorMessage ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads all backup session batches for a specific device, newest first.</summary>
    public IReadOnlyList<BackupSessionRecord> ReadBackupSessions(string deviceUid)
    {
        var list = new List<BackupSessionRecord>();
        if (!TableExists("backup_sessions")) return list;

        using SqliteCommand cmd = CreateCommand(
            """
            SELECT id, device_uid, device_name, device_model, started_at, finished_at, files_count, total_size_bytes, status, error_message
            FROM backup_sessions
            WHERE device_uid = $uid
            ORDER BY started_at DESC;
            """);
        cmd.Parameters.AddWithValue("$uid", deviceUid);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BackupSessionRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ParseIsoOrNull(reader.GetString(4)) ?? DateTimeOffset.UtcNow,
                reader.IsDBNull(5) ? null : ParseIsoOrNull(reader.GetString(5)),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return list;
    }

    /// <summary>Reads all file transfer items belonging to a specific backup session.</summary>
    public IReadOnlyList<BackupHistoryItemRecord> ReadBackupHistoryItems(long sessionId)
    {
        var list = new List<BackupHistoryItemRecord>();
        if (!TableExists("backup_history_records")) return list;

        using SqliteCommand cmd = CreateCommand(
            """
            SELECT id, session_id, device_source_path, dest_path, file_size, transferred_at, media_type
            FROM backup_history_records
            WHERE session_id = $session
            ORDER BY id ASC;
            """);
        cmd.Parameters.AddWithValue("$session", sessionId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BackupHistoryItemRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                ParseIsoOrNull(reader.GetString(5)) ?? DateTimeOffset.UtcNow,
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return list;
    }

    /// <summary>
    /// Automatically backfills legacy backup runs and files into <c>backup_sessions</c> and <c>backup_history_records</c>
    /// if the session tables are currently empty but historical runs exist.
    /// </summary>
    public void BackfillLegacySessionsIfEmpty()
    {
        if (!TableExists("backup_sessions") || !TableExists("runs") || !TableExists("files")) return;

        using var countCmd = CreateCommand("SELECT COUNT(*) FROM backup_sessions;");
        long sessionCount = Convert.ToInt64(countCmd.ExecuteScalar());
        if (sessionCount > 0) return;

        using var runsCmd = CreateCommand(
            """
            SELECT r.id, r.started_at, r.finished_at, r.copied, r.device_udid, d.name, d.model
            FROM runs r
            LEFT JOIN devices d ON r.device_udid = d.udid
            WHERE r.device_udid IS NOT NULL AND r.copied > 0
            ORDER BY r.id ASC;
            """);

        using var reader = runsCmd.ExecuteReader();
        var runsToMigrate = new List<(long Id, string StartedAt, string? FinishedAt, int Copied, string DeviceUdid, string? DeviceName, string? DeviceModel)>();
        while (reader.Read())
        {
            runsToMigrate.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        reader.Close();

        if (runsToMigrate.Count == 0) return;

        using var transaction = connection.BeginTransaction();
        try
        {
            var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var run in runsToMigrate)
            {
                DateTimeOffset runStart = ParseIsoOrNull(run.StartedAt) ?? DateTimeOffset.UtcNow;
                DateTimeOffset runEnd = (ParseIsoOrNull(run.FinishedAt) ?? runStart).AddSeconds(30);

                using var filesCmd = CreateCommand(
                    """
                    SELECT source_path, dest_path, source_size, copied_at
                    FROM files
                    WHERE state = 'done'
                      AND copied_at >= $start
                      AND copied_at <= $end;
                    """);
                filesCmd.Transaction = transaction;
                filesCmd.Parameters.AddWithValue("$start", IsoUtc(runStart)!);
                filesCmd.Parameters.AddWithValue("$end", IsoUtc(runEnd)!);

                using var fReader = filesCmd.ExecuteReader();
                var matchedFiles = new List<(string SourcePath, string DestPath, long Size, string CopiedAt)>();
                long totalSize = 0;
                while (fReader.Read())
                {
                    string sp = fReader.GetString(0);
                    if (processedFiles.Contains(sp)) continue;

                    string dp = fReader.IsDBNull(1) ? sp : fReader.GetString(1);
                    long sz = fReader.GetInt64(2);
                    string ca = fReader.IsDBNull(3) ? run.StartedAt : fReader.GetString(3);
                    matchedFiles.Add((sp, dp, sz, ca));
                    totalSize += sz;
                    processedFiles.Add(sp);
                }
                fReader.Close();

                int count = matchedFiles.Count > 0 ? matchedFiles.Count : run.Copied;

                using var insertSessionCmd = CreateCommand(
                    """
                    INSERT INTO backup_sessions (device_uid, device_name, device_model, started_at, finished_at, files_count, total_size_bytes, status)
                    VALUES ($uid, $name, $model, $start, $finish, $count, $size, 'Completed');
                    SELECT last_insert_rowid();
                    """);
                insertSessionCmd.Transaction = transaction;
                insertSessionCmd.Parameters.AddWithValue("$uid", run.DeviceUdid);
                insertSessionCmd.Parameters.AddWithValue("$name", (object?)run.DeviceName ?? "Unknown Device");
                insertSessionCmd.Parameters.AddWithValue("$model", (object?)run.DeviceModel ?? "Generic Device");
                insertSessionCmd.Parameters.AddWithValue("$start", run.StartedAt);
                insertSessionCmd.Parameters.AddWithValue("$finish", (object?)run.FinishedAt ?? run.StartedAt);
                insertSessionCmd.Parameters.AddWithValue("$count", count);
                insertSessionCmd.Parameters.AddWithValue("$size", totalSize);

                long sessionId = Convert.ToInt64(insertSessionCmd.ExecuteScalar());

                foreach (var f in matchedFiles)
                {
                    using var insertFileCmd = CreateCommand(
                        """
                        INSERT INTO backup_history_records (session_id, device_source_path, dest_path, file_size, transferred_at, media_type)
                        VALUES ($sid, $sp, $dp, $sz, $ta, $type);
                        """);
                    insertFileCmd.Transaction = transaction;
                    insertFileCmd.Parameters.AddWithValue("$sid", sessionId);
                    insertFileCmd.Parameters.AddWithValue("$sp", f.SourcePath);
                    insertFileCmd.Parameters.AddWithValue("$dp", f.DestPath);
                    insertFileCmd.Parameters.AddWithValue("$sz", f.Size);
                    insertFileCmd.Parameters.AddWithValue("$ta", f.CopiedAt);
                    insertFileCmd.Parameters.AddWithValue("$type", Path.GetExtension(f.SourcePath).TrimStart('.').ToLowerInvariant());
                    insertFileCmd.ExecuteNonQuery();
                }
            }

            // Check if there are any remaining files with state='done' that were not covered by the exact run windows
            using var remainingFilesCmd = CreateCommand(
                """
                SELECT source_path, dest_path, source_size, copied_at
                FROM files
                WHERE state = 'done';
                """);
            remainingFilesCmd.Transaction = transaction;
            using var remReader = remainingFilesCmd.ExecuteReader();
            var remainingFiles = new List<(string SourcePath, string DestPath, long Size, string CopiedAt)>();
            while (remReader.Read())
            {
                string sp = remReader.GetString(0);
                if (!processedFiles.Contains(sp))
                {
                    string dp = remReader.IsDBNull(1) ? sp : remReader.GetString(1);
                    long sz = remReader.GetInt64(2);
                    string ca = remReader.IsDBNull(3) ? DateTimeOffset.UtcNow.ToString("o") : remReader.GetString(3);
                    remainingFiles.Add((sp, dp, sz, ca));
                    processedFiles.Add(sp);
                }
            }
            remReader.Close();

            if (remainingFiles.Count > 0 && runsToMigrate.Count > 0)
            {
                var primaryRun = runsToMigrate[0];
                long remTotalSize = remainingFiles.Sum(f => f.Size);
                using var baselineSessionCmd = CreateCommand(
                    """
                    INSERT INTO backup_sessions (device_uid, device_name, device_model, started_at, finished_at, files_count, total_size_bytes, status)
                    VALUES ($uid, $name, $model, $start, $finish, $count, $size, 'Completed');
                    SELECT last_insert_rowid();
                    """);
                baselineSessionCmd.Transaction = transaction;
                baselineSessionCmd.Parameters.AddWithValue("$uid", primaryRun.DeviceUdid);
                baselineSessionCmd.Parameters.AddWithValue("$name", (object?)primaryRun.DeviceName ?? "Unknown Device");
                baselineSessionCmd.Parameters.AddWithValue("$model", (object?)primaryRun.DeviceModel ?? "Generic Device");
                baselineSessionCmd.Parameters.AddWithValue("$start", remainingFiles[0].CopiedAt);
                baselineSessionCmd.Parameters.AddWithValue("$finish", remainingFiles[^1].CopiedAt);
                baselineSessionCmd.Parameters.AddWithValue("$count", remainingFiles.Count);
                baselineSessionCmd.Parameters.AddWithValue("$size", remTotalSize);

                long bSessionId = Convert.ToInt64(baselineSessionCmd.ExecuteScalar());

                foreach (var f in remainingFiles)
                {
                    using var insertFileCmd = CreateCommand(
                        """
                        INSERT INTO backup_history_records (session_id, device_source_path, dest_path, file_size, transferred_at, media_type)
                        VALUES ($sid, $sp, $dp, $sz, $ta, $type);
                        """);
                    insertFileCmd.Transaction = transaction;
                    insertFileCmd.Parameters.AddWithValue("$sid", bSessionId);
                    insertFileCmd.Parameters.AddWithValue("$sp", f.SourcePath);
                    insertFileCmd.Parameters.AddWithValue("$dp", f.DestPath);
                    insertFileCmd.Parameters.AddWithValue("$sz", f.Size);
                    insertFileCmd.Parameters.AddWithValue("$ta", f.CopiedAt);
                    insertFileCmd.Parameters.AddWithValue("$type", Path.GetExtension(f.SourcePath).TrimStart('.').ToLowerInvariant());
                    insertFileCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>Returns aggregated backup totals for a device (total sessions, total files, total bytes).</summary>
    public (int TotalSessions, int TotalFiles, long TotalBytes) ReadDeviceBackupSummary(string deviceUid)
    {
        if (!TableExists("backup_sessions")) return (0, 0, 0);

        using SqliteCommand cmd = CreateCommand(
            """
            SELECT COUNT(*), COALESCE(SUM(files_count), 0), COALESCE(SUM(total_size_bytes), 0)
            FROM backup_sessions
            WHERE device_uid = $uid AND status != 'Aborted';
            """);
        cmd.Parameters.AddWithValue("$uid", deviceUid);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
        }
        return (0, 0, 0);
    }

    /// <summary>
    /// Safely prunes redundant audit history data (sessions and file records older than the cutoff).
    /// Does NOT touch core files/manifest tables or affect deduplication.
    /// </summary>
    public (int PrunedSessions, int PrunedRecords) PruneRedundantHistory(DateTimeOffset cutoffTime)
    {
        if (!TableExists("backup_sessions") || !TableExists("backup_history_records"))
        {
            return (0, 0);
        }

        string cutoffIso = IsoUtc(cutoffTime)!;
        int prunedRecords = 0;
        int prunedSessions = 0;

        using (var transaction = connection.BeginTransaction())
        {
            using (SqliteCommand deleteItems = CreateCommand(
                """
                DELETE FROM backup_history_records
                WHERE session_id IN (
                    SELECT id FROM backup_sessions
                    WHERE started_at < $cutoff AND status != 'InFlight'
                );
                """))
            {
                deleteItems.Parameters.AddWithValue("$cutoff", cutoffIso);
                deleteItems.Transaction = transaction;
                prunedRecords = deleteItems.ExecuteNonQuery();
            }

            using (SqliteCommand deleteSessions = CreateCommand(
                """
                DELETE FROM backup_sessions
                WHERE started_at < $cutoff AND status != 'InFlight';
                """))
            {
                deleteSessions.Parameters.AddWithValue("$cutoff", cutoffIso);
                deleteSessions.Transaction = transaction;
                prunedSessions = deleteSessions.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return (prunedSessions, prunedRecords);
    }

    /// <summary>Executes WAL checkpoint and SQLite VACUUM to reclaim physical disk space.</summary>
    public void VacuumDatabase()
    {
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        Execute("VACUUM;");
    }

    /// <summary>Returns physical database footprint and core vs redundant record statistics.</summary>
    public DatabaseMetricsRecord GetDatabaseMetrics()
    {
        long dbSize = 0;
        long walSize = 0;

        try
        {
            if (File.Exists(LongPath.ToExtended(DatabasePath)))
            {
                dbSize = new FileInfo(LongPath.ToExtended(DatabasePath)).Length;
            }
            string walPath = DatabasePath + "-wal";
            if (File.Exists(LongPath.ToExtended(walPath)))
            {
                walSize = new FileInfo(LongPath.ToExtended(walPath)).Length;
            }
        }
        catch
        {
            // Best effort file size reading
        }

        int coreFiles = 0;
        int historyRecords = 0;
        int backupSessions = 0;

        if (TableExists("files"))
        {
            using SqliteCommand cmd = CreateCommand("SELECT COUNT(*) FROM files WHERE state = 'done';");
            coreFiles = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        if (TableExists("backup_history_records"))
        {
            using SqliteCommand cmd = CreateCommand("SELECT COUNT(*) FROM backup_history_records;");
            historyRecords = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        if (TableExists("backup_sessions"))
        {
            using SqliteCommand cmd = CreateCommand("SELECT COUNT(*) FROM backup_sessions;");
            backupSessions = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        return new DatabaseMetricsRecord(dbSize, walSize, coreFiles, historyRecords, backupSessions);
    }

    /// <summary>
    /// Scans database against local disk files to clean ghost records (files manually deleted on disk by user).
    /// Does NOT delete or modify existing files on disk or device.
    /// </summary>
    public (int ScannedCount, int RemovedGhostCount) RepairDatabaseConsistency(
        string destinationRoot,
        IProgress<(int Scanned, int Removed)>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        if (!TableExists("files")) return (0, 0);

        var ghostIds = new List<long>();
        var itemsToCheck = new List<(long Id, string DestPath)>();

        using (SqliteCommand selectCmd = CreateCommand("SELECT id, dest_path FROM files WHERE state = 'done' AND dest_path IS NOT NULL;"))
        using (SqliteDataReader reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                itemsToCheck.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        int scanned = 0;
        int removed = 0;

        foreach (var (id, relPath) in itemsToCheck)
        {
            scanned++;
            string fullPath = Path.Combine(destinationRoot, relPath);
            if (!File.Exists(LongPath.ToExtended(fullPath)))
            {
                ghostIds.Add(id);
                removed++;
            }

            if (scanned % 100 == 0)
            {
                progress?.Report((scanned, removed));
            }
        }

        if (ghostIds.Count > 0)
        {
            using var transaction = connection.BeginTransaction();
            // Batch delete in chunks of 500
            for (int i = 0; i < ghostIds.Count; i += 500)
            {
                var chunk = ghostIds.Skip(i).Take(500).ToList();
                string inClause = string.Join(",", chunk);
                using SqliteCommand deleteCmd = CreateCommand($"DELETE FROM files WHERE id IN ({inClause});");
                deleteCmd.Transaction = transaction;
                deleteCmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        Execute("PRAGMA integrity_check;");
        progress?.Report((scanned, removed));
        return (scanned, removed);
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

    private void EnsureColumnExists(string table, string column, string definition)
    {
        if (!TableExists(table)) return;
        try
        {
            using SqliteCommand pragma = CreateCommand($"PRAGMA table_info({table});");
            using SqliteDataReader reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Column already exists
                }
            }

            Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }
        catch { }
    }

    private bool ColumnExists(string table, string column)
    {
        if (!TableExists(table)) return false;
        try
        {
            using SqliteCommand pragma = CreateCommand($"PRAGMA table_info({table});");
            using SqliteDataReader reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
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

    private static DateTime? ParseWallOrNull(string? value) =>
        value is not null &&
        DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ParseIsoOrNull(string? value) =>
        value is null ? null : ParseIso(value);

    /// <summary>Registers or updates an Android device record in SQLite.</summary>
    public void UpsertAndroidDevice(string deviceId, string name, DateTimeOffset lastSeenAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO android_devices (device_id, name, created_at, last_seen_at)
            VALUES ($device_id, $name, $created_at, $last_seen_at)
            ON CONFLICT(device_id) DO UPDATE SET
                name = excluded.name,
                last_seen_at = excluded.last_seen_at;
            """);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$created_at", lastSeenAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last_seen_at", lastSeenAt.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Records that a file was successfully transferred over FTP to a specific Android device.</summary>
    public void RecordAndroidSync(string destPath, string deviceId, DateTimeOffset syncedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        using SqliteCommand command = CreateCommand(
            """
            INSERT OR REPLACE INTO android_sync_records (dest_path, device_id, synced_at)
            VALUES ($dest_path, $device_id, $synced_at);
            """);
        command.Parameters.AddWithValue("$dest_path", destPath);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$synced_at", syncedAt.ToString("o", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the set of relative destination paths that have been synced to the target Android device.</summary>
    public HashSet<string> GetAndroidSyncedDestPaths(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!TableExists("android_sync_records"))
        {
            return set;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM android_sync_records WHERE device_id = $device_id;");
        command.Parameters.AddWithValue("$device_id", deviceId);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>Reads all registered Android devices from SQLite.</summary>
    public IReadOnlyList<Android.AndroidDeviceRecord> ReadAndroidDevices()
    {
        var list = new List<Android.AndroidDeviceRecord>();
        if (!TableExists("android_devices"))
        {
            return list;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT device_id, name, created_at, last_seen_at FROM android_devices ORDER BY last_seen_at DESC;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            string name = reader.GetString(1);
            DateTimeOffset created = DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c) ? c : DateTimeOffset.MinValue;
            DateTimeOffset last = DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var l) ? l : DateTimeOffset.MinValue;

            list.Add(new Android.AndroidDeviceRecord(id, name, created, last));
        }

        return list;
    }

    /// <summary>Returns all exported file records for the given device model from <c>iphone_exported_files</c>.</summary>
    public IReadOnlyList<IPhoneExportedFileRecord> GetIPhoneExportedFiles(string deviceModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        var result = new List<IPhoneExportedFileRecord>();
        if (!TableExists("iphone_exported_files"))
        {
            return result;
        }

        using SqliteCommand command = CreateCommand(
            """
            SELECT dest_path, exported_path, device_model, exported_at, file_size, sha256
            FROM iphone_exported_files
            WHERE device_model = $model;
            """);
        command.Parameters.AddWithValue("$model", deviceModel);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IPhoneExportedFileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    /// <summary>Upserts a row into <c>iphone_exported_files</c>.</summary>
    public void UpsertIPhoneExportedFile(IPhoneExportedFileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO iphone_exported_files (dest_path, exported_path, device_model, exported_at, file_size, sha256)
            VALUES ($dest, $exported, $model, $at, $size, $sha)
            ON CONFLICT(dest_path) DO UPDATE SET
                exported_path = excluded.exported_path,
                device_model = excluded.device_model,
                exported_at = excluded.exported_at,
                file_size = excluded.file_size,
                sha256 = excluded.sha256;
            """);
        command.Parameters.AddWithValue("$dest", record.DestPath);
        command.Parameters.AddWithValue("$exported", record.ExportedPath);
        command.Parameters.AddWithValue("$model", record.DeviceModel);
        command.Parameters.AddWithValue("$at", IsoUtc(record.ExportedAt)!);
        command.Parameters.AddWithValue("$size", record.FileSize);
        command.Parameters.AddWithValue("$sha", (object?)record.Sha256 ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes a row from <c>iphone_exported_files</c> by its destination relative path.</summary>
    public void DeleteIPhoneExportedFile(string destPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(destPath);
        if (!TableExists("iphone_exported_files")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM iphone_exported_files WHERE dest_path = $dest;");
        command.Parameters.AddWithValue("$dest", destPath);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the recorded <c>iphone_devices</c> row for the specified device model, if any.</summary>
    public IPhoneDeviceRecord? GetIPhoneDevice(string deviceModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        if (!TableExists("iphone_devices")) return null;

        using SqliteCommand command = CreateCommand(
            "SELECT device_model, sync_folder, last_exported FROM iphone_devices WHERE device_model = $model;");
        command.Parameters.AddWithValue("$model", deviceModel);
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new IPhoneDeviceRecord(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
        }

        return null;
    }

    /// <summary>Upserts a row into <c>iphone_devices</c>.</summary>
    public void UpsertIPhoneDevice(string deviceModel, string syncFolder, DateTimeOffset lastExported)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncFolder);

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO iphone_devices (device_model, sync_folder, last_exported)
            VALUES ($model, $folder, $at)
            ON CONFLICT(device_model) DO UPDATE SET
                sync_folder = excluded.sync_folder,
                last_exported = excluded.last_exported;
            """);
        command.Parameters.AddWithValue("$model", deviceModel);
        command.Parameters.AddWithValue("$folder", syncFolder);
        command.Parameters.AddWithValue("$at", IsoUtc(lastExported)!);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns all manually selected destination relative paths for the specified device model.</summary>
    public HashSet<string> GetManualSelections(string deviceModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("iphone_manual_selections"))
        {
            return set;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM iphone_manual_selections WHERE device_model = $model;");
        command.Parameters.AddWithValue("$model", deviceModel);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>Returns dictionary of manually selected destination relative paths mapping to whether they were auto-filled.</summary>
    public Dictionary<string, bool> GetManualSelectionsWithAutofilled(string deviceModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("iphone_manual_selections"))
        {
            return dict;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT dest_path, is_autofilled FROM iphone_manual_selections WHERE device_model = $model;");
        command.Parameters.AddWithValue("$model", deviceModel);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetInt32(1) == 1;
        }

        return dict;
    }

    /// <summary>
    /// Checks if a given relative destination path exists in the <c>files</c> table with <c>state = 'done'</c>.
    /// </summary>
    public bool IsFileArchivedAndDone(string destPath)
    {
        if (string.IsNullOrWhiteSpace(destPath)) return false;
        string forward = destPath.Replace('\\', '/');
        string back = destPath.Replace('/', '\\');
        using SqliteCommand command = CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM files WHERE (dest_path = $p1 OR dest_path = $p2) AND state = 'done');");
        command.Parameters.AddWithValue("$p1", forward);
        command.Parameters.AddWithValue("$p2", back);
        return command.ExecuteScalar() is long present && present != 0;
    }

    /// <summary>Adds a destination path to manual selections for the specified device model. Returns true if newly added.</summary>
    public bool AddManualSelection(string deviceModel, string destPath, bool isAutofilled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO iphone_manual_selections (device_model, dest_path, added_at, is_autofilled)
            VALUES ($model, $dest, $at, $is_autofilled)
            ON CONFLICT(device_model, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """);
        command.Parameters.AddWithValue("$model", deviceModel);
        command.Parameters.AddWithValue("$dest", destPath);
        command.Parameters.AddWithValue("$at", IsoUtc(DateTimeOffset.UtcNow)!);
        command.Parameters.AddWithValue("$is_autofilled", isAutofilled ? 1 : 0);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Removes a destination path from manual selections and cascades cleanup of orphaned auto-filled pairs.</summary>
    public void RemoveManualSelection(string deviceModel, string destPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        if (!TableExists("iphone_manual_selections")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM iphone_manual_selections WHERE device_model = $model AND dest_path = $dest;");
        command.Parameters.AddWithValue("$model", deviceModel);
        command.Parameters.AddWithValue("$dest", destPath);
        command.ExecuteNonQuery();

        CascadeRemoveOrphanedAutofills(deviceModel);
    }

    /// <summary>Removes orphaned auto-filled selections that no longer have a manually selected main item in the same folder.</summary>
    public void CascadeRemoveOrphanedAutofills(string deviceModel)
    {
        if (!TableExists("iphone_manual_selections")) return;
        var dict = GetManualSelectionsWithAutofilled(deviceModel);
        var autofilledItems = dict.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var manualItems = dict.Where(kv => !kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRemove = new List<string>();
        foreach (var autoItem in autofilledItems)
        {
            string dir = Path.GetDirectoryName(autoItem.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(autoItem);

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(m.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(m);
                return string.Equals(mDir, dir, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(mStem, stem, StringComparison.OrdinalIgnoreCase);
            });

            if (!hasManualParent)
            {
                toRemove.Add(autoItem);
            }
        }

        if (toRemove.Count > 0)
        {
            BatchRemoveManualSelections(deviceModel, toRemove);
        }
    }

    /// <summary>Adds multiple destination paths to manual selections inside a single transaction.</summary>
    public void BatchAddManualSelections(string deviceModel, IEnumerable<string> destPaths, bool isAutofilled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        ArgumentNullException.ThrowIfNull(destPaths);

        using var transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO iphone_manual_selections (device_model, dest_path, added_at, is_autofilled)
            VALUES ($model, $dest, $at, $is_autofilled)
            ON CONFLICT(device_model, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """;
        var modelParam = command.Parameters.Add("$model", SqliteType.Text);
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);
        var atParam = command.Parameters.Add("$at", SqliteType.Text);
        var autoParam = command.Parameters.Add("$is_autofilled", SqliteType.Integer);
        modelParam.Value = deviceModel;
        atParam.Value = IsoUtc(DateTimeOffset.UtcNow)!;
        autoParam.Value = isAutofilled ? 1 : 0;

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Removes multiple destination paths from manual selections inside a single transaction.</summary>
    public void BatchRemoveManualSelections(string deviceModel, IEnumerable<string> destPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        ArgumentNullException.ThrowIfNull(destPaths);

        if (!TableExists("iphone_manual_selections")) return;

        using var transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM iphone_manual_selections WHERE device_model = $model AND dest_path = $dest;";
        var modelParam = command.Parameters.Add("$model", SqliteType.Text);
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);
        modelParam.Value = deviceModel;

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Clears all manual selections for the specified device model.</summary>
    public void ClearManualSelections(string deviceModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        if (!TableExists("iphone_manual_selections")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM iphone_manual_selections WHERE device_model = $model;");
        command.Parameters.AddWithValue("$model", deviceModel);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns all manually selected destination relative paths for the specified Android device ID.</summary>
    public HashSet<string> GetAndroidManualSelections(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("android_manual_selections"))
        {
            return set;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM android_manual_selections WHERE device_id = $device_id;");
        command.Parameters.AddWithValue("$device_id", deviceId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>Returns dictionary of manually selected destination relative paths mapping to whether they were auto-filled for Android.</summary>
    public Dictionary<string, bool> GetAndroidManualSelectionsWithAutofilled(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("android_manual_selections"))
        {
            return dict;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT dest_path, is_autofilled FROM android_manual_selections WHERE device_id = $device_id;");
        command.Parameters.AddWithValue("$device_id", deviceId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetInt32(1) == 1;
        }

        return dict;
    }

    /// <summary>Adds a destination path to manual selections for the specified Android device ID. Returns true if newly added.</summary>
    public bool AddAndroidManualSelection(string deviceId, string destPath, bool isAutofilled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO android_manual_selections (device_id, dest_path, added_at, is_autofilled)
            VALUES ($device_id, $dest, $at, $is_autofilled)
            ON CONFLICT(device_id, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$dest", destPath);
        command.Parameters.AddWithValue("$at", IsoUtc(DateTimeOffset.UtcNow)!);
        command.Parameters.AddWithValue("$is_autofilled", isAutofilled ? 1 : 0);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Removes a destination path from manual selections for the specified Android device ID.</summary>
    public void RemoveAndroidManualSelection(string deviceId, string destPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        if (!TableExists("android_manual_selections")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM android_manual_selections WHERE device_id = $device_id AND dest_path = $dest;");
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$dest", destPath);
        command.ExecuteNonQuery();

        CascadeRemoveOrphanedAndroidAutofills(deviceId);
    }

    /// <summary>Removes orphaned auto-filled selections that no longer have a manually selected main item in the same folder for Android.</summary>
    public void CascadeRemoveOrphanedAndroidAutofills(string deviceId)
    {
        if (!TableExists("android_manual_selections")) return;
        var dict = GetAndroidManualSelectionsWithAutofilled(deviceId);
        var autofilledItems = dict.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var manualItems = dict.Where(kv => !kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRemove = new List<string>();
        foreach (var autoItem in autofilledItems)
        {
            string dir = Path.GetDirectoryName(autoItem.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(autoItem);

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(m.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(m);
                return string.Equals(mDir, dir, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(mStem, stem, StringComparison.OrdinalIgnoreCase);
            });

            if (!hasManualParent)
            {
                toRemove.Add(autoItem);
            }
        }

        if (toRemove.Count > 0)
        {
            BatchRemoveAndroidManualSelections(deviceId, toRemove);
        }
    }

    /// <summary>Adds multiple destination paths to manual selections for Android inside a single transaction.</summary>
    public void BatchAddAndroidManualSelections(string deviceId, IEnumerable<string> destPaths, bool isAutofilled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(destPaths);

        using var transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO android_manual_selections (device_id, dest_path, added_at, is_autofilled)
            VALUES ($device_id, $dest, $at, $is_autofilled)
            ON CONFLICT(device_id, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """;
        var idParam = command.Parameters.Add("$device_id", SqliteType.Text);
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);
        var atParam = command.Parameters.Add("$at", SqliteType.Text);
        var autoParam = command.Parameters.Add("$is_autofilled", SqliteType.Integer);
        idParam.Value = deviceId;
        atParam.Value = IsoUtc(DateTimeOffset.UtcNow)!;
        autoParam.Value = isAutofilled ? 1 : 0;

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Removes multiple destination paths from manual selections for Android inside a single transaction.</summary>
    public void BatchRemoveAndroidManualSelections(string deviceId, IEnumerable<string> destPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(destPaths);

        if (!TableExists("android_manual_selections")) return;

        using var transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM android_manual_selections WHERE device_id = $device_id AND dest_path = $dest;";
        var idParam = command.Parameters.Add("$device_id", SqliteType.Text);
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);
        idParam.Value = deviceId;

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Clears all manual selections for the specified Android device ID.</summary>
    public void ClearAndroidManualSelections(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!TableExists("android_manual_selections")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM android_manual_selections WHERE device_id = $device_id;");
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes an Android sync record by destination path and device ID.</summary>
    public void RemoveAndroidSyncRecord(string destPath, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!TableExists("android_sync_records")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM android_sync_records WHERE dest_path = $dest AND device_id = $device_id;");
        command.Parameters.AddWithValue("$dest", destPath);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns synced destination paths for an Android device that are no longer in the target set.</summary>
    public List<string> GetOrphanedAndroidSyncedFiles(string deviceId, IReadOnlySet<string> activeTargetPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(activeTargetPaths);

        var list = new List<string>();
        if (!TableExists("android_sync_records")) return list;

        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM android_sync_records WHERE device_id = $device_id;");
        command.Parameters.AddWithValue("$device_id", deviceId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            if (!activeTargetPaths.Contains(path))
            {
                list.Add(path);
            }
        }

        return list;
    }

    /// <summary>Records an uploaded file to Google Photos.</summary>
    public void RecordGooglePhotosSync(string destPath, string? mediaKey, DateTimeOffset uploadedAt, string? albumName, long fileSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        if (!TableExists("google_photos_sync_records")) return;

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO google_photos_sync_records (dest_path, media_key, uploaded_at, album_name, file_size)
            VALUES ($dest, $media_key, $uploaded_at, $album_name, $file_size)
            ON CONFLICT(dest_path) DO UPDATE SET
                media_key = excluded.media_key,
                uploaded_at = excluded.uploaded_at,
                album_name = excluded.album_name,
                file_size = excluded.file_size;
            """);
        command.Parameters.AddWithValue("$dest", destPath);
        command.Parameters.AddWithValue("$media_key", (object?)mediaKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$uploaded_at", IsoUtc(uploadedAt)!);
        command.Parameters.AddWithValue("$album_name", (object?)albumName ?? DBNull.Value);
        command.Parameters.AddWithValue("$file_size", fileSize);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the set of relative destination paths that have been uploaded to Google Photos.</summary>
    public HashSet<string> GetGooglePhotosSyncedDestPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("google_photos_sync_records")) return set;

        using SqliteCommand command = CreateCommand("SELECT dest_path FROM google_photos_sync_records;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }
        return set;
    }

    /// <summary>Returns the set of relative destination paths manually selected for Google Photos upload.</summary>
    public HashSet<string> GetGooglePhotosManualSelections()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("google_photos_manual_selections")) return set;

        using SqliteCommand command = CreateCommand("SELECT dest_path FROM google_photos_manual_selections;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }
        return set;
    }

    /// <summary>Returns the map of destination paths with their is_autofilled flags for Google Photos.</summary>
    public Dictionary<string, bool> GetGooglePhotosManualSelectionsWithAutofilled()
    {
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("google_photos_manual_selections")) return dict;

        using SqliteCommand command = CreateCommand("SELECT dest_path, is_autofilled FROM google_photos_manual_selections;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetInt32(1) == 1;
        }
        return dict;
    }

    /// <summary>Adds multiple destination paths to manual selections for Google Photos inside a single transaction.</summary>
    public void BatchAddGooglePhotosManualSelections(IEnumerable<string> destPaths, bool isAutofilled = false)
    {
        ArgumentNullException.ThrowIfNull(destPaths);
        using var transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO google_photos_manual_selections (dest_path, added_at, is_autofilled)
            VALUES ($dest, $at, $is_autofilled)
            ON CONFLICT(dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """;
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);
        var atParam = command.Parameters.Add("$at", SqliteType.Text);
        var autoParam = command.Parameters.Add("$is_autofilled", SqliteType.Integer);
        atParam.Value = IsoUtc(DateTimeOffset.UtcNow)!;
        autoParam.Value = isAutofilled ? 1 : 0;

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Removes multiple destination paths from manual selections for Google Photos inside a single transaction.</summary>
    public void BatchRemoveGooglePhotosManualSelections(IEnumerable<string> destPaths)
    {
        ArgumentNullException.ThrowIfNull(destPaths);
        if (!TableExists("google_photos_manual_selections")) return;

        using var transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM google_photos_manual_selections WHERE dest_path = $dest;";
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Clears all manual selections for Google Photos.</summary>
    public void ClearGooglePhotosManualSelections()
    {
        if (!TableExists("google_photos_manual_selections")) return;
        using SqliteCommand command = CreateCommand("DELETE FROM google_photos_manual_selections;");
        command.ExecuteNonQuery();
    }

    /// <summary>Removes orphaned auto-filled selections for Google Photos.</summary>
    public void CascadeRemoveOrphanedGooglePhotosAutofills()
    {
        if (!TableExists("google_photos_manual_selections")) return;
        var dict = GetGooglePhotosManualSelectionsWithAutofilled();
        var autofilledItems = dict.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var manualItems = dict.Where(kv => !kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRemove = new List<string>();
        foreach (var autoItem in autofilledItems)
        {
            string dir = Path.GetDirectoryName(autoItem.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(autoItem);

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(m.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(m);
                return string.Equals(mDir, dir, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(mStem, stem, StringComparison.OrdinalIgnoreCase);
            });

            if (!hasManualParent)
            {
                toRemove.Add(autoItem);
            }
        }

        if (toRemove.Count > 0)
        {
            BatchRemoveGooglePhotosManualSelections(toRemove);
        }
    }

    /// <summary>Adds destination paths to unified manual selections across all sync targets (iPhone, Android, Google Photos).</summary>
    public void BatchAddUnifiedManualSelections(string? deviceModel, string? deviceId, IEnumerable<string> destPaths, bool isAutofilled = false)
    {
        var list = destPaths.ToList();
        if (list.Count == 0) return;
        BatchAddManualSelections(string.IsNullOrWhiteSpace(deviceModel) ? "iPhone" : deviceModel, list, isAutofilled);
        BatchAddAndroidManualSelections(string.IsNullOrWhiteSpace(deviceId) ? "Android Device" : deviceId, list, isAutofilled);
        BatchAddGooglePhotosManualSelections(list, isAutofilled);
    }

    /// <summary>Removes destination paths from unified manual selections across all sync targets.</summary>
    public void BatchRemoveUnifiedManualSelections(string? deviceModel, string? deviceId, IEnumerable<string> destPaths)
    {
        var list = destPaths.ToList();
        if (list.Count == 0) return;
        BatchRemoveManualSelections(string.IsNullOrWhiteSpace(deviceModel) ? "iPhone" : deviceModel, list);
        BatchRemoveAndroidManualSelections(string.IsNullOrWhiteSpace(deviceId) ? "Android Device" : deviceId, list);
        BatchRemoveGooglePhotosManualSelections(list);
    }

    /// <summary>Clears unified manual selections across all sync targets.</summary>
    public void ClearUnifiedManualSelections(string? deviceModel, string? deviceId)
    {
        ClearManualSelections(string.IsNullOrWhiteSpace(deviceModel) ? "iPhone" : deviceModel);
        ClearAndroidManualSelections(string.IsNullOrWhiteSpace(deviceId) ? "Android Device" : deviceId);
        ClearGooglePhotosManualSelections();
    }

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

/// <summary>An exported file row recorded in <c>iphone_exported_files</c>.</summary>
public sealed record IPhoneExportedFileRecord(
    string DestPath,
    string ExportedPath,
    string DeviceModel,
    DateTimeOffset ExportedAt,
    long FileSize,
    string? Sha256 = null);

/// <summary>An iPhone sync device record in <c>iphone_devices</c>.</summary>
public sealed record IPhoneDeviceRecord(
    string DeviceModel,
    string SyncFolder,
    DateTimeOffset LastExported);

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

/// <summary>A completed-file row projected for the <c>reorganize</c> engine's placement and move planning.</summary>
/// <param name="SourcePath">Device source path (part of the file's identity).</param>
/// <param name="SourceSize">Source size in bytes (identity, and the expected on-disk size when reconciling a move).</param>
/// <param name="DestPath">Current destination path relative to the archive root.</param>
/// <param name="ExifDateTimeOriginal">Parsed EXIF capture time (wall clock), or <see langword="null"/>.</param>
/// <param name="SourceMtime">Parsed source modified time, or <see langword="null"/>.</param>
public sealed record ReorganizeEntry(
    string SourcePath,
    long SourceSize,
    string DestPath,
    DateTime? ExifDateTimeOriginal,
    DateTimeOffset? SourceMtime);

/// <summary>A device recorded in the journal's <c>devices</c> table.</summary>
/// <param name="Udid">Device UDID / Unique ID.</param>
/// <param name="Name">Device name, if known.</param>
/// <param name="Model">Device product type/model, if known.</param>
/// <param name="LastSeen">UTC time the device was last seen, or <see langword="null"/> when the column is empty.</param>
/// <param name="HardwareSerial">Hardware serial number, if known.</param>
/// <param name="DeviceType">Device type category (iPhone, Android, etc.).</param>
public sealed record DeviceRecord(
    string Udid,
    string? Name,
    string? Model,
    DateTimeOffset? LastSeen = null,
    string? HardwareSerial = null,
    string? DeviceType = null);

/// <summary>
/// A recorded backup session grouping files transferred together in a batch (e.g. 100 photos on 2026-10-10).
/// </summary>
public sealed record BackupSessionRecord(
    long Id,
    string DeviceUid,
    string? DeviceName,
    string? DeviceModel,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int FilesCount,
    long TotalSizeBytes,
    string Status,
    string? ErrorMessage);

/// <summary>
/// A specific file transfer history item under a backup session (Redundant/Audit data, safe to prune).
/// </summary>
public sealed record BackupHistoryItemRecord(
    long Id,
    long SessionId,
    string DeviceSourcePath,
    string DestPath,
    long FileSize,
    DateTimeOffset TransferredAt,
    string? MediaType);

/// <summary>
/// Metrics regarding database physical storage footprint and table record counts.
/// </summary>
public sealed record DatabaseMetricsRecord(
    long DatabaseSizeBytes,
    long WalSizeBytes,
    int CoreFilesCount,
    int HistoryRecordsCount,
    int BackupSessionsCount);

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
