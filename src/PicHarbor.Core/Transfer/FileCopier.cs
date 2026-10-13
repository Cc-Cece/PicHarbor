using System.Buffers;
using System.Security.Cryptography;
using PicHarbor.Core.Device;
using PicHarbor.Core.Errors;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.Transfer;

/// <summary>
/// Copies one device file at a time, atomically and verifiably (R1, R7, R10), updating the journal
/// at every state transition and disambiguating filename collisions without ever overwriting (R5).
/// </summary>
/// <remarks>
/// <para>Per-file pipeline:</para>
/// <list type="number">
///   <item>skip if the journal already has the file <c>done</c>;</item>
///   <item>mark <c>in_progress</c>;</item>
///   <item>stream the AFC read stream into a staging <c>.partial</c> under the destination, counting bytes;</item>
///   <item>flush to disk (fsync);</item>
///   <item>verify the byte count equals the AFC-reported size — mismatch ⇒ fail, never publish;</item>
///   <item>read EXIF from the local copy to choose its date folder under the archive's <see cref="OrganizeScheme"/>;</item>
///   <item>resolve any filename collision (<c>_2</c>, <c>_3</c>, …);</item>
///   <item>atomically <see cref="File.Move(string,string)"/> the staging file into its final path;</item>
///   <item>mark <c>done</c> with the recorded metadata.</item>
/// </list>
/// <para>
/// The final folder never contains a partial file: bytes land in a staging <c>.partial</c> and are
/// only ever moved (renamed) into place after they are complete and verified. On failure the staging
/// file is removed and the journal records the error.
/// </para>
/// <para>
/// Every destination path is normalized through <see cref="LongPath"/> so a deep destination plus a
/// long original filename can exceed the legacy 260-character limit (R6). When <c>verifyHash</c> is
/// set, each file's SHA-256 is computed in the same pass that streams it and recorded in the manifest
/// (R12); the device read path is unchanged and remains read-only.
/// </para>
/// <para>
/// Across a run the copier guards liveness with a single run-level <see cref="ForwardProgressWatchdog"/>
/// fed by the byte heartbeat: if the device stops delivering bytes (a parked read, a between-file
/// fast-fail burst, or an intra-file native spin — all "forward byte progress stopped") for the
/// configured timeout, the run stops cleanly and resumably with a
/// <see cref="DeviceConnectionLostException"/> (exit 3) rather than hanging or spinning through every
/// remaining file (#11 / #25 / #38 / #42). The copier is <see cref="IDisposable"/> so that watchdog's
/// timer is released at the end of the run.
/// </para>
/// </remarks>
public sealed class FileCopier : IDisposable
{
    private const int BufferSize = 1024 * 1024;
    private const string StagingFolderName = ".picharbor-tmp";

    /// <summary>
    /// Default number of consecutive per-file failures the forward-progress watchdog treats as a lost
    /// device connection (#38). Ten failures in a row with no progress between them is an unambiguous
    /// "device gone" signal — a real disconnect fast-fails every file — while staying well clear of the
    /// isolated bad or changed files a healthy multi-thousand-file run may legitimately hit.
    /// </summary>
    private const int DefaultConsecutiveFailureLimit = 10;

    private readonly string? deviceSubfolder;
    private readonly IReadOnlyList<TransferJournal>? fallbackJournals;
    private readonly IMediaSourceClient client;
    private readonly TransferJournal journal;
    private readonly DateFolderOrganizer organizer;
    private readonly OrganizeScheme organizeScheme;
    private readonly IMediaMetadataExtractor metadataExtractor;
    private readonly TimeProvider clock;
    private readonly TimeSpan readTimeout;
    private readonly Action<long>? onBytesStreamed;
    private readonly bool verifyHash;
    private readonly bool strictVerification;
    private readonly Func<RemoteFile, bool?>? deletedPhotoDecision;
    private readonly bool rebackupDeleted;
    private readonly IReadOnlyList<PicHarbor.Core.Storage.LibraryMedia> catalogMedia = [];
    private readonly IReadOnlyList<PicHarbor.Core.Storage.SourceIdentity> catalogSources = [];
    private readonly string destinationRoot;
    private readonly string stagingDirectory;
    private readonly HashSet<string> assignedDestPaths;
    private readonly ForwardProgressWatchdog? progressWatchdog;

