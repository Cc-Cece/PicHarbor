using PicHarbor.Core.Device;
using PicHarbor.Core.Progress;
using PicHarbor.Core.Transfer;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Progress;

public sealed class TextProgressReporterTests
{
    private static CopyResult Copied(string path, string dest, long bytes) =>
        new(CopyStatus.Copied, new RemoteFile(path, bytes, null), dest, bytes, null);

    [Fact]
    public void Prints_done_line_with_counter_and_destination()
    {
        var writer = new StringWriter();
        var reporter = new TextProgressReporter(writer);
        var progress = new TransferProgress(2, 3000);
        reporter.Start(progress);

        progress.StartFile("IMG_1.HEIC", 1000);
        progress.RecordBytes(1000);
        progress.CompleteFile(CopyStatus.Copied);
        reporter.OnFileCompleted(Copied("/DCIM/100APPLE/IMG_1.HEIC", Path.Combine("2024", "2024-08", "IMG_1.HEIC"), 1000));

        string output = writer.ToString();
        output.ShouldContain("[done]");
        output.ShouldContain("1/2");
        output.ShouldContain(Path.Combine("2024", "2024-08", "IMG_1.HEIC"));
    }

    [Fact]
    public void Prints_skip_line()
    {
        var writer = new StringWriter();
        var reporter = new TextProgressReporter(writer);
        var progress = new TransferProgress(1, 1000);
        reporter.Start(progress);

        progress.StartFile("IMG_2.HEIC", 1000);
        progress.RecordSkippedBytes(1000);
        progress.CompleteFile(CopyStatus.Skipped);
        reporter.OnFileCompleted(new CopyResult(CopyStatus.Skipped, new RemoteFile("/DCIM/IMG_2.HEIC", 1000, null), null, 0, null));

        writer.ToString().ShouldContain("[skip]");
        writer.ToString().ShouldContain("IMG_2.HEIC");
    }

    [Fact]
    public void Prints_fail_line_with_error()
    {
        var writer = new StringWriter();
        var reporter = new TextProgressReporter(writer);
        var progress = new TransferProgress(1, 1000);
        reporter.Start(progress);

        progress.StartFile("IMG_3.HEIC", 1000);
        progress.CompleteFile(CopyStatus.Failed);
        reporter.OnFileCompleted(new CopyResult(CopyStatus.Failed, new RemoteFile("/DCIM/IMG_3.HEIC", 1000, null), null, 0, "size mismatch"));

        writer.ToString().ShouldContain("[fail]");
        writer.ToString().ShouldContain("size mismatch");
    }

    [Fact]
    public void OnFileCompleted_before_Start_throws()
    {
        var reporter = new TextProgressReporter(new StringWriter());
        Should.Throw<InvalidOperationException>(() =>
            reporter.OnFileCompleted(Copied("/DCIM/x.HEIC", "x", 1)));
    }
}
