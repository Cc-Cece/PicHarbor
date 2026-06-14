using System.Security.Cryptography;
using System.Text;
using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.Util;
using GetAndSee.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Transfer;

public sealed class FileCopierTests : IDisposable
{
    private readonly TempDirectory destination = new();
    private readonly TransferJournal journal;
    private readonly DateFolderOrganizer organizer = new();
    private readonly IMediaMetadataExtractor extractor = Substitute.For<IMediaMetadataExtractor>();
    private readonly IPhoneClient client = Substitute.For<IPhoneClient>();

    public FileCopierTests()
    {
        journal = TransferJournal.Open(destination.Path);
        extractor.Extract(Arg.Any<string>())
            .Returns(new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone"));
    }

    public void Dispose()
    {
        journal.Dispose();
        destination.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void SetupRead(string path, byte[] content) =>
        client.OpenReadAsync(path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content, writable: false)));

    private FileCopier CreateCopier() =>
        new(client, journal, organizer, extractor, destination.Path);

    private string? ReadManifestSha256(string relativeDest)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(destination.Path, TransferJournal.DatabaseFileName),
            Mode = SqliteOpenMode.ReadOnly,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM manifest WHERE dest_path = $dest;";
        command.Parameters.AddWithValue("$dest", relativeDest);
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public async Task Copies_file_into_date_folder_and_marks_done()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello world media bytes");
        var file = new RemoteFile("/DCIM/100APPLE/IMG_1.HEIC", content.Length, null);
        SetupRead(file.Path, content);
        journal.EnsurePending(file);

