using GetAndSee.Core.Device;
using GetAndSee.Core.Organize;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Organize;

public sealed class DateFolderOrganizerTests
{
    private readonly DateFolderOrganizer organizer = new();

    [Fact]
    public void Uses_exif_date_when_present()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 1000, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var metadata = new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone 12 Pro");

        string result = organizer.GetRelativeDestination(file, metadata);

        result.ShouldBe(Path.Combine("2024", "2024-08", "IMG_0001.HEIC"));
    }

    [Fact]
    public void Falls_back_to_mtime_when_no_exif()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0002.MOV", 1000, new DateTimeOffset(2023, 2, 9, 12, 0, 0, TimeSpan.Zero));

        string result = organizer.GetRelativeDestination(file, MediaMetadata.Empty);

        result.ShouldBe(Path.Combine("2023", "2023-02", "IMG_0002.MOV"));
    }

    [Fact]
    public void Routes_to_unsorted_when_no_date_at_all()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0003.PNG", 1000, null);

        string result = organizer.GetRelativeDestination(file, MediaMetadata.Empty);

        result.ShouldBe(Path.Combine("unsorted", "IMG_0003.PNG"));
    }

    [Fact]
    public void Rejects_future_exif_date_and_falls_back_to_mtime()
    {
        // R16: a capture date far in the future is nonsense; fall back to the file's mtime.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0004.HEIC", 1000, new DateTimeOffset(2022, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var metadata = new MediaMetadata(new DateTime(2999, 1, 1), null, null, null, null);

        string result = organizer.GetRelativeDestination(file, metadata);

        result.ShouldBe(Path.Combine("2022", "2022-05", "IMG_0004.HEIC"));
    }

    [Fact]
    public void Rejects_prehistoric_date_and_routes_to_unsorted()
    {
        // R16: before 1990 is out of range; with no valid fallback the file is unsorted.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0005.HEIC", 1000, new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var metadata = new MediaMetadata(new DateTime(1925, 1, 1), null, null, null, null);

        string result = organizer.GetRelativeDestination(file, metadata);

        result.ShouldBe(Path.Combine("unsorted", "IMG_0005.HEIC"));
    }

    [Theory]
    [InlineData("a<b>c:d.heic", "a_b_c_d.heic")]
    [InlineData("normal_name.MOV", "normal_name.MOV")]
    public void Sanitizes_invalid_filename_characters(string input, string expected)
    {
        DateFolderOrganizer.SanitizeFileName(input).ShouldBe(expected);
    }

    [Fact]
    public void Sanitize_never_returns_empty()
    {
        DateFolderOrganizer.SanitizeFileName("   ").ShouldBe("unnamed");
    }

    [Theory]
    [InlineData("/DCIM/100APPLE/IMG_0001.HEIC", "IMG_0001.HEIC")]
    [InlineData("IMG_0009.MOV", "IMG_0009.MOV")]
    public void Extracts_filename_from_remote_path(string path, string expected)
    {
        DateFolderOrganizer.ExtractFileName(path).ShouldBe(expected);
    }
}
