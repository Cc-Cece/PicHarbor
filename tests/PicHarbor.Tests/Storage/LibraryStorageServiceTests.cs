using System.IO;
using PicHarbor.Core.Storage;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Storage;

public sealed class LibraryStorageServiceTests
{
    [Theory]
    [InlineData("d", "D:")]
    [InlineData("D:", "D:")]
    [InlineData("D:\\", "D:")]
    [InlineData("c", "C:")]
    [InlineData("  e:  ", "E:")]
    public void Normalizes_drive_letter_correctly(string input, string expected)
    {
        LibraryStorageService.NormalizeDriveLetter(input).ShouldBe(expected);
    }

    [Fact]
    public void Resolves_system_drive_to_user_pictures()
    {
        string userPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string? root = Path.GetPathRoot(userPictures);
        if (!string.IsNullOrEmpty(root))
        {
            string systemLetter = LibraryStorageService.NormalizeDriveLetter(root);
            string libraryPath = LibraryStorageService.GetLibraryRootForDrive(systemLetter);
            libraryPath.ShouldBe(Path.Combine(userPictures, "PicHarbor"));
        }
    }

    [Fact]
    public void Resolves_data_drive_to_pictures_picharbor()
    {
        string path = LibraryStorageService.GetLibraryRootForDrive("Z:");
        path.ShouldBe(@"Z:\Pictures\PicHarbor");
    }

    [Theory]
    [InlineData("iPhone 15 Pro", "iPhone 15 Pro")]
    [InlineData("Kanbara/iPhone:15", "Kanbara_iPhone_15")]
    [InlineData("Xiaomi 14 <Pro> *test*", "Xiaomi 14 _Pro_ _test_")]
    [InlineData("", "DefaultDevice")]
    [InlineData("   ", "DefaultDevice")]
    [InlineData("...", "DefaultDevice")]
    public void Sanitizes_device_folder_names(string raw, string expected)
    {
        LibraryStorageService.SanitizeDeviceFolderName(raw).ShouldBe(expected);
    }

    [Fact]
    public void Gets_device_folder_path()
    {
        string root = @"D:\Pictures\PicHarbor";
        string devicePath = LibraryStorageService.GetDeviceFolderPath(root, "iPhone 15 Pro");
        devicePath.ShouldBe(Path.Combine(root, "iPhone 15 Pro"));
    }

    [Fact]
    public void Discovers_volumes_without_throwing()
    {
        var volumes = LibraryStorageService.DiscoverVolumes();
        volumes.ShouldNotBeNull();
        if (volumes.Count > 0)
        {
            volumes[0].TotalBytes.ShouldBeGreaterThan(0);
            volumes[0].DisplayText.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
