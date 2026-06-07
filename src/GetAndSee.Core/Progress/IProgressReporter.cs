using GetAndSee.Core.Transfer;

namespace GetAndSee.Core.Progress;

/// <summary>
/// Renders transfer progress. Implementations decide how and when to display it (a live dashboard
/// refreshes on a timer reading <see cref="TransferProgress.Snapshot"/>; the text fallback prints one
/// line per completed file). Keeping the copy loop behind this interface makes the engine UI-agnostic.
/// </summary>
public interface IProgressReporter : IDisposable
{
    /// <summary>Called once before the first file, with the shared progress model to read from.</summary>
    /// <param name="progress">The live progress model for this run.</param>
    void Start(TransferProgress progress);

    /// <summary>Called after each file reaches a terminal state.</summary>
    /// <param name="result">The file's copy result.</param>
    void OnFileCompleted(CopyResult result);
}
