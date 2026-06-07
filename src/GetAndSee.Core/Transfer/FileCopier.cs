using System.Buffers;
using System.Security.Cryptography;
using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Transfer;

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
///   <item>read EXIF from the local copy to choose the <c>YYYY/YYYY-MM</c> folder;</item>
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
/// </remarks>
public sealed class FileCopier
{
    private const int BufferSize = 1024 * 1024;
    private const string StagingFolderName = ".get-and-see-tmp";

    private readonly IPhoneClient client;
    private readonly TransferJournal journal;
    private readonly DateFolderOrganizer organizer;
    private readonly IMediaMetadataExtractor metadataExtractor;
    private readonly TimeProvider clock;
    private readonly TimeSpan readTimeout;
    private readonly Action<long>? onBytesStreamed;
    private readonly bool verifyHash;
    private readonly string destinationRoot;
    private readonly string stagingDirectory;
    private readonly HashSet<string> assignedDestPaths;

    /// <summary>Creates a copier targeting <paramref name="destinationRoot"/>.</summary>
    /// <param name="client">Connected read-only device client.</param>
    /// <param name="journal">Open transfer journal at the destination root.</param>
    /// <param name="organizer">Date-folder organizer.</param>
    /// <param name="metadataExtractor">EXIF/metadata extractor used on the local copy.</param>
    /// <param name="destinationRoot">Destination root directory.</param>
    /// <param name="clock">Time source; defaults to the system clock. Also drives the read-stall watchdog.</param>
    /// <param name="readTimeout">
    /// Per-read inactivity timeout for the stall watchdog (#11 / R2). <see langword="null"/> or
    /// non-positive disables the watchdog (the read can block indefinitely, as in Sprint 1).
    /// </param>
    /// <param name="onBytesStreamed">
    /// Optional cheap per-chunk callback invoked with the number of bytes just streamed, used to drive
    /// the live progress/speed readout. Must not block.
    /// </param>
    /// <param name="verifyHash">
    /// When <see langword="true"/>, compute each copied file's SHA-256 during the copy (single pass over
    /// the stream buffer) and record it in the manifest's <c>sha256</c> column (R12 / <c>--verify-hash</c>).
    /// Read-only and off by default; when <see langword="false"/> no hashing occurs and the copy is
    /// byte-for-byte identical to the default path.
    /// </param>
    public FileCopier(
        IPhoneClient client,
        TransferJournal journal,
        DateFolderOrganizer organizer,
        IMediaMetadataExtractor metadataExtractor,
        string destinationRoot,
        TimeProvider? clock = null,
        TimeSpan? readTimeout = null,
        Action<long>? onBytesStreamed = null,
        bool verifyHash = false)
    {
        this.client = client;
        this.journal = journal;
        this.organizer = organizer;
        this.metadataExtractor = metadataExtractor;
        // Normalize to the extended-length form once so every derived destination path (staging, final,
        // collision checks) can exceed MAX_PATH for free (R6).
        this.destinationRoot = LongPath.ToExtended(destinationRoot);
        this.clock = clock ?? TimeProvider.System;
        this.readTimeout = readTimeout ?? TimeSpan.Zero;
        this.onBytesStreamed = onBytesStreamed;
        this.verifyHash = verifyHash;
        stagingDirectory = Path.Combine(this.destinationRoot, StagingFolderName);
        assignedDestPaths = new HashSet<string>(journal.GetUsedDestPaths(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes any leftover staging <c>.partial</c> files from a previous interrupted run. Best-effort.
    /// </summary>
    public void CleanStaging()
    {
        try
        {
            if (System.IO.Directory.Exists(stagingDirectory))
            {
                foreach (string partial in System.IO.Directory.EnumerateFiles(stagingDirectory, "*.partial"))
                {
                    SafeDelete(partial);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort; orphaned staging files are harmless and never published.
        }
    }

    /// <summary>Copies a single file through the atomic pipeline.</summary>
    /// <param name="file">The source file to copy.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The <see cref="CopyResult"/> describing what happened.</returns>
    public async Task<CopyResult> CopyAsync(RemoteFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (journal.GetState(file.Path, file.Size) == FileState.Done)
        {
            return new CopyResult(CopyStatus.Skipped, file, null, 0, null);
        }

        journal.MarkInProgress(file.Path, file.Size, clock.GetUtcNow());
        System.IO.Directory.CreateDirectory(stagingDirectory);
        string stagingPath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".partial");

        try
        {
            (long written, string? sha256) = await StreamToStagingAsync(file, stagingPath, cancellationToken).ConfigureAwait(false);

            if (written != file.Size)
            {
                SafeDelete(stagingPath);
                string error = $"size mismatch — expected {file.Size} bytes, received {written}.";
                journal.MarkFailed(file.Path, file.Size, error, clock.GetUtcNow());
                return new CopyResult(CopyStatus.Failed, file, null, 0, error);
            }

            MediaMetadata metadata = metadataExtractor.Extract(stagingPath);
            string relativeDest = ResolveUniqueRelativePath(organizer.GetRelativeDestination(file, metadata));
            string finalPath = Path.Combine(destinationRoot, relativeDest);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(stagingPath, finalPath);

            journal.MarkDone(file.Path, file.Size, relativeDest, metadata, clock.GetUtcNow(), sha256);
            return new CopyResult(CopyStatus.Copied, file, relativeDest, written, null, sha256);
        }
        catch (OperationCanceledException)
        {
            // Resumable: leave the journal row in_progress so a re-run retries this file.
            SafeDelete(stagingPath);
            throw;
        }
        catch (DeviceStallException)
        {
            // The device stopped responding mid-file (#11 / R2). Leave the row in_progress
            // (resumable) and propagate so the run stops cleanly rather than thrashing on every
            // remaining file — each subsequent read would also stall.
            SafeDelete(stagingPath);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(stagingPath);
            journal.MarkFailed(file.Path, file.Size, ex.Message, clock.GetUtcNow());
            return new CopyResult(CopyStatus.Failed, file, null, 0, ex.Message);
        }
    }

    private async Task<(long Bytes, string? Sha256)> StreamToStagingAsync(RemoteFile file, string stagingPath, CancellationToken cancellationToken)
    {
        Stream rawSource = await client.OpenReadAsync(file.Path, cancellationToken).ConfigureAwait(false);

        // Wrap the device read in the stall watchdog (#11 / R2) when a timeout is configured. The
        // watchdog owns and disposes the raw stream.
        await using Stream source = readTimeout > TimeSpan.Zero
            ? new WatchdogReadStream(rawSource, readTimeout, clock)
            : rawSource;

        await using var partial = new FileStream(
            stagingPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);

        // Opt-in SHA-256 (R12 / --verify-hash): hash the bytes as they already stream past — a single
        // pass over the same buffer, with no extra device read or disk read, so it stays read-only.
        // When the flag is off no hasher is created and the hot path is unchanged from Sprint 2.
        using IncrementalHash? hasher = verifyHash
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;

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
        if (IsAvailable(relativePath))
        {
            assignedDestPaths.Add(relativePath);
            return relativePath;
        }

        string directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(relativePath);
        string extension = Path.GetExtension(relativePath);

        for (int suffix = 2; ; suffix++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{suffix}{extension}");
            if (IsAvailable(candidate))
            {
                assignedDestPaths.Add(candidate);
                return candidate;
            }
        }
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
}
