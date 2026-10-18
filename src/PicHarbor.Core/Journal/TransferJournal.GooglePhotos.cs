using Microsoft.Data.Sqlite;

namespace PicHarbor.Core.Journal;

public sealed partial class TransferJournal
{
    /// <summary>Saves upload results in short, durable batches; progress counts committed records.</summary>
    public void RecordGooglePhotosSyncBatch(IReadOnlyList<GooglePhotosSyncRecord> records,
        Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (records.Count == 0 || !TableExists("google_photos_sync_records")) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO google_photos_sync_records (dest_path, media_key, uploaded_at, album_name, file_size)
            VALUES ($dest, $media_key, $uploaded_at, $album_name, $file_size)
            ON CONFLICT(dest_path) DO UPDATE SET
                media_key = excluded.media_key, uploaded_at = excluded.uploaded_at,
                album_name = excluded.album_name, file_size = excluded.file_size;
            """;
        command.Parameters.Add("$dest", SqliteType.Text);
        command.Parameters.Add("$media_key", SqliteType.Text);
        command.Parameters.Add("$uploaded_at", SqliteType.Text);
        command.Parameters.Add("$album_name", SqliteType.Text);
        command.Parameters.Add("$file_size", SqliteType.Integer);
        // Release the SQLite write lock between batches so maintenance and other tasks can proceed.
        const int batchSize = 256;
        for (int offset = 0; offset < records.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(offset + batchSize, records.Count);
            using (var transaction = connection.BeginTransaction())
            {
                command.Transaction = transaction;
                for (int index = offset; index < end; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = records[index];
                    ArgumentException.ThrowIfNullOrWhiteSpace(record.DestPath);
                    command.Parameters["$dest"].Value = record.DestPath;
                    command.Parameters["$media_key"].Value = (object?)record.MediaKey ?? DBNull.Value;
                    command.Parameters["$uploaded_at"].Value = IsoUtc(record.UploadedAt)!;
                    command.Parameters["$album_name"].Value = (object?)record.AlbumName ?? DBNull.Value;
                    command.Parameters["$file_size"].Value = record.FileSize;
                    command.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            progress?.Invoke(end);
        }
    }
}

/// <summary>A successfully resolved cloud result ready to persist to the local journal.</summary>
public sealed record GooglePhotosSyncRecord(string DestPath, string? MediaKey, DateTimeOffset UploadedAt,
    string? AlbumName, long FileSize);
