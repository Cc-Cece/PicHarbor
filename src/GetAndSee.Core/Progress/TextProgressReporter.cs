using GetAndSee.Core.Organize;
using GetAndSee.Core.Transfer;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Progress;

/// <summary>
/// The plain-text fallback reporter: one line per completed file (the Sprint 1 output). Used when the
/// output is redirected/piped/non-interactive or <c>--no-dashboard</c> is set, so logs and CI stay
/// clean and ANSI-free.
/// </summary>
public sealed class TextProgressReporter : IProgressReporter
{
    private readonly TextWriter writer;
    private TransferProgress? progress;

    /// <summary>Creates a text reporter.</summary>
    /// <param name="writer">Destination writer; defaults to <see cref="Console.Out"/>.</param>
    public TextProgressReporter(TextWriter? writer = null) => this.writer = writer ?? Console.Out;

    /// <inheritdoc />
    public void Start(TransferProgress progress) => this.progress = progress;

    /// <inheritdoc />
    public void OnFileCompleted(CopyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ProgressSnapshot snapshot = progress?.Snapshot()
            ?? throw new InvalidOperationException("Start must be called before OnFileCompleted.");

        string counter = $"{snapshot.ProcessedFiles:N0}/{snapshot.TotalFiles:N0}";
        switch (result.Status)
        {
            case CopyStatus.Copied:
                writer.WriteLine(
                    $"[done] {counter}  {ByteSize.Humanize(snapshot.ProcessedBytes)}/{ByteSize.Humanize(snapshot.TotalBytes)}  → {result.RelativeDestPath}");
                break;
            case CopyStatus.Skipped:
                writer.WriteLine(
                    $"[skip] {counter}  already copied  → {DateFolderOrganizer.ExtractFileName(result.File.Path)}");
                break;
            case CopyStatus.Failed:
                writer.WriteLine(
                    $"[fail] {DateFolderOrganizer.ExtractFileName(result.File.Path)}: {result.Error}");
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No live region to tear down.
    }
}
