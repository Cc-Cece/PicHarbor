using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Preflight;
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

        var command = new Command(
            "copy",
            "Copy all iPhone /DCIM/ media to the destination, organized into YYYY/YYYY-MM folders.");
        command.Add(destinationOption);
        command.Add(dryRunOption);
        command.SetAction((parseResult, cancellationToken) =>
            RunAsync(parseResult.GetValue(destinationOption)!, parseResult.GetValue(dryRunOption), cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(string destination, bool dryRun, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(destination, dryRun, cancellationToken).ConfigureAwait(false);
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

    private static async Task<int> ExecuteAsync(string destination, bool dryRun, CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination);

        var preflight = new PreflightChecks();
        await preflight.EnsureDriverServiceReachableAsync(cancellationToken).ConfigureAwait(false);

        using var client = new AfcIPhoneClient();
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
        foreach (RemoteFile file in files)
        {
            journal.EnsurePending(file);
        }

        var copier = new FileCopier(client, journal, organizer, new ExifMetadataExtractor(), destination);
        copier.CleanStaging();

        int copied = 0, skipped = 0, failed = 0, processed = 0;
        long bytesCopied = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (RemoteFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyResult result = await copier.CopyAsync(file, cancellationToken).ConfigureAwait(false);
            processed++;

            switch (result.Status)
            {
                case CopyStatus.Copied:
                    copied++;
                    bytesCopied += result.BytesCopied;
                    Console.WriteLine(
                        $"[done] {processed:N0}/{files.Count:N0}  " +
                        $"{ByteSize.Humanize(bytesCopied)}/{ByteSize.Humanize(totalBytes)}  → {result.RelativeDestPath}");
                    break;
                case CopyStatus.Skipped:
                    skipped++;
                    Console.WriteLine(
                        $"[skip] {processed:N0}/{files.Count:N0}  already copied  → {DateFolderOrganizer.ExtractFileName(file.Path)}");
                    break;
                case CopyStatus.Failed:
                    failed++;
                    WriteError($"[fail] {DateFolderOrganizer.ExtractFileName(file.Path)}: {result.Error}");
                    break;
            }
        }

        stopwatch.Stop();

        var runStats = new RunStats(files.Count, copied, skipped, failed, bytesCopied, stopwatch.Elapsed);
        new SummaryWriter().Write(destination, journal.ReadManifest(), device, runStats, DateTimeOffset.UtcNow);
        WriteRunSummary(destination, runStats);

        return failed > 0 ? 1 : 0;
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

    private static void WriteRunSummary(string destination, RunStats stats)
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

        if (stats.Failed > 0)
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
