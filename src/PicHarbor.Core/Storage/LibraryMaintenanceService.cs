using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Storage;

/// <summary>Recoverable content-based archive maintenance. External directories are never scanned.</summary>
public sealed class LibraryMaintenanceService
{
    /// <summary>Lists online recoverable copies without modifying them.</summary>
    public IReadOnlyList<RecycledCopy> ListRecycled(LibraryCatalog catalog)
    {
        var result=new List<RecycledCopy>();
        foreach(var member in catalog.Members.Where(m=>File.Exists(TransferJournal.ResolveDatabasePath(m.Root))))
        {
            using var journal=TransferJournal.OpenReadOnly(member.Root);
            result.AddRange(ReadOperations(journal,"Recycled").Select(o=>new RecycledCopy(member.Root,o.Id,o.Source,o.At)));
        }
        return result.OrderByDescending(c=>c.RecycledAt).ToList();
    }
    /// <summary>Checks included members, recycles identical redundant copies, and conditionally expires old copies.</summary>
    public Task<LibraryCheckResult> CheckAsync(LibraryCatalog catalog, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(catalog, cancellationToken), cancellationToken);

    private static LibraryCheckResult Check(LibraryCatalog catalog, CancellationToken ct)
    {
        var leases = new List<IDisposable>();
        var errors = new List<string>();
        int checkedCount = 0, recycled = 0, purged = 0;
        if(catalog.IsUnincludedCopy) return new(0,0,0,["This copied archive must be explicitly included before maintenance."]);
        try
        {
            var online = catalog.Members.Where(m => Directory.Exists(m.Root)).OrderBy(m => m.StorageId, StringComparer.Ordinal).ToList();
            foreach (var member in online) leases.Add(LibraryWriteCoordinator.Acquire(member.Root));
            var candidates = new List<LibraryMedia>();
            foreach (var member in online)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var journal = TransferJournal.Open(member.Root);
                    if(catalog.PendingLayouts.TryGetValue(member.StorageId,out var scheme))
                    {
                        var reorganizer=new PicHarbor.Core.Reorganize.Reorganizer(journal,new(),member.Root);
                        var reorganized=reorganizer.ExecuteWithLease(reorganizer.Plan(scheme),ct);
                        if(reorganized.Failed==0) catalog.CompleteLayout(member.StorageId);
                        else errors.Add("Some files could not be reorganized in "+member.Root);
                    }
                    Recover(journal, member.Root, errors);
                    foreach (var media in journal.ReadLibraryMedia(member.Root))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (media.State == ReplicaState.Recycled || IsAuxiliary(media.RelativePath)) continue;
                        checkedCount++;
                        try
                        {
                            string path = ArchivePathResolver.Resolve(member.Root, media.RelativePath);
                            string hash;
                            try { hash = Digest(path,ct:ct); }
                            catch (IOException ex) when(ex is FileNotFoundException or DirectoryNotFoundException)
                            {
                                _ = Directory.GetFileSystemEntries(member.Root);
                                string? moved = FindMoved(member.Root, media, ct);
                                if (moved is null) { SetState(journal, media.RelativePath, ReplicaState.Missing); continue; }
                                MoveReferences(journal, media.RelativePath, moved);
                                path = ArchivePathResolver.Resolve(member.Root, moved);
                                hash = Digest(path,ct:ct);
                            }
                            if (media.Sha256 is not null && media.Sha256 != hash || new FileInfo(path).Length != media.Size)
                            {
                                SetState(journal, media.RelativePath, ReplicaState.Changed,media.Sha256);
                                var changed=new FileInfo(path);
                                journal.RecordChangedVersion(media.RelativePath,hash,changed.Length,changed.LastWriteTimeUtc);
                                errors.Add($"Content changed: {media.Root}/{media.RelativePath}");
                                continue;
                            }
                            using var update = journal.Connection.CreateCommand();
                            update.CommandText = "UPDATE files SET state='done',sha256=$hash,media_id=$hash,replica_state='Available' WHERE dest_path=$path AND (sha256 IS NULL OR sha256=$hash); INSERT OR IGNORE INTO media_content VALUES($hash,$size,$hash);";
                            update.Parameters.AddWithValue("$hash", hash); update.Parameters.AddWithValue("$size", new FileInfo(path).Length);
                            string relative = Path.GetRelativePath(member.Root, path);
                            update.Parameters.AddWithValue("$path", relative); update.ExecuteNonQuery();
                            candidates.Add(media with { Sha256 = hash, MediaId = hash, RelativePath = relative, State = ReplicaState.Available });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
                    }
                    purged += Purge(journal, member.Root, catalog,ct, errors);
                    journal.ConvertManualSelectionReferences();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException) { errors.Add(ex.Message); }
            }
            foreach (var group in candidates.GroupBy(m => (m.Size, m.Sha256)).Where(g => g.Count() > 1))
            {
                var ordered = group.OrderBy(m=>catalog.IsPreferred(m) ? 0 : 1).ThenBy(m => IsFixed(m.Root) ? 0 : 1).ThenBy(m => m.ArchivedAt)
                    .ThenBy(m => m.StorageId, StringComparer.Ordinal).ThenBy(m => m.RelativePath, StringComparer.Ordinal).ToList();
                var keeper = ordered[0];
                catalog.SetPreferred(keeper);
                foreach (var duplicate in ordered.Skip(1).DistinctBy(m => (m.StorageId, m.RelativePath)))
                {
                    ct.ThrowIfCancellationRequested();
                    if (duplicate.StorageId == keeper.StorageId && duplicate.RelativePath == keeper.RelativePath) continue;
                    try
                    {
                        using (var protectedCopy=TransferJournal.OpenReadOnly(duplicate.Root))
                        using (var restored=protectedCopy.Connection.CreateCommand())
                        {
                            restored.CommandText="SELECT COUNT(*) FROM maintenance_operations WHERE source_path=$path AND state='Restored';";
                            restored.Parameters.AddWithValue("$path",duplicate.RelativePath);
                            if(Convert.ToInt64(restored.ExecuteScalar())>0) continue;
                        }
                        // Freshly re-read both files immediately before moving. Never rely on old cached hashes.
                        using var keeperStream = new FileStream(ArchivePathResolver.Resolve(keeper.Root,keeper.RelativePath),FileMode.Open,FileAccess.Read,FileShare.Read);
                        if (Convert.ToHexString(SHA256.HashData(keeperStream)).ToLowerInvariant() != keeper.Sha256 ||
                            Digest(ArchivePathResolver.Resolve(duplicate.Root, duplicate.RelativePath),ct:ct) != keeper.Sha256) continue;
                        using var journal = TransferJournal.Open(duplicate.Root);
                        string id = Guid.NewGuid().ToString("N");
                        string target = $".picharbor/recycle/{id}/{Path.GetFileName(duplicate.RelativePath)}";
                        using var prepare = journal.Connection.CreateCommand();
                        prepare.CommandText = "INSERT INTO maintenance_operations VALUES($id,'Duplicate','Prepared',$source,$target,$root,$keeper,$hash,$size,$at);";
                        prepare.Parameters.AddWithValue("$id", id); prepare.Parameters.AddWithValue("$source", duplicate.RelativePath);
                        prepare.Parameters.AddWithValue("$target", target); prepare.Parameters.AddWithValue("$root", keeper.Root);
                        prepare.Parameters.AddWithValue("$keeper", keeper.RelativePath); prepare.Parameters.AddWithValue("$hash", keeper.Sha256!);
                        prepare.Parameters.AddWithValue("$size", duplicate.Size); prepare.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                        prepare.ExecuteNonQuery();
                        string destination = ArchivePathResolver.Resolve(duplicate.Root, target);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Move(ArchivePathResolver.Resolve(duplicate.Root, duplicate.RelativePath), destination);
                        Complete(journal, id, duplicate.RelativePath); recycled++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException) { errors.Add(ex.Message); }
                }
            }
            catalog.ReadMedia(); catalog.Save();
            return new(checkedCount, recycled, purged, errors);
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    /// <summary>Restores one recycled duplicate, retaining source history.</summary>
    public void Restore(string root, string operationId)
    {
        using var lease = LibraryWriteCoordinator.Acquire(root);
        using var journal = TransferJournal.Open(root);
        using var cmd = journal.Connection.CreateCommand();
        cmd.CommandText = "SELECT source_path,target_path,sha256 FROM maintenance_operations WHERE operation_id=$id AND state='Recycled';";
        cmd.Parameters.AddWithValue("$id", operationId);
        string source, target, hash;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("No recoverable copy found.");
            source = reader.GetString(0); target = reader.GetString(1); hash = reader.GetString(2);
        }
        string recycled = ArchivePathResolver.Resolve(root, target);
        if (Digest(recycled) != hash) throw new InvalidDataException("The recycled copy failed verification.");
        string original = ArchivePathResolver.Resolve(root, source);
        cmd.CommandText="UPDATE maintenance_operations SET state='Restoring' WHERE operation_id=$id;";
        cmd.ExecuteNonQuery();
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Move(recycled, original);
        cmd.CommandText = "UPDATE maintenance_operations SET state='Restored' WHERE operation_id=$id; UPDATE files SET replica_state='Available' WHERE dest_path=$source;";
        cmd.Parameters.AddWithValue("$source", source); cmd.ExecuteNonQuery();
    }

    internal static string Digest(string path, Action? progress = null, CancellationToken ct = default)
    {
        var before = new FileInfo(path); long size = before.Length; var modified = before.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[256 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.AppendData(buffer.AsSpan(0,read));
            progress?.Invoke();
        }
        string hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
        before.Refresh();
        if (before.Length != size || before.LastWriteTimeUtc != modified) throw new IOException("File changed during verification.");
        return hash;
    }

    private static void SetState(TransferJournal journal, string path, ReplicaState state,string? expectedHash=null)
    {
        using var cmd = journal.Connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET replica_state=$state WHERE dest_path=$path AND ($expected IS NULL OR sha256=$expected);";
        cmd.Parameters.AddWithValue("$expected",(object?)expectedHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", state.ToString()); cmd.Parameters.AddWithValue("$path", path); cmd.ExecuteNonQuery();
    }

    private static void Complete(TransferJournal journal, string id, string source)
    {
        using var transaction = journal.Connection.BeginTransaction();
        using var cmd = journal.Connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE maintenance_operations SET state='Recycled' WHERE operation_id=$id; UPDATE files SET replica_state='Recycled' WHERE dest_path=$source;";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$source", source); cmd.ExecuteNonQuery(); transaction.Commit();
    }

    private static void Recover(TransferJournal journal, string root, List<string> errors)
    {
        foreach(var restoring in ReadOperations(journal,"Restoring"))
        {
            string original=ArchivePathResolver.Resolve(root,restoring.Source);
            string recycled=ArchivePathResolver.Resolve(root,restoring.Target);
            if(File.Exists(original) && Digest(original)==restoring.Hash)
            {
                using var completed=journal.Connection.CreateCommand();
                completed.CommandText="UPDATE maintenance_operations SET state='Restored' WHERE operation_id=$id; UPDATE files SET replica_state='Available' WHERE dest_path=$source;";
                completed.Parameters.AddWithValue("$id",restoring.Id);completed.Parameters.AddWithValue("$source",restoring.Source);completed.ExecuteNonQuery();
            }
            else if(File.Exists(recycled) && Digest(recycled)==restoring.Hash) Complete(journal,restoring.Id,restoring.Source);
            else errors.Add("Unresolved restore operation: "+restoring.Id);
        }
        var operations = ReadOperations(journal, "Prepared");
        foreach (var op in operations)
        {
            try
            {
                string target = ArchivePathResolver.Resolve(root, op.Target);
                if (File.Exists(target) && Digest(target) == op.Hash) Complete(journal, op.Id, op.Source);
                else if (File.Exists(ArchivePathResolver.Resolve(root, op.Source)))
                {
                    using var cmd = journal.Connection.CreateCommand(); cmd.CommandText = "UPDATE maintenance_operations SET state='Cancelled' WHERE operation_id=$id;";
                    cmd.Parameters.AddWithValue("$id", op.Id); cmd.ExecuteNonQuery();
                }
                else errors.Add("Unresolved recovery operation: " + op.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
        }
    }

    private sealed record Operation(string Id, string Source, string Target, string KeeperRoot, string Keeper, string Hash, DateTimeOffset At);
    private static List<Operation> ReadOperations(TransferJournal journal, string state)
    {
        using var cmd = journal.Connection.CreateCommand(); cmd.CommandText = "SELECT operation_id,source_path,target_path,keeper_root,keeper_path,sha256,created_at FROM maintenance_operations WHERE state=$state;";
        cmd.Parameters.AddWithValue("$state", state); using var reader = cmd.ExecuteReader(); var result = new List<Operation>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        return result;
    }

    private static int Purge(TransferJournal journal, string root, LibraryCatalog catalog,CancellationToken ct, List<string> errors)
    {
        int count = 0;
        foreach (var op in ReadOperations(journal, "Recycled").Where(o => o.At.AddDays(30) <= DateTimeOffset.UtcNow))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var known=catalog.ReadMedia().Where(m=>m.State==ReplicaState.Available && m.Sha256==op.Hash).ToList();
                var keeperMedia=known.FirstOrDefault(m=>m.Root==op.KeeperRoot && m.RelativePath==op.Keeper) ?? known.FirstOrDefault();
                if(keeperMedia is null) continue;
                string keeper = ArchivePathResolver.Resolve(keeperMedia.Root, keeperMedia.RelativePath);
                if (!File.Exists(keeper)) continue;
                using var keeperStream=new FileStream(keeper,FileMode.Open,FileAccess.Read,FileShare.Read);
                if (Convert.ToHexString(SHA256.HashData(keeperStream)).ToLowerInvariant() != op.Hash) continue;
                string target = ArchivePathResolver.Resolve(root, op.Target);
                if (File.Exists(target) && Digest(target) != op.Hash) continue;
                File.Delete(target);
                using var cmd = journal.Connection.CreateCommand(); cmd.CommandText = "UPDATE maintenance_operations SET state='Purged' WHERE operation_id=$id;";
                cmd.Parameters.AddWithValue("$id", op.Id); cmd.ExecuteNonQuery(); count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
        }
        return count;
    }

    private static bool IsFixed(string root) => new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Fixed;
    private static bool IsAuxiliary(string path) => path.Split(['/', '\\']).Any(p => p.Equals(".picharbor", StringComparison.OrdinalIgnoreCase) || p.Equals(".AppleSync", StringComparison.OrdinalIgnoreCase) || p.Equals("iPod Photo Cache", StringComparison.OrdinalIgnoreCase));
    private static string? FindMoved(string root, LibraryMedia media, CancellationToken ct)
    {
        if (media.Sha256 is null) return null;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
                if (!IsAuxiliary(Path.GetRelativePath(root, child)) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, file);
                if (IsAuxiliary(relative)) continue;
                _ = ArchivePathResolver.Resolve(root, relative);
                if (new FileInfo(file).Length == media.Size && Digest(file,ct:ct) == media.Sha256) return relative;
            }
        }
        return null;
    }

    private static void MoveReferences(TransferJournal journal, string oldPath, string newPath)
    {
        journal.UpdatePathReferences(oldPath,newPath);
    }
}
