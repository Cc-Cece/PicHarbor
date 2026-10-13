using Microsoft.Data.Sqlite;
using PicHarbor.Core.Storage;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.Journal;

public sealed partial class TransferJournal
{
    private SqliteTransaction? schemaTransaction;
    private string deviceId = string.Empty;

    /// <summary>Scopes source identity operations to one device. Unknown legacy identities remain separate.</summary>
    public void BindDevice(string? id)
    {
        deviceId = id ?? string.Empty;
        if (deviceId.Length == 0 || new SqliteConnectionStringBuilder(connection.ConnectionString).Mode == SqliteOpenMode.ReadOnly) return;
        using var pending = CreateCommand("UPDATE files SET device_id=$device WHERE device_id='' AND state='pending' AND NOT EXISTS(SELECT 1 FROM files f WHERE f.device_id=$device AND f.source_path=files.source_path AND f.source_size=files.source_size);");
        pending.ExecuteNonQuery();
    }

    /// <summary>Reads portable locations without inventing local copies for external replicas.</summary>
    public IReadOnlyList<LibraryMedia> ReadLibraryMedia(string root)
    {
        var identity = GetLibraryIdentity();
        using var cmd = CreateCommand("SELECT id,dest_path,source_size,sha256,replica_state,COALESCE(exif_datetime_original,source_mtime,copied_at),copied_at FROM files WHERE state IN ('done','missing') AND dest_path IS NOT NULL;");
        using var reader = cmd.ExecuteReader();
        var result = new List<LibraryMedia>();
        while (reader.Read())
        {
            string path = reader.GetString(1);
            string? hash = reader.IsDBNull(3) ? null : reader.GetString(3);
            var status = Enum.TryParse<ReplicaState>(reader.GetString(4), out var parsed) ? parsed : ReplicaState.Available;
            result.Add(new(hash ?? identity.StorageId + ":" + reader.GetInt64(0), identity.StorageId, root, path,
                reader.GetInt64(2), hash, status, reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5))) { ArchivedAt=reader.IsDBNull(6) ? null : ParseIso(reader.GetString(6)) });
        }
        return result;
    }

    internal SqliteConnection Connection => connection;

    internal void SetSourceHash(string source,long size,string hash)
    {
        using var command=CreateCommand("UPDATE files SET sha256=$hash,media_id=$hash WHERE device_id=$device AND source_path=$path AND source_size=$size;");
        command.Parameters.AddWithValue("$hash",hash);command.Parameters.AddWithValue("$path",source);command.Parameters.AddWithValue("$size",size);command.ExecuteNonQuery();
    }

    /// <summary>Projects completed or externally verified source identities without inventing local files.</summary>
    public IReadOnlyList<SourceIdentity> ReadSourceIdentities()
    {
        using var command=CreateCommand("SELECT device_id,source_path,source_size,source_mtime,sha256 FROM files WHERE state IN ('done','missing','external') AND sha256 IS NOT NULL;");
        using var reader=command.ExecuteReader();var result=new List<SourceIdentity>();
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),reader.IsDBNull(3)?null:ParseIso(reader.GetString(3)),reader.GetString(4)));
        return result;
    }

    /// <summary>Records a verified source backed by another member without claiming a local physical copy.</summary>
    public void MarkExternalSourceVerified(PicHarbor.Core.Device.RemoteFile file,LibraryMedia media)
    {
        using var command=CreateCommand("UPDATE files SET state='external',sha256=$hash,media_id=$hash,source_mtime=$mtime,dest_path=NULL,replica_root=$root WHERE device_id=$device AND source_path=$path AND source_size=$size;");
        command.Parameters.AddWithValue("$hash",media.Sha256!);command.Parameters.AddWithValue("$mtime",(object?)IsoUtc(file.ModifiedAt)??DBNull.Value);
        command.Parameters.AddWithValue("$root",media.Root);command.Parameters.AddWithValue("$path",file.Path);command.Parameters.AddWithValue("$size",file.Size);command.ExecuteNonQuery();
    }

    private string ResolveSelectionPath(string key)
    {
        if(!key.StartsWith("#media/",StringComparison.Ordinal)) return key;
        string root=Path.GetDirectoryName(Path.GetDirectoryName(DatabasePath))!;
        var media=PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(root).FirstOrDefault(m=>m.SelectionKey==key);
        return media?.DestPath ?? key;
    }

    internal void ConvertManualSelectionReferences()
    {
        using var transaction=connection.BeginTransaction();
        foreach(string table in new[]{"iphone_manual_selections","android_manual_selections","google_photos_manual_selections"})
        {
            using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText=$"UPDATE OR IGNORE {table} SET dest_path='#media/' || (SELECT MIN(sha256) FROM files WHERE files.dest_path={table}.dest_path AND sha256 IS NOT NULL AND replica_state='Available') WHERE dest_path NOT LIKE '#media/%' AND (SELECT COUNT(DISTINCT sha256) FROM files WHERE files.dest_path={table}.dest_path AND sha256 IS NOT NULL AND replica_state='Available')=1;";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal void RecordChangedVersion(string relativePath, string hash, long size, DateTime modified)
    {
        using var command=CreateCommand("""
            INSERT OR IGNORE INTO files(device_id,source_path,source_size,source_mtime,dest_path,state,sha256,media_id,replica_state,copied_at)
            VALUES('local-edit',$identity,$size,$modified,$path,'done',$hash,$hash,'Available',$modified);
            """);
        command.Parameters.AddWithValue("$identity",relativePath+":"+hash);command.Parameters.AddWithValue("$size",size);
        command.Parameters.AddWithValue("$modified",modified.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$path",relativePath);
        command.Parameters.AddWithValue("$hash",hash);command.ExecuteNonQuery();
    }

    internal void UpdatePathReferences(string oldPath, string newPath)
    {
        using var transaction = connection.BeginTransaction();
        foreach (string table in new[] {"files","backup_history_records","android_sync_records","iphone_exported_files","iphone_manual_selections","android_manual_selections","google_photos_sync_records","google_photos_manual_selections"})
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"UPDATE OR IGNORE {table} SET dest_path=$new WHERE dest_path=$old;";
            command.Parameters.AddWithValue("$old",oldPath);command.Parameters.AddWithValue("$new",newPath);command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Reads an existing content digest and maintenance state for one device source.</summary>
    public (string? Hash, ReplicaState State) GetReplicaVerification(string path, long size)
    {
        using var cmd = CreateCommand("SELECT sha256,replica_state FROM files WHERE device_id=$device AND source_path=$path AND source_size=$size;");
        cmd.Parameters.AddWithValue("$path",path); cmd.Parameters.AddWithValue("$size",size);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.IsDBNull(0) ? null : reader.GetString(0), Enum.Parse<ReplicaState>(reader.GetString(1))) : (null,ReplicaState.Missing);
    }

    /// <summary>Updates source attributes after deciding that a file needs copying.</summary>
    public void RefreshSourceMetadata(PicHarbor.Core.Device.RemoteFile file)
    {
        using var cmd = CreateCommand("UPDATE files SET source_mtime=$mtime,replica_state='Available' WHERE device_id=$device AND source_path=$path AND source_size=$size;");
        cmd.Parameters.AddWithValue("$path",file.Path);cmd.Parameters.AddWithValue("$size",file.Size);
        cmd.Parameters.AddWithValue("$mtime",(object?)IsoUtc(file.ModifiedAt) ?? DBNull.Value);cmd.ExecuteNonQuery();
    }

    /// <summary>Finds a surviving linked copy after automated duplicate recycling.</summary>
    public (string Root, string Path, string Hash)? GetLinkedReplica(string sourcePath, long size)
    {
        using var cmd = CreateCommand("SELECT o.keeper_root,o.keeper_path,o.sha256 FROM files f JOIN maintenance_operations o ON o.source_path=f.dest_path WHERE f.device_id=$device AND f.source_path=$path AND f.source_size=$size AND o.state IN ('Recycled','Purged') ORDER BY o.created_at DESC LIMIT 1;");
        cmd.Parameters.AddWithValue("$path",sourcePath);cmd.Parameters.AddWithValue("$size",size);
        using var reader=cmd.ExecuteReader();return reader.Read() ? (reader.GetString(0),reader.GetString(1),reader.GetString(2)) : null;
    }

    /// <summary>Gets the persisted identity of this archive member.</summary>
    public LibraryIdentity GetLibraryIdentity()
    {
        using var cmd = CreateCommand("SELECT library_id, storage_id FROM library_identity WHERE singleton = 1;");
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Library identity is missing.");
        return new(reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Joins this archive to a logical library. A copied archive receives a new storage identity.</summary>
    public void JoinLibrary(string libraryId, bool copied = false)
    {
        using var cmd = CreateCommand("UPDATE library_identity SET library_id = $library, storage_id = $storage WHERE singleton = 1;");
        cmd.Parameters.AddWithValue("$library", libraryId);
        cmd.Parameters.AddWithValue("$storage", copied ? Guid.NewGuid().ToString("N") : GetLibraryIdentity().StorageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads the source timestamp used to identify an existing backup.</summary>
    public DateTimeOffset? GetSourceModifiedAt(string path, long size)
    {
        using var cmd = CreateCommand("SELECT source_mtime FROM files WHERE device_id = $device AND source_path = $path AND source_size = $size;");
        cmd.Parameters.AddWithValue("$path", path); cmd.Parameters.AddWithValue("$size", size);
        return cmd.ExecuteScalar() is string value ? ParseIso(value) : null;
    }

    /// <summary>Returns true if the source has been intentionally excluded after deletion.</summary>
    public bool IsSourceExcluded(string path, long size, DateTimeOffset? modifiedAt = null)
    {
        using var cmd = CreateCommand("SELECT COUNT(*) FROM source_exclusions WHERE device_id = $device AND source_path = $path AND source_size = $size AND source_mtime IS $mtime;");
        cmd.Parameters.AddWithValue("$mtime",(object?)IsoUtc(modifiedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", path); cmd.Parameters.AddWithValue("$size", size);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Preserves a deletion decision without modifying the source device.</summary>
    public void ExcludeSource(string path, long size, DateTimeOffset? modifiedAt = null)
    {
        using var cmd = CreateCommand("INSERT INTO source_exclusions(device_id,source_path,source_size,source_mtime) VALUES ($device,$path,$size,$mtime) ON CONFLICT(device_id,source_path,source_size) DO UPDATE SET source_mtime=excluded.source_mtime;");
        cmd.Parameters.AddWithValue("$mtime",(object?)IsoUtc(modifiedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", path); cmd.Parameters.AddWithValue("$size", size); cmd.ExecuteNonQuery();
    }

    private void InitializeLibrarySchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS library_identity (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), library_id TEXT NOT NULL,
                storage_id TEXT NOT NULL, writer_id TEXT);
            CREATE TABLE IF NOT EXISTS media_content (
                media_id TEXT PRIMARY KEY, size INTEGER NOT NULL CHECK(size>=0), sha256 TEXT NOT NULL,
                UNIQUE(size,sha256));
            CREATE TABLE IF NOT EXISTS source_exclusions (
                device_id TEXT NOT NULL, source_path TEXT NOT NULL, source_size INTEGER NOT NULL CHECK(source_size>=0),
                PRIMARY KEY(device_id,source_path,source_size));
            CREATE TABLE IF NOT EXISTS maintenance_operations (
                operation_id TEXT PRIMARY KEY, kind TEXT NOT NULL, state TEXT NOT NULL,
                source_path TEXT NOT NULL, target_path TEXT NOT NULL, keeper_root TEXT,
                keeper_path TEXT, sha256 TEXT NOT NULL, size INTEGER NOT NULL CHECK(size>=0),
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS migration_anomalies (
                id INTEGER PRIMARY KEY, original_table TEXT NOT NULL, original_id INTEGER, reason TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS migration_quarantine (
                original_table TEXT NOT NULL, original_id INTEGER NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(original_table,original_id));
            """);
        using var identity = CreateCommand("INSERT OR IGNORE INTO library_identity(singleton,library_id,storage_id) VALUES (1,$library,$storage);");
        identity.Parameters.AddWithValue("$library", Guid.NewGuid().ToString("N"));
        identity.Parameters.AddWithValue("$storage", Guid.NewGuid().ToString("N")); identity.ExecuteNonQuery();
        EnsureColumnExists("files", "media_id", "TEXT");
        EnsureColumnExists("files", "replica_state", "TEXT NOT NULL DEFAULT 'Available'");
        EnsureColumnExists("files", "replica_root", "TEXT");
        EnsureColumnExists("source_exclusions", "source_mtime", "TEXT");
        Execute("CREATE INDEX IF NOT EXISTS ix_files_dest_path ON files(dest_path);");
        Execute("""
            DROP VIEW IF EXISTS manifest;
            CREATE VIEW manifest AS
            SELECT source_path,dest_path,source_size AS size_bytes,source_mtime,exif_datetime_original,
                gps_latitude,gps_longitude,camera_make,camera_model,copied_at,sha256
            FROM files WHERE id IN (SELECT MIN(id) FROM files WHERE state='done' AND replica_state='Available' GROUP BY dest_path);
            """);
        using (var foreign = CreateCommand("PRAGMA foreign_key_list(backup_history_records);"))
        using (var reader = foreign.ExecuteReader())
        {
            if (!reader.Read())
            {
                reader.Close();
                Execute("""
                    INSERT OR IGNORE INTO migration_quarantine
                    SELECT 'backup_history_records',h.id,json_object('id',h.id,'session_id',h.session_id,'device_source_path',h.device_source_path,'dest_path',h.dest_path,'file_size',h.file_size,'transferred_at',h.transferred_at,'media_type',h.media_type)
                    FROM backup_history_records h WHERE h.file_size<0 OR NOT EXISTS(SELECT 1 FROM backup_sessions s WHERE s.id=h.session_id);
                    CREATE TABLE backup_history_records_v8 (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL REFERENCES backup_sessions(id),
                        device_source_path TEXT NOT NULL, dest_path TEXT NOT NULL, file_size INTEGER NOT NULL CHECK(file_size>=0),
                        transferred_at TEXT NOT NULL, media_type TEXT);
                    INSERT INTO backup_history_records_v8 SELECT h.* FROM backup_history_records h
                        WHERE h.file_size>=0 AND EXISTS(SELECT 1 FROM backup_sessions s WHERE s.id=h.session_id);
                    DROP TABLE backup_history_records;
                    ALTER TABLE backup_history_records_v8 RENAME TO backup_history_records;
                    CREATE INDEX ix_history_session ON backup_history_records(session_id);
                    """);
            }
        }
        // Existing malformed history remains inspectable; new malformed writes are rejected.
        Execute("""
            INSERT INTO migration_anomalies(original_table,original_id,reason)
                SELECT 'backup_history_records',h.id,'Invalid session or file size' FROM backup_history_records h
                WHERE (h.file_size<0 OR NOT EXISTS(SELECT 1 FROM backup_sessions s WHERE s.id=h.session_id))
                AND NOT EXISTS(SELECT 1 FROM migration_anomalies a WHERE a.original_table='backup_history_records' AND a.original_id=h.id);
            CREATE TRIGGER IF NOT EXISTS history_validate_insert BEFORE INSERT ON backup_history_records
            WHEN NEW.file_size<0 OR NOT EXISTS(SELECT 1 FROM backup_sessions WHERE id=NEW.session_id)
            BEGIN SELECT RAISE(ABORT,'Invalid backup history relationship'); END;
            CREATE TRIGGER IF NOT EXISTS files_validate_insert BEFORE INSERT ON files
            WHEN NEW.source_size<0 OR NEW.state NOT IN ('pending','in_progress','done','failed','missing','external')
            BEGIN SELECT RAISE(ABORT,'Invalid file record'); END;
            CREATE TRIGGER IF NOT EXISTS files_validate_update BEFORE UPDATE ON files
            WHEN NEW.source_size<0 OR NEW.state NOT IN ('pending','in_progress','done','failed','missing','external')
            BEGIN SELECT RAISE(ABORT,'Invalid file record'); END;
            """);
        foreach (string table in new[] {"files","devices","runs","settings","backup_sessions","backup_history_records","library_identity","source_exclusions","maintenance_operations"})
        foreach (string operation in new[] {"INSERT","UPDATE","DELETE"})
            Execute($"CREATE TRIGGER IF NOT EXISTS writer_v8_{table}_{operation} BEFORE {operation} ON {table} BEGIN SELECT CASE WHEN picharbor_writer_version()<8 THEN RAISE(ABORT,'Newer PicHarbor required') END; END;");
    }

    private void ValidateIntegrity()
    {
        using var integrity = CreateCommand("PRAGMA integrity_check;");
        if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Photo library integrity verification failed.");
        using var foreignKeys = CreateCommand("PRAGMA foreign_key_check;");
        using var reader = foreignKeys.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Photo library relationships are inconsistent.");
    }

    private void BackupBeforeMigration()
    {
        string backup = DatabasePath + ".pre-v8";
        if (File.Exists(backup)) return;
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup + ".tmp", Pooling = false }.ToString());
        target.Open(); connection.BackupDatabase(target); target.Close();
        File.Move(backup + ".tmp", backup);
    }

    private static string RelocateDatabase(string source, string target)
    {
        using var original = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LongPath.ToExtended(source), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        original.Open();
        using (var version = original.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt64(version.ExecuteScalar()) > SchemaVersion)
                throw new InvalidDataException("This photo library requires a newer version of PicHarbor.");
        }
        string staging = target + ".relocating";
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LongPath.ToExtended(staging), Pooling = false }.ToString()))
        {
            destination.Open(); original.BackupDatabase(destination);
            using var check = destination.CreateCommand(); check.CommandText = "PRAGMA integrity_check;";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("Unable to relocate the photo library safely.");
        }
        File.Move(staging, target, overwrite: false);
        // The original is intentionally retained as a recovery copy; ResolveDatabasePath prefers the verified target.
        return target;
    }
}