    /// <summary>Creates a copier targeting <paramref name="destinationRoot"/>.</summary>
    public FileCopier(
        IMediaSourceClient client,
        TransferJournal journal,
        DateFolderOrganizer organizer,
        IMediaMetadataExtractor metadataExtractor,
        string destinationRoot,
        OrganizeScheme organizeScheme,
        TimeProvider? clock = null,
        TimeSpan? readTimeout = null,
        Action<long>? onBytesStreamed = null,
        bool verifyHash = false,
        int consecutiveFailureLimit = DefaultConsecutiveFailureLimit,
        Action? onDisconnect = null,
        string? deviceSubfolder = null,
        IReadOnlyList<TransferJournal>? fallbackJournals = null,
        bool strictVerification = false,
        bool rebackupDeleted = true,
        Func<RemoteFile, bool?>? deletedPhotoDecision = null)
    {
        this.client = client;
        this.journal = journal;
        journal.BindDevice(client.Device?.Udid);
        this.organizer = organizer;
        this.organizeScheme = organizeScheme;
        this.metadataExtractor = metadataExtractor;
        this.destinationRoot = LongPath.ToExtended(destinationRoot);
        this.clock = clock ?? TimeProvider.System;
        this.readTimeout = readTimeout ?? TimeSpan.Zero;
        this.onBytesStreamed = onBytesStreamed;
        this.verifyHash = verifyHash;
        this.strictVerification = strictVerification;
        this.rebackupDeleted = rebackupDeleted;
        this.deletedPhotoDecision = deletedPhotoDecision;
        this.deviceSubfolder = string.IsNullOrWhiteSpace(deviceSubfolder) ? null : deviceSubfolder.Trim();
        this.fallbackJournals = fallbackJournals;
        if(File.Exists(Path.Combine(destinationRoot,TransferJournal.MetadataFolderName,"library.json")))
        {
            var catalog=new PicHarbor.Core.Storage.LibraryCatalog(destinationRoot);
            if (catalog.PendingLayouts.ContainsKey(journal.GetLibraryIdentity().StorageId))
                throw new InvalidOperationException("Complete the pending archive self-check before continuing backup.");
            catalogMedia=catalog.ReadMedia();
            catalogSources=catalog.ReadSources();
        }
        stagingDirectory = Path.Combine(this.destinationRoot, TransferJournal.MetadataFolderName, "staging");
        assignedDestPaths = new HashSet<string>(journal.GetUsedDestPaths(), StringComparer.OrdinalIgnoreCase);
        progressWatchdog = this.readTimeout > TimeSpan.Zero
            ? new ForwardProgressWatchdog(this.readTimeout, this.clock, consecutiveFailureLimit, onTrip: onDisconnect)
            : null;
    }

