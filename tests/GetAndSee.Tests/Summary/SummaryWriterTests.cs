using GetAndSee.Core.Device;
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
        var device = new DeviceInfo("00008101-ABCDEF", "Denis's iPhone", "iPhone13,3");
        var run = new RunStats(Enumerated: 3, Copied: 3, Skipped: 0, Failed: 0, BytesCopied: 7_500_000, Elapsed: TimeSpan.FromMinutes(5));

        string text = writer.Build(@"D:\Photos", manifest, device, run, DateTimeOffset.Parse("2026-06-06T14:32:11Z"));

        text.ShouldContain(@"get-and-see archive at D:\Photos");
        text.ShouldContain("Last updated: 2026-06-06 14:32:11 UTC");
        text.ShouldContain("Total: 3 files");
        text.ShouldContain("Photos:");
        text.ShouldContain("Videos:");
        text.ShouldContain("Screenshots:");
        text.ShouldContain("Denis's iPhone (iPhone13,3)");
        text.ShouldContain("Date range: 2023-01-01 to 2024-08-15");
        text.ShouldContain("3 copied");
    }

    [Fact]
    public void Omits_device_line_when_unknown()
    {
        var writer = new SummaryWriter();
        var run = new RunStats(0, 0, 0, 0, 0, TimeSpan.Zero);

        string text = writer.Build(@"D:\Photos", [], device: null, run, DateTimeOffset.UtcNow);

        text.ShouldNotContain("Devices:");
        text.ShouldContain("Total: 0 files");
    }
}
