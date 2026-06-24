using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.FakeDevice;
using GetAndSee.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Harness;

/// <summary>
/// In-process end-to-end tests that drive the real copy pipeline against the <see cref="FakeAfcDevice"/> with
/// no hardware — the inner-loop regression net for the whole copy engine (Sprint 3.5). Each managed-observable
/// disconnect shape (connection-fatal, parked read, between-file burst, spinning close, disconnect-at-N) is
/// reproduced ending in the exit-3 logic plus a byte-identical resume.
/// </summary>
public sealed class CopyPipelineHarnessTests
{
    private static readonly DateTimeOffset Aug2024 = new(2024, 8, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sep2024 = new(2024, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const long Mib = 1024 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_library_copies_every_file_byte_identical_and_writes_the_manifest()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create().AddSmallLibrary();
        FakeAfcDevice device = spec.Build();

        HarnessResult result = await new CopyPipelineHarness(dest.Path, device).RunAsync(Token);

        result.ExitCode.ShouldBe(0);
        result.Copied.ShouldBe(spec.Files.Count);
        result.Failed.ShouldBe(0);
        AssertArchiveByteIdentical(dest.Path, spec);
        File.Exists(Path.Combine(dest.Path, SummaryWriter.FileName)).ShouldBeTrue();
        AllDoneInJournal(dest.Path, spec);
        NoPartialsLeak(dest.Path);
    }

    [Fact]
    public async Task Organizes_into_date_folders_unsorted_and_colocates_live_photo_pairs()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_AUG.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/101APPLE/IMG_SEP.HEIC", Sep2024)
            .AddLivePhotoPair("/DCIM/101APPLE/IMG_LIVE", Sep2024)
            .AddUnsorted("/DCIM/100APPLE/NODATE.DAT");
        FakeAfcDevice device = spec.Build();

        HarnessResult result = await new CopyPipelineHarness(dest.Path, device).RunAsync(Token);

        result.ExitCode.ShouldBe(0);
        File.Exists(Path.Combine(dest.Path, "2024", "2024-08", "IMG_AUG.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(dest.Path, "2024", "2024-09", "IMG_SEP.HEIC")).ShouldBeTrue();
        // A Live Photo pair shares a capture date, so its still and motion clip co-locate in one folder.
        File.Exists(Path.Combine(dest.Path, "2024", "2024-09", "IMG_LIVE.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(dest.Path, "2024", "2024-09", "IMG_LIVE.MOV")).ShouldBeTrue();
        // A file with no trustworthy capture date routes to unsorted/.
        File.Exists(Path.Combine(dest.Path, DateFolderOrganizer.UnsortedFolder, "NODATE.DAT")).ShouldBeTrue();
    }

    [Fact]
    public async Task Disambiguates_same_name_files_with_numeric_suffixes()
    {
        using TempDirectory dest = new();
        // Three same-named files captured the same month collide on the destination name.
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/101APPLE/IMG_0001.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/102APPLE/IMG_0001.HEIC", Aug2024);
        FakeAfcDevice device = spec.Build();

        HarnessResult result = await new CopyPipelineHarness(dest.Path, device).RunAsync(Token);

        result.ExitCode.ShouldBe(0);
        result.Copied.ShouldBe(3);
        string folder = Path.Combine(dest.Path, "2024", "2024-08");
        File.Exists(Path.Combine(folder, "IMG_0001.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "IMG_0001_2.HEIC")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "IMG_0001_3.HEIC")).ShouldBeTrue();
        // Never overwritten: the three distinct files keep three distinct contents.
        byte[] first = File.ReadAllBytes(Path.Combine(folder, "IMG_0001.HEIC"));
        byte[] second = File.ReadAllBytes(Path.Combine(folder, "IMG_0001_2.HEIC"));
        first.ShouldNotBe(second);
    }

    [Fact]
    public async Task Verify_hash_records_sha256_for_every_file()
    {
        using TempDirectory dest = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_1.HEIC", Aug2024)
            .AddLargeVideo("/DCIM/100APPLE/IMG_2.MOV", Aug2024);
        FakeAfcDevice device = spec.Build();

        HarnessResult result = await new CopyPipelineHarness(dest.Path, device, verifyHash: true).RunAsync(Token);

        result.ExitCode.ShouldBe(0);
        foreach (FakeDeviceFile file in spec.Files)
        {
            string expected = ExpectedSha256(file);
            ManifestSha256(dest.Path, ExpectedRelativePath(file)).ShouldBe(expected);
        }
    }

    [Fact]
    public async Task A_slow_but_progressing_read_completes_and_never_trips_the_watchdog()
    {
        using TempDirectory dest = new();
        // The negative case for the inactivity watchdog: a slow read delivers content in many small chunks.
        // With the watchdog armed it must still complete — every chunk resets the forward-progress clock — so
        // a slow device is never mistaken for a stalled one.
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddLargeVideo("/DCIM/100APPLE/IMG_SLOW.MOV", Aug2024, fault: ReadFault.Slow);
        FakeAfcDevice device = spec.Build();

        HarnessResult result = await new CopyPipelineHarness(dest.Path, device, readTimeout: Timeout).RunAsync(Token);

        result.ExitCode.ShouldBe(0);
        result.Copied.ShouldBe(1);
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    [Fact]
    public async Task Connection_loss_mid_copy_stops_at_exit_3_then_resumes_byte_identical()
    {
        using TempDirectory dest = new();
        // The middle file's read delivers 1 MiB, then the connection drops (a cable yank surfacing fatal).
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_A.HEIC", Aug2024)
            .AddLargeVideo("/DCIM/100APPLE/IMG_B.MOV", Aug2024, fault: ReadFault.ConnectionFatalAfter(1 * Mib))
            .AddSmallPhoto("/DCIM/100APPLE/IMG_C.HEIC", Aug2024);
        FakeAfcDevice device = spec.Build();

        HarnessResult interrupted = await new CopyPipelineHarness(dest.Path, device, readTimeout: Timeout).RunAsync(Token);

        interrupted.ExitCode.ShouldBe(3);
        interrupted.Copied.ShouldBe(1); // only IMG_A finished before the drop
        InProgressInJournal(dest.Path, spec.Files[1]); // IMG_B left resumable, never falsely done
        NoPartialsLeak(dest.Path);

        // Reconnect (healed device), re-run the same command: the done file is skipped, the rest complete.
        FakeAfcDevice healed = spec.Healed().Build();
        HarnessResult resume = await new CopyPipelineHarness(dest.Path, healed, readTimeout: Timeout).RunAsync(Token);

        resume.ExitCode.ShouldBe(0);
        resume.Skipped.ShouldBe(1); // IMG_A already done
        resume.Copied.ShouldBe(2);  // IMG_B + IMG_C
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    [Fact]
    public async Task Watchdog_trip_on_a_parked_read_stops_at_exit_3_then_resumes_byte_identical()
    {
        using TempDirectory dest = new();
        FakeTimeProvider clock = new();
        RecordingProcessTerminator terminator = new();
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_A.HEIC", Aug2024)
            .AddLargeVideo("/DCIM/100APPLE/IMG_B.MOV", Aug2024, fault: ReadFault.ParkAfter(1 * Mib))
            .AddSmallPhoto("/DCIM/100APPLE/IMG_C.HEIC", Aug2024);
        FakeAfcDevice device = spec.Build();
        CopyPipelineHarness harness = new(dest.Path, device, clock: clock, readTimeout: Timeout, terminator: terminator);

        Task<HarnessResult> run = Task.Run(() => harness.RunAsync(Token));
        await device.ReadParked;        // IMG_B delivered 1 MiB then the native read parked
        clock.Advance(Timeout);         // no bytes for the timeout → the forward-progress watchdog trips

        // The escape-hatch requested exit 3 on the watchdog's timer thread (asserted on this thread, where
        // the trip completed synchronously, so there is no cross-thread read race).
        terminator.WasInvoked.ShouldBeTrue("the watchdog trip must run the escape-hatch");
        terminator.ExitCode.ShouldBe(3);

        HarnessResult interrupted = await run;
        interrupted.ExitCode.ShouldBe(3);
        InProgressInJournal(dest.Path, spec.Files[1]);
        device.ReleaseStalledReads();   // unwind the orphaned native-read analogue

        FakeAfcDevice healed = spec.Healed().Build();
        HarnessResult resume = await new CopyPipelineHarness(dest.Path, healed, readTimeout: Timeout).RunAsync(Token);

        resume.ExitCode.ShouldBe(0);
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    [Fact]
    public async Task Between_file_failure_burst_trips_the_breaker_at_exit_3_then_resumes()
    {
        using TempDirectory dest = new();
        // A cable-yank that fast-fails every file (each read returns empty) instead of parking. The
        // forward-progress breaker must stop the run rather than churn every file at 100% CPU (#38).
        FakeDeviceSpec spec = FakeDeviceSpec.Create();
        for (int i = 0; i < 14; i++)
        {
            spec.AddSmallPhoto($"/DCIM/100APPLE/IMG_{i:D4}.HEIC", Aug2024);
        }

        spec.FailEveryReadWith(ReadFault.Empty);
        FakeAfcDevice device = spec.Build();

        HarnessResult interrupted = await new CopyPipelineHarness(dest.Path, device, readTimeout: Timeout).RunAsync(Token);

        interrupted.ExitCode.ShouldBe(3);
        // It stopped early — far fewer than all 14 files were processed before the breaker fired.
        device.OpenReadCount.ShouldBeLessThan(14);
        NoPartialsLeak(dest.Path);

        FakeAfcDevice healed = spec.Healed().Build();
        HarnessResult resume = await new CopyPipelineHarness(dest.Path, healed, readTimeout: Timeout).RunAsync(Token);

        resume.ExitCode.ShouldBe(0);
        resume.Copied.ShouldBe(14);
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    [Fact]
    public async Task Premature_eof_marks_the_file_failed_then_a_retry_completes_it()
    {
        using TempDirectory dest = new();
        // An isolated short-return (the device returns fewer bytes than it declared) is a per-file size
        // mismatch: the file is failed (never published partial) and the run continues, exiting 1.
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_OK.HEIC", Aug2024)
            .AddLargeVideo("/DCIM/100APPLE/IMG_SHORT.MOV", Aug2024, fault: ReadFault.PrematureEofAfter(1 * Mib));
        FakeAfcDevice device = spec.Build();

        HarnessResult result = await new CopyPipelineHarness(dest.Path, device, readTimeout: Timeout).RunAsync(Token);

        result.ExitCode.ShouldBe(1);
        result.Copied.ShouldBe(1);
        result.Failed.ShouldBe(1);
        FailedInJournal(dest.Path, spec.Files[1]);
        NoPartialsLeak(dest.Path);

        // A retry against a healed device completes the previously short file, byte-identical.
        FakeAfcDevice healed = spec.Healed().Build();
        HarnessResult retry = await new CopyPipelineHarness(dest.Path, healed, readTimeout: Timeout).RunAsync(Token);

        retry.ExitCode.ShouldBe(0);
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    [Fact]
    public async Task Spinning_close_disconnect_terminates_with_exit_3_then_resumes_byte_identical()
    {
        using TempDirectory dest = new();
        FakeTimeProvider clock = new();
        RecordingProcessTerminator terminator = new();
        // The #45 native shape's MANAGED analogue: a premature EOF whose stream then busy-spins on dispose
        // (afc_file_close pinning a core). The escape-hatch must terminate (exit 3) from its timer thread.
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_A.HEIC", Aug2024)
            .AddLargeVideo("/DCIM/126APPLE/IMG_SPIN.MOV", Aug2024, fault: ReadFault.SpinOnDisposeAfter(1 * Mib));
        FakeAfcDevice device = spec.Build();
        CopyPipelineHarness harness = new(dest.Path, device, clock: clock, readTimeout: Timeout, terminator: terminator);

        Task<HarnessResult> run = Task.Run(() => harness.RunAsync(Token));
        try
        {
            await device.DisposeSpinStarted;    // IMG_SPIN's close is now busy-spinning (the #45 hang)
            clock.Advance(Timeout);             // heartbeat dead → watchdog trips on its independent timer thread

            // The escape-hatch fired on the timer thread while the synchronous close is still wedged.
            terminator.WasInvoked.ShouldBeTrue("the watchdog must terminate when the close spins (#45)");
            terminator.ExitCode.ShouldBe(3);
            run.IsCompleted.ShouldBeFalse("the spinning close must still be wedged when the terminate fires");
        }
        finally
        {
            // Reap the pinned core as the OS would on a real terminate — even if an assertion above threw —
            // so the SpinWait busy-loop never keeps a CPU pinned for the rest of the test run.
            device.ReleaseDisposeSpins();
            await run;
        }

        File.Exists(Path.Combine(dest.Path, SummaryWriter.FileName)).ShouldBeTrue();

        FakeAfcDevice healed = spec.Healed().Build();
        HarnessResult resume = await new CopyPipelineHarness(dest.Path, healed, readTimeout: Timeout).RunAsync(Token);

        resume.ExitCode.ShouldBe(0);
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    [Fact]
    public async Task Disconnect_at_file_n_stops_at_exit_3_then_resumes_byte_identical()
    {
        using TempDirectory dest = new();
        // The device serves two files, then is gone: every later open fails connection-fatal.
        FakeDeviceSpec spec = FakeDeviceSpec.Create()
            .AddSmallPhoto("/DCIM/100APPLE/IMG_1.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/100APPLE/IMG_2.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/100APPLE/IMG_3.HEIC", Aug2024)
            .AddSmallPhoto("/DCIM/100APPLE/IMG_4.HEIC", Aug2024)
            .DisconnectAfterFiles(2);
        FakeAfcDevice device = spec.Build();

        HarnessResult interrupted = await new CopyPipelineHarness(dest.Path, device, readTimeout: Timeout).RunAsync(Token);

        interrupted.ExitCode.ShouldBe(3);
        interrupted.Copied.ShouldBe(2);
        NoPartialsLeak(dest.Path);

        FakeAfcDevice healed = spec.Healed().Build();
        HarnessResult resume = await new CopyPipelineHarness(dest.Path, healed, readTimeout: Timeout).RunAsync(Token);

        resume.ExitCode.ShouldBe(0);
        resume.Skipped.ShouldBe(2);
        resume.Copied.ShouldBe(2);
        AssertArchiveByteIdentical(dest.Path, spec);
    }

    private static void AssertArchiveByteIdentical(string destinationRoot, FakeDeviceSpec spec)
    {
        foreach (FakeDeviceFile file in spec.Files)
        {
            string full = Path.Combine(destinationRoot, ExpectedRelativePath(file));
            File.Exists(full).ShouldBeTrue($"{file.Path} should be copied to {ExpectedRelativePath(file)}");
            byte[] expected = FakeContent.Materialize((int)file.Size, file.ContentSeed);
            File.ReadAllBytes(full).ShouldBe(expected, $"{file.Path} content must be byte-identical");
        }
    }

    private static string ExpectedRelativePath(FakeDeviceFile file)
    {
        DateFolderOrganizer organizer = new();
        RemoteFile remote = new(file.Path, file.Size, file.CaptureDate);
        return organizer.GetRelativeDestination(remote, MediaMetadata.Empty);
    }

    private static string ExpectedSha256(FakeDeviceFile file)
    {
        byte[] content = FakeContent.Materialize((int)file.Size, file.ContentSeed);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
    }

    private static void AllDoneInJournal(string destinationRoot, FakeDeviceSpec spec)
    {
        using TransferJournal journal = TransferJournal.OpenReadOnly(destinationRoot);
        foreach (FakeDeviceFile file in spec.Files)
        {
            journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done, $"{file.Path} should be done");
        }
    }

    private static void InProgressInJournal(string destinationRoot, FakeDeviceFile file)
    {
        using TransferJournal journal = TransferJournal.OpenReadOnly(destinationRoot);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);
    }

    private static void FailedInJournal(string destinationRoot, FakeDeviceFile file)
    {
        using TransferJournal journal = TransferJournal.OpenReadOnly(destinationRoot);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Failed);
    }

    private static void NoPartialsLeak(string destinationRoot) =>
        Directory.GetFiles(destinationRoot, "*.partial", SearchOption.AllDirectories).ShouldBeEmpty();

    private static string? ManifestSha256(string destinationRoot, string relativeDest)
    {
        Microsoft.Data.Sqlite.SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Path.Combine(destinationRoot, TransferJournal.DatabaseFileName),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
        };
        using Microsoft.Data.Sqlite.SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM manifest WHERE dest_path = $dest;";
        command.Parameters.AddWithValue("$dest", relativeDest);
        return command.ExecuteScalar() as string;
    }
}
