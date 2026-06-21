using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.FakeDevice;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Harness;

/// <summary>
/// Unit tests for <see cref="FakeAfcDevice"/> and its <see cref="FakeDeviceSpec"/> virtual tree: the fake is
/// read-faithful (listing, stat, and open agree with the spec), exposes only the read-only contract, and
/// injects the device-level disconnect faults deterministically.
/// </summary>
public sealed class FakeAfcDeviceTests
{
    private static readonly DateTimeOffset Aug2024 = new(2024, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connect_exposes_the_spec_device_identity()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create()
            .WithDevice("00008101-0001020304050607", "Sample iPhone", "iPhone13,3")
            .Build();

        await device.ConnectAsync(Token);

        device.Device.ShouldNotBeNull();
        device.Device!.Udid.ShouldBe("00008101-0001020304050607");
        device.Device.Name.ShouldBe("Sample iPhone");
        device.Device.ProductType.ShouldBe("iPhone13,3");
    }

    [Fact]
    public async Task Operations_before_connect_throw()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create().AddSmallPhoto("/DCIM/100APPLE/IMG_1.HEIC", Aug2024).Build();

        await Should.ThrowAsync<DeviceException>(async () => await device.ListDirectoryAsync("/DCIM/", Token));
    }

    [Fact]
    public async Task Lists_directories_and_files_faithfully()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_1.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/101APPLE/IMG_2.HEIC", Aug2024)
            .Build();
        await device.ConnectAsync(Token);

        IReadOnlyList<string> dcim = await device.ListDirectoryAsync("/DCIM/", Token);
        dcim.ShouldBe(["100APPLE", "101APPLE"]);

        IReadOnlyList<string> folder = await device.ListDirectoryAsync("/DCIM/100APPLE", Token);
        folder.ShouldBe(["IMG_1.HEIC"]);
    }

    [Fact]
    public async Task Stat_reports_size_capture_date_and_directory_flag()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create()
            .AddFile("/DCIM/100APPLE/IMG_1.HEIC", size: 4096, captureDate: Aug2024)
            .Build();
        await device.ConnectAsync(Token);

        RemoteFileInfo fileInfo = await device.GetFileInfoAsync("/DCIM/100APPLE/IMG_1.HEIC", Token);
        fileInfo.IsDirectory.ShouldBeFalse();
        fileInfo.Size.ShouldBe(4096);
        fileInfo.ModifiedAt.ShouldBe(Aug2024);

        RemoteFileInfo dirInfo = await device.GetFileInfoAsync("/DCIM/100APPLE", Token);
        dirInfo.IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task Open_read_streams_the_files_deterministic_content()
    {
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddFile("/DCIM/100APPLE/IMG_1.HEIC", size: 9000, captureDate: Aug2024);
        using FakeAfcDevice device = spec.Build();
        await device.ConnectAsync(Token);

        await using Stream stream = await device.OpenReadAsync("/DCIM/100APPLE/IMG_1.HEIC", Token);
        byte[] read = await ReadAllAsync(stream);

        read.ShouldBe(FakeContent.Materialize(9000, spec.Files[0].ContentSeed));
    }

    [Fact]
    public async Task The_real_enumerator_walks_the_whole_tree()
    {
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        using FakeAfcDevice device = spec.Build();
        await device.ConnectAsync(Token);
        DcimEnumerator enumerator = new(device);

        List<RemoteFile> found = [];
        await foreach (RemoteFile file in enumerator.EnumerateAsync(cancellationToken: Token))
        {
            found.Add(file);
        }

        found.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(spec.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_empty_library_lists_an_empty_dcim_without_throwing()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create().Build();
        await device.ConnectAsync(Token);

        IReadOnlyList<string> dcim = await device.ListDirectoryAsync("/DCIM/", Token);

        dcim.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_path_throws_a_device_error_not_a_connection_loss()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create().AddSmallPhoto("/DCIM/100APPLE/IMG_1.HEIC", Aug2024).Build();
        await device.ConnectAsync(Token);

        DeviceException error = await Should.ThrowAsync<DeviceException>(
            async () => await device.OpenReadAsync("/DCIM/100APPLE/MISSING.HEIC", Token));
        error.ShouldNotBeOfType<DeviceConnectionLostException>();
    }

    [Fact]
    public async Task Disconnect_after_files_serves_n_opens_then_fails_connection_fatal()
    {
        using FakeAfcDevice device = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_1.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/100APPLE/IMG_2.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/100APPLE/IMG_3.HEIC", Aug2024)
            .DisconnectAfterFiles(2)
            .Build();
        await device.ConnectAsync(Token);

        await using Stream first = await device.OpenReadAsync("/DCIM/100APPLE/IMG_1.HEIC", Token);
        await using Stream second = await device.OpenReadAsync("/DCIM/100APPLE/IMG_2.HEIC", Token);
        await Should.ThrowAsync<DeviceConnectionLostException>(
            async () => await device.OpenReadAsync("/DCIM/100APPLE/IMG_3.HEIC", Token));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream sink = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, Token)) > 0)
        {
            sink.Write(buffer, 0, read);
        }

        return sink.ToArray();
    }
}
