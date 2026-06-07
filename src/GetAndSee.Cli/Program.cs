using System.CommandLine;
using GetAndSee.Cli.Commands;

namespace GetAndSee.Cli;

/// <summary>
/// Entry point: wires the <c>System.CommandLine</c> root and the <c>copy</c> subcommand, and bridges
/// Ctrl+C to a cancellation token so an interrupted run leaves the journal resumable.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true; // don't hard-kill; let the pipeline unwind and save the journal.
            cancellation.Cancel();
        };

        var root = new RootCommand("get-and-see — read-only iPhone → PC media copier (USB / AFC).");
        root.Add(CopyCommand.Build());
        root.Add(StatusCommand.Build());

        return await root.Parse(args).InvokeAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
    }
}
