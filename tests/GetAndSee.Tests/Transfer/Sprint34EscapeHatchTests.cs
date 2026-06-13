using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.Core.Transfer;
using GetAndSee.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Transfer;

/// <summary>
/// The Sprint 3.4 escape-hatch repro (#45): a read that faults (premature EOF) followed by a disposal/close
/// that <b>busy-spins</b> — the real shape captured live (<c>afc_file_close</c> pinning a core on a dead
/// transport). On its independent timer thread the run-level forward-progress watchdog must write the
/// summary and invoke the injected process terminator with exit code 3 <i>without</i> re-entering native
/// code, so the host survives and there is no hang. A stall double or a cancellation-ignoring read does NOT
/// satisfy this — it models a spinning unwind, deterministically, on a fake clock.
/// </summary>
public sealed class Sprint34EscapeHatchTests : IDisposable
{
    private readonly TempDirectory destination = new();
    private readonly TransferJournal journal;
    private readonly DateFolderOrganizer organizer = new();
    private readonly IMediaMetadataExtractor extractor = Substitute.For<IMediaMetadataExtractor>();
    private readonly IPhoneClient client = Substitute.For<IPhoneClient>();

    public Sprint34EscapeHatchTests()
    {
        journal = TransferJournal.Open(destination.Path);
        extractor.Extract(Arg.Any<string>())
            .Returns(new MediaMetadata(new DateTime(2024, 8, 15, 10, 0, 0), null, null, "Apple", "iPhone"));
    }

    public void Dispose()
    {
        journal.Dispose();
        destination.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spinning_close_during_disposal_writes_summary_and_terminates_with_exit_3()
    {
        // #45: bytes flow, then a premature zero-byte read (the yank), then afc_file_close busy-spins during
        // the await-using disposal. The watchdog's independent timer must escape via a hard terminate, not
        // hang waiting on native code that will never return.
        FakeTimeProvider clock = new();
        RecordingProcessTerminator terminator = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, TextWriter.Null);
        SpinOnDisposeStream spinning = new(chunksBeforeEof: 2, chunkSize: 1024 * 1024);

        RemoteFile file = new("/DCIM/126APPLE/IMG_6834.MOV", 392_323_980L, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(spinning));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path,
            clock: clock, readTimeout: TimeSpan.FromSeconds(30),
            onDisconnect: escapeHatch.Activate);

        // Run the copy OFF the test thread: the close-spin is a SYNCHRONOUS block (reads complete
        // synchronously), so it would otherwise wedge the test thread before we can advance the clock —
        // just as the real spin wedges the main copy thread while the independent timer stays free.
        Task<CopyResult> copy = Task.Run(() => copier.CopyAsync(file, Token));
        try
        {
            await spinning.DisposeSpinStarted;        // the close is now busy-spinning (the #45 hang)

            clock.Advance(TimeSpan.FromSeconds(30));   // heartbeat dead → watchdog trips on its timer thread

            // The escape-hatch ran on the (independent) timer thread: summary written, then terminate(3).
            terminator.WasInvoked.ShouldBeTrue("the watchdog must terminate when the close spins (#45)");
            terminator.ExitCode.ShouldBe(DisconnectEscapeHatch.DisconnectExitCode);
            File.Exists(Path.Combine(destination.Path, SummaryWriter.FileName))
                .ShouldBeTrue("summary.txt must be written before the terminate");
        }
        finally
        {
            // Reap the orphaned spinning thread exactly as the OS would on a real TerminateProcess, so the
            // test never leaks a pinned core — whether the assertions passed or failed.
            spinning.ReleaseSpin();
            await copy;
        }
    }
}
