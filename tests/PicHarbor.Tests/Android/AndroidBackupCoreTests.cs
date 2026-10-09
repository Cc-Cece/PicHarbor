using PicHarbor.Core.Android;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Android;

public sealed class AndroidBackupCoreTests
{
    [Fact]
    public void FtpEntryParser_parses_unix_directory_and_file()
    {
        string dirLine = "drwxrwxr-x  2 1000 1000 4096 Oct 09 12:00 Camera";
        string fileLine = "-rw-rw-r--  1 1000 1000 2451024 Oct 09 12:00 PXL_20241009_120000.jpg";

        var dir = FtpEntryParser.ParseLine(dirLine);
        dir.ShouldNotBeNull();
        dir.Name.ShouldBe("Camera");
        dir.IsDirectory.ShouldBeTrue();

        var file = FtpEntryParser.ParseLine(fileLine);
        file.ShouldNotBeNull();
        file.Name.ShouldBe("PXL_20241009_120000.jpg");
        file.IsDirectory.ShouldBeFalse();
        file.Size.ShouldBe(2451024);
    }

    [Fact]
    public void FtpEntryParser_parses_unix_file_with_spaces()
    {
        string line = "-rw-r--r-- 1 ftp ftp 3145728 Oct 09 12:00 Trip Photo 2024.JPG";
        var entry = FtpEntryParser.ParseLine(line);

        entry.ShouldNotBeNull();
        entry.Name.ShouldBe("Trip Photo 2024.JPG");
        entry.Size.ShouldBe(3145728);
        entry.IsDirectory.ShouldBeFalse();
    }

    [Fact]
    public void FtpEntryParser_parses_dos_format()
    {
        string dirLine = "04-15-24  01:23PM       <DIR>          Screenshots";
        string fileLine = "04-15-24  01:23PM              1048576 Screenshot_2024.png";

        var dir = FtpEntryParser.ParseLine(dirLine);
        dir.ShouldNotBeNull();
        dir.Name.ShouldBe("Screenshots");
        dir.IsDirectory.ShouldBeTrue();

        var file = FtpEntryParser.ParseLine(fileLine);
        file.ShouldNotBeNull();
        file.Name.ShouldBe("Screenshot_2024.png");
        file.IsDirectory.ShouldBeFalse();
        file.Size.ShouldBe(1048576);
    }

    [Fact]
    public void FtpEntryParser_ignores_dots_and_empty()
    {
        FtpEntryParser.ParseLine("").ShouldBeNull();
        FtpEntryParser.ParseLine("   ").ShouldBeNull();
        FtpEntryParser.ParseLine("drwxr-xr-x 2 ftp ftp 4096 Oct 09 12:00 .").ShouldBeNull();
        FtpEntryParser.ParseLine("drwxr-xr-x 2 ftp ftp 4096 Oct 09 12:00 ..").ShouldBeNull();
    }

    [Fact]
    public void FilterOptions_correctly_classifies_media()
    {
        var options = new AndroidBackupFilterOptions();

        options.IsSupportedMediaFile("photo.jpg", out bool isVideo1).ShouldBeTrue();
        isVideo1.ShouldBeFalse();

        options.IsSupportedMediaFile("photo.heic", out bool isVideo2).ShouldBeTrue();
        isVideo2.ShouldBeFalse();

        options.IsSupportedMediaFile("video.mp4", out bool isVideo3).ShouldBeTrue();
        isVideo3.ShouldBeTrue();

        options.IsSupportedMediaFile("document.pdf", out _).ShouldBeFalse();
        options.IsSupportedMediaFile("archive.zip", out _).ShouldBeFalse();
    }

    [Fact]
    public void FilterOptions_respects_size_threshold()
    {
        var options = new AndroidBackupFilterOptions
        {
            MinFileSizeBytes = 100 * 1024,
            IgnoreSmallImages = true
        };

        options.MinFileSizeBytes.ShouldBe(102400);
        options.IgnoreSmallImages.ShouldBeTrue();
    }
}
