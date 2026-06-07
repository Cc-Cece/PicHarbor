using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using GetAndSee.Cli.Ui;
using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Preflight;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Summary;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.Util;
using Spectre.Console;

namespace GetAndSee.Cli.Commands;

/// <summary>
/// The <c>copy</c> verb: pre-flight, connect read-only, enumerate <c>/DCIM/</c>, then either plan
/// (<c>--dry-run</c>) or copy atomically into date folders and write the manifest + summary.
/// </summary>
internal static class CopyCommand
{
    /// <summary>Default per-read inactivity timeout for the stall watchdog, in seconds (#11 / R2).</summary>
    public const int DefaultReadTimeoutSeconds = 30;

    /// <summary>Builds the <c>copy</c> command and its options.</summary>
    /// <returns>The configured command.</returns>
    public static Command Build()
    {
        var destinationOption = new Option<string>("--dest", "-d")
        {
            Description = "Destination root folder for the copied, date-organized archive.",
            Required = true,
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Enumerate and plan only — opens no AFC read streams and writes no files.",
        };
        var readTimeoutOption = new Option<int>("--read-timeout")
        {
            Description =
                "Seconds with no bytes from the device before a read is treated as a stall and the run " +
                "stops cleanly (resumable). 0 disables the watchdog.",
            DefaultValueFactory = _ => DefaultReadTimeoutSeconds,
        };
        var noDashboardOption = new Option<bool>("--no-dashboard")
        {
            Description = "Disable the live dashboard and use plain per-file text output instead.",
        };
        var verifyHashOption = new Option<bool>("--verify-hash")
        {
            Description =
                "Also compute each file's SHA-256 while it copies and record it in the manifest " +
                "(slower; for the cautious). Read-only and off by default.",
        };

        var command = new Command(
            "copy",
            "Copy all iPhone /DCIM/ media to the destination, organized into YYYY/YYYY-MM folders.");
        command.Add(destinationOption);
        command.Add(dryRunOption);
        command.Add(readTimeoutOption);
        command.Add(noDashboardOption);
        command.Add(verifyHashOption);
        command.SetAction((parseResult, cancellationToken) =>
            RunAsync(
                parseResult.GetValue(destinationOption)!,
                parseResult.GetValue(dryRunOption),
                ToTimeout(parseResult.GetValue(readTimeoutOption)),
                parseResult.GetValue(noDashboardOption),
                parseResult.GetValue(verifyHashOption),
                cancellationToken));

        return command;
    }

    private static TimeSpan ToTimeout(int seconds) =>
        seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    private static async Task<int> RunAsync(string destination, bool dryRun, TimeSpan readTimeout, bool noDashboard, bool verifyHash, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(destination, dryRun, readTimeout, noDashboard, verifyHash, cancellationToken).ConfigureAwait(false);
        }
        catch (PreflightException ex)
        {
            WriteError(ex.Message);
            return 2;
        }
        catch (DeviceException ex)
        {
            WriteError(ex.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Interrupted.[/] Progress is saved — re-run the same command to resume.");
            return 130;
        }
    }

    private static async Task<int> ExecuteAsync(string destination, bool dryRun, TimeSpan readTimeout, bool noDashboard, bool verifyHash, CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination);

        var preflight = new PreflightChecks();
        await preflight.EnsureDriverServiceReachableAsync(cancellationToken).ConfigureAwait(false);

        using AfcIPhoneClient client = new(readTimeout);
        Console.WriteLine("Connecting to iPhone…");
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        DeviceInfo? device = client.Device;
        Console.WriteLine($"Connected: {DescribeDevice(device)}");

        Console.WriteLine("Scanning /DCIM/ …");
        var enumerator = new DcimEnumerator(client);
        var files = new List<RemoteFile>();
        long totalBytes = 0;
        await foreach (RemoteFile file in enumerator.EnumerateAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            files.Add(file);
            totalBytes += file.Size;
        }

        Console.WriteLine($"Found {files.Count:N0} files ({ByteSize.Humanize(totalBytes)}).");

        var organizer = new DateFolderOrganizer();
        if (dryRun)
        {
            return DryRun(destination, files, organizer, totalBytes);
        }

        preflight.EnsureDestinationWritable(destination);
        preflight.EnsureSufficientFreeSpace(destination, totalBytes);

        using TransferJournal journal = TransferJournal.Open(destination);
        DateTimeOffset runStartedAt = DateTimeOffset.UtcNow;
        if (device is not null)
        {
            journal.UpsertDevice(device.Udid, device.Name, device.ProductType, runStartedAt);
        }

        foreach (RemoteFile file in files)
        {
            journal.EnsurePending(file);
        }

