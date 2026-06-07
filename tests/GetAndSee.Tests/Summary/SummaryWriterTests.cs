using GetAndSee.Core.Journal;
using GetAndSee.Core.Summary;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Summary;

public sealed class SummaryWriterTests
{
    [Fact]
    public void Builds_summary_in_session3_format()
    {
        var writer = new SummaryWriter();
        var manifest = new List<ManifestEntry>
        {
            new(Path.Combine("2024", "2024-08", "IMG_1.HEIC"), 2_000_000, "2024-08-15T10:00:00", null),
            new(Path.Combine("2024", "2024-08", "IMG_1.MOV"), 5_000_000, "2024-08-15T10:00:00", null),
            new(Path.Combine("unsorted", "Screenshot.PNG"), 500_000, null, "2023-01-01T00:00:00Z"),
        };
        var devices = new List<DeviceRecord> { new("00008101-ABCDEF", "Denis's iPhone", "iPhone13,3") };
        var runs = new RunsSummary(
            TotalRuns: 4,
            FirstRunAt: DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            LatestRunAt: DateTimeOffset.Parse("2026-06-06T14:00:00Z"),
            LastCopied: 3,
            LastSkipped: 0,
            LastFailed: 0,
            LastElapsed: TimeSpan.FromMinutes(5));

        string text = writer.Build(@"D:\Photos", manifest, devices, runs, DateTimeOffset.Parse("2026-06-06T14:32:11Z"));

        text.ShouldContain(@"get-and-see archive at D:\Photos");
        text.ShouldContain("Last updated: 2026-06-06 14:32:11 UTC");
        text.ShouldContain("Total: 3 files");
        text.ShouldContain("Photos:");
        text.ShouldContain("Videos:");
        text.ShouldContain("Screenshots:");
        text.ShouldContain("Denis's iPhone (iPhone13,3)");
        text.ShouldContain("Date range: 2023-01-01 to 2024-08-15");
        text.ShouldContain("Runs: 4");
        text.ShouldContain("first run 2026-06-01");
        text.ShouldContain("3 copied");
    }

    [Fact]
    public void Omits_device_and_runs_lines_when_empty()
    {
        var writer = new SummaryWriter();
        var runs = new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero);

        string text = writer.Build(@"D:\Photos", [], [], runs, DateTimeOffset.UtcNow);

        text.ShouldNotContain("Devices:");
        text.ShouldNotContain("Runs:");
        text.ShouldContain("Total: 0 files");
    }

    [Fact]
    public void Live_photo_line_is_omitted_when_no_pairs()
    {
        var writer = new SummaryWriter();
        var manifest = new List<ManifestEntry>
        {
            new(Path.Combine("2024", "2024-08", "IMG_1.HEIC"), 2_000_000, "2024-08-15T10:00:00", null),
            new(Path.Combine("2024", "2024-08", "IMG_2.MOV"), 3_000_000, "2024-08-15T11:00:00", null),
        };
        var runs = new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero);

        string text = writer.Build(@"D:\Photos", manifest, [], runs, DateTimeOffset.UtcNow);

        text.ShouldNotContain("Live Photos:");
    }

    [Fact]
    public void Single_live_photo_uses_singular_pair()
    {
        var writer = new SummaryWriter();
        var manifest = new List<ManifestEntry>
        {
            new(Path.Combine("2024", "2024-08", "IMG_1.HEIC"), 2_000_000, "2024-08-15T10:00:00", null),
            new(Path.Combine("2024", "2024-08", "IMG_1.MOV"), 3_000_000, "2024-08-15T10:00:00", null),
        };
        var runs = new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero);

        string text = writer.Build(@"D:\Photos", manifest, [], runs, DateTimeOffset.UtcNow);

        text.ShouldContain("Live Photos: 1 pair");
        text.ShouldNotContain("Live Photos: 1 pairs");
    }

    [Fact]
    public void Multiple_live_photos_use_plural_pairs()
    {
        var writer = new SummaryWriter();
        var manifest = new List<ManifestEntry>
        {
            new(Path.Combine("2024", "2024-08", "IMG_1.HEIC"), 2_000_000, "2024-08-15T10:00:00", null),
            new(Path.Combine("2024", "2024-08", "IMG_1.MOV"), 3_000_000, "2024-08-15T10:00:00", null),
            new(Path.Combine("2024", "2024-08", "IMG_2.HEIC"), 2_000_000, "2024-08-15T11:00:00", null),
            new(Path.Combine("2024", "2024-08", "IMG_2.MOV"), 3_000_000, "2024-08-15T11:00:00", null),
        };
        var runs = new RunsSummary(0, null, null, 0, 0, 0, TimeSpan.Zero);

        string text = writer.Build(@"D:\Photos", manifest, [], runs, DateTimeOffset.UtcNow);

        text.ShouldContain("Live Photos: 2 pairs");
    }
}
