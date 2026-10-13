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
public sealed partial class TransferJournal : IDisposable
{
    /// <summary>Hidden metadata package folder inside the archive destination root.</summary>
    public const string MetadataFolderName = ".picharbor";

    /// <summary>The journal/manifest filename written at the destination root or inside .picharbor.</summary>
    public const string DatabaseFileName = "picharbor.db";

    /// <summary>Legacy database filename for backwards compatibility.</summary>
    public const string LegacyDatabaseFileName = "get-and-see.db";

    /// <summary>Current journal schema version (bumped when tables are added; migrated in place).</summary>
    public const long SchemaVersion = 8;

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

        if (!File.Exists(LongPath.ToExtended(modernPath)) && File.Exists(LongPath.ToExtended(databasePath)))
        {
            EnsureMetadataDirectory(destinationRoot);
            databasePath = RelocateDatabase(databasePath, modernPath);
        }
        else if (string.Equals(databasePath, modernPath, StringComparison.OrdinalIgnoreCase))
            EnsureMetadataDirectory(destinationRoot);

        var builder = new SqliteConnectionStringBuilder { DataSource = LongPath.ToExtended(databasePath) };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        connection.CreateFunction("picharbor_writer_version", () => SchemaVersion, isDeterministic: true);

        var journal = new TransferJournal(connection) { DatabasePath = databasePath };
        try { journal.InitializeSchema(); return journal; }
        catch { connection.Dispose(); throw; }
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
        var journal = new TransferJournal(connection) { DatabasePath = databasePath };
        if (journal.QueryUserVersion() > SchemaVersion)
        {
            journal.Dispose();
            throw new InvalidDataException("This photo library requires a newer version of PicHarbor.");
        }
        return journal;
    }

    private void InitializeSchema()
    {
        long existingVersion = QueryUserVersion();
        if (existingVersion > SchemaVersion)
            throw new InvalidDataException("This photo library requires a newer version of PicHarbor.");
        if (existingVersion < SchemaVersion && TableExists("files")) BackupBeforeMigration();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        using var migration = connection.BeginTransaction();
        schemaTransaction = migration;
        Execute(
            """
            CREATE TABLE IF NOT EXISTS files (
                id                     INTEGER PRIMARY KEY,
                source_path            TEXT    NOT NULL,
                device_id              TEXT    NOT NULL DEFAULT '',
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
        EnsureColumnExists("files", "device_id", "TEXT NOT NULL DEFAULT ''");
        Execute("DROP INDEX IF EXISTS ux_files_identity;");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_files_device_identity ON files (device_id, source_path, source_size);");
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
            CREATE TABLE IF NOT EXISTS iphone_sync_folder_history (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                udid           TEXT,
                device_model   TEXT NOT NULL,
                folder         TEXT NOT NULL,
                changed_at     TEXT NOT NULL
            );
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS scope_subfolder_selections (
                scope_kind     TEXT NOT NULL,
                device_key     TEXT NOT NULL,
                node_path      TEXT NOT NULL,
                updated_at     TEXT NOT NULL,
                PRIMARY KEY (scope_kind, device_key, node_path)
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
        EnsureColumnExists("iphone_devices", "udid", "TEXT");
        EnsureColumnExists("iphone_devices", "folder_name", "TEXT");
        EnsureColumnExists("iphone_manual_selections", "udid", "TEXT");
        EnsureColumnExists("iphone_exported_files", "udid", "TEXT");

        // 主键迁移必须在补齐 udid 列之后执行。
        EnsureIphoneUdidPrimaryKey();

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
        // 归档文件夹名与设备身份一起持久化——两者同生共死（库就在归档目录里），不会不同步。
        EnsureColumnExists("devices", "archive_folder", "TEXT");

        Migrate();
        InitializeLibrarySchema();
        ValidateIntegrity();
        migration.Commit();
        schemaTransaction = null;
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
            EnsureColumnExists("devices", "archive_folder", "TEXT");

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
            INSERT OR IGNORE INTO files (device_id, source_path, source_size, source_mtime, state)
            VALUES ($device, $path, $size, $mtime, 'pending');
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
            "SELECT state FROM files WHERE source_path = $path AND source_size = $size AND (device_id=$device OR ($device='' AND (SELECT COUNT(*) FROM files f WHERE f.source_path=$path AND f.source_size=$size)=1));");
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$size", sourceSize);
        return FileStateText.FromText(command.ExecuteScalar() as string);
    }

    /// <summary>Returns the recorded destination path for a file, or null if not yet set.</summary>
    public string? GetDestRelativePath(string sourcePath, long sourceSize)
    {
        using SqliteCommand command = CreateCommand(
            "SELECT dest_path FROM files WHERE source_path = $path AND source_size = $size AND (device_id=$device OR ($device='' AND (SELECT COUNT(*) FROM files f WHERE f.source_path=$path AND f.source_size=$size)=1));");
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
            WHERE device_id = $device AND source_path = $path AND source_size = $size;
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
            WHERE device_id = $device AND source_path = $path AND source_size = $size;
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
        string? previousPath = GetDestRelativePath(sourcePath, sourceSize);
        if (previousPath is not null && previousPath != newRelativeDestPath)
        {
            UpdatePathReferences(previousPath,newRelativeDestPath);
            return;
        }
        using SqliteCommand command = CreateCommand(
            "UPDATE files SET dest_path = $dest WHERE device_id = $device AND source_path = $path AND source_size = $size;");
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
            WHERE device_id = $device AND source_path = $path AND source_size = $size;
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
        string deviceColumn=ColumnExists("files","device_id") ? "device_id" : "''";
        string hashColumn=ColumnExists("files","sha256") ? "sha256" : "NULL";
        string availability=ColumnExists("files","replica_state") ? "AND replica_state='Available'" : "";
        using SqliteCommand command = CreateCommand(
            $"""
            SELECT source_path, source_size, dest_path, exif_datetime_original, source_mtime, {deviceColumn}, {hashColumn}
            FROM files
            WHERE id IN (SELECT MIN(id) FROM files WHERE state='done' AND dest_path IS NOT NULL {availability} GROUP BY dest_path)
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
                ParseIsoOrNull(reader.IsDBNull(4) ? null : reader.GetString(4))) { DeviceId = reader.GetString(5),Sha256=reader.IsDBNull(6) ? null : reader.GetString(6) });
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

    /// <summary>
    /// 仅更新既有设备的硬件序列号（用于人工修正自动识别的结果），不改名、不新建记录。
    /// </summary>
    /// <param name="udid">设备 UDID（本项目中安卓侧即配置的设备标识）。</param>
    /// <param name="hardwareSerial">修正后的硬件序列号；传 <c>null</c> 表示清空。</param>
    /// <returns>是否命中了设备记录。</returns>
    public bool UpdateDeviceHardwareSerial(string udid, string? hardwareSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);

        if (!ColumnExists("devices", "hardware_serial"))
        {
            return false;
        }

        using SqliteCommand command = CreateCommand(
            "UPDATE devices SET hardware_serial = $serial WHERE udid = $udid;");
        command.Parameters.AddWithValue("$udid", udid);
        command.Parameters.AddWithValue("$serial", (object?)hardwareSerial ?? DBNull.Value);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 判断该硬件序列号是否已被<b>其它</b>设备占用。
    /// </summary>
    /// <remarks>
    /// 用于拦截「同一个通用串被多台设备上报」的情况——这类序列号单看一台设备无法发现，
    /// 必须拿已登记的记录做交叉比对。
    /// </remarks>
    public bool IsHardwareSerialTakenByOther(string hardwareSerial, string? excludeUdid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareSerial);

        if (!ColumnExists("devices", "hardware_serial"))
        {
            return false;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT COUNT(*) FROM devices WHERE hardware_serial = $serial AND udid != $udid;");
        command.Parameters.AddWithValue("$serial", hardwareSerial.Trim());
        command.Parameters.AddWithValue("$udid", excludeUdid ?? string.Empty);
        long count = (long)(command.ExecuteScalar() ?? 0L);
        return count > 0;
    }

    /// <summary>
    /// 将既有设备的标识（udid）迁移为新值，保留其全部备份历史。
    /// </summary>
    /// <remarks>
    /// 用于「旧版本已备份、更新后重新识别」的场景：设备身份从旧的软件 ID（如 FTP 主机名）
    /// 换成真实硬件序列号时，必须<b>迁移</b>而不是新建记录，否则历史批次会与新设备脱钩。
    /// 目标标识已被占用时拒绝迁移并返回 <c>false</c>。
    /// </remarks>
    /// <returns>是否完成了迁移。</returns>
    public bool MigrateDeviceUdid(string oldUdid, string newUdid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldUdid);
        ArgumentException.ThrowIfNullOrWhiteSpace(newUdid);

        if (string.Equals(oldUdid, newUdid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 目标标识已被别的设备占用 → 拒绝，避免主键冲突。
        using (SqliteCommand exists = CreateCommand("SELECT COUNT(*) FROM devices WHERE udid = $udid;"))
        {
            exists.Parameters.AddWithValue("$udid", newUdid);
            if ((long)(exists.ExecuteScalar() ?? 0L) > 0)
            {
                return false;
            }
        }

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        try
        {
            (string Table, string Column)[] references =
            {
                ("devices", "udid"),
                ("runs", "device_udid"),
                ("backup_sessions", "device_uid"),
                ("files", "device_id"),
                ("source_exclusions", "device_id"),
            };

            int affectedRows = 0;
            foreach ((string table, string column) in references)
            {
                using SqliteCommand update = CreateCommand(
                    $"UPDATE {table} SET {column} = $new WHERE {column} = $old;");
                update.Transaction = transaction ?? schemaTransaction;
                update.Parameters.AddWithValue("$new", newUdid);
                update.Parameters.AddWithValue("$old", oldUdid);
                affectedRows += update.ExecuteNonQuery();
            }

            transaction?.Commit();

            // 只有真的改到了行才算迁移成功；源记录不存在时不能谎报「已迁移」。
            return affectedRows > 0;
        }
        catch
        {
            transaction?.Rollback();
            return false;
        }
    }

    /// <summary>读取设备的显示名称；设备不存在时返回 <c>null</c>。</summary>
    public string? GetDeviceName(string udid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);

        using SqliteCommand command = CreateCommand("SELECT name FROM devices WHERE udid = $udid;");
        command.Parameters.AddWithValue("$udid", udid);
        return command.ExecuteScalar() as string;
    }

    /// <summary>判断某个显示名称是否已被<b>其它</b>设备占用。</summary>
    public bool IsDeviceNameTakenByOther(string name, string? excludeUdid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using SqliteCommand command = CreateCommand(
            "SELECT COUNT(*) FROM devices WHERE name = $name AND udid != $udid;");
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$udid", excludeUdid ?? string.Empty);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// 更新设备的显示名称（重命名）。
    /// </summary>
    /// <remarks>
    /// <b>只改显示名，绝不触碰归档目录名</b>——归档目录名参与 manifest 的相对路径，
    /// 改动会让已备份文件与新备份文件落进不同目录。
    /// 与其它设备重名时拒绝更新（显示名是用户区分设备的唯一依据）。
    /// </remarks>
    /// <returns>是否更新成功。</returns>
    public bool UpdateDeviceName(string udid, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        if (IsDeviceNameTakenByOther(trimmed, udid))
        {
            return false;
        }

        using SqliteCommand command = CreateCommand("UPDATE devices SET name = $name WHERE udid = $udid;");
        command.Parameters.AddWithValue("$udid", udid);
        command.Parameters.AddWithValue("$name", trimmed);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 读取设备的归档文件夹名（与设备身份一起持久化，不会因配置丢失而失联）。
    /// </summary>
    /// <returns>未记录或设备不存在时返回 <c>null</c>。</returns>
    public string? GetDeviceArchiveFolder(string udid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        if (!ColumnExists("devices", "archive_folder")) return null;

        using SqliteCommand command = CreateCommand("SELECT archive_folder FROM devices WHERE udid = $udid;");
        command.Parameters.AddWithValue("$udid", udid);
        return command.ExecuteScalar() as string;
    }

    /// <summary>记录设备的归档文件夹名。</summary>
    public bool SetDeviceArchiveFolder(string udid, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!ColumnExists("devices", "archive_folder")) return false;

        using SqliteCommand command = CreateCommand(
            "UPDATE devices SET archive_folder = $folder WHERE udid = $udid;");
        command.Parameters.AddWithValue("$udid", udid);
        command.Parameters.AddWithValue("$folder", folder.Trim());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 判断某个归档文件夹名是否已被<b>其它</b>设备占用。
    /// </summary>
    /// <remarks>
    /// 老记录没有 <c>archive_folder</c>，此时以显示名（<c>name</c>）为准——旧版本正是拿显示名当目录名。
    /// </remarks>
    public bool IsDeviceArchiveFolderTakenByOther(string folder, string? excludeUdid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string sql = ColumnExists("devices", "archive_folder")
            ? "SELECT COUNT(*) FROM devices WHERE udid != $udid AND (archive_folder = $folder OR (archive_folder IS NULL AND name = $folder));"
            : "SELECT COUNT(*) FROM devices WHERE udid != $udid AND name = $folder;";

        using SqliteCommand command = CreateCommand(sql);
        command.Parameters.AddWithValue("$folder", folder.Trim());
        command.Parameters.AddWithValue("$udid", excludeUdid ?? string.Empty);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>
    /// 从该设备已备份文件的相对路径推导归档文件夹名（取路径第一段）。
    /// </summary>
    /// <remarks>
    /// 兜底用途：库里没记 <c>archive_folder</c>，且显示名与目录名已不一致（用户改过显示名）时，
    /// 显示名匹配不到目录——但 manifest 里的相对路径第一段就是真实归档目录名。
    /// </remarks>
    /// <returns>推导出的目录名；无从推导时返回 <c>null</c>。</returns>
    public string? InferDeviceArchiveFolderFromHistory(string udid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        if (!TableExists("backup_history_records") || !TableExists("backup_sessions")) return null;

        try
        {
            using SqliteCommand command = CreateCommand(
                """
                SELECT dest_path FROM backup_history_records
                WHERE session_id IN (SELECT id FROM backup_sessions WHERE device_uid = $uid)
                LIMIT 1;
                """);
            command.Parameters.AddWithValue("$uid", udid);

            if (command.ExecuteScalar() is not string destPath || string.IsNullOrWhiteSpace(destPath))
            {
                return null;
            }

            string normalized = destPath.Replace('\\', '/').TrimStart('/');
            int slash = normalized.IndexOf('/');
            string first = slash > 0 ? normalized[..slash] : normalized;
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch
        {
            return null;
        }
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
            WHERE ($uid='' OR device_uid = $uid)
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

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
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
                filesCmd.Transaction = transaction ?? schemaTransaction;
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
                insertSessionCmd.Transaction = transaction ?? schemaTransaction;
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
                    insertFileCmd.Transaction = transaction ?? schemaTransaction;
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
            remainingFilesCmd.Transaction = transaction ?? schemaTransaction;
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
                baselineSessionCmd.Transaction = transaction ?? schemaTransaction;
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
                    insertFileCmd.Transaction = transaction ?? schemaTransaction;
                    insertFileCmd.Parameters.AddWithValue("$sid", bSessionId);
                    insertFileCmd.Parameters.AddWithValue("$sp", f.SourcePath);
                    insertFileCmd.Parameters.AddWithValue("$dp", f.DestPath);
                    insertFileCmd.Parameters.AddWithValue("$sz", f.Size);
                    insertFileCmd.Parameters.AddWithValue("$ta", f.CopiedAt);
                    insertFileCmd.Parameters.AddWithValue("$type", Path.GetExtension(f.SourcePath).TrimStart('.').ToLowerInvariant());
                    insertFileCmd.ExecuteNonQuery();
                }
            }

            transaction?.Commit();
        }
        catch
        {
            transaction?.Rollback();
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
                deleteItems.Transaction = transaction ?? schemaTransaction;
                prunedRecords = deleteItems.ExecuteNonQuery();
            }

            using (SqliteCommand deleteSessions = CreateCommand(
                """
                DELETE FROM backup_sessions
                WHERE started_at < $cutoff AND status != 'InFlight';
                """))
            {
                deleteSessions.Parameters.AddWithValue("$cutoff", cutoffIso);
                deleteSessions.Transaction = transaction ?? schemaTransaction;
                prunedSessions = deleteSessions.ExecuteNonQuery();
            }

            transaction?.Commit();
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
        if (!string.Equals(Path.GetFullPath(destinationRoot), Path.GetDirectoryName(Path.GetDirectoryName(DatabasePath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repair must use the directory containing this archive.");
        _ = Directory.GetFileSystemEntries(destinationRoot);
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
            string fullPath = PicHarbor.Core.Storage.ArchivePathResolver.Resolve(destinationRoot, relPath);
            try { _ = File.GetAttributes(LongPath.ToExtended(fullPath)); }
            catch (FileNotFoundException) { ghostIds.Add(id); removed++; }
            catch (DirectoryNotFoundException) { _ = Directory.GetFileSystemEntries(destinationRoot); ghostIds.Add(id); removed++; }

            if (scanned % 100 == 0)
            {
                progress?.Report((scanned, removed));
            }
        }

        if (ghostIds.Count > 0)
        {
            using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
            // Batch delete in chunks of 500
            for (int i = 0; i < ghostIds.Count; i += 500)
            {
                var chunk = ghostIds.Skip(i).Take(500).ToList();
                string inClause = string.Join(",", chunk);
                using SqliteCommand deleteCmd = CreateCommand($"UPDATE files SET state = 'missing', replica_state = 'Missing' WHERE id IN ({inClause});");
                deleteCmd.Transaction = transaction ?? schemaTransaction;
                deleteCmd.ExecuteNonQuery();
            }
            transaction?.Commit();
        }

        ValidateIntegrity();
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
        if (schemaTransaction is not null) command.Transaction = schemaTransaction;
        if (System.Text.RegularExpressions.Regex.IsMatch(sql, @"\$device\b") &&
            (sql.Contains("source_path", StringComparison.Ordinal) || sql.Contains("source_exclusions", StringComparison.Ordinal)))
            command.Parameters.AddWithValue("$device", deviceId);
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
        catch (SqliteException) { throw; }
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

    /// <summary>一次性迁移标记：iPhone 三张表的主键已迁到 <c>udid</c>。</summary>
    private const string IphoneUdidPkMarker = "iphone_udid_pk_v1";

    /// <summary>旧行没有 udid 时的占位值；设备真正连接后会被回填成真实 udid。</summary>
    private static string LegacyUdid(string deviceModel) => "legacy:" + deviceModel;

    /// <summary>
    /// 一次性迁移：把 iPhone 三张表的主键从「显示名 / 目标路径」迁到 <c>udid</c>。
    /// </summary>
    /// <remarks>
    /// 旧主键 <c>device_model</c> 里实际存的是<b>显示名</b>，两台同名设备会互相覆盖；
    /// 而 <c>iphone_exported_files</c> 的旧主键是 <c>dest_path</c>，导致「已导出」记录被<b>所有</b> iPhone 共享
    /// ——第二台 iPhone 会跳过全部照片。迁移后每台设备各自记账。
    /// 旧行 <c>udid</c> 为空时回填 <see cref="LegacyUdid"/>；设备真正连接后由 <c>BackfillDeviceUdid</c>
    /// 改成真实 udid，历史记录得以保留。
    /// </remarks>
    private void EnsureIphoneUdidPrimaryKey()
    {
        if (!TableExists("iphone_devices")) return;
        if (GetRawSetting(IphoneUdidPkMarker) is not null) return;

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        try
        {
            // ① iphone_devices：device_model → udid
            ExecuteIn(transaction ?? schemaTransaction!, """
                CREATE TABLE iphone_devices_new (
                    udid           TEXT PRIMARY KEY,
                    device_model   TEXT NOT NULL,
                    sync_folder    TEXT NOT NULL,
                    last_exported  TEXT NOT NULL,
                    folder_name    TEXT
                );
                """);
            ExecuteIn(transaction ?? schemaTransaction!, """
                INSERT INTO iphone_devices_new (udid, device_model, sync_folder, last_exported, folder_name)
                SELECT COALESCE(NULLIF(TRIM(udid), ''), 'legacy:' || device_model),
                       device_model, sync_folder, last_exported, folder_name
                FROM iphone_devices;
                """);
            ExecuteIn(transaction ?? schemaTransaction!, "DROP TABLE iphone_devices;");
            ExecuteIn(transaction ?? schemaTransaction!, "ALTER TABLE iphone_devices_new RENAME TO iphone_devices;");

            // ② iphone_manual_selections：(device_model, dest_path) → (udid, dest_path)
            if (TableExists("iphone_manual_selections"))
            {
                ExecuteIn(transaction ?? schemaTransaction!, """
                    CREATE TABLE iphone_manual_selections_new (
                        udid           TEXT NOT NULL,
                        dest_path      TEXT NOT NULL,
                        device_model   TEXT NOT NULL,
                        added_at       TEXT NOT NULL,
                        is_autofilled  INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (udid, dest_path)
                    );
                    """);
                ExecuteIn(transaction ?? schemaTransaction!, """
                    INSERT INTO iphone_manual_selections_new (udid, dest_path, device_model, added_at, is_autofilled)
                    SELECT COALESCE(NULLIF(TRIM(udid), ''), 'legacy:' || device_model),
                           dest_path, device_model, added_at, is_autofilled
                    FROM iphone_manual_selections;
                    """);
                ExecuteIn(transaction ?? schemaTransaction!, "DROP TABLE iphone_manual_selections;");
                ExecuteIn(transaction ?? schemaTransaction!, "ALTER TABLE iphone_manual_selections_new RENAME TO iphone_manual_selections;");
            }

            // ③ iphone_exported_files：dest_path → (udid, dest_path)
            if (TableExists("iphone_exported_files"))
            {
                ExecuteIn(transaction ?? schemaTransaction!, """
                    CREATE TABLE iphone_exported_files_new (
                        udid           TEXT NOT NULL,
                        dest_path      TEXT NOT NULL,
                        exported_path  TEXT NOT NULL,
                        device_model   TEXT NOT NULL,
                        exported_at    TEXT NOT NULL,
                        file_size      INTEGER NOT NULL,
                        sha256         TEXT,
                        PRIMARY KEY (udid, dest_path)
                    );
                    """);
                ExecuteIn(transaction ?? schemaTransaction!, """
                    INSERT INTO iphone_exported_files_new (udid, dest_path, exported_path, device_model, exported_at, file_size, sha256)
                    SELECT COALESCE(NULLIF(TRIM(udid), ''), 'legacy:' || device_model),
                           dest_path, exported_path, device_model, exported_at, file_size, sha256
                    FROM iphone_exported_files;
                    """);
                ExecuteIn(transaction ?? schemaTransaction!, "DROP TABLE iphone_exported_files;");
                ExecuteIn(transaction ?? schemaTransaction!, "ALTER TABLE iphone_exported_files_new RENAME TO iphone_exported_files;");
            }

            SetRawSetting(transaction ?? schemaTransaction!, IphoneUdidPkMarker, "1");
            transaction?.Commit();
        }
        catch
        {
            transaction?.Rollback();
            throw;
        }
    }

    /// <summary>在指定事务内执行一条语句。</summary>
    private void ExecuteIn(SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
        command.CommandText = sql;
        if (schemaTransaction is not null) command.Transaction = schemaTransaction;
        if (sql.Contains("$device", StringComparison.Ordinal) && !sql.Contains("$device_id", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$device", deviceId);
        command.ExecuteNonQuery();
    }

    /// <summary>读取 <c>settings</c> 表中的原始值；不存在时返回 <c>null</c>。</summary>
    private string? GetRawSetting(string key)
    {
        if (!TableExists("settings")) return null;
        try
        {
            using SqliteCommand command = CreateCommand("SELECT value FROM settings WHERE key = $key;");
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在指定事务内写入 <c>settings</c> 表。</summary>
    private void SetRawSetting(SqliteTransaction transaction, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
        command.CommandText =
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
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

        string udid = string.IsNullOrWhiteSpace(record.Udid)
            ? LegacyUdid(record.DeviceModel)
            : record.Udid.Trim();

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO iphone_exported_files (udid, dest_path, exported_path, device_model, exported_at, file_size, sha256)
            VALUES ($udid, $dest, $exported, $model, $at, $size, $sha)
            ON CONFLICT(udid, dest_path) DO UPDATE SET
                exported_path = excluded.exported_path,
                device_model = excluded.device_model,
                exported_at = excluded.exported_at,
                file_size = excluded.file_size,
                sha256 = excluded.sha256;
            """);
        command.Parameters.AddWithValue("$udid", udid);
        command.Parameters.AddWithValue("$dest", record.DestPath);
        command.Parameters.AddWithValue("$exported", record.ExportedPath);
        command.Parameters.AddWithValue("$model", record.DeviceModel);
        command.Parameters.AddWithValue("$at", IsoUtc(record.ExportedAt)!);
        command.Parameters.AddWithValue("$size", record.FileSize);
        command.Parameters.AddWithValue("$sha", (object?)record.Sha256 ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes a row from <c>iphone_exported_files</c> for the referenced device.
    /// </summary>
    /// <remarks>
    /// 必须带设备身份——「已导出」记录是<b>按设备</b>记账的，只按 dest_path 删除会误伤其它设备。
    /// </remarks>
    public void DeleteIPhoneExportedFile(IPhoneDeviceRef reference, string destPath)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrEmpty(destPath);
        if (!TableExists("iphone_exported_files")) return;

        using SqliteCommand command = CreateCommand(
            "DELETE FROM iphone_exported_files WHERE dest_path = $dest AND udid = $dev_udid;");
        command.Parameters.AddWithValue("$dest", destPath);
        command.Parameters.AddWithValue("$dev_udid", EffectiveUdid(reference));
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
            INSERT INTO iphone_devices (udid, device_model, sync_folder, last_exported)
            VALUES ('legacy:' || $model, $model, $folder, $at)
            ON CONFLICT(udid) DO UPDATE SET
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
            INSERT INTO iphone_manual_selections (udid, device_model, dest_path, added_at, is_autofilled)
            VALUES ('legacy:' || $model, $model, $dest, $at, $is_autofilled)
            ON CONFLICT(udid, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
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
            string dir = Path.GetDirectoryName(ResolveSelectionPath(autoItem).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(autoItem));

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(ResolveSelectionPath(m).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(m));
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

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
        command.CommandText =
            """
            INSERT INTO iphone_manual_selections (udid, device_model, dest_path, added_at, is_autofilled)
            VALUES ('legacy:' || $model, $model, $dest, $at, $is_autofilled)
            ON CONFLICT(udid, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
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

        transaction?.Commit();
    }

    /// <summary>Removes multiple destination paths from manual selections inside a single transaction.</summary>
    public void BatchRemoveManualSelections(string deviceModel, IEnumerable<string> destPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceModel);
        ArgumentNullException.ThrowIfNull(destPaths);

        if (!TableExists("iphone_manual_selections")) return;

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
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

        transaction?.Commit();
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

    // ------------------------------------------------------------------
    // UDID-aware iPhone APIs (IPhoneDeviceRef).
    // Reads match by udid first and lazily backfill the udid column on rows
    // keyed by the legacy device_model name; writes always carry the udid.
    // ------------------------------------------------------------------

    /// <summary>设备在归档库中的有效身份键：有 udid 用 udid，否则用 legacy 占位（迁移前的旧行）。</summary>
    private static string EffectiveUdid(IPhoneDeviceRef reference) =>
        reference.HasUdid ? reference.Udid!.Trim() : LegacyUdid(reference.FallbackKey);

    private static string DeviceMatchSql(IPhoneDeviceRef reference, bool hasUdidColumn) =>
        hasUdidColumn
            ? "(udid = $dev_udid OR udid = $dev_legacy)"
            : "device_model = $dev_fb";

    private static void BindDeviceParams(SqliteCommand command, IPhoneDeviceRef reference)
    {
        // 无 udid 时绑定空串：udid 列迁移后永不为空，因此不会误匹配。
        command.Parameters.AddWithValue("$dev_udid", reference.HasUdid ? reference.Udid!.Trim() : string.Empty);
        command.Parameters.AddWithValue("$dev_legacy", LegacyUdid(reference.FallbackKey));
        command.Parameters.AddWithValue("$dev_eff", EffectiveUdid(reference));
        command.Parameters.AddWithValue("$dev_fb", reference.FallbackKey);
    }

    /// <summary>
    /// 把某张表里属于该设备的 legacy 行改写成真实 udid。
    /// </summary>
    /// <remarks>
    /// 用 <c>UPDATE OR IGNORE</c>：目标表可能是复合主键（如 <c>(udid, dest_path)</c>），
    /// 真实 udid 行已存在时跳过冲突项；随后清掉残留的 legacy 行（已迁移或重复）。
    /// </remarks>
    private void BackfillDeviceUdid(string table, IPhoneDeviceRef reference)
    {
        if (!reference.HasUdid || !TableExists(table) || !ColumnExists(table, "udid")) return;
        try
        {
            using (SqliteCommand update = CreateCommand(
                $"UPDATE OR IGNORE {table} SET udid = $dev_udid WHERE udid = $dev_legacy;"))
            {
                BindDeviceParams(update, reference);
                update.ExecuteNonQuery();
            }

            using SqliteCommand cleanup = CreateCommand($"DELETE FROM {table} WHERE udid = $dev_legacy;");
            BindDeviceParams(cleanup, reference);
            cleanup.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>Returns the recorded <c>iphone_devices</c> row for the referenced device, preferring the UDID row.</summary>
    public IPhoneDeviceRecord? GetIPhoneDevice(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        if (!TableExists("iphone_devices")) return null;

        bool hasUdidCol = ColumnExists("iphone_devices", "udid");
        bool hasFolderCol = ColumnExists("iphone_devices", "folder_name");
        string selectCols = hasUdidCol && hasFolderCol
            ? "device_model, sync_folder, last_exported, udid, folder_name"
            : "device_model, sync_folder, last_exported";
        using SqliteCommand command = CreateCommand(
            $"SELECT {selectCols} FROM iphone_devices WHERE {DeviceMatchSql(reference, hasUdidCol)} LIMIT 1;");
        BindDeviceParams(command, reference);
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            var record = hasUdidCol && hasFolderCol && !reader.IsDBNull(3)
                ? new IPhoneDeviceRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4))
                : new IPhoneDeviceRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
            BackfillDeviceUdid("iphone_devices", reference);
            return record;
        }

        return null;
    }

    /// <summary>
    /// Upserts the iPhone device row for the referenced device. <paramref name="folderName"/> is the
    /// sticky sync-folder name: it is only written when the row has none, so later device renames
    /// never move the directory. A sync-folder change is appended to the history table.
    /// </summary>
    public void UpsertIPhoneDevice(IPhoneDeviceRef reference, string syncFolder, DateTimeOffset lastExported, string? folderName = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncFolder);

        bool hasUdidCol = ColumnExists("iphone_devices", "udid");
        bool hasFolderCol = ColumnExists("iphone_devices", "folder_name");

        string? previousFolder = null;
        if (TableExists("iphone_devices"))
        {
            using SqliteCommand readCmd = CreateCommand(
                $"SELECT sync_folder FROM iphone_devices WHERE {DeviceMatchSql(reference, hasUdidCol)} LIMIT 1;");
            BindDeviceParams(readCmd, reference);
            previousFolder = readCmd.ExecuteScalar() as string;
        }

        if (!string.IsNullOrWhiteSpace(previousFolder) &&
            !string.Equals(previousFolder, syncFolder, StringComparison.OrdinalIgnoreCase))
        {
            AppendIPhoneSyncFolderHistory(reference, previousFolder, DateTimeOffset.UtcNow);
        }

        int updated = 0;
        if (TableExists("iphone_devices"))
        {
            using SqliteCommand updateCmd = CreateCommand(
                $"""
                UPDATE iphone_devices
                SET sync_folder = $folder,
                    last_exported = $at,
                    device_model = $dev_fb
                    {(hasUdidCol ? ", udid = COALESCE(udid, $dev_udid)" : string.Empty)}
                    {(hasFolderCol ? ", folder_name = COALESCE(folder_name, $fname)" : string.Empty)}
                WHERE {DeviceMatchSql(reference, hasUdidCol)};
                """);
            BindDeviceParams(updateCmd, reference);
            updateCmd.Parameters.AddWithValue("$folder", syncFolder);
            updateCmd.Parameters.AddWithValue("$at", IsoUtc(lastExported)!);
            updateCmd.Parameters.AddWithValue("$fname", (object?)folderName ?? reference.FallbackKey);
            updated = updateCmd.ExecuteNonQuery();
        }

        if (updated == 0)
        {
            using SqliteCommand insertCmd = CreateCommand(
                $"""
                INSERT INTO iphone_devices (udid, device_model, sync_folder, last_exported
                    {(hasFolderCol ? ", folder_name" : string.Empty)})
                VALUES ($dev_eff, $dev_fb, $folder, $at
                    {(hasFolderCol ? ", $fname" : string.Empty)})
                ON CONFLICT(udid) DO UPDATE SET
                    sync_folder = excluded.sync_folder,
                    last_exported = excluded.last_exported;
                """);
            BindDeviceParams(insertCmd, reference);
            insertCmd.Parameters.AddWithValue("$folder", syncFolder);
            insertCmd.Parameters.AddWithValue("$at", IsoUtc(lastExported)!);
            if (hasFolderCol)
            {
                insertCmd.Parameters.AddWithValue("$fname", (object?)folderName ?? reference.FallbackKey);
            }
            insertCmd.ExecuteNonQuery();
        }
    }

    /// <summary>Returns all manually selected destination relative paths for the referenced device.</summary>
    public HashSet<string> GetManualSelections(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("iphone_manual_selections"))
        {
            return set;
        }

        bool hasUdidCol = ColumnExists("iphone_manual_selections", "udid");
        using SqliteCommand command = CreateCommand(
            $"SELECT dest_path FROM iphone_manual_selections WHERE {DeviceMatchSql(reference, hasUdidCol)};");
        BindDeviceParams(command, reference);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        BackfillDeviceUdid("iphone_manual_selections", reference);
        return set;
    }

    /// <summary>Returns manual selections mapping to whether they were auto-filled, for the referenced device.</summary>
    public Dictionary<string, bool> GetManualSelectionsWithAutofilled(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("iphone_manual_selections"))
        {
            return dict;
        }

        bool hasUdidCol = ColumnExists("iphone_manual_selections", "udid");
        using SqliteCommand command = CreateCommand(
            $"SELECT dest_path, is_autofilled FROM iphone_manual_selections WHERE {DeviceMatchSql(reference, hasUdidCol)};");
        BindDeviceParams(command, reference);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            dict[reader.GetString(0)] = reader.GetInt32(1) == 1;
        }

        BackfillDeviceUdid("iphone_manual_selections", reference);
        return dict;
    }

    /// <summary>Adds a destination path to manual selections for the referenced device. Returns true if newly added.</summary>
    public bool AddManualSelection(IPhoneDeviceRef reference, string destPath, bool isAutofilled = false)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        bool hasUdidCol = ColumnExists("iphone_manual_selections", "udid");
        using SqliteCommand command = CreateCommand(
            $"""
            INSERT INTO iphone_manual_selections (udid, device_model, dest_path, added_at, is_autofilled)
            VALUES ($dev_eff, $dev_fb, $dest, $at, $is_autofilled)
            ON CONFLICT(udid, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """);
        BindDeviceParams(command, reference);
        command.Parameters.AddWithValue("$dest", destPath);
        command.Parameters.AddWithValue("$at", IsoUtc(DateTimeOffset.UtcNow)!);
        command.Parameters.AddWithValue("$is_autofilled", isAutofilled ? 1 : 0);
        BackfillDeviceUdid("iphone_manual_selections", reference);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Removes a destination path from manual selections and cascades cleanup of orphaned auto-filled pairs.</summary>
    public void RemoveManualSelection(IPhoneDeviceRef reference, string destPath)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        if (!TableExists("iphone_manual_selections")) return;

        bool hasUdidCol = ColumnExists("iphone_manual_selections", "udid");
        using SqliteCommand command = CreateCommand(
            $"DELETE FROM iphone_manual_selections WHERE {DeviceMatchSql(reference, hasUdidCol)} AND dest_path = $dest;");
        BindDeviceParams(command, reference);
        command.Parameters.AddWithValue("$dest", destPath);
        command.ExecuteNonQuery();

        CascadeRemoveOrphanedAutofills(reference);
    }

    /// <summary>Removes orphaned auto-filled selections for the referenced device.</summary>
    public void CascadeRemoveOrphanedAutofills(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!TableExists("iphone_manual_selections")) return;
        var dict = GetManualSelectionsWithAutofilled(reference);
        var autofilledItems = dict.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        var manualItems = dict.Where(kv => !kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toRemove = new List<string>();
        foreach (var autoItem in autofilledItems)
        {
            string dir = Path.GetDirectoryName(ResolveSelectionPath(autoItem).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(autoItem));

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(ResolveSelectionPath(m).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(m));
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
            BatchRemoveManualSelections(reference, toRemove);
        }
    }

    /// <summary>Adds multiple destination paths to manual selections for the referenced device in one transaction.</summary>
    public void BatchAddManualSelections(IPhoneDeviceRef reference, IEnumerable<string> destPaths, bool isAutofilled = false)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentNullException.ThrowIfNull(destPaths);

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
        command.CommandText =
            $"""
            INSERT INTO iphone_manual_selections (udid, device_model, dest_path, added_at, is_autofilled)
            VALUES ($dev_eff, $dev_fb, $dest, $at, $is_autofilled)
            ON CONFLICT(udid, dest_path) DO UPDATE SET is_autofilled = excluded.is_autofilled;
            """;
        BindDeviceParams(command, reference);
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);
        command.Parameters.Add("$at", SqliteType.Text).Value = IsoUtc(DateTimeOffset.UtcNow)!;
        command.Parameters.Add("$is_autofilled", SqliteType.Integer).Value = isAutofilled ? 1 : 0;

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }

        transaction?.Commit();
        BackfillDeviceUdid("iphone_manual_selections", reference);
    }

    /// <summary>Removes multiple destination paths from manual selections for the referenced device in one transaction.</summary>
    public void BatchRemoveManualSelections(IPhoneDeviceRef reference, IEnumerable<string> destPaths)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentNullException.ThrowIfNull(destPaths);

        if (!TableExists("iphone_manual_selections")) return;

        bool hasUdidCol = ColumnExists("iphone_manual_selections", "udid");
        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
        command.CommandText = $"DELETE FROM iphone_manual_selections WHERE {DeviceMatchSql(reference, hasUdidCol)} AND dest_path = $dest;";
        BindDeviceParams(command, reference);
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }

        transaction?.Commit();
    }

    /// <summary>Clears all manual selections for the referenced device.</summary>
    public void ClearManualSelections(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        if (!TableExists("iphone_manual_selections")) return;

        bool hasUdidCol = ColumnExists("iphone_manual_selections", "udid");
        using SqliteCommand command = CreateCommand(
            $"DELETE FROM iphone_manual_selections WHERE {DeviceMatchSql(reference, hasUdidCol)};");
        BindDeviceParams(command, reference);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns all exported file records for the referenced device from <c>iphone_exported_files</c>.</summary>
    public IReadOnlyList<IPhoneExportedFileRecord> GetIPhoneExportedFiles(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        var result = new List<IPhoneExportedFileRecord>();
        if (!TableExists("iphone_exported_files"))
        {
            return result;
        }

        bool hasUdidCol = ColumnExists("iphone_exported_files", "udid");
        string selectCols = hasUdidCol
            ? "dest_path, exported_path, device_model, exported_at, file_size, sha256, udid"
            : "dest_path, exported_path, device_model, exported_at, file_size, sha256";
        using SqliteCommand command = CreateCommand(
            $"SELECT {selectCols} FROM iphone_exported_files WHERE {DeviceMatchSql(reference, hasUdidCol)};");
        BindDeviceParams(command, reference);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? udid = hasUdidCol && !reader.IsDBNull(6) ? reader.GetString(6) : null;
            result.Add(new IPhoneExportedFileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                udid));
        }

        BackfillDeviceUdid("iphone_exported_files", reference);
        return result;
    }

    /// <summary>Appends a sync-folder history entry (the folder that has just been abandoned or merged from).</summary>
    public void AppendIPhoneSyncFolderHistory(IPhoneDeviceRef reference, string folder, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!TableExists("iphone_sync_folder_history")) return;

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO iphone_sync_folder_history (udid, device_model, folder, changed_at)
            VALUES ($dev_udid, $dev_fb, $folder, $at);
            """);
        command.Parameters.AddWithValue("$dev_udid", (object?)reference.Udid ?? DBNull.Value);
        command.Parameters.AddWithValue("$dev_fb", reference.FallbackKey);
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$at", IsoUtc(changedAt)!);
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the sync-folder history for the referenced device, newest first.</summary>
    public IReadOnlyList<IPhoneSyncFolderHistoryRecord> GetIPhoneSyncFolderHistory(IPhoneDeviceRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        var result = new List<IPhoneSyncFolderHistoryRecord>();
        if (!TableExists("iphone_sync_folder_history"))
        {
            return result;
        }

        using SqliteCommand command = CreateCommand(
            """
            SELECT udid, device_model, folder, changed_at FROM iphone_sync_folder_history
            WHERE ($dev_udid IS NOT NULL AND udid = $dev_udid) OR device_model = $dev_fb
            ORDER BY changed_at DESC;
            """);
        command.Parameters.AddWithValue("$dev_udid", (object?)reference.Udid ?? DBNull.Value);
        command.Parameters.AddWithValue("$dev_fb", reference.FallbackKey);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IPhoneSyncFolderHistoryRecord(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseIso(reader.GetString(3)) ?? DateTimeOffset.UtcNow));
        }

        return result;
    }

    /// <summary>
    /// Re-points legacy name-keyed rows to the device's new display name (same UDID rename). Rows that
    /// already carry a udid are left untouched. The <c>iphone_devices</c> rename is best-effort: if a row
    /// already exists under the new name (PK conflict) it is skipped.
    /// </summary>
    public void RenameIPhoneDeviceLegacyRows(IPhoneDeviceRef reference, string oldDeviceModel)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.FallbackKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDeviceModel);
        if (string.Equals(oldDeviceModel, reference.FallbackKey, StringComparison.Ordinal)) return;

        foreach (string table in new[] { "iphone_manual_selections", "iphone_exported_files" })
        {
            if (!TableExists(table)) continue;
            bool hasUdidCol = ColumnExists(table, "udid");
            try
            {
                using SqliteCommand command = CreateCommand(
                    hasUdidCol && reference.HasUdid
                    ? $"UPDATE {table} SET device_model = $new_name, udid = COALESCE(udid, $dev_udid) WHERE device_model = $old_name AND (udid IS NULL OR udid = '');"
                    : $"UPDATE {table} SET device_model = $new_name WHERE device_model = $old_name AND (udid IS NULL OR udid = '');");
                if (hasUdidCol && reference.HasUdid)
                {
                    command.Parameters.AddWithValue("$dev_udid", reference.Udid!);
                }
                command.Parameters.AddWithValue("$new_name", reference.FallbackKey);
                command.Parameters.AddWithValue("$old_name", oldDeviceModel);
                command.ExecuteNonQuery();
            }
            catch { }
        }

        if (!TableExists("iphone_devices")) return;
        try
        {
            bool hasUdidCol = ColumnExists("iphone_devices", "udid");
            using SqliteCommand command = CreateCommand(
                hasUdidCol && reference.HasUdid
                ? "UPDATE iphone_devices SET device_model = $new_name, udid = COALESCE(udid, $dev_udid) WHERE device_model = $old_name AND (udid IS NULL OR udid = '');"
                : "UPDATE iphone_devices SET device_model = $new_name WHERE device_model = $old_name AND (udid IS NULL OR udid = '');");
            if (hasUdidCol && reference.HasUdid)
            {
                command.Parameters.AddWithValue("$dev_udid", reference.Udid!);
            }
            command.Parameters.AddWithValue("$new_name", reference.FallbackKey);
            command.Parameters.AddWithValue("$old_name", oldDeviceModel);
            command.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>Returns every recorded <c>iphone_devices</c> row (all devices).</summary>
    public IReadOnlyList<IPhoneDeviceRecord> GetAllIPhoneDevices()
    {
        var result = new List<IPhoneDeviceRecord>();
        if (!TableExists("iphone_devices")) return result;

        bool hasUdidCol = ColumnExists("iphone_devices", "udid");
        bool hasFolderCol = ColumnExists("iphone_devices", "folder_name");
        string selectCols = hasUdidCol && hasFolderCol
            ? "device_model, sync_folder, last_exported, udid, folder_name"
            : "device_model, sync_folder, last_exported";
        using SqliteCommand command = CreateCommand($"SELECT {selectCols} FROM iphone_devices;");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(hasUdidCol && hasFolderCol && !reader.IsDBNull(3)
                ? new IPhoneDeviceRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4))
                : new IPhoneDeviceRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private const string IgnoredSyncFoldersKey = "iphone_syncfolder_ignored";

    /// <summary>Returns the user-ignored sync folder paths (full paths, newline separated in one KV row).</summary>
    public IReadOnlyList<string> GetIgnoredSyncFolders()
    {
        var list = new List<string>();
        if (!TableExists("settings")) return list;

        using SqliteCommand command = CreateCommand("SELECT value FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", IgnoredSyncFoldersKey);
        string? raw = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (string line in raw.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                list.Add(trimmed);
            }
        }

        return list;
    }

    /// <summary>Adds a sync folder path to the ignore list so scans stop suggesting it.</summary>
    public void AddIgnoredSyncFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string normalized = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = new List<string>(GetIgnoredSyncFolders());
        if (existing.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase))) return;
        existing.Add(normalized);

        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """);
        command.Parameters.AddWithValue("$key", IgnoredSyncFoldersKey);
        command.Parameters.AddWithValue("$value", string.Join("\n", existing));
        command.ExecuteNonQuery();
    }

    /// <summary>Records a pending sync-folder merge request in the journal settings table (key: <paramref name="deviceKey"/>).</summary>
    public void SetPendingSyncFolderMerge(string deviceKey, string oldFolder, string newFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFolder);
        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """);
        command.Parameters.AddWithValue("$key", $"iphone_pending_merge_{deviceKey}");
        command.Parameters.AddWithValue("$value", $"{oldFolder}\n{newFolder}");
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the (oldFolder, newFolder) pending merge pair for the device key, or null.</summary>
    public (string OldFolder, string NewFolder)? GetPendingSyncFolderMerge(string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        if (!TableExists("settings")) return null;
        using SqliteCommand command = CreateCommand("SELECT value FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", $"iphone_pending_merge_{deviceKey}");
        string? raw = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int idx = raw.IndexOf('\n');
        if (idx <= 0 || idx >= raw.Length - 1) return null;
        return (raw[..idx], raw[(idx + 1)..]);
    }

    /// <summary>Clears the pending sync-folder merge marker for the device key.</summary>
    public void ClearPendingSyncFolderMerge(string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        if (!TableExists("settings")) return;
        using SqliteCommand command = CreateCommand("DELETE FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", $"iphone_pending_merge_{deviceKey}");
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the checked subfolder relative paths stored for the given scope kind and device key. Empty set when the table is absent (old journal).</summary>
    public HashSet<string> GetScopeSubfolderSelections(string scopeKind, string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists("scope_subfolder_selections"))
        {
            return set;
        }

        using SqliteCommand command = CreateCommand(
            "SELECT node_path FROM scope_subfolder_selections WHERE scope_kind = $kind AND device_key = $device;");
        command.Parameters.AddWithValue("$kind", scopeKind);
        command.Parameters.AddWithValue("$device", deviceKey);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>Replaces the checked subfolder set for the given scope kind and device key (full overwrite in one transaction).</summary>
    public void SetScopeSubfolderSelections(string scopeKind, string deviceKey, IEnumerable<string> checkedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        try
        {
            using (var delete = CreateCommand("DELETE FROM scope_subfolder_selections WHERE scope_kind = $kind AND device_key = $device;"))
            {
                delete.Transaction = transaction ?? schemaTransaction;
                delete.Parameters.AddWithValue("$kind", scopeKind);
                delete.Parameters.AddWithValue("$device", deviceKey);
                delete.ExecuteNonQuery();
            }

            using (var insert = CreateCommand(
                """
                INSERT INTO scope_subfolder_selections (scope_kind, device_key, node_path, updated_at)
                VALUES ($kind, $device, $path, $at)
                ON CONFLICT(scope_kind, device_key, node_path) DO UPDATE SET updated_at = excluded.updated_at;
                """))
            {
                insert.Transaction = transaction ?? schemaTransaction;
                insert.Parameters.AddWithValue("$kind", scopeKind);
                insert.Parameters.AddWithValue("$device", deviceKey);
                var at = insert.Parameters.Add("$at", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                string timestamp = DateTimeOffset.UtcNow.ToString("O");
                at.Value = timestamp;
                foreach (string pathValue in checkedPaths)
                {
                    if (string.IsNullOrWhiteSpace(pathValue)) continue;
                    path.Value = pathValue;
                    insert.ExecuteNonQuery();
                }
            }

            transaction?.Commit();
        }
        catch
        {
            transaction?.Rollback();
            throw;
        }
    }

    /// <summary>Clears the stored checked subfolder set for the given scope kind and device key.</summary>
    public void ClearScopeSubfolderSelections(string scopeKind, string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        if (!TableExists("scope_subfolder_selections")) return;
        using SqliteCommand command = CreateCommand(
            "DELETE FROM scope_subfolder_selections WHERE scope_kind = $kind AND device_key = $device;");
        command.Parameters.AddWithValue("$kind", scopeKind);
        command.Parameters.AddWithValue("$device", deviceKey);
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
            string dir = Path.GetDirectoryName(ResolveSelectionPath(autoItem).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(autoItem));

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(ResolveSelectionPath(m).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(m));
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

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
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

        transaction?.Commit();
    }

    /// <summary>Removes multiple destination paths from manual selections for Android inside a single transaction.</summary>
    public void BatchRemoveAndroidManualSelections(string deviceId, IEnumerable<string> destPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(destPaths);

        if (!TableExists("android_manual_selections")) return;

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
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

        transaction?.Commit();
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
        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
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
        transaction?.Commit();
    }

    /// <summary>Removes multiple destination paths from manual selections for Google Photos inside a single transaction.</summary>
    public void BatchRemoveGooglePhotosManualSelections(IEnumerable<string> destPaths)
    {
        ArgumentNullException.ThrowIfNull(destPaths);
        if (!TableExists("google_photos_manual_selections")) return;

        using var transaction = schemaTransaction is null ? connection.BeginTransaction() : null;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction ?? schemaTransaction;
        command.CommandText = "DELETE FROM google_photos_manual_selections WHERE dest_path = $dest;";
        var destParam = command.Parameters.Add("$dest", SqliteType.Text);

        foreach (var path in destPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            destParam.Value = path;
            command.ExecuteNonQuery();
        }
        transaction?.Commit();
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
            string dir = Path.GetDirectoryName(ResolveSelectionPath(autoItem).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(autoItem));

            bool hasManualParent = manualItems.Any(m =>
            {
                string mDir = Path.GetDirectoryName(ResolveSelectionPath(m).Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
                string mStem = Path.GetFileNameWithoutExtension(ResolveSelectionPath(m));
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
    string? Sha256 = null,
    string? Udid = null);

/// <summary>An iPhone sync device record in <c>iphone_devices</c>.</summary>
public sealed record IPhoneDeviceRecord(
    string DeviceModel,
    string SyncFolder,
    DateTimeOffset LastExported,
    string? Udid = null,
    string? FolderName = null);

/// <summary>
/// Device reference for the UDID-aware iPhone journal APIs. When <see cref="Udid"/> is known, reads
/// match the udid column first and lazily backfill legacy rows keyed by <see cref="FallbackKey"/>
/// (the device display name); when unknown, lookups fall back to the name key (legacy behavior).
/// </summary>
public sealed record IPhoneDeviceRef(string? Udid, string FallbackKey)
{
    /// <summary>Indicates whether a UDID is available for udid-first matching.</summary>
    public bool HasUdid => !string.IsNullOrWhiteSpace(Udid);
}

/// <summary>A historical sync folder recorded in <c>iphone_sync_folder_history</c>.</summary>
public sealed record IPhoneSyncFolderHistoryRecord(
    string? Udid,
    string DeviceModel,
    string Folder,
    DateTimeOffset ChangedAt);

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
    string? SourceMtimeIso)
{
    /// <summary>Physical member root for a unified library manifest.</summary>
    public string? ArchiveRoot { get; init; }
    /// <summary>Stable content identity where verified.</summary>
    public string? MediaId { get; init; }
    /// <summary>Stable manual-selection reference, with legacy path fallback.</summary>
    public string SelectionKey => MediaId is null ? DestPath.Replace('\\','/') : "#media/" + MediaId;
    /// <summary>Stable identities of related Live Photo, sidecar and RAW/JPG components.</summary>
    public IReadOnlyList<string> RelatedMediaIds { get; init; } = [];
    /// <summary>Availability of this media location.</summary>
    public PicHarbor.Core.Storage.ReplicaState Availability { get; init; }
}

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
    double? GpsLongitude)
{
    /// <summary>Archive member root for unified queries.</summary>
    public string? ArchiveRoot { get; init; }
    /// <summary>Stable member identity.</summary>
    public string? StorageId { get; init; }
    /// <summary>Stable identities of related media components.</summary>
    public IReadOnlyList<string> RelatedMediaIds { get; init; } = [];
    /// <summary>Verified content identity for path-independent references.</summary>
    public string? MediaId { get; init; }
    /// <summary>Current physical availability.</summary>
    public PicHarbor.Core.Storage.ReplicaState Availability { get; init; }
}

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
    DateTimeOffset? SourceMtime)
{
    /// <summary>Device owning the source identity; empty for unknown legacy records.</summary>
    public string DeviceId { get; init; } = string.Empty;
    /// <summary>Recorded content digest for validating moves.</summary>
    public string? Sha256 { get; init; }
}

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
    string? ErrorMessage)
{
    /// <summary>Member identity disambiguating locally numbered sessions.</summary>
    public string? StorageId { get; init; }
    /// <summary>Member root containing this session.</summary>
    public string? ArchiveRoot { get; init; }
}

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