        // Pre-flight advisories for a long run: PC-sleep note (R13) and on-battery warning (R14).
        AnsiConsole.MarkupLine("[dim]Tip: disable PC sleep so a long transfer isn't interrupted.[/]");
        if (preflight.GetHostPowerStatus() == HostPowerStatus.Battery)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] running on battery — connect AC power before a large transfer.");
        }

        var progress = new TransferProgress(files.Count, totalBytes);
        IProgressReporter reporter = ProgressMode.ShouldUseDashboard(noDashboard, Console.IsOutputRedirected)
            ? new LiveDashboard()
            : new TextProgressReporter();
        reporter.Start(progress);

        var copier = new FileCopier(
            client, journal, organizer, new ExifMetadataExtractor(), destination,
            readTimeout: readTimeout, onBytesStreamed: progress.RecordBytes, verifyHash: verifyHash);
        copier.CleanStaging();

        int copied = 0, skipped = 0, failed = 0;
        long bytesCopied = 0;
        string? stallMessage = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            foreach (RemoteFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.StartFile(DateFolderOrganizer.ExtractFileName(file.Path), file.Size);

                CopyResult result = await copier.CopyAsync(file, cancellationToken).ConfigureAwait(false);

                if (result.Status == CopyStatus.Skipped)
                {
                    progress.RecordSkippedBytes(file.Size);
                }

                progress.CompleteFile(result.Status);
                reporter.OnFileCompleted(result);

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
            // The device connection dropped mid-run (#11 / #25 / R2): a parked native call tripped the
            // watchdog, or a native call returned a connection-fatal AFC error. Stop the run cleanly —
            // the in-flight file is left non-done (resumable); everything copied so far is journaled.
            stallMessage = ex.Message;
        }
        finally
        {
            // Tear down the live region before printing the summary below.
            reporter.Dispose();
        }

        stopwatch.Stop();

        if (stallMessage is not null)
        {
            WriteError(stallMessage);
        }

        bool stalled = stallMessage is not null;
        int exitCode = stalled ? 3 : (failed > 0 ? 1 : 0);

        journal.RecordRun(runStartedAt, DateTimeOffset.UtcNow, "copy", copied, skipped, failed, exitCode, device?.Udid);

        var runStats = new RunStats(files.Count, copied, skipped, failed, bytesCopied, stopwatch.Elapsed);
        new SummaryWriter().Write(
            destination, journal.ReadManifest(), journal.ReadDevices(), journal.ReadRunsSummary(), DateTimeOffset.UtcNow);
        WriteRunSummary(destination, runStats, stalled);

        return exitCode;
    }

    private static int DryRun(string destination, List<RemoteFile> files, DateFolderOrganizer organizer, long totalBytes)
    {
        AnsiConsole.Write(new Rule("[bold]Dry run — planned copy (nothing is written)[/]").LeftJustified());
        foreach (RemoteFile file in files)
        {
            string relative = organizer.GetRelativeDestination(file, null);
            Console.WriteLine($"  would copy  → {Path.Combine(destination, relative)}");
        }

        Console.WriteLine();
        Console.WriteLine($"DRY RUN: would copy {files.Count:N0} files ({ByteSize.Humanize(totalBytes)}) to {destination}.");
        Console.WriteLine("No AFC read streams were opened and no files were written.");
        return 0;
    }

    private static void WriteRunSummary(string destination, RunStats stats, bool stalled)
    {
        double megabytesPerSecond = stats.Elapsed.TotalSeconds > 0
            ? stats.BytesCopied / 1024d / 1024d / stats.Elapsed.TotalSeconds
            : 0;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Run summary[/]").LeftJustified());
        Console.WriteLine($"Enumerated: {stats.Enumerated:N0}");
        Console.WriteLine($"Copied:     {stats.Copied:N0} ({ByteSize.Humanize(stats.BytesCopied)})");
        Console.WriteLine($"Skipped:    {stats.Skipped:N0} (already done)");
        Console.WriteLine($"Failed:     {stats.Failed:N0}");
        Console.WriteLine(
            $"Elapsed:    {FormatDuration(stats.Elapsed)}  ({megabytesPerSecond.ToString("0.0", CultureInfo.InvariantCulture)} MB/s avg)");
        Console.WriteLine($"Manifest:   {Path.Combine(destination, TransferJournal.DatabaseFileName)}");
        Console.WriteLine($"Summary:    {Path.Combine(destination, SummaryWriter.FileName)}");

        long remaining = stats.Enumerated - stats.Copied - stats.Skipped;
        if (stalled)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Run stopped early[/] — {remaining:N0} file(s) not yet copied. Reconnect and run the same command to resume.");
        }
        else if (stats.Failed > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{stats.Failed:N0} file(s) failed[/] — re-run the same command to retry.");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]All files copied and verified.[/]");
        }
    }

    private static string DescribeDevice(DeviceInfo? device)
    {
        if (device is null)
        {
            return "unknown device";
        }

        if (!string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(device.ProductType))
        {
            return $"{device.Name} ({device.ProductType})";
        }

        return device.Name ?? device.ProductType ?? device.Udid;
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }

        return elapsed.TotalMinutes >= 1 ? $"{elapsed.Minutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private static void WriteError(string message) =>
        AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {message}");
}