    /// <summary>
    /// Removes any leftover staging <c>.partial</c> files from a previous interrupted run. Best-effort.
    /// </summary>
    public void CleanStaging()
    {
        using var lease = PicHarbor.Core.Storage.LibraryWriteCoordinator.AcquireLibrary(destinationRoot);
        CleanDirectory(stagingDirectory);
        CleanDirectory(Path.Combine(destinationRoot, StagingFolderName));

        static void CleanDirectory(string dir)
        {
            try
            {
                if (System.IO.Directory.Exists(dir))
                {
                    foreach (string partial in System.IO.Directory.EnumerateFiles(dir, "*.partial"))
                    {
                        SafeDelete(partial);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Copies a single file through the atomic pipeline.</summary>
    /// <param name="file">The source file to copy.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The <see cref="CopyResult"/> describing what happened.</returns>
    public async Task<CopyResult> CopyAsync(RemoteFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var lease = PicHarbor.Core.Storage.LibraryWriteCoordinator.AcquireLibrary(destinationRoot);
        journal.EnsurePending(file);

        var priorSource=catalogSources.FirstOrDefault(s=>s.DeviceId==(client.Device?.Udid ?? "") &&
            s.SourcePath==file.Path && s.Size==file.Size && s.ModifiedAt==file.ModifiedAt);
        bool knownDeletedSource=priorSource is not null && IsKnownDeletedSource(priorSource);
        string? recordedLocalPath=journal.GetDestRelativePath(file.Path,file.Size);
        bool hasLocalRecord=journal.GetState(file.Path,file.Size)==FileState.Done && recordedLocalPath is not null &&
            File.Exists(PicHarbor.Core.Storage.ArchivePathResolver.Resolve(destinationRoot,recordedLocalPath)) &&
            journal.GetReplicaVerification(file.Path,file.Size).State==PicHarbor.Core.Storage.ReplicaState.Available;
        if(priorSource is not null && !hasLocalRecord)
        {
            foreach(var media in catalogMedia.Where(m=>m.Sha256==priorSource.MediaId && m.State==PicHarbor.Core.Storage.ReplicaState.Available))
            {
                string existing=PicHarbor.Core.Storage.ArchivePathResolver.Resolve(media.Root,media.RelativePath);
                if(!File.Exists(existing) || new FileInfo(existing).Length!=file.Size ||
                    VerifyLocal(existing,cancellationToken)!=media.Sha256) continue;
                if(strictVerification && await HashSourceAsync(file,cancellationToken).ConfigureAwait(false)!=media.Sha256) continue;
                bool isLocal=PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(media.Root)==PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(destinationRoot);
                if(!isLocal)
                    journal.MarkExternalSourceVerified(file,media);
                else journal.MarkDone(file.Path,file.Size,media.RelativePath,metadataExtractor.Extract(existing),clock.GetUtcNow(),media.Sha256);
                progressWatchdog?.RecordProgress();
                return new CopyResult(CopyStatus.Skipped,file,null,0,"Verified registered library content.");
            }
        }

        // Forward-progress / connection-health gate: if the run-level watchdog has already tripped (a burst
        // of consecutive fast-failures, or no bytes for the timeout while a prior file was in flight), stop
        // the run cleanly here — before touching this file — rather than marching it into the same failure.
        if (progressWatchdog?.Tripped == true)
        {
            throw new DeviceConnectionLostException();
        }

        string? relPath = journal.GetDestRelativePath(file.Path, file.Size)
                          ?? organizer.GetRelativeDestination(file, null, organizeScheme, deviceSubfolder);
        string candidatePath = LongPath.ToExtended(PicHarbor.Core.Storage.ArchivePathResolver.Resolve(destinationRoot, relPath));
        var verification = journal.GetReplicaVerification(file.Path,file.Size);
        var linked = journal.GetLinkedReplica(file.Path,file.Size);
        if (linked is { } other && journal.GetSourceModifiedAt(file.Path,file.Size) == file.ModifiedAt)
        {
            bool registered=catalogMedia.Any(m=>m.Sha256==other.Hash &&
                string.Equals(LongPath.NormalizeIdentityPath(m.Root),LongPath.NormalizeIdentityPath(other.Root),StringComparison.OrdinalIgnoreCase));
            string linkedPath = PicHarbor.Core.Storage.ArchivePathResolver.Resolve(other.Root,other.Path);
            if (registered && File.Exists(linkedPath) && VerifyLocal(linkedPath,cancellationToken) == other.Hash &&
                (!strictVerification || await HashSourceAsync(file,cancellationToken).ConfigureAwait(false)==other.Hash))
            {
                progressWatchdog?.RecordProgress();
                return new CopyResult(CopyStatus.Skipped,file,null,0,"Verified surviving archive copy.");
            }
        }

        if (File.Exists(candidatePath))
        {
            var targetInfo = new FileInfo(candidatePath);
            bool isJournalDone = journal.GetState(file.Path, file.Size) == FileState.Done;

            var sourceId = new FileIdentity(file.Path, file.Size, file.ModifiedAt);
            var targetId = new FileIdentity(candidatePath, targetInfo.Length, targetInfo.LastWriteTimeUtc);

            var equiv = FileEquivalenceResolver.Resolve(sourceId, targetId, allowHashCheck: false);
            bool isEquivalent = isJournalDone && targetInfo.Length == file.Size &&
                journal.GetSourceModifiedAt(file.Path, file.Size) == file.ModifiedAt &&
                verification.State == PicHarbor.Core.Storage.ReplicaState.Available;
            if (isEquivalent && verification.Hash is not null)
                isEquivalent = VerifyLocal(candidatePath,cancellationToken) == verification.Hash;
            if (isEquivalent && strictVerification)
                isEquivalent = verification.Hash is not null && await HashSourceAsync(file,cancellationToken).ConfigureAwait(false) == verification.Hash;
            if (!isEquivalent && verification.Hash is not null)
            {
                string actualHash=VerifyLocal(candidatePath,cancellationToken);
                journal.RecordChangedVersion(relPath,actualHash,targetInfo.Length,targetInfo.LastWriteTimeUtc);
            }

            if (isEquivalent)
            {
                if (!isJournalDone)
                {
                    journal.MarkDone(file.Path, file.Size, relPath, MediaMetadata.Empty, clock.GetUtcNow(), null);
                }
                progressWatchdog?.RecordProgress();
                return new CopyResult(CopyStatus.Skipped, file, relPath, 0, null);
            }
        }

        if (!File.Exists(candidatePath) && (knownDeletedSource || linked is null && journal.GetState(file.Path,file.Size) is FileState.Done or FileState.Missing))
        {
            // File.Exists alone cannot distinguish deletion from an inaccessible member.
            _ = Directory.GetFileSystemEntries(destinationRoot);
            bool missing;
            try { _ = File.GetAttributes(candidatePath); missing = false; }
            catch (FileNotFoundException) { missing = true; }
            catch (DirectoryNotFoundException) { _ = Directory.GetFileSystemEntries(destinationRoot); missing = true; }
            if (missing)
            {
                bool? decision = deletedPhotoDecision is null ? rebackupDeleted : deletedPhotoDecision(file);
                if (decision is null) throw new OperationCanceledException(cancellationToken);
                if (!decision.Value) { journal.ExcludeSource(file.Path,file.Size,file.ModifiedAt); return new CopyResult(CopyStatus.Skipped,file,null,0,"Deletion retained by your backup preference."); }
            }
        }
        if (!rebackupDeleted && journal.IsSourceExcluded(file.Path,file.Size,file.ModifiedAt))
            return new CopyResult(CopyStatus.Skipped,file,null,0,"Deletion retained by your backup preference.");

        // Cross-volume / fallback journal check: if already backed up in another discovered archive, skip!
        if (journal.GetState(file.Path, file.Size) != FileState.Done && fallbackJournals is not null)
        {
            foreach (TransferJournal fallback in fallbackJournals)
            {
                fallback.BindDevice(client.Device?.Udid);
                string? fallbackPath = fallback.GetDestRelativePath(file.Path, file.Size);
                string fallbackRoot = Path.GetDirectoryName(Path.GetDirectoryName(fallback.DatabasePath))!;
                if (fallback.GetLibraryIdentity().LibraryId == journal.GetLibraryIdentity().LibraryId &&
                    fallback.GetState(file.Path, file.Size) == FileState.Done && fallbackPath is not null &&
                    File.Exists(PicHarbor.Core.Storage.ArchivePathResolver.Resolve(fallbackRoot, fallbackPath)) &&
                    new FileInfo(PicHarbor.Core.Storage.ArchivePathResolver.Resolve(fallbackRoot, fallbackPath)).Length == file.Size &&
                    fallback.GetSourceModifiedAt(file.Path, file.Size) == file.ModifiedAt)
                {
                    var saved = fallback.GetReplicaVerification(file.Path,file.Size);
                    if (saved.State != PicHarbor.Core.Storage.ReplicaState.Available || saved.Hash is null ||
                        VerifyLocal(PicHarbor.Core.Storage.ArchivePathResolver.Resolve(fallbackRoot,fallbackPath),cancellationToken) != saved.Hash) continue;
                    if (strictVerification && await HashSourceAsync(file,cancellationToken).ConfigureAwait(false) != saved.Hash) continue;
                    progressWatchdog?.RecordProgress();
                    return new CopyResult(CopyStatus.Skipped, file, null, 0, "Verified copy in another archive member.");
                }
            }
        }

        journal.RefreshSourceMetadata(file);
        journal.MarkInProgress(file.Path, file.Size, clock.GetUtcNow());
        System.IO.Directory.CreateDirectory(stagingDirectory);
        string stagingPath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".partial");

        // While streaming, observe both the caller's token and the watchdog's: a watchdog trip cancels the
        // in-flight read so the stuck read is abandoned instead of blocking forever.
        using CancellationTokenSource? linkedCts = progressWatchdog is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, progressWatchdog.Token);
        CancellationToken streamToken = linkedCts?.Token ?? cancellationToken;

        try
        {
            (long written, string? sha256) = await StreamToStagingAsync(file, stagingPath, streamToken).ConfigureAwait(false);

            if (written != file.Size)
            {
                SafeDelete(stagingPath);
                string error = $"size mismatch — expected {file.Size} bytes, received {written}.";
                journal.MarkFailed(file.Path, file.Size, error, clock.GetUtcNow());
                progressWatchdog?.RecordFailure();
                return new CopyResult(CopyStatus.Failed, file, null, 0, error);
            }

            MediaMetadata metadata = metadataExtractor.Extract(stagingPath);
            string relativeDest = ResolveUniqueRelativePath(organizer.GetRelativeDestination(file, metadata, organizeScheme, deviceSubfolder));
            string finalPath = PicHarbor.Core.Storage.ArchivePathResolver.Resolve(destinationRoot, relativeDest);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(stagingPath, finalPath);

            journal.MarkDone(file.Path, file.Size, relativeDest, metadata, clock.GetUtcNow(), sha256);
            progressWatchdog?.RecordProgress();
            return new CopyResult(CopyStatus.Copied, file, relativeDest, written, null, sha256);
        }
        catch (OperationCanceledException) when (progressWatchdog?.Tripped == true && !cancellationToken.IsCancellationRequested)
        {
            // The forward-progress watchdog tripped: the device stopped delivering bytes (a park, a
            // between-file fast-fail burst, or an intra-file native spin — all "bytes stopped flowing").
            // The in-flight read was abandoned by the cancellation; leave the row in_progress (resumable)
            // and surface a connection loss so the run stops cleanly (exit 3) rather than spinning.
            SafeDelete(stagingPath);
            throw new DeviceConnectionLostException();
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation (Ctrl+C): resumable, leave the row in_progress so a re-run retries it.
            SafeDelete(stagingPath);
            throw;
        }
        catch (Exception ex) when (ex is DeviceStallException or DeviceConnectionLostException)
        {
            // The device connection dropped mid-file (#11 / #25 / R2): a blocking native open/stat/list
            // call parked and the DeviceWatchdog abandoned it, or a native read/open returned a
            // connection-fatal AFC error. Leave the row in_progress (resumable) and propagate so the run
            // stops cleanly rather than marching every remaining file into the same hang.
            SafeDelete(stagingPath);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(stagingPath);
            journal.MarkFailed(file.Path, file.Size, ex.Message, clock.GetUtcNow());
            progressWatchdog?.RecordFailure();
            return new CopyResult(CopyStatus.Failed, file, null, 0, ex.Message);
        }
    }

    private async Task<string> HashSourceAsync(RemoteFile file, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, progressWatchdog?.Token ?? CancellationToken.None);
        var raw = await client.OpenReadAsync(file.Path,linked.Token).ConfigureAwait(false);
        await using Stream stream = progressWatchdog is not null ? new AbandonableReadStream(raw, clock) : raw;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0,BufferSize),linked.Token).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0,read));
                progressWatchdog?.RecordProgress();
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private string VerifyLocal(string path, CancellationToken ct)
        => PicHarbor.Core.Storage.LibraryMaintenanceService.Digest(path,()=>progressWatchdog?.RecordProgress(),ct);

    private bool IsKnownDeletedSource(PicHarbor.Core.Storage.SourceIdentity source)
    {
        var copies=catalogMedia.Where(m=>m.Sha256==source.MediaId).ToList();
        if(copies.Count==0) return false;
        foreach(var copy in copies)
        {
            if(copy.State is PicHarbor.Core.Storage.ReplicaState.Offline or PicHarbor.Core.Storage.ReplicaState.Inaccessible or PicHarbor.Core.Storage.ReplicaState.Changed) return false;
            try
            {
                _ = Directory.GetFileSystemEntries(copy.Root);
                _ = File.GetAttributes(PicHarbor.Core.Storage.ArchivePathResolver.Resolve(copy.Root,copy.RelativePath));
                return false;
            }
            catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { if(!Directory.Exists(copy.Root)) return false; }
            catch(IOException) { return false; }
            catch(UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    private async Task<(long Bytes, string? Sha256)> StreamToStagingAsync(RemoteFile file, string stagingPath, CancellationToken cancellationToken)
    {
        if (ReadDiagnostics.Enabled)
        {
            ReadDiagnostics.Log($"begin file path={file.Path} expectedSize={file.Size}");
        }

        Stream rawSource = await client.OpenReadAsync(file.Path, cancellationToken).ConfigureAwait(false);

        // Shield the device read so the run-level forward-progress watchdog can abandon it on a stall or
        // native spin (#11 / #25 / #42). The shield owns and disposes the raw stream. With no watchdog
        // (readTimeout <= 0) the raw stream is used directly, preserving the Sprint-1 unguarded behavior.
        await using Stream source = progressWatchdog is not null
            ? new AbandonableReadStream(rawSource, clock)
            : rawSource;

        await using var partial = new FileStream(
            stagingPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);

        // Record content identity while streaming, without a second device read.
        using IncrementalHash? hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long total = await CopyStreamAsync(source, partial, hasher, cancellationToken).ConfigureAwait(false);

        await partial.FlushAsync(cancellationToken).ConfigureAwait(false);
        partial.Flush(flushToDisk: true); // fsync: bytes are durable before the rename (R1).

        string? sha256 = hasher is not null
            ? Convert.ToHexStringLower(hasher.GetHashAndReset())
            : null;
        return (total, sha256);
    }

    private async Task<long> CopyStreamAsync(Stream source, Stream destination, IncrementalHash? hasher, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher?.AppendData(buffer.AsSpan(0, read));
                total += read;
                onBytesStreamed?.Invoke(read);
                progressWatchdog?.RecordProgress();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private string ResolveUniqueRelativePath(string relativePath)
    {
        string resolved = CollisionSuffix.Resolve(relativePath, IsAvailable);
        assignedDestPaths.Add(resolved);
        return resolved;
    }

    private bool IsAvailable(string relativePath) =>
        !assignedDestPaths.Contains(relativePath) &&
        !File.Exists(Path.Combine(destinationRoot, relativePath));

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a staging .partial behind is safe; it is never published and is cleaned next run.
        }
    }

    /// <summary>Stops the forward-progress watchdog timer and releases its resources.</summary>
    public void Dispose() => progressWatchdog?.Dispose();
}
