using System.Globalization;
using GetAndSee.Core.Progress;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.Util;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace GetAndSee.Cli.Ui;

/// <summary>
/// Spectre.Console live dashboard (<see cref="IProgressReporter"/>): overall progress, the current
/// file, current/average speed, ETA, and counts — refreshed on a background loop at ≤4 Hz.
/// </summary>
/// <remarks>
/// The dashboard performs <b>no device I/O</b>: it only reads <see cref="TransferProgress.Snapshot"/>,
/// which is computed from counters the copy loop already maintains. Rendering runs on a background
/// task so it never blocks (or is blocked by) the transfer, and the refresh is throttled to ~4 Hz so
/// it cannot measurably slow the copy.
/// </remarks>
internal sealed class LiveDashboard : IProgressReporter
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    private const int BarWidth = 28;

    private readonly IAnsiConsole console;
    private readonly CancellationTokenSource stop = new();
    private TransferProgress? progress;
    private Task? renderLoop;

    /// <summary>Creates a dashboard writing to <paramref name="console"/> (defaults to <see cref="AnsiConsole.Console"/>).</summary>
    /// <param name="console">Target console.</param>
    public LiveDashboard(IAnsiConsole? console = null) => this.console = console ?? AnsiConsole.Console;

    /// <inheritdoc />
    public void Start(TransferProgress progress)
    {
        this.progress = progress;
        renderLoop = Task.Run(RenderLoopAsync);
    }

    /// <inheritdoc />
    public void OnFileCompleted(CopyResult result)
    {
        // The live region renders from the background loop; per-file counts come from the snapshot.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        stop.Cancel();
        try
        {
            renderLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // A failed render must never crash the run.
        }

        stop.Dispose();
    }

    private async Task RenderLoopAsync()
    {
        TransferProgress source = progress!;
        try
        {
            await console.Live(Render(source.Snapshot()))
                .StartAsync(async ctx =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(Render(source.Snapshot()));
                        ctx.Refresh();
                        try
                        {
                            await Task.Delay(RefreshInterval, stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    // Final frame so the last state is visible after the run ends.
                    ctx.UpdateTarget(Render(source.Snapshot()));
                    ctx.Refresh();
                })
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Rendering is best-effort; never let a dashboard error take down the transfer.
        }
    }

    private static IRenderable Render(ProgressSnapshot s)
    {
        return RenderSnapshot(s);
    }

    /// <summary>
    /// Builds the dashboard frame for a snapshot. Internal for unit testing — device-supplied
    /// filenames are escaped so a name containing markup characters cannot break rendering.
    /// </summary>
    /// <param name="s">The snapshot to render.</param>
    /// <returns>The renderable dashboard frame.</returns>
    internal static IRenderable RenderSnapshot(ProgressSnapshot s)
    {
        string overall =
            $"[bold]Overall[/]  {Bar(s.ByteFraction)} {s.ByteFraction * 100:0.0}%   " +
            $"{ByteSize.Humanize(s.ProcessedBytes)} / {ByteSize.Humanize(s.TotalBytes)}   " +
            $"({s.ProcessedFiles:N0}/{s.TotalFiles:N0} files)";

        double fileFraction = s.CurrentFileTotalBytes > 0
            ? Math.Clamp((double)s.CurrentFileCopiedBytes / s.CurrentFileTotalBytes, 0, 1)
            : 0;
        string currentName = string.IsNullOrEmpty(s.CurrentFileName) ? "—" : Markup.Escape(s.CurrentFileName);
        string current = $"[bold]Current[/]  {Bar(fileFraction)} {currentName}";

        string speed =
            $"[bold]Speed[/]    {Megabytes(s.CurrentBytesPerSecond)} MB/s " +
            $"[dim](avg {Megabytes(s.AverageBytesPerSecond)})[/]   ETA {FormatEta(s.Eta)}";

        string files =
            $"[bold]Files[/]    [green]{s.CopiedFiles:N0}[/] done · {s.SkippedFiles:N0} skipped · " +
            $"{FailedMarkup(s.FailedFiles)}";

        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow(overall);
        grid.AddRow(current);
        grid.AddRow(speed);
        grid.AddRow(files);

        return new Panel(grid)
            .Header("[bold] get-and-see — copying [/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static string Bar(double fraction)
    {
        int filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * BarWidth);
        return $"[grey][[[/][green]{new string('#', filled)}[/]{new string('-', BarWidth - filled)}[grey]]][/]";
    }

    private static string Megabytes(double bytesPerSecond) =>
        (bytesPerSecond / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);

    private static string FailedMarkup(int failed) =>
        failed > 0 ? $"[red]{failed:N0} failed[/]" : "0 failed";

    private static string FormatEta(TimeSpan? eta)
    {
        if (eta is not TimeSpan value)
        {
            return "—";
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        return value.TotalMinutes >= 1 ? $"{value.Minutes}m {value.Seconds}s" : $"{value.Seconds}s";
    }
}
