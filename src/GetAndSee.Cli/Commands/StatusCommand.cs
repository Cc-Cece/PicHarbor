using System.CommandLine;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Summary;
using Spectre.Console;

namespace GetAndSee.Cli.Commands;

/// <summary>
/// The <c>status</c> verb: open an existing <c>get-and-see.db</c> and print the archive totals and
/// last-run summary — with no iPhone connected. Read-only inspection; never touches a device.
/// </summary>
internal static class StatusCommand
{
    /// <summary>Builds the <c>status</c> command.</summary>
    /// <returns>The configured command.</returns>
    public static Command Build()
    {
        var destinationOption = new Option<string>("--dest", "-d")
        {
            Description = "Destination root folder of an existing get-and-see archive.",
            Required = true,
        };

        var command = new Command(
            "status",
            "Show archive totals and the last-run summary for a destination (no device needed).");
        command.Add(destinationOption);
        command.SetAction(parseResult => Run(parseResult.GetValue(destinationOption)!));

        return command;
    }

    /// <summary>
    /// Opens the archive at <paramref name="destination"/> read-only and prints its summary. Returns
    /// the process exit code: <c>0</c> on success, <c>2</c> when no archive exists at the destination.
    /// Exposed internally so it can be exercised without a device.
    /// </summary>
    /// <param name="destination">Destination root folder of an existing archive.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string destination)
    {
        destination = Path.GetFullPath(destination);
        string databasePath = Path.Combine(destination, TransferJournal.DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]No get-and-see archive found at[/] {destination}.");
            AnsiConsole.MarkupLineInterpolated(
                $"Run [bold]get-and-see copy --dest \"{destination}\"[/] first.");
            return 2;
        }

        using TransferJournal journal = TransferJournal.OpenReadOnly(destination);
        string summary = new SummaryWriter().Build(
            destination,
            journal.ReadManifest(),
            journal.ReadDevices(),
            journal.ReadRunsSummary(),
            DateTimeOffset.UtcNow);

        Console.Write(summary);
        return 0;
    }
}
