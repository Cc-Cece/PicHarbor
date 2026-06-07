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
    public async Task Read_stall_leaves_file_in_progress_and_propagates_so_the_run_stops()
    {
        // A device that stops sending bytes mid-file (#11 / R2).
        var stallingStream = new ControlledReadStream();
        var file = new RemoteFile("/DCIM/100APPLE/IMG_STALL.MOV", 10_000_000, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(stallingStream));
        journal.EnsurePending(file);

        var copier = new FileCopier(
            client, journal, organizer, extractor, destination.Path,
            readTimeout: TimeSpan.FromMilliseconds(200));

        // The watchdog turns the indefinite hang into a clean, propagating stall.
        await Should.ThrowAsync<DeviceStallException>(async () => await copier.CopyAsync(file, Token));

        // Resumable: the in-flight file is left non-done, and no partial leaks into the final tree.
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.Exists(Path.Combine(destination.Path, "2024")).ShouldBeFalse();
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
}
