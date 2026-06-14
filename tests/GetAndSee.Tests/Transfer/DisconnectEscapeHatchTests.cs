using System.Text;
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

    [Fact]
    public void Activate_still_terminates_when_printing_the_disconnect_line_throws()
    {
        // The terminate is the load-bearing exit-3 guarantee: a broken pipe on a redirected stdout (racing
        // the yank) must NOT skip it. A throwing output writer is swallowed, the terminator is still invoked
        // with 3, and Activate must not rethrow onto the watchdog's timer thread.
        using TransferJournal journal = TransferJournal.Open(destination.Path);
        RecordingProcessTerminator terminator = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, new ThrowingTextWriter());

        Should.NotThrow(() => escapeHatch.Activate());

        terminator.Invocations.ShouldBe(1);
        terminator.ExitCode.ShouldBe(DisconnectEscapeHatch.DisconnectExitCode);
        File.Exists(Path.Combine(destination.Path, SummaryWriter.FileName))
            .ShouldBeTrue("a swallowed print must not cascade into skipping the summary write");
    }

    /// <summary>A <see cref="TextWriter"/> whose writes always throw, modeling a broken stdout pipe.</summary>
    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        // Throw from the char funnel AND the string overload, so the guard is exercised whichever write API
        // the escape-hatch uses now or later — the test must never go silently green if the print changes.
        public override void Write(char value) =>
            throw new IOException("simulated broken pipe on stdout");

        public override void WriteLine(string? value) =>
            throw new IOException("simulated broken pipe on stdout");
    }
}
