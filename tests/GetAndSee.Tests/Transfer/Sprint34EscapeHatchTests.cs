using System.Text;
using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.Core.Transfer;
using GetAndSee.FakeDevice;
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
        ScriptedReadStream spinning = new(1, 392_323_980L, ReadFault.SpinOnDisposeAfter(2 * 1024 * 1024));

        RemoteFile file = new("/DCIM/126APPLE/IMG_6834.MOV", 392_323_980L, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(spinning));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path, OrganizeScheme.YearMonth,
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

            // The close is STILL wedged: the escape-hatch fired while the synchronous spin was active (the
            // #45 shape), not after it somehow unwound. (Asserted in the try so the finally below always
            // releases the spin — keeping it before ReleaseSpin must never skip that cleanup.)
            copy.IsCompleted.ShouldBeFalse("the spinning close must still be wedged when the terminate fires");
        }
        finally
        {
            // Reap the orphaned spinning thread exactly as the OS would on a real TerminateProcess, so the
            // test never leaks a pinned core — whether the assertions passed or failed.
            spinning.ReleaseSpin();
            await copy;
        }
    }

    [Fact]
    public async Task A_parked_read_with_the_escape_hatch_wired_terminates_with_exit_3()
    {
        // A prior manifestation (#11 / #42): the read parks mid-file (ignores cancellation, never returns).
        // With the escape-hatch wired, the watchdog reaches the same single outcome as the spinning close —
        // terminate with exit 3 on its independent timer thread.
        FakeTimeProvider clock = new();
        RecordingProcessTerminator terminator = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, TextWriter.Null);
        ScriptedReadStream stream = new(1, 392_323_980L, ReadFault.ParkAfter(3 * 1024 * 1024, observeCancellation: false));

        RemoteFile file = new("/DCIM/126APPLE/IMG_PARK.MOV", 392_323_980L, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(stream));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path, OrganizeScheme.YearMonth,
            clock: clock, readTimeout: TimeSpan.FromSeconds(15), onDisconnect: escapeHatch.Activate);

        Task<CopyResult> copy = copier.CopyAsync(file, Token);
        await stream.ParkedReadStarted;             // 3 MB streamed, then the read parks
        clock.Advance(TimeSpan.FromSeconds(15));     // a full timeout after the freeze → the watchdog trips

        terminator.WasInvoked.ShouldBeTrue("a parked read past the timeout must terminate (exit 3)");
        terminator.ExitCode.ShouldBe(DisconnectEscapeHatch.DisconnectExitCode);

        stream.ReleasePark(); // let the abandoned read unwind; the token-cancel path stays resumable
        await Should.ThrowAsync<DeviceConnectionLostException>(async () => await copy);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);
        Directory.GetFiles(destination.Path, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_healthy_run_with_the_escape_hatch_wired_never_terminates()
    {
        // False-positive guard: a normal file copies and completes; the escape-hatch (a process terminate in
        // production) must never fire on a healthy run.
        FakeTimeProvider clock = new();
        RecordingProcessTerminator terminator = new();
        DisconnectEscapeHatch escapeHatch = new(destination.Path, terminator, TextWriter.Null);
        byte[] content = Encoding.UTF8.GetBytes("healthy media bytes — copied cleanly");
        RemoteFile file = new("/DCIM/100APPLE/IMG_OK.HEIC", content.Length, null);
        client.OpenReadAsync(file.Path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(content, writable: false)));
        journal.EnsurePending(file);

        using FileCopier copier = new(
            client, journal, organizer, extractor, destination.Path, OrganizeScheme.YearMonth,
            clock: clock, readTimeout: TimeSpan.FromSeconds(15), onDisconnect: escapeHatch.Activate);

        CopyResult result = await copier.CopyAsync(file, Token);

        result.Status.ShouldBe(CopyStatus.Copied);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
        terminator.WasInvoked.ShouldBeFalse("a healthy run must never trip the escape-hatch");
    }
}
