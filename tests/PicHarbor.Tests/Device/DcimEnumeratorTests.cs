using NSubstitute;
using PicHarbor.Core.Device;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Device;

public sealed class DcimEnumeratorTests
{
    [Fact]
    public async Task Walks_directories_and_yields_only_files()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IPhoneClient>();
        client.ListDirectoryAsync("/DCIM", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["100APPLE"]));
        client.ListDirectoryAsync("/DCIM/100APPLE", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["IMG_1.HEIC", "IMG_2.MOV"]));
        client.GetFileInfoAsync("/DCIM/100APPLE", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteFileInfo(0, null, IsDirectory: true)));
        client.GetFileInfoAsync("/DCIM/100APPLE/IMG_1.HEIC", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteFileInfo(111, DateTimeOffset.UnixEpoch, IsDirectory: false)));
        client.GetFileInfoAsync("/DCIM/100APPLE/IMG_2.MOV", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteFileInfo(222, DateTimeOffset.UnixEpoch, IsDirectory: false)));

        var enumerator = new DcimEnumerator(client);
        var files = new List<RemoteFile>();
        await foreach (RemoteFile file in enumerator.EnumerateAsync(cancellationToken: token))
        {
            files.Add(file);
        }

        files.Count.ShouldBe(2);
        files.ShouldContain(f => f.Path == "/DCIM/100APPLE/IMG_1.HEIC" && f.Size == 111);
        files.ShouldContain(f => f.Path == "/DCIM/100APPLE/IMG_2.MOV" && f.Size == 222);
    }
}
