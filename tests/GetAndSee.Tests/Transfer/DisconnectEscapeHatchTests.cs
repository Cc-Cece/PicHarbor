using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.Core.Transfer;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Transfer;

/// <summary>
/// Unit tests for the disconnect escape-hatch (#45) in isolation: it writes <c>summary.txt</c> from a fresh
/// read-only journal connection, then invokes the injected terminator with exit code 3 exactly once, and
/// never lets a summary-write failure block the load-bearing terminate.
/// </summary>
public sealed class DisconnectEscapeHatchTests : IDisposable
{
    private readonly TempDirectory destination = new();

    public void Dispose() => destination.Dispose();

    [Fact]
    public void Activate_writes_summary_then_terminates_with_exit_3()
    {
        using (TransferJournal journal = TransferJournal.Open(destination.Path))
        {
            RemoteFile file = new("/DCIM/100APPLE/IMG_1.HEIC", 10, null);
            journal.EnsurePending(file);
            journal.MarkDone(
                file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
                MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        RecordingProcessTerminator terminator = new();
        StringWriter output = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, output);

        escapeHatch.Activate();

        File.Exists(Path.Combine(destination.Path, SummaryWriter.FileName)).ShouldBeTrue();
        terminator.Invocations.ShouldBe(1);
        terminator.ExitCode.ShouldBe(DisconnectEscapeHatch.DisconnectExitCode);
        output.ToString().ShouldContain("Device disconnected");
    }

    [Fact]
    public void Activate_fires_only_once_even_if_called_repeatedly()
    {
        using TransferJournal journal = TransferJournal.Open(destination.Path);
        RecordingProcessTerminator terminator = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, TextWriter.Null);

        escapeHatch.Activate();
        escapeHatch.Activate();
        escapeHatch.Activate();

        terminator.Invocations.ShouldBe(1);
    }

    [Fact]
    public void Activate_still_terminates_when_the_summary_cannot_be_written()
    {
        // No journal exists at the destination → the read-only open throws → the summary is skipped. The
        // terminate is the load-bearing guarantee and must still fire (the journal on disk is the durable
        // record; summary.txt is regenerated next run).
        RecordingProcessTerminator terminator = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, TextWriter.Null);

        escapeHatch.Activate();

        terminator.Invocations.ShouldBe(1);
        terminator.ExitCode.ShouldBe(DisconnectEscapeHatch.DisconnectExitCode);
    }
}
