using System.CommandLine;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Reorganize;
using GetAndSee.Core.Util;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace GetAndSee.Cli.Commands;

/// <summary>
/// The <c>reorganize</c> verb: migrate an existing archive to a different <c>--organize-by</c> layout by
/// moving files within the archive root. <b>Offline</b> — it never connects to a device — and resumable: an
/// interrupted run is finished by re-running the same command. Opens the journal writable (it updates each
/// file's <c>dest_path</c> and the recorded scheme); a <c>--dry-run</c> previews the moves and writes nothing.
/// </summary>
internal static class ReorganizeCommand
{
    /// <summary>Maximum number of old → new move samples <c>--dry-run</c> prints before summarizing the rest.</summary>
    internal const int DryRunSampleLimit = 10;

    /// <summary>Builds the <c>reorganize</c> command and its options.</summary>
    /// <returns>The configured command.</returns>
    public static Command Build()
    {
        Option<string> destinationOption = new("--dest", "-d")
        {
            Description = "Destination root folder of an existing get-and-see archive.",
            Required = true,
        };
        Option<string> organizeByOption = new("--organize-by")
        {
            Description =
                "Target folder layout to migrate to: month (flat YYYY-MM), year-month (nested " +
                "YYYY\\YYYY-MM), year, or flat.",
            Required = true,
        };
        organizeByOption.AcceptOnlyFromAmong([.. OrganizeSchemes.AllTokens]);
        Option<bool> dryRunOption = new("--dry-run")
        {
            Description = "Show the planned moves and write nothing (no move, no journal change).",
        };

        Command command = new(
            "reorganize",
            "Reorganize an existing archive into a different folder layout by moving files on the PC (offline; no device needed).");
        command.Add(destinationOption);
        command.Add(organizeByOption);
        command.Add(dryRunOption);

        command.SetAction((parseResult, cancellationToken) =>
            Task.FromResult(Run(
                parseResult.GetValue(destinationOption)!,
                OrganizeSchemes.Parse(parseResult.GetValue(organizeByOption)!),
                parseResult.GetValue(dryRunOption),
                AnsiConsole.Console,
                cancellationToken)));

        return command;
    }

    /// <summary>
    /// Plans (and, unless <paramref name="dryRun"/>, executes) the reorganize. Exposed internally so it can
    /// be exercised without the Spectre root. Returns <c>0</c> on success (including a clean no-op), <c>1</c>
    /// when one or more files could not be moved (resumable), <c>2</c> for a missing/unreadable archive, and
    /// <c>130</c> on Ctrl+C.
    /// </summary>
    /// <param name="destination">Destination root folder of an existing archive.</param>
    /// <param name="target">The target layout to migrate to.</param>
    /// <param name="dryRun">When <see langword="true"/>, preview only — nothing is moved or written.</param>
    /// <param name="console">The console to render to.</param>
    /// <param name="cancellationToken">Observed between moves so Ctrl+C stops cleanly and resumably.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string destination, OrganizeScheme target, bool dryRun, IAnsiConsole console, CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination);
        string databasePath = Path.Combine(destination, TransferJournal.DatabaseFileName);
        // Probe via the \\?\ long-path prefix so a deep archive root is detected before the open (R6 / #39).
        if (!File.Exists(LongPath.ToExtended(databasePath)))
        {
            console.MarkupLineInterpolated($"[yellow]No get-and-see archive found at[/] {destination}.");
            console.MarkupLineInterpolated($"Run [bold]get-and-see copy --dest \"{destination}\"[/] first.");
            return 2;
        }

        try
        {
            using TransferJournal journal = TransferJournal.Open(destination);
            Reorganizer reorganizer = new(journal, new DateFolderOrganizer(), destination);
            ReorganizePlan plan = reorganizer.Plan(target);

            if (dryRun)
            {
                RenderDryRun(console, plan, target);
                return 0;
            }

            ReorganizeReport report = reorganizer.Execute(plan, cancellationToken);
            RenderReport(console, report);
            return report.Failed > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            console.MarkupLine("[yellow]Interrupted.[/] Progress is saved — re-run the same command to resume.");
            return 130;
        }
        catch (SqliteException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] the archive database could not be read ({ex.Message}).");
            return 2;
        }
    }

    private static void RenderDryRun(IAnsiConsole console, ReorganizePlan plan, OrganizeScheme target)
    {
        console.Write(new Rule(
            $"[bold]Dry run — reorganize to '{OrganizeSchemes.ToToken(target)}' (nothing is written)[/]").LeftJustified());

        if (plan.Moves.Count == 0)
        {
            console.MarkupLineInterpolated(
                $"The archive is already organized as '{OrganizeSchemes.ToToken(target)}'. Nothing to move.");
            return;
        }

        int shown = Math.Min(plan.Moves.Count, DryRunSampleLimit);
        for (int i = 0; i < shown; i++)
        {
            PlannedMove move = plan.Moves[i];
            console.MarkupLineInterpolated($"  would move  {move.CurrentDestPath} [dim]->[/] {move.TargetDestPath}");
        }

        if (plan.Moves.Count > shown)
        {
            console.MarkupLineInterpolated($"  …and {plan.Moves.Count - shown:N0} more.");
        }

        console.WriteLine();
        console.MarkupLineInterpolated(
            $"DRY RUN: would move {plan.Moves.Count:N0} file(s) — {plan.AlreadyPlaced:N0} already in place, {plan.CollisionsResuffixed:N0} renamed to avoid a collision.");
        console.MarkupLine("Empty source folders are removed after the move. No files were moved and the journal was not changed.");
    }

    private static void RenderReport(IAnsiConsole console, ReorganizeReport report)
    {
        console.Write(new Rule("[bold]Reorganize summary[/]").LeftJustified());

        if (report is { Moved: 0, Failed: 0 })
        {
            console.MarkupLineInterpolated(
                $"[green]Already organized as '{OrganizeSchemes.ToToken(report.FinalScheme)}'.[/] Nothing to move.");
            return;
        }

        console.MarkupLineInterpolated($"Moved:           {report.Moved:N0}");
        console.MarkupLineInterpolated($"Already placed:  {report.AlreadyPlaced:N0}");
        console.MarkupLineInterpolated($"Re-suffixed:     {report.CollisionsResuffixed:N0}");
        console.MarkupLineInterpolated($"Folders removed: {report.DirectoriesRemoved:N0}");
        if (report.Failed > 0)
        {
            console.MarkupLineInterpolated(
                $"[red]Failed:          {report.Failed:N0}[/] — left in place and resumable; re-run the same command to retry.");
        }

        console.MarkupLineInterpolated($"Layout:          {OrganizeSchemes.ToToken(report.FinalScheme)}");
        if (report.Failed == 0)
        {
            console.MarkupLine("[green]Archive reorganized.[/]");
        }
    }
}
