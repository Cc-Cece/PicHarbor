using PicHarbor.Cli.Ui;
using PicHarbor.Core.Progress;
using Shouldly;
using Spectre.Console.Testing;
using Xunit;

namespace PicHarbor.Tests.Ui;

public sealed class LiveDashboardTests
{
    private static ProgressSnapshot Snapshot(string fileName) =>
        new(
            TotalFiles: 100,
            CopiedFiles: 10,
            SkippedFiles: 2,
            FailedFiles: 1,
            TotalBytes: 1000,
            ProcessedBytes: 400,
            CurrentBytesPerSecond: 5d * 1024 * 1024,
            AverageBytesPerSecond: 4d * 1024 * 1024,
            Eta: TimeSpan.FromMinutes(3),
            CurrentFileName: fileName,
            CurrentFileCopiedBytes: 200,
            CurrentFileTotalBytes: 400);

    [Fact]
    public void Renders_counts_speed_and_current_file()
    {
        var console = new TestConsole();

        console.Write(LiveDashboard.RenderSnapshot(Snapshot("IMG_1234.HEIC")));

        string output = console.Output;
        output.ShouldContain("MB/s");
        output.ShouldContain("done");
        output.ShouldContain("IMG_1234.HEIC");
    }

    [Fact]
    public void Escapes_markup_hostile_filenames_without_throwing()
    {
        var console = new TestConsole();

        // A device-supplied name containing markup characters must not break Spectre rendering.
        Should.NotThrow(() => console.Write(LiveDashboard.RenderSnapshot(Snapshot("IMG_[evil].HEIC"))));
        console.Output.ShouldContain("evil");
    }

    [Fact]
    public void Shows_failed_count_when_failures_occur()
    {
        var console = new TestConsole();

        console.Write(LiveDashboard.RenderSnapshot(Snapshot("IMG_1.HEIC")));

        console.Output.ShouldContain("failed");
    }
}
