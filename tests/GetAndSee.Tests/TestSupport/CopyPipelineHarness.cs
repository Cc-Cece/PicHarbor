using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.Core.Transfer;

namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// Outcome of one <see cref="CopyPipelineHarness"/> run, mirroring what the <c>copy</c> verb computes.
/// </summary>
/// <param name="ExitCode">The process exit code the real CLI would return (0 ok, 1 some failures, 3 lost device).</param>
/// <param name="Copied">Files streamed and verified this run.</param>
/// <param name="Skipped">Files already <c>done</c> and skipped (resume).</param>
/// <param name="Failed">Files that failed this run (e.g. a size mismatch).</param>
/// <param name="BytesCopied">Total bytes written this run.</param>
/// <param name="StallMessage">The lost-connection message if the run stopped on a disconnect, else <see langword="null"/>.</param>
public sealed record HarnessResult(
    int ExitCode,
    int Copied,
    int Skipped,
    int Failed,
    long BytesCopied,
    string? StallMessage);

/// <summary>
/// Drives the <b>real</b> copy pipeline end-to-end against a <see cref="FakeAfcDevice"/> with no hardware:
/// the real <see cref="DcimEnumerator"/>, <see cref="TransferJournal"/>, <see cref="DateFolderOrganizer"/>,
/// <see cref="FileCopier"/> (and through it the <c>ForwardProgressWatchdog</c>, <c>AbandonableReadStream</c>,
/// and <see cref="DisconnectEscapeHatch"/>), and the <see cref="SummaryWriter"/>. It reproduces the
/// orchestration the <c>copy</c> verb performs around those components — connect, enumerate, ensure-pending,
/// the copy loop, the disconnect catch, the run record, and the summary — so the engine is exercised exactly
/// as it ships while the CLI's Spectre UI and pre-flight (which are not what these tests assert) are left out.
/// </summary>
/// <remarks>
/// The shipped <c>CopyCommand</c> is deliberately not refactored to share this loop — keeping the shipped
/// binary byte-identical is a hard rule of this sprint — so this harness mirrors its ~30-line loop instead.
/// </remarks>
public sealed class CopyPipelineHarness
{
    private readonly string destinationRoot;
    private readonly FakeAfcDevice device;
    private readonly TimeProvider clock;
    private readonly TimeSpan readTimeout;
    private readonly bool verifyHash;
    private readonly IProcessTerminator terminator;
    private readonly IMediaMetadataExtractor metadataExtractor;

    /// <summary>Creates a harness targeting <paramref name="destinationRoot"/>, driving the real pipeline against <paramref name="device"/>.</summary>
    /// <param name="destinationRoot">Destination root (a temp directory).</param>
    /// <param name="device">The fake device to copy from.</param>
    /// <param name="clock">Time source shared with the watchdog and journal timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="readTimeout">Forward-progress watchdog timeout; <see langword="null"/>/zero disables it.</param>
    /// <param name="verifyHash">Whether to compute and record SHA-256 (<c>--verify-hash</c>).</param>
    /// <param name="terminator">
    /// Process terminator for the disconnect escape-hatch. Defaults to a <see cref="RecordingProcessTerminator"/>
    /// (so a watchdog trip never kills the test host); pass your own to assert its invocation directly.
    /// </param>
    /// <param name="metadataExtractor">Metadata extractor; defaults to <see cref="EmptyMetadataExtractor"/> (mtime-driven organize).</param>
    public CopyPipelineHarness(
        string destinationRoot,
        FakeAfcDevice device,
        TimeProvider? clock = null,
        TimeSpan? readTimeout = null,
        bool verifyHash = false,
        IProcessTerminator? terminator = null,
        IMediaMetadataExtractor? metadataExtractor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(device);
        this.destinationRoot = destinationRoot;
        this.device = device;
        this.clock = clock ?? TimeProvider.System;
        this.readTimeout = readTimeout ?? TimeSpan.Zero;
        this.verifyHash = verifyHash;
        // A recording terminator by default so a watchdog trip in a test never kills the test host; a test that
        // wants to assert the escape-hatch fired passes its own and holds the reference.
        this.terminator = terminator ?? new RecordingProcessTerminator();
        this.metadataExtractor = metadataExtractor ?? new EmptyMetadataExtractor();
    }

    /// <summary>Runs one full copy pass (connect → enumerate → copy loop → summary) and returns its outcome.</summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The run's <see cref="HarnessResult"/>.</returns>
    public async Task<HarnessResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await device.ConnectAsync(cancellationToken).ConfigureAwait(false);
        DeviceInfo? connectedDevice = device.Device;

        DcimEnumerator enumerator = new(device);
        List<RemoteFile> files = [];
        await foreach (RemoteFile file in enumerator.EnumerateAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            files.Add(file);
        }

        using TransferJournal journal = TransferJournal.Open(destinationRoot);
        DateTimeOffset runStartedAt = clock.GetUtcNow();
        if (connectedDevice is not null)
        {
            journal.UpsertDevice(connectedDevice.Udid, connectedDevice.Name, connectedDevice.ProductType, runStartedAt);
        }

        foreach (RemoteFile file in files)
        {
            journal.EnsurePending(file);
        }

        DateFolderOrganizer organizer = new();
        DisconnectEscapeHatch escapeHatch = new(destinationRoot, terminator, TextWriter.Null);
        using FileCopier copier = new(
            device, journal, organizer, metadataExtractor, destinationRoot,
            clock: clock, readTimeout: readTimeout, verifyHash: verifyHash,
            onDisconnect: escapeHatch.Activate);
        copier.CleanStaging();

        int copied = 0;
        int skipped = 0;
        int failed = 0;
        long bytesCopied = 0;
        string? stallMessage = null;

        try
        {
            foreach (RemoteFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyResult result = await copier.CopyAsync(file, cancellationToken).ConfigureAwait(false);
                switch (result.Status)
                {
                    case CopyStatus.Copied:
                        copied++;
                        bytesCopied += result.BytesCopied;
                        break;
                    case CopyStatus.Skipped:
                        skipped++;
                        break;
                    case CopyStatus.Failed:
                        failed++;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is DeviceStallException or DeviceConnectionLostException)
        {
            // The device dropped mid-run: stop cleanly, leaving the in-flight file resumable (exactly as the
            // copy verb does). Everything copied so far is journaled.
            stallMessage = ex.Message;
        }

        bool stalled = stallMessage is not null;
        int exitCode = stalled ? 3 : (failed > 0 ? 1 : 0);

        journal.RecordRun(runStartedAt, clock.GetUtcNow(), "copy", copied, skipped, failed, exitCode, connectedDevice?.Udid);

        // In production the disconnect escape-hatch hard-terminates the process right after writing summary.txt,
        // so the copy loop never reaches here. In the harness the recording terminator does not stop the loop,
        // so this write can momentarily race the escape-hatch's concurrent write to the same path — the
        // escape-hatch's summary is authoritative, so this one is best-effort.
        try
        {
            new SummaryWriter().Write(
                destinationRoot, journal.ReadManifest(), journal.ReadDevices(), journal.ReadRunsSummary(), clock.GetUtcNow());
        }
        catch (IOException)
        {
            // A sharing violation against the escape-hatch's durable write; that copy is the authoritative one.
        }

        return new HarnessResult(exitCode, copied, skipped, failed, bytesCopied, stallMessage);
    }
}