        CopyResult result = await CreateCopier().CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        string expected = Path.Combine(destination.Path, "2024", "2024-08", "IMG_1.HEIC");
        File.Exists(expected).ShouldBeTrue();
        File.ReadAllBytes(expected).ShouldBe(content);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Fails_and_never_publishes_when_size_mismatches()
    {
        byte[] content = Encoding.UTF8.GetBytes("only thirteen");
        var file = new RemoteFile("/DCIM/100APPLE/IMG_2.HEIC", content.Length + 100, null); // claims larger than reality
        SetupRead(file.Path, content);
        journal.EnsurePending(file);

        CopyResult result = await CreateCopier().CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Failed);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Failed);
        Directory.Exists(Path.Combine(destination.Path, "2024")).ShouldBeFalse();
    }

    [Fact]
    public async Task Skips_files_already_done()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_3.HEIC", 10, null);
        journal.EnsurePending(file);
        journal.MarkDone(
            file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_3.HEIC"),
            MediaMetadata.Empty, DateTimeOffset.UtcNow);

        CopyResult result = await CreateCopier().CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Skipped);
    }

    [Fact]
    public async Task Disambiguates_collisions_without_overwriting()
    {
        byte[] a = Encoding.UTF8.GetBytes("file A contents");
        byte[] b = Encoding.UTF8.GetBytes("file B different contents");
        var fileA = new RemoteFile("/DCIM/100APPLE/IMG_9.HEIC", a.Length, null);
        var fileB = new RemoteFile("/DCIM/101APPLE/IMG_9.HEIC", b.Length, null);
        SetupRead(fileA.Path, a);
        SetupRead(fileB.Path, b);
        journal.EnsurePending(fileA);
        journal.EnsurePending(fileB);
        FileCopier copier = CreateCopier();

        CopyResult resultA = await copier.CopyAsync(fileA, Token);
        CopyResult resultB = await copier.CopyAsync(fileB, Token);

        resultA.RelativeDestPath.ShouldBe(Path.Combine("2024", "2024-08", "IMG_9.HEIC"));
        resultB.RelativeDestPath.ShouldBe(Path.Combine("2024", "2024-08", "IMG_9_2.HEIC"));
        File.ReadAllBytes(Path.Combine(destination.Path, resultA.RelativeDestPath!)).ShouldBe(a);
        File.ReadAllBytes(Path.Combine(destination.Path, resultB.RelativeDestPath!)).ShouldBe(b);
    }

    [Fact]
    public async Task Read_stall_mid_file_stops_the_run_resumably_via_the_forward_progress_watchdog()
    {
        // A device that stops sending bytes mid-file (#11 / #42): the run-level forward-progress watchdog
        // trips after the timeout and surfaces a clean, resumable connection loss instead of hanging.
        FakeTimeProvider clock = new();
        var stallingStream = new ControlledReadStream();
        var file = new RemoteFile("/DCIM/100APPLE/IMG_STALL.MOV", 10_000_000, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(stallingStream));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            clock: clock, readTimeout: TimeSpan.FromSeconds(30));

        Task<CopyResult> copy = copier.CopyAsync(file, Token);
        await stallingStream.Started;            // the read is in flight
        clock.Advance(TimeSpan.FromSeconds(30)); // no bytes for the timeout → the watchdog trips

        await Should.ThrowAsync<DeviceConnectionLostException>(async () => await copy);

        // Resumable: the in-flight file is left non-done, and no partial leaks into the final tree.
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.Exists(Path.Combine(destination.Path, "2024")).ShouldBeFalse();
    }

    [Fact]
    public async Task Intra_file_freeze_after_bytes_flowed_stops_the_run_resumably_without_spinning()
    {
        // #42 (the intra-file spin): a cable yank mid-stream of a large file. Bytes flow (the .partial
        // grows), then forward progress freezes — the native read pins a core and never returns. This is
        // NOT a park from the first byte: the watchdog must reset on the bytes that DID flow and then trip
        // a full timeout after they stop, turning the intra-file spin into a clean, resumable exit-3 stop.
        FakeTimeProvider clock = new();
        var stream = new ChunkThenStallReadStream(chunksBeforeStall: 3, chunkSize: 1024 * 1024);
        var file = new RemoteFile("/DCIM/126APPLE/IMG_6834.MOV", 392_323_980L, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(stream));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            clock: clock, readTimeout: TimeSpan.FromSeconds(15));

        Task<CopyResult> copy = copier.CopyAsync(file, Token);
        await stream.StalledReadStarted;          // 3 MB streamed, then forward progress froze
        clock.Advance(TimeSpan.FromSeconds(15));   // a full timeout AFTER the freeze → the watchdog trips

        await Should.ThrowAsync<DeviceConnectionLostException>(async () => await copy);

        // Resumable, nothing published, and the loop actually terminated (no spin).
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);
        Directory.GetFiles(destination.Path, "*.MOV", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();

        stream.ReleaseStall(); // let the orphaned read unwind during teardown
    }

    [Fact]
    public async Task A_zero_byte_file_copies_cleanly_and_never_trips_the_watchdog()
    {
        // False-positive guard: a legitimately empty file produces no bytes but completes instantly. It
        // must copy as normal, not be mistaken for a stalled device.
        FakeTimeProvider clock = new();
        var file = new RemoteFile("/DCIM/100APPLE/EMPTY.DAT", 0, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false)));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            clock: clock, readTimeout: TimeSpan.FromSeconds(15));

        CopyResult result = await copier.CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
    }

    [Fact]
    public async Task Connection_lost_during_open_leaves_file_in_progress_and_stops_the_run()
    {
        // A connection-fatal AFC error (a yanked cable surfacing as MuxError/ServiceNotConnected) must
        // stop the whole run resumably — exactly like a watchdog stall — rather than failing this one
        // file and marching the next file into its own open-hang (#25 #3).
        var file = new RemoteFile("/DCIM/100APPLE/IMG_LOST.MOV", 5_000_000, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Throws(new DeviceConnectionLostException());
        journal.EnsurePending(file);

        await Should.ThrowAsync<DeviceConnectionLostException>(
            async () => await CreateCopier().CopyAsync(file, Token));

        // Resumable: the in-flight file is left non-done, and no partial leaks into the final tree.
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.Exists(Path.Combine(destination.Path, "2024")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_single_unreadable_file_is_marked_failed_and_does_not_stop_the_run()
    {
        // The conservative boundary for #3: a per-file device error (one corrupt/locked file) must
        // still MarkFailed and let the run continue — only a connection-fatal error stops everything.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_BAD.HEIC", 1234, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Throws(new DeviceException("Could not open \"/DCIM/100APPLE/IMG_BAD.HEIC\" for reading: ObjectNotFound."));
        journal.EnsurePending(file);

        CopyResult result = await CreateCopier().CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Failed);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Failed);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Copies_into_a_destination_path_longer_than_260_chars()
    {
        // R6: a deep destination combined with a long original filename exceeds the legacy MAX_PATH
        // (260). Without the \\?\ long-path prefix this throws PathTooLongException and the copy fails.
        string longStem = new('L', 200);
        byte[] content = Encoding.UTF8.GetBytes("long path media bytes");
        var file = new RemoteFile($"/DCIM/100APPLE/{longStem}.HEIC", content.Length, null);
        SetupRead(file.Path, content);
        journal.EnsurePending(file);

        string relative = Path.Combine("2024", "2024-08", $"{longStem}.HEIC");
        string fullPath = Path.Combine(destination.Path, relative);
        fullPath.Length.ShouldBeGreaterThan(260, "the test must exercise a path beyond MAX_PATH");

        CopyResult result = await CreateCopier().CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        result.RelativeDestPath.ShouldBe(relative);
        // Read back through the extended-length prefix — the only reliable way to touch a > 260 path.
        string extended = LongPath.ToExtended(fullPath);
        File.Exists(extended).ShouldBeTrue();
        File.ReadAllBytes(extended).ShouldBe(content);

        // Remove the > 260 file via the extended path so TempDirectory (non-extended) cleanup succeeds.
        File.Delete(extended);
    }

    [Fact]
    public async Task Verify_hash_records_the_sha256_into_the_manifest()
    {
        byte[] content = Encoding.UTF8.GetBytes("verify me byte for byte");
        var file = new RemoteFile("/DCIM/100APPLE/IMG_HASH.HEIC", content.Length, null);
        SetupRead(file.Path, content);
        journal.EnsurePending(file);
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(content));

        var copier = new FileCopier(
            client, journal, organizer, extractor, destination.Path, verifyHash: true);
        CopyResult result = await copier.CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        result.Sha256.ShouldBe(expectedSha);
        ReadManifestSha256(result.RelativeDestPath!).ShouldBe(expectedSha);
    }

    [Fact]
    public async Task Verify_hash_reflects_a_single_changed_byte()
    {
        // During-copy hashing records the digest of the bytes actually received. A changed/corrupted
        // byte yields a different digest from the known-good one — how a manifest SHA-256 surfaces it.
        byte[] good = Encoding.UTF8.GetBytes("the original untampered content!");
        byte[] tampered = (byte[])good.Clone();
        tampered[5] ^= 0xFF;
        string goodSha = Convert.ToHexStringLower(SHA256.HashData(good));
        var file = new RemoteFile("/DCIM/100APPLE/IMG_TAMPER.HEIC", tampered.Length, null);
        SetupRead(file.Path, tampered);
        journal.EnsurePending(file);

        var copier = new FileCopier(
            client, journal, organizer, extractor, destination.Path, verifyHash: true);
        CopyResult result = await copier.CopyAsync(file, Token);

        result.Sha256.ShouldNotBe(goodSha);
        result.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(tampered)));
    }

    [Fact]
    public async Task Default_copy_records_no_hash()
    {
        // Zero cost when the flag is absent: no hash is computed or stored, matching Sprint 2 behavior.
        byte[] content = Encoding.UTF8.GetBytes("no hash on the default path");
        var file = new RemoteFile("/DCIM/100APPLE/IMG_NOHASH.HEIC", content.Length, null);
        SetupRead(file.Path, content);
        journal.EnsurePending(file);

        CopyResult result = await CreateCopier().CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        result.Sha256.ShouldBeNull();
        ReadManifestSha256(result.RelativeDestPath!).ShouldBeNull();
    }

    [Fact]
    public async Task Copies_a_multi_chunk_file_verifying_size_and_hash()
    {
        // Larger than the 1 MiB copy buffer so the streaming loop runs many iterations — the same code
        // path a multi-GB ProRes/4K file exercises (the full multi-GB run is QA #21 on hardware).
        var content = new byte[(5 * 1024 * 1024) + 12_345];
        new Random(1234).NextBytes(content);
        var file = new RemoteFile("/DCIM/100APPLE/IMG_BIG.MOV", content.Length, null);
        SetupRead(file.Path, content);
        journal.EnsurePending(file);
        string expectedSha = Convert.ToHexStringLower(SHA256.HashData(content));

        var copier = new FileCopier(
            client, journal, organizer, extractor, destination.Path, verifyHash: true);
        CopyResult result = await copier.CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        result.BytesCopied.ShouldBe(content.Length);
        result.Sha256.ShouldBe(expectedSha);
        File.ReadAllBytes(Path.Combine(destination.Path, result.RelativeDestPath!)).ShouldBe(content);
    }

    [Fact]
    public async Task A_device_that_fast_returns_zero_bytes_for_every_file_stops_the_run_instead_of_spinning()
    {
        // #38 (the busy-spin): on a real cable-yank the native read returns FAST and WRONG — afc_file_read
        // reports success with 0 bytes — instead of parking, so the per-call inactivity watchdog never
        // trips. Each file fails its size check fast and the copy loop spins to the next at 100% CPU. This
        // reproduces that SPIN (a 0-byte stream returned for EVERY file in a tight loop, NOT a stall) and
        // asserts the forward-progress breaker turns it into a clean, resumable stop.
        const int limit = 3;
        List<RemoteFile> files = ManyFiles(50);
        client.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false)));
        foreach (RemoteFile file in files)
        {
            journal.EnsurePending(file);
        }

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            readTimeout: TimeSpan.FromSeconds(30), consecutiveFailureLimit: limit);

        int processed = await CountProcessedUntilStopAsync(copier, files);

        // The run STOPPED after `limit` consecutive fast failures rather than churning all 50 files —
        // proof there is no spin. Nothing was published and no partial leaked (resumable).
        processed.ShouldBe(limit);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.Exists(Path.Combine(destination.Path, "2024")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_device_that_fast_throws_a_per_file_error_for_every_file_stops_the_run_instead_of_spinning()
    {
        // The other #38 manifestation: the yanked transport returns a non-connection-fatal AfcError fast
        // (one not in AfcErrors.IsConnectionFatal's allow-list), so every file throws a per-file
        // DeviceException and the loop spins. The breaker still sees the no-forward-progress burst and
        // stops the run — proving the fix is manifestation-agnostic, not tied to a specific error code.
        const int limit = 4;
        List<RemoteFile> files = ManyFiles(50);
        client.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new DeviceException("Error reading from the device: a non-fatal AFC error."));
        foreach (RemoteFile file in files)
        {
            journal.EnsurePending(file);
        }

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            readTimeout: TimeSpan.FromSeconds(30), consecutiveFailureLimit: limit);

        int processed = await CountProcessedUntilStopAsync(copier, files);

        processed.ShouldBe(limit);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Isolated_failures_between_successes_never_trip_the_breaker()
    {
        // A genuinely bad or changed file amid a healthy run must NOT stop the run: a success resets the
        // streak, so only a CONSECUTIVE burst (a disconnect) trips the breaker. Here failures alternate
        // with successes — more than `limit` failures in total, but never two in a row — and the whole
        // run completes without a DeviceConnectionLostException.
        const int limit = 3;
        byte[] good = Encoding.UTF8.GetBytes("a healthy readable file");
        List<RemoteFile> files = new();
        for (int i = 0; i < 8; i++)
        {
            RemoteFile file = new($"/DCIM/100APPLE/IMG_{i}.HEIC", good.Length, null);
            if (i % 2 == 0)
            {
                SetupRead(file.Path, good);
            }
            else
            {
                client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
                    .Throws(new DeviceException("one isolated bad file"));
            }

            journal.EnsurePending(file);
            files.Add(file);
        }

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            readTimeout: TimeSpan.FromSeconds(30), consecutiveFailureLimit: limit);

        int copied = 0, failed = 0;
        foreach (RemoteFile file in files)
        {
            // Reaching the end of this loop without an exception is itself the assertion that the breaker
            // never tripped despite four total failures.
            CopyResult result = await copier.CopyAsync(file, Token);
            if (result.Status == CopyStatus.Copied)
            {
                copied++;
            }
            else if (result.Status == CopyStatus.Failed)
            {
                failed++;
            }
        }

        copied.ShouldBe(4);
        failed.ShouldBe(4);
    }

    private static List<RemoteFile> ManyFiles(int count)
    {
        List<RemoteFile> files = new(count);
        for (int i = 0; i < count; i++)
        {
            files.Add(new RemoteFile($"/DCIM/100APPLE/IMG_{i}.HEIC", 1000, null));
        }

        return files;
    }

    private static async Task<int> CountProcessedUntilStopAsync(FileCopier copier, IReadOnlyList<RemoteFile> files)
    {
        // Drive the copy loop exactly as CopyCommand does — one CopyAsync per file — until the breaker
        // throws DeviceConnectionLostException (the clean stop), returning how many files were processed
        // first. Without the breaker this would walk the entire list: the 100% CPU spin the test exists
        // to prevent.
        int processed = 0;
        await Should.ThrowAsync<DeviceConnectionLostException>(async () =>
        {
            foreach (RemoteFile file in files)
            {
                await copier.CopyAsync(file, Token);
                processed++;
            }
        });

        return processed;
    }
}
