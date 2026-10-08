using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Search;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Search;

public sealed class MediaSearchTests : IDisposable
{
    private static readonly string PhotoA = Path.Combine("2024-08", "A.HEIC");   // 2 MiB, Apple iPhone 12 Pro, GPS
    private static readonly string VideoB = Path.Combine("2024-09", "B.MOV");    // 50 MiB, Apple iPhone 12 Pro, no GPS
    private static readonly string ShotC = Path.Combine("2023-01", "C.PNG");     // 500 KB, no camera, no GPS
    private static readonly string OtherD = Path.Combine("unsorted", "D.DAT");   // 1 KB, undated, no camera, no GPS

    private readonly TempDirectory dir = new();

    public MediaSearchTests() => Seed();

    public void Dispose() => dir.Dispose();

    [Theory]
    [InlineData(MediaType.Photo)]
    [InlineData(MediaType.Video)]
    [InlineData(MediaType.Screenshot)]
    [InlineData(MediaType.Other)]
    public void Filters_by_type(MediaType type)
    {
        string expected = type switch
        {
            MediaType.Photo => PhotoA,
            MediaType.Video => VideoB,
            MediaType.Screenshot => ShotC,
            _ => OtherD,
        };

        Find(new MediaSearchCriteria(Type: type)).ShouldBe([expected]);
    }

    [Fact]
    public void Filters_by_from_date_excluding_undated()
    {
        // Everything captured in 2024 (the 2023 screenshot and the undated file are excluded).
        Find(new MediaSearchCriteria(From: Utc(2024, 1, 1))).ShouldBe([PhotoA, VideoB]);
    }

    [Fact]
    public void Filters_by_to_date_excluding_undated()
    {
        Find(new MediaSearchCriteria(To: EndOfDay(2023, 12, 31))).ShouldBe([ShotC]);
    }

    [Fact]
    public void Filters_by_a_single_month_window()
    {
        Find(new MediaSearchCriteria(From: Utc(2024, 8, 1), To: EndOfDay(2024, 8, 31))).ShouldBe([PhotoA]);
    }

    [Theory]
    [InlineData("iPhone 12")]
    [InlineData("apple")]
    public void Filters_by_camera_substring_case_insensitively(string camera)
    {
        Find(new MediaSearchCriteria(Camera: camera)).ShouldBe([PhotoA, VideoB]);
    }

    [Fact]
    public void Filters_by_min_size()
    {
        Find(new MediaSearchCriteria(MinSize: 1024 * 1024)).ShouldBe([PhotoA, VideoB]);
    }

    [Fact]
    public void Filters_by_max_size()
    {
        Find(new MediaSearchCriteria(MaxSize: 1024 * 1024)).ShouldBe([ShotC, OtherD]);
    }

    [Fact]
    public void Filters_by_has_gps()
    {
        Find(new MediaSearchCriteria(HasGps: true)).ShouldBe([PhotoA]);
    }

    [Fact]
    public void Combines_type_and_gps()
    {
        Find(new MediaSearchCriteria(Type: MediaType.Photo, HasGps: true)).ShouldBe([PhotoA]);
    }

    [Fact]
    public void Combines_date_and_type()
    {
        Find(new MediaSearchCriteria(From: Utc(2024, 1, 1), Type: MediaType.Video)).ShouldBe([VideoB]);
    }

    [Fact]
    public void Combines_a_size_range()
    {
        Find(new MediaSearchCriteria(MinSize: 1_000_000, MaxSize: 10_000_000)).ShouldBe([PhotoA]);
    }

    [Fact]
    public void No_filters_returns_all_ordered_by_capture_date_undated_last()
    {
        Find(new MediaSearchCriteria()).ShouldBe([ShotC, PhotoA, VideoB, OtherD]);
    }

    [Fact]
    public void Excludes_heic_when_IncludeHeic_is_false()
    {
        Find(new MediaSearchCriteria(IncludeHeic: false)).ShouldBe([ShotC, VideoB, OtherD]);
    }

    [Fact]
    public void Filters_by_filename_keyword()
    {
        Find(new MediaSearchCriteria(FileNameKeyword: "A.HEIC")).ShouldBe([PhotoA]);
        Find(new MediaSearchCriteria(FileNameKeyword: "2024-09")).ShouldBe([VideoB]);
    }

    [Fact]
    public void Returns_empty_when_nothing_matches()
    {
        Find(new MediaSearchCriteria(Type: MediaType.Photo, To: EndOfDay(2020, 12, 31))).ShouldBeEmpty();
        Find(new MediaSearchCriteria(Camera: "Nikon")).ShouldBeEmpty();
    }

    private IReadOnlyList<string> Find(MediaSearchCriteria criteria)
    {
        using TransferJournal journal = TransferJournal.OpenReadOnly(dir.Path);
        return MediaSearch.Find(journal.ReadSearchRows(), criteria).Select(hit => hit.RelativePath).ToList();
    }

    private void Seed()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        Add(journal, "/DCIM/100APPLE/A.HEIC", 2 * 1024 * 1024, PhotoA,
            new MediaMetadata(new DateTime(2024, 8, 15, 12, 0, 0), 37.7749, -122.4194, "Apple", "iPhone 12 Pro"));
        Add(journal, "/DCIM/100APPLE/B.MOV", 50L * 1024 * 1024, VideoB,
            new MediaMetadata(new DateTime(2024, 9, 20, 8, 0, 0), null, null, "Apple", "iPhone 12 Pro"));
        Add(journal, "/DCIM/100APPLE/C.PNG", 500 * 1024, ShotC,
            new MediaMetadata(new DateTime(2023, 1, 10, 0, 0, 0), null, null, null, null));
        Add(journal, "/DCIM/100APPLE/D.DAT", 1024, OtherD, MediaMetadata.Empty);
    }

    private static void Add(TransferJournal journal, string sourcePath, long size, string dest, MediaMetadata metadata)
    {
        var file = new RemoteFile(sourcePath, size, null);
        journal.EnsurePending(file);
        journal.MarkDone(file.Path, file.Size, dest, metadata, DateTimeOffset.UtcNow);
    }

    private static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset EndOfDay(int year, int month, int day) => new(year, month, day, 23, 59, 59, TimeSpan.Zero);
}
