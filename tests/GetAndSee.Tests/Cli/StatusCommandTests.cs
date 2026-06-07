using GetAndSee.Cli.Commands;
using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Cli;

/// <summary>
/// Serializes tests that capture <see cref="Console.Out"/> so a parallel test class can't race the
/// process-wide console redirect.
/// </summary>
[CollectionDefinition("console", DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection("console")]
public sealed class StatusCommandTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void Returns_exit_2_when_no_archive_exists()
    {
        // The destination exists but has no get-and-see.db — status must not create one (read-only).
        int exit = StatusCommand.Run(dir.Path);

        exit.ShouldBe(2);
        File.Exists(Path.Combine(dir.Path, TransferJournal.DatabaseFileName)).ShouldBeFalse();
    }

    [Fact]
    public void Prints_summary_sections_and_returns_0_for_an_existing_archive()
    {
        SeedArchive();

        (int exit, string output) = CaptureRun(dir.Path);

        exit.ShouldBe(0);
        output.ShouldContain("get-and-see archive at");
        output.ShouldContain("Total: 1 files");
        output.ShouldContain("Sample iPhone (iPhone13,3)");
    }

    private void SeedArchive()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        var file = new RemoteFile("/DCIM/100APPLE/IMG_1.HEIC", 2_000_000, null);
        journal.EnsurePending(file);
        journal.UpsertDevice("00008101-ABCDEF", "Sample iPhone", "iPhone13,3", DateTimeOffset.UtcNow);
        journal.MarkDone(
            file.Path,
            file.Size,
            Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone"),
            DateTimeOffset.UtcNow);
    }

    private static (int Exit, string Output) CaptureRun(string destination)
    {
        TextWriter original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            int exit = StatusCommand.Run(destination);
            return (exit, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
