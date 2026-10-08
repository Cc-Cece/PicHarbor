using PicHarbor.Core.Device;
using PicHarbor.Core.Organize;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Organize;

public sealed class DateFolderOrganizerTests
{
    private readonly DateFolderOrganizer organizer = new();

    [Theory]
    [InlineData(OrganizeScheme.Month, "2024-08/IMG_0001.HEIC")]
    [InlineData(OrganizeScheme.YearMonth, "2024/2024-08/IMG_0001.HEIC")]
    [InlineData(OrganizeScheme.Year, "2024/IMG_0001.HEIC")]
    [InlineData(OrganizeScheme.Flat, "IMG_0001.HEIC")]
    public void Places_dated_file_per_scheme(OrganizeScheme scheme, string expectedForwardSlash)
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 1000, null);
        var metadata = new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone 12 Pro");

        string result = organizer.GetRelativeDestination(file, metadata, scheme);

        result.ShouldBe(expectedForwardSlash.Replace('/', Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData(OrganizeScheme.Month)]
    [InlineData(OrganizeScheme.YearMonth)]
    [InlineData(OrganizeScheme.Year)]
    [InlineData(OrganizeScheme.Flat)]
    public void Unsorted_is_identical_under_every_scheme(OrganizeScheme scheme)
    {
        // A file with no trustworthy capture date is never placed in a dated folder — the unsorted
        // fallback is the same regardless of layout.
        var file = new RemoteFile("/DCIM/100APPLE/NODATE.DAT", 1000, null);

        string result = organizer.GetRelativeDestination(file, MediaMetadata.Empty, scheme);

        result.ShouldBe(Path.Combine(DateFolderOrganizer.UnsortedFolder, "NODATE.DAT"));
    }

    [Theory]
    [InlineData(OrganizeScheme.Month)]
    [InlineData(OrganizeScheme.YearMonth)]
    [InlineData(OrganizeScheme.Year)]
    [InlineData(OrganizeScheme.Flat)]
    public void Live_photo_pair_colocates_under_every_scheme(OrganizeScheme scheme)
    {
        // A Live Photo's still (.HEIC) and motion clip (.MOV) share a capture date, so they must land in
        // the same folder under every scheme — the pair is never split across folders.
        var captured = new DateTimeOffset(2024, 9, 3, 7, 45, 0, TimeSpan.Zero);
        var still = new RemoteFile("/DCIM/101APPLE/IMG_0101.HEIC", 100, captured);
        var motion = new RemoteFile("/DCIM/101APPLE/IMG_0101.MOV", 200, captured);

        string stillPath = organizer.GetRelativeDestination(still, MediaMetadata.Empty, scheme);
        string motionPath = organizer.GetRelativeDestination(motion, MediaMetadata.Empty, scheme);

        Path.GetDirectoryName(stillPath).ShouldBe(Path.GetDirectoryName(motionPath));
    }

    [Fact]
    public void Prefers_exif_date_over_mtime()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 1000, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var metadata = new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone 12 Pro");

        string result = organizer.GetRelativeDestination(file, metadata, OrganizeScheme.YearMonth);

        result.ShouldBe(Path.Combine("2024", "2024-08", "IMG_0001.HEIC"));
    }

    [Fact]
    public void Falls_back_to_mtime_when_no_exif()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0002.MOV", 1000, new DateTimeOffset(2023, 2, 9, 12, 0, 0, TimeSpan.Zero));

        string result = organizer.GetRelativeDestination(file, MediaMetadata.Empty, OrganizeScheme.YearMonth);

        result.ShouldBe(Path.Combine("2023", "2023-02", "IMG_0002.MOV"));
    }

    [Fact]
    public void Routes_to_unsorted_when_no_date_at_all()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0003.PNG", 1000, null);

        string result = organizer.GetRelativeDestination(file, MediaMetadata.Empty, OrganizeScheme.Month);

        result.ShouldBe(Path.Combine("unsorted", "IMG_0003.PNG"));
    }

    [Fact]
    public void Rejects_future_exif_date_and_falls_back_to_mtime()
    {
        // R16: a capture date far in the future is nonsense; fall back to the file's mtime.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0004.HEIC", 1000, new DateTimeOffset(2022, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var metadata = new MediaMetadata(new DateTime(2999, 1, 1), null, null, null, null);

        string result = organizer.GetRelativeDestination(file, metadata, OrganizeScheme.YearMonth);

        result.ShouldBe(Path.Combine("2022", "2022-05", "IMG_0004.HEIC"));
    }

    [Fact]
    public void Rejects_prehistoric_date_and_routes_to_unsorted()
    {
        // R16: before 1990 is out of range; with no valid fallback the file is unsorted.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0005.HEIC", 1000, new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var metadata = new MediaMetadata(new DateTime(1925, 1, 1), null, null, null, null);

        string result = organizer.GetRelativeDestination(file, metadata, OrganizeScheme.YearMonth);

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
