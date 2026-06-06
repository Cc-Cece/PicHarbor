using System.Text;
using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Transfer;
using GetAndSee.Tests.TestSupport;
using NSubstitute;
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
}
